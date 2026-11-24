#!/usr/bin/env bash
# Empacota a Ronic Mirror para macOS:
#   dist/RonicMirror.app
#   dist/RonicMirror-macOS.zip   (pronto para anexar no GitHub Releases)
#
# Variáveis opcionais:
#   VERSION=0.2.0            versão gravada no Info.plist e no nome do .zip
#   UNIVERSAL=1              gera binários universais (arm64 + x86_64) — requer Xcode completo
#   SIGN_IDENTITY="Developer ID Application: Fulano (TEAMID)"   assinatura real
#
# Assinatura padrão:
#   - na sua máquina: certificado local estável (scripts/dev-signing.sh), assim a permissão de
#     Gravação de Tela continua valendo depois de recompilar;
#   - no CI (variável CI definida): ad-hoc ("-").
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${VERSION:-0.1.0}"
if [[ -z "${SIGN_IDENTITY:-}" ]]; then
  if [[ -n "${CI:-}" ]]; then
    SIGN_IDENTITY="-"
  else
    SIGN_IDENTITY="$("$ROOT/scripts/dev-signing.sh" 2>/dev/null || true)"
    [[ -n "$SIGN_IDENTITY" ]] || SIGN_IDENTITY="-"
  fi
fi
BUILD="$ROOT/build/macos"
DIST="$ROOT/dist"
APP="$DIST/RonicMirror.app"

echo "==> Ronic Mirror $VERSION (macOS)"
rm -rf "$BUILD" "$APP"
mkdir -p "$BUILD" "$DIST"

# 1) core-server (Go)
echo "==> Compilando core-server"
if [[ "${UNIVERSAL:-0}" == "1" ]]; then
  for arch in arm64 amd64; do
    (cd "$ROOT/core-server" && GOOS=darwin GOARCH=$arch CGO_ENABLED=0 \
      go build -trimpath -ldflags "-s -w" -o "$BUILD/core-server-$arch" .)
  done
  lipo -create -output "$BUILD/core-server" "$BUILD/core-server-arm64" "$BUILD/core-server-amd64"
else
  (cd "$ROOT/core-server" && CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o "$BUILD/core-server" .)
fi

# 2) App Swift
echo "==> Compilando app Swift"
SWIFT_ARGS=(-c release --package-path "$ROOT/macos")
if [[ "${UNIVERSAL:-0}" == "1" ]]; then
  SWIFT_ARGS+=(--arch arm64 --arch x86_64)
fi
swift build "${SWIFT_ARGS[@]}"
BIN_DIR="$(swift build "${SWIFT_ARGS[@]}" --show-bin-path)"

# 3) Monta o bundle .app
echo "==> Montando $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/RonicMirror" "$APP/Contents/MacOS/RonicMirror"
cp "$BUILD/core-server" "$APP/Contents/MacOS/core-server"
cp "$ROOT/macos/Resources/Info.plist" "$APP/Contents/Info.plist"
cp -R "$ROOT/shared-web" "$APP/Contents/Resources/web"
cp "$ROOT/macos/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$(date +%Y%m%d%H%M)" "$APP/Contents/Info.plist"

# 4) Assinatura (ad-hoc por padrão; o helper é assinado antes do app)
echo "==> Assinando ($SIGN_IDENTITY)"
SIGN_FLAGS=(--force --sign "$SIGN_IDENTITY")
if [[ "$SIGN_IDENTITY" == Developer\ ID* ]]; then
  SIGN_FLAGS+=(--options runtime --timestamp)
fi
codesign "${SIGN_FLAGS[@]}" "$APP/Contents/MacOS/core-server"
codesign "${SIGN_FLAGS[@]}" "$APP"
codesign --verify --deep --strict "$APP"

# 5) Zip para o GitHub Releases (ditto preserva assinatura e atributos)
ZIP="$DIST/RonicMirror-macOS.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

echo
echo "Pronto:"
echo "  App: $APP"
echo "  Zip: $ZIP"
echo "Abra com: open \"$APP\""
