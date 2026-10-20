using System.Text;
using Avalonia.Media;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// ANSI 转义序列解析器 - 终端逻辑层核心
/// 解析 Linux 终端 ANSI 控制码，转换为终端操作指令
/// </summary>
public class AnsiEscapeParser
{
    private readonly StringBuilder _buffer = new();
    private TerminalBuffer? _bufferModel;

    // 事件：当有纯文本需要显示时
    public event EventHandler<string>? TextOutput;
    // 事件：当需要清屏时
    public event EventHandler? ClearScreen;
    // 事件：当需要清行时
    public event EventHandler? ClearLine;
    // 事件：当需要移动光标时
    public event EventHandler<(int x, int y)>? MoveCursor;
    // 事件：当需要改变颜色时
    public event EventHandler<int[]>? ChangeColor;
    // 事件：当需要删除字符时
    public event EventHandler<int>? DeleteChars;

    // ANSI 标准颜色映射
    private static readonly Color[] AnsiColors = new[]
    {
        Color.FromRgb(0, 0, 0),       // 0 Black
        Color.FromRgb(205, 49, 49),   // 1 Red
        Color.FromRgb(13, 188, 121),  // 2 Green
        Color.FromRgb(229, 229, 16),  // 3 Yellow
        Color.FromRgb(36, 114, 200),  // 4 Blue
        Color.FromRgb(188, 63, 188),  // 5 Magenta
        Color.FromRgb(15, 168, 213),  // 6 Cyan
        Color.FromRgb(229, 229, 229), // 7 White
        Color.FromRgb(102, 102, 102), // 8 Bright Black
        Color.FromRgb(241, 76, 76),   // 9 Bright Red
        Color.FromRgb(35, 209, 139),  // 10 Bright Green
        Color.FromRgb(245, 245, 67),  // 11 Bright Yellow
        Color.FromRgb(59, 142, 234),  // 12 Bright Blue
        Color.FromRgb(214, 112, 255), // 13 Bright Magenta
        Color.FromRgb(41, 184, 219),  // 14 Bright Cyan
        Color.FromRgb(255, 255, 255)  // 15 Bright White
    };

    public void SetTerminalBuffer(TerminalBuffer buffer)
    {
        _bufferModel = buffer;
    }

