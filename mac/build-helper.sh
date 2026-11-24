#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo "請在 macOS 上編譯報讀助手。" >&2
    exit 1
fi
if ! xcode-select -p >/dev/null 2>&1; then
    echo "請先執行 xcode-select --install，完成後再執行此腳本。" >&2
    exit 1
fi

source_dir="$(cd "$(dirname "$0")" && pwd)"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

app="DimsumVoiceHelper.app"
executable="Contents/MacOS/DimsumVoiceHelper"
mkdir -p "$staging/$app/Contents/MacOS" "$staging/$app/Contents/Resources"
cp "$source_dir/sounds/only.wav" "$staging/$app/Contents/Resources/only.wav"
cp "$source_dir/dimsum_vo_helper.swift" "$staging/main.swift"
cp "$source_dir/DimsumVoiceHelper-Info.plist" "$staging/$app/Contents/Info.plist"
MACOSX_DEPLOYMENT_TARGET=13.0 xcrun --sdk macosx swiftc -swift-version 5 -enable-bare-slash-regex -O \
    "$staging/main.swift" "$source_dir/SharedSettingsView.swift" "$source_dir/LastSpokenLookup.swift" \
    "$source_dir/ScreenReaderLookupAction.swift" \
    -o "$staging/$app/$executable"
codesign --force --sign - "$staging/$app"
codesign --verify "$staging/$app"
ditto "$staging/$app" "$source_dir/$app"
codesign --verify "$source_dir/$app"
echo "已編譯 $source_dir/$app"

if [[ "${1:-}" == --self-test ]]; then
    "$source_dir/$app/$executable" --self-test
fi
