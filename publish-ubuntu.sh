#!/usr/bin/env bash
# =============================================================================
#  TextFlowPlus —— Ubuntu 发布 / 打包脚本（自包含发布）
#
#  用法：
#    ./publish-ubuntu.sh                    发布 Release（自包含）+ 打包 tar.gz
#    ./publish-ubuntu.sh --rid linux-arm64  指定目标架构（默认按本机自动识别）
#    ./publish-ubuntu.sh --version 1.1.0    指定包版本号（默认取 git tag / csproj）
#    ./publish-ubuntu.sh --no-package       只发布，不打包
#    ./publish-ubuntu.sh --clean            清理 publish/ dist/ 产物
#    ./publish-ubuntu.sh --help             查看帮助
#
#  可选参数：
#    --sdk <目录>      指定 .NET SDK 安装目录（例如 $HOME/.dotnet10）
#    --no-auto-sdk     未找到合适 SDK 时不自动下载
#
#  产物：
#    publish/<rid>/                              自包含发布目录
#    dist/textflowplus-<版本>-linux-<架构>.tar.gz    发布包（内含 install.sh）
#
#  说明：
#    - 以「自包含」方式发布，目标机无需安装 .NET 运行时；
#    - 本工程使用 Avalonia 12.1.2，其 XAML 源码生成器要求编译器
#      Roslyn >= 4.14（即 .NET SDK 10，或 9.0.3xx 及以上）。若系统只有
#      .NET 8 SDK（Roslyn 4.8），生成器会被静默跳过并报出成百上千个
#      CS0103，因此本脚本会先检测 SDK，必要时（默认）自动下载合适的 SDK；
#    - Windows 专属功能（语音输入 / PaddleOCR / NAudio）不参与 Linux 构建。
# =============================================================================

set -euo pipefail

# ----------------------------------------------------------------------------
# 常量
# ----------------------------------------------------------------------------
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
cd "$SCRIPT_DIR"

readonly PROJECT_FILE="$SCRIPT_DIR/src/TextFlowPlus/TextFlowPlus.csproj"
readonly TFM="net8.0"
readonly APP_ID="textflowplus"                 # 安装目录名 / 命令名 / 桌面项文件名
readonly APP_NAME="TextFlowPlus"               # 桌面项显示名（英文）
readonly APP_NAME_ZH="TextFlowPlus 编辑器"      # 桌面项显示名（中文）
readonly PUBLISH_ROOT="$SCRIPT_DIR/publish"
readonly DIST_ROOT="$SCRIPT_DIR/dist"
readonly STAGE_ROOT="$PUBLISH_ROOT/.package"   # 暂存目录（位于已忽略的 publish/ 下）
readonly INSTALL_TEMPLATE="$SCRIPT_DIR/packaging/install.sh.in"
readonly ICON_SOURCE="$SCRIPT_DIR/src/TextFlowPlus/Assets/AIELogo/icon-256.png"
readonly REQUIRED_ROSLYN="4.14"            # Avalonia 12.1.2 生成器所需的最低编译器版本

[[ -f "$PROJECT_FILE" ]] || { echo "未找到项目文件：$PROJECT_FILE" >&2; exit 1; }

# 可执行文件名 = csproj 的 AssemblyName
EXE_NAME="$(sed -n 's:.*<AssemblyName>[[:space:]]*\([^<]*[^[:space:]]\)[[:space:]]*</AssemblyName>.*:\1:p' \
    "$PROJECT_FILE" 2>/dev/null | head -n 1 || true)"
EXE_NAME="${EXE_NAME:-TextFlowPlus}"
readonly EXE_NAME

# ---------------------------------------------------------------- 颜色与输出 --
if [[ -t 1 ]]; then
    C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
    C_CYAN=$'\033[36m'; C_BOLD=$'\033[1m'; C_RESET=$'\033[0m'
else
    C_RED=""; C_GREEN=""; C_YELLOW=""; C_CYAN=""; C_BOLD=""; C_RESET=""
fi

