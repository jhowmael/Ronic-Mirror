import CoreGraphics
import CoreMedia
import Foundation

/// A tela de UM aparelho: um monitor virtual próprio, sua captura e seu fluxo de vídeo.
/// Criada quando o aparelho é aceito e destruída (o monitor some) quando ele sai.
@MainActor
final class DeviceScreen: Identifiable {
    let conn: String
    let deviceKey: String // deviceId (ou conn, se o navegador não tiver) — usado para lembrar a posição
    private(set) var name: String
    let display: VirtualDisplay
    let capturer = ScreenCapturer(fps: 60)
    let router: FrameRouter
    var placement: Placement
    private(set) var status: AppModel.Status = .waiting

    private var captureTask: Task<Void, Never>?
    private var generation = 0

    nonisolated var id: String { conn }
    var displayID: CGDirectDisplayID { display.displayID }
    var bounds: CGRect { CGDisplayBounds(display.displayID) }
    var sizeLabel: String { "\(display.width)×\(display.height)" }

    init?(device: Device, source: SourceClient, placement: Placement) {
        conn = device.conn
        deviceKey = device.deviceId.isEmpty ? device.conn : device.deviceId
        name = device.name
        self.placement = placement
        let (w, h) = VirtualDisplay.fit(width: device.width > 0 ? device.width : 1366,
                                        height: device.height > 0 ? device.height : 1024)
        var hasher = Hasher()
        hasher.combine(deviceKey)
        let serial = UInt32(truncatingIfNeeded: hasher.finalize()) | 0x100
        guard let d = VirtualDisplay(width: w, height: h, name: "Ronic Mirror · \(device.name)", serial: serial) else { return nil }
        display = d
        router = FrameRouter(source: source, fps: capturer.fps, target: device.conn)
        let router = self.router
        capturer.onFrame = { pixelBuffer, pts in router.handle(pixelBuffer, pts: pts) }
    }

    /// Ajusta a resolução ao tamanho do aparelho (ex.: girou). Retorna true se mudou.
    func resize(width: Int, height: Int) -> Bool {
        let (w, h) = VirtualDisplay.fit(width: width, height: height)
        guard w != display.width || h != display.height else { return false }
        return display.setResolution(width: w, height: h)
    }

    func restartCapture(onResult: @escaping (DeviceScreen, Error?) -> Void) {
        generation += 1
        let gen = generation
        let previous = captureTask
        let (id, w, h) = (display.displayID, display.width, display.height)
        status = .waiting
        captureTask = Task {
            await previous?.value
            guard gen == self.generation else { return }
            do {
                try await capturer.start(displayID: id, width: w, height: h)
                guard gen == self.generation else { return }
                self.status = .ok
                onResult(self, nil)
            } catch {
                guard gen == self.generation else { return }
                self.status = .error(error.localizedDescription)
                onResult(self, error)
            }
        }
    }

    func stop() {
        generation += 1
        let capturer = self.capturer
        Task { await capturer.stop() }
        router.h264.invalidate()
    }

    /// Converte coordenadas normalizadas (0…1) da tela do aparelho em coordenadas globais do Mac.
    func point(x: Double, y: Double) -> CGPoint {
        let b = bounds
        return CGPoint(x: b.minX + min(max(x, 0), 1) * b.width, y: b.minY + min(max(y, 0), 1) * b.height)
    }
}
