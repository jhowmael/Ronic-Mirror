import CoreImage
import CoreImage.CIFilterBuiltins
import SwiftUI

struct ContentView: View {
    @EnvironmentObject var model: AppModel
    @State private var tab = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            if model.needsPermission { permissionBanner }

            Picker("", selection: $tab) {
                Text("Conexão").tag(0)
                Text(devicesTabTitle).tag(1)
                Text("Telas").tag(2)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            Group {
                switch tab {
                case 0: ConnectionTab()
                case 1: DevicesTab()
                default: DisplaysTab()
                }
            }
            .frame(maxWidth: .infinity, alignment: .topLeading)

            Spacer(minLength: 0)

            Label("Ao fechar esta janela, o Ronic Mirror continua rodando na barra de menus. Para encerrar, use “Sair” no ícone da barra de menus.",
                  systemImage: "info.circle")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(22)
        .frame(width: 560, height: 640)
        .onAppear { model.refreshLayout() }
        .onChange(of: model.devices.filter(\.isPending).count) { pending in
            if pending > 0 { tab = 1 }
        }
    }

    private var devicesTabTitle: String {
        let pending = model.devices.filter(\.isPending).count
        let active = model.devices.count - pending
        if pending > 0 { return "Aparelhos (\(pending) aguardando)" }
        return active > 0 ? "Aparelhos (\(active))" : "Aparelhos"
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 44, height: 44)
            VStack(alignment: .leading, spacing: 2) {
                Text("Ronic Mirror").font(.title2.bold())
                Text("Use o iPad, tablet ou celular como segundo monitor via Wi-Fi")
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
            Text("1. Em Ajustes do Sistema › Privacidade e Segurança › Gravação de Tela e Áudio do Sistema, ative “RonicMirror”.\n2. Se pedir, clique em Reabrir app.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Text("Já está ativado e mesmo assim aparece este aviso? Isso acontece depois de atualizar o app: a chave nos Ajustes ainda é da versão antiga. Clique em Redefinir permissão e ative de novo.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Abrir Ajustes") { model.openScreenRecordingSettings() }
                Button("Redefinir permissão") { model.resetPermission() }
                Button("Reabrir app") { model.relaunch() }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
    }
}

// MARK: - Aba Conexão

struct ConnectionTab: View {
    @EnvironmentObject var model: AppModel
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Section2(title: "No iPad, tablet ou celular, abra no navegador") {
                HStack(alignment: .center, spacing: 16) {
                    QRCodeView(text: model.url)
                        .frame(width: 110, height: 110)
                    VStack(alignment: .leading, spacing: 8) {
                        Text(model.url)
                            .font(.system(size: 20, weight: .semibold, design: .monospaced))
                            .textSelection(.enabled)
                            .lineLimit(1)
                            .minimumScaleFactor(0.6)
                        Button {
                            model.copyURL()
                            copied = true
                            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { copied = false }
                        } label: {
                            Label(copied ? "Copiado!" : "Copiar link", systemImage: copied ? "checkmark" : "doc.on.doc")
                        }
                        .controlSize(.large)
                        Text("Ou aponte a câmera do aparelho para o QR code.")
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

            Section2(title: "Status") {
                VStack(alignment: .leading, spacing: 6) {
                    StatusRow(title: "Servidor local", status: model.serverStatus)
                    StatusRow(title: "Monitor virtual \(model.resolution)", status: model.displayStatus)
                    StatusRow(title: "Captura de tela · \(model.codec) até 60 fps · cursor em tempo real", status: model.captureStatus)
                    HStack(spacing: 8) {
                        Image(systemName: "ipad.and.iphone")
                            .foregroundStyle(model.viewers > 0 ? .green : .secondary)
                            .frame(width: 14)
                        Text(model.viewers == 1 ? "1 aparelho conectado" : "\(model.viewers) aparelhos conectados")
                    }
                }
                .font(.callout)
            }
        }
    }
}

// MARK: - Aba Aparelhos

struct DevicesTab: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Toggle(isOn: $model.requireApproval) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Pedir minha autorização para aparelhos novos")
                        Text("Ninguém vê sua tela sem você clicar em Aceitar. Aparelhos marcados como confiáveis entram direto.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.switch)

                let pending = model.devices.filter(\.isPending)
                let active = model.devices.filter { !$0.isPending }

                if !pending.isEmpty {
                    Section2(title: "Aguardando autorização") {
                        ForEach(pending) { device in
                            DeviceRow(device: device) {
                                Button("Aceitar") { model.accept(device, trust: false) }
                                    .buttonStyle(.borderedProminent)
                                Menu("Mais") {
                                    Button("Aceitar e confiar sempre") { model.accept(device, trust: true) }
                                    Button("Recusar") { model.reject(device) }
                                    Divider()
                                    Button("Bloquear", role: .destructive) { model.block(device) }
                                }
                                .fixedSize()
                            }
                        }
                    }
                }

                Section2(title: "Conectados") {
                    if active.isEmpty {
                        Text("Nenhum aparelho conectado. Abra o link da aba Conexão no aparelho.")
                            .foregroundStyle(.secondary)
                            .font(.callout)
                    }
                    ForEach(active) { device in
                        DeviceRow(device: device) {
                            Button("Desconectar") { model.disconnect(device) }
                            Button("Bloquear", role: .destructive) { model.block(device) }
                        }
                    }
                }

                if !model.trusted.isEmpty {
                    DisclosureGroup("Aparelhos confiáveis (\(model.trusted.count))") {
                        ForEach(model.trusted) { saved in
                            SavedRow(saved: saved, actionTitle: "Esquecer") { model.forget(saved) }
                        }
                    }
                }

                if !model.blocked.isEmpty {
                    DisclosureGroup("Aparelhos bloqueados (\(model.blocked.count))") {
                        ForEach(model.blocked) { saved in
                            SavedRow(saved: saved, actionTitle: "Desbloquear") { model.unblock(saved) }
                        }
                    }
                }
            }
            .padding(.trailing, 6)
        }
    }
}

