using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace TextFlowPlus.Models;

/// <summary>
/// 错别字高亮着色器：对指定的字符偏移区间绘制红底标记（精确到字符，而非整行）。
/// 基于 DocumentColorizingTransformer.ChangeLinePart 实现；每个编辑器常驻一个实例并
/// 注册在 TextView.LineTransformers 中，检查时仅更新区间列表后整体重绘。
/// </summary>
public class TypoHighlightColorizer : DocumentColorizingTransformer
{
    private readonly List<(int Start, int Length)> _ranges = new();
    private IBrush? _activeBrush;

    /// <summary>当前是否还持有标记（供文本变更时自动清理判断）。</summary>
    public bool HasMarkers => _ranges.Count > 0;

    /// <summary>标记背景色（默认红底半透明）。</summary>
    public IBrush HighlightBrush { get; set; } = new SolidColorBrush(Color.FromRgb(255, 82, 82), 0.45);

    /// <summary>以新区间列表整体替换旧标记。</summary>
    public void SetRanges(IEnumerable<(int Start, int Length)> ranges)
    {
        _ranges.Clear();
        _ranges.AddRange(ranges);
        _activeBrush = _ranges.Count > 0 ? HighlightBrush : null;
    }

    /// <summary>清除全部标记。</summary>
    public void Clear()
    {
        _ranges.Clear();
        _activeBrush = null;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_activeBrush == null) return;
        foreach (var (start, length) in _ranges)
        {
            int end = start + length;
            if (start >= line.EndOffset || end <= line.Offset) continue;
            int s = Math.Max(line.Offset, start);
            int e = Math.Min(line.EndOffset, end);
            if (e > s)
                ChangeLinePart(s, e, el => el.BackgroundBrush = _activeBrush);
        }
    }
}
