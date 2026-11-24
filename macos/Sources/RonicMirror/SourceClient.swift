import Foundation

/// Cliente WebSocket que conecta o app nativo ao core-server como "fonte" (ws://127.0.0.1:<porta>/source).
/// Envia frames JPEG (binário) e mensagens JSON (texto); recebe eventos de controle (texto).
final class SourceClient: NSObject, URLSessionWebSocketDelegate {
    var onMessage: (([String: Any]) -> Void)?   // main thread
    var onConnectionChange: ((Bool) -> Void)?   // main thread
    /// A fila de vídeo transbordou (frames descartados): é preciso um novo quadro-chave. Qualquer thread.
    var onOverflow: (() -> Void)?
    /// Toques do aparelho. Chamado na thread de rede (NÃO na main thread), para responder na hora.
    var onInput: (([String: Any]) -> Void)?

    private var session: URLSession!
    private var task: URLSessionWebSocketTask?
    private var port = 0
    private var stopped = false

    private let lock = NSLock()
    private var isOpen = false
    private var sending = false   // há uma mensagem binária em trânsito
    private var queue: [Data] = [] // vídeo H.264: não pode perder frames, então enfileira em ordem
    private static let maxQueue = 30 // ~0,5 s: se passar disso, descarta e recomeça num quadro-chave

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

    /// Vídeo H.264: envia em ordem. Se a fila crescer demais, descarta tudo e pede um quadro-chave. Thread-safe.
    func sendOrdered(_ data: Data) {
        lock.lock()
        guard isOpen, let t = task else { lock.unlock(); return }
        if queue.count >= Self.maxQueue {
            queue.removeAll()
            lock.unlock()
            onOverflow?()
            return
        }
        if sending {
            queue.append(data)
            lock.unlock()
            return
        }
        sending = true
        lock.unlock()
        transmit(data, on: t)
    }

    private func transmit(_ data: Data, on t: URLSessionWebSocketTask) {
        t.send(.data(data)) { [weak self] error in
            guard let self else { return }
            self.lock.lock()
            if error == nil, !self.queue.isEmpty, t === self.task {
                let next = self.queue.removeFirst()
                self.lock.unlock()
                self.transmit(next, on: t)
            } else {
                self.sending = false
                self.queue.removeAll()
                self.lock.unlock()
            }
        }
    }

    /// Mensagens pequenas e urgentes (cursor): vão direto, sem esperar a fila de vídeo. Thread-safe.
    func sendImmediate(_ data: Data) {
        lock.lock()
        let t = isOpen ? task : nil
        lock.unlock()
        t?.send(.data(data)) { _ in }
    }

    /// JPEG: envia só se não houver outro em trânsito (latest-frame-wins). Thread-safe.
    func sendFrame(_ data: Data) {
        lock.lock()
        guard isOpen, !sending, let t = task else { lock.unlock(); return }
        sending = true
        lock.unlock()
        transmit(data, on: t) // ao terminar, continua drenando a fila de vídeo (se houver), na ordem
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
                    if obj["type"] as? String == "input", let onInput = self.onInput {
                        onInput(obj) // toques: direto, sem esperar a main thread (que desenha a janela)
                    } else {
                        DispatchQueue.main.async { self.onMessage?(obj) }
                    }
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
        queue.removeAll()
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
