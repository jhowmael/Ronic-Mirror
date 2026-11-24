import AppKit
import CoreGraphics
import Foundation

enum AppInfo {
    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0-dev"
    }
}

/// Orquestra: monitor virtual  →  captura (ScreenCaptureKit)  →  core-server  →  iPad.
@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    enum Status: Equatable { case waiting, ok, error(String) }

    @Published var url: String = "Iniciando…"
    @Published var otherURLs: [String] = []
    @Published var serverStatus: Status = .waiting
    @Published var displayStatus: Status = .waiting
    @Published var captureStatus: Status = .waiting
    @Published var viewers = 0
    @Published var resolution = ""
    @Published var needsPermission = false {
        didSet { needsPermission ? startPermissionWatch() : stopPermissionWatch() }
    }
    @Published var position: DisplaySide {
        didSet {
            UserDefaults.standard.set(position.rawValue, forKey: Keys.position)
            display?.place(position)
        }
    }

    private enum Keys {
        static let position = "position"
        static let width = "lastWidth"
        static let height = "lastHeight"
    }

    private var display: VirtualDisplay?
    private let server = CoreServerProcess()
    private let source = SourceClient()
    private let capturer = ScreenCapturer(fps: 60)
    private lazy var router = FrameRouter(source: source, fps: capturer.fps)
    @Published var codec = "JPEG"
    private var started = false
    private var captureGeneration = 0
    private var captureTask: Task<Void, Never>?
    private var resizeWork: DispatchWorkItem?
    private var permissionTimer: Timer?

    private init() {
        position = DisplaySide(rawValue: UserDefaults.standard.string(forKey: Keys.position) ?? "") ?? .right
    }

    // MARK: Ciclo de vida

    func start() {
        guard !started else { return }
        started = true

        createDisplay()

        // Captura -> servidor (na fila de captura, sem passar pela main thread).
        let router = self.router
        capturer.onFrame = { pixelBuffer, pts in router.handle(pixelBuffer, pts: pts) }
        capturer.onStop = { [weak self] error in
            Task { @MainActor in self?.captureFailed(error) }
        }

        source.onConnectionChange = { [weak self] connected in
            guard let self else { return }
            if connected { self.sendStreamInfo() }
        }
        source.onMessage = { [weak self] msg in self?.handleServerMessage(msg) }

        server.onReady = { [weak self] ready in
            guard let self else { return }
            self.serverStatus = .ok
            self.applyURLs(ready.urls)
            self.source.connect(port: ready.port)
        }
        server.onFailure = { [weak self] message in
            self?.serverStatus = .error(message)
        }
        server.start()

        if !CGPreflightScreenCaptureAccess() {
            needsPermission = true
            CGRequestScreenCaptureAccess() // abre o pedido do sistema (Ajustes > Privacidade > Gravação de Tela)
        }
        restartCapture()
    }

    func shutdown() {
        source.disconnect()
        server.stop()
        display = nil // destruir o CGVirtualDisplay remove o monitor
    }

    // MARK: Monitor virtual

    private func createDisplay() {
        let w = UserDefaults.standard.integer(forKey: Keys.width)
        let h = UserDefaults.standard.integer(forKey: Keys.height)
        let width = w > 0 ? w : 1366   // padrão: iPad Pro 12.9" em pontos
        let height = h > 0 ? h : 1024
        guard let d = VirtualDisplay(width: width, height: height) else {
            displayStatus = .error("Não foi possível criar o monitor virtual.")
            return
        }
        display = d
        displayStatus = .ok
        resolution = "\(d.width)×\(d.height)"
        // Dá um instante para o WindowServer registrar o novo monitor antes de posicioná-lo.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            guard let self else { return }
            self.display?.place(self.position)
        }
    }

    /// O iPad informou sua resolução: ajusta o monitor virtual (com debounce).
    private func resizeDisplay(width: Int, height: Int) {
        guard let display else { return }
        let w = VirtualDisplay.sanitize(width), h = VirtualDisplay.sanitize(height)
        guard w != display.width || h != display.height else { return }
        resizeWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, let display = self.display else { return }
            guard display.setResolution(width: w, height: h) else { return }
            UserDefaults.standard.set(w, forKey: Keys.width)
            UserDefaults.standard.set(h, forKey: Keys.height)
            self.resolution = "\(w)×\(h)"
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                self.display?.place(self.position)
                self.restartCapture()
            }
        }
        resizeWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    // MARK: Captura

    func restartCapture() {
        guard let display else { return }
        captureGeneration += 1
        let generation = captureGeneration
        let (id, w, h) = (display.displayID, display.width, display.height)
        captureStatus = .waiting
        let previous = captureTask
        // Serializa reinícios: espera o anterior terminar e ignora pedidos que ficaram obsoletos.
        captureTask = Task {
            await previous?.value
            guard generation == self.captureGeneration else { return }
            do {
                try await capturer.start(displayID: id, width: w, height: h)
                guard generation == self.captureGeneration else { return }
                self.captureStatus = .ok
                self.needsPermission = false
                self.sendStreamInfo()
            } catch {
                guard generation == self.captureGeneration else { return }
                self.captureFailed(error)
            }
        }
    }

    private func captureFailed(_ error: Error) {
        let ns = error as NSError
        // -3801 = SCStreamErrorUserDeclined (sem permissão de Gravação de Tela)
        if ns.code == -3801 || !CGPreflightScreenCaptureAccess() {
            needsPermission = true
            captureStatus = .error("Sem permissão de Gravação de Tela.")
        } else {
            captureStatus = .error(error.localizedDescription)
        }
    }

    private func sendStreamInfo() {
        guard let display else { return }
        source.sendJSON(["type": "stream-info", "codec": router.currentCodec.rawValue, "width": display.width, "height": display.height, "fps": capturer.fps])
    }

    // MARK: Mensagens do servidor

    private func handleServerMessage(_ msg: [String: Any]) {
        switch msg["type"] as? String {
        case "server-info":
            if let urls = msg["urls"] as? [String] { applyURLs(urls) }
        case "viewers":
            viewers = msg["count"] as? Int ?? 0
            let useH264 = msg["h264"] as? Bool ?? false
            router.setCodec(useH264 ? .h264 : .jpeg)
            codec = useH264 ? "H.264" : "JPEG"
            sendStreamInfo()
        case "keyframe-request":
            router.requestKeyFrame()
        case "viewer-resolution":
            if let w = msg["width"] as? Int, let h = msg["height"] as? Int { resizeDisplay(width: w, height: h) }
        default:
            break
        }
    }

    private func applyURLs(_ urls: [String]) {
        guard let first = urls.first else { return }
        url = first
        otherURLs = Array(urls.dropFirst())
    }

    // MARK: Ações da interface

    func copyURL() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(url, forType: .string)
    }

    func openScreenRecordingSettings() {
        if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(u)
        }
    }

    // MARK: Permissão de Gravação de Tela

    /// Enquanto falta a permissão, verifica a cada 2 s (sem abrir alertas) e retoma a captura sozinho.
    private func startPermissionWatch() {
        guard permissionTimer == nil else { return }
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.needsPermission, CGPreflightScreenCaptureAccess() else { return }
                self.restartCapture()
            }
        }
    }

    private func stopPermissionWatch() {
        permissionTimer?.invalidate()
        permissionTimer = nil
    }

    /// Remove a entrada antiga do app na lista de Gravação de Tela (comum depois de atualizar o app,
    /// quando a chave aparece ligada mas pertence à versão anterior) e pede a permissão de novo.
    func resetPermission() {
        if let id = Bundle.main.bundleIdentifier {
            let task = Process()
            task.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
            task.arguments = ["reset", "ScreenCapture", id]
            try? task.run()
            task.waitUntilExit()
        }
        CGRequestScreenCaptureAccess()
        openScreenRecordingSettings()
    }

    /// Após conceder a permissão, o macOS normalmente exige reabrir o app.
    func relaunch() {
        let path = Bundle.main.bundlePath
        guard path.hasSuffix(".app") else { restartCapture(); return }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "sleep 1; open \"\(path)\""]
        try? task.run()
        NSApp.terminate(nil)
    }
}
