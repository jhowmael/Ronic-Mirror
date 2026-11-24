import CoreImage
import CoreImage.CIFilterBuiltins
import SwiftUI

struct ContentView: View {
    @EnvironmentObject var model: AppModel
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            header

            if model.needsPermission { permissionBanner }

            section("1. No iPad, abra no Safari") {
                HStack(alignment: .center, spacing: 16) {
                    QRCodeView(text: model.url)
                        .frame(width: 96, height: 96)
                    VStack(alignment: .leading, spacing: 8) {
                        Text(model.url)
                            .font(.system(size: 20, weight: .semibold, design: .monospaced))
                            .textSelection(.enabled)
                            .lineLimit(1)
                            .minimumScaleFactor(0.6)
                        HStack(spacing: 10) {
                            Button {
                                model.copyURL()
                                copied = true
                                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { copied = false }
                            } label: {
                                Label(copied ? "Copiado!" : "Copiar link", systemImage: copied ? "checkmark" : "doc.on.doc")
                            }
                            .controlSize(.large)
                            .keyboardShortcut("c", modifiers: [.command, .shift])
                        }
                        Text("Ou aponte a câmera do iPad para o QR code.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                if !model.otherURLs.isEmpty {
                    Text("Outros endereços: " + model.otherURLs.joined(separator: "  ·  "))
                        .font(.caption.monospaced())
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
            }

            section("2. Posição do iPad em relação ao Mac") {
                Picker("Posição", selection: $model.position) {
                    ForEach(DisplaySide.allCases) { side in
                        Text(side.label).tag(side)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }

            section("Status") {
                VStack(alignment: .leading, spacing: 6) {
                    StatusRow(title: "Servidor local", status: model.serverStatus)
                    StatusRow(title: "Monitor virtual \(model.resolution)", status: model.displayStatus)
                    StatusRow(title: "Captura de tela", status: model.captureStatus)
                    HStack(spacing: 8) {
                        Image(systemName: "ipad.landscape")
                            .foregroundStyle(model.viewers > 0 ? .green : .secondary)
                            .frame(width: 14)
                        Text(model.viewers == 1 ? "1 iPad conectado" : "\(model.viewers) iPads conectados")
                    }
                }
                .font(.callout)
            }

            Label("Ao fechar esta janela, o Ronic Mirror continua rodando na barra de menus. Para encerrar, use “Sair” no ícone da barra de menus.",
                  systemImage: "info.circle")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(22)
        .frame(width: 460)
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 44, height: 44)
            VStack(alignment: .leading, spacing: 2) {
                Text("Ronic Mirror").font(.title2.bold())
                Text("Use o iPad como monitor estendido via Wi-Fi")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var permissionBanner: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Permissão de Gravação de Tela necessária", systemImage: "exclamationmark.triangle.fill")
                .font(.headline)
                .foregroundStyle(.orange)
            Text("Ative “Ronic Mirror” em Ajustes do Sistema › Privacidade e Segurança › Gravação de Tela e Áudio do Sistema. Depois reabra o app.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Abrir Ajustes") { model.openScreenRecordingSettings() }
                Button("Reabrir app") { model.relaunch() }
                Button("Tentar novamente") { model.restartCapture() }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)
            content()
        }
    }
}

struct StatusRow: View {
    let title: String
    let status: AppModel.Status

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Circle()
                .fill(color)
                .frame(width: 9, height: 9)
                .frame(width: 14)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                if case .error(let message) = status {
                    Text(message).font(.caption).foregroundStyle(.red)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var color: Color {
        switch status {
        case .ok: return .green
        case .waiting: return .yellow
        case .error: return .red
        }
    }
}

/// QR code gerado localmente com Core Image (sem dependências).
struct QRCodeView: View {
    let text: String

    var body: some View {
        if let image = Self.make(text) {
            Image(nsImage: image)
                .interpolation(.none)
                .resizable()
                .scaledToFit()
                .padding(6)
                .background(Color.white, in: RoundedRectangle(cornerRadius: 8))
        } else {
            RoundedRectangle(cornerRadius: 8).fill(.quaternary)
        }
    }

    static func make(_ text: String) -> NSImage? {
        guard text.hasPrefix("http") else { return nil }
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)),
              let cg = CIContext().createCGImage(output, from: output.extent) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: output.extent.width, height: output.extent.height))
    }
}
