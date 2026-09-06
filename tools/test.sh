#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FW_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
FRAMEWORK_PATH="${FW_TEST_FRAMEWORK_PATH:-fwc}"
IFS='/' read -r -a path_parts <<<"$FRAMEWORK_PATH"
[[ -n "$FRAMEWORK_PATH" && "$FRAMEWORK_PATH" != */ ]] || exit 1
for part in "${path_parts[@]}"; do
  if [[ ! "$part" =~ ^[A-Za-z0-9_.\ -]+$ || "$part" == . || "$part" == .. || "$part" == *' ' || "$part" == *. ]]; then
    echo "unsafe framework test path" >&2
    exit 1
  fi
done
TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/fw-template-test.XXXXXX")"
GODOT_EDITOR_TIMEOUT_SECONDS="${FW_GODOT_EDITOR_TIMEOUT_SECONDS:-90}"
GODOT_RUN_TIMEOUT_SECONDS="${FW_GODOT_RUN_TIMEOUT_SECONDS:-30}"

for value in "${GODOT_EDITOR_TIMEOUT_SECONDS}" "${GODOT_RUN_TIMEOUT_SECONDS}"; do
  [[ "${value}" =~ ^[1-9][0-9]*$ ]] || {
    echo "Godot test timeouts must be positive seconds." >&2
    exit 1
  }
done

cleanup() {
  rm -rf "${TEST_ROOT}"
}
trap cleanup EXIT

for pair in \
  "docs/rule.md:templates/fw_new/default/docs/fw/rule.md.tpl" \
  "docs/spec.md:templates/fw_new/default/docs/fw/spec.md.tpl" \
  "docs/use.md:templates/fw_new/default/docs/fw/use.md.tpl"; do
  source_path="${FW_ROOT}/${pair%%:*}"
  mirror_path="${FW_ROOT}/${pair#*:}"
  cmp -s "${source_path}" "${mirror_path}" || {
    echo "framework mirror is stale: ${pair}" >&2
    exit 1
  }
done

GODOT_DOTNET=""
godot_candidates=()
for variable in GODOT_BIN GODOT GODOT4; do
  if [[ -n "${!variable:-}" ]]; then
    godot_candidates+=("${!variable}")
  fi
done
for candidate in godot_mono godot4_mono godot_console godot godot4; do
  if command -v "${candidate}" >/dev/null 2>&1; then
    godot_candidates+=("$(command -v "${candidate}")")
  fi
done
for candidate in "${godot_candidates[@]}"; do
  [[ -f "${candidate}" || -L "${candidate}" ]] || continue
  resolved="$(readlink -f "${candidate}")"
  if [[ -x "${resolved}" ]] && { [[ "$(basename "${resolved}")" =~ [Mm][Oo][Nn][Oo] ]] || [[ -d "$(dirname "${resolved}")/GodotSharp" ]]; }; then
    GODOT_DOTNET="${resolved}"
    break
  fi
done
if [[ "${CI:-}" == "true" && -z "${GODOT_DOTNET}" ]]; then
  echo "CI requires Godot .NET; headless checks cannot be skipped." >&2
  exit 1
fi

dotnet build "${FW_ROOT}/csharp/FwGen/FwGen.csproj" -c Release
GODOT_BIN="${GODOT_DOTNET}" dotnet run --project "${FW_ROOT}/csharp/FwGenTests/FwGenTests.csproj" -c Release
dotnet run --project "${FW_ROOT}/csharp/Fw.Verify/Fw.Verify.csproj" -c Release

mkdir -p "${TEST_ROOT}/${FRAMEWORK_PATH}"
tar \
  --exclude='.git' \
  --exclude='*/bin' \
  --exclude='*/obj' \
  --exclude='*/.godot' \
  -C "${FW_ROOT}" -cf - . | tar -C "${TEST_ROOT}/${FRAMEWORK_PATH}" -xf -
GENERATOR="${FW_ROOT}/csharp/FwGen/FwGen.csproj"
bash "${FW_ROOT}/tools/new.sh" --project-root "${TEST_ROOT}" --name fw_audit --framework-path "${FRAMEWORK_PATH}" --generator-project "${GENERATOR}"
[[ ! -e "${TEST_ROOT}/.codex/skills" ]] || {
  echo "The default FWC template must not install agent skills; FWS is optional and separate." >&2
  exit 1
}
printf '%s\n' \
  '<Project Sdk="Microsoft.NET.Sdk">' \
  '  <PropertyGroup>' \
  '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>' \
  '  </PropertyGroup>' \
  '  <Import Project="csharp/_gen/_fw_host.props" />' \
  '</Project>' > "${TEST_ROOT}/host_audit.csproj"
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" check
generated_before="$(cd "${TEST_ROOT}" && find scripts/_gen scripts/_fw csharp/_gen "${FRAMEWORK_PATH}/scripts/.gdignore" -type f -print0 | sort -z | xargs -0 sha256sum)"
for command in sync system bridge config; do
  dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" "${command}"
