#!/usr/bin/env bash
set -euo pipefail
PROPS="$(cd "$(dirname "$0")/.." && pwd)/ext/Lidarr/src/Directory.Build.props"
python3 - "$PROPS" <<'PY2'
from pathlib import Path
import sys
p = Path(sys.argv[1])
text = p.read_text()
needle = '<AssemblyVersion>10.0.0.*</AssemblyVersion>'
replacement = '<AssemblyVersion>3.0.0.*</AssemblyVersion>'
if replacement in text:
    print('AssemblyVersion already set to 3.0.0.*')
elif needle in text:
    p.write_text(text.replace(needle, replacement, 1))
    print('Patched Lidarr AssemblyVersion to 3.0.0.*')
else:
    raise SystemExit('Expected AssemblyVersion marker not found in ' + str(p))
PY2
