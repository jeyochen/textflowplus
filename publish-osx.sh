#!/bin/bash
# =============================================================
# TextFlowPlus macOS 发布脚本
# 生成自包含的 .app 应用程序包
# =============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/TextFlowPlus"
PUBLISH_DIR="$SCRIPT_DIR/publish"
APP_NAME="TextFlowPlus"
APP_BUNDLE="$PUBLISH_DIR/$APP_NAME.app"
RID="osx-arm64"

# 颜色输出
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

echo -e "${GREEN}============================================${NC}"
echo -e "${GREEN}  TextFlowPlus macOS 发布构建${NC}"
echo -e "${GREEN}============================================${NC}"
echo ""

# 确保 DOTNET_ROOT 已设置
if [ -z "$DOTNET_ROOT" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
fi
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

echo -e "${YELLOW}[1/6] 检查 .NET SDK...${NC}"
dotnet --version
echo ""

echo -e "${YELLOW}[2/6] 清理旧发布文件...${NC}"
rm -rf "$PUBLISH_DIR"
rm -rf "$PROJECT_DIR/bin/Release"
rm -rf "$PROJECT_DIR/obj/Release"
echo ""

echo -e "${YELLOW}[3/6] 发布自包含应用 (RID: $RID)...${NC}"
dotnet publish "$PROJECT_DIR/TextFlowPlus.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$PUBLISH_DIR/app-files"
echo ""

echo -e "${YELLOW}[4/6] 创建 .app 包结构...${NC}"
# 创建 .app 目录结构
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# 复制所有发布文件到 MacOS 目录
cp -R "$PUBLISH_DIR/app-files/"* "$APP_BUNDLE/Contents/MacOS/"

# 清理不需要的文件
rm -f "$APP_BUNDLE/Contents/MacOS/"*.pdb 2>/dev/null || true
rm -rf "$APP_BUNDLE/Contents/MacOS/runtimes/win"* 2>/dev/null || true
rm -rf "$APP_BUNDLE/Contents/MacOS/runtimes/linux"* 2>/dev/null || true

# 生成 Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" << INFOEOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>zh_CN</string>
    <key>CFBundleExecutable</key>
    <string>TextFlowPlus</string>
    <key>CFBundleIdentifier</key>
    <string>com.textflowplus.app</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>TextFlowPlus</string>
    <key>CFBundleDisplayName</key>
    <string>TextFlowPlus</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
</dict>
</plist>
INFOEOF

# 从 app.ico 生成 macOS .icns 图标
ICON_ICO="$PROJECT_DIR/Assets/app.ico"

if [ -f "$ICON_ICO" ]; then
    echo "  从 app.ico 生成应用图标..."
    ICONSET_DIR="$APP_BUNDLE/Contents/Resources/icon.iconset"
    mkdir -p "$ICONSET_DIR"

    # 直接使用 AIE 图标源 PNG (512px) 作为基准，比从 ICO 转换更清晰
    BASE_PNG="$APP_BUNDLE/Contents/Resources/icon-base.png"
    if [ -f "$PROJECT_DIR/Assets/AIELogo/icon-512.png" ]; then
        cp "$PROJECT_DIR/Assets/AIELogo/icon-512.png" "$BASE_PNG"
    else
        sips -s format png -z 512 512 "$ICON_ICO" --out "$BASE_PNG" 2>/dev/null
    fi

    # 从基准 PNG 生成各尺寸
    sips -z 16 16    "$BASE_PNG" --out "$ICONSET_DIR/icon_16x16.png" 2>/dev/null
    sips -z 32 32    "$BASE_PNG" --out "$ICONSET_DIR/icon_16x16@2x.png" 2>/dev/null
    sips -z 32 32    "$BASE_PNG" --out "$ICONSET_DIR/icon_32x32.png" 2>/dev/null
    sips -z 64 64    "$BASE_PNG" --out "$ICONSET_DIR/icon_32x32@2x.png" 2>/dev/null
    sips -z 128 128  "$BASE_PNG" --out "$ICONSET_DIR/icon_128x128.png" 2>/dev/null
    sips -z 256 256  "$BASE_PNG" --out "$ICONSET_DIR/icon_128x128@2x.png" 2>/dev/null
    sips -z 256 256  "$BASE_PNG" --out "$ICONSET_DIR/icon_256x256.png" 2>/dev/null
    sips -z 512 512  "$BASE_PNG" --out "$ICONSET_DIR/icon_256x256@2x.png" 2>/dev/null
    sips -z 512 512  "$BASE_PNG" --out "$ICONSET_DIR/icon_512x512.png" 2>/dev/null
    sips -z 1024 1024 "$BASE_PNG" --out "$ICONSET_DIR/icon_512x512@2x.png" 2>/dev/null

    if iconutil -c icns "$ICONSET_DIR" -o "$APP_BUNDLE/Contents/Resources/app.icns" 2>/dev/null; then
        rm -rf "$ICONSET_DIR" "$BASE_PNG"
        echo "  图标已生成: app.icns"
    else
        rm -rf "$ICONSET_DIR" "$BASE_PNG"
        echo "  图标转换失败，使用默认图标"
    fi
else
    echo "  未找到 app.ico，跳过图标生成"
fi

# 确保可执行权限
chmod +x "$APP_BUNDLE/Contents/MacOS/$APP_NAME" 2>/dev/null || true

rm -rf "$PUBLISH_DIR/app-files"

# 重新签名 .app 包。
# 必须做：apphost 二进制自带一个残缺的 adhoc 签名（Identifier=apphost，
# Info.plist 未绑定，且声明了 resources 必须存在却没有 _CodeSignature/CodeResources）。
# 本地文件没有 quarantine 属性，Gatekeeper 不校验，所以双击能开；
# 一旦经浏览器/网站下载被加上 com.apple.quarantine，Gatekeeper 全量校验就会
# 判定签名损坏，弹出"已损坏，无法打开，你应该将它移到废纸篓"。
# 重签后会生成 Contents/_CodeSignature/CodeResources，签名与 bundle 一致。
echo -e "${YELLOW}[5/6] 为 .app 重新签名 (adhoc)...${NC}"
if codesign --force --deep --sign - "$APP_BUNDLE" 2>/dev/null; then
    if codesign --verify --strict "$APP_BUNDLE" 2>/dev/null; then
        echo -e "  签名完成并通过校验"
    else
        echo -e "  ${YELLOW}警告: 签名校验未通过${NC}"
    fi
else
    echo -e "  ${YELLOW}警告: codesign 失败（需要安装 Xcode Command Line Tools）${NC}"
fi

# ⚠️ 绝对不要对已签名的 bundle 执行 xattr -cr / xattr -c！
# codesign 会为 .app 内 233 个带可执行位的 .dll（Format=generic，非 Mach-O）
# 把嵌套签名写进扩展属性 com.apple.cs.CodeSignature，删掉 xattr 就等于
# 删掉这些签名，校验会立刻变成 "code object is not signed at all"。
# 同理，ZIP 里的 __MACOSX/._* 条目正是承载这些 xattr 的，不是垃圾，必须保留。
echo ""

echo -e "${YELLOW}[6/6] 生成 DMG 与 ZIP 分发包...${NC}"
# 创建 DMG
DMG_NAME="TextFlowPlus-1.0.0-osx-arm64.dmg"
DMG_PATH="$PUBLISH_DIR/$DMG_NAME"

# 创建临时 DMG 目录（用 ditto 而非 cp -R，确保签名 xattr 一并复制）
DMG_DIR="$PUBLISH_DIR/dmg-contents"
mkdir -p "$DMG_DIR"
ditto "$APP_BUNDLE" "$DMG_DIR/$APP_NAME.app"
# 创建 Applications 快捷方式
ln -sf /Applications "$DMG_DIR/Applications" 2>/dev/null || true

# 生成 DMG
hdiutil create -volname "TextFlowPlus" \
    -srcfolder "$DMG_DIR" \
    -ov -format UDZO \
    "$DMG_PATH" 2>/dev/null && \
    echo -e "  DMG 已生成: ${GREEN}$DMG_PATH${NC}" || \
    echo -e "  ${YELLOW}DMG 生成跳过（可能缺少 hdiutil 权限）${NC}"

rm -rf "$DMG_DIR"

# 生成 ZIP。必须用 ditto 且加 --sequesterRsrc：它会签名所需的 xattr
# (com.apple.cs.*) 存进 __MACOSX/._* 条目，解压时再还原。
# 直接用 zip 命令会解引用符号链接且丢失 xattr，导致解压后签名失效。
ZIP_PATH="$PUBLISH_DIR/$APP_NAME.zip"
rm -f "$ZIP_PATH"
if ditto -c -k --sequesterRsrc --keepParent "$APP_BUNDLE" "$ZIP_PATH" 2>/dev/null; then
    echo -e "  ZIP 已生成: ${GREEN}$ZIP_PATH${NC}"
else
    echo -e "  ${YELLOW}ZIP 生成失败${NC}"
fi
echo ""

# 自检：把 ZIP 解开再校验一次签名。
# 这一步专抓"打包/解压环节吃掉了签名 xattr"的问题——它只在解压后才暴露，
# 而用户正是通过网站下载解压后才触发"已损坏"。
echo -e "${YELLOW}[自检] 验证 ZIP 解压后的签名...${NC}"
VERIFY_DIR="$(mktemp -d)"
if [ -f "$ZIP_PATH" ] && ditto -x -k "$ZIP_PATH" "$VERIFY_DIR" 2>/dev/null && \
   codesign --verify --strict "$VERIFY_DIR/$APP_NAME.app" 2>/dev/null; then
    echo -e "  ZIP 解压后签名校验通过"
    rm -rf "$VERIFY_DIR"
else
    echo -e "  ${YELLOW}警告: ZIP 解压后签名校验失败！保留现场供排查: $VERIFY_DIR${NC}"
fi
echo ""

echo -e "${GREEN}============================================${NC}"
echo -e "${GREEN}  发布完成!${NC}"
echo -e "${GREEN}============================================${NC}"
echo ""
echo -e "  📦 .app 包:  ${GREEN}$APP_BUNDLE${NC}"
echo -e "  💿 DMG 镜像: ${GREEN}$DMG_PATH${NC}"
echo -e "  🗜  ZIP 包:  ${GREEN}$ZIP_PATH${NC}"
echo ""
echo -e "  应用大小: $(du -sh "$APP_BUNDLE" | cut -f1)"
echo ""
echo -e "  运行方式:"
echo -e "    open \"$APP_BUNDLE\""
echo -e "    或直接双击 DMG 中的 .app 图标"
echo ""
