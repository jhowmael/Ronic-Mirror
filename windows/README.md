# Ronic Mirror: Windows

App nativo para Windows 10/11 (x64) em **C# / .NET 10 / WinForms**. Ele não reimplementa a rede nem a página web do aparelho. Reaproveita:

* **`/core-server`**, compilado como `core-server.exe` (`GOOS=windows`)
* **`/shared-web`**, copiado para `web\` ao lado do `.exe`

Ele segue o mesmo contrato do app do macOS: [`docs/PROTOCOL.md`](../docs/PROTOCOL.md).

## Arquivos

| Arquivo | Responsabilidade | Equivalente no macOS |
|---|---|---|
| `Program.cs` | Entrada, instância única, DPI PerMonitorV2 | `RonicMirrorApp.swift` |
| `MainForm.cs` | Janela com abas Conexão / Aparelhos / Telas e **bandeja do sistema** (segundo plano) | `ContentView.swift` + `MenuBarContent` |
| `ApprovalForm.cs` | "Aparelho X quer usar esta tela": Aceitar / Recusar / Bloquear + "Confiar neste aparelho" | `NSAlert` em `AppModel.swift` |
| `ArrangementControl.cs` | Editor visual: desenha os monitores e permite arrastar a tela do Ronic Mirror | `DisplayArrangementView` em `ContentView.swift` |
| `CursorStreamer.cs` | Cursor fora do vídeo: posição até 120×/s + formato em PNG | `CursorStreamer.swift` |
| `AppController.cs` | Orquestração: monitor → captura → servidor | `AppModel.swift` |
| `ParsecVdd.cs` | Porte do [parsec-vdd.h](https://github.com/nomi-san/parsec-vdd): SetupAPI + `DeviceIoControl`, com keep-alive a cada 50 ms | `CGVirtualDisplay` (bridge) |
| `VirtualMonitor.cs` | Cria o monitor, escolhe a resolução mais próxima da do aparelho e posiciona com `ChangeDisplaySettingsEx` (`DM_POSITION`): Esquerda/Direita/Acima/Abaixo ou posição livre, sempre "grudando" na borda mais próxima | `VirtualDisplay.swift` |
| `ScreenCapturer.cs` | Captura **só a área do monitor virtual** (GDI) a até 60 fps, desenha o cursor e detecta mudança. A codificação JPEG roda numa segunda thread, em paralelo com a captura | `ScreenCapturer.swift` + `H264Encoder.swift` |
| `ServerProcess.cs` | Inicia `core-server.exe --exit-on-stdin-close` e lê os eventos JSON | `CoreServerProcess.swift` |
| `SourceClient.cs` | `ClientWebSocket` → `ws://127.0.0.1:PORT/source` | `SourceClient.swift` |

## Monitor virtual: Parsec VDD

O Windows não permite criar um monitor sem um driver de vídeo (IddCx). Este projeto usa o **Parsec Virtual Display Driver**, que é gratuito, assinado digitalmente e instalado por milhões de usuários do Parsec.

* Na primeira execução, o app mostra **Instalar driver**. O botão baixa `parsec-vdd-0.41.0.0.exe` do site oficial (`builds.parsec.app`) e o executa com `/S` (instalação silenciosa), pedindo elevação via UAC.
* Depois disso, o app roda **sem administrador**: ele abre o dispositivo e adiciona ou remove monitores por IOCTL.
* O driver remove o monitor se não receber um "ping" a cada ~100 ms. Por isso, se o app fechar ou travar, o monitor some sozinho.
* **Resoluções:** o driver oferece modos fixos (1280×800, 1600×1200, 1920×1200 e outros). O app escolhe o mais próximo, em proporção e área, da resolução que o aparelho (iPad, tablet ou celular) informa. Aparelho em pé (retrato) usa rotação de 90°.

## Compilar

Funciona no Windows, no macOS ou no Linux, graças a `EnableWindowsTargeting`:

```bash
# na raiz do repositório
./scripts/package-windows.sh          # → dist/RonicMirror-Windows.zip
```

Conteúdo do zip:

```
RonicMirror-Windows/
├── RonicMirror.exe    (self-contained: não precisa instalar o .NET)
├── core-server.exe
├── web\               (cópia de /shared-web)
└── LEIA-ME.txt
```

Para desenvolver no Windows:

```powershell
cd core-server; go build -o bin\core-server.exe .; cd ..
cd windows\RonicMirror; dotnet run
```

## Próximos passos

* Captura via **Windows.Graphics.Capture / DXGI** e vídeo **H.264** via Media Foundation (o servidor e a página já suportam: ver `docs/PROTOCOL.md`).
* Resoluções personalizadas do driver (`HKLM\SOFTWARE\Parsec\vdd`) com o tamanho exato do aparelho.
* Assinatura Authenticode do `.exe` (evita o aviso do SmartScreen).
