import AppKit
import CoreGraphics
import Foundation

enum AppInfo {
    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    }
}

/// Um aparelho conectado ou aguardando autorização (vem do core-server).
struct Device: Identifiable, Equatable {
    let conn: String
    let deviceId: String
    let name: String
    let ip: String
    let state: String // "pending" | "active"
    let since: Date
    let codec: String
    let width: Int
    let height: Int
    var id: String { conn }
    var isPending: Bool { state == "pending" }

    var symbol: String {
        let n = name.lowercased()
        if n.contains("iphone") || n.contains("celular") { return "iphone" }
        if n.contains("ipad") || n.contains("tablet") { return "ipad" }
        if n.contains("mac") || n.contains("pc") || n.contains("linux") { return "laptopcomputer" }
        return "rectangle.on.rectangle"
    }
}

/// Aparelho lembrado (confiável ou bloqueado), salvo no computador.
struct SavedDevice: Codable, Identifiable, Hashable {
    var deviceId: String
    var name: String
    var ip: String
    var id: String { deviceId + "|" + ip }
}

/// Orquestra: monitor virtual → captura → core-server → aparelhos.
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
    @Published var codec = "JPEG"
    @Published var needsPermission = false {
        didSet { needsPermission ? startPermissionWatch() : stopPermissionWatch() }
    }

    // Aparelhos
    @Published var devices: [Device] = []
    @Published var requireApproval: Bool {
        didSet { UserDefaults.standard.set(requireApproval, forKey: Keys.requireApproval); sendPolicy() }
    }
    @Published private(set) var trusted: [SavedDevice] = []
    @Published private(set) var blocked: [SavedDevice] = []

    // Posição da tela
    @Published private(set) var placement: Placement
    @Published private(set) var displayLayout: [DisplayInfo] = []

    private enum Keys {
        static let placementSide = "placementSide"
        static let placementX = "placementX"
        static let placementY = "placementY"
        static let legacyPosition = "position"
        static let width = "lastWidth"
        static let height = "lastHeight"
        static let requireApproval = "requireApproval"
        static let trusted = "trustedDevices"
        static let blocked = "blockedDevices"
    }

    private var display: VirtualDisplay?
    private let server = CoreServerProcess()
    private let source = SourceClient()
    private let capturer = ScreenCapturer(fps: 60)
    private lazy var router = FrameRouter(source: source, fps: capturer.fps)
    private lazy var cursor = CursorStreamer(source: source)
    private var started = false
    private var captureGeneration = 0
    private var captureTask: Task<Void, Never>?
    private var resizeWork: DispatchWorkItem?
    private var permissionTimer: Timer?

    // Pedidos de autorização (um alerta por vez)
    private var promptQueue: [Device] = []
    private var prompted: Set<String> = []
    private var currentPrompt: String?

    private init() {
        let d = UserDefaults.standard
        requireApproval = d.object(forKey: Keys.requireApproval) as? Bool ?? true
        let side = d.string(forKey: Keys.placementSide) ?? d.string(forKey: Keys.legacyPosition) ?? "right"
        if side == "custom" {
            placement = .custom(CGPoint(x: d.double(forKey: Keys.placementX), y: d.double(forKey: Keys.placementY)))
        } else {
            placement = .side(DisplaySide(rawValue: side) ?? .right)
        }
        trusted = Self.load(Keys.trusted)
        blocked = Self.load(Keys.blocked)
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
            guard let self, connected else { return }
            self.sendPolicy()
            self.sendStreamInfo()
            self.cursor.resendAll()
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
        cursor.start()

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.refreshLayout() }
        }

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

    // MARK: Monitor virtual e posição

    private func createDisplay() {
        let w = UserDefaults.standard.integer(forKey: Keys.width)
        let h = UserDefaults.standard.integer(forKey: Keys.height)
        guard let d = VirtualDisplay(width: w > 0 ? w : 1366, height: h > 0 ? h : 1024) else {
            displayStatus = .error("Não foi possível criar o monitor virtual.")
            return
        }
        display = d
        cursor.setDisplay(d.displayID)
        displayStatus = .ok
        resolution = "\(d.width)×\(d.height)"
        // Dá um instante para o WindowServer registrar o novo monitor antes de posicioná-lo.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.applyPlacement() }
    }

    /// Posição rápida (botões Esquerda/Direita/Acima/Abaixo).
    func setSide(_ side: DisplaySide) {
        setPlacement(.side(side))
    }

    /// Posição livre (arrastando no editor visual).
    func moveVirtualDisplay(to origin: CGPoint) {
        setPlacement(.custom(origin))
    }

    private func setPlacement(_ p: Placement) {
        placement = p
        let d = UserDefaults.standard
        switch p {
        case .side(let s):
            d.set(s.rawValue, forKey: Keys.placementSide)
        case .custom(let point):
            d.set("custom", forKey: Keys.placementSide)
            d.set(Double(point.x), forKey: Keys.placementX)
            d.set(Double(point.y), forKey: Keys.placementY)
        }
        applyPlacement()
    }

    private func applyPlacement() {
        guard let display else { return }
        let origin = display.apply(placement)
        if case .custom = placement {
            // guarda a posição já ajustada à borda
            UserDefaults.standard.set(Double(origin.x), forKey: Keys.placementX)
            UserDefaults.standard.set(Double(origin.y), forKey: Keys.placementY)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in self?.refreshLayout() }
    }

    func refreshLayout() {
        displayLayout = VirtualDisplay.layout(virtualID: display?.displayID)
    }

    /// O aparelho informou sua resolução: ajusta o monitor virtual (com debounce).
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
                self.applyPlacement()
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
        case "devices":
            updateDevices(msg["devices"] as? [[String: Any]] ?? [])
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

    // MARK: Aparelhos: lista, autorização, bloqueio

    private func updateDevices(_ list: [[String: Any]]) {
        devices = list.map { d in
            Device(conn: d["conn"] as? String ?? "",
                   deviceId: d["deviceId"] as? String ?? "",
                   name: d["name"] as? String ?? "Aparelho",
                   ip: d["ip"] as? String ?? "",
                   state: d["state"] as? String ?? "",
                   since: Date(timeIntervalSince1970: (d["since"] as? Double ?? 0) / 1000),
                   codec: (d["codec"] as? String ?? "jpeg") == "h264" ? "H.264" : "JPEG",
                   width: d["width"] as? Int ?? 0,
                   height: d["height"] as? Int ?? 0)
        }
        // Fecha o alerta se o aparelho desistiu ou já foi decidido.
        if let current = currentPrompt, !devices.contains(where: { $0.conn == current && $0.isPending }) {
            NSApp.abortModal()
        }
        for d in devices where d.isPending && !prompted.contains(d.conn) {
            prompted.insert(d.conn)
            promptQueue.append(d)
        }
        if currentPrompt == nil && !promptQueue.isEmpty {
            DispatchQueue.main.async { [weak self] in self?.presentNextPrompt() }
        }
    }

    /// Alerta "Aparelho X quer usar esta tela" com Aceitar / Recusar / Bloquear.
    private func presentNextPrompt() {
        guard currentPrompt == nil else { return }
        while let next = promptQueue.first {
            promptQueue.removeFirst()
            guard devices.contains(where: { $0.conn == next.conn && $0.isPending }) else { continue }

            currentPrompt = next.conn
            let alert = NSAlert()
            alert.messageText = "\(next.name) quer usar esta tela"
            alert.informativeText = "Endereço na rede: \(next.ip)\n\nSó aceite se você reconhece este aparelho."
            alert.icon = NSApp.applicationIconImage
            alert.addButton(withTitle: "Aceitar")
            alert.addButton(withTitle: "Recusar")
            alert.addButton(withTitle: "Bloquear")
            alert.showsSuppressionButton = true
            alert.suppressionButton?.title = "Confiar neste aparelho (não perguntar de novo)"
            alert.suppressionButton?.state = .on
            NSApp.activate(ignoringOtherApps: true)
            alert.window.level = .floating
            let response = alert.runModal()
            let trust = alert.suppressionButton?.state == .on
            currentPrompt = nil

            guard let device = devices.first(where: { $0.conn == next.conn && $0.isPending }) else { continue }
            switch response {
            case .alertFirstButtonReturn: accept(device, trust: trust)
            case .alertSecondButtonReturn: reject(device)
            case .alertThirdButtonReturn: block(device)
            default: break // alerta fechado porque o aparelho saiu
            }
        }
    }

    func accept(_ device: Device, trust: Bool) {
        if trust && !device.deviceId.isEmpty && !trusted.contains(where: { $0.deviceId == device.deviceId }) {
            trusted.append(SavedDevice(deviceId: device.deviceId, name: device.name, ip: device.ip))
            save(trusted, Keys.trusted)
            sendPolicy()
        }
        deviceAction(device, "accept")
    }

    func reject(_ device: Device) { deviceAction(device, "reject") }

    func disconnect(_ device: Device) { deviceAction(device, "kick") }

    func block(_ device: Device) {
        trusted.removeAll { $0.deviceId == device.deviceId && !device.deviceId.isEmpty }
        if !blocked.contains(where: { $0.deviceId == device.deviceId && $0.ip == device.ip }) {
            blocked.append(SavedDevice(deviceId: device.deviceId, name: device.name, ip: device.ip))
        }
        save(trusted, Keys.trusted)
        save(blocked, Keys.blocked)
        deviceAction(device, "block")
        sendPolicy()
    }

    func unblock(_ saved: SavedDevice) {
        blocked.removeAll { $0 == saved }
        save(blocked, Keys.blocked)
        sendPolicy()
    }

    func forget(_ saved: SavedDevice) {
        trusted.removeAll { $0 == saved }
        save(trusted, Keys.trusted)
        sendPolicy()
    }

    func isTrusted(_ device: Device) -> Bool {
        !device.deviceId.isEmpty && trusted.contains { $0.deviceId == device.deviceId }
    }

    private func deviceAction(_ device: Device, _ action: String) {
        source.sendJSON(["type": "device-action", "conn": device.conn, "action": action])
    }

    private func sendPolicy() {
        source.sendJSON([
            "type": "policy",
            "requireApproval": requireApproval,
            "trusted": trusted.map(\.deviceId).filter { !$0.isEmpty },
            "blockedIds": blocked.map(\.deviceId).filter { !$0.isEmpty },
            "blockedIps": blocked.map(\.ip).filter { !$0.isEmpty },
        ])
    }

    private static func load(_ key: String) -> [SavedDevice] {
        guard let data = UserDefaults.standard.data(forKey: key) else { return [] }
        return (try? JSONDecoder().decode([SavedDevice].self, from: data)) ?? []
    }

    private func save(_ list: [SavedDevice], _ key: String) {
        UserDefaults.standard.set(try? JSONEncoder().encode(list), forKey: key)
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
