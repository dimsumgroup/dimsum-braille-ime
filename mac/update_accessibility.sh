#!/usr/bin/env bash
# 同步 NVDA 字典並重新產生報讀助手的發音表。
# 加 --pull 時直接從 GitHub 取得三個檔案。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE_DIR="$HOME/Library/Application Support/DimsumManager"
LOCAL_DIC_DIR="$SCRIPT_DIR/nvda_dic"
VOICEOVER_DIR="$SCRIPT_DIR/voiceover_output"
if [[ "$SCRIPT_DIR" == *.app/Contents/Resources || "${1:-}" == --pull ]]; then
  LOCAL_DIC_DIR="$CACHE_DIR/nvda_dic"
  VOICEOVER_DIR="$HOME/Library/Application Support/DimsumVoiceOver/voiceover_output"
fi
CONVERT_TOOL="$SCRIPT_DIR/convert_to_voiceover.py"

NVDA_FILES=(characterDescriptions.dic symbols.dic cldr.dic)
OUTPUT_FILES=(DimsumPronunciations.swift voiceover_pronunciations.csv DimsumPronunciations.json)
INPUT_RECORD=nvda-source-hashes.json
UPSTREAM=dimsumgroup/dimsum-braille-ime

find_source_dir() {
  local repo
  for repo in "$SCRIPT_DIR/.." "$CACHE_DIR/dimsum-braille-ime" "$SCRIPT_DIR/dimsum-braille-ime" "$SCRIPT_DIR/../dimsum-braille-ime"; do
    if [[ -d "$repo/NVDA dic" ]]; then
      printf '%s\n' "$repo/NVDA dic"
      return
    fi
  done
  printf '%s\n' "$SCRIPT_DIR/nvda_dic"
}

# 與 GitHub Contents API 內的 blob SHA 計算法一致。
git_blob_hash() {
  local size
  size="$(LC_ALL=C /usr/bin/wc -c < "$1" | tr -d '[:space:]')"
  {
    printf 'blob %s\000' "$size"
    cat "$1"
  } | /usr/bin/shasum -a 1 | awk '{print $1}'
}

echo "點心粵語點字輸入法 — 更新發音"
mkdir -p "$VOICEOVER_DIR"
staging="$(mktemp -d "$VOICEOVER_DIR/.nvda-update.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
mkdir "$staging/inputs" "$staging/outputs"

if [[ "${1:-}" == --pull ]]; then
  echo "正在從 GitHub 取得最新 NVDA 字典……"
  curl_options=(--fail --silent --show-error --location --connect-timeout 10 --max-time 30 --retry 2)
  /usr/bin/curl "${curl_options[@]}" "https://api.github.com/repos/$UPSTREAM/commits/main" -o "$staging/commit.json"
  revision="$(/usr/bin/plutil -extract sha raw -o - "$staging/commit.json")"
  if [[ ! "$revision" =~ ^[0-9a-f]{40}$ ]]; then
    echo "無法識別 GitHub 字典版本。" >&2
    exit 1
  fi
  for file in "${NVDA_FILES[@]}"; do
    /usr/bin/curl "${curl_options[@]}" "https://raw.githubusercontent.com/$UPSTREAM/$revision/NVDA%20dic/$file" \
      -o "$staging/inputs/$file"
  done
else
  source_dir="$(find_source_dir)"
  for file in "${NVDA_FILES[@]}"; do
    cp "$source_dir/$file" "$staging/inputs/$file"
  done
fi

for file in "${NVDA_FILES[@]}"; do
  if [[ ! -s "$staging/inputs/$file" ]]; then
    echo "NVDA 字典缺少內容：${file}" >&2
    exit 1
  fi
done

if [[ -x "$SCRIPT_DIR/dimsum_tool" ]]; then
  "$SCRIPT_DIR/dimsum_tool" convert-voiceover "$staging/inputs" "$staging/outputs"
elif command -v python3 >/dev/null 2>&1 && [[ -f "$CONVERT_TOOL" ]]; then
  python3 "$CONVERT_TOOL" "$staging/inputs" "$staging/outputs"
else
  echo "找不到發音表轉換工具。請重新安裝 DimsumManager.app。" >&2
  exit 1
fi
for file in "${OUTPUT_FILES[@]}"; do
  if [[ ! -s "$staging/outputs/$file" ]]; then
    echo "未成功產生 ${file}。" >&2
    exit 1
  fi
done

# 紀錄經過轉換的來源及實際輸出。先更換發音表，最後才發佈紀錄。
table_hash="$(git_blob_hash "$staging/outputs/DimsumPronunciations.json")"
description_hash="$(git_blob_hash "$staging/inputs/characterDescriptions.dic")"
symbols_hash="$(git_blob_hash "$staging/inputs/symbols.dic")"
cldr_hash="$(git_blob_hash "$staging/inputs/cldr.dic")"
printf '{"tableSHA":"%s","sourceSHAs":{"characterDescriptions.dic":"%s","symbols.dic":"%s","cldr.dic":"%s"}}\n' \
  "$table_hash" "$description_hash" "$symbols_hash" "$cldr_hash" > "$staging/outputs/$INPUT_RECORD"

mkdir -p "$LOCAL_DIC_DIR"
for file in "${NVDA_FILES[@]}"; do
  cp "$staging/inputs/$file" "$LOCAL_DIC_DIR/$file"
done
for file in "${OUTPUT_FILES[@]}" "$INPUT_RECORD"; do
  mv -f "$staging/outputs/$file" "$VOICEOVER_DIR/$file"
done
echo "已更新發音資料。請重新啟動報讀助手以套用。"
