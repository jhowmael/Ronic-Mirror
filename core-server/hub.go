package main

import (
	"context"
	"encoding/json"
	"log"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"
)

const (
	maxFrameBytes = 64 << 20 // 64 MB: limite de leitura de um frame vindo da fonte
	writeTimeout  = 5 * time.Second
	videoQueueLen = 120 // ~2 s de vídeo a 60 fps por viewer
)

// Tipos de frame binário (primeiro byte). Ver docs/PROTOCOL.md.
const (
	frameH264Config = 0x10 // avcC (AVCDecoderConfigurationRecord)
	frameH264Key    = 0x11 // quadro-chave: 8 bytes de timestamp (µs, BE) + NALs AVCC
	frameH264Delta  = 0x12 // quadro delta: idem
	frameJPEG       = 0xFF // JPEG puro (começa com FF D8)
)

// Resolution é o tamanho de tela informado por um viewer, em pontos CSS.
type Resolution struct {
	Width  int     `json:"width"`
	Height int     `json:"height"`
	DPR    float64 `json:"dpr"`
}

type peer struct {
	conn   *websocket.Conn
	frames chan []byte // fila ordenada de frames binários
	ctrl   chan []byte // mensagens de texto (JSON)
	cancel context.CancelFunc

	// Somente viewers (protegidos por Hub.mu):
	h264    bool // o navegador decodifica H.264 (WebCodecs)
	needKey bool // aguardando um quadro-chave antes de receber deltas
}

func newPeer(c *websocket.Conn, cancel context.CancelFunc) *peer {
	return &peer{conn: c, frames: make(chan []byte, videoQueueLen), ctrl: make(chan []byte, 32), cancel: cancel, needKey: true}
}

// pushLatest descarta o que estiver pendente e enfileira só o frame novo (JPEG: latest-frame-wins).
func (p *peer) pushLatest(f []byte) {
	p.drain()
	select {
	case p.frames <- f:
	default:
	}
}

// pushOrdered enfileira sem descartar (vídeo H.264). Retorna false se a fila estiver cheia.
func (p *peer) pushOrdered(f []byte) bool {
	select {
	case p.frames <- f:
		return true
	default:
		return false
	}
}

func (p *peer) drain() {
	for {
		select {
		case <-p.frames:
		default:
			return
		}
	}
}

func (p *peer) pushCtrl(v any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	select {
	case p.ctrl <- b:
	default: // fila cheia: descartar mensagem de controle antiga é aceitável
	}
}

// writeLoop serializa todas as escritas no socket (o websocket não aceita escritas concorrentes).
func (p *peer) writeLoop(ctx context.Context) {
	defer p.cancel()
	for {
		var typ websocket.MessageType
		var data []byte
		select {
		case <-ctx.Done():
			return
		case data = <-p.ctrl:
			typ = websocket.MessageText
		case data = <-p.frames:
			typ = websocket.MessageBinary
		}
		wctx, cancel := context.WithTimeout(ctx, writeTimeout)
		err := p.conn.Write(wctx, typ, data)
		cancel()
		if err != nil {
			return
		}
	}
}

type Hub struct {
	port int

	mu          sync.Mutex
	source      *peer
	viewers     map[*peer]struct{}
	lastFrame   []byte          // último JPEG (enviado na hora a quem conecta)
	streamInfo  json.RawMessage // última mensagem "stream-info" da fonte
	lastViewRes *Resolution
	lastKeyReq  time.Time
	lastH264    bool // último valor de "h264" informado à fonte
}

func newHub(port int) *Hub {
	return &Hub{port: port, viewers: make(map[*peer]struct{})}
}

// ---------- Fonte (app nativo, somente loopback) ----------

func (h *Hub) handleSource(w http.ResponseWriter, r *http.Request) {
	if !isLoopback(r.RemoteAddr) {
		http.Error(w, "a fonte só pode conectar via 127.0.0.1", http.StatusForbidden)
		return
	}
	c, err := websocket.Accept(w, r, nil)
	if err != nil {
		return
	}
	c.SetReadLimit(maxFrameBytes)
	ctx, cancel := context.WithCancel(context.Background())
	src := newPeer(c, cancel)
	defer func() {
		cancel()
		c.Close(websocket.StatusNormalClosure, "")
	}()

	h.mu.Lock()
	if old := h.source; old != nil {
		old.cancel() // só uma fonte por vez: a nova substitui a antiga
	}
	h.source = src
	res := h.lastViewRes
	for v := range h.viewers {
		v.needKey = true
		v.pushCtrl(map[string]any{"type": "source", "connected": true})
	}
	h.mu.Unlock()

	log.Printf("fonte conectada (%s)", r.RemoteAddr)
	go src.writeLoop(ctx)

	src.pushCtrl(map[string]any{"type": "server-info", "version": version, "port": h.port, "urls": viewerURLs(h.port)})
	h.mu.Lock()
	h.notifyViewersLocked()
	h.mu.Unlock()
	if res != nil {
		src.pushCtrl(map[string]any{"type": "viewer-resolution", "width": res.Width, "height": res.Height, "dpr": res.DPR})
	}

	for {
		typ, data, err := c.Read(ctx)
		if err != nil {
			break
		}
		switch typ {
		case websocket.MessageBinary:
			if len(data) > 0 {
				h.broadcastFrame(data)
			}
		case websocket.MessageText:
			h.handleSourceText(data)
		}
	}

	h.mu.Lock()
	if h.source == src {
		h.source = nil
		h.lastFrame = nil
		h.streamInfo = nil
		for v := range h.viewers {
			v.pushCtrl(map[string]any{"type": "source", "connected": false})
		}
	}
	h.mu.Unlock()
	log.Printf("fonte desconectada")
}

