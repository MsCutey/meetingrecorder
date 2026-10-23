#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
ARCH="${ARCH:-$(uname -m)}"
if [[ "$ARCH" != arm64 && "$ARCH" != x86_64 ]]; then
    echo "Unsupported architecture: $ARCH" >&2
    exit 1
fi
export CLANG_MODULE_CACHE_PATH="$ROOT/artifacts/clang-cache"
export SWIFTPM_MODULECACHE_OVERRIDE="$ROOT/artifacts/swift-cache"
BUILD="$ROOT/artifacts/macos-$ARCH"
OUT="$ROOT/artifacts/macos-release-$ARCH"
APP="$OUT/MeetingRecorder.app"
TEST_FLAGS=()
# CLT 27 ships Testing.framework but does not discover its macro plugin automatically.
PLUGIN="$(dirname "$(xcrun --find swiftc)")/../lib/swift/host/plugins/testing/libTestingMacros.dylib"
if [[ -f "$PLUGIN" ]]; then
    TEST_FLAGS=(-Xswiftc -load-plugin-library -Xswiftc "$PLUGIN")
fi
swift test --disable-sandbox --package-path macos --scratch-path artifacts/macos-tests "${TEST_FLAGS[@]}"
swift build --build-system native --disable-sandbox --package-path macos --scratch-path "$BUILD" -c release --arch "$ARCH"
BIN="$(swift build --build-system native --disable-sandbox --package-path macos --scratch-path "$BUILD" -c release --arch "$ARCH" --show-bin-path)"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/MeetingRecorder" "$APP/Contents/MacOS/MeetingRecorder"
cp macos/Support/Info.plist "$APP/Contents/Info.plist"
ICONSET="$BUILD/MeetingRecorder.iconset"
mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
    sips -z "$SIZE" "$SIZE" src/MeetingRecorder.App/Assets/MeetingRecorder.png --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
    DOUBLE=$((SIZE * 2))
    sips -z "$DOUBLE" "$DOUBLE" src/MeetingRecorder.App/Assets/MeetingRecorder.png --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/MeetingRecorder.icns"
# A Developer ID can be supplied by the maintainer; ad-hoc signing works for local builds.
IDENTITY="${SIGN_IDENTITY:--}"
if [[ "$IDENTITY" == - ]]; then
    codesign --force --sign - --identifier com.mscutey.meetingrecorder "$APP"
else
    codesign --force --sign "$IDENTITY" --options runtime --timestamp "$APP"
fi
codesign --verify --strict --verbose=2 "$APP"
cp macos/README.md "$OUT/README-macOS.md"
cp THIRD-PARTY-NOTICES.txt "$OUT/THIRD-PARTY-NOTICES.txt"
ZIP="$ROOT/artifacts/MeetingRecorder-1.2.0-macOS-$ARCH.zip"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
(cd "$ROOT/artifacts" && shasum -a 256 "$(basename "$ZIP")" > "SHA256SUMS-macOS-$ARCH.txt")
echo "Built: $ZIP"
