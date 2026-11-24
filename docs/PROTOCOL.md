# Contrato do core-server (v1)

Este é o contrato **único** entre as três partes do projeto. Qualquer plataforma nativa (macOS hoje, Windows na Fase 2) que siga este documento funciona com o mesmo `core-server` e o mesmo cliente web, sem alterações.

```
┌──────────────────────┐   ws://127.0.0.1:PORT/source   ┌─────────────┐   ws://IP:PORT/view   ┌──────────────┐
│ App nativo (fonte)   │ ─────── JPEG (binário) ──────▶ │ core-server │ ─── JPEG (binário) ─▶ │ navegador    │
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

**Mensagens binárias**: o primeiro byte indica o tipo.

| 1º byte | Conteúdo | Regras |
|---|---|---|
| `0xFF` | Um **JPEG** completo (o próprio `FF D8` do JPEG) | Envie no máximo 1 em trânsito e descarte os outros (*latest-frame-wins*). |
| `0x10` | `0x10` + **avcC** (AVCDecoderConfigurationRecord do H.264) | Envie **antes de todo quadro-chave**. |
| `0x11` | `0x11` + timestamp em µs (8 bytes, big-endian) + NALs em formato **AVCC** (prefixo de 4 bytes) | Quadro-chave H.264 |
| `0x12` | Igual ao `0x11` | Quadro delta H.264. **Nunca descarte deltas.** Se precisar descartar, descarte tudo até o próximo quadro-chave. |

Tamanho máximo de 64 MB por mensagem. O H.264 deve ser gerado **sem B-frames**, em tempo real (baixa latência).

**Mensagens de texto**

| Mensagem | |
|---|---|
| `{"type":"hello","role":"source","platform":"macos"\|"windows","version":"…"}` | Informativo |
| `{"type":"stream-info","codec":"h264"\|"jpeg","width":1366,"height":1024,"fps":60}` | Envie ao conectar e quando a resolução ou o codec mudar. Repassado aos viewers. |

### Servidor → fonte

| Mensagem | Quando |
|---|---|
| `{"type":"server-info","version":"…","port":3000,"urls":["http://…"]}` | Logo ao conectar |
| `{"type":"viewers","count":1,"h264":true}` | Ao conectar e quando um viewer entra, sai ou muda de codec. `h264` é `true` quando **todos** os viewers decodificam H.264. Nesse caso a fonte deve usar H.264; caso contrário, JPEG. |
| `{"type":"keyframe-request"}` | Um viewer novo entrou ou ficou para trás. Gere um quadro-chave (H.264) ou reenvie a tela atual (JPEG). |
| `{"type":"viewer-resolution","width":1180,"height":820,"dpr":2}` | Quando um viewer conecta ou gira. São **pontos CSS** já na orientação atual. A fonte deve ajustar o monitor virtual para `width×height`. |

Uma fonte que só sabe JPEG (como a do Windows hoje) funciona normalmente: ela ignora `h264` e manda `0xFF`.

## 3. Viewer (iPad, tablet, celular ou qualquer navegador): `ws://IP:PORT/view`

### Viewer → servidor

| Mensagem | |
|---|---|
| `{"type":"hello","width":1180,"height":820,"dpr":2,"codecs":["h264","jpeg"]}` | Ao conectar. Inclua `"h264"` só se `VideoDecoder.isConfigSupported` confirmar o suporte. |
| `{"type":"resize","width":…,"height":…,"dpr":…}` | Quando a tela gira ou muda de tamanho. Valores fora de 320…8192 são ignorados. |
| `{"type":"codecs","codecs":["jpeg"]}` | O decodificador H.264 falhou: volta para JPEG. |
| `{"type":"keyframe-request"}` | O viewer perdeu frames ou ficou para trás. |

### Servidor → viewer

* **binário**: os mesmos frames da fonte. Um viewer sem H.264 recebe só JPEG. Um viewer novo só recebe deltas depois de um quadro-chave.
* `{"type":"source","connected":true|false}`
* `{"type":"stream-info",…}` (repasse da fonte)

**Filas por viewer:** JPEG guarda no máximo 1 frame pendente, sempre o mais recente. H.264 usa uma fila ordenada de até 120 frames (~2 s a 60 fps). Se ela encher, é esvaziada e o servidor pede um novo quadro-chave. Assim, um aparelho lento pula imagens em vez de acumular atraso.

## 4. HTTP

| Rota | Descrição |
|---|---|
| `GET /` | Arquivos estáticos do cliente web (`Cache-Control: no-cache`) |
| `GET /api/info` | `{"version","port","urls","sourceConnected","viewers","h264"}` |

## Evolução

Mudanças compatíveis (novos campos ou tipos de mensagem) não alteram a versão. Codecs novos (por exemplo, HEVC) entram como novos valores do 1º byte e em `codecs`.
