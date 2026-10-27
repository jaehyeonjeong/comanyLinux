#!/bin/bash
# 버전 올리기 (WSL/리눅스용, tools/bump-version.ps1 과 같은 규칙)
#   ./tools/bump_version.sh patch|minor|major        예: 1.1.0 → 1.1.1 / 1.2.0 / 2.0.0
#   ./tools/bump_version.sh set 1.2.0-rc1
#   옵션: --dry-run  --force (CHANGELOG [Unreleased] 가 비어 있어도 진행)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VF="$ROOT/VERSION"; CL="$ROOT/CHANGELOG.md"
SEMVER='^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'
DRY=0; FORCE=0; MODE=""; SETV=""
for a in "$@"; do
  case "$a" in
    --dry-run) DRY=1 ;; --force) FORCE=1 ;;
    major|minor|patch) MODE="$a" ;;
    set) MODE=set ;;
    *) if [[ "$MODE" == "set" && -z "$SETV" ]]; then SETV="$a"; else echo "알 수 없는 인수: $a" >&2; exit 2; fi ;;
  esac
done
[[ -n "$MODE" ]] || { echo "사용법: $0 patch|minor|major | set x.y.z [--dry-run] [--force]" >&2; exit 2; }

CUR="$(tr -d '\r\n' < "$VF" | sed 's/^\xEF\xBB\xBF//')"
[[ "$CUR" =~ $SEMVER ]] || { echo "VERSION 이 SemVer 가 아님: '$CUR'" >&2; exit 1; }
MAJ=${BASH_REMATCH[1]}; MIN=${BASH_REMATCH[2]}; PAT=${BASH_REMATCH[3]}
case "$MODE" in
  major) NEW="$((MAJ+1)).0.0" ;;
  minor) NEW="$MAJ.$((MIN+1)).0" ;;
  patch) NEW="$MAJ.$MIN.$((PAT+1))" ;;
  set)   NEW="$SETV"; [[ "$NEW" =~ $SEMVER ]] || { echo "새 버전이 SemVer 가 아님: '$NEW'" >&2; exit 1; } ;;
esac
# 커졌는지 (숫자부 비교; 같으면 프리릴리스 없는 쪽이 큼)
ver_key() { [[ "$1" =~ $SEMVER ]]; printf '%05d%05d%05d%s' "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}" "${BASH_REMATCH[4]:-~}"; }
if [[ "$(ver_key "$NEW")" < "$(ver_key "$CUR")" || "$(ver_key "$NEW")" == "$(ver_key "$CUR")" ]]; then
  echo "새 버전 $NEW 이(가) 현재 $CUR 보다 크지 않습니다." >&2; exit 1
fi

grep -q '^## \[Unreleased\]' "$CL" || { echo "CHANGELOG.md 에 '## [Unreleased]' 절이 없습니다." >&2; exit 1; }
BODY="$(awk '/^## \[Unreleased\]/{f=1; next} /^## \[/{if(f) exit} f' "$CL")"
if ! grep -qE '^\s*[-*]\s+\S' <<< "$BODY" && [[ $FORCE -eq 0 ]]; then
  echo "CHANGELOG.md 의 [Unreleased] 에 변경 내용이 없습니다. 먼저 적거나 --force 를 쓰세요." >&2; exit 1
fi
DATE="$(date +%Y-%m-%d)"
NEWCL="$(awk -v v="$NEW" -v d="$DATE" '
  /^## \[Unreleased\]/ && !done { print; print ""; print "## [" v "] - " d; done=1; next }
  { print }' "$CL")"

echo "VERSION   : $CUR → $NEW"
echo "CHANGELOG : [Unreleased] → [$NEW] - $DATE"
[[ $DRY -eq 1 ]] && { echo "(dry run — 파일은 바꾸지 않음)"; exit 0; }

printf '%s\n' "$NEWCL" > "$CL"
printf '%s\n' "$NEW" > "$VF"
echo "완료. 다음: dotnet build / dotnet test / ./pi_display/deploy_pi_display.sh rpi"
