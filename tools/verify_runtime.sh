#!/usr/bin/env bash
set -euo pipefail

fw_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_root="${1:-$(cd "$fw_root/.." && pwd)}"

dotnet run --project "$fw_root/csharp/Fw.Verify/Fw.Verify.csproj"

marker="$(mktemp "${TMPDIR:-/tmp}/fw-runtime.XXXXXX")"
rm -f "$marker"
trap 'rm -f "$marker"' EXIT

set +e
godot_output="$(
    FW_RUNTIME_VERIFY_MARKER="$marker" \
        godot --headless --path "$project_root" --script res://fw/tools/verify_runtime.gd 2>&1
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