info() { printf '%s[信息]%s %s\n' "$C_CYAN"   "$C_RESET" "$*"; }
ok()   { printf '%s[完成]%s %s\n' "$C_GREEN"  "$C_RESET" "$*"; }
warn() { printf '%s[警告]%s %s\n' "$C_YELLOW" "$C_RESET" "$*" >&2; }
step() { printf '\n%s%s==> %s%s\n' "$C_BOLD" "$C_CYAN" "$*" "$C_RESET"; }
die()  { printf '%s[错误]%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

# 帮助内容直接取自本文件顶部的注释块，避免两处维护
usage() {
    awk '/^# ==========/{n++; if (n==2) {print; exit}} n>=1' "${BASH_SOURCE[0]}" \
        | sed 's/^# \{0,1\}//'
}

# ------------------------------------------------------------------- 工具函数 --
detect_rid() {
    case "$(uname -m)" in
        x86_64|amd64)  printf 'linux-x64' ;;
        aarch64|arm64) printf 'linux-arm64' ;;
        *) die "不支持的 CPU 架构：$(uname -m)（仅支持 x86_64 / aarch64，可用 --rid 手动指定）" ;;
    esac
}

# 版本号：git tag > csproj <FileVersion> > 1.0.0
detect_version() {
    local v=""
    v="$(git -C "$SCRIPT_DIR" describe --tags --abbrev=0 2>/dev/null || true)"
    if [[ -z "$v" ]]; then
        v="$(sed -n 's:.*<FileVersion>[[:space:]]*\([^<]*[^[:space:]]\)[[:space:]]*</FileVersion>.*:\1:p' \
            "$PROJECT_FILE" 2>/dev/null | head -n 1 || true)"
    fi
    printf '%s' "${v#v}"
}

# 取某个 dotnet 所带「最高版本 SDK」的 Roslyn(major.minor)，如 5.9
# 用法：sdk_roslyn_mm <dotnet 可执行文件路径>
sdk_roslyn_mm() {
    local bin="$1" sdk_line ver root csc out
    sdk_line="$("$bin" --list-sdks 2>/dev/null | sort -V | tail -n 1 || true)"
    [[ -n "$sdk_line" ]] || return 1
    ver="${sdk_line%% *}"
    root="$(printf '%s' "$sdk_line" | sed -n 's/.*\[\(.*\)\][[:space:]]*$/\1/p')"
    [[ -n "$root" ]] || return 1
    csc="$root/$ver/Roslyn/bincore/csc.dll"
    [[ -f "$csc" ]] || return 1
    out="$("$bin" "$csc" -version 2>/dev/null || true)"
    [[ -n "$out" ]] || return 1
    printf '%s' "${out%% *}" | sed -E 's/^([0-9]+)\.([0-9]+).*/\1.\2/'
}

# 比较 major.minor：version_ge 5.9 4.14  → 成立返回 0
version_ge() {
    local am an bm bn
    am="${1%%.*}"; an="${1#*.}"; an="${an%%.*}"
    bm="${2%%.*}"; bn="${2#*.}"; bn="${bn%%.*}"
    if (( am > bm )); then return 0; fi
    if (( am < bm )); then return 1; fi
    (( an >= bn )) || return 1
    return 0
}

# 候选 .NET 安装根目录（去重）
dotnet_candidates() {
    local -a cands=()
    [[ -n "${SDK_OVERRIDE:-}" ]] && cands+=("$SDK_OVERRIDE")
    [[ -n "${DOTNET_ROOT:-}" ]] && cands+=("$DOTNET_ROOT")
    cands+=("$HOME/.dotnet" "/usr/lib/dotnet" "/usr/share/dotnet" "/usr/local/share/dotnet")
    if command -v dotnet >/dev/null 2>&1; then
        local real
        real="$(readlink -f "$(command -v dotnet)" 2>/dev/null || command -v dotnet)"
        cands+=("$(dirname "$real")")
    fi
    printf '%s\n' "${cands[@]}" | awk 'NF && !seen[$0]++'
}

# 自动下载安装 .NET 10 SDK（Roslyn 5.x，满足要求）
auto_install_sdk() {
    local dir="$HOME/.dotnet" tmp installer
    step "未检测到 Roslyn >= $REQUIRED_ROSLYN 的 .NET SDK，自动下载安装 .NET 10 SDK"
    info "安装目录：$dir（约 240 MB，仅首次需要；可用 --no-auto-sdk 关闭）"

    tmp="$(mktemp -d)"
    installer="$tmp/dotnet-install.sh"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer" \
            || die "下载 dotnet-install.sh 失败，请检查网络或用 --sdk 指定已有 SDK"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$installer" https://dot.net/v1/dotnet-install.sh \
            || die "下载 dotnet-install.sh 失败，请检查网络或用 --sdk 指定已有 SDK"
    else
        die "缺少 curl/wget，无法自动下载 SDK。请手动安装 .NET 10 SDK，或用 --sdk 指定已有 SDK。"
    fi

    bash "$installer" --channel 10.0 --install-dir "$dir" || die "安装 .NET SDK 失败"
    rm -rf "$tmp"
    ok "SDK 安装完成：$dir"
}

