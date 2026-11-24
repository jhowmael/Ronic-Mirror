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
)

// Resolution é o tamanho de tela informado por um viewer (iPad), em pontos CSS.
type Resolution struct {
	Width  int     `json:"width"`
	Height int     `json:"height"`
	DPR    float64 `json:"dpr"`
}

type peer struct {
	conn   *websocket.Conn
	frames chan []byte // capacidade 1: sempre o frame mais recente (latest-frame-wins)
	ctrl   chan []byte // mensagens de texto (JSON)
	cancel context.CancelFunc
}

func newPeer(c *websocket.Conn, cancel context.CancelFunc) *peer {
	return &peer{conn: c, frames: make(chan []byte, 1), ctrl: make(chan []byte, 32), cancel: cancel}
}

// pushFrame substitui qualquer frame pendente pelo novo — um iPad lento nunca acumula atraso.
func (p *peer) pushFrame(f []byte) {
	select {
	case p.frames <- f:
	default:
		select {
		case <-p.frames:
		default:
		}
		select {
		case p.frames <- f:
		default:
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
	default: // fila cheia: descarta mensagem de controle antiga é aceitável
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
	lastFrame   []byte
	streamInfo  json.RawMessage // última mensagem "stream-info" da fonte
	lastViewRes *Resolution
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
	viewerCount := len(h.viewers)
	res := h.lastViewRes
	for v := range h.viewers {
		v.pushCtrl(map[string]any{"type": "source", "connected": true})
	}
	h.mu.Unlock()

	log.Printf("fonte conectada (%s)", r.RemoteAddr)
	go src.writeLoop(ctx)

	src.pushCtrl(map[string]any{"type": "server-info", "version": version, "port": h.port, "urls": viewerURLs(h.port)})
	src.pushCtrl(map[string]any{"type": "viewers", "count": viewerCount})
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
			h.broadcastFrame(data)
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
	h.lastFrame = f
	for v := range h.viewers {
		v.pushFrame(f)
	}
	h.mu.Unlock()
}

// ---------- Viewers (iPads na rede local) ----------

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
		v.pushFrame(h.lastFrame)
	}
	h.notifySourceLocked(map[string]any{"type": "viewers", "count": count})
	h.mu.Unlock()

	log.Printf("iPad conectado (%s) — total: %d", r.RemoteAddr, count)
	go v.writeLoop(ctx)

	for {
		typ, data, err := c.Read(ctx)
		if err != nil {
			break
		}
		if typ == websocket.MessageText {
			h.handleViewerText(data)
		}
	}

	h.mu.Lock()
	delete(h.viewers, v)
	count = len(h.viewers)
	h.notifySourceLocked(map[string]any{"type": "viewers", "count": count})
	h.mu.Unlock()
	log.Printf("iPad desconectado — total: %d", count)
}

func (h *Hub) handleViewerText(data []byte) {
	var msg struct {
		Type string `json:"type"`
		Resolution
	}
	if json.Unmarshal(data, &msg) != nil {
		return
	}
	if msg.Type != "hello" && msg.Type != "resize" {
		return
	}
	if msg.Width < 320 || msg.Height < 320 || msg.Width > 8192 || msg.Height > 8192 {
		return
	}
	res := msg.Resolution
	h.mu.Lock()
	h.lastViewRes = &res
	h.notifySourceLocked(map[string]any{"type": "viewer-resolution", "width": res.Width, "height": res.Height, "dpr": res.DPR})
	h.mu.Unlock()
}

func (h *Hub) notifySourceLocked(v any) {
	if h.source != nil {
		h.source.pushCtrl(v)
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
