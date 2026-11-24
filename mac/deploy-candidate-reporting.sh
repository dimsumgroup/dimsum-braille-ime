#!/bin/bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
rime_dir="$HOME/Library/Rime"
processor='    - lua_processor@*dimsum_candidate_reporter'
schemas=(dimsum_cb dimsum_cb_std dimsum_full)

if [[ "$(uname -s)" != Darwin ]]; then
    echo "請在安裝了 Squirrel 的 Mac 上部署候選報讀。" >&2
    exit 1
fi
if [[ "${1:-}" == --check ]]; then
    report="$rime_dir/dimsum-reporting/candidates"
    if [[ -r "$report" && "$(head -c 8 "$report")" == dimsum-2 ]]; then
        echo "已收到新版 Rime 候選報告。"
        exit 0
    fi
    echo "未收到新版候選報告。請切換到點心方案並試打，再執行本檢查。" >&2
    exit 1
fi

if [[ "${1:-}" != --prepare ]]; then
    squirrel_app="/Library/Input Methods/Squirrel.app"
    if [[ ! -d "$squirrel_app" ]]; then
        squirrel_app="$HOME/Library/Input Methods/Squirrel.app"
    fi
    deployer="$squirrel_app/Contents/MacOS/rime_deployer"
    squirrel="$squirrel_app/Contents/MacOS/Squirrel"
    if [[ ! -x "$deployer" || ! -x "$squirrel" ]]; then
        echo "找不到Squirrel，請先安裝。" >&2
        exit 1
    fi
fi

for schema in "${schemas[@]}"; do
    file="$rime_dir/$schema.schema.yaml"
    if [[ ! -f "$file" ]]; then
        echo "找不到 $schema 方案。請先執行 bash install.sh。" >&2
        exit 1
    fi
    if ! grep -qxF '  processors:' "$file"; then
        echo "無法辨認 $schema 的 processors 設定。" >&2
        exit 1
    fi
done

mkdir -p "$rime_dir/lua"
mkdir -p -m 700 "$rime_dir/dimsum-reporting"
chmod 700 "$rime_dir/dimsum-reporting"
install -m 644 "$script_dir/rime/lua/dimsum_candidate_reporter.lua" "$rime_dir/lua/dimsum_candidate_reporter.lua"

timestamp="$(date +%Y%m%d-%H%M%S)"
for schema in "${schemas[@]}"; do
    file="$rime_dir/$schema.schema.yaml"
    if grep -qxF "$processor" "$file"; then continue; fi
    staged="$(mktemp "$rime_dir/.dimsum-schema.XXXXXX")"
    trap 'rm -f "$staged"' EXIT
    awk -v processor="$processor" '
        { print }
        /^  processors:$/ && !added { print processor; added = 1 }
        END { if (!added) exit 1 }
    ' "$file" > "$staged"
    backup="$file.bak.$timestamp"
    if [[ ! -e "$backup" ]]; then cp -p "$file" "$backup"; fi
    mv "$staged" "$file"
    trap - EXIT
done

if [[ "${1:-}" == --prepare ]]; then exit 0; fi
"$deployer" --build "$rime_dir" "$squirrel_app/Contents/SharedSupport"
for schema in "${schemas[@]}"; do
    if ! grep -qF 'lua_processor@*dimsum_candidate_reporter' "$rime_dir/build/$schema.schema.yaml"; then
        echo "編譯後的 $schema 方案缺少候選報讀元件。" >&2
        exit 1
    fi
done
"$squirrel" --reload
echo "已重新載入點心方案。試打後可執行 bash deploy-candidate-reporting.sh --check 確認候選報告。"