done
generated_after="$(cd "${TEST_ROOT}" && find scripts/_gen scripts/_fw csharp/_gen "${FRAMEWORK_PATH}/scripts/.gdignore" -type f -print0 | sort -z | xargs -0 sha256sum)"
[[ "${generated_before}" == "${generated_after}" ]] || {
  echo "repeated generation is not deterministic" >&2
  exit 1
}
printf '\n// tampered\n' >> "${TEST_ROOT}/csharp/_gen/_core_systems.cs"
if dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" check; then
  echo "fw check accepted a modified generated file" >&2
  exit 1
fi
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" system
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" check
printf '\n# tampered\n' >> "${TEST_ROOT}/scripts/_fw/fw/rt/system/_app_root.gd"
if dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" check; then
  echo "fw check accepted a modified kit projection" >&2
  exit 1
fi
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" sync
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" check
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" config_pack
pack_before="$(cd "${TEST_ROOT}" && find pack/config -type f -print0 | sort -z | xargs -0 sha256sum)"
dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" config_pack
pack_after="$(cd "${TEST_ROOT}" && find pack/config -type f -print0 | sort -z | xargs -0 sha256sum)"
[[ "${pack_before}" == "${pack_after}" ]] || {
  echo "repeated config packing is not deterministic" >&2
  exit 1
}
dotnet build "${TEST_ROOT}/fw_audit.csproj" -c Release
dotnet build "${TEST_ROOT}/host_audit.csproj" -c Release

if [[ -n "${GODOT_DOTNET}" && -x "${GODOT_DOTNET}" ]]; then
  dotnet build "${TEST_ROOT}/fw_audit.csproj" -c Debug
  timeout "${GODOT_EDITOR_TIMEOUT_SECONDS}s" "${GODOT_DOTNET}" --headless --path "${TEST_ROOT}" --log-file "${TEST_ROOT}/godot_editor.log" --import
  dotnet run --project "${GENERATOR}" -c Release -- --root "${TEST_ROOT}" check
  dotnet build "${TEST_ROOT}/fw_audit.csproj" -c Debug
  mkdir -p "${TEST_ROOT}/scripts/_fw_probe"
  sed -e 's#res://fw/scripts/fw#res://scripts/_fw/fw#g' \
    -e "s#res://fw/tests/#res://${FRAMEWORK_PATH}/tests/#g" \
    "${TEST_ROOT}/${FRAMEWORK_PATH}/tests/runtime_test.gd" > "${TEST_ROOT}/scripts/_fw_probe/_runtime_test.gd"
  sed \
    -e 's#res://fw/scripts/fw#res://scripts/_fw/fw#g' \
    -e 's#res:fw/scripts/fw#res:scripts/_fw/fw#g' \
    "${TEST_ROOT}/${FRAMEWORK_PATH}/tools/verify_runtime.gd" > "${TEST_ROOT}/scripts/_fw_probe/_verify_runtime.gd"
  timeout "${GODOT_RUN_TIMEOUT_SECONDS}s" "${GODOT_DOTNET}" --headless --path "${TEST_ROOT}" --log-file "${TEST_ROOT}/godot_runtime.log" --script "res://scripts/_fw_probe/_runtime_test.gd"
  timeout "${GODOT_RUN_TIMEOUT_SECONDS}s" "${GODOT_DOTNET}" --headless --path "${TEST_ROOT}" --log-file "${TEST_ROOT}/godot_services.log" --script "res://scripts/_fw_probe/_verify_runtime.gd"
  GODOT_BIN="${GODOT_DOTNET}" bash "${TEST_ROOT}/${FRAMEWORK_PATH}/tools/verify_runtime.sh" "${TEST_ROOT}"
  timeout "${GODOT_RUN_TIMEOUT_SECONDS}s" "${GODOT_DOTNET}" --headless --path "${TEST_ROOT}" --log-file "${TEST_ROOT}/godot_game.log" --quit-after 3
  test -f "${TEST_ROOT}/.godot/global_script_class_cache.cfg"
  ! grep -Eiq 'SCRIPT ERROR|Parse Error|Compile Error|Can.t run project|^ERROR:' \
    "${TEST_ROOT}/godot_editor.log" "${TEST_ROOT}/godot_services.log" "${TEST_ROOT}/godot_game.log"
  ! sed \
    -e "/^ERROR: System 'second' init must return true; initialization failed\.$/d" \
    -e '/^ERROR: System manager tick cannot be reentered\.$/d' \
    -e '/^ERROR: FUI can only open scenes whose root extends FForm\.$/d' \
    -e '/^ERROR: FUI form id cannot be empty\.$/d' \
    "${TEST_ROOT}/godot_runtime.log" \
    | grep -Eiq 'SCRIPT ERROR|Parse Error|Compile Error|Can.t run project|^ERROR:'
else
  echo "Godot .NET not found; headless C# template check skipped. Set GODOT_BIN to enable it."
fi

echo "fw tests passed."
