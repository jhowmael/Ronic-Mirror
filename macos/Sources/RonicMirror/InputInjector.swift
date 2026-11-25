import ApplicationServices
import CoreGraphics
import Foundation

/// O que o roteador de toques precisa para mexer no mouse (permite testar sem clicar de verdade).
protocol MouseInjecting: AnyObject {
    func click(at p: CGPoint, right: Bool)
    func dragBegin(at p: CGPoint)
    func dragMove(to p: CGPoint)
    func dragEnd(at p: CGPoint)
    func scroll(at p: CGPoint, dx: CGFloat, dy: CGFloat)
}

/// Transforma os toques do aparelho em eventos de mouse no Mac.
///   toque com 1 dedo → clique esquerdo (dois toques rápidos = clique duplo)
///   toque com 2 dedos → clique direito
///   arrastar → rolagem
///   segurar e arrastar → clicar e arrastar
/// Exige a permissão de Acessibilidade (Ajustes › Privacidade e Segurança › Acessibilidade).
final class InputInjector: MouseInjecting {
    private let source = CGEventSource(stateID: .hidSystemState)
    private var lastClick: (time: TimeInterval, point: CGPoint, count: Int)?
    private var dragging = false
    private var lastDrag = CGPoint.zero

    static var isTrusted: Bool { AXIsProcessTrusted() }

    /// Abre o pedido do sistema para liberar a Acessibilidade.
    static func requestTrust() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    func click(at p: CGPoint, right: Bool) {
        moveCursor(to: p)
        if right {
            post(.rightMouseDown, p, button: .right)
            post(.rightMouseUp, p, button: .right)
            return
        }
        let now = ProcessInfo.processInfo.systemUptime
        var count = 1
        if let last = lastClick, now - last.time < 0.45, hypot(last.point.x - p.x, last.point.y - p.y) < 12 {
            count = min(last.count + 1, 3)
        }
        lastClick = (now, p, count)
        post(.leftMouseDown, p, button: .left, clickCount: count)
        post(.leftMouseUp, p, button: .left, clickCount: count)
    }

    func dragBegin(at p: CGPoint) {
        moveCursor(to: p)
        dragging = true
        lastDrag = p
        post(.leftMouseDown, p, button: .left)
    }

    func dragMove(to p: CGPoint) {
        guard dragging else { return }
        // O deslocamento (delta) é necessário para arrastar janelas e em vários apps.
        post(.leftMouseDragged, p, button: .left, delta: CGPoint(x: p.x - lastDrag.x, y: p.y - lastDrag.y))
        lastDrag = p
    }

    func dragEnd(at p: CGPoint) {
        guard dragging else { return }
        dragging = false
        post(.leftMouseUp, p, button: .left)
    }

    /// dx/dy em pontos: o conteúdo acompanha o dedo, como numa tela de toque.
    func scroll(at p: CGPoint, dx: CGFloat, dy: CGFloat) {
        if let current = CGEvent(source: nil)?.location, hypot(current.x - p.x, current.y - p.y) > 3 {
            moveCursor(to: p)
        }
        guard let e = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2,
                              wheel1: Int32(dy.rounded()), wheel2: Int32(dx.rounded()), wheel3: 0) else { return }
        e.post(tap: .cghidEventTap)
    }

    private func moveCursor(to p: CGPoint) {
        post(.mouseMoved, p, button: .left)
    }

    private func post(_ type: CGEventType, _ p: CGPoint, button: CGMouseButton, clickCount: Int = 1, delta: CGPoint = .zero) {
        guard let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: button) else { return }
        e.setIntegerValueField(.mouseEventClickState, value: Int64(clickCount))
        if delta != .zero {
            e.setIntegerValueField(.mouseEventDeltaX, value: Int64(delta.x.rounded()))
            e.setIntegerValueField(.mouseEventDeltaY, value: Int64(delta.y.rounded()))
        }
        e.post(tap: .cghidEventTap)
    }
}

/// Recebe os toques direto da thread de rede e aplica na tela certa, sem passar pela main thread.
///
/// Arrastar para outra tela: o dedo não consegue sair da tela do aparelho. Então, enquanto você
/// arrasta algo e mantém o dedo PARADO NA BORDA, o cursor continua andando sozinho naquela direção
/// (acelerando), atravessa para o monitor vizinho e para um pouco depois da borda. Ao soltar o dedo,
/// o item é solto lá. Só atravessa onde existe outro monitor.
final class InputRouter {
    private let lock = NSLock()
    private var displays: [String: CGDirectDisplayID] = [:]
    private let injector: MouseInjecting
    private let boundsOf: (CGDirectDisplayID) -> CGRect
    private let isOnScreen: (CGPoint) -> Bool
    private let isTrusted: () -> Bool
    let queue = DispatchQueue(label: "ronicmirror.input", qos: .userInteractive)
    /// Chamado (na main thread) quando falta a permissão de Acessibilidade.
    var onNeedsTrust: (() -> Void)?

    // Estado do arrasto (só acessado na `queue`).
    private var dragDisplay: CGDirectDisplayID?
    private var dragPoint = CGPoint.zero
    private var escaped = false        // o cursor já saiu da tela do aparelho
    private var pushFinished = false   // já atravessou e assentou no monitor vizinho
    private var pushDirection: CGVector?
    private var pushStarted: TimeInterval = 0
    private var pushTimer: DispatchSourceTimer?