struct DeviceRow<Actions: View>: View {
    @EnvironmentObject var model: AppModel
    let device: Device
    @ViewBuilder var actions: () -> Actions

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: device.symbol)
                .font(.system(size: 22))
                .foregroundStyle(device.isPending ? Color.orange : Color.accentColor)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(device.name).font(.body.weight(.semibold))
                    if model.isTrusted(device) {
                        Text("confiável")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Color.green.opacity(0.18), in: Capsule())
                    }
                }
                Text(detail).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            actions()
        }
        .padding(10)
        .background(Color.secondary.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
    }

    private var detail: String {
        if device.isPending { return "IP \(device.ip) · quer se conectar" }
        var parts = ["IP \(device.ip)", "conectado \(device.since.formatted(.relative(presentation: .named)))", device.codec]
        if device.width > 0 { parts.append("\(device.width)×\(device.height)") }
        return parts.joined(separator: " · ")
    }
}

struct SavedRow: View {
    let saved: SavedDevice
    let actionTitle: String
    let action: () -> Void

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text(saved.name)
                Text("IP \(saved.ip)").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button(actionTitle, action: action)
        }
        .padding(.vertical, 4)
    }
}

// MARK: - Aba Telas (editor visual de posição)

struct DisplaysTab: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Arraste a tela **Ronic Mirror** para onde ela fica em relação aos seus monitores. Ao soltar, ela gruda na borda mais próxima, como em Ajustes do Sistema › Monitores.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)

            DisplayArrangementView()
                .frame(height: 300)

            HStack(spacing: 8) {
                Text("Posição rápida:").foregroundStyle(.secondary)
                ForEach(DisplaySide.allCases) { side in
                    let selected = model.placement == .side(side)
                    Button {
                        model.setSide(side)
                    } label: {
                        Label(side.label, systemImage: side.symbol)
                    }
                    .buttonStyle(.bordered)
                    .tint(selected ? .accentColor : nil)
                    .background(selected ? Color.accentColor.opacity(0.15) : .clear, in: RoundedRectangle(cornerRadius: 6))
                }
            }
            if case .custom = model.placement {
                Text("Posição personalizada").font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}

