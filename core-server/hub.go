package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/http"
	"sort"
	"sync"
	"time"

	"github.com/coder/websocket"
)

const (
	maxFrameBytes = 64 << 20 // 64 MB: limite de leitura de um frame vindo da fonte
	writeTimeout  = 5 * time.Second
	// Fila de vídeo por aparelho: no máximo ~200 ms. Se o Wi-Fi engasgar, em vez de acumular atraso
	// (a imagem "ficar para trás" do mouse), a fila é descartada e o aparelho recebe um quadro-chave novo.
	videoQueueLen = 12
)

// Tipos de mensagem binária (primeiro byte). Ver docs/PROTOCOL.md.
const (
	frameH264Config = 0x10 // avcC (AVCDecoderConfigurationRecord)
	frameH264Key    = 0x11 // quadro-chave: 8 bytes de timestamp (µs, BE) + NALs AVCC
	frameH264Delta  = 0x12 // quadro delta: idem
	frameCursorPos  = 0x20 // posição do cursor (fora do vídeo, para latência mínima)
	frameCursorImg  = 0x21 // imagem do cursor (PNG)
	frameJPEG       = 0xFF // JPEG puro (começa com FF D8)
	// 0xA0 + tamanho (u8) + conn (ASCII) + mensagem: entrega a mensagem SÓ ao aparelho "conn".
	// Cada aparelho tem a sua própria tela virtual; sem esse prefixo, a mensagem vai para todos.
	frameTargeted = 0xA0
)

// Vídeo já é comprimido: compressão do WebSocket só gastaria CPU e somaria atraso.
var acceptOptions = &websocket.AcceptOptions{CompressionMode: websocket.CompressionDisabled}

// Estados de um viewer.
const (
	stateNew     = "new"     // conectou, ainda não mandou "hello"
	statePending = "pending" // aguardando autorização no computador
	stateActive  = "active"  // recebendo a tela
	stateClosed  = "closed"  // recusado/desconectado/bloqueado (encerrando)
)

// Resolution é o tamanho de tela informado por um viewer, em pontos CSS.
type Resolution struct {
	Width  int     `json:"width"`
	Height int     `json:"height"`
	DPR    float64 `json:"dpr"`
}

// Policy é definida pelo app nativo (que guarda as listas) e enviada ao servidor.
type Policy struct {
	RequireApproval bool     `json:"requireApproval"`
	Trusted         []string `json:"trusted"`    // deviceIds que entram sem perguntar
	BlockedIDs      []string `json:"blockedIds"` // deviceIds bloqueados
	BlockedIPs      []string `json:"blockedIps"` // IPs bloqueados
}

type peer struct {
	conn   *websocket.Conn
	frames chan []byte // fila de vídeo
	ctrl   chan []byte // mensagens de texto (JSON)
	cursor chan []byte // posição do cursor (só a mais recente)
	shape  chan []byte // imagem do cursor (só a mais recente)
	cancel context.CancelFunc

	// Somente viewers (protegidos por Hub.mu):
	id       string
	deviceID string
	name     string
	ip       string
	since    time.Time
	state    string
	h264     bool // o navegador decodifica H.264 (WebCodecs)
	needKey  bool // aguardando um quadro-chave antes de receber deltas
	res      *Resolution
	keyReqAt time.Time       // último pedido de quadro-chave feito para este aparelho
	info     json.RawMessage // último "stream-info" da tela deste aparelho
}

func newPeer(c *websocket.Conn, cancel context.CancelFunc) *peer {
	return &peer{
		conn: c, cancel: cancel, needKey: true, state: stateNew, since: time.Now(),
		frames: make(chan []byte, videoQueueLen), ctrl: make(chan []byte, 32),
		cursor: make(chan []byte, 1), shape: make(chan []byte, 1),
	}
}

func drainChan(ch chan []byte) {
	for {
		select {
		case <-ch:
		default:
			return
		}
	}
}

// pushSlot substitui o conteúdo pendente pelo novo (latest-wins).
func pushSlot(ch chan []byte, f []byte) {
	drainChan(ch)
	select {
	case ch <- f:
	default:
	}
}

// pushLatest: JPEG, sempre só o mais recente.
func (p *peer) pushLatest(f []byte) { pushSlot(p.frames, f) }

