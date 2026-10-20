using System.Collections.Generic;
using Avalonia.Media;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 终端缓冲区模型（支持颜色）
/// </summary>
public class TerminalBuffer
{
    private readonly List<List<ColoredChar>> _lines = new();
    private readonly int _maxLines;
    private int _cursorX;
    private int _cursorY;
    private int _width;
    private int _height;
    
    // 当前颜色状态
    private Color? _currentForeground;
    private Color? _currentBackground;
    private bool _currentBold;
    private bool _currentItalic;
    private bool _currentUnderline;

    public event EventHandler? BufferChanged;

    private bool _suppressEvents;

    public TerminalBuffer(int width = 80, int height = 24, int maxLines = 1000)
    {
        _width = width;
        _height = height;
        _maxLines = maxLines;
        Clear();
    }

    /// <summary>
    /// 开始批量写入，暂时禁用事件触发
    /// </summary>
    public void BeginBatchWrite()
    {
        _suppressEvents = true;
    }

    /// <summary>
    /// 结束批量写入，触发一次事件
    /// </summary>
    public void EndBatchWrite()
    {
        _suppressEvents = false;
        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    public int CursorX
    {
        get => _cursorX;
        set => _cursorX = Math.Max(0, Math.Min(value, _width - 1));
    }

    public int CursorY
    {
        get => _cursorY;
        set
        {
            _cursorY = value;
            while (_lines.Count <= _cursorY)
            {
                _lines.Add(CreateEmptyLine());
            }
        }
    }

    public int Width => _width;
    public int Height => _height;
    public IReadOnlyList<IReadOnlyList<ColoredChar>> Lines => _lines.AsReadOnly();

    // 当前颜色属性
    public Color? CurrentForeground => _currentForeground;
    public Color? CurrentBackground => _currentBackground;
    public bool CurrentBold => _currentBold;
    public bool CurrentItalic => _currentItalic;
    public bool CurrentUnderline => _currentUnderline;

    /// <summary>
    /// 创建空行
    /// </summary>
    private List<ColoredChar> CreateEmptyLine()
    {
        var line = new List<ColoredChar>();
        for (int i = 0; i < _width; i++)
        {
            line.Add(new ColoredChar(' '));
        }
        return line;
    }

    /// <summary>
    /// 设置当前颜色属性
    /// </summary>
    public void SetColor(Color? foreground, Color? background, bool? bold = null, bool? italic = null, bool? underline = null)
    {
        _currentForeground = foreground;
        _currentBackground = background;
        if (bold.HasValue) _currentBold = bold.Value;
        if (italic.HasValue) _currentItalic = italic.Value;
        if (underline.HasValue) _currentUnderline = underline.Value;
    }

    /// <summary>
    /// 重置颜色属性
    /// </summary>
    public void ResetColor()
    {
        _currentForeground = null;
        _currentBackground = null;
        _currentBold = false;
        _currentItalic = false;
        _currentUnderline = false;
    }

    /// <summary>
    /// 调整终端大小
    /// </summary>
    public void Resize(int width, int height)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        // 变宽时给已有行补空格，变窄时**不截断**（否则会静默丢失已显示的内容）
        foreach (var line in _lines)
        {
            while (line.Count < width)
            {
                line.Add(new ColoredChar(' '));
            }
        }

        _width = width;
        _height = height;

        // 光标越界时钳制
        _cursorX = Math.Max(0, Math.Min(_cursorX, _width - 1));
        if (_cursorY >= _height && _lines.Count > _height) _cursorY = _height - 1;

        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>确保行足够长（可超过 _width：写满一行后自动换行会推入新行）。</summary>
    private void EnsureLineLength(List<ColoredChar> line, int length)
    {
        while (line.Count < length)
        {
            line.Add(new ColoredChar(' '));
        }
    }

    /// <summary>滚动：移除首行并追加空行（用于换行/下移超出可见区）。</summary>
    private void ScrollUp()
    {
        if (_lines.Count == 0) return;
        _lines.RemoveAt(0);
        _lines.Add(CreateEmptyLine());
    }

    /// <summary>
    /// 写入字符
    /// </summary>
    public void Write(char c)
    {
        // 确保当前行存在
        while (_lines.Count <= _cursorY)
        {
            _lines.Add(CreateEmptyLine());
        }

        if (c == '\n')
        {
            // 换行：移动光标到下一行行首
            _cursorY++;
            _cursorX = 0;
            while (_lines.Count <= _cursorY) _lines.Add(CreateEmptyLine());

            // 超出可见区域则滚动
            while (_cursorY >= _height && _lines.Count > _height)
            {
                ScrollUp();
                _cursorY--;
            }
            _cursorY = Math.Max(0, _cursorY);
        }
        else if (c == '\r')
        {
            _cursorX = 0;
        }
        else if (c == '\b')
        {
            // 退格：光标左移（不擦除字符，与真实终端一致；擦除由 \x1b[K 等完成）
            if (_cursorX > 0) _cursorX--;
        }
        else if (c == '\t')
        {
            // 制表符：前进到下一个 8 列制表位（此前 \t 会被当作控制字符直接丢弃）
            int next = ((_cursorX / 8) + 1) * 8;
            var line = _lines[_cursorY];
            EnsureLineLength(line, next);
            for (int i = _cursorX; i < next && i < line.Count; i++)
            {
                line[i] = new ColoredChar(' ', _currentForeground, _currentBackground,
                    _currentBold, _currentItalic, _currentUnderline);
            }
            _cursorX = next;
        }
        else if (c >= 32)
        {
            // 可打印字符：写满一行自动换行到下一行行首（不再丢弃超出宽度的字符）
            if (_cursorX >= _width)
            {
                _cursorY++;
                _cursorX = 0;
                while (_lines.Count <= _cursorY) _lines.Add(CreateEmptyLine());
                while (_cursorY >= _height && _lines.Count > _height)
                {
                    ScrollUp();
                    _cursorY--;
                }
            }

            var line = _lines[_cursorY];
            // 行内容可长于终端宽度（历史行不回绕），按需扩展
            EnsureLineLength(line, _cursorX + 1);
            line[_cursorX] = new ColoredChar(c, _currentForeground, _currentBackground,
                _currentBold, _currentItalic, _currentUnderline);
            _cursorX++;
        }

        // 限制最大行数
        while (_lines.Count > _maxLines)
        {
            _lines.RemoveAt(0);
            if (_cursorY > 0) _cursorY--;
        }

        if (!_suppressEvents)
        {
            BufferChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 写入字符串
    /// </summary>
    public void Write(string text)
    {
        foreach (var c in text)
        {
            Write(c);
        }
    }

    /// <summary>
    /// 清屏
    /// </summary>
    public void Clear()
    {
        _lines.Clear();
        for (int i = 0; i < _height; i++)
        {
            _lines.Add(CreateEmptyLine());
        }
        _cursorX = 0;
        _cursorY = 0;
        ResetColor();
        if (!_suppressEvents)
        {
            BufferChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 清除从光标到行尾
    /// </summary>
    public void ClearToEndOfLine() => ClearLine(0);

    /// <summary>
    /// 清除行（EL）：0=光标到行尾，1=行首到光标，2=整行。
    /// </summary>
    public void ClearLine(int mode)
    {
        if (_cursorY >= _lines.Count) return;
        var line = _lines[_cursorY];

        switch (mode)
        {
            case 0:
                for (int i = _cursorX; i < line.Count; i++) line[i] = new ColoredChar(' ');
                break;
            case 1:
                for (int i = 0; i <= _cursorX && i < line.Count; i++) line[i] = new ColoredChar(' ');
                break;
            case 2:
                for (int i = 0; i < line.Count; i++) line[i] = new ColoredChar(' ');
                break;
        }

        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清除显示区（ED）：0=光标到屏幕底部，1=屏幕顶部到光标，2/3=整屏。
    /// </summary>
    public void ClearDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearToEndOfLine();
                for (int y = _cursorY + 1; y < _lines.Count; y++) _lines[y] = CreateEmptyLine();
                break;
            case 1:
                for (int y = 0; y < _cursorY && y < _lines.Count; y++) _lines[y] = CreateEmptyLine();
                for (int i = 0; i <= _cursorX && _cursorY < _lines.Count && i < _lines[_cursorY].Count; i++)
                    _lines[_cursorY][i] = new ColoredChar(' ');
                break;
            case 2:
            case 3:
                Clear();
                return;
        }

        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>插入空行（IL）：在光标行位置插入 count 行，下方内容下移。</summary>
    public void InsertLines(int count)
    {
        if (count < 1) count = 1;
        for (int n = 0; n < count; n++) _lines.Insert(_cursorY, CreateEmptyLine());
        _cursorX = 0;
        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删除行（DL）：从光标行开始删除 count 行，下方内容上移。</summary>
    public void DeleteLines(int count)
    {
        if (count < 1) count = 1;
        for (int n = 0; n < count; n++)
        {
            if (_cursorY < _lines.Count) _lines.RemoveAt(_cursorY);
            else break;
        }
        _cursorX = 0;
        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删除字符（DCH）：删除光标处 count 个字符，右侧内容左移（行尾补空格）。</summary>
    public void DeleteChars(int count)
    {
        if (count < 1) count = 1;
        if (_cursorY >= _lines.Count) return;
        var line = _lines[_cursorY];
        if (_cursorX >= line.Count) return;

        int remove = Math.Min(count, line.Count - _cursorX);
        line.RemoveRange(_cursorX, remove);
        EnsureLineLength(line, Math.Min(_width, line.Count + remove));
        while (line.Count < _width) line.Add(new ColoredChar(' '));

        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>插入字符（ICH）：在光标处插入 count 个空格，右侧内容右移。</summary>
    public void InsertChars(int count)
    {
        if (count < 1) count = 1;
        if (_cursorY >= _lines.Count) return;
        var line = _lines[_cursorY];
        EnsureLineLength(line, _cursorX);

        for (int n = 0; n < count; n++) line.Insert(Math.Min(_cursorX, line.Count), new ColoredChar(' '));
        _cursorX += count;

        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>向上滚动 count 行（SU）。</summary>
    public void ScrollUpLines(int count)
    {
        if (count < 1) count = 1;
        for (int n = 0; n < count; n++) ScrollUp();
        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>向下滚动 count 行（SD）。</summary>
    public void ScrollDownLines(int count)
    {
        if (count < 1) count = 1;
        for (int n = 0; n < count; n++) _lines.Insert(0, CreateEmptyLine());
        if (!_suppressEvents) BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清除从光标到屏幕底部
    /// </summary>
    public void ClearToEndOfScreen()
    {
        ClearToEndOfLine();
        for (int y = _cursorY + 1; y < _lines.Count; y++)
        {
            _lines[y] = CreateEmptyLine();
        }
        if (!_suppressEvents)
        {
            BufferChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 清除整个屏幕
    /// </summary>
    public void ClearScreen()
    {
        Clear();
    }

    /// <summary>
    /// 移动光标到指定位置
    /// </summary>
    public void MoveCursor(int x, int y)
    {
        CursorX = x;
        CursorY = y;
    }

    /// <summary>
    /// 上移光标
    /// </summary>
    public void MoveCursorUp(int count = 1)
    {
        CursorY = Math.Max(0, _cursorY - count);
    }

    /// <summary>
    /// 下移光标
    /// </summary>
    public void MoveCursorDown(int count = 1)
    {
        if (count < 1) count = 1;
        _cursorY += count;

        // 超出可见区域则滚动，保持光标在屏内
        while (_cursorY >= _height && _lines.Count > _height)
        {
            ScrollUp();
            _cursorY--;
        }

        while (_lines.Count <= _cursorY)
        {
            _lines.Add(CreateEmptyLine());
        }

        if (!_suppressEvents)
        {
            BufferChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 左移光标
    /// </summary>
    public void MoveCursorLeft(int count = 1)
    {
        CursorX = Math.Max(0, _cursorX - count);
    }

    /// <summary>
    /// 右移光标
    /// </summary>
    public void MoveCursorRight(int count = 1)
    {
        CursorX = Math.Min(_width - 1, _cursorX + count);
    }
}