using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

/// <summary>行级差异类型：未变 / 仅旧侧（删除）/ 仅新侧（新增）。</summary>
public enum DiffLineKind
{
    Unchanged,
    Removed,
    Added
}

/// <summary>一个差异块（连续差异区）：块首行号（1-based，无对应行 = -1）与块内行数。</summary>
public sealed record DiffBlock(int OldLine, int NewLine, int OldCount, int NewCount);

/// <summary>
/// Git 修改文件对比视图（VS2022 风格：左侧修改前 HEAD 版本、右侧修改后工作区版本）。
/// 行级差异用 LCS 回溯计算，两侧对应行按 删除(红)/新增(绿) 底色标记；
/// 顶部提供 上一差异/下一差异 导航与差异计数。
/// </summary>
public partial class DiffView : UserControl
{
    /// <summary>导航结果反馈（MainWindow 注入状态栏）。</summary>
    public event Action<string>? StatusReported;

    /// <summary>被对比文件的完整路径（用于"同一文件的对比标签页只开一个"去重）。</summary>
    public string FilePath { get; }

    private readonly List<DiffBlock> _blocks = new();
    private int _currentIndex = -1;
    private readonly NavHighlightColorizer _navOld = new();
    private readonly NavHighlightColorizer _navNew = new();

    public DiffView(string fileName, string headContent, string workContent, string filePath = "")
    {
        InitializeComponent();

        FilePath = filePath;
        FileNameText.Text = fileName;

        var oldLines = SplitLines(headContent);
        var newLines = SplitLines(workContent);
        var (oldKinds, newKinds, blocks) = DiffLines(oldLines, newLines);
        _blocks.AddRange(blocks);

        var removed = oldKinds.Count(k => k == DiffLineKind.Removed);
        var added = newKinds.Count(k => k == DiffLineKind.Added);
        ChangeSummaryText.Text = added > 0 || removed > 0
            ? LangService.TFormat("+{0}  -{1}  行", added, removed)
            : LangService.T("文件无差异");

        LeftEditor.Document = new TextDocument(headContent.Length == 0 ? LangService.T("（新增文件：HEAD 中不存在此文件）") : headContent);
        RightEditor.Document = new TextDocument(workContent.Length == 0 ? LangService.T("（文件已删除或不存在）") : workContent);

        // 差异底色：旧侧删除行红、新侧新增行绿
        var removedBg = new SolidColorBrush(Color.FromArgb(60, 255, 80, 80));
        var addedBg = new SolidColorBrush(Color.FromArgb(60, 80, 200, 120));
        LeftEditor.TextArea.TextView.LineTransformers.Add(new DiffLineColorizer(oldKinds, removedBg, addedBg));
        RightEditor.TextArea.TextView.LineTransformers.Add(new DiffLineColorizer(newKinds, removedBg, addedBg));
        // 导航高亮（黄色）叠在差异底色之上
        LeftEditor.TextArea.TextView.LineTransformers.Add(_navOld);
        RightEditor.TextArea.TextView.LineTransformers.Add(_navNew);

        PrevDiffButton.Click += (_, _) => Navigate(false);
        NextDiffButton.Click += (_, _) => Navigate(true);
        DiffNavText.Text = LangService.TFormat("差异 {0}/{1}", 0, _blocks.Count);
        PrevDiffButton.IsEnabled = _blocks.Count > 0;
        NextDiffButton.IsEnabled = _blocks.Count > 0;

        ApplyThemeColors();
        // 界面语言：标题/提示按当前语言设置（每次打开对比时重建，无需监听切换）
        LeftHeaderText.Text = LangService.T("修改前 (HEAD)");
        RightHeaderText.Text = LangService.T("修改后 (工作区)");
        ToolTip.SetTip(PrevDiffButton, LangService.T("上一个差异"));
        ToolTip.SetTip(NextDiffButton, LangService.T("下一个差异"));
    }

    /// <summary>跳到上一个(true)/下一个(false)差异块；越界循环；无差异时提示。</summary>
    private void Navigate(bool forward)
    {
        if (_blocks.Count == 0)
        {
            StatusReported?.Invoke(LangService.T("文件无差异"));
            return;
        }
        _currentIndex = forward
            ? (_currentIndex + 1) % _blocks.Count
            : (_currentIndex <= 0 ? _blocks.Count - 1 : _currentIndex - 1);
        var b = _blocks[_currentIndex];
        _navOld.SetLine(b.OldLine);
        _navNew.SetLine(b.NewLine);
        LeftEditor.TextArea.TextView.Redraw();
        RightEditor.TextArea.TextView.Redraw();
        if (b.OldLine > 0) LeftEditor.ScrollToLine(b.OldLine);
        if (b.NewLine > 0) RightEditor.ScrollToLine(b.NewLine);
        DiffNavText.Text = LangService.TFormat("差异 {0}/{1}", _currentIndex + 1, _blocks.Count);
        StatusReported?.Invoke(b.OldLine > 0 && b.NewLine > 0
            ? LangService.TFormat("差异 {0}/{1}：旧侧第 {2} 行 / 新侧第 {3} 行", _currentIndex + 1, _blocks.Count, b.OldLine, b.NewLine)
            : b.OldLine > 0
                ? LangService.TFormat("差异 {0}/{1}：旧侧第 {2} 行（删除）", _currentIndex + 1, _blocks.Count, b.OldLine)
                : LangService.TFormat("差异 {0}/{1}：新侧第 {2} 行（新增）", _currentIndex + 1, _blocks.Count, b.NewLine));
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// 行级 LCS 差异。行数乘积较大时退化为“公共前缀/后缀 + 中间整体标记”，避免大文件内存爆炸。
    /// 返回两侧行标记与差异块列表（连续差异区）。
    /// </summary>
    private static (DiffLineKind[] Old, DiffLineKind[] New, List<DiffBlock> Blocks) DiffLines(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if ((long)n * m > 4_000_000)
        {
            return CoarseDiff(a, b);
        }

        // dp[i,j] = a[i..] 与 b[j..] 的 LCS 长度
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var ok = new DiffLineKind[n];
        var nk = new DiffLineKind[m];
        var blocks = new List<DiffBlock>();
        int blockOldStart = -1, blockNewStart = -1, blockOldCount = 0, blockNewCount = 0;
        void Flush()
        {
            if (blockOldCount > 0 || blockNewCount > 0)
            {
                blocks.Add(new DiffBlock(blockOldStart, blockNewStart, blockOldCount, blockNewCount));
            }
            blockOldStart = blockNewStart = -1;
            blockOldCount = blockNewCount = 0;
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                ok[x] = DiffLineKind.Unchanged;
                nk[y] = DiffLineKind.Unchanged;
                Flush();
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ok[x] = DiffLineKind.Removed;
                if (blockOldCount == 0) blockOldStart = x + 1;
                blockOldCount++;
                x++;
            }
            else
            {
                nk[y] = DiffLineKind.Added;
                if (blockNewCount == 0) blockNewStart = y + 1;
                blockNewCount++;
                y++;
            }
        }
        while (x < n)
        {
            ok[x] = DiffLineKind.Removed;
            if (blockOldCount == 0) blockOldStart = x + 1;
            blockOldCount++;
            x++;
        }
        while (y < m)
        {
            nk[y] = DiffLineKind.Added;
            if (blockNewCount == 0) blockNewStart = y + 1;
            blockNewCount++;
            y++;
        }
        Flush();
        return (ok, nk, blocks);
    }

