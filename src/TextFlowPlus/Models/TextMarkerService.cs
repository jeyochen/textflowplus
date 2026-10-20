using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace TextFlowPlus.Models;

public class TextMarkerService
{
    private readonly TextArea _textArea;
    private readonly List<TextMarker> _markers = new();

    public TextMarkerService(TextArea textArea)
    {
        _textArea = textArea;
    }

    public void Create(int offset, int length, Color color)
    {
        var marker = new TextMarker(offset, length, color);
        _markers.Add(marker);
        _textArea.TextView.LineTransformers.Add(marker);
    }

    public void Clear()
    {
        foreach (var marker in _markers)
            _textArea.TextView.LineTransformers.Remove(marker);
        _markers.Clear();
    }
}

public class TextMarker : IVisualLineTransformer
{
    private readonly int _offset;
    private readonly int _length;
    private readonly Color _color;

    public TextMarker(int offset, int length, Color color)
    {
        _offset = offset;
        _length = length;
        _color = color;
    }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        int startOffset = context.VisualLine.FirstDocumentLine.Offset;
        int endOffset = startOffset + context.VisualLine.FirstDocumentLine.Length;
        if (_offset < endOffset && _offset + _length > startOffset)
        {
            foreach (var element in elements)
            {
                element.BackgroundBrush = new SolidColorBrush(_color, 0.5);
            }
        }
    }
}
