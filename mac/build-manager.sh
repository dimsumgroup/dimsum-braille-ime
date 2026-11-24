#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo "請在 macOS 上編譯管理器。" >&2
    exit 1
fi
if ! xcode-select -p >/dev/null 2>&1; then
    echo "請先執行 xcode-select --install，完成後再執行此腳本。" >&2
    exit 1
fi

source_dir="$(cd "$(dirname "$0")" && pwd)"
helper="$source_dir/DimsumVoiceHelper.app"
if [[ ! -x "$helper/Contents/MacOS/DimsumVoiceHelper" ]]; then
    echo "請先執行 bash build-helper.sh --self-test。" >&2
    exit 1
fi
codesign --verify --strict "$helper"

build_dir="$source_dir/build"
mkdir -p "$build_dir"
staging="$(mktemp -d "$build_dir/.dimsum-build.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
manager="$staging/DimsumManager.app"
contents="$manager/Contents"
resources="$contents/Resources"
helpers="$contents/Helpers"
mkdir -p "$contents/MacOS" "$resources" "$helpers"

# 內嵌報讀助手及維護腳本使用的原生 CLI。
ditto "$helper" "$helpers/DimsumVoiceHelper.app"
cp "$source_dir/DimsumManager-Info.plist" "$contents/Info.plist"
MACOSX_DEPLOYMENT_TARGET=13.0 xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library -O \
    "$source_dir/DimsumManager.swift" "$source_dir/SharedSettingsView.swift" "$source_dir/DimsumUpdates.swift" \
    "$source_dir/ScreenReaderLookupAction.swift" \
    -o "$contents/MacOS/DimsumManager"
MACOSX_DEPLOYMENT_TARGET=13.0 xcrun --sdk macosx swiftc -swift-version 5 -enable-bare-slash-regex -O \
    "$source_dir/dimsum_tool.swift" -o "$helpers/dimsum_tool"

# 兩個指令使用同一個背景執行檔，從各自的 bundle ID 判斷查詢來源。
MACOSX_DEPLOYMENT_TARGET=13.0 xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
    -D DIMSUM_LOOKUP_ACTION -O "$source_dir/ScreenReaderLookupAction.swift" -o "$staging/DimsumLookupAction"
release_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$contents/Info.plist")"
for reader in VoiceOver TurnOver; do
    reader_id="$(printf '%s' "$reader" | tr '[:upper:]' '[:lower:]')"
    lookup_app="$helpers/點心查配詞（${reader}）.app"
    mkdir -p "$lookup_app/Contents/MacOS"
    cp "$staging/DimsumLookupAction" "$lookup_app/Contents/MacOS/DimsumLookupAction"
    sed -e "s/@READER@/${reader}/g" -e "s/@READER_ID@/${reader_id}/g" \
        -e "s/@VERSION@/${release_version}/g" "$source_dir/LookupAction-Info.plist" > "$lookup_app/Contents/Info.plist"
    codesign --force --sign - "$lookup_app"
done

for name in install.sh update.sh update_accessibility.sh deploy-candidate-reporting.sh uninstall.sh \
            convert_to_voiceover.py README.md; do
    cp "$source_dir/$name" "$resources/$name"
done
for name in rime nvda_dic voiceover_output licenses; do
    ditto "$source_dir/$name" "$resources/$name"
done
# 現有腳本透過資源目錄下的 dimsum_tool 呼叫已簽署的 CLI。
cat > "$resources/dimsum_tool" <<'LAUNCHER'
#!/bin/bash
contents="$(cd "$(dirname "$0")/.." && pwd)"
exec "$contents/Helpers/dimsum_tool" "$@"
LAUNCHER
chmod 755 "$resources/dimsum_tool"

pkg_name="Squirrel-1.1.2.pkg"
pkg_digest="614746013212937623d5bbab9901e9c43d1ec937aa32307d6b6092a05e308287"
if [[ -f "$source_dir/$pkg_name" ]]; then
    cp "$source_dir/$pkg_name" "$resources/$pkg_name"
else
    echo "從 Squirrel 官方發行頁下載 ${pkg_name}……"
    /usr/bin/curl --fail --location --silent --show-error --retry 2 \
        "https://github.com/rime/squirrel/releases/download/1.1.2/$pkg_name" \
        --output "$resources/$pkg_name"
fi
if [[ "$(/usr/bin/shasum -a 256 "$resources/$pkg_name" | awk '{print $1}')" != "$pkg_digest" ]]; then
    echo "$pkg_name 的 SHA-256 與官方發行檔案不符。" >&2
    exit 1
fi

# 先簽署內層，再簽署整個管理器。最後從已打包的位置執行助手自檢。
codesign --force --sign - "$helpers/dimsum_tool"
codesign --force --sign - "$manager"
codesign --verify --deep --strict "$manager"
"$helpers/DimsumVoiceHelper.app/Contents/MacOS/DimsumVoiceHelper" --self-test

archive="DimsumManager-macOS.zip"
ditto -c -k --keepParent "$manager" "$staging/$archive"
if [[ -e "$build_dir/DimsumManager.app" ]]; then
    mv "$build_dir/DimsumManager.app" "$staging/previous.app"
fi
if ! mv "$manager" "$build_dir/DimsumManager.app"; then
    if [[ -e "$staging/previous.app" ]]; then
        mv "$staging/previous.app" "$build_dir/DimsumManager.app"
    fi
    exit 1
fi
mv -f "$staging/$archive" "$build_dir/$archive"
printf '已編譯管理器：%s\n已建立發行包：%s\n' "$build_dir/DimsumManager.app" "$build_dir/$archive"
