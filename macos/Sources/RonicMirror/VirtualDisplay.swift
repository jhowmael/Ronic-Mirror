import AppKit
import CGVirtualDisplayBridge
import CoreGraphics
import Foundation

enum DisplaySide: String, CaseIterable, Identifiable {
    case left, right, top, bottom
    var id: String { rawValue }
    var label: String {
        switch self {
        case .left: return "Esquerda"
        case .right: return "Direita"
        case .top: return "Acima"
        case .bottom: return "Abaixo"
        }
    }
    var symbol: String {
        switch self {
        case .left: return "arrow.left"
        case .right: return "arrow.right"
        case .top: return "arrow.up"
        case .bottom: return "arrow.down"
        }
    }
}

/// Onde a tela do Ronic Mirror fica em relação aos outros monitores.
enum Placement: Equatable {
    case side(DisplaySide)
    case custom(CGPoint) // origem desejada em coordenadas globais (o monitor principal começa em 0,0)
}

/// Um monitor ativo, para o editor visual de posição.
struct DisplayInfo: Identifiable, Equatable {
    let id: CGDirectDisplayID
    let frame: CGRect      // coordenadas globais (y cresce para baixo, como no CoreGraphics)
    let name: String
    let isMain: Bool
    let isVirtual: Bool
}

/// Um monitor virtual REAL (tela estendida) criado via CGVirtualDisplay.
/// O monitor existe enquanto esta instância estiver viva.
final class VirtualDisplay {
    static let maxPixels = 4096

    private let display: CGVirtualDisplay
    private(set) var width: Int
    private(set) var height: Int

    var displayID: CGDirectDisplayID { display.displayID }

    init?(width: Int, height: Int) {
        let descriptor = CGVirtualDisplayDescriptor()
        descriptor.setDispatchQueue(DispatchQueue.main)
        descriptor.name = "Ronic Mirror"
        descriptor.maxPixelsWide = UInt32(Self.maxPixels)
        descriptor.maxPixelsHigh = UInt32(Self.maxPixels)
        // ~132 pontos por polegada (densidade típica de um tablet em pontos) -> tamanho físico aproximado.
        descriptor.sizeInMillimeters = CGSize(width: Double(width) / 132.0 * 25.4, height: Double(height) / 132.0 * 25.4)
        descriptor.vendorID = 0x5354
        descriptor.productID = 0x0001
        descriptor.serialNum = 0x0001

        display = CGVirtualDisplay(descriptor: descriptor)
        self.width = width
        self.height = height
        guard display.displayID != 0, setResolution(width: width, height: height) else { return nil }
    }

    /// Altera a resolução (em pixels == pontos, sem HiDPI, para manter o tráfego Wi-Fi leve).
    @discardableResult
    func setResolution(width: Int, height: Int) -> Bool {
        let w = Self.sanitize(width), h = Self.sanitize(height)
        let settings = CGVirtualDisplaySettings()
        settings.hiDPI = 0
        settings.modes = [CGVirtualDisplayMode(width: UInt(w), height: UInt(h), refreshRate: 60)]
        guard display.apply(settings) else { return false }
        self.width = w
        self.height = h
        return true
    }

    /// Posiciona o monitor virtual e devolve a origem final (já "grudada" na borda de outro monitor).
    @discardableResult
    func apply(_ placement: Placement) -> CGPoint {
        let others = Self.activeDisplays().filter { $0 != displayID }.map { CGDisplayBounds($0) }
        let size = CGSize(width: width, height: height)
        guard !others.isEmpty else { return .zero }
        let union = others.reduce(CGRect.null) { $0.union($1) }
        let main = CGDisplayBounds(CGMainDisplayID())

        let desired: CGPoint
        switch placement {
        case .side(.left): desired = CGPoint(x: union.minX - size.width, y: main.minY)
        case .side(.right): desired = CGPoint(x: union.maxX, y: main.minY)
        case .side(.top): desired = CGPoint(x: main.midX - size.width / 2, y: union.minY - size.height)
        case .side(.bottom): desired = CGPoint(x: main.midX - size.width / 2, y: union.maxY)
        case .custom(let p): desired = p
        }
        let origin = Self.snap(CGRect(origin: desired, size: size), to: others)
        move(to: origin)
        return origin
    }

    private func move(to origin: CGPoint) {
        var config: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&config) == .success, let config else { return }
        CGConfigureDisplayOrigin(config, displayID, Int32(origin.x.rounded()), Int32(origin.y.rounded()))
        if CGCompleteDisplayConfiguration(config, .forSession) != .success {
            CGCancelDisplayConfiguration(config)
        }
    }

    /// Encosta o retângulo na borda mais próxima de algum monitor, sem sobrepor nenhum
    /// (o macOS, assim como o Windows, exige que os monitores se toquem).
    static func snap(_ r: CGRect, to others: [CGRect], minOverlap: CGFloat = 80) -> CGPoint {
        func clamp(_ v: CGFloat, _ lo: CGFloat, _ hi: CGFloat) -> CGFloat { lo > hi ? (lo + hi) / 2 : min(max(v, lo), hi) }
        func overlapsAny(_ c: CGRect) -> Bool {
            others.contains { o in
                let i = o.intersection(c)
                return !i.isNull && i.width > 0.5 && i.height > 0.5
            }
        }
        var best: CGPoint?
        var bestDistance = CGFloat.greatestFiniteMagnitude
        for d in others {
            let my = min(minOverlap, r.height / 2, d.height / 2)
            let mx = min(minOverlap, r.width / 2, d.width / 2)
            let y = clamp(r.minY, d.minY - r.height + my, d.maxY - my)
            let x = clamp(r.minX, d.minX - r.width + mx, d.maxX - mx)
            let candidates = [
                CGPoint(x: d.maxX, y: y),               // à direita de d
                CGPoint(x: d.minX - r.width, y: y),     // à esquerda de d
                CGPoint(x: x, y: d.maxY),               // abaixo de d
                CGPoint(x: x, y: d.minY - r.height),    // acima de d
            ]
            for c in candidates {
                let rounded = CGPoint(x: c.x.rounded(), y: c.y.rounded())
                if overlapsAny(CGRect(origin: rounded, size: r.size)) { continue }
                let dist = hypot(rounded.x - r.minX, rounded.y - r.minY)
                if dist < bestDistance {
                    bestDistance = dist
                    best = rounded
                }
            }
        }
        return best ?? r.origin
    }

    static func activeDisplays() -> [CGDirectDisplayID] {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)
        return Array(ids.prefix(Int(count)))
    }

    /// Todos os monitores ativos, com nome, para o editor visual.
    static func layout(virtualID: CGDirectDisplayID?) -> [DisplayInfo] {
        let names: [CGDirectDisplayID: String] = Dictionary(uniqueKeysWithValues: NSScreen.screens.compactMap { screen in
            guard let n = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            return (CGDirectDisplayID(n.uint32Value), screen.localizedName)
        })
        let main = CGMainDisplayID()
        return activeDisplays().map { id in
            let isVirtual = id == virtualID
            let name = isVirtual ? "Ronic Mirror" : (names[id] ?? "Monitor")
            return DisplayInfo(id: id, frame: CGDisplayBounds(id), name: name, isMain: id == main, isVirtual: isVirtual)
        }
    }

    static func sanitize(_ v: Int) -> Int {
        let clamped = min(max(v, 640), maxPixels)
        return clamped - (clamped % 2) // dimensões pares evitam problemas de codificação
    }
}
