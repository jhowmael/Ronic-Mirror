// Ronic Mirror — página aberta no iPad, tablet ou celular.
// Recebe vídeo H.264 (WebCodecs, decodificado por hardware) ou JPEG via WebSocket (/view)
// e desenha em um <canvas> em tela cheia. Envia a resolução da tela para o monitor virtual se ajustar.
// Contrato: docs/PROTOCOL.md
(() => {
  "use strict";

  const canvas = document.getElementById("screen");
  const ctx = canvas.getContext("2d", { alpha: false, desynchronized: true });
  const overlay = document.getElementById("overlay");
  const statusEl = document.getElementById("status");
  const badge = document.getElementById("badge");

  const FRAME_H264_CONFIG = 0x10;
  const FRAME_H264_KEY = 0x11;
  const FRAME_H264_DELTA = 0x12;
  const FRAME_JPEG = 0xff;

  let ws = null;
  let sourceConnected = false;
  let receivedFrame = false;
  let userDismissed = false;
  let wakeLock = null;
  let h264Supported = false; // definido por probeH264() antes de conectar

  // ---------- Status / overlay ----------

  function setStatus(text, kind) {
    statusEl.textContent = text;
    statusEl.className = kind || "";
    updateOverlay();
  }

  function updateOverlay() {
    const streaming = ws && ws.readyState === WebSocket.OPEN && sourceConnected && receivedFrame;
    overlay.classList.toggle("hidden", streaming && userDismissed);
  }

  function onFirstFrame() {
    if (!receivedFrame) {
      receivedFrame = true;
      setStatus("Transmitindo", "ok");
    }
  }

  // ---------- Resolução da tela ----------

  // Tamanho da tela inteira (não só da janela), na orientação atual, em pontos CSS.
  function screenSize() {
    const sw = window.screen.width;
    const sh = window.screen.height;
    const landscape = window.innerWidth >= window.innerHeight;
    const long = Math.max(sw, sh);
    const short = Math.min(sw, sh);
    return {
      width: Math.round(landscape ? long : short),
      height: Math.round(landscape ? short : long),
      dpr: window.devicePixelRatio || 1,
    };
  }

  function send(obj) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  function sendResolution(type) {
    const msg = Object.assign({ type }, screenSize());
    if (type === "hello") msg.codecs = h264Supported ? ["h264", "jpeg"] : ["jpeg"];
    send(msg);
  }

  let resizeTimer = null;
  function onResize() {
    layoutCanvas();
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => sendResolution("resize"), 400);
  }
  window.addEventListener("resize", onResize);
  window.addEventListener("orientationchange", onResize);

  // Centraliza o canvas mantendo a proporção (letterbox).
  function layoutCanvas() {
    if (!canvas.width || !canvas.height) return;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const scale = Math.min(vw / canvas.width, vh / canvas.height);
    const w = Math.round(canvas.width * scale);
    const h = Math.round(canvas.height * scale);
    canvas.style.width = w + "px";
    canvas.style.height = h + "px";
    canvas.style.left = Math.round((vw - w) / 2) + "px";
    canvas.style.top = Math.round((vh - h) / 2) + "px";
  }

  function drawSource(source, width, height) {
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
      layoutCanvas();
    }
    ctx.drawImage(source, 0, 0, width, height);
    frames++;
    onFirstFrame();
  }

  // ---------- Estatísticas ----------

  let frames = 0;
  let bytes = 0;
  let codecLabel = "";

  setInterval(() => {
    if (!badge.hidden) {
      badge.textContent = `${codecLabel} · ${frames} fps · ${(bytes * 8 / 1e6).toFixed(1)} Mbps · ${canvas.width}×${canvas.height}`;
    }
    frames = 0;
    bytes = 0;
  }, 1000);

  // ---------- JPEG (sempre o frame mais recente) ----------

  let pendingJpeg = null;
  let decodingJpeg = false;

  function enqueueJpeg(data) {
    codecLabel = "JPEG";
    pendingJpeg = data;
    if (!decodingJpeg) decodeNextJpeg();
  }

  async function decodeNextJpeg() {
    if (!pendingJpeg) { decodingJpeg = false; return; }
    decodingJpeg = true;
    const data = pendingJpeg;
    pendingJpeg = null;
    try {
      const bitmap = await createImageBitmap(new Blob([data], { type: "image/jpeg" }));
      drawSource(bitmap, bitmap.width, bitmap.height);
      if (bitmap.close) bitmap.close();
    } catch (e) {
      console.warn("JPEG inválido", e);
    }
    decodeNextJpeg();
  }

  // ---------- H.264 (WebCodecs, decodificação por hardware) ----------

  let decoder = null;
  let lastConfig = null; // bytes do avcC atualmente configurado
  let needKey = true;
  let decodeErrors = 0;

  async function probeH264() {
    if (typeof VideoDecoder !== "function" || typeof EncodedVideoChunk !== "function") return false;
    try {
      const r = await VideoDecoder.isConfigSupported({ codec: "avc1.640033", optimizeForLatency: true });
      return !!r.supported;
    } catch {
      return false;
    }
  }

  function hex2(n) { return n.toString(16).padStart(2, "0"); }

  function sameBytes(a, b) {
    if (!a || !b || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  }

  function createDecoder() {
    decoder = new VideoDecoder({
      output: (frame) => {
        drawSource(frame, frame.displayWidth, frame.displayHeight);
        frame.close();
      },
      error: (e) => {
        console.warn("erro no decodificador H.264", e);
        decodeErrors++;
        lastConfig = null;
        needKey = true;
        if (decodeErrors >= 3) {
          // Este aparelho não está dando conta: volta para JPEG.
          h264Supported = false;
          send({ type: "codecs", codecs: ["jpeg"] });
        } else {
          send({ type: "keyframe-request" });
        }
      },
    });
  }

  function configureH264(avcc) {
    if (sameBytes(avcc, lastConfig) && decoder && decoder.state === "configured") return;
    if (!decoder || decoder.state === "closed") createDecoder();
    const codec = "avc1." + hex2(avcc[1]) + hex2(avcc[2]) + hex2(avcc[3]);
    try {
      decoder.configure({ codec, description: avcc.slice(), optimizeForLatency: true, hardwareAcceleration: "prefer-hardware" });
      lastConfig = avcc.slice();
      needKey = true;
    } catch (e) {
      console.warn("configuração H.264 recusada", e);
      h264Supported = false;
      send({ type: "codecs", codecs: ["jpeg"] });
    }
  }

  function decodeH264(u8, isKey) {
    if (!decoder || decoder.state !== "configured") return;
    if (!isKey && needKey) return;
    // Se o aparelho ficou para trás, descarta tudo e pede um quadro-chave (evita acumular atraso).
    if (decoder.decodeQueueSize > 8 && !isKey) {
      decoder.reset();
      lastConfig = null;
      needKey = true;
      send({ type: "keyframe-request" });
      return;
    }
    const view = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
    const timestamp = Number(view.getBigUint64(1));
    decoder.decode(new EncodedVideoChunk({ type: isKey ? "key" : "delta", timestamp, data: u8.subarray(9) }));
    if (isKey) {
      needKey = false;
      decodeErrors = 0;
    }
  }

  function handleBinary(buf) {
    bytes += buf.byteLength;
    const u8 = new Uint8Array(buf);
    switch (u8[0]) {
      case FRAME_JPEG:
        enqueueJpeg(buf);
        break;
      case FRAME_H264_CONFIG:
        codecLabel = "H.264";
        configureH264(u8.subarray(1));
        break;
      case FRAME_H264_KEY:
        decodeH264(u8, true);
        break;
      case FRAME_H264_DELTA:
        decodeH264(u8, false);
        break;
    }
  }

  // ---------- WebSocket ----------

  function connect() {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    ws = new WebSocket(`${proto}//${location.host}/view`);
    ws.binaryType = "arraybuffer";

    ws.onopen = () => {
      setStatus("Conectado. Aguardando o computador…", "warn");
      needKey = true;
      sendResolution("hello");
    };

    ws.onmessage = (ev) => {
      if (typeof ev.data !== "string") {
        handleBinary(ev.data);
        return;
      }
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.type === "source") {
        sourceConnected = !!msg.connected;
        if (!sourceConnected) {
          receivedFrame = false;
          setStatus("Computador desconectado. Aguardando…", "warn");
        } else {
          needKey = true;
          setStatus(receivedFrame ? "Transmitindo" : "Computador conectado. Aguardando imagem…", "ok");
        }
      }
    };

    ws.onclose = () => {
      sourceConnected = false;
      receivedFrame = false;
      setStatus("Sem conexão. Tentando novamente…", "warn");
      setTimeout(connect, 1000);
    };

    ws.onerror = () => { try { ws.close(); } catch {} };
  }

  // ---------- Tela cheia + manter a tela acesa ----------

  function isFullscreen() {
    return !!(document.fullscreenElement || document.webkitFullscreenElement);
  }

  function enterFullscreen() {
    const el = document.documentElement;
    if (isFullscreen()) return;
    const req = el.requestFullscreen || el.webkitRequestFullscreen;
    if (req) {
      try {
        const p = req.call(el);
        if (p && p.catch) p.catch(() => {});
      } catch {}
    }
  }

  async function keepAwake() {
    try {
      if ("wakeLock" in navigator && !wakeLock) {
        wakeLock = await navigator.wakeLock.request("screen");
        wakeLock.addEventListener("release", () => { wakeLock = null; });
      }
    } catch {}
  }

  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") keepAwake();
  });

  overlay.addEventListener("click", () => {
    userDismissed = true;
    enterFullscreen();
    keepAwake();
    updateOverlay();
  });

  // Toque com 3 dedos: mostra/oculta estatísticas. Toque simples fora da tela cheia: volta à tela cheia.
  canvas.addEventListener("touchstart", (e) => {
    if (e.touches.length === 3) badge.hidden = !badge.hidden;
  }, { passive: true });
  canvas.addEventListener("click", () => { enterFullscreen(); keepAwake(); });

  document.addEventListener("gesturestart", (e) => e.preventDefault()); // bloqueia zoom por pinça

  probeH264().then((ok) => {
    h264Supported = ok;
    connect();
  });
})();
