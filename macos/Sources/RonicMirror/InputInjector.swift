import ApplicationServices
import CoreGraphics
import Foundation

/// Transforma os toques do aparelho em eventos de mouse no Mac.
///   toque com 1 dedo → clique esquerdo (dois toques rápidos = clique duplo)
///   toque com 2 dedos → clique direito
///   arrastar → rolagem
///   segurar e arrastar → clicar e arrastar
/// Exige a permissão de Acessibilidade (Ajustes › Privacidade e Segurança › Acessibilidade).
final class InputInjector {
    private let source = CGEventSource(stateID: .hidSystemState)
    private var lastClick: (time: TimeInterval, point: CGPoint, count: Int)?
    private var dragging = false

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
        post(.leftMouseDown, p, button: .left)
    }

    func dragMove(to p: CGPoint) {
        guard dragging else { return }
        post(.leftMouseDragged, p, button: .left)
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

    private func post(_ type: CGEventType, _ p: CGPoint, button: CGMouseButton, clickCount: Int = 1) {
        guard let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: button) else { return }
        e.setIntegerValueField(.mouseEventClickState, value: Int64(clickCount))
        e.post(tap: .cghidEventTap)
    }
}

/// Recebe os toques direto da thread de rede e aplica na tela certa, sem passar pela main thread.
final class InputRouter {
    private let lock = NSLock()
    private var displays: [String: CGDirectDisplayID] = [:]
    private let injector = InputInjector()
    private let queue = DispatchQueue(label: "ronicmirror.input", qos: .userInteractive)
    /// Chamado (na main thread) quando falta a permissão de Acessibilidade.
    var onNeedsTrust: (() -> Void)?

    func setDisplays(_ map: [String: CGDirectDisplayID]) {
        lock.lock(); displays = map; lock.unlock()
    }

    func handle(_ msg: [String: Any]) {
        queue.async { [self] in
            guard let conn = msg["conn"] as? String else { return }
            lock.lock(); let id = displays[conn]; lock.unlock()
            guard let id else { return }
            guard InputInjector.isTrusted else {
                DispatchQueue.main.async { self.onNeedsTrust?() }
                return
            }
            let b = CGDisplayBounds(id)
            let x = min(max(msg["x"] as? Double ?? 0.5, 0), 1), y = min(max(msg["y"] as? Double ?? 0.5, 0), 1)
            let p = CGPoint(x: b.minX + x * b.width, y: b.minY + y * b.height)
            switch msg["kind"] as? String {
            case "tap": injector.click(at: p, right: false)
            case "rtap": injector.click(at: p, right: true)
            case "scroll": injector.scroll(at: p, dx: (msg["dx"] as? Double ?? 0) * b.width, dy: (msg["dy"] as? Double ?? 0) * b.height)
            case "down": injector.dragBegin(at: p)
            case "move": injector.dragMove(to: p)
            case "up": injector.dragEnd(at: p)
            default: break
            }
        }
    }
}
