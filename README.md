# TextFlowPlus

> 基于 [Avalonia](https://avaloniaui.net/) 与 .NET 8 的跨平台 AI 代码编辑器 —— 内置 AI 助手、SSH 远程开发、Git 集成与常用开发工具箱。

**当前版本：1.0.0** ｜ 支持 Windows / macOS / Linux

> **关于改名**：本软件原名 **AIEditor**（因与同名开源项目冲突而改名），现已统一更名为
> **TextFlowPlus**。可执行文件名、解决方案/工程文件名、源码目录、安装包名、配置目录、
> 更新检查的产品标识、界面显示名**均已同步更新**；
> 为不影响老用户，程序启动时会**自动沿用旧的 `AIEditor` / `aieditor` 配置目录**（见
> [配置文件位置](#配置文件位置)），既有设置不会丢失。

---

## 目录

- [功能特性](#功能特性)
- [平台支持](#平台支持)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [发布与打包](#发布与打包)
- [使用说明](#使用说明)
- [项目结构](#项目结构)
- [技术栈](#技术栈)
- [常见问题](#常见问题)
- [已知问题](#已知问题)
- [许可证](#许可证)

---

## 功能特性
TextFlowPlus 1.0
跨平台多功能文本、代码编辑器；支持AI助手、语法着色、ssh远程连接、加密解密、文件对比、
文件对比、git仓库管理；支持多语言界面、支持打开远程文件夹、支持语音识别、语音合成和
OCR文字识别。您在使用过程中有任何问题或建议都可以通过关于菜单下的意见反馈进反馈。
(**由于是免费软件，没有做macos签名，所以macos下第一次打开会提示文件已损坏，
其实没有损坏。依次点击系统设置-->隐私与安全-->找到已阻止TextFlowPlus选项点击后面的
仍要打开就可以正常使用了。**)

### 代码编辑

| 能力 | 说明 |
| --- | --- |
| 多标签编辑 | 基于 AvaloniaEdit，支持切换、关闭、最近打开列表 |
| 语法高亮 | C#、C++、XAML、XML、JSON、YAML、Markdown、Go、Rust、Java、JavaScript、TypeScript、HTML、CSS、Python、Vue、Proto、NSIS、Log |
| 代码折叠 | 括号折叠与缩进折叠，可折叠/展开当前块或全部 |
| 查找替换 | 查找、替换、F3 查找下一个 |
| 编码处理 | 自动检测编码；支持 GB2312 / UTF-8 / UTF-8 with BOM 的保存与相互转换 |
| 富文本文档 | 打开/编辑 `.docx`、`.doc`、`.rtf`（基于 Avalonia Pro RichTextEditor）；另可查看常见图片格式 |
| 编辑增强 | 选中匹配高亮、错别字高亮、文本标记、跳转行、缩放 |
| 代码格式化 | 按文件类型自动选择格式化方式；也可交由大模型格式化 |
| 主题 | 浅色 / 深色主题切换 |

### AI 助手

- 可停靠面板，基于 `Microsoft.Agents.AI` 的 `ChatClientAgent` 实现
- **流式交互**：回答逐字实时显示（打字机效果），无需等待整段生成完成
- **思考过程实时展示**：模型输出推理链时，以可折叠的「🧠 思考中…」区块实时滚动展示；
  标题栏 🧠 按钮可随时开关，选择会随配置持久化
- **随时停止**：生成过程中「发送」按钮切换为「停止」，点击即中断并保留已生成内容
- 后端支持 **Ollama**（本地部署）或任意 **OpenAI 兼容网关**（填写 API Key 即可）
- 用自然语言直接修改当前打开的文档，例如：
  - `把所有 foo 替换成 bar`
  - `删除第 5-7 行`
  - `把第 3 行改成 xxx`
  - `帮我修正这段代码`
- 内置**本地命令**（不消耗模型额度）：新建 / 打开 / 保存文件、文件对比、哈希计算、加密解密、格式化当前文档；输入 `help` 查看全部命令

> **关于思考过程**：能否看到推理链取决于所连的模型/网关。像 **deepseek-r1、qwen3** 这类
> 默认就返回 `reasoning_content` 的模型可直接显示；若模型未输出思考内容，思考区自动隐藏，
> 不会显示空白框，也不影响正常对话。当前固定的 SDK 版本（`Microsoft.Extensions.AI.OpenAI`
> 9.6.0-preview）不会把"请求思考"的参数发给服务端，因此无法为需要显式开启的模型强制打开思考。

### SSH 远程开发

- SSH 交互式终端，支持 ANSI 转义与彩色输出
- 远程文件夹浏览，远程文件打开 / 编辑 / 保存
- SFTP 上传与下载（支持文件与文件夹）
- SSH 端口转发
- 连接信息持久化、命令历史、终端配色自定义、密码加密存储

### 终端

- 内置本地终端

### Git 集成

- 修改文件列表面板
- 差异查看

### 开发工具箱

| 工具 | 说明 |
| --- | --- |
| 文件对比 | 左右并排差异视图，支持双向合并 |
| 文件夹对比 | 目录级差异比较 |
| 信息摘要计算 | 哈希 / 摘要计算 |
| 加密 / 解密 | 加解密工具 |
| HTTP 协议调试 | HTTP 请求构造与响应查看，JSON 格式化与压缩 |
| OCR 文字识别 | Windows 专属（PaddleOCR）※ |
| 截屏 OCR | Windows 专属 ※ |
| 语音转文本 | Windows 专属（NAudio + RNNoise + ten_vad）※ |

> ※ 见 [已知问题](#已知问题)。

### 界面与本地化

- 界面语言：**中文 / English / 日本語**
- 单实例运行：重复启动会把文件路径转交给已运行实例
- 支持命令行传入文件/文件夹，以及「打开方式 → TextFlowPlus」

---

## 平台支持

| 功能 | Windows | macOS | Linux |
| --- | :---: | :---: | :---: |
| 编辑 / AI 助手 / 终端 / Git / 工具箱 | ✅ | ✅ | ✅ |
| SSH 远程开发 | ✅ | ✅ | ✅ |
| 富文本文档（.docx / .rtf / .doc） | ✅ | ✅ | ✅ |
| OCR / 截屏 OCR / 语音转文本 | ⚠️ ※ | ❌ | ❌ |

跨平台功能在三大平台一致；语音输入与 OCR 依赖 Windows 专属原生组件，非 Windows 平台不参与编译。

---

## 环境要求

### .NET SDK —— 请注意版本

> **必须使用 .NET 10 SDK，或 .NET SDK 9.0.3xx 及以上。**

原因是本项目使用 Avalonia 12.1.2，其 XAML **源码生成器要求编译器 Roslyn ≥ 4.14**。
系统若只装了 .NET 8 SDK（Roslyn 4.8），生成器会被编译器静默跳过，导致
`InitializeComponent` 与所有 `x:Name` 字段都不生成，编译时抛出**成百上千个 `CS0103`**：

```
error CS0103: 当前上下文中不存在名称“InitializeComponent”
error CS0103: 当前上下文中不存在名称“FileTabControl”
```

检查当前 SDK 是否可用：

```bash
dotnet --list-sdks          # 需要 10.x 或 9.0.3xx+
```

### 系统要求

| 平台 | 要求 |
| --- | --- |
| Windows | Windows 10 及以上 |
| macOS | macOS 12 及以上 |
| Linux | Ubuntu 22.04 及以上（或其他带 X11/Wayland 的发行版） |

Linux 运行期需要系统图形库：

```bash
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libfreetype6 \
                 libxext6 libxrandr2 libxi6 libxcursor1 libgl1 libegl1
```

---

## 快速开始

```bash
# 1. 获取源码
git clone git@gitee.com:actci/TextFlowPlus.git
cd TextFlowPlus

# 2. 编译（Debug）
dotnet build src/TextFlowPlus/TextFlowPlus.csproj -c Debug

# 3. 运行
dotnet run --project src/TextFlowPlus/TextFlowPlus.csproj
```

编译产物位于 `src/TextFlowPlus/bin/<配置>/net8.0/<RID>/`（自包含，可直接执行）：

```bash
./src/TextFlowPlus/bin/Debug/net8.0/linux-x64/TextFlowPlus
```

也可以传入文件或文件夹直接打开：

```bash
TextFlowPlus /path/to/file.cs
TextFlowPlus /path/to/project-dir
```

---

## 发布与打包

三个平台各自提供打包脚本 / 工程。

### Ubuntu

```bash
./publish-ubuntu.sh                    # 发布 Release（自包含）+ 打包 tar.gz
./publish-ubuntu.sh --rid linux-arm64  # 指定目标架构（默认按本机识别）
./publish-ubuntu.sh --version 1.1.0    # 指定包版本号
./publish-ubuntu.sh --no-package       # 只发布，不打包
./publish-ubuntu.sh --sdk ~/.dotnet10  # 指定 .NET SDK 安装目录
./publish-ubuntu.sh --no-auto-sdk      # SDK 不满足要求时不自动下载
./publish-ubuntu.sh --clean            # 清理发布 / 打包产物
```

产物：

```
publish/<rid>/                              自包含发布目录
dist/textflowplus-<版本>-linux-<架构>.tar.gz    发布包（内含 install.sh）
```

脚本会自动检测 SDK 的 Roslyn 版本；若系统 SDK 过旧，默认自动下载 .NET 10 SDK 到 `~/.dotnet`。

安装包内提供安装脚本：

```bash
tar -xzf dist/textflowplus-1.0.0-linux-x64.tar.gz
cd textflowplus-1.0.0-linux-x64
sudo ./install.sh          # 安装到 /usr/local/textflowplus，并创建应用菜单项
./install.sh --user        # 免 root，安装到 ~/.local
sudo ./install.sh --uninstall
```

### macOS

```bash
./publish-osx.sh           # 生成自包含的 TextFlowPlus.app 与 DMG
```

### Windows

自包含发布后使用 `src/TextFlowPlus/setup.nsi`（NSIS）生成安装程序。

---

## 使用说明

### AI 模型配置

打开 AI 助手面板 → **模型设置**：

| 项目 | 说明 |
| --- | --- |
| 接口地址 | Ollama 地址形如 `http://host:11434`；填入 API Key 时按 OpenAI 兼容协议连接 |
| API Key | 使用 OpenAI 兼容网关时填写；纯 Ollama 可留空 |
| 模型名 | 留空则使用默认模型 |

> 标题栏 **🧠** 按钮用于开关思考过程显示，无需在这里配置；该选择保存在同一配置文件里。

默认配置为 `http://8.130.49.113:11435` + `qwen2.5:3b`（演示用）。**建议改为自己的 Ollama 或网关地址**，配置保存在：

```
<LocalApplicationData>/TextFlowPlus/agent_settings.json
```

### 快捷键

| 快捷键 | 功能 | 快捷键 | 功能 |
| --- | --- | --- | --- |
| `Ctrl+N` | 新建 | `Ctrl+F` | 查找 |
| `Ctrl+O` | 打开 | `Ctrl+H` | 替换 |
| `Ctrl+Shift+O` | 打开文件夹 | `F3` | 查找下一个 |
| `Ctrl+S` | 保存 | `Ctrl+K` | 格式化代码 |
| `Ctrl+Shift+S` | 另存为 | `Ctrl+G` | 跳转行 |
| `Ctrl+W` | 关闭标签 | `Ctrl+Shift+T` | 终端 |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 | `Ctrl++` / `Ctrl+-` / `Ctrl+0` | 放大 / 缩小 / 重置缩放 |
| `Ctrl+X` / `Ctrl+C` / `Ctrl+V` / `Ctrl+A` | 剪切 / 复制 / 粘贴 / 全选 | | |
| `Ctrl+Shift+[` / `Ctrl+Shift+]` | 折叠 / 展开当前块 | `Ctrl+Alt+[` / `Ctrl+Alt+]` | 折叠 / 展开所有块 |

### 配置文件位置

| 内容 | 位置 |
| --- | --- |
| AI 模型配置 | `<LocalApplicationData>/TextFlowPlus/agent_settings.json` |
| SSH 会话 | `<LocalApplicationData>/TextFlowPlus/ssh_sessions.json` |
| SSH 最近主机 | `<LocalApplicationData>/TextFlowPlus/ssh_recent_hosts.json` |
| 终端外观 | `<LocalApplicationData>/TextFlowPlus/terminal_appearance.json` |
| 远程连接（含密码，加密存储） | `~/TextFlowPlus/remote_connections.json` |

> `<LocalApplicationData>` 在 Windows 为 `%LOCALAPPDATA%`，Linux 为 `~/.local/share`，macOS 为 `~/Library/Application Support`。
>
> **升级兼容**：若上述 `TextFlowPlus` 目录尚不存在、而改名前的 `AIEditor`（或用户主目录下的
> `aieditor`）目录存在，程序会继续读写旧目录，因此从 AIEditor 升级上来的用户不会丢失
> 主题、语言、AI 模型配置、SSH 会话与远程连接信息。如需彻底迁移到新目录，将旧目录整体改名即可。

---

## 项目结构

```
TextFlowPlus/
├── TextFlowPlus.sln
├── publish-ubuntu.sh              # Ubuntu 发布打包脚本
├── publish-osx.sh                 # macOS 发布打包脚本
├── packaging/
│   └── install.sh.in              # Linux 安装脚本模板（打包时生成 install.sh）
├── RequirementDoc.md
└── src/TextFlowPlus/
    ├── TextFlowPlus.csproj
    ├── Program.cs                 # 入口：单实例仲裁 + Avalonia 启动
    ├── App.axaml(.cs)             # 应用级资源、主题、样式
    ├── app.manifest
    ├── setup.nsi                  # Windows NSIS 安装脚本
    ├── 软件许可协议.rtf
    ├── Assets/
    │   ├── AIELogo/               # 应用图标（多尺寸 PNG）
    │   ├── Highlighting/          # 自定义语法高亮定义（*.xshd）
    │   └── Models/ten-vad/        # 语音活动检测原生库（Windows）
    ├── Models/                    # 编辑器数据模型（标签页、差异、标记、颜色化器）
    ├── Services/
    │   ├── Git/                   # Git 集成
    │   ├── Localization/          # 多语言（中文/English/日本語）
    │   ├── Ssh/                   # SSH 会话、终端、SFTP、端口转发
    │   ├── Terminal/              # 本地终端
    │   └── Voice/                 # 语音输入（Windows 专属）
    └── Views/                     # 主窗口、各对话框与面板
```

---

## 技术栈

| 组件 | 用途 |
| --- | --- |
| [Avalonia](https://avaloniaui.net/) 12.1.2 | 跨平台 UI 框架 |
| .NET 8 | 目标运行时（自包含发布） |
| AvaloniaEdit | 代码编辑器控件 |
| Avalonia.Controls.RichTextEditor | 富文本文档（Avalonia Pro，见下） |
| Avalonia.Controls.DataGrid | 表格控件 |
| SkiaSharp | 2D 渲染 |
| SSH.NET | SSH / SFTP |
| Microsoft.Agents.AI + Microsoft.Extensions.AI | AI 助手 |
| Newtonsoft.Json | JSON 序列化 |
| BouncyCastle.Cryptography | 加解密 |
| PaddleOCRSharp | OCR（Windows 专属） |
| NAudio / RNNoise / ten_vad | 语音输入（Windows 专属） |

---

## 常见问题

**Q：编译报大量 `CS0103`（找不到 `InitializeComponent`、`x:Name` 字段）？**

SDK 版本过低。这是 Avalonia 12.1.2 源码生成器对编译器版本的要求导致的，请升级到 .NET 10 SDK（或 9.0.3xx+），详见 [环境要求](#环境要求)。

**Q：Linux 上安装后菜单/顶栏显示默认齿轮图标？**

GTK 会信任 `hicolor` 目录下已存在的 `icon-theme.cache`；若安装图标后未刷新缓存，按名字就查不到图标。`install.sh` 已会自动调用 `gtk-update-icon-cache`。若你用的是旧包，手动执行一次：

```bash
sudo gtk-update-icon-cache -f -t /usr/share/icons/hicolor
```

然后重启程序（必要时注销重新登录，让 GNOME Shell 刷新应用图标缓存）。

**Q：Linux 上程序起不来？**

先检查动态库是否缺失：

```bash
ldd /usr/local/textflowplus/TextFlowPlus | grep "not found"
```

按 [环境要求](#环境要求) 安装对应图形库。

**Q：如何切换界面语言？**

菜单 **视图 (_V) → 主题** 下有 **中文 / 英文 / 日语** 三项。
（菜单 **语言 (_L)** 是当前文档的语法模式，用于切换高亮语言与 JSON 工具，不是界面语言。）

---

## 已知问题

- **OCR 与语音转文本未生效（含 Windows）**：`Views/OCRDialog.axaml.cs`、`Views/ScreenshotSelectionWindow.axaml.cs`、`Views/SpeechToTextDialog.axaml.cs` 中的实现被 `#if WINDOWS` 包裹，但工程只在 Windows 上定义了 `VOICE_INPUT` 常量，**从未定义 `WINDOWS`**，因此这些代码路径在所有平台都不会被编译。若要启用，需在 `TextFlowPlus.csproj` 的 Windows 条件中一并定义 `WINDOWS`。
- **富文本控件许可**：`Avalonia.Controls.RichTextEditor` 属 Avalonia Pro 控件，个人 / 非商业用途免费，**商业使用需购买许可**。未配置 `AvaloniaUILicenseKey` 时，Docx / Rtf 序列化器会在打开或保存时抛异常。

---

## 许可证

本项目为专有软件，许可条款详见 [`src/TextFlowPlus/软件许可协议.rtf`](src/TextFlowPlus/软件许可协议.rtf)。

第三方组件（Avalonia、AvaloniaEdit、SSH.NET、SkiaSharp、PaddleOCR、NAudio 等）遵循其各自的许可协议；其中 Avalonia Pro 系列控件（RichTextEditor 及其文档序列化器）商用需单独授权。