# 挑选可用的 dotnet（Roslyn >= REQUIRED_ROSLYN）
DOTNET_BIN=""
resolve_dotnet() {
    local cand bin mm
    while IFS= read -r cand; do
        bin="$cand/dotnet"
        [[ -x "$bin" ]] || continue
        mm="$(sdk_roslyn_mm "$bin" || true)"
        [[ -n "$mm" ]] || continue
        if version_ge "$mm" "$REQUIRED_ROSLYN"; then
            DOTNET_BIN="$bin"
            info "使用 .NET SDK：$bin（Roslyn $mm）"
            return 0
        fi
        warn "跳过 $bin：自带 Roslyn $mm，低于 Avalonia 12.1.2 生成器要求的 $REQUIRED_ROSLYN"
    done < <(dotnet_candidates)
    return 1
}

# --------------------------------------------------------------------- 动作 --
# 发布（自包含）
do_publish() {
    step "发布 Release（自包含，$RID）"
    rm -rf "$PUBLISH_ROOT/$RID"
    "$DOTNET_BIN" publish "$PROJECT_FILE" \
        -c Release \
        -r "$RID" \
        --self-contained true \
        --nologo \
        -o "$PUBLISH_ROOT/$RID" \
        || die "发布失败（配置 Release，RID $RID）"

    local exe="$PUBLISH_ROOT/$RID/$EXE_NAME"
    [[ -f "$exe" ]] || die "发布目录中未找到可执行文件：$exe"
    chmod +x "$exe"
    ok "发布完成：$PUBLISH_ROOT/$RID（$(du -sh "$PUBLISH_ROOT/$RID" | cut -f1)）"
}

# 打包成 tar.gz（内含 install.sh / 图标 / 说明）
do_package() {
    local ver="$1"
    [[ -f "$INSTALL_TEMPLATE" ]] || die "缺少安装脚本模板：$INSTALL_TEMPLATE"
    [[ -f "$ICON_SOURCE" ]]      || die "缺少图标文件：$ICON_SOURCE"

    local arch="${RID#linux-}"
    local pkg_name="textflowplus-$ver-linux-$arch"
    local pkg_dir="$STAGE_ROOT/$pkg_name"

    step "打包：$pkg_name.tar.gz"
    rm -rf "$pkg_dir"
    mkdir -p "$pkg_dir/app" "$pkg_dir/icons"
    cp -a "$PUBLISH_ROOT/$RID/." "$pkg_dir/app/"
    cp -a "$ICON_SOURCE" "$pkg_dir/icon.png"

    # 多尺寸图标：install.sh 按尺寸装进 hicolor/<N>x<N>/apps（HiDPI 下更清晰）
    local size src
    for size in 32 64 128 192 256 512; do
        src="$SCRIPT_DIR/src/TextFlowPlus/Assets/AIELogo/icon-$size.png"
        if [[ -f "$src" ]]; then
            cp -a "$src" "$pkg_dir/icons/$size.png"
        fi
    done

    # 由模板生成包内安装脚本（占位符替换）
    sed -e "s|@APP_ID@|$APP_ID|g" \
        -e "s|@APP_NAME@|$APP_NAME|g" \
        -e "s|@APP_NAME_ZH@|$APP_NAME_ZH|g" \
        -e "s|@EXE_NAME@|$EXE_NAME|g" \
        -e "s|@APP_VERSION@|$ver|g" \
        -e "s|@BUNDLE_RID@|$RID|g" \
        -e "s|@TFM@|$TFM|g" \
        "$INSTALL_TEMPLATE" > "$pkg_dir/install.sh"
    chmod 755 "$pkg_dir/install.sh"

    cat > "$pkg_dir/README-install.md" <<EOF
# $APP_NAME（$APP_NAME_ZH）Ubuntu 安装包

版本：$ver    架构：$RID    发布日期：$(date +%F)

## 安装

    tar -xzf $pkg_name.tar.gz
    cd $pkg_name
    sudo ./install.sh          # 安装到 /usr/local/$APP_ID，并创建应用菜单项

安装完成后可从「应用程序」菜单启动，或在终端执行 $APP_ID。

## 常用命令

    sudo ./install.sh --prefix /opt/$APP_ID   # 自定义安装目录
    ./install.sh --user                       # 免 root，安装到 ~/.local（仅当前用户可用）
    sudo ./install.sh --uninstall             # 卸载程序本体与菜单项
    ./install.sh --help                       # 查看全部选项

## 运行期依赖

程序自带 .NET 运行时，目标机**无需**安装 .NET。但需要系统图形库：

    sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libfreetype6 \\
                     libxext6 libxrandr2 libxi6 libxcursor1 libgl1 libegl1

缺少图形库时程序无法启动，可用 ldd 查看缺失项：

    ldd /usr/local/$APP_ID/$EXE_NAME | grep "not found"

## 功能说明

- Linux 构建**不含**语音输入与 PaddleOCR（两者为 Windows 专属，依赖 NAudio /
  ten_vad.dll / PaddleOCR.dll）；
- 其余功能（编辑、SSH、终端、Git、AI 助手等）跨平台可用。
EOF

    mkdir -p "$DIST_ROOT"
    rm -f "$DIST_ROOT/$pkg_name.tar.gz"
    tar -czf "$DIST_ROOT/$pkg_name.tar.gz" -C "$STAGE_ROOT" "$pkg_name"
    rm -rf "$STAGE_ROOT"

    local size
    size="$(du -h "$DIST_ROOT/$pkg_name.tar.gz" | cut -f1)"
    ok "发布包：$DIST_ROOT/$pkg_name.tar.gz（$size）"
    printf '\n%s安装方法：%s\n' "$C_BOLD" "$C_RESET"
    printf '    tar -xzf %s/%s.tar.gz\n' "$DIST_ROOT" "$pkg_name"
    printf '    cd %s && sudo ./install.sh\n\n' "$pkg_name"
}

