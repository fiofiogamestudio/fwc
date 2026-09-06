#!/usr/bin/env bash
set -euo pipefail

fw_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_root="${1:-$(cd "$fw_root/.." && pwd)}"
project_root="$(cd "$project_root" && pwd)"
case "$fw_root" in
  "$project_root"/*) probe_resource="res://${fw_root#"$project_root"/}/tools/verify_runtime.gd" ;;
  *) echo "The runtime probe must be installed inside the specified project root." >&2; exit 1 ;;
esac

dotnet run --project "$fw_root/csharp/Fw.Verify/Fw.Verify.csproj"

marker="$(mktemp "${TMPDIR:-/tmp}/fw-runtime.XXXXXX")"
rm -f "$marker"
trap 'rm -f "$marker"' EXIT

set +e
godot_output="$(
    FW_RUNTIME_VERIFY_MARKER="$marker" \
        "${GODOT_BIN:-godot}" --headless --path "$project_root" --script "$probe_resource" 2>&1
)"
godot_status=$?
set -e
printf '%s\n' "$godot_output"

if [[ $godot_status -ne 0 ]]; then
    exit "$godot_status"
fi
if grep -Eq '^(SCRIPT ERROR|ERROR):' <<<"$godot_output"; then
    echo "Godot runtime verification reported script or engine errors." >&2
    exit 1
fi
if [[ ! -f "$marker" ]]; then
    echo "Godot runtime verification did not reach its success marker." >&2
    exit 1
fi
