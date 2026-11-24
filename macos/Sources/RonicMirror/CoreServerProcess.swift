import Foundation

/// Inicia e supervisiona o binário multiplataforma /core-server como processo filho.
/// Contrato (docs/PROTOCOL.md): o servidor imprime eventos JSON, um por linha, no stdout.
final class CoreServerProcess {
    struct Ready { let port: Int; let urls: [String] }

    var onReady: ((Ready) -> Void)?      // chamado na main thread
    var onFailure: ((String) -> Void)?   // chamado na main thread

    private var process: Process?
    private let stdinPipe = Pipe()       // ao fechar (app encerrado/crash), o servidor sai sozinho
    private var buffer = Data()

    func start(preferredPort: Int = 3000) {
        guard let exe = Self.locateBinary() else {
            onFailure?("Binário core-server não encontrado. Rode `make server` na raiz do repositório.")
            return
        }
        let p = Process()
        p.executableURL = exe
        var args = ["--port", String(preferredPort), "--exit-on-stdin-close"]
        if let web = Self.locateWebDir() { args += ["--web", web.path] }
        p.arguments = args
        p.standardInput = stdinPipe
        p.standardError = FileHandle.standardError

        let out = Pipe()
        p.standardOutput = out
        out.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let chunk = handle.availableData
            guard !chunk.isEmpty else { handle.readabilityHandler = nil; return }
            self?.consume(chunk)
        }
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async {
                self?.onFailure?("O servidor local encerrou (código \(proc.terminationStatus)).")
            }
        }
        do {
            try p.run()
            process = p
        } catch {
            onFailure?("Falha ao iniciar o servidor: \(error.localizedDescription)")
        }
    }

    func stop() {
        guard let p = process else { return }
        p.terminationHandler = nil
        if p.isRunning { p.terminate() }
        process = nil
    }

    private func consume(_ chunk: Data) {
        buffer.append(chunk)
        while let nl = buffer.firstIndex(of: 0x0A) {
            let line = buffer.subdata(in: buffer.startIndex..<nl)
            buffer.removeSubrange(buffer.startIndex...nl)
            guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  let event = obj["event"] as? String else { continue }
            DispatchQueue.main.async { [weak self] in
                switch event {
                case "ready":
                    let port = obj["port"] as? Int ?? 3000
                    let urls = obj["urls"] as? [String] ?? []
                    self?.onReady?(Ready(port: port, urls: urls))
                case "error":
                    self?.onFailure?(obj["message"] as? String ?? "Erro desconhecido no servidor.")
                default: break
                }
            }
        }
    }

    // MARK: Localização dos recursos (app empacotado ou repositório em desenvolvimento)

    /// Raiz do repositório, derivada da localização deste arquivo-fonte (só útil em `swift run`).
    private static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent() // RonicMirror
            .deletingLastPathComponent() // Sources
            .deletingLastPathComponent() // macos
            .deletingLastPathComponent() // raiz
    }

    static func locateBinary() -> URL? {
        let fm = FileManager.default
        var candidates: [URL] = []
        if let env = ProcessInfo.processInfo.environment["RONIC_MIRROR_SERVER"] {
            candidates.append(URL(fileURLWithPath: env))
        }
        if let aux = Bundle.main.url(forAuxiliaryExecutable: "core-server") { candidates.append(aux) }
        candidates.append(repoRoot.appendingPathComponent("core-server/bin/core-server"))
        return candidates.first { fm.isExecutableFile(atPath: $0.path) }
    }

    static func locateWebDir() -> URL? {
        let fm = FileManager.default
        var candidates: [URL] = []
        if let res = Bundle.main.resourceURL { candidates.append(res.appendingPathComponent("web")) }
        candidates.append(repoRoot.appendingPathComponent("shared-web"))
        return candidates.first { fm.fileExists(atPath: $0.appendingPathComponent("index.html").path) }
    }
}
