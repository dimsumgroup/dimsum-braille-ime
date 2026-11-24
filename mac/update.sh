#!/usr/bin/env bash
# ============================================================================
# 點心粵語點字輸入法 — macOS 一鍵更新方案與詞庫
#
# 用法：
#   ./update.sh          ← 從本地 repo 同步並重新部署
#   ./update.sh --pull   ← 先 git pull 最新版本，再同步部署
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# 動態尋找 dimsum-braille-ime repo（支援多種部署場景）
CACHE_DIR="$HOME/Library/Application Support/DimsumManager"
CACHE_REPO="$CACHE_DIR/dimsum-braille-ime"

find_repo_dir() {
  # 直接在 GitHub 儲存庫的 mac/ 內執行時，使用同一個 checkout 的共用資料。
  if [[ -d "$SCRIPT_DIR/../Dimsum dic and schema" && -d "$SCRIPT_DIR/../NVDA dic" ]]; then
    (cd "$SCRIPT_DIR/.." && pwd)
    return
  fi
  # 1. 用戶快取（之前 --pull 克隆的）
  if [ -d "$CACHE_REPO/.git" ]; then echo "$CACHE_REPO"; return 0; fi
  # 2. Resources/ 內嵌（DMG 分發模式）
  if [ -d "$SCRIPT_DIR/dimsum-braille-ime" ]; then echo "$SCRIPT_DIR/dimsum-braille-ime"; return 0; fi
  # 3. 開發模式：mac/ 的同層
  if [ -d "$SCRIPT_DIR/../dimsum-braille-ime" ]; then echo "$SCRIPT_DIR/../dimsum-braille-ime"; return 0; fi
  return 1
}

clone_or_update_repo() {
  if ! command -v git >/dev/null 2>&1; then
    echo "  ✗ 系統沒有 git（新 Mac 需先安裝 Xcode 輸入的工具：xcode-select --install）"
    return 1
  fi
  mkdir -p "$CACHE_DIR"
  if [ -d "$CACHE_REPO/.git" ]; then
    git -C "$CACHE_REPO" pull --rebase 2>&1 | while read -r line; do echo "  $line"; done || return 1
  else
    git clone "https://github.com/dimsumgroup/dimsum-braille-ime.git" "$CACHE_REPO" 2>&1 | while read -r line; do echo "  $line"; done || return 1
  fi
  [ -d "$CACHE_REPO/.git" ]
}

REPO_DIR="$(find_repo_dir)" || REPO_DIR=""
SOURCE_DIR="${REPO_DIR:+$REPO_DIR/Dimsum dic and schema}"
NVDA_SOURCE_DIR="${REPO_DIR:+$REPO_DIR/NVDA dic}"
DATA_DIR="$SCRIPT_DIR/rime"
NVDA_DATA_DIR="$SCRIPT_DIR/nvda_dic"
RIME_USER_DIR="$HOME/Library/Rime"

# 同步的 4 個核心檔案（跨平台一致）
SYNC_FILES=(
  "dimsum_cb.dict.yaml"
  "dimsum_cb.schema.yaml"
  "dimsum_cb_std.schema.yaml"
  "dimsum_full.schema.yaml"
)

# NVDA 字典檔案
NVDA_FILES=(
  "characterDescriptions.dic"
  "cldr.dic"
  "symbols.dic"
)

# 部署到使用者目錄的全部檔案（含平台特定）
DEPLOY_FILES=(
  "default.custom.yaml"
  "squirrel.custom.yaml"
  "dimsum_cb.schema.yaml"
  "dimsum_cb_std.schema.yaml"
  "dimsum_full.schema.yaml"
  "dimsum_cb.dict.yaml"
)

green()  { printf "\033[32m%s\033[0m\n" "$1"; }
yellow() { printf "\033[33m%s\033[0m\n" "$1"; }
red()    { printf "\033[31m%s\033[0m\n" "$1"; }

echo "================================================"
echo "  點心粵語點字輸入法 — macOS 更新"
echo "================================================"

# 0. 若 --pull，先 git pull 或 clone
if [ "${1:-}" = "--pull" ]; then
  green "→ 從 GitHub 更新詞庫資料……"
  if clone_or_update_repo; then
    REPO_DIR="$CACHE_REPO"
    SOURCE_DIR="$REPO_DIR/Dimsum dic and schema"
    NVDA_SOURCE_DIR="$REPO_DIR/NVDA dic"
    green "✓ git 更新完成"
  else
    red "✗ 無法從 GitHub 更新"
    yellow "  將嘗試使用本地已有的詞庫資料"
  fi
fi

# 1. 尋找原始項目來源目錄（快取 → Resources/ 內嵌 → 開發模式）
if [ -z "$REPO_DIR" ]; then
  REPO_DIR="$(find_repo_dir)" || {
    red "✗ 找不到 dimsum-braille-ime 詞庫資料來源"
    yellow "  執行 ./update.sh --pull 可從 GitHub 下載最新詞庫"
    exit 1
  }
  SOURCE_DIR="$REPO_DIR/Dimsum dic and schema"
  NVDA_SOURCE_DIR="$REPO_DIR/NVDA dic"
fi

