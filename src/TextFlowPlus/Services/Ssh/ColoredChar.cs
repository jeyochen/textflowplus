using Avalonia.Media;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 带颜色信息的字符
/// </summary>
public class ColoredChar
{
    public char Char { get; set; }
    public Color? Foreground { get; set; }
    public Color? Background { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    public ColoredChar()
    {
        Char = ' ';
    }

    public ColoredChar(char c)
    {
        Char = c;
    }

    public ColoredChar(char c, Color? foreground, Color? background, bool bold = false, bool italic = false, bool underline = false)
    {
        Char = c;
        Foreground = foreground;
        Background = background;
        Bold = bold;
        Italic = italic;
        Underline = underline;
    }

    public ColoredChar Clone()
    {
        return new ColoredChar(Char, Foreground, Background, Bold, Italic, Underline);
    }
}