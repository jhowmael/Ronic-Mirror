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
| `0xA0` | `0xA0` + tamanho do conn (u8) + conn (ASCII) + uma das mensagens acima | **Endereçada a um aparelho.** Cada aparelho tem a sua própria tela virtual, e o servidor entrega a mensagem só ao aparelho `conn`. Sem esse prefixo, a mensagem vai para todos. |
| `0x20` | `0x20` + x, y (float32 LE, de 0 a 1 dentro da tela virtual) + visível (u8) | **Posição do cursor**, fora do vídeo: até 120×/s, só quando muda. O vídeo é capturado **sem** cursor, e o aparelho desenha o ponteiro sozinho, sem o atraso de compressão e decodificação. |
| `0x21` | `0x21` + largura, altura, hotspot X, hotspot Y (float32 LE, nas unidades da tela virtual) + PNG | **Formato do cursor**, enviado quando muda (seta, mão, texto…). O PNG pode ter mais pixels que a largura/altura (2×, para Retina). |

Tamanho máximo de 64 MB por mensagem. O H.264 deve ser gerado **sem B-frames**, em tempo real (baixa latência).

**Mensagens de texto**

| Mensagem | |
|---|---|
| `{"type":"hello","role":"source","platform":"macos"\|"windows","version":"…"}` | Informativo |
| `{"type":"stream-info","conn":"d3","codec":"h264"\|"jpeg","width":1180,"height":820,"fps":60}` | Tamanho oficial da tela do aparelho `conn`. Envie ao criar a tela e **antes** dos frames de uma resolução nova. A página desenha sempre nesse tamanho. |
| `{"type":"policy","requireApproval":true,"trusted":["<deviceId>"],"blockedIds":["<deviceId>"],"blockedIps":["192.168.1.20"]}` | **Envie ao conectar** e sempre que as listas mudarem. O app nativo guarda as listas; o servidor só aplica. Enquanto a política não chega, todo aparelho novo fica aguardando. |
| `{"type":"device-action","conn":"d3","action":"accept"\|"reject"\|"kick"\|"block"}` | Aceitar um aparelho que aguarda, recusar, desconectar ou bloquear (por deviceId **e** IP). |

### Servidor → fonte

| Mensagem | Quando |
|---|---|
| `{"type":"server-info","version":"…","port":3000,"urls":["http://…"]}` | Logo ao conectar |
| `{"type":"viewers","count":1,"h264":true}` | Ao conectar e quando um viewer entra, sai ou muda de codec. `h264` é `true` quando **todos** os viewers decodificam H.264. Nesse caso a fonte deve usar H.264; caso contrário, JPEG. |
| `{"type":"keyframe-request","conn":"d3"}` | Esse aparelho entrou ou ficou para trás. Gere um quadro-chave (H.264) ou reenvie a tela atual (JPEG) **da tela dele**. |
| `{"type":"input","conn":"d3","kind":"tap"\|"rtap"\|"scroll"\|"down"\|"move"\|"up","x":0.5,"y":0.5,"dx":0,"dy":-0.02}` | Toque no aparelho. `x`/`y` vão de 0 a 1 dentro da tela do aparelho; `dx`/`dy` são frações da largura e da altura. `tap` = clique esquerdo, `rtap` = clique direito, `scroll` = rolagem (o conteúdo acompanha o dedo), `down`/`move`/`up` = clicar e arrastar. |
| `{"type":"devices","devices":[{"conn":"d3","deviceId":"…","name":"iPad (Safari)","ip":"192.168.1.20","state":"pending"\|"active","since":1764000000000,"codec":"h264","width":1180,"height":820}]}` | Sempre que um aparelho entra, sai, muda de estado ou de resolução. `state: "pending"` = aguardando você aceitar. |
| `{"type":"viewer-resolution","conn":"d3","width":1180,"height":820,"dpr":2}` | Quando o aparelho é aceito ou gira. São **pontos CSS** já na orientação atual. A fonte ajusta **a tela desse aparelho** para esse tamanho. |

Uma fonte que só sabe JPEG (como a do Windows hoje) funciona normalmente: ela ignora `h264` e manda `0xFF`.

## 3. Viewer (iPad, tablet, celular ou qualquer navegador): `ws://IP:PORT/view`

### Viewer → servidor

| Mensagem | |
|---|---|
| `{"type":"hello","width":1180,"height":820,"dpr":2,"codecs":["h264","jpeg"],"deviceId":"<uuid salvo no navegador>","deviceName":"iPad (Safari)"}` | Ao conectar. Inclua `"h264"` só se `VideoDecoder.isConfigSupported` confirmar o suporte. O `deviceId` identifica o aparelho para "confiar" e "bloquear". |
| `{"type":"resize","width":…,"height":…,"dpr":…}` | Quando a tela gira ou muda de tamanho. Valores fora de 320…8192 são ignorados. |
| `{"type":"codecs","codecs":["jpeg"]}` | O decodificador H.264 falhou: volta para JPEG. |
| `{"type":"keyframe-request"}` | O viewer perdeu frames ou ficou para trás. |
| `{"type":"input",…}` | Toques (ver a tabela da fonte). Só são repassados de aparelhos aceitos. |

### Servidor → viewer

* **binário**: os mesmos frames da fonte. Um viewer sem H.264 recebe só JPEG. Um viewer novo só recebe deltas depois de um quadro-chave.
* `{"type":"server","version":"0.4.0"}`: logo ao conectar. Se a versão mudar entre reconexões (o app foi atualizado), a página se recarrega sozinha.
* `{"type":"source","connected":true|false}`
* `{"type":"auth","state":"pending"|"accepted"|"rejected"|"kicked"|"blocked"}`: resultado da autorização. Um aparelho só recebe imagem e cursor depois de `accepted`. Em `rejected`, `kicked` e `blocked` o servidor fecha a conexão logo em seguida, e a página não reconecta sozinha.
* `{"type":"stream-info",…}` (repasse da fonte)

**Latência:** JPEG guarda no máximo 1 frame pendente por aparelho, sempre o mais recente. H.264 usa uma fila ordenada de no máximo 12 frames (~200 ms). Se ela encher, é esvaziada e o servidor pede um quadro-chave novo. O buffer de envio do sistema também é limitado (128 KB), e a compressão do WebSocket fica desligada. Assim, num Wi-Fi lento o aparelho **pula imagens em vez de ficar atrasado**. O cursor tem prioridade sobre o vídeo na fila de envio.

## 4. HTTP

| Rota | Descrição |
|---|---|
| `GET /` | Arquivos estáticos do cliente web (`Cache-Control: no-cache`) |
| `GET /api/info` | `{"version","port","urls","sourceConnected","viewers","h264"}` |

## Evolução

Mudanças compatíveis (novos campos ou tipos de mensagem) não alteram a versão. Codecs novos (por exemplo, HEVC) entram como novos valores do 1º byte e em `codecs`.
