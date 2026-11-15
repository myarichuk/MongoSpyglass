#!/usr/bin/env bash
# Fetches mongod/mongos for the conformance suite (docs/spec/06-testing.md §1) without Docker.
# Source: the conda-forge `mongodb` package (official server source, built by conda-forge).
# Result: tools/mongod/.cache/<version>/bin/{mongod,mongos}; prints the bin directory.
#   ./fetch.sh            # default version
#   ./fetch.sh 7.0.34     # another version available on conda-forge
# Cache: .cache/repodata.json is downloaded once and reused; the package's sha256 (from repodata) is verified.
# Needs python3 and the `zstandard` module (installed into .cache/venv if the system python lacks it).
set -euo pipefail
VERSION=${1:-8.0.23}
HERE=$(cd "$(dirname "$0")" && pwd)
CACHE="$HERE/.cache"
DEST="$CACHE/$VERSION"
if [[ -x "$DEST/bin/mongod" ]]; then echo "$DEST/bin"; exit 0; fi

command -v python3 >/dev/null || { echo "fetch.sh: python3 is required" >&2; exit 1; }
mkdir -p "$CACHE"
PY=python3
if ! python3 -c "import zstandard" 2>/dev/null; then
  # PEP 668 ("externally-managed-environment", Debian 12+/Ubuntu 24.04): never pip-install into the system
  # python; use a venv that lives in the cache dir instead.
  VENV="$CACHE/venv"
  if [[ ! -x "$VENV/bin/python" ]]; then
    python3 -m venv "$VENV" >&2 || {
      echo "fetch.sh: cannot create a venv at $VENV (Debian/Ubuntu: apt install python3-venv)" >&2; exit 1; }
  fi
  "$VENV/bin/python" -c "import zstandard" 2>/dev/null ||
    "$VENV/bin/python" -m pip install -q zstandard >&2 ||
    { echo "fetch.sh: pip install zstandard into $VENV failed" >&2; exit 1; }
  PY="$VENV/bin/python"
fi

mkdir -p "$DEST"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
# The .conda package is a zip (extracted with python's zipfile, so no `unzip` is needed) holding pkg-*.tar.zst.
"$PY" - "$VERSION" "$CACHE" "$TMP" "$DEST" <<'PY'
import glob, hashlib, json, os, sys, tarfile, urllib.request, zipfile
import zstandard

version, cache, tmp, dest = sys.argv[1:5]
BASE = "https://conda.anaconda.org/conda-forge/linux-64"

repo_path = os.path.join(cache, "repodata.json")
if os.path.exists(repo_path):
    print("using cached repodata.json", file=sys.stderr)
else:
    print("downloading repodata.json (large, once)", file=sys.stderr)
    part = repo_path + ".part"
    urllib.request.urlretrieve(f"{BASE}/repodata.json", part)
    os.replace(part, repo_path)
with open(repo_path) as f:
    repo = json.load(f)

packages = repo.get("packages.conda", {})
cands = sorted((k for k in packages if k.startswith(f"mongodb-{version}-")),
               key=lambda k: packages[k].get("build_number", 0))
if not cands:
    sys.exit(f"mongodb {version} not found on conda-forge")
name = cands[-1]
info = packages[name]
expected = info.get("sha256")
if not expected:
    sys.exit(f"{name}: no sha256 in repodata, refusing to install an unverified binary")
print(name, file=sys.stderr)

pkg = os.path.join(tmp, "pkg.conda")
urllib.request.urlretrieve(f"{BASE}/{name}", pkg)
digest = hashlib.sha256()
with open(pkg, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        digest.update(chunk)
if digest.hexdigest() != expected:
    sys.exit(f"{name}: sha256 mismatch (repodata {expected}, download {digest.hexdigest()})")

with zipfile.ZipFile(pkg) as z:
    members = [n for n in z.namelist() if "/" not in n and n.startswith("pkg-") and n.endswith(".tar.zst")]
    if len(members) != 1:
        sys.exit(f"{name}: expected one pkg-*.tar.zst inside the package, found {members}")
    z.extract(members[0], tmp)
with open(os.path.join(tmp, members[0]), "rb") as f:
    with tarfile.open(fileobj=zstandard.ZstdDecompressor().stream_reader(f), mode="r|") as t:
        t.extractall(dest, filter="data")
PY

"$DEST/bin/mongod" --version | head -1 >&2
echo "$DEST/bin"