    static let edgeMargin = 0.03      // fração da tela que conta como "borda"
    static let landingDistance: CGFloat = 260 // quanto o cursor entra no monitor vizinho

    init(injector: MouseInjecting = InputInjector(),
         boundsOf: @escaping (CGDirectDisplayID) -> CGRect = { CGDisplayBounds($0) },
         isOnScreen: @escaping (CGPoint) -> Bool = InputRouter.pointIsOnAnyDisplay,
         isTrusted: @escaping () -> Bool = { InputInjector.isTrusted }) {
        self.injector = injector
        self.boundsOf = boundsOf
        self.isOnScreen = isOnScreen
        self.isTrusted = isTrusted
    }

    static func pointIsOnAnyDisplay(_ p: CGPoint) -> Bool {
        var count: UInt32 = 0
        var id: CGDirectDisplayID = 0
        return CGGetDisplaysWithPoint(p, 1, &id, &count) == .success && count > 0
    }

    func setDisplays(_ map: [String: CGDirectDisplayID]) {
        lock.lock(); displays = map; lock.unlock()
    }

    func handle(_ msg: [String: Any]) {
        queue.async { [self] in process(msg) }
    }

    private func process(_ msg: [String: Any]) {
        guard let conn = msg["conn"] as? String else { return }
        lock.lock(); let id = displays[conn]; lock.unlock()
        guard let id else { return }
        guard isTrusted() else {
            DispatchQueue.main.async { self.onNeedsTrust?() }
            return
        }
        let b = boundsOf(id)
        let x = min(max(msg["x"] as? Double ?? 0.5, 0), 1), y = min(max(msg["y"] as? Double ?? 0.5, 0), 1)
        let p = CGPoint(x: b.minX + x * b.width, y: b.minY + y * b.height)
        switch msg["kind"] as? String {
        case "tap": injector.click(at: p, right: false)
        case "rtap": injector.click(at: p, right: true)
        case "scroll": injector.scroll(at: p, dx: (msg["dx"] as? Double ?? 0) * b.width, dy: (msg["dy"] as? Double ?? 0) * b.height)
        case "down":
            stopPush()
            dragDisplay = id
            dragPoint = p
            escaped = false
            pushFinished = false
            injector.dragBegin(at: p)
        case "move":
            guard dragDisplay != nil else { return }
            if !escaped {
                dragPoint = p
                injector.dragMove(to: p)
            }
            if let dir = Self.edgeDirection(x: x, y: y) {
                startPush(dir)
            } else {
                stopPush()
            }
        case "up":
            stopPush()
            if dragDisplay != nil { injector.dragEnd(at: dragPoint) }
            dragDisplay = nil
        default: break
        }
    }

    /// Direção "para fora" quando o dedo está na borda da tela (nil = longe da borda).
    static func edgeDirection(x: Double, y: Double) -> CGVector? {
        let m = edgeMargin
        let dx: CGFloat = x >= 1 - m ? 1 : (x <= m ? -1 : 0)
        let dy: CGFloat = y >= 1 - m ? 1 : (y <= m ? -1 : 0)
        if dx == 0 && dy == 0 { return nil }
        let len = hypot(dx, dy)
        return CGVector(dx: dx / len, dy: dy / len)
    }

    private func startPush(_ dir: CGVector) {
        guard !pushFinished else { return }
        if pushTimer != nil, pushDirection == dir { return }
        stopPush()
        pushDirection = dir
        pushStarted = ProcessInfo.processInfo.systemUptime
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .milliseconds(8))
        t.setEventHandler { [weak self] in self?.pushTick() }
        t.resume()
        pushTimer = t
    }

    private func stopPush() {
        pushTimer?.cancel()
        pushTimer = nil
        pushDirection = nil
    }

    /// Um passo do "empurrão" na borda: acelera de ~700 até ~2600 pontos por segundo.
    func pushTick(now: TimeInterval = ProcessInfo.processInfo.systemUptime, dt: CGFloat = 1.0 / 120) {
        guard let dir = pushDirection, let display = dragDisplay else { stopPush(); return }
        let speed = min(700 + CGFloat(now - pushStarted) * 2200, 2600)
        let next = CGPoint(x: dragPoint.x + dir.dx * speed * dt, y: dragPoint.y + dir.dy * speed * dt)
        guard isOnScreen(next) else { stopPush(); return } // não há monitor naquela direção
        dragPoint = next
        injector.dragMove(to: next)

        let b = boundsOf(display)
        if !b.contains(next) {
            escaped = true
            // Distância já percorrida dentro do monitor vizinho.
            let outside = max(b.minX - next.x, next.x - b.maxX, b.minY - next.y, next.y - b.maxY)
            if outside >= Self.landingDistance {
                pushFinished = true
                stopPush()
            }
        }
    }

    // Só para testes: inicia o empurrão sem timer, para avançar passo a passo.
    func testBeginPush(_ dir: CGVector, at start: TimeInterval) {
        pushDirection = dir
        pushStarted = start
    }
    var testState: (point: CGPoint, escaped: Bool, finished: Bool) { (dragPoint, escaped, pushFinished) }
    func testProcess(_ msg: [String: Any]) { process(msg) }
}
