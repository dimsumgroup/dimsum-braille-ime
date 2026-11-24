#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo "請在 macOS 上建立磁碟映像。" >&2
    exit 1
fi
if [[ $# -gt 1 ]]; then
    echo "用法：bash build-dmg.sh [DimsumManager.app 或 DimsumManager-macOS.zip]" >&2
    exit 2
fi

source_dir="$(cd "$(dirname "$0")" && pwd)"
build_dir="$source_dir/build"
input="${1:-$build_dir/DimsumManager.app}"
if [[ $# -eq 0 && ! -d "$input" ]]; then
    input="$build_dir/DimsumManager-macOS.zip"
fi
if [[ ! -e "$input" ]]; then
    echo "找不到 ${input}，請先執行 build-manager.sh 或傳入現有的發行 ZIP。" >&2
    exit 1
fi

mkdir -p "$build_dir"
staging="$(mktemp -d "$build_dir/.dmg-build.XXXXXX")"
trap 'rm -rf "$staging"' EXIT

if [[ "$input" == *.zip ]]; then
    mkdir "$staging/extracted"
    ditto -x -k "$input" "$staging/extracted"
    input="$staging/extracted/DimsumManager.app"
fi
if [[ ! -x "$input/Contents/MacOS/DimsumManager" ]]; then
    echo "找不到已編譯的 DimsumManager.app。" >&2
    exit 1
fi
codesign --verify --deep --strict "$input"

volume="$staging/volume"
mkdir "$volume"
ditto "$input" "$volume/DimsumManager.app"
ln -s /Applications "$volume/Applications"
cp "$source_dir/安裝及使用說明.txt" "$volume/安裝及使用說明.txt"
cp "$source_dir/dmg-layout.dsstore" "$volume/.DS_Store"

image="$staging/DimsumManager-macOS.dmg"
hdiutil create -volname "DimsumManager" -srcfolder "$volume" -fs HFS+ -format UDZO "$image"
hdiutil verify "$image"
mv -f "$image" "$build_dir/DimsumManager-macOS.dmg"
printf '已建立磁碟映像：%s\n' "$build_dir/DimsumManager-macOS.dmg"