if [ ! -d "$SOURCE_DIR" ]; then
  red "✗ 找不到原始項目來源：$SOURCE_DIR"
  yellow "  執行 ./update.sh --pull 可從 GitHub 下載最新詞庫"
  exit 1
fi
green "✓ 找到原始項目來源：$REPO_DIR"

# 簽署後的 App 資源是發行時的原始檔。更新時在暫存目錄準備部署檔案。
if [[ "$SCRIPT_DIR" == *.app/Contents/Resources ]]; then
  staging="$(mktemp -d)"
  trap 'rm -rf "$staging"' EXIT
  cp -R "$DATA_DIR" "$staging/rime"
  DATA_DIR="$staging/rime"
  NVDA_DATA_DIR="$CACHE_DIR/nvda_dic"
  mkdir -p "$NVDA_DATA_DIR"
fi

# 2. 同步 Rime 方案與詞庫
green "→ 同步方案與詞庫……"
UPDATED=0
for f in "${SYNC_FILES[@]}"; do
  if [ -f "$SOURCE_DIR/$f" ]; then
    if ! diff -q "$SOURCE_DIR/$f" "$DATA_DIR/$f" >/dev/null 2>&1; then
      cp "$SOURCE_DIR/$f" "$DATA_DIR/$f"
      yellow "  已更新：$f"
      UPDATED=$((UPDATED + 1))
    else
      green "  無變化：$f"
    fi
  else
    red "  ✗ 來源缺少：$f"
  fi
done

# 3. 同步 NVDA 字典
green "→ 同步 NVDA 報讀字典……"
for f in "${NVDA_FILES[@]}"; do
  if [ -f "$NVDA_SOURCE_DIR/$f" ]; then
    if ! diff -q "$NVDA_SOURCE_DIR/$f" "$NVDA_DATA_DIR/$f" >/dev/null 2>&1; then
      cp "$NVDA_SOURCE_DIR/$f" "$NVDA_DATA_DIR/$f"
      yellow "  已更新：$f"
      UPDATED=$((UPDATED + 1))
    else
      green "  無變化：$f"
    fi
  fi
done

if [ "$UPDATED" -gt 0 ]; then
  green "✓ 詞庫資料已更新 $UPDATED 個檔案"
fi

# 3.5 檢查使用者目錄是否落後於 app 內檔案（過時就必須重新部署）
NEED_DEPLOY=0
if [ ! -d "$RIME_USER_DIR" ]; then
  NEED_DEPLOY=1
else
  for f in "${DEPLOY_FILES[@]}"; do
    if [ -f "$DATA_DIR/$f" ]; then
      if [[ "$f" == *.schema.yaml && -f "$RIME_USER_DIR/$f" ]] &&
         grep -qxF '    - lua_processor@*dimsum_candidate_reporter' "$RIME_USER_DIR/$f" &&
         diff -q <(sed '/^    - lua_processor@\*dimsum_candidate_reporter$/d' "$RIME_USER_DIR/$f") \
           <(awk '1' "$DATA_DIR/$f") >/dev/null; then
        continue
      fi
      if [[ "$f" == *.schema.yaml ]] ||
         ! cmp -s "$RIME_USER_DIR/$f" "$DATA_DIR/$f"; then
        yellow "  使用者目錄過時：$f"
        NEED_DEPLOY=1
      fi
    fi
  done
fi

if ! cmp -s "$DATA_DIR/lua/dimsum_candidate_reporter.lua" "$RIME_USER_DIR/lua/dimsum_candidate_reporter.lua" ||
   [[ ! -d "$RIME_USER_DIR/dimsum-reporting" ]]; then
  NEED_DEPLOY=1
fi

if [ "$UPDATED" -eq 0 ] && [ "$NEED_DEPLOY" -eq 0 ]; then
  green "✓ 所有檔案均為最新，無需重新部署"
  exit 0
fi

# 4. 部署到使用者目錄
green "→ 部署到使用者目錄……"
mkdir -p "$RIME_USER_DIR"
TS="$(date +%Y%m%d-%H%M%S)"
for f in "${DEPLOY_FILES[@]}"; do
  if [ -f "$DATA_DIR/$f" ]; then
    [ -f "$RIME_USER_DIR/$f" ] && cp "$RIME_USER_DIR/$f" "$RIME_USER_DIR/$f.bak.$TS"
    cp "$DATA_DIR/$f" "$RIME_USER_DIR/$f"
  fi
done

# 5. 清除舊編譯快取，再部署候選報讀及重新載入。
rm -f "$RIME_USER_DIR"/build/dimsum_* 2>/dev/null || true
green "→ 重新編譯方案並部署候選報讀……"
bash "$SCRIPT_DIR/deploy-candidate-reporting.sh"

# 8. 若 NVDA 字典有更新，提示重新產生 VoiceOver 發音
if [ "$UPDATED" -gt 0 ]; then
  yellow "ℹ NVDA 字典已更新，建議執行以下指令重新產生 VoiceOver 發音檔："
  yellow "  請按管理器的「更新發音」，或執行 bash update_accessibility.sh。"
fi

cat <<EOF

$(green "============================================================")
$(green "  ✓ 更新完成！已更新 $UPDATED 個檔案")
$(green "============================================================")

EOF
