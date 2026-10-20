using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace TextFlowPlus.Views;

/// <summary>
/// 为 AvaloniaEdit 补充 IME 拼音预编辑（preedit）显示。
/// 背景：AvaloniaEdit 的 TextAreaTextInputMethodClient.SupportsPreedit 硬编码返回 false，
/// SetPreeditText 为空实现，导致编辑区输入时拼音字母不可见（只有上屏后的汉字）。
/// 本类通过自定义 TextInputMethodClient 接管 IME，在光标处用 Popup 显示当前拼音。
/// </summary>
public sealed class EditorImeSupport : TextInputMethodClient, IDisposable
{
    private readonly TextEditor _editor;
    private readonly Popup _popup;
    private readonly TextBlock _preeditBlock;

    public EditorImeSupport(TextEditor editor)
    {
        _editor = editor;

        _preeditBlock = new TextBlock
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            Foreground = Brushes.Black,
            FontFamily = editor.FontFamily,
            FontSize = Math.Max(12, editor.FontSize),
            Padding = new Thickness(3, 1),
            TextWrapping = TextWrapping.NoWrap,
        };

        _popup = new Popup
        {
            Child = _preeditBlock,
            PlacementTarget = editor.TextArea,
            // 底部对齐 + 左对齐 + 偏移：让拼音 Popup 的左边缘落在光标处、文字向右推进
            Placement = PlacementMode.Bottom,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            HorizontalOffset = 0,
            VerticalOffset = 0,
            IsHitTestVisible = false,
            Focusable = false,
            IsOpen = false,
        };

        editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        editor.TextArea.TextView.ScrollOffsetChanged += OnCaretChanged;
    }

    public override bool SupportsPreedit => true;
    public override bool SupportsSurroundingText => true;
    public override string SurroundingText => _editor.Text;
    public override Visual TextViewVisual => _editor.TextArea.TextView;

    public override Avalonia.Input.TextInput.TextSelection Selection
    {
        get => new Avalonia.Input.TextInput.TextSelection(_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength);
        set
        {
            _editor.SelectionStart = value.Start;
            _editor.SelectionLength = Math.Max(0, value.End - value.Start);
        }
    }

    public override Rect CursorRectangle
    {
        get
        {
            var p = GetCaretPointInTextArea();
            var h = _editor.TextArea.TextView.DefaultLineHeight;
            return new Rect(p, new Size(1, h));
        }
    }

    public override void SetPreeditText(string? preeditText)
    {
        if (string.IsNullOrEmpty(preeditText))
        {
            _popup.IsOpen = false;
            return;
        }

        _preeditBlock.Text = preeditText;
        UpdatePopupOffset();
        _popup.IsOpen = true;
    }

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        SetPreeditText(preeditText);
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_popup.IsOpen) UpdatePopupOffset();
    }

    /// <summary>获取光标在 TextArea 坐标系中的位置（TextView 视觉坐标 → TextArea 坐标）。</summary>
    private Point GetCaretPointInTextArea()
    {
        var caretPos = _editor.TextArea.Caret.Position;
        var pt = _editor.TextArea.TextView.GetVisualPosition(caretPos, VisualYPosition.LineBottom);
        var translated = _editor.TextArea.TextView.TranslatePoint(pt, _editor.TextArea);
        return translated ?? pt;
    }

    private void UpdatePopupOffset()
    {
        var p = GetCaretPointInTextArea();
        // 左对齐 target 左边缘 + 光标 X → Popup 左边缘落在光标处，拼音向右推进
        _popup.HorizontalOffset = p.X;
        // Bottom 模式下 Popup 默认贴 target 底部，减去高度让它回到光标所在行
        _popup.VerticalOffset = p.Y - _editor.TextArea.Bounds.Height;
    }

    public void Dispose()
    {
        _editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
        _editor.TextArea.TextView.ScrollOffsetChanged -= OnCaretChanged;
        _popup.IsOpen = false;
    }
}
