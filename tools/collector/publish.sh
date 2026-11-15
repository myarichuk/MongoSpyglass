#!/usr/bin/env bash
# Builds spyglass-collector with its UI: CSS (Tailwind), the Blazor WASM SPA, the server, then the SPA
# copied into the server's wwwroot (03 §10: one container, UI served from /).
#   tools/collector/publish.sh [out-dir]      (default: artifacts/collector)
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-$root/artifacts/collector}"
ui="$root/src/Spyglass.Collector.Ui"

if [ -x "$root/node_modules/.bin/tailwindcss" ]; then
  (cd "$ui" && "$root/node_modules/.bin/tailwindcss" -c tailwind.config.js -i wwwroot/css/input.css -o wwwroot/css/app.css --minify)
elif [ "${SPYGLASS_SKIP_CSS:-}" = 1 ]; then
  echo "warning: SPYGLASS_SKIP_CSS=1: shipping the committed wwwroot/css/app.css without rebuilding it" >&2
else
  # Refuse rather than silently ship a stale app.css: Razor classes added since the last build would be unstyled.
  echo "error: tailwindcss is not installed (needs node/npm and 'npm install' at the repo root)." >&2
  echo "       Without it the committed wwwroot/css/app.css would be shipped. Install Node.js and run 'npm install'" >&2
  echo "       at the repo root, or set SPYGLASS_SKIP_CSS=1 to ship the committed CSS anyway." >&2
  exit 1
fi
rm -rf "$out" "$out.ui"
dotnet publish "$ui" -c Release -o "$out.ui" -nologo -v q
dotnet publish "$root/src/Spyglass.Collector" -c Release -o "$out" -nologo -v q
cp -r "$out.ui/wwwroot" "$out/wwwroot"
rm -rf "$out.ui"
echo "published to $out (run: dotnet $out/spyglass-collector.dll)"
