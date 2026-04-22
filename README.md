# TextFlow

TextFlow 是一个基于 WPF 和 AvalonEdit 构建的多功能文本/代码编辑器，支持多标签页编辑、语法高亮、编码转换等功能。

## 编译环境

### 系统要求
- **操作系统**: Windows 10/11 (x64)
- **.NET SDK**: .NET 8.0 或更高版本
  - 下载地址：https://dotnet.microsoft.com/download/dotnet/8.0

### 开发工具（可选）
- Visual Studio 2022 / Visual Studio Code
- Rider 或其他支持 .NET 的 IDE

### 编译步骤

```bash
# 克隆或进入项目目录
cd TextFlow

# 还原依赖包
dotnet restore

# 编译项目
dotnet build

# 运行项目
dotnet run

# 发布独立可执行文件（包含运行时，无需安装 .NET）
dotnet publish -c Release
```

### 项目依赖

| 包名 | 版本 | 用途 |
|------|------|------|
| AvalonEdit | 6.3.0.90 | 文本编辑器核心控件 |
| Newtonsoft.Json | 13.0.3 | JSON 序列化和处理 |
| System.Text.Encoding.CodePages | 8.0.0 | 支持 GB2312 等编码 |

## 运行环境

### 最低要求
- **操作系统**: Windows 7 SP1 或更高版本
- **.NET 运行时**: 
  - 方式一：安装 .NET 8.0 Runtime
  - 方式二：使用已发布的独立版本（self-contained），无需额外安装运行时

### 运行方式

#### 1. 开发环境运行
```bash
dotnet run
```

#### 2. 发布后运行
```bash
# 发布为独立应用
dotnet publish -c Release -r win-x64 --self-contained

# 运行发布的程序
.\bin\Release\net8.0-windows7.0\win-x64\publish\TextFlow.exe
```

## 软件功能

### 文件操作
- 新建、打开、保存、另存为文件
- 支持同时打开多个文件（多标签页）
- 支持拖拽文件到窗口打开
- 支持大文件（最大 500MB）

### 编辑功能
- 撤销/重做
- 剪切/复制/粘贴
- 全选
- 查找（Ctrl+F）
- 替换（Ctrl+H）
- 代码格式化（支持 JSON、XAML、C#、C++、JavaScript、Python）

### 字体设置
- **字体选择**: 支持 Consolas、Courier New、Lucida Console 等 10 种字体
- **字号选择**: 支持 8-72 共 16 种预设字号，可手动输入自定义字号
- **缩放功能**: 支持 Ctrl++ 放大、Ctrl+- 缩小、Ctrl+0 重置

### 语法高亮
支持以下语言的语法高亮：
- C/C++
- C#
- Python
- JavaScript
- JSON
- XAML
- YAML
- Markdown
- 纯文本

### 编码支持
- 自动检测文件编码
- 支持多种编码格式：
  - UTF-8（带/不带 BOM）
  - UTF-16（Little/Big Endian）
  - UTF-32
  - GB2312/GBK（简体中文）
  - ANSI
- 编码转换功能

### 视图功能
- 工具栏显示/隐藏
- 状态栏显示/隐藏
- 分栏对比模式（同时查看两个文件）
- 显示/隐藏制表符、空格、行尾标记

### JSON 专用功能
- JSON 格式化（美化）
- JSON 压缩（最小化）
- JSON 语法检查（带错误位置提示）

### 快捷键

| 快捷键 | 功能 |
|--------|------|
| Ctrl+N | 新建文件 |
| Ctrl+O | 打开文件 |
| Ctrl+S | 保存文件 |
| Ctrl+Shift+S | 另存为 |
| Ctrl+W | 关闭当前标签 |
| Ctrl+Z | 撤销 |
| Ctrl+Y | 重做 |
| Ctrl+X | 剪切 |
| Ctrl+C | 复制 |
| Ctrl+V | 粘贴 |
| Ctrl+A | 全选 |
| Ctrl+F | 查找 |
| Ctrl+H | 替换 |
| Ctrl+K | 格式化代码 |
| Ctrl++ | 放大 |
| Ctrl+- | 缩小 |
| Ctrl+0 | 重置缩放 |

## 项目结构

```
TextFlow/
├── MainWindow.xaml          # 主窗口界面定义
├── MainWindow.xaml.cs       # 主窗口逻辑代码
├── App.xaml                 # 应用程序定义
├── EditorTab.cs             # 标签页数据模型
├── FindDialog.xaml          # 查找对话框
├── ReplaceDialog.xaml       # 替换对话框
├── TextFlow.csproj          # 项目文件
├── Resources/
│   └── Highlighting/        # 语法高亮定义文件
│       ├── JSON-Mode.xshd
│       ├── XAML-Mode.xshd
│       ├── Markdown-Mode.xshd
│       └── YAML-Mode.xshd
└── README.md                # 本文件
```

## 版本信息

- **当前版本**: v1.0
- **.NET 版本**: net8.0-windows7.0
- **AvalonEdit 版本**: 6.3.0.90

## 许可证
在法律法规允许的范围内本软件可以任意使用于二次开发、商业应用、学习研究等用途。
欢迎广大开发者在此基础上做更多功能的扩展开发。


## 原著作者
simon3032@126.com