# 清理产物
do_clean() {
    step "清理发布 / 打包产物"
    rm -rf "$PUBLISH_ROOT" "$DIST_ROOT"
    ok "已清理：publish/ dist/"
}

# ------------------------------------------------------------------ 主流程 --
ACTION="publish"      # publish | clean
PACKAGE=1
RID=""
VERSION=""
SDK_OVERRIDE="${TEXTFLOWPLUS_DOTNET:-}"
AUTO_SDK=1

while (($#)); do
    case "$1" in
        -h|--help)      usage; exit 0 ;;
        --no-package)   PACKAGE=0 ;;
        --clean)        ACTION="clean" ;;
        --rid)          shift; [[ $# -gt 0 ]] || die "--rid 需要一个值（如 linux-x64）"; RID="$1" ;;
        --rid=*)        RID="${1#*=}" ;;
        --version)      shift; [[ $# -gt 0 ]] || die "--version 需要一个值（如 1.1.0）"; VERSION="$1" ;;
        --version=*)    VERSION="${1#*=}" ;;
        --sdk)          shift; [[ $# -gt 0 ]] || die "--sdk 需要一个目录路径"; SDK_OVERRIDE="$1" ;;
        --sdk=*)        SDK_OVERRIDE="${1#*=}" ;;
        --no-auto-sdk)  AUTO_SDK=0 ;;
        *)              die "未知参数：$1（使用 --help 查看用法）" ;;
    esac
    shift
done

RID="${RID:-$(detect_rid)}"

if [[ "$ACTION" == "clean" ]]; then
    do_clean
    exit 0
fi

VERSION="${VERSION#v}"
[[ -n "$VERSION" ]] || VERSION="$(detect_version)"
[[ -n "$VERSION" ]] || VERSION="1.0.0"

step "准备环境"
info "项目：$PROJECT_FILE"
info "目标：$RID    版本：$VERSION"

if ! resolve_dotnet; then
    if (( AUTO_SDK )); then
        auto_install_sdk
        resolve_dotnet || die "自动安装后仍未找到可用的 .NET SDK。请用 --sdk <目录> 手动指定。"
    else
        die "未找到 Roslyn >= $REQUIRED_ROSLYN 的 .NET SDK（已按 --no-auto-sdk 跳过自动下载）。
    原因：Avalonia 12.1.2 的 XAML 源码生成器需要 Roslyn >= 4.14，
          仅安装 .NET 8 SDK（Roslyn 4.8）会导致生成器被跳过并报大量 CS0103。
    解决：安装 .NET 10 SDK —— https://dotnet.microsoft.com/download
          或指定已有 SDK：./publish-ubuntu.sh --sdk \$HOME/.dotnet10"
    fi
fi

do_publish
(( PACKAGE )) && do_package "$VERSION"

printf '\n%s发布结束。%s\n' "$C_BOLD" "$C_RESET"
