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
    var supportsH264: Bool { codec == "H.264" }

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

/// Orquestra: uma tela virtual POR APARELHO → captura → core-server → aparelho.
@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    enum Status: Equatable { case waiting, ok, error(String) }

    @Published var url: String = "Iniciando…"
    @Published var otherURLs: [String] = []
    @Published var serverStatus: Status = .waiting
    @Published var viewers = 0
    @Published var needsPermission = false {
        didSet { needsPermission ? startPermissionWatch() : stopPermissionWatch() }
    }
    @Published var needsAccessibility = false

    // Aparelhos
    @Published var devices: [Device] = []
    @Published var requireApproval: Bool {
        didSet { UserDefaults.standard.set(requireApproval, forKey: Keys.requireApproval); sendPolicy() }
    }
    @Published private(set) var trusted: [SavedDevice] = []
    @Published private(set) var blocked: [SavedDevice] = []

    // Telas (uma por aparelho conectado)
    @Published private(set) var screens: [DeviceScreen] = []
    @Published private(set) var displayLayout: [DisplayInfo] = []
    @Published private(set) var screensRevision = 0 // força a interface a reler status/tamanhos

    private enum Keys {
        static let placements = "placements"
        static let requireApproval = "requireApproval"
        static let trusted = "trustedDevices"
        static let blocked = "blockedDevices"
    }

    private let server = CoreServerProcess()
    private let source = SourceClient()
    private lazy var cursor = CursorStreamer(source: source)
    private let input = InputRouter()
    private var started = false
    private var permissionTimer: Timer?
    private var accessibilityTimer: Timer?
    private var resizeWork: [String: DispatchWorkItem] = [:]

    // Pedidos de autorização (um alerta por vez)
    private var promptQueue: [Device] = []
    private var prompted: Set<String> = []
    private var currentPrompt: String?

    private init() {
        requireApproval = UserDefaults.standard.object(forKey: Keys.requireApproval) as? Bool ?? true
        trusted = Self.load(Keys.trusted)
        blocked = Self.load(Keys.blocked)
    }

    // MARK: Ciclo de vida

    func start() {
        guard !started else { return }
        started = true

        source.onConnectionChange = { [weak self] connected in
            guard let self, connected else { return }
            self.sendPolicy()
            for s in self.screens { self.sendStreamInfo(s) }
            self.cursor.resendAll()
        }
        source.onMessage = { [weak self] msg in self?.handleServerMessage(msg) }
        let input = self.input
        source.onInput = { msg in input.handle(msg) }
        input.onNeedsTrust = { [weak self] in
            guard let self, !self.needsAccessibility else { return }
            self.needsAccessibility = true
            InputInjector.requestTrust()
            self.startAccessibilityWatch()
        }
        source.onOverflow = { [weak self] in
            DispatchQueue.main.async { self?.screens.forEach { $0.router.requestKeyFrame() } }
        }

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
        refreshLayout()
    }

    func shutdown() {
        screens.forEach { $0.stop() }
        screens.removeAll() // destruir os CGVirtualDisplay remove os monitores
        source.disconnect()
        server.stop()
    }

    // MARK: Telas: uma por aparelho

    /// Cria a tela de cada aparelho aceito e remove a de quem saiu.
    private func syncScreens() {
        let active = devices.filter { !$0.isPending }
        let activeConns = Set(active.map(\.conn))

        // Saíram: o monitor virtual some e as janelas voltam para as outras telas.
        let gone = screens.filter { !activeConns.contains($0.conn) }
        if !gone.isEmpty {
            gone.forEach { $0.stop() }
            screens.removeAll { !activeConns.contains($0.conn) }
            afterScreensChanged()
        }

        for device in active {
            if let screen = screens.first(where: { $0.conn == device.conn }) {
                screen.router.setCodec(device.supportsH264 ? .h264 : .jpeg)
                continue
            }
            let key = device.deviceId.isEmpty ? device.conn : device.deviceId
            guard let screen = DeviceScreen(device: device, source: source, placement: savedPlacement(for: key)) else { continue }
            screen.router.setCodec(device.supportsH264 ? .h264 : .jpeg)
            screens.append(screen)
            afterScreensChanged()
            sendStreamInfo(screen)
            // Um instante para o WindowServer registrar o monitor antes de posicionar e capturar.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self, weak screen] in
                guard let self, let screen, self.screens.contains(where: { $0 === screen }) else { return }
                self.applyPlacement(screen)
                self.startCapture(screen)
            }
        }
    }

    private func afterScreensChanged() {
        cursor.setTargets(screens.map { ($0.conn, $0.displayID) })
        input.setDisplays(Dictionary(uniqueKeysWithValues: screens.map { ($0.conn, $0.displayID) }))
        screensRevision += 1
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in self?.refreshLayout() }
    }

    private func startCapture(_ screen: DeviceScreen) {
        screen.restartCapture { [weak self] screen, error in
            guard let self else { return }
            if let error {
                let ns = error as NSError
                if ns.code == -3801 || !CGPreflightScreenCaptureAccess() { self.needsPermission = true }
            } else {
                self.needsPermission = false
                self.sendStreamInfo(screen)
                screen.router.requestKeyFrame()
            }
            self.screensRevision += 1
        }
    }

    private func restartAllCaptures() {
        screens.forEach { startCapture($0) }
    }

    /// O aparelho girou ou mudou de tamanho: ajusta a tela dele (com debounce).
    private func resizeScreen(conn: String, width: Int, height: Int) {
        resizeWork[conn]?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, let screen = self.screens.first(where: { $0.conn == conn }) else { return }
            guard screen.resize(width: width, height: height) else { return }
            self.sendStreamInfo(screen) // avisa o tamanho novo antes dos frames novos
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                self.applyPlacement(screen)
                self.startCapture(screen)
            }
        }
        resizeWork[conn] = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    private func sendStreamInfo(_ screen: DeviceScreen) {
        source.sendJSON(["type": "stream-info", "conn": screen.conn, "codec": screen.router.currentCodec.rawValue,
                         "width": screen.display.width, "height": screen.display.height, "fps": screen.capturer.fps])
    }

    // MARK: Posição das telas

    func screen(for displayID: CGDirectDisplayID) -> DeviceScreen? {
        screens.first { $0.displayID == displayID }
    }

    /// Posição rápida (Esquerda/Direita/Acima/Abaixo) de uma tela.
    func setSide(_ side: DisplaySide, for screen: DeviceScreen) {
        setPlacement(.side(side), for: screen)
    }

    /// Posição livre (arrastando no editor visual).
    func moveScreen(_ displayID: CGDirectDisplayID, to origin: CGPoint) {
        guard let screen = screen(for: displayID) else { return }
        setPlacement(.custom(origin), for: screen)
    }

    private func setPlacement(_ p: Placement, for screen: DeviceScreen) {
        screen.placement = p
        applyPlacement(screen)
    }

    private func applyPlacement(_ screen: DeviceScreen) {
        let origin = screen.display.apply(screen.placement)
        if case .custom = screen.placement { screen.placement = .custom(origin) } // já ajustada à borda
        savePlacement(screen.placement, for: screen.deviceKey)
        screensRevision += 1
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in self?.refreshLayout() }
    }

    func refreshLayout() {
        let names = Dictionary(uniqueKeysWithValues: screens.map { ($0.displayID, $0.name) })
        displayLayout = VirtualDisplay.layout(virtualNames: names)
    }

    private func savedPlacement(for key: String) -> Placement {
        let all = UserDefaults.standard.dictionary(forKey: Keys.placements) as? [String: String] ?? [:]
        guard let value = all[key] else { return .side(.right) }
        if value.hasPrefix("custom:") {
            let parts = value.dropFirst(7).split(separator: ",").compactMap { Double($0) }
            if parts.count == 2 { return .custom(CGPoint(x: parts[0], y: parts[1])) }
        }
        return .side(DisplaySide(rawValue: value) ?? .right)
    }

    private func savePlacement(_ p: Placement, for key: String) {
        var all = UserDefaults.standard.dictionary(forKey: Keys.placements) as? [String: String] ?? [:]
        switch p {
        case .side(let s): all[key] = s.rawValue
        case .custom(let pt): all[key] = "custom:\(Int(pt.x)),\(Int(pt.y))"
        }
        UserDefaults.standard.set(all, forKey: Keys.placements)
    }

    // MARK: Mensagens do servidor

    private func handleServerMessage(_ msg: [String: Any]) {
        switch msg["type"] as? String {
        case "server-info":
            if let urls = msg["urls"] as? [String] { applyURLs(urls) }
        case "viewers":
            viewers = msg["count"] as? Int ?? 0
        case "devices":
            updateDevices(msg["devices"] as? [[String: Any]] ?? [])
        case "keyframe-request":
            if let conn = msg["conn"] as? String {
                screens.first { $0.conn == conn }?.router.requestKeyFrame()
            } else {
                screens.forEach { $0.router.requestKeyFrame() }
            }
        case "viewer-resolution":
            if let conn = msg["conn"] as? String, let w = msg["width"] as? Int, let h = msg["height"] as? Int {
                resizeScreen(conn: conn, width: w, height: h)
            }
        default:
            break
        }
    }

    private func applyURLs(_ urls: [String]) {
        guard let first = urls.first else { return }
        url = first
        otherURLs = Array(urls.dropFirst())
    }

    // MARK: Toque no aparelho → mouse no Mac

    private func startAccessibilityWatch() {
        accessibilityTimer?.invalidate()
        accessibilityTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] timer in
            Task { @MainActor in
                guard let self else { return }
                if InputInjector.isTrusted {
                    self.needsAccessibility = false
                    timer.invalidate()
                }
            }
        }
    }

    func openAccessibilitySettings() {
        if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(u)
        }
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
        syncScreens()

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
            alert.informativeText = "Endereço na rede: \(next.ip)\n\nSó aceite se você reconhece este aparelho. Ele ganha uma tela própria e pode controlar o mouse por toque."
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
                self.needsPermission = false
                self.restartAllCaptures()
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
        guard path.hasSuffix(".app") else { restartAllCaptures(); return }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "sleep 1; open \"\(path)\""]
        try? task.run()
        NSApp.terminate(nil)
    }
}