    /// <summary>
    /// 解析并处理 ANSI 转义序列
    /// </summary>
    public void ParseAndProcess(string text)
    {
        // 开始批量写入，减少事件触发
        _bufferModel?.BeginBatchWrite();
        
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length)
            {
                // 刷新缓冲区后再处理转义序列
                FlushBuffer();

                if (text[i + 1] == '[')
                {
                    // CSI 序列 - 处理控制命令
                    i = ParseCsiSequence(text, i);
                }
                else if (text[i + 1] == ']')
                {
                    // OSC 序列 - 标题等，跳过
                    i = ParseOscSequence(text, i);
                }
                else
                {
                    // 其他转义序列，跳过
                    i += 2;
                }
            }
            else if (text[i] == '\r')
            {
                // 回车 - 移动到行首（先刷新缓冲区）
                FlushBuffer();
                _bufferModel?.MoveCursor(0, _bufferModel.CursorY);
                i++;

                // 检查是否紧跟 \n (\r\n 序列)
                if (i < text.Length && text[i] == '\n')
                {
                    // 一起处理 \r\n：先回车再换行
                    _bufferModel?.MoveCursorDown();
                    _bufferModel?.MoveCursor(0, _bufferModel.CursorY);
                    i++;
                }
            }
            else if (text[i] == '\n')
            {
                // 单独换行（前面没有\r）- 先刷新缓冲区
                FlushBuffer();
                // 检查是否紧跟 \r (\n\r 序列，某些系统使用)
                if (i + 1 < text.Length && text[i + 1] == '\r')
                {
                    // 一起处理 \n\r：先换行再回车
                    _bufferModel?.MoveCursorDown();
                    _bufferModel?.MoveCursor(0, _bufferModel.CursorY);
                    i += 2;
                }
                else
                {
                    _bufferModel?.MoveCursorDown();
                    _bufferModel?.MoveCursor(0, _bufferModel.CursorY);
                    i++;
                }
            }
            else if (text[i] == '\b')
            {
                // 退格（先刷新缓冲区）
                FlushBuffer();
                _bufferModel?.MoveCursorLeft();
                i++;
            }
            else if (text[i] == '\x07')
            {
                // 响铃，忽略
                i++;
            }
            else
            {
                // 普通字符 - 添加到缓冲区
                _buffer.Append(text[i]);
                i++;
            }
        }

        // 输出剩余缓冲区内容
        FlushBuffer();
        
        // 结束批量写入，触发一次事件
        _bufferModel?.EndBatchWrite();
    }

    /// <summary>
    /// 刷新缓冲区，输出文本
    /// </summary>
    private void FlushBuffer()
    {
        if (_buffer.Length > 0)
        {
            var text = _buffer.ToString();
            _bufferModel?.Write(text);
            TextOutput?.Invoke(this, text);
            _buffer.Clear();
        }
    }

    /// <summary>
    /// 解析 CSI 序列 (ESC[)
    /// </summary>
    private int ParseCsiSequence(string text, int startIndex)
    {
        int i = startIndex + 2; // 跳过 ESC[
        int start = i;

        // 找到序列结束符 (A-Z 或 a-z)
        while (i < text.Length && text[i] < 0x40)
        {
            i++;
        }

        if (i >= text.Length)
        {
            return text.Length;
        }

        char finalByte = text[i];
        string paramsStr = text.Substring(start, i - start);
        i++; // 跳过结束符

        // 解析参数
        var parameters = paramsStr.Split(';').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();

        // 处理不同的命令
        switch (finalByte)
        {
            case 'm': // SGR - 设置图形渲染
                ProcessSgr(parameters);
                break;

            case 'J': // ED - 清除显示
                ProcessClearDisplay(parameters.Length > 0 ? parameters[0] : 0);
                break;

            case 'K': // EL - 清除行
                ProcessClearLine(parameters.Length > 0 ? parameters[0] : 0);
                break;

            case 'H': // CUP - 光标位置
            case 'f': // HVP - 水平垂直位置
                ProcessMoveCursor(parameters);
                break;

            case 'A': // CUU - 光标上移
                _bufferModel?.MoveCursorUp(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'B': // CUD - 光标下移
                _bufferModel?.MoveCursorDown(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'C': // CUF - 光标右移
                _bufferModel?.MoveCursorRight(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'D': // CUB - 光标左移
                _bufferModel?.MoveCursorLeft(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'G': // CHA - 光标水平绝对位置
                if (parameters.Length > 0 && parameters[0] > 0)
                {
                    _bufferModel?.MoveCursor(parameters[0] - 1, _bufferModel.CursorY);
                }
                break;

            case 'L': // IL - 插入行
                _bufferModel?.InsertLines(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'M': // DL - 删除行
                _bufferModel?.DeleteLines(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case '@': // ICH - 插入字符
                _bufferModel?.InsertChars(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'P': // DCH - 删除字符
                _bufferModel?.DeleteChars(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'S': // SU - 向上滚动
                _bufferModel?.ScrollUpLines(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'T': // SD - 向下滚动
                _bufferModel?.ScrollDownLines(parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1);
                break;

            case 'n': // DSR - 设备状态报告
                // TODO: 实现状态报告
                break;

            case 'c': // DA - 设备属性
                // TODO: 实现设备属性
                break;

            case 'h': // SM - 设置模式
                // TODO: 实现模式设置
                break;

            case 'l': // RM - 重置模式
                // TODO: 实现模式重置
                break;

            case 'g': // TBC - 制表符清除
                // TODO: 实现制表符清除
                break;

            case 'd': // VPA - 垂直位置绝对
                if (parameters.Length > 0 && parameters[0] > 0)
                {
                    _bufferModel?.MoveCursor(_bufferModel.CursorX, parameters[0] - 1);
                }
                break;
        }

        return i;
    }

    /// <summary>
    /// 解析 OSC 序列 (ESC])
    /// </summary>
    private int ParseOscSequence(string text, int startIndex)
    {
        int i = startIndex + 2; // 跳过 ESC]

        // 找到序列结束符 (BEL 或 ESC\)
        while (i < text.Length && text[i] != '\x07' && !(text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\'))
        {
            i++;
        }

        if (i < text.Length)
        {
            if (text[i] == '\x07')
                i++;
            else if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                i += 2;
        }

        return i;
    }

    /// <summary>
    /// 处理 SGR 颜色命令
    /// </summary>
    private void ProcessSgr(int[] parameters)
    {
        if (_bufferModel == null) return;

        if (parameters.Length == 0 || parameters[0] == 0)
        {
            // 重置所有属性
            _bufferModel.ResetColor();
            ChangeColor?.Invoke(this, new[] { 0 });
            return;
        }

        Color? foreground = _bufferModel.CurrentForeground;
        Color? background = _bufferModel.CurrentBackground;
        bool bold = _bufferModel.CurrentBold;
        bool italic = _bufferModel.CurrentItalic;
        bool underline = _bufferModel.CurrentUnderline;

        int i = 0;
        while (i < parameters.Length)
        {
            int param = parameters[i];

            if (param >= 30 && param <= 37)
            {
                // 前景色 (30-37)
                foreground = AnsiColors[param - 30];
            }
            else if (param >= 40 && param <= 47)
            {
                // 背景色 (40-47)
                background = AnsiColors[param - 40];
            }
            else if (param >= 90 && param <= 97)
            {
                // 亮前景色 (90-97)
                foreground = AnsiColors[8 + (param - 90)];
            }
            else if (param >= 100 && param <= 107)
            {
                // 亮背景色 (100-107)
                background = AnsiColors[8 + (param - 100)];
            }
            else if (param == 38 && i + 2 < parameters.Length)
            {
                // 256 色或 RGB 前景色
                i++;
                if (parameters[i] == 5 && i + 1 < parameters.Length)
                {
                    // 256 色模式
                    i++;
                    int colorIndex = parameters[i];
                    foreground = Get256Color(colorIndex);
                }
                else if (parameters[i] == 2 && i + 3 < parameters.Length)
                {
                    // RGB 模式
                    i++;
                    int r = parameters[i];
                    i++;
                    int g = parameters[i];
                    i++;
                    int b = parameters[i];
                    foreground = Color.FromRgb((byte)r, (byte)g, (byte)b);
                }
            }
            else if (param == 48 && i + 2 < parameters.Length)
            {
                // 256 色或 RGB 背景色
                i++;
                if (parameters[i] == 5 && i + 1 < parameters.Length)
                {
                    // 256 色模式
                    i++;
                    int colorIndex = parameters[i];
                    background = Get256Color(colorIndex);
                }
                else if (parameters[i] == 2 && i + 3 < parameters.Length)
                {
                    // RGB 模式
                    i++;
                    int r = parameters[i];
                    i++;
                    int g = parameters[i];
                    i++;
                    int b = parameters[i];
                    background = Color.FromRgb((byte)r, (byte)g, (byte)b);
                }
            }
            else if (param == 39)
            {
                // 重置前景色
                foreground = null;
            }
            else if (param == 49)
            {
                // 重置背景色
                background = null;
            }
            else if (param == 1)
            {
                // 粗体/高亮
                bold = true;
            }
            else if (param == 2)
            {
                // 正常强度
                bold = false;
            }
            else if (param == 3)
            {
                // 斜体
                italic = true;
            }
            else if (param == 4)
            {
                // 下划线
                underline = true;
            }
            else if (param == 22)
            {
                // 关闭粗体/高亮
                bold = false;
            }
            else if (param == 23)
            {
                // 关闭斜体
                italic = false;
            }
            else if (param == 24)
            {
                // 关闭下划线
                underline = false;
            }

            i++;
        }

        _bufferModel.SetColor(foreground, background, bold, italic, underline);
        ChangeColor?.Invoke(this, parameters);
    }

    /// <summary>
    /// 获取 256 色
    /// </summary>
    private Color Get256Color(int index)
    {
        if (index >= 0 && index <= 15)
        {
            return AnsiColors[index];
        }
        else if (index >= 16 && index <= 231)
        {
            // 216 色调色板
            index -= 16;
            int r = (index / 36) * 51;
            int g = ((index / 6) % 6) * 51;
            int b = (index % 6) * 51;
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }
        else if (index >= 232 && index <= 255)
        {
            // 24 级灰度
            int gray = 8 + (index - 232) * 10;
            return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
        }

        return AnsiColors[7]; // 默认白色
    }

    /// <summary>
    /// 处理清屏命令
    /// </summary>
    private void ProcessClearDisplay(int mode)
    {
        // 0=光标到屏底，1=屏顶到光标，2=整屏，3=整屏+回滚缓冲
        _bufferModel?.ClearDisplay(mode);
        if (mode == 2 || mode == 3)
        {
            ClearScreen?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 处理清行命令
    /// </summary>
    private void ProcessClearLine(int mode)
    {
        // 0=光标到行尾，1=行首到光标，2=整行
        _bufferModel?.ClearLine(mode);
        ClearLine?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 处理移动光标命令
    /// </summary>
    private void ProcessMoveCursor(int[] parameters)
    {
        int row = parameters.Length > 0 && parameters[0] > 0 ? parameters[0] - 1 : 0;
        int col = parameters.Length > 1 && parameters[1] > 0 ? parameters[1] - 1 : 0;

        _bufferModel?.MoveCursor(col, row);
        MoveCursor?.Invoke(this, (col, row));
    }
}