struct DisplayArrangementView: View {
    @EnvironmentObject var model: AppModel
    @State private var dragOffset: CGSize = .zero

    var body: some View {
        GeometryReader { geo in
            let displays = model.displayLayout
            let union = displays.reduce(CGRect.null) { $0.union($1.frame) }
            let padding: CGFloat = 28
            let scale = union.isNull ? 0 : min((geo.size.width - padding * 2) / union.width, (geo.size.height - padding * 2) / union.height)
            let originX = (geo.size.width - union.width * scale) / 2 - union.minX * scale
            let originY = (geo.size.height - union.height * scale) / 2 - union.minY * scale

            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 12)
                    .fill(Color.secondary.opacity(0.08))
                ForEach(displays) { d in
                    let w = d.frame.width * scale, h = d.frame.height * scale
                    let x = d.frame.minX * scale + originX + (d.isVirtual ? dragOffset.width : 0)
                    let y = d.frame.minY * scale + originY + (d.isVirtual ? dragOffset.height : 0)
                    DisplayTile(info: d, dragging: d.isVirtual && dragOffset != .zero)
                        .frame(width: max(w - 2, 4), height: max(h - 2, 4))
                        .offset(x: x + 1, y: y + 1)
                        .zIndex(d.isVirtual ? 1 : 0)
                        .gesture(
                            DragGesture()
                                .onChanged { dragOffset = $0.translation }
                                .onEnded { value in
                                    guard scale > 0 else { return }
                                    let target = CGPoint(x: d.frame.minX + value.translation.width / scale,
                                                         y: d.frame.minY + value.translation.height / scale)
                                    dragOffset = .zero
                                    model.moveVirtualDisplay(to: target)
                                },
                            including: d.isVirtual ? .all : .none
                        )
                }
            }
        }
    }
}

struct DisplayTile: View {
    let info: DisplayInfo
    let dragging: Bool

    var body: some View {
        ZStack(alignment: .top) {
            RoundedRectangle(cornerRadius: 6)
                .fill(info.isVirtual
                      ? AnyShapeStyle(LinearGradient(colors: [Color(red: 0.13, green: 0.89, blue: 0.96), Color(red: 0, green: 0.52, blue: 0.69)],
                                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                      : AnyShapeStyle(Color(nsColor: .controlBackgroundColor)))
                .shadow(color: .black.opacity(dragging ? 0.35 : 0.12), radius: dragging ? 8 : 2, y: dragging ? 4 : 1)
            RoundedRectangle(cornerRadius: 6)
                .strokeBorder(info.isVirtual ? Color.white.opacity(0.7) : Color.secondary.opacity(0.5), lineWidth: 1.5)
            if info.isMain {
                Rectangle().fill(Color.secondary.opacity(0.35)).frame(height: 5) // barra de menus
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            VStack(spacing: 2) {
                if info.isVirtual { Image(systemName: "ipad.and.iphone").font(.system(size: 16, weight: .semibold)) }
                Text(info.name).font(.caption.weight(.semibold))
                Text("\(Int(info.frame.width))×\(Int(info.frame.height))\(info.isMain ? " · principal" : "")")
                    .font(.caption2)
                    .opacity(0.8)
            }
            .foregroundStyle(info.isVirtual ? Color.white : Color.primary)
            .multilineTextAlignment(.center)
            .lineLimit(1)
            .minimumScaleFactor(0.5)
            .padding(4)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .contentShape(Rectangle())
        .help(info.isVirtual ? "Arraste para reposicionar a tela do Ronic Mirror" : info.name)
    }
}

// MARK: - Componentes

struct Section2<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content

    var body: some View {
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
