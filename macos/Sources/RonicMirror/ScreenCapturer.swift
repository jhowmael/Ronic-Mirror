import CoreGraphics
import CoreImage
import CoreMedia
import Foundation
import ScreenCaptureKit

enum CaptureError: LocalizedError {
    case displayNotFound
    var errorDescription: String? { "O monitor virtual não apareceu no ScreenCaptureKit." }
}

/// Codifica frames BGRA em JPEG usando Core Image (acelerado pela GPU).
final class JPEGEncoder {
    private let context = CIContext(options: [.cacheIntermediates: false, .useSoftwareRenderer: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    var quality: Double

    init(quality: Double) { self.quality = quality }

    func encode(_ pixelBuffer: CVPixelBuffer) -> Data? {
        let image = CIImage(cvPixelBuffer: pixelBuffer)
        let options = [CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String): quality]
        return context.jpegRepresentation(of: image, colorSpace: colorSpace, options: options)
    }
}

/// Captura EXCLUSIVAMENTE o monitor virtual (pelo displayID) com ScreenCaptureKit.
final class ScreenCapturer: NSObject, SCStreamOutput, SCStreamDelegate {
    /// Chamado na fila de captura com cada frame novo (só quando a tela mudou).
    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?
    /// Chamado quando o stream para por erro (ex.: permissão revogada).
    var onStop: ((Error) -> Void)?

    private var stream: SCStream?
    private let queue = DispatchQueue(label: "ronicmirror.capture", qos: .userInteractive)
    let fps: Int

    init(fps: Int = 60) { self.fps = fps }

    func start(displayID: CGDirectDisplayID, width: Int, height: Int) async throws {
        await stop()

        // O monitor recém-criado pode levar alguns instantes para aparecer na lista.
        var target: SCDisplay?
        for _ in 0..<20 {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            if let d = content.displays.first(where: { $0.displayID == displayID }) {
                target = d
                break
            }
            try await Task.sleep(nanoseconds: 300_000_000)
        }
        guard let target else { throw CaptureError.displayNotFound }

        let filter = SCContentFilter(display: target, excludingWindows: [])
        let stream = SCStream(filter: filter, configuration: makeConfig(width: width, height: height), delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try await stream.startCapture()
        self.stream = stream
    }

    func stop() async {
        guard let stream else { return }
        self.stream = nil
        try? await stream.stopCapture()
    }

    private func makeConfig(width: Int, height: Int) -> SCStreamConfiguration {
        let c = SCStreamConfiguration()
        c.width = width
        c.height = height
        c.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        c.pixelFormat = kCVPixelFormatType_32BGRA
        c.colorSpaceName = CGColorSpace.sRGB
        c.showsCursor = false // o cursor vai separado (CursorStreamer), sem o atraso do vídeo
        c.queueDepth = 6
        return c
    }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete, // .idle = nada mudou na tela: não envia nada
              let pixelBuffer = sampleBuffer.imageBuffer
        else { return }
        onFrame?(pixelBuffer, sampleBuffer.presentationTimeStamp)
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        if stream === self.stream { self.stream = nil }
        onStop?(error)
    }
}
