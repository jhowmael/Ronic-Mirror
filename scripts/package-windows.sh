#!/usr/bin/env bash
# Empacota o Ronic Mirror para Windows (x64). Roda no macOS, Linux ou Windows (Git Bash):
#   dist/RonicMirror-Windows/           RonicMirror.exe + core-server.exe + web\
#   dist/RonicMirror-Windows.zip        pronto para o GitHub Releases
#
# Requer: Go 1.22+ e .NET SDK 10+.   Variável opcional: VERSION=0.2.0
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${VERSION:-0.1.0}"
OUT="$ROOT/dist/RonicMirror-Windows"
ZIP="$ROOT/dist/RonicMirror-Windows.zip"

echo "==> Ronic Mirror $VERSION (Windows x64)"
rm -rf "$OUT" "$ZIP"
mkdir -p "$OUT"

echo "==> Compilando core-server.exe"
(cd "$ROOT/core-server" && GOOS=windows GOARCH=amd64 CGO_ENABLED=0 \
  go build -trimpath -ldflags "-s -w" -o "$OUT/core-server.exe" .)

echo "==> Compilando RonicMirror.exe"
dotnet publish "$ROOT/windows/RonicMirror/RonicMirror.csproj" -c Release \
  -p:Version="$VERSION" -o "$ROOT/build/windows-publish" --nologo -v quiet
cp "$ROOT/build/windows-publish/RonicMirror.exe" "$OUT/RonicMirror.exe"

echo "==> Copiando cliente web"
cp -R "$ROOT/shared-web" "$OUT/web"

cat > "$OUT/LEIA-ME.txt" <<TXT
Ronic Mirror $VERSION para Windows

1. Abra RonicMirror.exe.
2. Na primeira vez, clique em "Instalar driver" (monitor virtual Parsec VDD) e confirme o pedido de administrador.
3. Se o Firewall do Windows perguntar, permita o "core-server" em redes privadas.
4. No iPad, abra no Safari o link mostrado na janela (ou leia o QR code).

Ao fechar a janela, o app continua na bandeja do sistema. Para encerrar: botão direito no ícone > Sair.
Mais informações: https://github.com/jhowmael/Ronic---Mirror
TXT

(cd "$ROOT/dist" && rm -f "$ZIP" && \
  if command -v zip >/dev/null; then zip -qr "$ZIP" "RonicMirror-Windows"; \
  else powershell -NoProfile -Command "Compress-Archive -Path 'RonicMirror-Windows' -DestinationPath 'RonicMirror-Windows.zip'"; fi)

echo
echo "Pronto: $ZIP"
