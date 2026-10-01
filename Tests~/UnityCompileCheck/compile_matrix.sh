#!/usr/bin/env bash
# Compiles the package against the real managed assemblies of several Unity versions, without installing Unity.
# For each version it streams the official Linux editor archive, extracts only Editor/Data/Managed/UnityEngine/*.dll
# (about 200 MB on disk, the 4 GB archive is never stored) and builds UnityCompileCheck.csproj with the
# version symbols Unity would define. Warnings are errors, so an obsolete API fails the build.
#
# Usage: ./compile_matrix.sh [version-prefix ...]      e.g. ./compile_matrix.sh 2021.3 2022.3 6000.0 6000.6
# Needs: curl, xz, tar, python3, dotnet 8 SDK.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CACHE="${UNITY_DLL_CACHE:-${TMPDIR:-/tmp}/unity-managed}"
DOTNET="${DOTNET:-dotnet}"
VERSIONS=("$@")
[ ${#VERSIONS[@]} -eq 0 ] && VERSIONS=(2021.3 2022.3 6000.0 6000.6)

# Every UNITY_X_Y_OR_NEWER symbol up to the given version (only the ones this package checks matter).
defines_for() {
  python3 - "$1" <<'EOF'
import sys
v = tuple(int(x) for x in sys.argv[1].split('.')[:2])
known = [(2021,1),(2021,2),(2021,3),(2022,1),(2022,2),(2022,3),(2023,1),(2023,2),(6000,0),(6000,1),(6000,2),(6000,3),(6000,4),(6000,5),(6000,6),(6000,7),(6000,8),(6000,9)]
print('%3B'.join(f'UNITY_{a}_{b}_OR_NEWER' for a, b in known if (a, b) <= v))
EOF
}

failed=0
for prefix in "${VERSIONS[@]}"; do
  read -r version url < <(curl -sS "https://services.api.unity.com/unity/editor/release/v1/releases?limit=1&platform=LINUX&architecture=X86_64&version=${prefix}" |
    python3 -c "import json,sys; r=json.load(sys.stdin)['results'][0]; print(r['version'], [d['url'] for d in r['downloads'] if d['platform']=='LINUX'][0])")
  dir="$CACHE/$version"
  if [ ! -f "$dir/Editor/Data/Managed/UnityEngine/UnityEditor.dll" ]; then
    echo "Downloading managed assemblies of Unity $version..."
    mkdir -p "$dir"
    curl -sS -L "$url" | xz -dc -T0 | tar -x -C "$dir" --wildcards 'Editor/Data/Managed/UnityEngine/*.dll'
  fi
  rm -rf "$HERE/obj"
  echo "== Unity $version"
  if "$DOTNET" build "$HERE/UnityCompileCheck.csproj" -nologo -v:q \
      -p:UnityManaged="$dir/Editor/Data/Managed/UnityEngine" -p:UnityLabel="$version" \
      "-p:UnityDefines=$(defines_for "$version")"; then
    echo "   OK"
  else
    echo "   FAILED"
    failed=1
  fi
done
exit $failed
