#!/usr/bin/env bash
# ============================================================================
# 點心粵語點字輸入法 — macOS（Squirrel）一鍵安裝腳本
#
# 用法：./install.sh
# 自動完成：安裝 Squirrel（如需）→ 啟用輸入法 → 部署方案 → 編譯 → 重新載入
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_DIR="$SCRIPT_DIR/rime"
SQUIRREL_APP="/Library/Input Methods/Squirrel.app"
if [[ ! -d "$SQUIRREL_APP" && -d "$HOME$SQUIRREL_APP" ]]; then
  SQUIRREL_APP="$HOME$SQUIRREL_APP"
fi
RIME_USER_DIR="$HOME/Library/Rime"
PKG_FILE="$SCRIPT_DIR/Squirrel-1.1.2.pkg"

FILES=(
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
echo "  點心粵語點字輸入法 — macOS 一鍵安裝"
echo "================================================"

# 1. 檢查/安裝 Squirrel
if [ ! -d "$SQUIRREL_APP" ]; then
  yellow "✗ 找不到 Squirrel"
  if [ -f "$PKG_FILE" ]; then
    if [[ ! -t 0 ]]; then
      yellow "  請先在管理器執行「01 安裝 Squirrel」。"
      exit 1
    fi
    yellow "→ 正在安裝 Squirrel-1.1.2.pkg（需要管理員密碼）……"
    /usr/bin/sudo /usr/sbin/installer -pkg "$PKG_FILE" -target /
    green "✓ Squirrel 安裝完成"
  else
    red "✗ 找不到 $PKG_FILE"
    yellow "  請先至 https://rime.im/download/ 下載並安裝 Squirrel.pkg"
    exit 1
  fi
else
  green "✓ 已偵測到 Squirrel"
fi

# 2. 啟用 Squirrel 輸入法（用 TIS API，不需登出登入）
green "→ 啟用 Squirrel 輸入法……"
if [ -x "$SCRIPT_DIR/dimsum_tool" ]; then
  if "$SCRIPT_DIR/dimsum_tool" enable-squirrel; then
    green "✓ Squirrel 輸入法已啟用"
  else
    yellow "⚠ 無法自動啟用 Squirrel 輸入法來源"
    yellow "  請到 系統設定 → 鍵盤 → 輸入法 → 編輯 → 手動加入「Squirrel」"
  fi
else
  yellow "⚠ 找不到 dimsum_tool，無法自動啟用輸入法來源"
  yellow "  請到 系統設定 → 鍵盤 → 輸入法 → 編輯 → 手動加入「Squirrel」"
fi

# 3. 檢查資料檔是否齊全
for f in "${FILES[@]}"; do
  if [ ! -f "$DATA_DIR/$f" ]; then
    red "✗ 缺少檔案：$DATA_DIR/$f"
    exit 1
  fi
done
green "✓ 資料檔齊全"

# 4. 建立 Rime 使用者目錄
mkdir -p "$RIME_USER_DIR"

# 5. 備份既有同名檔案
TS="$(date +%Y%m%d-%H%M%S)"
BACKUP_COUNT=0
for f in "${FILES[@]}"; do
  if [ -f "$RIME_USER_DIR/$f" ]; then
    cp "$RIME_USER_DIR/$f" "$RIME_USER_DIR/$f.bak.$TS"
    BACKUP_COUNT=$((BACKUP_COUNT + 1))
  fi
done
[ "$BACKUP_COUNT" -gt 0 ] && yellow "→ 已備份 ${BACKUP_COUNT} 個既有檔案（.bak.${TS}）"

# 6. 複製方案與詞庫
green "→ 正在複製點心粵語點字方案與詞庫（約 5.5MB）……"
for f in "${FILES[@]}"; do
  cp "$DATA_DIR/$f" "$RIME_USER_DIR/$f"
done

# 7. 清除舊編譯快取
rm -f "$RIME_USER_DIR"/build/dimsum_* 2>/dev/null || true
rm -f "$RIME_USER_DIR"/dimsum_*.bin 2>/dev/null || true
green "✓ 已清除舊編譯快取"

# 7a. 建立空的 user dict 檔案（避免 rime_deployer 編版本編譯失敗）
for schema in dimsum_cb dimsum_cb_std dimsum_full; do
  if [ ! -f "$RIME_USER_DIR/${schema}.user.dict.yaml" ]; then
    cat > "$RIME_USER_DIR/${schema}.user.dict.yaml" << USERDICT
---
name: ${schema}.user
version: "1"
sort: by_weight
...
USERDICT
  fi
done
green "✓ 已建立 user dict 檔案"

# 8. 部署候選報讀、編譯方案並重新載入 Squirrel。
green "→ 正在編譯方案並部署候選報讀……"
bash "$SCRIPT_DIR/deploy-candidate-reporting.sh"

# 10. 輸出完成訊息
cat <<EOF

$(green "============================================================")
$(green "  ✓ 點心粵語點字輸入法安裝完成！")
$(green "============================================================")

使用方式：
  • 按 Control + \`(反引號，數字 1 左側) → 切換三個點心方案
  • 記憶版 / 標準版：F D S / J K L 六鍵並擊輸入點字
  • 一陽指版：逐鍵輸入點碼
  • 空白鍵上字，H/G 瀏覽候選，數字鍵 1-9 快速選字

如候選窗未出現：
  點擊螢幕右上角輸入法圖示 → 選「重新部署 / Redeploy」

EOF
