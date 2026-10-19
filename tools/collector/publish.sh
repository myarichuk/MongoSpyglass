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
else
  echo "tailwindcss not installed (npm install at the repo root); using the committed wwwroot/css/app.css" >&2
fi
rm -rf "$out" "$out.ui"
dotnet publish "$ui" -c Release -o "$out.ui" -nologo -v q
dotnet publish "$root/src/Spyglass.Collector" -c Release -o "$out" -nologo -v q
cp -r "$out.ui/wwwroot" "$out/wwwroot"
rm -rf "$out.ui"
echo "published to $out (run: dotnet $out/spyglass-collector.dll)"
