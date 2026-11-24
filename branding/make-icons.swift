// Gera o logo da Ronic Mirror: um "R" com reflexo espelhado sobre fundo azul-ciano.
//
//   swift branding/make-icons.swift
//
// Saídas (em branding/out/, depois copiadas por branding/make-icons.sh):
//   logo-<N>.png  para N em 16…1024 (quadrado com cantos arredondados, fundo transparente)
import AppKit
import CoreText
import Foundation

let outDir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "branding/out")
try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

/// Caminho vetorial da letra "R" (fonte do sistema, peso Black), normalizado na origem.
func glyphPath(_ char: String, size: CGFloat) -> CGPath {
    let font = CTFontCreateWithName("SFProRounded-Black" as CFString, size, nil)
    let fallback = NSFont.systemFont(ofSize: size, weight: .black) as CTFont
    let useFont = (CTFontCopyPostScriptName(font) as String).contains("Rounded") ? font : fallback
    var unichars = Array(char.utf16)
    var glyphs = [CGGlyph](repeating: 0, count: unichars.count)
    CTFontGetGlyphsForCharacters(useFont, &unichars, &glyphs, unichars.count)
    return CTFontCreatePathForGlyph(useFont, glyphs[0], nil)!
}

func render(size px: Int) -> Data {
    let s = CGFloat(px)
    let cs = CGColorSpace(name: CGColorSpace.sRGB)!
    let ctx = CGContext(data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: 0, space: cs,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.setShouldAntialias(true)
    ctx.interpolationQuality = .high

    // Fundo: quadrado arredondado em gradiente ciano (claro em cima, profundo embaixo).
    let inset = s * 0.04
    let rect = CGRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let radius = rect.width * 0.225
    let bg = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)
    ctx.saveGState()
    ctx.addPath(bg)
    ctx.clip()
    let bgGradient = CGGradient(colorsSpace: cs, colors: [color(0x22E4F5), color(0x00B4D8), color(0x0083B0)] as CFArray,
                                locations: [0, 0.55, 1])!
    ctx.drawLinearGradient(bgGradient, start: CGPoint(x: 0, y: s), end: CGPoint(x: s, y: 0), options: [])

    // Brilho suave no topo (efeito vidro).
    let shine = CGGradient(colorsSpace: cs, colors: [color(0xFFFFFF, 0.28), color(0xFFFFFF, 0)] as CFArray, locations: [0, 1])!
    ctx.drawRadialGradient(shine, startCenter: CGPoint(x: s * 0.3, y: s * 0.95), startRadius: 0,
                           endCenter: CGPoint(x: s * 0.3, y: s * 0.95), endRadius: s * 0.75, options: [])

    // Letra R e o seu reflexo espelhado.
    let glyph = glyphPath("R", size: s * 0.52)
    let gb = glyph.boundingBoxOfPath
    let gap = s * 0.026
    let mirrorX = s * 0.5
    let baseY = (s - gb.height) / 2 - gb.minY

    // R principal: à esquerda do eixo do espelho, encostado nele.
    var tMain = CGAffineTransform(translationX: mirrorX - gap - gb.maxX, y: baseY)
    let main = glyph.copy(using: &tMain)!
    // R espelhado: refletido horizontalmente em torno do eixo.
    var tMirror = CGAffineTransform(translationX: mirrorX + gap + gb.maxX, y: baseY).scaledBy(x: -1, y: 1)
    let mirrored = glyph.copy(using: &tMirror)!

    // Sombra do R principal.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -s * 0.012), blur: s * 0.03, color: color(0x00334D, 0.35))
    ctx.addPath(main)
    ctx.setFillColor(color(0xFFFFFF))
    ctx.fillPath()
    ctx.restoreGState()

    // Reflexo: gradiente de branco translúcido que esmaece para longe do espelho.
    ctx.saveGState()
    ctx.addPath(mirrored)
    ctx.clip()
    let fade = CGGradient(colorsSpace: cs, colors: [color(0xFFFFFF, 0.78), color(0xFFFFFF, 0.18)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(fade, start: CGPoint(x: mirrorX, y: 0), end: CGPoint(x: mirrorX + gap + gb.width, y: 0), options: [])
    ctx.restoreGState()

    // Eixo do espelho: linha vertical luminosa que some nas pontas.
    let lineW = max(1, s * 0.012)
    let top = baseY + gb.maxY + s * 0.07, bottom = baseY + gb.minY - s * 0.07
    ctx.saveGState()
    ctx.clip(to: CGRect(x: mirrorX - lineW / 2, y: bottom, width: lineW, height: top - bottom))
    let lineGrad = CGGradient(colorsSpace: cs, colors: [color(0xFFFFFF, 0), color(0xFFFFFF, 0.95), color(0xFFFFFF, 0)] as CFArray,
                              locations: [0, 0.5, 1])!
    ctx.drawLinearGradient(lineGrad, start: CGPoint(x: 0, y: bottom), end: CGPoint(x: 0, y: top), options: [])
    ctx.restoreGState()

    ctx.restoreGState()

    let image = ctx.makeImage()!
    let rep = NSBitmapImageRep(cgImage: image)
    return rep.representation(using: .png, properties: [:])!
}

for size in [16, 20, 24, 32, 40, 48, 64, 128, 180, 256, 512, 1024] {
    let url = outDir.appendingPathComponent("logo-\(size).png")
    try render(size: size).write(to: url)
}
print("Ícones gerados em \(outDir.path)")
