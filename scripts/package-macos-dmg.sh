#!/bin/bash
set -euo pipefail

if [[ $# != 3 ]]; then
    echo "Usage: $0 APP_PATH DMG_PATH VOLUME_NAME" >&2
    exit 1
fi
APP="$1"
DMG="$2"
VOLUME_NAME="$3"
if [[ ! -d "$APP/Contents/MacOS" || "$DMG" != *.dmg ]]; then
    echo "Expected a built .app and a .dmg output path." >&2
    exit 1
fi
codesign --verify --strict "$APP"
mkdir -p "$(dirname "$DMG")"
DMG_STAGE="$(mktemp -d "$(dirname "$DMG")/.dmg-stage.XXXXXX")"
trap 'rm -rf "$DMG_STAGE"' EXIT
ditto "$APP" "$DMG_STAGE/$(basename "$APP")"
ln -s /Applications "$DMG_STAGE/Applications"
cat > "$DMG_STAGE/安装说明.txt" <<'EOF'
将旁边的应用拖到 Applications 文件夹即可安装。
更新前请先退出旧版本；完成安装后推出此磁盘镜像，从“应用程序”打开软件。
EOF
hdiutil create -ov -fs HFS+ -format UDZO -volname "$VOLUME_NAME" -srcfolder "$DMG_STAGE" "$DMG"
hdiutil verify "$DMG"
