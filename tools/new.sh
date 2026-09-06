#!/usr/bin/env bash
set -euo pipefail

NAME=""
PROJECT_ROOT=""
GENERATOR_PROJECT=""
FRAMEWORK_PATH="fwc"
FORCE=0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --name)
      NAME="$2"
      shift 2
      ;;
    --project-root|--root)
      PROJECT_ROOT="$2"
      shift 2
      ;;
    --generator-project)
      GENERATOR_PROJECT="$2"
      shift 2
      ;;
    --framework-path)
      FRAMEWORK_PATH="$2"
      shift 2
      ;;
    --force)
      FORCE=1
      shift
      ;;
    *)
      echo "unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

FRAMEWORK_PATH="${FRAMEWORK_PATH//\\//}"
IFS='/' read -r -a path_parts <<<"$FRAMEWORK_PATH"
if [[ -z "$FRAMEWORK_PATH" || "$FRAMEWORK_PATH" == */ ]]; then
  echo "framework path must be project-relative without traversal" >&2
  exit 1
fi
for part in "${path_parts[@]}"; do
  if [[ ! "$part" =~ ^[A-Za-z0-9_.\ -]+$ || "$part" == . || "$part" == .. || "$part" == *' ' || "$part" == *. ]]; then
    echo "framework path must be project-relative without traversal or shell metacharacters" >&2
    exit 1
  fi
done

if [[ -z "$PROJECT_ROOT" ]]; then
  PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
fi
PROJECT_ROOT="$(cd "${PROJECT_ROOT}" && pwd)"

if [[ -z "$GENERATOR_PROJECT" ]]; then
  GENERATOR_PROJECT="$PROJECT_ROOT/$FRAMEWORK_PATH/csharp/FwGen/FwGen.csproj"
fi

pushd "$PROJECT_ROOT" >/dev/null
ARGS=(
  run
  --project "$GENERATOR_PROJECT"
  --
  --root "$PROJECT_ROOT"
  craft
  fw-new
  --framework-path "$FRAMEWORK_PATH"
)
if [[ -n "$NAME" ]]; then
  ARGS+=(--name "$NAME")
fi
if [[ "$FORCE" -eq 1 ]]; then
  ARGS+=(--force)
fi
dotnet "${ARGS[@]}"
popd >/dev/null