func (h *Hub) handleSourceText(data []byte) {
	var msg struct {
		Type string `json:"type"`
	}
	if json.Unmarshal(data, &msg) != nil {
		return
	}
	switch msg.Type {
	case "stream-info":
		h.mu.Lock()
		h.streamInfo = append(json.RawMessage(nil), data...)
		for v := range h.viewers {
			select {
			case v.ctrl <- h.streamInfo:
			default:
			}
		}
		h.mu.Unlock()
	}
}

func (h *Hub) broadcastFrame(f []byte) {
	h.mu.Lock()
	defer h.mu.Unlock()

	switch f[0] {
	case frameH264Config:
		h.lastFrame = nil
		for v := range h.viewers {
			if v.h264 && !v.pushOrdered(f) {
				h.overflowLocked(v)
			}
		}
	case frameH264Key:
		for v := range h.viewers {
			if !v.h264 {
				continue
			}
			if v.pushOrdered(f) {
				v.needKey = false
			} else {
				h.overflowLocked(v)
			}
		}
	case frameH264Delta:
		for v := range h.viewers {
			if !v.h264 || v.needKey {
				continue
			}
			if !v.pushOrdered(f) {
				h.overflowLocked(v)
			}
		}
	default: // JPEG (ou formato desconhecido): sempre o mais recente
		h.lastFrame = f
		for v := range h.viewers {
			v.pushLatest(f)
		}
	}
}

// overflowLocked: o viewer ficou para trás. Descarta a fila e espera o próximo quadro-chave.
func (h *Hub) overflowLocked(v *peer) {
	v.drain()
	v.needKey = true
	h.requestKeyFrameLocked()
}

func (h *Hub) requestKeyFrameLocked() {
	if h.source == nil || time.Since(h.lastKeyReq) < 300*time.Millisecond {
		return
	}
	h.lastKeyReq = time.Now()
	h.source.pushCtrl(map[string]any{"type": "keyframe-request"})
}

// notifyViewersLocked informa à fonte quantos viewers existem e se TODOS decodificam H.264.
func (h *Hub) notifyViewersLocked() {
	if h.source == nil {
		return
	}
	all := len(h.viewers) > 0
	for v := range h.viewers {
		if !v.h264 {
			all = false
		}
	}
	h.lastH264 = all
	h.source.pushCtrl(map[string]any{"type": "viewers", "count": len(h.viewers), "h264": all})
}

// ---------- Viewers (iPad, tablet, celular, navegador) ----------

func (h *Hub) handleViewer(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, nil)
	if err != nil {
		return
	}
	c.SetReadLimit(64 << 10)
	ctx, cancel := context.WithCancel(context.Background())
	v := newPeer(c, cancel)
	defer func() {
		cancel()
		c.Close(websocket.StatusNormalClosure, "")
	}()

	h.mu.Lock()
	h.viewers[v] = struct{}{}
	count := len(h.viewers)
	v.pushCtrl(map[string]any{"type": "source", "connected": h.source != nil})
	if h.streamInfo != nil {
		v.ctrl <- h.streamInfo
	}
	if h.lastFrame != nil {
		v.pushLatest(h.lastFrame)
	}
	h.notifyViewersLocked()
	h.mu.Unlock()

	log.Printf("viewer conectado (%s) — total: %d", r.RemoteAddr, count)
	go v.writeLoop(ctx)

	for {
		typ, data, err := c.Read(ctx)
		if err != nil {
			break
		}
		if typ == websocket.MessageText {
			h.handleViewerText(v, data)
		}
	}

	h.mu.Lock()
	delete(h.viewers, v)
	count = len(h.viewers)
	h.notifyViewersLocked()
	h.mu.Unlock()
	log.Printf("viewer desconectado — total: %d", count)
}

func (h *Hub) handleViewerText(v *peer, data []byte) {
	var msg struct {
		Type   string   `json:"type"`
		Codecs []string `json:"codecs"`
		Resolution
	}
	if json.Unmarshal(data, &msg) != nil {
		return
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	// Codecs suportados pelo navegador (no "hello" ou numa mensagem "codecs" de fallback).
	if msg.Codecs != nil {
		supports := false
		for _, c := range msg.Codecs {
			if c == "h264" {
				supports = true
			}
		}
		if supports != v.h264 {
			v.h264 = supports
			v.needKey = true
			h.notifyViewersLocked()
		}
		if supports {
			h.requestKeyFrameLocked()
		}
	}

	switch msg.Type {
	case "keyframe-request":
		v.needKey = true
		h.requestKeyFrameLocked()
	case "hello", "resize":
		if msg.Width < 320 || msg.Height < 320 || msg.Width > 8192 || msg.Height > 8192 {
			return
		}
		res := msg.Resolution
		h.lastViewRes = &res
		if h.source != nil {
			h.source.pushCtrl(map[string]any{"type": "viewer-resolution", "width": res.Width, "height": res.Height, "dpr": res.DPR})
		}
	}
}

// ---------- Utilitários ----------

func (h *Hub) handleInfo(w http.ResponseWriter, _ *http.Request) {
	h.mu.Lock()
	info := map[string]any{
		"version":         version,
		"port":            h.port,
		"urls":            viewerURLs(h.port),
		"sourceConnected": h.source != nil,
		"viewers":         len(h.viewers),
		"h264":            h.lastH264,
	}
	h.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(info)
}

func isLoopback(remoteAddr string) bool {
	host, _, err := net.SplitHostPort(remoteAddr)
	if err != nil {
		return false
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}
