#!/usr/bin/env bash
# ============================================================================
# 點心粵語點字輸入法 — macOS（Squirrel）解除安裝腳本
#
# 用法：
#   ./uninstall.sh        僅移除點心方案（保留 Squirrel 和其他 Rime 方案）
#   ./uninstall.sh --full 連 Squirrel 一起移除
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RIME_USER_DIR="$HOME/Library/Rime"
SQUIRREL_APP="/Library/Input Methods/Squirrel.app"
FULL_MODE=false

if [ "${1:-}" = "--full" ]; then
  FULL_MODE=true
fi

green()  { printf "\033[32m%s\033[0m\n" "$1"; }
yellow() { printf "\033[33m%s\033[0m\n" "$1"; }
red()    { printf "\033[31m%s\033[0m\n" "$1"; }

echo "================================================"
echo "  點心粵語點字輸入法 — macOS 解除安裝"
echo "================================================"

# 智能 sudo：GUI 模式（無終端機）用對話框輸入密碼
if [ ! -t 0 ]; then
  ASKPASS="/tmp/dimsum_askpass.sh"
  cat > "$ASKPASS" << 'ASKPASS'
#!/bin/bash
osascript -e 'display dialog "點心粵語輸入法需要管理員權限" default answer "" with hidden answer with title "管理員密碼"' -e 'text returned of result' 2>/dev/null
ASKPASS
  chmod +x "$ASKPASS"
  export SUDO_ASKPASS="$ASKPASS"
  SUDO="sudo -A"
else
  SUDO="sudo"
fi

DIMSUM_FILES=(
  "dimsum_cb.schema.yaml"
  "dimsum_cb_std.schema.yaml"
  "dimsum_full.schema.yaml"
  "dimsum_cb.dict.yaml"
  "squirrel.custom.yaml"
)

# 1. 移除點心方案檔案
for f in "${DIMSUM_FILES[@]}"; do
  if [ -f "$RIME_USER_DIR/$f" ]; then
    rm -f "$RIME_USER_DIR/$f"
    yellow "  已移除：$f"
  fi
done

rm -f "$RIME_USER_DIR/lua/dimsum_candidate_reporter.lua" \
  "$RIME_USER_DIR/dimsum-reporting/candidates" "$RIME_USER_DIR/dimsum-reporting/candidates.tmp"
rmdir "$RIME_USER_DIR/dimsum-reporting" 2>/dev/null || true

# 2. 移除編譯產物
rm -f "$RIME_USER_DIR"/build/dimsum_* 2>/dev/null || true
rm -f "$RIME_USER_DIR"/dimsum_*.bin 2>/dev/null || true
green "✓ 點心方案檔案與編譯快取已移除"

# 3. 清理 default.custom.yaml 中的 dimsum 方案條目
DEFAULT_CUSTOM="$RIME_USER_DIR/default.custom.yaml"
if [ -f "$DEFAULT_CUSTOM" ]; then
  if [ -x "$SCRIPT_DIR/dimsum_tool" ]; then
    "$SCRIPT_DIR/dimsum_tool" clean-default-custom
  elif command -v python3 >/dev/null 2>&1; then
    python3 << 'PYEOF'
import re, os
path = os.path.expanduser("~/Library/Rime/default.custom.yaml")
with open(path, "r", encoding="utf-8") as f:
    content = f.read()
# 移除 schema_list 中的 dimsum 條目
content = re.sub(r'\s*-\s*\{schema:\s*dimsum_\w+\}\s*\n', '\n', content)
with open(path, "w", encoding="utf-8") as f:
    f.write(content)
print("  已清理 default.custom.yaml 中的 dimsum 方案條目")
PYEOF
  else
    yellow "  ⚠ 無法自動清理 default.custom.yaml（缺少 dimsum_tool 與 python3）"
    yellow "    可手動編輯 $DEFAULT_CUSTOM，刪除 dimsum 開頭的方案行"
  fi
fi

# 4. 觸發 Squirrel 重新載入
if [ -x "$SCRIPT_DIR/dimsum_tool" ]; then
  "$SCRIPT_DIR/dimsum_tool" reload-squirrel || true
else
  osascript -l JavaScript -e 'ObjC.import("Foundation"); const c = $.NSDistributedNotificationCenter.defaultCenter; c.postNotificationNameObjectUserInfoDeliverImmediately("SquirrelReloadNotification", null, null, true)' 2>/dev/null || true
fi

green "✓ 點心粵語點字輸入法已解除安裝"

# 5. --full 模式：連 Squirrel 一起移除
if [ "$FULL_MODE" = true ]; then
  echo ""
  yellow "→ --full 模式：移除 Squirrel……"

  # 停用 Squirrel 輸入法來源
  if [ -x "$SCRIPT_DIR/dimsum_tool" ]; then
    "$SCRIPT_DIR/dimsum_tool" disable-squirrel || true
  else
    osascript -l JavaScript -e 'ObjC.import("Carbon"); 1' 2>/dev/null || true
  fi

  # 移除 Squirrel.app
  if [ -d "$SQUIRREL_APP" ]; then
    $SUDO rm -rf "$SQUIRREL_APP"
    yellow "  已移除 Squirrel.app"
  fi

  # 清除安裝記錄
  $SUDO pkgutil --forget im.rime.inputmethod.Squirrel 2>/dev/null || true
  yellow "  已清除 Squirrel 安裝記錄"

  green "✓ Squirrel 已完全移除"
  yellow "  提示：可能需要登出再登入才能完全移除輸入法選單項目"
fi

cat <<EOF

$(green "============================================================")
$(green "  ✓ 解除安裝完成！")
$(green "============================================================")

EOF
