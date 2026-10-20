# TextFlowPlus · T+ Logo

支持 Agent 的多功能编辑器 TextFlowPlus 的品牌标识：**蓝框 T+**（透明底）。

> 历史说明：本软件曾用名 AIEditor，标识也经历过 `AIE`（字母锁形）阶段。
> 现已统一更名为 **TextFlowPlus**，并把图形标识重新设计为 **蓝描边方框内的 T+**。

## 设计概念

- **蓝色描边方框** —— 编辑器的"窗口/画布"：圆角正方形外框界定出一块编辑区域，
  框内保持透明，标识可直接叠加在任意底色上。
- **T —— 文本之柱**：一横一竖，既是 *Text* 的首字母，也是抽象的文本行与光标，
  位于框内左上方，是视觉主体。
- **+ —— 智能增益**：独立居于框内右下方，象征 AI 加持与能力叠加（*Plus*），
  同时是全图唯一的暖色强调。
- **分离版式**：T 与 + **不重叠**，在框内构成稳定的对角平衡，
  两个符号各自完整，缩到 32px 仍可辨认。

## 立体化处理

- **背景**：**透明**（框内也透明），不占用底色，可叠加在浅色或深色背景上；
- **T 笔画**：纵向渐变 —— 浅底用亮海蓝 → 深海洋蓝（`#4FAEF5 → #0B4EA2`），
  深底用白 → 淡靛（`#FFFFFF → #C7D2FE`），保证两种底色下都清晰；
- **+ 号**：琥珀三色渐变（`#FDE68A → #FBBF24 → #D97706`），全图唯一强调色；
- **外框**：海蓝对角线渐变描边（`#5CC0FF → #1E7BE0 → #0B4EA2`），圆角 `rx=104`。

> 注：全部文件均为**透明底**，且不叠加投影 —— 外框本身已提供足够的图形重量，
> 加投影反而会在小尺寸下糊成一团。

## 几何规格（512 视图坐标）

| 元素 | 参数 |
| --- | --- |
| 外框 | `rect x=30 y=30 w=452 h=452 rx=104`，stroke 30，无填充 |
| T 横笔 | `M104 152 H284`，stroke 50，圆头 |
| T 竖笔 | `M194 152 V352`，stroke 50，圆头 |
| + 竖笔 | `M368 266 V400`，stroke 38 |
| + 横笔 | `M301 333 H435`，stroke 38 |
| T 渐变范围 | `userSpaceOnUse 120 → 372` |
| + 渐变范围 | `userSpaceOnUse 266 → 400` |
| 背景 | 透明，无底板、无投影 |

**不重叠间距**：T 竖笔右缘 `x=219`，+ 横笔左缘 `x=282` —— 水平净距 **63**；
框内缘约 `x=75..437`、`y=75..437`，T 与 + 均完整落在框内。

笔画采用统一的圆头圆角（`stroke-linecap/linejoin: round`），小尺寸下不会出现尖角毛刺。

## 品牌色板

| 用途 | 色值 |
| --- | --- |
| 外框起（受光） | `#5CC0FF` |
| 外框中 | `#1E7BE0` |
| 外框止（深海蓝） | `#0B4EA2` |
| T（浅底） | `#4FAEF5 → #0B4EA2` |
| T（深底） | `#FFFFFF → #C7D2FE` |
| 琥珀（+ 号强调色） | `#FBBF24`（深底）/ `#B45309`（浅底收尾） |
| 深字（浅底字标） | `#0F172A` |

## 文件说明

| 文件 | 用途 |
| --- | --- |
| `icon.svg` | 应用图标（矢量源，512×512，蓝框 + 透明底） |
| `icon-512/256/192/128/64/32.png` | 应用/商店/网页图标各档位 |
| `mark-color.svg` / `mark-color.png` | 纯标识，浅色背景用 |
| `mark-white.svg` / `mark-white.png` | 纯标识，深色背景/水印用 |
| `lockup-light\|dark.svg` | 标识+「TextFlowPlus」字标（矢量） |
| `lockup-light\|dark.png` | 960×512；`@2x.png` 为 1920×1024 高清版 |
| `aie.ico` | 多尺寸 ICO（256/128/64/48/32/16，PNG 内嵌） |
| `render-logo.ps1` | 用 System.Drawing 重新渲染全部位图（无需外部依赖） |
| `preview.html` | 本地浏览器展示页 |

## 重新渲染

```powershell
pwsh -File .\render-logo.ps1
```

脚本源几何与各 SVG 完全一致（坐标体系见脚本内注释与上表），改色/改形状后重跑即可。
注意：SVG 与 `render-logo.ps1` 是**两套独立实现**，改动几何时须同步修改，否则矢量与位图会不一致。

## 接入应用（已完成）

新图标已替换到全部引用位置（`Assets\app.ico`、`uninstall.ico` 为 `aie.ico` 的副本）：

- Windows exe/任务栏图标：`TextFlowPlus.csproj` → `<ApplicationIcon>Assets\app.ico</ApplicationIcon>`（无需改动）
- NSIS 安装器/卸载器：`setup.nsi` 引用 `Assets/app.ico` 与 `uninstall.ico`
- macOS 图标：`publish-osx.sh` 现直接以 `Assets/AIELogo/icon-512.png` 为基准生成 `app.icns`（比从 ICO 转换更清晰）
- 网页 favicon：`icon-32.png` 或 `aie.ico`

> 注意：老版本曾指向 `Assets\textflow-icon.*` / `baize.*`，这些文件现已覆盖为新 logo，若后续需要保留旧设计请从 git 历史取回。