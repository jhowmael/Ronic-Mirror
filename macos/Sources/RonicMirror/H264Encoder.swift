import CoreMedia
import QuartzCore
import Foundation
import VideoToolbox

/// Codificador H.264 por hardware (VideoToolbox), ajustado para baixa latência:
/// sem B-frames, tempo real, quadro-chave sob demanda.
///
/// Saída (docs/PROTOCOL.md):
///   0x10 + avcC                               configuração (enviada antes de todo quadro-chave)
///   0x11 + timestamp µs (8 bytes BE) + AVCC    quadro-chave
///   0x12 + timestamp µs (8 bytes BE) + AVCC    quadro delta
final class H264Encoder {
    /// Chamado (numa thread do VideoToolbox) com cada pacote pronto, já com o cabeçalho de tipo.
    var onPacket: ((Data) -> Void)?

    let fps: Int
    private var session: VTCompressionSession?
    private var width = 0
    private var height = 0
    private let lock = NSLock()
    private var forceKey = true

    init(fps: Int) { self.fps = fps }

    deinit { invalidate() }

    func requestKeyFrame() {
        lock.lock(); forceKey = true; lock.unlock()
    }

    func invalidate() {
        if let s = session {
            VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(s)
        }
        session = nil
    }

    /// Codifica um frame. Chamado sempre da mesma fila (fila de captura).
    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        let w = CVPixelBufferGetWidth(pixelBuffer), h = CVPixelBufferGetHeight(pixelBuffer)
        if session == nil || w != width || h != height {
            invalidate()
            guard makeSession(width: w, height: h) else { return }
        }
        guard let session else { return }

        lock.lock()
        let key = forceKey
        forceKey = false
        lock.unlock()

        let props: CFDictionary? = key ? [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue!] as CFDictionary : nil
        let status = VTCompressionSessionEncodeFrame(session, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
                                                     duration: .invalid, frameProperties: props, infoFlagsOut: nil) { [weak self] status, _, sample in
            guard status == noErr, let sample, let self else { return }
            self.emit(sample)
        }
        if status != noErr {
            // Sessão inválida (ex.: GPU reiniciou): recria no próximo frame.
            invalidate()
            requestKeyFrame()
        }
    }

    private func makeSession(width: Int, height: Int) -> Bool {
        // Modo de baixa latência do VideoToolbox (feito para videochamadas): sem "olhar à frente",
        // sem picos de tamanho e cada frame sai assim que é codificado. Se não existir, usa o normal.
        var s: VTCompressionSession?
        var lowLatency = true
        var status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                codecType: kCMVideoCodecType_H264,
                                                encoderSpecification: [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: kCFBooleanTrue] as CFDictionary,
                                                imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        if status != noErr {
            lowLatency = false
            status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                codecType: kCMVideoCodecType_H264, encoderSpecification: nil,
                                                imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        }
        guard status == noErr, let s else { return false }

        // Taxa de bits proporcional à área: ~6 Mbps para 1180×820 a 60 fps (entre 3 e 20 Mbps),
        // leve o bastante para não congestionar o Wi-Fi (congestionamento = atraso).
        let bitrate = min(max(Double(width * height * fps) * 0.1, 3_000_000), 20_000_000)
        let set = { (key: CFString, value: CFTypeRef) in VTSessionSetProperty(s, key: key, value: value) }
        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse) // sem B-frames = sem atraso extra
        if lowLatency, #available(macOS 12.0, *) {
            set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_ConstrainedHigh_AutoLevel)
        } else {
            set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel)
        }
        set(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: bitrate))
        set(kVTCompressionPropertyKey_DataRateLimits, [NSNumber(value: bitrate / 8 * 1.5), NSNumber(value: 1)] as CFArray)
        set(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: 2)) // recuperação rápida se algo se perder
        set(kVTCompressionPropertyKey_MaxFrameDelayCount, NSNumber(value: 0))
        VTCompressionSessionPrepareToEncodeFrames(s)

        session = s
        self.width = width
        self.height = height
        requestKeyFrame()
        return true
    }

    private func emit(_ sample: CMSampleBuffer) {
        guard let block = sample.dataBuffer else { return }

        var isKey = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]],
           let notSync = attachments.first?[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKey = !notSync
        }

        if isKey, let fmt = sample.formatDescription,
           let atoms = CMFormatDescriptionGetExtension(fmt, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any],
           let avcC = atoms["avcC"] as? Data {
            var config = Data([0x10])
            config.append(avcC)
            onPacket?(config)
        }

        let length = CMBlockBufferGetDataLength(block)
        var packet = Data(count: 9 + length)
        packet[0] = isKey ? 0x11 : 0x12
        let micros = UInt64(max(0, sample.presentationTimeStamp.seconds) * 1_000_000)
        withUnsafeBytes(of: micros.bigEndian) { packet.replaceSubrange(1..<9, with: $0) }
        packet.withUnsafeMutableBytes { raw in
            _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: raw.baseAddress! + 9)
        }
        onPacket?(packet)
    }
}

/// Decide, frame a frame, entre H.264 (quando o aparelho suporta) e JPEG (compatibilidade),
/// e endereça cada mensagem ao aparelho dono desta tela (prefixo 0xA0 + conn).
final class FrameRouter {
    enum Codec: String { case jpeg, h264 }

    private let lock = NSLock()
    private var codec: Codec = .jpeg
    private let jpeg = JPEGEncoder(quality: 0.5)
    private var lastJPEG: CFTimeInterval = 0
    let h264: H264Encoder
    private let source: SourceClient
    private let prefix: Data

    init(source: SourceClient, fps: Int, target: String) {
        self.source = source
        prefix = FrameRouter.prefix(for: target)
        h264 = H264Encoder(fps: fps)
        let prefix = self.prefix
        h264.onPacket = { [source] packet in source.sendOrdered(prefix + packet) }
    }

    /// 0xA0 + tamanho + conn: o servidor entrega a mensagem só ao aparelho "conn".
    static func prefix(for conn: String) -> Data {
        let bytes = Array(conn.utf8.prefix(255))
        return Data([0xA0, UInt8(bytes.count)] + bytes)
    }

    var currentCodec: Codec {
        lock.lock(); defer { lock.unlock() }
        return codec
    }

    func setCodec(_ c: Codec) {
        lock.lock()
        let changed = c != codec
        codec = c
        lock.unlock()
        if changed && c == .h264 { h264.requestKeyFrame() }
    }

    func requestKeyFrame() { h264.requestKeyFrame() }

    /// Chamado na fila de captura.
    func handle(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        switch currentCodec {
        case .h264:
            h264.encode(pixelBuffer, pts: pts)
        case .jpeg:
            // JPEG é ~10× mais pesado que H.264: no máximo 30 fps para não afogar o Wi-Fi.
            let now = CACurrentMediaTime()
            guard now - lastJPEG >= 1.0 / 31 else { return }
            lastJPEG = now
            if let data = jpeg.encode(pixelBuffer) { source.sendFrame(prefix + data) }
        }
    }
}
