#!/usr/bin/env bash
# Reproduces the findings in docs/review/2026-10-17-architecture-review.md.
#   ./run.sh unit          NRules / BSON / zlib / PipeWriter repros (no server needed)
#   ./run.sh e2e [port]    real MongoDB driver -> (direct | proxy bare | proxy + Service listeners) -> Mongo.Fakes
set -euo pipefail
cd "$(dirname "$0")"
FIX=$(pwd)/obj/fixtures
mkdir -p "$FIX/app"
python3 - "$FIX/app" <<'PY'
import json, sys, os
d = sys.argv[1]
with open(os.path.join(d, "users.json"), "w") as f:
    for i in range(200): f.write(json.dumps({"_id": i, "name": f"user{i}", "status": "A" if i % 2 else "B", "age": 20 + i % 50}) + "\n")
with open(os.path.join(d, "big.json"), "w") as f:
    for i in range(1000): f.write(json.dumps({"_id": i, "payload": "x" * 4000}) + "\n")
PY
dotnet build -c Release Harness/Harness.csproj -v q -nologo | grep -E " error " || true
if [[ "${1:-unit}" == "unit" ]]; then
  dotnet Harness/bin/Release/net10.0/Harness.dll unit
  exit 0
fi
PORT=${2:-37017}
dotnet build -c Release FakeHost/FakeHost.csproj -v q -nologo | grep -E " error " || true
dotnet FakeHost/bin/Release/net10.0/FakeHost.dll "$FIX" "$PORT" > obj/fake.log 2>&1 &
FAKE=$!
trap 'kill $FAKE 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do grep -q READY obj/fake.log && break; sleep 0.5; done
for v in direct bare full; do dotnet Harness/bin/Release/net10.0/Harness.dll e2e "$PORT" "$v"; echo; done
