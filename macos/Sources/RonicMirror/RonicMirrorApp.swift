import AppKit
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        showInDock(true)
        MainActor.assumeIsolated { AppModel.shared.start() }

        // Ao fechar a janela, o app continua rodando em segundo plano (só o ícone da barra de menus).
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { _ in
            DispatchQueue.main.async {
                let hasWindow = NSApp.windows.contains { $0.isVisible && $0.canBecomeMain }
                if !hasWindow { AppDelegate.showInDock(false) }
            }
        }
    }

    /// Fechar a janela NÃO encerra o app: o monitor virtual e o servidor continuam ativos.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated { AppModel.shared.shutdown() }
    }

    static func showInDock(_ visible: Bool) {
        NSApp.setActivationPolicy(visible ? .regular : .accessory)
        if visible { NSApp.activate(ignoringOtherApps: true) }
    }

    private func showInDock(_ visible: Bool) { Self.showInDock(visible) }
}

@main
struct RonicMirrorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel.shared

    var body: some Scene {
        Window("Ronic Mirror", id: "main") {
            ContentView()
                .environmentObject(model)
        }
        .windowResizability(.contentSize)

        MenuBarExtra {
            MenuBarContent()
                .environmentObject(model)
        } label: {
            Image(systemName: "r.square.fill")
        }
    }
}

/// Menu do ícone na barra de menus (o app continua aqui quando a janela é fechada).
struct MenuBarContent: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(model.url)
        Text(model.viewers == 1 ? "1 iPad conectado" : "\(model.viewers) iPads conectados")
        Divider()
        Button("Abrir Ronic Mirror") {
            AppDelegate.showInDock(true)
            openWindow(id: "main")
        }
        Button("Copiar link") { model.copyURL() }
        Picker("Posição do iPad", selection: $model.position) {
            ForEach(DisplaySide.allCases) { side in Text(side.label).tag(side) }
        }
        Divider()
        Button("Sair do Ronic Mirror") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
