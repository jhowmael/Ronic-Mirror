import AppKit
import CoreGraphics
import Foundation

/// Envia o cursor FORA do vídeo para o aparelho desenhar sozinho, sem o atraso de
/// captura → compressão → rede → decodificação:
///   0x20 + x (float32 LE, 0…1) + y (float32 LE, 0…1) + visível (u8)      até 120×/s, só quando muda
///   0x21 + largura, altura, hotspotX, hotspotY (float32 LE, em pontos) + PNG (2×)   quando o formato muda
final class CursorStreamer {
    private let source: SourceClient
    private let queue = DispatchQueue(label: "ronicmirror.cursor", qos: .userInteractive)
    private var timer: DispatchSourceTimer?
    private var shapeTimer: Timer?

    private let lock = NSLock()
    /// Uma entrada por aparelho: a tela dele e o prefixo de endereçamento.
    private var targets: [(conn: String, displayID: CGDirectDisplayID, prefix: Data)] = []
    private var last: [String: (x: Float, y: Float, visible: Bool)] = [:]
    private var lastShapeKey: Int?

    init(source: SourceClient) { self.source = source }

    func setTargets(_ list: [(conn: String, displayID: CGDirectDisplayID)]) {
        lock.lock()
        targets = list.map { ($0.conn, $0.displayID, FrameRouter.prefix(for: $0.conn)) }
        last = last.filter { entry in list.contains { $0.conn == entry.key } }
        lock.unlock()
    }

    func start() {
        guard timer == nil else { return }
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .milliseconds(8), leeway: .milliseconds(1)) // ~120 Hz
        t.setEventHandler { [weak self] in self?.tickPosition() }
        t.resume()
        timer = t

        shapeTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 20, repeats: true) { [weak self] _ in
            self?.tickShape()
        }
    }

    /// Reenvia posição e formato (ex.: depois de reconectar ao servidor).
    func resendAll() {
        lock.lock(); last = [:]; lastShapeKey = nil; lock.unlock()
        DispatchQueue.main.async { self.tickShape() }
    }

    private func tickPosition() {
        guard let location = CGEvent(source: nil)?.location else { return }
        lock.lock(); let list = targets; lock.unlock()

        for t in list {
            let b = CGDisplayBounds(t.displayID)
            guard b.width > 0, b.height > 0 else { continue }
            let visible = b.contains(location)
            let x = Float((location.x - b.minX) / b.width)
            let y = Float((location.y - b.minY) / b.height)

            lock.lock()
            let prev = last[t.conn]
            let changed = prev == nil || visible != prev!.visible ||
                (visible && (abs(x - prev!.x) > 0.00002 || abs(y - prev!.y) > 0.00002))
            if changed { last[t.conn] = (x, y, visible) }
            lock.unlock()
            guard changed else { continue }

            var data = Data(count: 10)
            data[0] = 0x20
            data.withUnsafeMutableBytes { raw in
                raw.storeBytes(of: x.bitPattern.littleEndian, toByteOffset: 1, as: UInt32.self)
                raw.storeBytes(of: y.bitPattern.littleEndian, toByteOffset: 5, as: UInt32.self)
            }
            data[9] = visible ? 1 : 0
            source.sendImmediate(t.prefix + data) // só o aparelho desta tela recebe
        }
    }

    private func tickShape() {
        guard let cursor = NSCursor.currentSystem, let tiff = cursor.image.tiffRepresentation else { return }
        var hasher = Hasher()
        hasher.combine(tiff)
        hasher.combine(cursor.hotSpot.x)
        hasher.combine(cursor.hotSpot.y)
        let key = hasher.finalize()
        lock.lock()
        let same = key == lastShapeKey
        if !same { lastShapeKey = key }
        lock.unlock()
        guard !same, let png = Self.png(of: cursor.image, scale: 2) else { return }

        let size = cursor.image.size
        var data = Data(count: 17)
        data[0] = 0x21
        data.withUnsafeMutableBytes { raw in
            for (i, v) in [Float(size.width), Float(size.height), Float(cursor.hotSpot.x), Float(cursor.hotSpot.y)].enumerated() {
                raw.storeBytes(of: v.bitPattern.littleEndian, toByteOffset: 1 + i * 4, as: UInt32.self)
            }
        }
        data.append(png)
        source.sendImmediate(data)
    }

    /// Renderiza a imagem do cursor em 2× (nítida em telas Retina) e devolve PNG.
    private static func png(of image: NSImage, scale: CGFloat) -> Data? {
        let size = image.size
        guard size.width > 0, size.height > 0,
              let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image.draw(in: CGRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
