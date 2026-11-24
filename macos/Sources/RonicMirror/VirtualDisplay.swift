import CGVirtualDisplayBridge
import CoreGraphics
import Foundation

enum DisplaySide: String, CaseIterable, Identifiable {
    case left, right
    var id: String { rawValue }
    var label: String { self == .left ? "Esquerda" : "Direita" }
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
        descriptor.name = "Ronic Mirror (iPad)"
        descriptor.maxPixelsWide = UInt32(Self.maxPixels)
        descriptor.maxPixelsHigh = UInt32(Self.maxPixels)
        // ~132 pontos por polegada (densidade típica de um iPad em pontos) -> tamanho físico aproximado.
        descriptor.sizeInMillimeters = CGSize(width: Double(width) / 132.0 * 25.4, height: Double(height) / 132.0 * 25.4)
        descriptor.vendorID = 0x5354 // "ST"
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

    /// Posiciona o monitor virtual à esquerda ou à direita de todos os outros monitores.
    func place(_ side: DisplaySide) {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)

        let others = ids.filter { $0 != displayID }
        let union = others.map { CGDisplayBounds($0) }.reduce(CGRect.null) { $0.union($1) }
        let mainBounds = CGDisplayBounds(CGMainDisplayID())
        guard !union.isNull else { return }

        let x: Int32 = side == .left ? Int32(union.minX) - Int32(width) : Int32(union.maxX)
        let y: Int32 = Int32(mainBounds.minY) // alinhado ao topo do monitor principal

        var config: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&config) == .success, let config else { return }
        CGConfigureDisplayOrigin(config, displayID, x, y)
        if CGCompleteDisplayConfiguration(config, .forSession) != .success {
            CGCancelDisplayConfiguration(config)
        }
    }

    static func sanitize(_ v: Int) -> Int {
        let clamped = min(max(v, 640), maxPixels)
        return clamped - (clamped % 2) // dimensões pares evitam problemas de codificação
    }
}
