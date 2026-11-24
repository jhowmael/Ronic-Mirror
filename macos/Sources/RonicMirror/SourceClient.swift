import Foundation

/// Cliente WebSocket que conecta o app nativo ao core-server como "fonte" (ws://127.0.0.1:<porta>/source).
/// Envia frames JPEG (binário) e mensagens JSON (texto); recebe eventos de controle (texto).
final class SourceClient: NSObject, URLSessionWebSocketDelegate {
    var onMessage: (([String: Any]) -> Void)?   // main thread
    var onConnectionChange: ((Bool) -> Void)?   // main thread

    private var session: URLSession!
    private var task: URLSessionWebSocketTask?
    private var port = 0
    private var stopped = false

    private let lock = NSLock()
    private var isOpen = false
    private var sending = false   // backpressure: no máximo 1 frame em trânsito; os demais são descartados

    override init() {
        super.init()
        session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
    }

    func connect(port: Int) {
        self.port = port
        stopped = false
        open()
    }

    func disconnect() {
        stopped = true
        task?.cancel(with: .normalClosure, reason: nil)
        task = nil
    }

    private func open() {
        guard !stopped, let url = URL(string: "ws://127.0.0.1:\(port)/source") else { return }
        let t = session.webSocketTask(with: url)
        t.maximumMessageSize = 1 << 20
        task = t
        t.resume()
        receive(on: t)
    }

    /// Envia um frame se não houver outro em trânsito (latest-frame-wins). Thread-safe.
    func sendFrame(_ data: Data) {
        lock.lock()
        guard isOpen, !sending, let t = task else { lock.unlock(); return }
        sending = true
        lock.unlock()
        t.send(.data(data)) { [weak self] _ in
            guard let self else { return }
            self.lock.lock(); self.sending = false; self.lock.unlock()
        }
    }

    func sendJSON(_ obj: [String: Any]) {
        guard let t = task,
              let data = try? JSONSerialization.data(withJSONObject: obj),
              let text = String(data: data, encoding: .utf8) else { return }
        t.send(.string(text)) { _ in }
    }

    private func receive(on t: URLSessionWebSocketTask) {
        t.receive { [weak self] result in
            guard let self, t === self.task else { return }
            switch result {
            case .success(.string(let text)):
                if let data = text.data(using: .utf8),
                   let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                    DispatchQueue.main.async { self.onMessage?(obj) }
                }
                self.receive(on: t)
            case .success:
                self.receive(on: t)
            case .failure:
                self.handleClosed(t)
            }
        }
    }

    private func handleClosed(_ t: URLSessionWebSocketTask) {
        lock.lock()
        let wasOpen = isOpen
        isOpen = false
        sending = false
        lock.unlock()
        if wasOpen { DispatchQueue.main.async { self.onConnectionChange?(false) } }
        guard !stopped else { return }
        DispatchQueue.global().asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self, t === self.task else { return }
            self.open()
        }
    }

    // MARK: URLSessionWebSocketDelegate

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        lock.lock(); isOpen = true; lock.unlock()
        sendJSON(["type": "hello", "role": "source", "platform": "macos", "version": AppInfo.version])
        DispatchQueue.main.async { self.onConnectionChange?(true) }
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        handleClosed(webSocketTask)
    }
}
