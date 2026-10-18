#!/usr/bin/env bash
# Fetches mongod/mongos for the conformance suite (docs/spec/06-testing.md §1) without Docker.
# Source: the conda-forge `mongodb` package (official server source, built by conda-forge).
# Result: tools/mongod/.cache/<version>/bin/{mongod,mongos}; prints the bin directory.
#   ./fetch.sh            # default version
#   ./fetch.sh 7.0.34     # another version available on conda-forge
set -euo pipefail
VERSION=${1:-8.0.23}
HERE=$(cd "$(dirname "$0")" && pwd)
DEST="$HERE/.cache/$VERSION"
if [[ -x "$DEST/bin/mongod" ]]; then echo "$DEST/bin"; exit 0; fi
mkdir -p "$DEST"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
python3 - "$VERSION" "$TMP" <<'PY'
import json, sys, urllib.request
version, tmp = sys.argv[1], sys.argv[2]
repo = json.load(urllib.request.urlopen("https://conda.anaconda.org/conda-forge/linux-64/repodata.json"))
cands = sorted((k for k in repo.get("packages.conda", {}) if k.startswith(f"mongodb-{version}-")),
               key=lambda k: repo["packages.conda"][k].get("build_number", 0))
if not cands:
    sys.exit(f"mongodb {version} not found on conda-forge")
name = cands[-1]
urllib.request.urlretrieve(f"https://conda.anaconda.org/conda-forge/linux-64/{name}", f"{tmp}/pkg.conda")
print(name, file=sys.stderr)
PY
(cd "$TMP" && unzip -q pkg.conda)
python3 -c "import zstandard" 2>/dev/null || pip install -q zstandard >&2
python3 - "$TMP" "$DEST" <<'PY'
import glob, sys, tarfile, zstandard
tmp, dest = sys.argv[1], sys.argv[2]
with open(glob.glob(f"{tmp}/pkg-*.tar.zst")[0], "rb") as f:
    with tarfile.open(fileobj=zstandard.ZstdDecompressor().stream_reader(f), mode="r|") as t:
        t.extractall(dest, filter="data")
PY
"$DEST/bin/mongod" --version | head -1 >&2
echo "$DEST/bin"
