using System;
using Avalonia;
using Avalonia.Input.TextInput;

namespace TextFlowPlus.Services.Terminal;

/// <summary>
/// 终端专用 IME 客户端。
/// 终端编辑器为只读（IsReadOnly=true），AvaloniaEdit 的 TextArea 因此不会注册
/// 自己的 IME 客户端（源码：<c>if (!ta.IsReadOnly) e.Client = ta._imClient;</c>），
/// 导致 Win32 平台上输入法（如微软拼音）无法组合中文。
/// 本客户端注册到平台后：组合文本经 <see cref="SetPreeditText"/> 回调给终端视图
/// 显示在输入行末尾，确认后由 TextInput 事件携带最终文本正常进入终端输入管线。
/// </summary>
public sealed class TerminalImeClient : TextInputMethodClient
{
    private readonly Visual _textViewVisual;

    public TerminalImeClient(Visual textViewVisual)
    {
        _textViewVisual = textViewVisual;
    }

    /// <summary>组合文本变化（null 表示组合结束/清空）。</summary>
    public event Action<string?>? PreeditChanged;

    /// <summary>承载文本的控件（平台用于定位输入法组合窗口）。</summary>
    public override Visual TextViewVisual => _textViewVisual;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    /// <summary>组合窗口锚点矩形（相对 TextViewVisual；终端为等宽渲染，给默认光标尺寸即可）。</summary>
    public override Rect CursorRectangle => new Rect(0, 0, 2, 16);

    public override TextSelection Selection
    {
        get => new TextSelection(0, 0);
        set { }
    }

    public override void SetPreeditText(string? preeditText)
    {
        PreeditChanged?.Invoke(preeditText);
    }
}
