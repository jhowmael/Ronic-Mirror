// core-server: servidor local leve e multiplataforma da Ronic Mirror.
//
// Responsabilidades:
//   - Detectar o IP local da máquina na rede Wi-Fi/LAN.
//   - Servir o cliente web (/shared-web) que o iPad abre no Safari.
//   - Aceitar UMA "fonte" (o app nativo macOS/Windows) em ws://127.0.0.1:<porta>/source
//     que envia frames JPEG da tela virtual.
//   - Retransmitir os frames para todos os "viewers" (iPads) em ws://<ip>:<porta>/view.
//
// O contrato completo está em docs/PROTOCOL.md.
package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

const version = "0.3.0"

func main() {
	port := flag.Int("port", 3000, "porta HTTP preferida (tenta as 10 seguintes se estiver ocupada)")
	webDir := flag.String("web", "", "pasta com os arquivos do cliente web (padrão: detecta automaticamente)")
	exitOnStdinClose := flag.Bool("exit-on-stdin-close", false, "encerra quando o stdin fechar (o app nativo morreu)")
	flag.Parse()

	log.SetOutput(os.Stderr) // stdout é reservado para eventos JSON (contrato com o app nativo)
	log.SetFlags(log.Ltime)

	dir, err := resolveWebDir(*webDir)
	if err != nil {
		fatal(err)
	}

	ln, actualPort, err := listen(*port, 10)
	if err != nil {
		fatal(err)
	}

	hub := newHub(actualPort)
	mux := http.NewServeMux()
	mux.HandleFunc("/source", hub.handleSource)
	mux.HandleFunc("/view", hub.handleViewer)
	mux.HandleFunc("/api/info", hub.handleInfo)
	mux.Handle("/", noCache(http.FileServer(http.Dir(dir))))

	if *exitOnStdinClose {
		go func() {
			_, _ = io.Copy(io.Discard, os.Stdin)
			log.Println("stdin fechado — encerrando")
			os.Exit(0)
		}()
	}

	urls := viewerURLs(actualPort)
	emit(map[string]any{"event": "ready", "version": version, "port": actualPort, "urls": urls, "web": dir})
	log.Printf("core-server %s pronto na porta %d (web: %s)", version, actualPort, dir)
	for _, u := range urls {
		log.Printf("  abra no iPad: %s", u)
	}

	srv := &http.Server{Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	if err := srv.Serve(ln); err != nil && !errors.Is(err, http.ErrServerClosed) {
		fatal(err)
	}
}

// listen tenta a porta preferida e as próximas `tries` portas.
func listen(preferred, tries int) (net.Listener, int, error) {
	var lastErr error
	for p := preferred; p <= preferred+tries; p++ {
		ln, err := net.Listen("tcp", fmt.Sprintf(":%d", p))
		if err == nil {
			return ln, p, nil
		}
		lastErr = err
	}
	return nil, 0, fmt.Errorf("nenhuma porta livre entre %d e %d: %w", preferred, preferred+tries, lastErr)
}

// resolveWebDir encontra a pasta do cliente web, tanto em desenvolvimento
// (repositório) quanto empacotado (.app / pasta do .exe).
func resolveWebDir(explicit string) (string, error) {
	var candidates []string
	if explicit != "" {
		candidates = append(candidates, explicit)
	}
	if exe, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exe)
		candidates = append(candidates,
			filepath.Join(exeDir, "web"),                    // Windows / genérico: web ao lado do binário
			filepath.Join(exeDir, "..", "Resources", "web"), // macOS .app: Contents/MacOS -> Contents/Resources/web
			filepath.Join(exeDir, "..", "..", "shared-web"), // repo: core-server/bin -> shared-web
		)
	}
	if cwd, err := os.Getwd(); err == nil {
		candidates = append(candidates, filepath.Join(cwd, "shared-web"), filepath.Join(cwd, "..", "shared-web"))
	}
	for _, c := range candidates {
		if st, err := os.Stat(filepath.Join(c, "index.html")); err == nil && !st.IsDir() {
			abs, _ := filepath.Abs(c)
			return abs, nil
		}
	}
	return "", fmt.Errorf("pasta do cliente web não encontrada (use -web). Procurado em: %v", candidates)
}

func noCache(h http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-cache")
		h.ServeHTTP(w, r)
	})
}

// emit escreve um evento JSON por linha no stdout (lido pelo app nativo).
func emit(v any) {
	b, _ := json.Marshal(v)
	fmt.Fprintln(os.Stdout, string(b))
}

func fatal(err error) {
	emit(map[string]any{"event": "error", "message": err.Error()})
	log.Printf("erro fatal: %v", err)
	os.Exit(1)
}