// pushOrdered enfileira sem descartar (vídeo H.264). Retorna false se a fila estiver cheia.
func (p *peer) pushOrdered(f []byte) bool {
	select {
	case p.frames <- f:
		return true
	default:
		return false
	}
}

func (p *peer) pushCtrl(v any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	p.pushRaw(b)
}

func (p *peer) pushRaw(b []byte) {
	select {
	case p.ctrl <- b:
	default: // fila cheia: descartar mensagem de controle antiga é aceitável
	}
}

// writeLoop serializa as escritas. Prioridade: cursor > controle/vídeo,
// para o ponteiro nunca esperar atrás de um frame grande.
func (p *peer) writeLoop(ctx context.Context) {
	defer p.cancel()
	for {
		var typ websocket.MessageType = websocket.MessageBinary
		var data []byte
		select {
		case data = <-p.shape:
		case data = <-p.cursor:
		default:
			select {
			case <-ctx.Done():
				return
			case data = <-p.shape:
			case data = <-p.cursor:
			case data = <-p.ctrl:
				typ = websocket.MessageText
			case data = <-p.frames:
			}
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
	nextID      int
	lastFrame   []byte          // último JPEG (enviado na hora a quem é aceito)
	streamInfo  json.RawMessage // última mensagem "stream-info" da fonte
	lastCursor  []byte
	lastShape   []byte
	lastViewRes *Resolution
	lastH264    bool

	policyKnown bool // até o app mandar a política, todo aparelho novo aguarda
	policy      Policy
	trusted     map[string]bool
	blockedIDs  map[string]bool
	blockedIPs  map[string]bool
}

func newHub(port int) *Hub {
	return &Hub{
		port: port, viewers: make(map[*peer]struct{}),
		trusted: map[string]bool{}, blockedIDs: map[string]bool{}, blockedIPs: map[string]bool{},
	}
}

// ---------- Fonte (app nativo, somente loopback) ----------

func (h *Hub) handleSource(w http.ResponseWriter, r *http.Request) {
	if !isLoopback(r.RemoteAddr) {
		http.Error(w, "a fonte só pode conectar via 127.0.0.1", http.StatusForbidden)
		return
	}
	c, err := websocket.Accept(w, r, acceptOptions)
	if err != nil {
		return
	}
	c.SetReadLimit(maxFrameBytes)
	ctx, cancel := context.WithCancel(context.Background())
	src := newPeer(c, cancel)
	src.ctrl = make(chan []byte, 1024) // toques (input) não podem se perder: ex. "soltar" depois de arrastar
	defer func() {
		cancel()
		c.Close(websocket.StatusNormalClosure, "")
	}()

	h.mu.Lock()
	if old := h.source; old != nil {
		old.cancel() // só uma fonte por vez: a nova substitui a antiga
	}
	h.source = src
	for v := range h.viewers {
		v.needKey = true
		v.pushCtrl(map[string]any{"type": "source", "connected": true})
	}
	src.pushCtrl(map[string]any{"type": "server-info", "version": version, "port": h.port, "urls": viewerURLs(h.port)})
	h.notifyViewersLocked()
	h.notifyDevicesLocked()
	h.mu.Unlock()

	log.Printf("fonte conectada (%s)", r.RemoteAddr)
	go src.writeLoop(ctx)

	for {
		typ, data, err := c.Read(ctx)
		if err != nil {
			break
		}
		switch typ {
		case websocket.MessageBinary:
			if len(data) > 0 {
				h.broadcastBinary(data)
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
		Type   string `json:"type"`
		Conn   string `json:"conn"`
		Action string `json:"action"`
		Policy
	}
	if json.Unmarshal(data, &msg) != nil {
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()

	switch msg.Type {
	case "stream-info":
		info := append(json.RawMessage(nil), data...)
		if msg.Conn != "" { // informação da tela de um aparelho específico
			if v := h.findLocked(msg.Conn); v != nil {
				v.info = info
				if v.state == stateActive {
					v.pushRaw(info)
				}
			}
			return
		}
		h.streamInfo = info
		for v := range h.viewers {
			if v.state == stateActive {
				v.pushRaw(info)
			}
		}

	case "policy":
		h.policyKnown = true
		h.policy = msg.Policy
		h.trusted = toSet(msg.Trusted)
		h.blockedIDs = toSet(msg.BlockedIDs)
		h.blockedIPs = toSet(msg.BlockedIPs)
		for v := range h.viewers {
			if v.state == statePending || v.state == stateActive {
				h.evaluateLocked(v)
			}
		}
		h.notifyDevicesLocked()

	case "device-action":
		v := h.findLocked(msg.Conn)
		if v == nil {
			return
		}
		switch msg.Action {
		case "accept":
			if v.state == statePending {
				h.activateLocked(v)
			}
		case "reject":
			h.closeViewerLocked(v, "rejected")
		case "kick":
			h.closeViewerLocked(v, "kicked")
		case "block":
			if v.deviceID != "" {
				h.blockedIDs[v.deviceID] = true
			}
			h.blockedIPs[v.ip] = true
			h.closeViewerLocked(v, "blocked")
		}
		h.notifyDevicesLocked()
		h.notifyViewersLocked()
	}
}

func (h *Hub) broadcastBinary(f []byte) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if f[0] == frameTargeted {
		if len(f) < 3 || len(f) < 2+int(f[1])+1 {
			return
		}
		n := int(f[1])
		if v := h.findLocked(string(f[2 : 2+n])); v != nil && v.state == stateActive {
			h.deliverLocked(v, f[2+n:])
		}
		return
	}

	// Sem destino: vale para todos (e fica guardado para quem for aceito depois).
	switch f[0] {
	case frameCursorPos:
		h.lastCursor = f
	case frameCursorImg:
		h.lastShape = f
	case frameH264Config:
		h.lastFrame = nil
	case frameH264Key, frameH264Delta:
	default:
		h.lastFrame = f
	}
	for v := range h.viewers {
		if v.state == stateActive {
			h.deliverLocked(v, f)
		}
	}
}

// deliverLocked entrega uma mensagem binária a um aparelho, respeitando as regras de cada tipo.
func (h *Hub) deliverLocked(v *peer, f []byte) {
	switch f[0] {
	case frameCursorPos:
		pushSlot(v.cursor, f)
	case frameCursorImg:
		pushSlot(v.shape, f)
	case frameH264Config:
		if v.h264 && !v.pushOrdered(f) {
			h.overflowLocked(v)
		}
	case frameH264Key:
		if !v.h264 {
			return
		}
		if v.pushOrdered(f) {
			v.needKey = false
		} else {
			h.overflowLocked(v)
		}
	case frameH264Delta:
		if !v.h264 || v.needKey {
			return
		}
		if !v.pushOrdered(f) {
			h.overflowLocked(v)
		}
	default: // JPEG: sempre o mais recente
		v.pushLatest(f)
	}
}

// overflowLocked: o viewer ficou para trás. Descarta a fila e espera o próximo quadro-chave.
func (h *Hub) overflowLocked(v *peer) {
	drainChan(v.frames)
	v.needKey = true
	h.requestKeyFrameLocked(v)
}

// requestKeyFrameLocked pede à fonte um quadro-chave para a tela deste aparelho.
func (h *Hub) requestKeyFrameLocked(v *peer) {
	if h.source == nil || time.Since(v.keyReqAt) < 300*time.Millisecond {
		return
	}
	v.keyReqAt = time.Now()
	h.source.pushCtrl(map[string]any{"type": "keyframe-request", "conn": v.id})
}

// ---------- Autorização ----------

func (h *Hub) isBlockedLocked(v *peer) bool {
	return (v.deviceID != "" && h.blockedIDs[v.deviceID]) || h.blockedIPs[v.ip]
}

// evaluateLocked decide o destino de um viewer conforme a política atual.
func (h *Hub) evaluateLocked(v *peer) {
	if h.isBlockedLocked(v) {
		h.closeViewerLocked(v, "blocked")
		return
	}
	if v.state == stateActive {
		return // quem já foi aceito continua (mudar a política não derruba ninguém)
	}
	if h.policyKnown && (!h.policy.RequireApproval || (v.deviceID != "" && h.trusted[v.deviceID])) {
		h.activateLocked(v)
		return
	}
	if v.state != statePending {
		v.state = statePending
		v.pushCtrl(map[string]any{"type": "auth", "state": "pending"})
		log.Printf("aparelho aguardando autorização: %s (%s)", v.name, v.ip)
	}
}

func (h *Hub) activateLocked(v *peer) {
	v.state = stateActive
	v.needKey = true
	v.pushCtrl(map[string]any{"type": "auth", "state": "accepted"})
	if v.info != nil {
		v.pushRaw(v.info)
	} else if h.streamInfo != nil {
		v.pushRaw(h.streamInfo)
	}
	if h.lastFrame != nil {
		v.pushLatest(h.lastFrame)
	}
	if h.lastShape != nil {
		pushSlot(v.shape, h.lastShape)
	}
	if h.lastCursor != nil {
		pushSlot(v.cursor, h.lastCursor)
	}
	if v.res != nil {
		h.lastViewRes = v.res
		if h.source != nil {
			h.source.pushCtrl(map[string]any{"type": "viewer-resolution", "conn": v.id, "width": v.res.Width, "height": v.res.Height, "dpr": v.res.DPR})
		}
	}
	v.keyReqAt = time.Time{} // o aparelho novo precisa de um quadro-chave já
	h.requestKeyFrameLocked(v)
	h.notifyViewersLocked()
	h.notifyDevicesLocked()
	log.Printf("aparelho conectado: %s (%s)", v.name, v.ip)
}

// closeViewerLocked avisa o aparelho do motivo e encerra a conexão logo depois.
func (h *Hub) closeViewerLocked(v *peer, reason string) {
	if v.state == stateClosed {
		return
	}
	v.state = stateClosed
	delete(h.viewers, v)
	drainChan(v.frames)
	v.pushCtrl(map[string]any{"type": "auth", "state": reason})
	go func() {
		time.Sleep(500 * time.Millisecond) // tempo para a mensagem chegar
		v.cancel()
	}()
	log.Printf("aparelho %s: %s (%s)", reason, v.name, v.ip)
	h.notifyViewersLocked()
	h.notifyDevicesLocked()
}

func (h *Hub) findLocked(id string) *peer {
	for v := range h.viewers {
		if v.id == id {
			return v
		}
	}
	return nil
}

// notifyViewersLocked informa à fonte quantos aparelhos estão ATIVOS e se todos decodificam H.264.
func (h *Hub) notifyViewersLocked() {
	if h.source == nil {
		return
	}
	count := 0
	all := true
	for v := range h.viewers {
		if v.state != stateActive {
			continue
		}
		count++
		if !v.h264 {
			all = false
		}
	}
	h.lastH264 = count > 0 && all
	h.source.pushCtrl(map[string]any{"type": "viewers", "count": count, "h264": h.lastH264})
}

type deviceInfo struct {
	Conn     string `json:"conn"`
	DeviceID string `json:"deviceId"`
	Name     string `json:"name"`
	IP       string `json:"ip"`
	State    string `json:"state"`
	Since    int64  `json:"since"` // ms desde 1970
	Codec    string `json:"codec"`
	Width    int    `json:"width"`
	Height   int    `json:"height"`
}

// notifyDevicesLocked manda à fonte a lista de aparelhos (aguardando e conectados).
func (h *Hub) notifyDevicesLocked() {
	if h.source == nil {
		return
	}
	list := make([]deviceInfo, 0, len(h.viewers))
	for v := range h.viewers {
		if v.state != statePending && v.state != stateActive {
			continue
		}
		d := deviceInfo{Conn: v.id, DeviceID: v.deviceID, Name: v.name, IP: v.ip, State: v.state, Since: v.since.UnixMilli(), Codec: "jpeg"}
		if v.h264 {
			d.Codec = "h264"
		}
		if v.res != nil {
			d.Width, d.Height = v.res.Width, v.res.Height
		}
		list = append(list, d)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].Since < list[j].Since })
	h.source.pushCtrl(map[string]any{"type": "devices", "devices": list})
}

// ---------- Viewers (iPad, tablet, celular, navegador) ----------

func (h *Hub) handleViewer(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, acceptOptions)
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
	v.ip, _, _ = net.SplitHostPort(r.RemoteAddr)

	h.mu.Lock()
	h.nextID++
	v.id = fmt.Sprintf("d%d", h.nextID)
	v.name = "Aparelho"
	h.viewers[v] = struct{}{}
	// A página compara a versão: se o app foi atualizado, ela se recarrega sozinha.
	v.pushCtrl(map[string]any{"type": "server", "version": version})
	v.pushCtrl(map[string]any{"type": "source", "connected": h.source != nil})
	if h.blockedIPs[v.ip] {
		h.closeViewerLocked(v, "blocked")
	}
	h.mu.Unlock()

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
	if _, ok := h.viewers[v]; ok {
		delete(h.viewers, v)
		if v.state == stateActive || v.state == statePending {
			log.Printf("aparelho saiu: %s (%s)", v.name, v.ip)
		}
		h.notifyViewersLocked()
		h.notifyDevicesLocked()
	}
	h.mu.Unlock()
}

func (h *Hub) handleViewerText(v *peer, data []byte) {
	var msg struct {
		Type       string   `json:"type"`
		Codecs     []string `json:"codecs"`
		DeviceID   string   `json:"deviceId"`
		DeviceName string   `json:"deviceName"`
		Resolution
	}
	if json.Unmarshal(data, &msg) != nil {
		return
	}

	h.mu.Lock()
	defer h.mu.Unlock()
	if v.state == stateClosed {
		return
	}

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
			if v.state == stateActive {
				h.notifyViewersLocked()
				h.notifyDevicesLocked()
			}
		}
		if supports && v.state == stateActive {
			h.requestKeyFrameLocked(v)
		}
	}

	validRes := msg.Width >= 320 && msg.Height >= 320 && msg.Width <= 8192 && msg.Height <= 8192
	switch msg.Type {
	case "hello":
		v.deviceID = clip(msg.DeviceID, 64)
		if n := clip(msg.DeviceName, 60); n != "" {
			v.name = n
		}
		if validRes {
			res := msg.Resolution
			v.res = &res
		}
		h.evaluateLocked(v)
		h.notifyDevicesLocked()
	case "resize":
		if !validRes {
			return
		}
		res := msg.Resolution
		v.res = &res
		if v.state == stateActive {
			h.lastViewRes = &res
			if h.source != nil {
				h.source.pushCtrl(map[string]any{"type": "viewer-resolution", "conn": v.id, "width": res.Width, "height": res.Height, "dpr": res.DPR})
			}
			h.notifyDevicesLocked()
		}
	case "input":
		// Toque no aparelho → mouse no computador (só de aparelhos aceitos).
		if v.state != stateActive || h.source == nil {
			return
		}
		var in map[string]any
		if json.Unmarshal(data, &in) != nil {
			return
		}
		kind, _ := in["kind"].(string)
		switch kind {
		case "tap", "rtap", "scroll", "down", "move", "up":
		default:
			return
		}
		out := map[string]any{"type": "input", "conn": v.id, "kind": kind}
		for _, k := range []string{"x", "y", "dx", "dy"} {
			if f, ok := in[k].(float64); ok {
				out[k] = f
			}
		}
		h.source.pushCtrl(out)
	case "keyframe-request":
		if v.state == stateActive {
			v.needKey = true
			h.requestKeyFrameLocked(v)
		}
	}
}

// ---------- Utilitários ----------

func (h *Hub) handleInfo(w http.ResponseWriter, _ *http.Request) {
	h.mu.Lock()
	active, pending := 0, 0
	for v := range h.viewers {
		switch v.state {
		case stateActive:
			active++
		case statePending:
			pending++
		}
	}
	info := map[string]any{
		"version":         version,
		"port":            h.port,
		"urls":            viewerURLs(h.port),
		"sourceConnected": h.source != nil,
		"viewers":         active,
		"pending":         pending,
		"h264":            h.lastH264,
	}
	h.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(info)
}

func toSet(list []string) map[string]bool {
	m := make(map[string]bool, len(list))
	for _, s := range list {
		if s != "" {
			m[s] = true
		}
	}
	return m
}

func clip(s string, n int) string {
	r := []rune(s)
	if len(r) > n {
		return string(r[:n])
	}
	return s
}

func isLoopback(remoteAddr string) bool {
	host, _, err := net.SplitHostPort(remoteAddr)
	if err != nil {
		return false
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}
