#!/usr/bin/env bash
# Gera todos os ícones da Ronic Mirror a partir de branding/make-icons.swift.
# Rode no macOS:  ./branding/make-icons.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/branding/out"
rm -rf "$OUT"
swift "$ROOT/branding/make-icons.swift" "$OUT"

# README / site
cp "$OUT/logo-512.png" "$ROOT/branding/logo.png"

# iPad (ícone da Tela de Início + favicon)
cp "$OUT/logo-180.png" "$ROOT/shared-web/apple-touch-icon.png"
cp "$OUT/logo-64.png"  "$ROOT/shared-web/favicon.png"
cp "$OUT/logo-256.png" "$ROOT/shared-web/logo.png"

# macOS (.icns)
ICONSET="$ROOT/build/RonicMirror.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  cp "$OUT/logo-$s.png" "$ICONSET/icon_${s}x${s}.png"
  d=$((s * 2)); cp "$OUT/logo-$d.png" "$ICONSET/icon_${s}x${s}@2x.png"
done
iconutil -c icns "$ICONSET" -o "$ROOT/macos/Resources/AppIcon.icns"

# Windows (.ico com PNGs embutidos) + logo para a janela
python3 - "$OUT" "$ROOT/windows/RonicMirror/Assets/ronic.ico" <<'PY'
import struct, sys, os
out, dest = sys.argv[1], sys.argv[2]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
blobs = [open(os.path.join(out, f"logo-{s}.png"), "rb").read() for s in sizes]
header = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries = b""
for s, b in zip(sizes, blobs):
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(b), offset)
    offset += len(b)
os.makedirs(os.path.dirname(dest), exist_ok=True)
open(dest, "wb").write(header + entries + b"".join(blobs))
PY
cp "$OUT/logo-128.png" "$ROOT/windows/RonicMirror/Assets/logo.png"
echo "OK"
