// Ronic Mirror — cliente web do iPad.
// Recebe frames JPEG via WebSocket (/view) e desenha em um <canvas> em tela cheia.
// Envia ao servidor a resolução da tela do iPad para o monitor virtual se ajustar.
// Contrato: docs/PROTOCOL.md
(() => {
  "use strict";

  const canvas = document.getElementById("screen");
  const ctx = canvas.getContext("2d", { alpha: false, desynchronized: true });
  const overlay = document.getElementById("overlay");
  const statusEl = document.getElementById("status");
  const badge = document.getElementById("badge");

  let ws = null;
  let sourceConnected = false;
  let receivedFrame = false;
  let userDismissed = false;
  let wakeLock = null;

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

  function sendResolution(type) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(Object.assign({ type }, screenSize())));
    }
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

  // ---------- Decodificação (sempre o frame mais recente) ----------

  let pending = null;
  let decoding = false;
  let frames = 0;
  let bytes = 0;

  function enqueueFrame(data) {
    pending = data;
    bytes += data.byteLength;
    if (!decoding) decodeNext();
  }

  async function decodeNext() {
    if (!pending) { decoding = false; return; }
    decoding = true;
    const data = pending;
    pending = null;
    try {
      const bitmap = await createImageBitmap(new Blob([data], { type: "image/jpeg" }));
      if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        layoutCanvas();
      }
      ctx.drawImage(bitmap, 0, 0);
      if (bitmap.close) bitmap.close();
      frames++;
      if (!receivedFrame) {
        receivedFrame = true;
        setStatus("Transmitindo", "ok");
      }
    } catch (e) {
      console.warn("frame inválido", e);
    }
    decodeNext();
  }

  setInterval(() => {
    if (!badge.hidden) {
      badge.textContent = `${frames} fps · ${(bytes * 8 / 1e6).toFixed(1)} Mbps · ${canvas.width}×${canvas.height}`;
    }
    frames = 0;
    bytes = 0;
  }, 1000);

  // ---------- WebSocket ----------

  function connect() {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    ws = new WebSocket(`${proto}//${location.host}/view`);
    ws.binaryType = "arraybuffer";

    ws.onopen = () => {
      setStatus("Conectado. Aguardando o computador…", "warn");
      sendResolution("hello");
    };

    ws.onmessage = (ev) => {
      if (typeof ev.data !== "string") {
        enqueueFrame(ev.data);
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

  connect();
})();
