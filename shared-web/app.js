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
  const cursorEl = document.getElementById("cursor");
  const hintEl = document.getElementById("hint");
  const retryBtn = document.getElementById("retry");

  const FRAME_H264_CONFIG = 0x10;
  const FRAME_H264_KEY = 0x11;
  const FRAME_H264_DELTA = 0x12;
  const FRAME_CURSOR_POS = 0x20;
  const FRAME_CURSOR_IMG = 0x21;
  const FRAME_JPEG = 0xff;

  let ws = null;
  let sourceConnected = false;
  let receivedFrame = false;
  let userDismissed = false;
  let wakeLock = null;
  let h264Supported = false; // definido por probeH264() antes de conectar
  let authState = "pending"; // pending | accepted | rejected | kicked | blocked
  let stopReconnect = false;
  let serverVersion = null; // versão do computador quando a página abriu

  // ---------- Identificação do aparelho (para o computador autorizar/bloquear) ----------

  function deviceId() {
    const key = "ronic-mirror-device-id";
    try {
      let id = localStorage.getItem(key);
      if (!id) {
        id = (crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2));
        localStorage.setItem(key, id);
      }
      return id;
    } catch {
      return "";
    }
  }

  function deviceName() {
    const ua = navigator.userAgent;
    let device = "Aparelho";
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) device = "iPad";
    else if (/iPhone/.test(ua)) device = "iPhone";
    else if (/Android/.test(ua)) device = /Mobile/.test(ua) ? "Celular Android" : "Tablet Android";
    else if (/Windows/.test(ua)) device = "PC Windows";
    else if (/Macintosh/.test(ua)) device = "Mac";
    else if (/Linux/.test(ua)) device = "Linux";
    let browser = "";
    if (/EdgA?\//.test(ua)) browser = "Edge";
    else if (/CriOS|Chrome\//.test(ua)) browser = "Chrome";
    else if (/FxiOS|Firefox\//.test(ua)) browser = "Firefox";
    else if (/Safari\//.test(ua)) browser = "Safari";
    return browser ? `${device} (${browser})` : device;
  }

  // ---------- Status / overlay ----------

  function setStatus(text, kind) {
    statusEl.textContent = text;
    statusEl.className = kind || "";
    updateOverlay();
  }

  function updateOverlay() {
    const streaming = ws && ws.readyState === WebSocket.OPEN && sourceConnected && receivedFrame && authState === "accepted";
    overlay.classList.toggle("hidden", streaming && userDismissed);
  }

  function onFirstFrame() {
    if (!receivedFrame) {
      receivedFrame = true;
      setStatus("Transmitindo", "ok");
      moveCursor();
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
    if (type === "hello") {
      msg.codecs = h264Supported ? ["h264", "jpeg"] : ["jpeg"];
      msg.deviceId = deviceId();
      msg.deviceName = deviceName();
    }
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
    canvasRect = { left: Math.round((vw - w) / 2), top: Math.round((vh - h) / 2), width: w, height: h };
    applyCursorSize();
    moveCursor();
  }

  // ---------- Cursor desenhado no aparelho (latência mínima, fora do vídeo) ----------

  let canvasRect = { left: 0, top: 0, width: 0, height: 0 };
  // Seta padrão até o computador mandar o formato real do cursor.
  const DEFAULT_CURSOR = "data:image/svg+xml," + encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="17" height="25" viewBox="0 0 17 25">' +
    '<path d="M1 1v20l5-5 3.5 8 3-1.3-3.4-7.7H16z" fill="#000" stroke="#fff" stroke-width="1.4" stroke-linejoin="round"/></svg>');
  let cursorShape = { w: 17, h: 25, hx: 1, hy: 1, url: DEFAULT_CURSOR };
  cursorEl.src = DEFAULT_CURSOR;
  let cursorPos = { x: 0, y: 0, visible: false };

  function cssScale() {
    return canvas.width ? canvasRect.width / canvas.width : 1;
  }

  function applyCursorSize() {
    if (!cursorShape.url) return;
    const s = cssScale();
    cursorEl.style.width = cursorShape.w * s + "px";
    cursorEl.style.height = cursorShape.h * s + "px";
  }

  function moveCursor() {
    const show = cursorPos.visible && !!cursorShape.url && receivedFrame && authState === "accepted";
    cursorEl.hidden = !show;
    if (!show) return;
    const s = cssScale();
    const x = canvasRect.left + cursorPos.x * canvasRect.width - cursorShape.hx * s;
    const y = canvasRect.top + cursorPos.y * canvasRect.height - cursorShape.hy * s;
    cursorEl.style.transform = `translate3d(${x}px, ${y}px, 0)`;
  }

  function handleCursorPos(buf) {
    const dv = new DataView(buf);
    cursorPos = { x: dv.getFloat32(1, true), y: dv.getFloat32(5, true), visible: dv.getUint8(9) === 1 };
    moveCursor();
  }

  function handleCursorImg(buf) {
    const dv = new DataView(buf);
    const w = dv.getFloat32(1, true), h = dv.getFloat32(5, true);
    const hx = dv.getFloat32(9, true), hy = dv.getFloat32(13, true);
    const url = URL.createObjectURL(new Blob([new Uint8Array(buf, 17)], { type: "image/png" }));
    cursorEl.onload = () => {
      if (cursorShape.url && cursorShape.url.startsWith("blob:")) URL.revokeObjectURL(cursorShape.url);
      cursorShape = { w, h, hx, hy, url };
      applyCursorSize();
      moveCursor();
    };
    cursorEl.src = url;
  }

  // Tamanho oficial da tela virtual (mensagem "stream-info" do computador). Desenhar o frame
  // SEMPRE nesse tamanho evita imagem esticada quando a resolução muda no meio da transmissão.
  let streamSize = null;

  function drawSource(source, frameWidth, frameHeight) {
    const width = streamSize ? streamSize.w : frameWidth;
    const height = streamSize ? streamSize.h : frameHeight;
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
      // Resolução/configuração nova: zera o decodificador antes (alguns navegadores mantêm o tamanho antigo).
      if (decoder.state === "configured") decoder.reset();
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
      case FRAME_CURSOR_POS:
        handleCursorPos(buf);
        return;
      case FRAME_CURSOR_IMG:
        handleCursorImg(buf);
        return;
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
      authState = "pending";
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
      if (msg.type === "server") {
        // O computador foi atualizado enquanto esta página estava aberta: recarrega para usar a versão nova.
        if (serverVersion && msg.version !== serverVersion) { location.reload(); return; }
        serverVersion = msg.version;
        return;
      }
      if (msg.type === "stream-info") {
        if (msg.width > 0 && msg.height > 0) streamSize = { w: msg.width, h: msg.height };
        return;
      }
      if (msg.type === "auth") {
        handleAuth(msg.state);
        return;
      }
      if (msg.type === "source") {
        sourceConnected = !!msg.connected;
        if (!sourceConnected) {
          receivedFrame = false;
          setStatus("Computador desconectado. Aguardando…", "warn");
        } else if (authState === "pending") {
          setStatus("Aguardando autorização no computador…", "warn");
        } else {
          needKey = true;
          setStatus(receivedFrame ? "Transmitindo" : "Computador conectado. Aguardando imagem…", "ok");
        }
      }
    };

    ws.onclose = () => {
      sourceConnected = false;
      receivedFrame = false;
      moveCursor();
      if (stopReconnect) return; // recusado, desconectado ou bloqueado pelo computador
      setStatus("Sem conexão. Tentando novamente…", "warn");
      setTimeout(connect, 1000);
    };

    ws.onerror = () => { try { ws.close(); } catch {} };
  }

  function handleAuth(state) {
    authState = state;
    const messages = {
      pending: ["Aguardando autorização no computador… Clique em Aceitar no Ronic Mirror.", "warn"],
      rejected: ["O computador recusou a conexão.", "bad"],
      kicked: ["O computador desconectou este aparelho.", "bad"],
      blocked: ["Este aparelho foi bloqueado no computador.", "bad"],
    };
    if (state === "accepted") {
      setStatus(receivedFrame ? "Transmitindo" : "Autorizado. Aguardando imagem…", "ok");
      sendResolution("resize");
    } else if (messages[state]) {
      setStatus(messages[state][0], messages[state][1]);
    }
    const ended = state === "rejected" || state === "kicked" || state === "blocked";
    stopReconnect = ended;
    retryBtn.hidden = !(state === "rejected" || state === "kicked");
    hintEl.hidden = ended;
    if (ended) {
      receivedFrame = false;
      userDismissed = false;
      overlay.classList.remove("hidden");
    }
    moveCursor();
  }

  retryBtn.addEventListener("click", (e) => {
    e.stopPropagation();
    stopReconnect = false;
    retryBtn.hidden = true;
    hintEl.hidden = false;
    connect();
  });

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

  // ---------- Toque → mouse no computador ----------
  //   1 dedo, toque rápido ........ clique esquerdo (dois toques = clique duplo)
  //   2 dedos, toque rápido ....... clique direito
  //   arrastar (1 ou 2 dedos) ..... rolagem
  //   segurar parado e arrastar ... clicar e arrastar (mover janelas, selecionar texto)
  //   3 dedos ..................... mostra/oculta as estatísticas

  const TAP_MAX_MS = 350;
  const TAP_MAX_MOVE = 10; // px
  const HOLD_MS = 350;
  const EDGE = 0.03; // na borda, o computador continua o arrasto para o monitor vizinho
  const dragRing = document.getElementById("drag-ring");

  function showRing(p, onEdge) {
    dragRing.hidden = false;
    dragRing.classList.toggle("edge", !!onEdge);
    dragRing.style.transform = `translate3d(${p.x}px, ${p.y}px, 0)`;
  }
  function hideRing() { dragRing.hidden = true; }
  function atEdge(p) {
    const q = toScreen(p);
    return q.x <= EDGE || q.x >= 1 - EDGE || q.y <= EDGE || q.y >= 1 - EDGE;
  }
  let gesture = null;

  function centroid(touches) {
    let x = 0, y = 0;
    for (const t of touches) { x += t.clientX; y += t.clientY; }
    return { x: x / touches.length, y: y / touches.length };
  }

  function toScreen(p) {
    return {
      x: Math.min(Math.max((p.x - canvasRect.left) / canvasRect.width, 0), 1),
      y: Math.min(Math.max((p.y - canvasRect.top) / canvasRect.height, 0), 1),
    };
  }

  function sendInput(kind, p, extra) {
    if (authState !== "accepted" || !receivedFrame || !canvasRect.width) return;
    const q = toScreen(p);
    send(Object.assign({ type: "input", kind, x: q.x, y: q.y }, extra || {}));
  }

  function controlling() {
    return overlay.classList.contains("hidden") && authState === "accepted" && receivedFrame;
  }

  document.addEventListener("touchstart", (e) => {
    if (!controlling()) return; // com o aviso aberto, o toque é para o aviso (tela cheia)
    e.preventDefault();
    if (e.touches.length >= 3) {
      if (gesture && gesture.mode === "drag") sendInput("up", gesture.last);
      if (gesture) clearTimeout(gesture.holdTimer);
      gesture = { mode: "ignore" };
      badge.hidden = !badge.hidden;
      return;
    }
    const c = centroid(e.touches);
    if (!gesture) {
      gesture = { mode: "none", start: c, startTime: performance.now(), last: c, fingers: e.touches.length };
      if (e.touches.length === 1) {
        gesture.holdTimer = setTimeout(() => {
          if (gesture && gesture.mode === "none") {
            gesture.mode = "drag";
            sendInput("down", gesture.last);
            showRing(gesture.last, false);
            if (navigator.vibrate) navigator.vibrate(12);
          }
        }, HOLD_MS);
      }
    } else if (gesture.mode !== "ignore") {
      gesture.fingers = Math.max(gesture.fingers, e.touches.length);
      clearTimeout(gesture.holdTimer);
      gesture.last = c;
    }
  }, { passive: false });

  document.addEventListener("touchmove", (e) => {
    if (!gesture || gesture.mode === "ignore") return;
    e.preventDefault();
    const c = centroid(e.touches);
    if (gesture.mode === "none" && Math.hypot(c.x - gesture.start.x, c.y - gesture.start.y) > TAP_MAX_MOVE) {
      clearTimeout(gesture.holdTimer);
      gesture.mode = "scroll";
    }
    if (gesture.mode === "scroll") {
      const dx = (c.x - gesture.last.x) / canvasRect.width;
      const dy = (c.y - gesture.last.y) / canvasRect.height;
      if (dx || dy) sendInput("scroll", c, { dx, dy });
    } else if (gesture.mode === "drag") {
      sendInput("move", c);
      showRing(c, atEdge(c)); // amarelo = na borda: segure para levar ao outro monitor
    }
    gesture.last = c;
  }, { passive: false });

  function endGesture(e, cancelled) {
    if (!gesture) return;
    e.preventDefault();
    if (e.touches.length > 0) { // ainda há dedos na tela
      if (gesture.mode !== "ignore") gesture.last = centroid(e.touches);
      return;
    }
    clearTimeout(gesture.holdTimer);
    const quick = performance.now() - gesture.startTime < TAP_MAX_MS;
    if (gesture.mode === "none" && quick && !cancelled) {
      sendInput(gesture.fingers >= 2 ? "rtap" : "tap", gesture.start);
    } else if (gesture.mode === "drag") {
      sendInput("up", gesture.last);
    }
    hideRing();
    gesture = null;
    if (!isFullscreen()) enterFullscreen();
    keepAwake();
  }
  document.addEventListener("touchend", (e) => endGesture(e, false), { passive: false });
  document.addEventListener("touchcancel", (e) => endGesture(e, true), { passive: false });

  document.addEventListener("gesturestart", (e) => e.preventDefault()); // bloqueia zoom por pinça

  probeH264().then((ok) => {
    h264Supported = ok;
    connect();
  });
})();