    /// <summary>大文件退化算法：两端相同行标记为未变，中间整体标记为删除+新增。</summary>
    private static (DiffLineKind[] Old, DiffLineKind[] New, List<DiffBlock> Blocks) CoarseDiff(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var ok = Enumerable.Repeat(DiffLineKind.Removed, n).ToArray();
        var nk = Enumerable.Repeat(DiffLineKind.Added, m).ToArray();
        int prefix = 0;
        while (prefix < n && prefix < m && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            ok[prefix] = DiffLineKind.Unchanged;
            nk[prefix] = DiffLineKind.Unchanged;
            prefix++;
        }
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix &&
               string.Equals(a[n - 1 - suffix], b[m - 1 - suffix], StringComparison.Ordinal))
        {
            ok[n - 1 - suffix] = DiffLineKind.Unchanged;
            nk[m - 1 - suffix] = DiffLineKind.Unchanged;
            suffix++;
        }
        var blocks = new List<DiffBlock>();
        int midOld = n - prefix - suffix;
        int midNew = m - prefix - suffix;
        if (midOld > 0 || midNew > 0)
        {
            blocks.Add(new DiffBlock(
                prefix < n ? prefix + 1 : -1,
                prefix < m ? prefix + 1 : -1,
                midOld, midNew));
        }
        return (ok, nk, blocks);
    }

    /// <summary>按行着色差异底色。</summary>
    private sealed class DiffLineColorizer : DocumentColorizingTransformer
    {
        private readonly DiffLineKind[] _kinds;
        private readonly IBrush _removedBg;
        private readonly IBrush _addedBg;

        public DiffLineColorizer(DiffLineKind[] kinds, IBrush removedBg, IBrush addedBg)
        {
            _kinds = kinds;
            _removedBg = removedBg;
            _addedBg = addedBg;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            int idx = line.LineNumber - 1;
            if (idx < 0 || idx >= _kinds.Length) return;
            var kind = _kinds[idx];
            if (kind == DiffLineKind.Unchanged) return;
            var bg = kind == DiffLineKind.Removed ? _removedBg : _addedBg;
            ChangeLinePart(line.Offset, line.EndOffset,
                element => element.TextRunProperties.SetBackgroundBrush(bg));
        }
    }

    /// <summary>导航当前行高亮（黄色半透明，叠在差异底色之上）。</summary>
    private sealed class NavHighlightColorizer : DocumentColorizingTransformer
    {
        private readonly IBrush _bg = new SolidColorBrush(Color.FromArgb(95, 255, 200, 60));
        private int _line;

        public void SetLine(int line) => _line = line;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_line > 0 && line.LineNumber == _line)
            {
                ChangeLinePart(line.Offset, line.EndOffset,
                    element => element.TextRunProperties.SetBackgroundBrush(_bg));
            }
        }
    }

    /// <summary>按当前主题刷新背景/前景（与项目 ThemeBrush 惯例一致）。</summary>
    private void ApplyThemeColors()
    {
        var bg = ThemeBrush("EditorBackgroundBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
        var fg = ThemeBrush("EditorForegroundBrush", Color.FromRgb(0xE0, 0xE0, 0xE0));
        Background = bg;
        // 编辑器控件与文本区背景与主题背景一致（AvaloniaEdit 内部默认底色会覆盖控件背景）
        LeftEditor.Background = bg;
        LeftEditor.TextArea.Background = bg;
        LeftEditor.Foreground = fg;
        LeftEditor.TextArea.Foreground = fg;
        RightEditor.Background = bg;
        RightEditor.TextArea.Background = bg;
        RightEditor.Foreground = fg;
        RightEditor.TextArea.Foreground = fg;
    }

    /// <summary>按当前主题变体取语义画刷（找不到时用暗色兜底）。</summary>
    private static IBrush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current is { } app && app.RequestedThemeVariant is { } variant &&
            app.TryFindResource(key, variant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }
}
