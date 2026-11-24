.PHONY: help server run-server mac-dev app universal windows clean

help:
	@echo "make server      - compila o core-server (Go) em core-server/bin/"
	@echo "make run-server  - roda só o servidor (útil para testar o cliente web)"
	@echo "make mac-dev     - roda o app macOS via 'swift run' (desenvolvimento)"
	@echo "make app         - gera dist/RonicMirror.app e o .zip para o GitHub Releases"
	@echo "make universal   - igual a 'make app', binário universal (requer Xcode completo)"
	@echo "make windows     - gera dist/RonicMirror-Windows.zip (compila daqui mesmo, sem precisar de Windows)"
	@echo "make clean       - remove artefatos de build"

server:
	cd core-server && go build -o bin/core-server .

run-server: server
	./core-server/bin/core-server --web shared-web

mac-dev: server
	cd macos && swift run RonicMirror

app:
	./scripts/package-macos.sh

universal:
	UNIVERSAL=1 ./scripts/package-macos.sh

clean:
	rm -rf build dist core-server/bin macos/.build windows/RonicMirror/bin windows/RonicMirror/obj

windows:
	./scripts/package-windows.sh
