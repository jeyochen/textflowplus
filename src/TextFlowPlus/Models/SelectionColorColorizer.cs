using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace TextFlowPlus.Models;

/// <summary>
/// 选中文字着色器：把用户通过工具栏「字体颜色」选中的文字段按指定前景色渲染。
/// 基于 DocumentColorizingTransformer.ChangeLinePart 实现；每个编辑器常驻一个实例并注册在
/// TextView.LineTransformers 中。颜色段用 TextSegmentCollection 管理，随文档编辑自动调整位置，
/// 被删除的内容对应的段会自动失效。
/// </summary>
public class SelectionColorColorizer : DocumentColorizingTransformer
{
    /// <summary>颜色标记段：保存用户指定的文字颜色。</summary>
    private sealed class ColorSegment : TextSegment
    {
        public IBrush Brush { get; init; } = Brushes.Black;
    }

    private readonly TextSegmentCollection<ColorSegment> _segments = new();

    /// <summary>当前是否还持有颜色标记。</summary>
    public bool HasMarkers => _segments.Count > 0;

    /// <summary>为 [start, start+length) 区间设置前景色（与该区间相交的旧标记先移除，避免重叠）。</summary>
    public void SetRange(int start, int length, IBrush brush)
    {
        if (length <= 0 || start < 0) return;
        foreach (var old in _segments.FindOverlappingSegments(start, length))
        {
            _segments.Remove(old);
        }
        _segments.Add(new ColorSegment { StartOffset = start, Length = length, Brush = brush });
    }

    /// <summary>清除全部颜色标记。</summary>
    public void Clear()
    {
        _segments.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var seg in _segments.FindOverlappingSegments(line.Offset, line.Length))
        {
            int s = System.Math.Max(line.Offset, seg.StartOffset);
            int e = System.Math.Min(line.EndOffset, seg.EndOffset);
            if (e > s)
            {
                var brush = seg.Brush;
                ChangeLinePart(s, e, el => el.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
