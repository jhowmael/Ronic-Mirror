# Contrato do core-server (v1)

Este é o contrato **único** entre as três partes do projeto. Qualquer plataforma nativa (macOS hoje, Windows na Fase 2) que siga este documento funciona com o mesmo `core-server` e o mesmo cliente web, sem alterações.

```
┌──────────────────────┐   ws://127.0.0.1:PORT/source   ┌─────────────┐   ws://IP:PORT/view   ┌──────────────┐
│ App nativo (fonte)   │ ─────── JPEG (binário) ──────▶ │ core-server │ ─── JPEG (binário) ─▶ │ iPad Safari  │
│ macOS / Windows      │ ◀────── JSON (texto) ───────── │  (Go)       │ ◀── JSON (texto) ──── │ shared-web   │
└──────────────────────┘                                └─────────────┘                       └──────────────┘
          ▲  inicia como processo filho, lê eventos JSON no stdout
          └──────────────────────────────────────────────────────────
```

## 1. Processo: como o app nativo inicia o servidor

```
core-server --port 3000 --web <pasta-do-cliente-web> --exit-on-stdin-close
```

| Flag | Significado |
|---|---|
| `--port` | Porta preferida. Se estiver ocupada, o servidor tenta as 10 seguintes. |
| `--web` | Pasta com `index.html`. Opcional: sem ela, o servidor procura `./web` ao lado do binário, `../Resources/web` (bundle .app) e `shared-web` no repositório. |
| `--exit-on-stdin-close` | O servidor encerra quando o stdin fecha. O app nativo mantém um pipe aberto no stdin, e assim o servidor nunca fica órfão se o app travar. Funciona igual no macOS e no Windows. |

**stdout** é reservado para eventos JSON, **um por linha**. Logs legíveis vão para o stderr.

```json
{"event":"ready","version":"0.1.0","port":3000,"urls":["http://192.168.1.8:3000"],"web":"/…/web"}
{"event":"error","message":"nenhuma porta livre entre 3000 e 3010: …"}
```

`urls` vem ordenada da mais provável para a menos provável (Wi-Fi/LAN privada primeiro; interfaces de VPN e virtuais são ignoradas). O app mostra `urls[0]`.

## 2. Fonte: `ws://127.0.0.1:PORT/source`

* Aceito **somente de loopback** (uma conexão pela LAN recebe 403).
* Só existe uma fonte por vez. Uma nova conexão substitui a anterior.

### Fonte → servidor

| Tipo | Conteúdo |
|---|---|
| **binário** | Um frame completo em **JPEG** (baseline, sRGB). Tamanho máximo de 64 MB. Envie no máximo 1 frame em trânsito por vez e descarte os outros (*latest-frame-wins*). |
| texto | `{"type":"hello","role":"source","platform":"macos"\|"windows","version":"0.1.0"}` (informativo) |
| texto | `{"type":"stream-info","codec":"jpeg","width":1366,"height":1024,"fps":30}`: envie ao conectar e sempre que a resolução mudar. Repassado aos iPads. |

### Servidor → fonte

| Mensagem | Quando |
|---|---|
| `{"type":"server-info","version":"…","port":3000,"urls":["http://…"]}` | Logo ao conectar |
| `{"type":"viewers","count":1}` | Ao conectar e quando um iPad entra ou sai |
| `{"type":"viewer-resolution","width":1180,"height":820,"dpr":2}` | Quando um iPad conecta ou gira. São **pontos CSS** já na orientação atual. A fonte deve ajustar o monitor virtual para `width×height`. |

## 3. Viewer (iPad): `ws://IP:PORT/view`

### Viewer → servidor
`{"type":"hello"|"resize","width":1180,"height":820,"dpr":2}`. Valores fora do intervalo 320…8192 são ignorados.

### Servidor → viewer
* **binário**: frame JPEG. Ao conectar, o viewer recebe na hora o último frame conhecido.
* `{"type":"source","connected":true|false}`
* `{"type":"stream-info",…}` (repasse da fonte)

O servidor guarda **no máximo 1 frame pendente por iPad**. Se um iPad ficar lento, ele pula frames em vez de acumular atraso.

## 4. HTTP

| Rota | Descrição |
|---|---|
| `GET /` | Arquivos estáticos do cliente web (`Cache-Control: no-cache`) |
| `GET /api/info` | `{"version","port","urls","sourceConnected","viewers"}` |

## Evolução

Mudanças compatíveis (novos campos ou tipos de mensagem) não alteram a versão. Um codec novo (por exemplo, H.264 via WebCodecs) entra como um novo valor de `stream-info.codec`. Os frames JPEG continuam como padrão.
