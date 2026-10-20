using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using TextFlowPlus.Services.Ssh;
using TextFlowPlus.Services.Terminal;

namespace TextFlowPlus.Views;

/// <summary>
/// 本地命令行终端视图（UserControl，可嵌入主窗口标签页）。
/// Windows 启动 PowerShell，macOS/Linux 启动系统默认 shell（$SHELL）。
/// 管道模式下 shell 不回显输入，因此由本视图负责输入回显与提示符显示；
/// 输出（含 ANSI 转义）由 TerminalBuffer + TerminalColorizer 渲染。
/// </summary>
public partial class LocalTerminalView : UserControl
{
    /// <summary>请求主窗口关闭当前标签页（shell 退出时触发）。</summary>
    public event Action? CloseRequested;

    // 底层会话（shell 进程 + 管道）
    private LocalTerminalService? _service;

    // 终端逻辑层
    private TerminalBuffer? _terminalBuffer;
    private AnsiEscapeParser? _ansiParser;
    private TerminalColorizer? _colorizer;
    private CommandHistoryService? _commandHistory;

    // 当前输入行缓冲区（本地回显用）
    private readonly StringBuilder _currentInputBuffer = new();

    private bool _isRunning;

    /// <summary>提示符当前是否已显示（提示符幂等显示：重复输出就会出现"两个连续提示符"）。</summary>
    private bool _promptShown;

    /// <summary>是否有命令正在执行（提交命令行之后、到 shell 回报执行完成之前）。</summary>
    private bool _commandRunning;

    /// <summary>
    /// 提示符兜底定时器：仅用于**没有完成信号**的 shell（bash/zsh）。
    /// Windows PowerShell 由目录回报哨兵精确告知"这条命令执行完了"，不走计时器猜测。
    /// </summary>
    private DispatcherTimer? _promptTimer;

    /// <summary>本地跟踪的当前工作目录（用于 Tab 补全；随 cd/Set-Location 命令更新）。</summary>
    private string _currentDirectory;
    private IClipboard? _clipboard;
    private bool _displayRefreshPending;

    /// <summary>终端专用 IME 客户端（只读编辑器默认不注册，需自行提供以支持中文组合输入）。</summary>
    private TerminalImeClient? _imeClient;

    /// <summary>输入法当前组合文本（拼音等），显示在输入行末尾，确认后由 TextInput 替换。</summary>
    private string? _imePreedit;

    /// <summary>
    /// 本地提示符（shell 不输出提示符时由本视图显示）。
    /// 形如 <c>PS D:\abc&gt; </c> / <c>$ /home/me&gt; </c>，把当前工作目录带进提示符。
    /// </summary>
    private string Prompt
    {
        get
        {
            string prefix = _service?.ShellName == "PowerShell" ? "PS " : "$ ";
            string dir = FormatPromptDirectory(_currentDirectory);
            // 目录未知时退回不带路径的简单提示符，避免出现 "PS >"
            return dir.Length == 0 ? $"{prefix}> " : $"{prefix}{dir}> ";
        }
    }

    /// <summary>
    /// 把工作目录格式化为提示符中显示的路径：
    /// 用户主目录显示为 <c>~</c>（其下子目录显示为 <c>~/xxx</c>），以缩短常见路径。
    /// </summary>
    private static string FormatPromptDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return "";
        string full = directory;
        try { full = Path.GetFullPath(directory); } catch { /* 非法路径：原样显示 */ }

        // 主目录折叠为 ~（Windows 与 Unix 通用）
        string home = HomeDirectory;
        if (!string.IsNullOrEmpty(home))
        {
            string homeFull = home;
            try { homeFull = Path.GetFullPath(home); } catch { /* 忽略 */ }
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(full, homeFull, comparison)) return "~";
            // 去掉末尾分隔符后比较，避免 "C:\Users\me\" 与 "C:\Users\me" 不一致
            string homeTrimmed = homeFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length > homeTrimmed.Length + 1 &&
                full.StartsWith(homeTrimmed, comparison) &&
                (full[homeTrimmed.Length] == Path.DirectorySeparatorChar ||
                 full[homeTrimmed.Length] == Path.AltDirectorySeparatorChar))
            {
                return "~" + full.Substring(homeTrimmed.Length);
            }
        }

        // 其它路径：去掉末尾多余的目录分隔符（根目录 "D:\" 保留）
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? full : trimmed;
    }

    public LocalTerminalView()
    {
        InitializeComponent();
        InitializeTerminal();
        StartShell();
    }

    private void InitializeTerminal()
    {
        TerminalEditor.IsReadOnly = true;
        TerminalEditor.Options.AllowScrollBelowDocument = true;
        TerminalEditor.ShowLineNumbers = false;
        // 长行自动换行 + 关闭横向滚动：以前长行（中文输出、长路径、宽表格）比窗口宽时，
        // 编辑器会自动向右滚动把左侧的提示符顶出视野，看着就像"命令行不见了"。
        // 换行后内容永远横向可见，与 PowerShell / Windows Terminal 的观感一致。
        TerminalEditor.WordWrap = true;
        // 右键不移动光标：否则右键会先折叠选区，导致"选中后右键复制"拿不到内容
        TerminalEditor.TextArea.RightClickMovesCaret = false;

        _terminalBuffer = new TerminalBuffer(80, 24, 1000);
        _terminalBuffer.BufferChanged += OnBufferChanged;

        _ansiParser = new AnsiEscapeParser();
        _ansiParser.SetTerminalBuffer(_terminalBuffer);
        _ansiParser.ClearScreen += OnAnsiClearScreen;

        _colorizer = new TerminalColorizer(_terminalBuffer);
        TerminalEditor.TextArea.TextView.LineTransformers.Add(_colorizer);

        TerminalEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));
        TerminalEditor.TextArea.SelectionForeground = new SolidColorBrush(Color.FromRgb(255, 255, 255));

        _commandHistory = new CommandHistoryService(100);

        _currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(_currentDirectory)) _currentDirectory = Environment.CurrentDirectory;

        TerminalEditor.TextArea.AddHandler(KeyDownEvent, TerminalEditor_KeyDown, handledEventsToo: true);
        TerminalEditor.TextArea.AddHandler(KeyUpEvent, TerminalEditor_KeyUp, handledEventsToo: true);
        TerminalEditor.TextArea.AddHandler(TextInputEvent, TerminalEditor_TextInput, handledEventsToo: true);

        // Ctrl+滚轮缩放字体（普通滚轮保持滚动内容）。
        // 挂在 TextArea 层并 handledEventsToo:true，保证垂直滚动条出现后（内部先处理滚轮）仍能拦截；
        // Ctrl 状态由键盘事件跟踪（Windows 滚轮事件不携带可靠修饰符）。
        TerminalEditor.TextArea.AddHandler(PointerWheelChangedEvent, TerminalEditor_PointerWheelChanged, handledEventsToo: true);

        // 注册终端 IME 客户端：只读 TextArea 不注册自己的客户端（源码 if(!ta.IsReadOnly)），
        // 平台需要客户端才能组合中文。事件从 TextArea 冒泡到 TerminalEditor，
        // 在类处理器（只读 → Client=null）之后执行，覆盖为我们的客户端。
        _imeClient = new TerminalImeClient(TerminalEditor);
        _imeClient.PreeditChanged += OnImePreeditChanged;
        TerminalEditor.AddHandler(TextInputMethodClientRequestedEvent, (_, args) =>
        {
            if (args is TextInputMethodClientRequestedEventArgs e)
            {
                e.Client = _imeClient;
            }
        });

        SizeChanged += (_, _) => UpdateTerminalSizeFromViewport();
    }

    /// <summary>Ctrl 键当前是否按下（由键盘事件跟踪，供滚轮缩放判断）。</summary>
    private bool _ctrlDown;

    private void TerminalEditor_KeyUp(object? sender, KeyEventArgs e)
    {
        _ctrlDown = (e.KeyModifiers & KeyModifiers.Control) != 0;
    }

    /// <summary>Ctrl+滚轮放大/缩小终端字体（普通滚轮仍为滚动内容）。</summary>
    private void TerminalEditor_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_ctrlDown && (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        e.Handled = true;
        double step = e.Delta.Y > 0 ? 1.0 : -1.0;
        double newSize = Math.Clamp(TerminalEditor.FontSize + step, 8.0, 32.0);
        if (Math.Abs(newSize - TerminalEditor.FontSize) < 0.01) return;
        TerminalEditor.FontSize = newSize;
        // 字号变化后按新字符尺寸重算行列数，并更新状态栏
        UpdateTerminalSizeFromViewport();
    }

    private void StartShell()
    {
        try
        {
            _service = new LocalTerminalService();
            ShellNameText.Text = _service.ShellName;
            _service.OutputReceived += OnShellOutput;
            _service.Exited += OnShellExited;
            _service.Start();
            _isRunning = true;
            UpdateStatus(true);
            // 立即显示初始提示符，光标紧跟在提示符后（输入字符时由 TextInput 继续）
            _promptShown = false;
            _commandRunning = false;
            ShowPromptIfNeeded();
        }
        catch (Exception ex)
        {
            _isRunning = false;
            UpdateStatus(false);
            AppendLocalNotice($"无法启动 shell：{ex.Message}");
        }
    }

    private void OnShellOutput(object? sender, string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        Dispatcher.UIThread.Post(() =>
        {
            // 拼接上一数据块遗留的半个哨兵，再摘出工作目录回报（OSC 序列，不显示到终端上）
            string combined = _pendingSentinelChunk + data;
            _pendingSentinelChunk = "";
            string visible = ExtractWorkingDirectoryReports(combined, out bool sawCompletionSignal);
            if (visible.Length > 0)
            {
                _ansiParser?.ParseAndProcess(visible);
            }
            RequestDisplayRefresh();

            if (sawCompletionSignal)
            {
                // shell 明确回报"这条命令执行完了"：立即显示提示符。
                // 这比"输出安静 250ms 就当命令结束"可靠：慢命令（ping / npm install 等）
                // 中途输出停顿时会被误判成结束，先冒一个提示符，命令真结束时又冒一个。
                OnCommandFinished();
            }
            else if (_promptTimer != null && _promptTimer.IsEnabled)
            {
                // 没有完成信号的 shell：有输出说明命令仍在执行，顺延兜底计时器
                _promptTimer.Stop();
                _promptTimer.Start();
            }
        });
    }

    /// <summary>上一数据块结尾被截断的哨兵片段（等待与下一块拼接）。</summary>
    private string _pendingSentinelChunk = "";

    /// <summary>
    /// 从 shell 输出中提取「当前目录回报」哨兵并更新 <see cref="_currentDirectory"/>，
    /// 返回剔除了哨兵后的可见输出。<paramref name="sawCompletionSignal"/> 表示本次数据里
    /// 出现了哨兵 —— shell 的循环在每条命令执行后（finally 块）都会写一次，
    /// 因此它就是"命令执行完毕"的精确信号。哨兵格式：ESC ] 1338 ; &lt;路径&gt; BEL。
    /// 这样提示符里的路径始终来自 shell 的真实 $PWD，而非对 cd 命令的文本解析。
    /// </summary>
    private string ExtractWorkingDirectoryReports(string data, out bool sawCompletionSignal)
    {
        string prefix = LocalTerminalService.CwdReportPrefix;   // "\x1b]1338;"
        sawCompletionSignal = data.Contains(prefix, StringComparison.Ordinal);
        if (!sawCompletionSignal) return data;

        var sb = new StringBuilder(data.Length);
        int index = 0;
        while (index < data.Length)
        {
            int start = data.IndexOf(prefix, index, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(data, index, data.Length - index);
                break;
            }
            // 哨兵之前的普通输出
            sb.Append(data, index, start - index);

            int pathStart = start + prefix.Length;
            // 结束符：BEL(\a) 为主；也容忍 ST（ESC \）与换行兜底
            int end = data.IndexOf('\a', pathStart);
            int st = data.IndexOf("\x1b\\", pathStart, StringComparison.Ordinal);
            if (st >= 0 && (end < 0 || st < end)) end = st;
            if (end < 0) end = data.IndexOf('\n', pathStart);
            if (end < 0)
            {
                // 哨兵被数据块截断：暂存后半段，等下一块拼接后再解析（不显示到终端）
                _pendingSentinelChunk = data.Substring(start);
                break;
            }

            string reported = data.Substring(pathStart, end - pathStart).Trim();
            if (reported.Length > 0)
            {
                try { _currentDirectory = Path.GetFullPath(reported); }
                catch { _currentDirectory = reported; }
            }

            // 跳过结束符本身
            index = end + (data.AsSpan(end).StartsWith("\x1b\\") ? 2 : 1);
        }
        return sb.ToString();
    }

    /// <summary>当前 shell 是否会回报"命令执行完毕"信号（Windows PowerShell 的目录哨兵）。</summary>
    private bool HasShellCompletionSignal => _service?.SupportsCwdReport == true;

    /// <summary>
    /// 显示提示符（幂等，且命令执行期间绝不显示）：
    /// 只有"命令已结束 + 当前没有提示符"时才输出一次。所有会回到命令行的路径
    /// （完成信号、兜底计时器、用户开始输入、Ctrl+C 取消）都走这里，
    /// 因此不会再出现两个连续的提示符。
    /// </summary>
    private void ShowPromptIfNeeded()
    {
        if (!_isRunning || _promptShown || _commandRunning) return;
        StopPromptTimer();
        _promptShown = true;
        Echo(Prompt);
    }

    /// <summary>
    /// 用户开始编辑输入行（键入 / 粘贴）：确保提示符可见。
    /// 对没有完成信号的 shell（bash/zsh）顺带把"命令已结束"置位——否则用户输入会
    /// 落在命令输出后面，而兜底计时器随后又补一个提示符，把已经输入的内容切成两半。
    /// </summary>
    private void BeginInputLine()
    {
        if (_commandRunning && !HasShellCompletionSignal)
        {
            _commandRunning = false;
        }
        ShowPromptIfNeeded();
    }

    /// <summary>
    /// 命令执行结束：显示提示符。若用户在命令执行期间已经提前输入了下一条命令，
    /// 就把"提示符 + 已输入内容"重画到新行——否则已输入的命令会悬在命令输出中间，
    /// 看不出它就是当前输入行（PowerShell 里提前输入的行也显示在命令行上）。
    /// </summary>
    private void OnCommandFinished()
    {
        _commandRunning = false;
        if (_currentInputBuffer.Length == 0)
        {
            ShowPromptIfNeeded();
            return;
        }

        StopPromptTimer();
        _promptShown = true;
        // 命令输出恰好停在行首时不必再补一个换行，避免多出空行
        bool atLineStart = _terminalBuffer == null || _terminalBuffer.CursorX == 0;
        Echo((atLineStart ? "" : "\r\n") + Prompt + _currentInputBuffer);
    }

    /// <summary>
    /// 提示符兜底显示（仅用于没有完成信号的 shell）：命令提交后输出安静约 400ms 再显示提示符，
    /// 期间只要还有输出就顺延。Windows PowerShell 走哨兵信号，不依赖这个猜测。
    /// </summary>
    private void SchedulePromptFallback()
    {
        StopPromptTimer();
        _promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _promptTimer.Tick += (_, _) =>
        {
            StopPromptTimer();
            OnCommandFinished();
        };
        _promptTimer.Start();
    }

    private void StopPromptTimer()
    {
        _promptTimer?.Stop();
        _promptTimer = null;
    }

    private void OnShellExited(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isRunning = false;
            UpdateStatus(false);
            AppendLocalNotice("shell 已退出");
            CloseRequested?.Invoke();
        });
    }

    private void UpdateStatus(bool running)
    {
        StatusText.Text = running ? "运行中" : "已退出";
        StatusDot.Fill = new SolidColorBrush(running ? Color.FromRgb(0, 180, 0) : Color.FromRgb(128, 128, 128));
    }

    /// <summary>在终端区追加一条本地提示（黄色醒目前缀，区别于 shell 输出）。</summary>
    private void AppendLocalNotice(string message)
    {
        if (_ansiParser == null || _terminalBuffer == null) return;
        _ansiParser.ParseAndProcess("\r\n\x1b[33m" + message + "\x1b[0m\r\n");
        RequestDisplayRefresh();
    }

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        RequestDisplayRefresh();
    }

    private void OnAnsiClearScreen(object? sender, EventArgs e)
    {
        RequestDisplayRefresh();
    }

    /// <summary>请求刷新终端显示（合并高频变更，UI 空闲时统一重建一次）。</summary>
    private void RequestDisplayRefresh()
    {
        if (_displayRefreshPending) return;
        _displayRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _displayRefreshPending = false;
            UpdateTerminalDisplay();
        }, DispatcherPriority.Background);
    }

    private void UpdateTerminalDisplay()
    {
        if (_terminalBuffer == null) return;

        var document = TerminalEditor.Document;

        // 记录用户当前选区：整篇重写（document.Text = ...）会清掉部分选区，
        // 表现为"选中文本后命令一有输出就复制不到内容"。重写后按原偏移恢复。
        int savedSelStart = TerminalEditor.SelectionStart;
        int savedSelLength = TerminalEditor.SelectionLength;
        var lines = _terminalBuffer.Lines;
        var sb = new StringBuilder(lines.Count * (_terminalBuffer.Width + 1));

        int lastNonEmptyLine = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            for (int j = 0; j < line.Count; j++)
            {
                if (line[j].Char != ' ')
                {
                    lastNonEmptyLine = i;
                    break;
                }
            }
        }
        if (lastNonEmptyLine < 0) lastNonEmptyLine = Math.Min(_terminalBuffer.CursorY, lines.Count - 1);

        for (int i = 0; i <= lastNonEmptyLine && i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            foreach (var c in lines[i])
            {
                sb.Append(c.Char);
            }
        }

        var rendered = sb.ToString();

        // 输入法组合文本（拼音等）显示在当前输入行末尾（不进缓冲，
        // 确认后由 TextInput 事件携带最终文本进入缓冲并清除组合显示）
        if (!string.IsNullOrEmpty(_imePreedit))
        {
            rendered += _imePreedit;
        }

        if (!string.Equals(document.Text, rendered, StringComparison.Ordinal))
        {
            document.Text = rendered;
            // 恢复选区必须放在重写之后、定位光标之前：实测定位光标不会清掉选区
            RestoreSelectionAfterRewrite(savedSelStart, savedSelLength);
        }

        // 更新光标位置：光标所在行已渲染则定位到该行；光标落在内容末尾的空行
        //（输出以换行结尾时）则定位到文档末尾，避免光标停留在远处与提示符脱节。
        var docLines = document.Lines;
        if (docLines.Count > 0)
        {
            int targetLine = Math.Min(_terminalBuffer.CursorY, docLines.Count - 1);
            var line = docLines[targetLine];
            // 组合文本显示在行尾：组合期间光标跟随在组合文本之后
            int x = !string.IsNullOrEmpty(_imePreedit)
                ? line.Length
                : targetLine < _terminalBuffer.CursorY
                    ? line.Length
                    : Math.Min(_terminalBuffer.CursorX, line.Length);
            TerminalEditor.CaretOffset = line.Offset + x;
        }

        _colorizer?.Invalidate();

        // 滚动到光标位置（仅当光标不在可视区时才滚动，避免每帧跳动）。
        // 开了自动换行后，这里仍按逻辑行号滚动：目标行会被完整带入视野。
        TerminalEditor.ScrollToLine(Math.Min(_terminalBuffer.CursorY + 1, document.LineCount));
    }

    /// <summary>
    /// 整篇重写后恢复用户选区：AvaloniaEdit 的 <c>Document.Text = ...</c> 是全量替换，
    /// 会把部分选区清成空选区（全选不受影响），导致用户"选中后命令一有输出就复制不到内容"。
    /// 偏移按新文档长度裁剪；没有选区时什么都不做。
    /// </summary>
    private void RestoreSelectionAfterRewrite(int start, int length)
    {
        if (length <= 0) return;
        int textLength = TerminalEditor.Document.TextLength;
        int s = Math.Clamp(start, 0, textLength);
        int l = Math.Clamp(length, 0, textLength - s);
        if (l > 0) TerminalEditor.Select(s, l);
    }

    // ========== 键盘输入处理（本地回显 + 发送到 shell） ==========

    private void TerminalEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        _ctrlDown = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (!_isRunning || _service == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                var command = _currentInputBuffer.ToString();
                // 整行提交：非交互模式 shell 从 stdin 读一行执行（不逐字符发送）
                _service.SendData(PrepareShellCommand(command) + "\n");
                Echo("\r\n");
                _commandHistory?.AddCommand(command);
                UpdateWorkingDirectory(command);
                _currentInputBuffer.Clear();
                // 命令行已提交：结束当前提示符，等 shell 的完成信号再显示下一个
                _promptShown = false;
                _commandRunning = true;
                // PowerShell 有目录哨兵作完成信号；其它 shell 用安静计时器兜底
                if (!HasShellCompletionSignal) SchedulePromptFallback();
                e.Handled = true;
                return;

            case Key.Back:
                if (_currentInputBuffer.Length > 0)
                {
                    // 行编辑在本地完成（shell 非交互模式无行编辑）
                    _terminalBuffer?.MoveCursorLeft(1);
                    _terminalBuffer?.DeleteChars(1);
                    _currentInputBuffer.Remove(_currentInputBuffer.Length - 1, 1);
                    RequestDisplayRefresh();
                }
                e.Handled = true;
                return;

            case Key.Up:
                NavigateHistory(_commandHistory?.GetPrevious());
                e.Handled = true;
                return;

            case Key.Down:
                NavigateHistory(_commandHistory?.GetNext());
                e.Handled = true;
                return;

            case Key.Tab:
                // 本地 Tab 补全：基于输入行最后一个词匹配当前目录下的文件/子目录
                TryCompleteTab();
                e.Handled = true;
                return;

            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                // 有选中文本时复制（与 SSH 终端一致），否则本地取消当前输入行
                if (!string.IsNullOrEmpty(TerminalEditor.SelectedText))
                {
                    CopySelectedText();
                }
                else
                {
                    // 非交互模式无终端信号：^C 只是本地取消当前输入行
                    Echo("^C\r\n");
                    _currentInputBuffer.Clear();
                    if (!_commandRunning)
                    {
                        // 命令没在跑：立刻给出新提示符（PowerShell 的 ^C 也是这样）
                        _promptShown = false;
                        ShowPromptIfNeeded();
                    }
                }
                e.Handled = true;
                return;

            case Key.C when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
            case Key.C when e.KeyModifiers == KeyModifiers.Meta:
                CopySelectedText();
                e.Handled = true;
                return;

            case Key.V when e.KeyModifiers == KeyModifiers.Control:
            case Key.V when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
            case Key.V when e.KeyModifiers == KeyModifiers.Meta:
                PasteText();
                e.Handled = true;
                return;

            case Key.L when e.KeyModifiers == KeyModifiers.Control:
                _terminalBuffer?.ClearScreen();
                // 清屏后重新显示提示符与当前输入行
                var pending = _currentInputBuffer.ToString();
                Echo(pending.Length > 0 ? Prompt + pending : Prompt);
                _promptShown = true;
                e.Handled = true;
                return;

            case Key.Escape:
                e.Handled = true;
                return;
        }
    }

    /// <summary>解析 cd / Set-Location 命令更新本地工作目录（支持 ~、..、相对与绝对路径）。</summary>
    /// <summary>发送前预处理：cd 等命令后跟含空格且未加引号的路径时自动补单引号
    /// （PowerShell Invoke-Expression 会把未加引号的路径拆成多个参数；单引号内反斜杠为字面量，尾随 \ 安全）。</summary>
    private static string PrepareShellCommand(string cmd)
    {
        var trimmed = cmd.TrimStart();
        if (trimmed.Length == 0) return cmd;
        int sp = trimmed.IndexOfAny(new[] { ' ', '\t' });
        string verb = sp < 0 ? trimmed : trimmed.Substring(0, sp);
        if (!string.Equals(verb, "cd", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(verb, "set-location", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(verb, "sl", StringComparison.OrdinalIgnoreCase))
        {
            return cmd;
        }
        string arg = sp < 0 ? "" : trimmed.Substring(sp + 1).Trim();
        if (arg.Length == 0) return cmd;
        if (arg[0] == '\'' || arg[0] == '"') return cmd;           // 已带引号
        if (arg.IndexOf(' ') < 0 && arg.IndexOf('\t') < 0) return cmd; // 无空格无需处理
        return verb + " '" + arg + "'";
    }

    private void UpdateWorkingDirectory(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        string t = command.TrimStart();
        string? target = null;
        string tLower = t.ToLowerInvariant();
        if (tLower == "cd" || tLower == "set-location" || tLower == "sl")
        {
            target = "";
        }
        else if (tLower.StartsWith("cd ") || tLower.StartsWith("set-location ") || tLower.StartsWith("sl "))
        {
            target = t.Substring(t.IndexOf(' ')).Trim().Trim('"', '\'');
        }
        if (target == null) return;
        if (target.Length == 0 || target == "~") target = HomeDirectory;
        else if (target.StartsWith("~/") || target.StartsWith("~\\")) target = Path.Combine(HomeDirectory, target.Substring(2));
        try
        {
            string full = Path.GetFullPath(Path.Combine(_currentDirectory, target));
            if (Directory.Exists(full)) _currentDirectory = full;
        }
        catch { /* 路径无效：忽略 */ }
    }

    private static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? Environment.CurrentDirectory;

    /// <summary>Tab 补全：用输入行最后一个词匹配当前目录下的文件/子目录。
    /// 唯一匹配直接补全；多个候选有公共前缀先补前缀；否则显示候选列表提示。</summary>
    private void TryCompleteTab()
    {
        if (!_isRunning) return;
        var text = _currentInputBuffer.ToString();
        int lastSpace = text.LastIndexOf(' ');
        string word = lastSpace < 0 ? text : text.Substring(lastSpace + 1);
        if (word.Length == 0) return;

        string sep = OperatingSystem.IsWindows() ? "\\" : "/";
        string searchDir = _currentDirectory;
        string prefix = word;
        string dirPart = "";
        int lastSep = word.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep >= 0)
        {
            dirPart = word.Substring(0, lastSep + 1);
            prefix = word.Substring(lastSep + 1);
            string dirRaw = dirPart;
            if (dirRaw.StartsWith("~/") || dirRaw.StartsWith("~\\"))
                dirRaw = Path.Combine(HomeDirectory, dirRaw.Substring(2));
            else if (dirRaw.TrimEnd('\\', '/') == "~")
                dirRaw = HomeDirectory + sep;
            if (Path.IsPathRooted(dirRaw))
                searchDir = dirRaw;
            else
                searchDir = Path.Combine(_currentDirectory, dirRaw);
            searchDir = searchDir.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }
        else if (word.StartsWith("~/") || word.StartsWith("~\\"))
        {
            dirPart = word.Substring(0, word.IndexOf('/') < 0 ? word.IndexOf('\\') : word.IndexOf('/')) + sep;
            prefix = word.Substring(dirPart.Length - sep.Length);
            searchDir = HomeDirectory;
        }

        if (!Directory.Exists(searchDir)) return;

        List<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(searchDir, prefix + "*")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return; }
        if (names.Count == 0) return;

        if (names.Count == 1)
        {
            // 唯一匹配：直接补全（目录后追加分隔符便于继续深入）
            string completion = names[0];
            if (Directory.Exists(Path.Combine(searchDir, completion))) completion += sep;
            ReplaceInputWord(lastSpace + 1, dirPart + completion);
            return;
        }

        string common = LongestCommonPrefix(names);
        if (common.Length > prefix.Length)
        {
            ReplaceInputWord(lastSpace + 1, dirPart + common);
        }
        else
        {
            ShowCandidates(names);
        }
    }

    /// <summary>把输入行从 startIndex 起的部分替换为 replacement（本地回显 + 缓冲）。</summary>
    private void ReplaceInputWord(int startIndex, string replacement)
    {
        int len = _currentInputBuffer.Length - startIndex;
        if (len > 0)
        {
            _terminalBuffer?.MoveCursorLeft(len);
            _terminalBuffer?.DeleteChars(len);
        }
        _currentInputBuffer.Remove(startIndex, len);
        Echo(replacement);
        _currentInputBuffer.Append(replacement);
        RequestDisplayRefresh();
    }

    /// <summary>在终端追加候选列表并恢复输入行显示（提示符 + 当前输入内容）。</summary>
    private void ShowCandidates(List<string> candidates)
    {
        Echo("\r\n" + string.Join("   ", candidates));
        Echo("\r\n" + Prompt + _currentInputBuffer.ToString());
        // 提示符与输入行已经重新显示出来了，标记为"提示符在位"，避免之后再补一个
        _promptShown = true;
    }

    /// <summary>多个候选的最长公共前缀（忽略大小写）。</summary>
    private static string LongestCommonPrefix(List<string> names)
    {
        if (names.Count == 0) return "";
        string p = names[0];
        for (int i = 1; i < names.Count; i++)
        {
            int j = 0;
            while (j < p.Length && j < names[i].Length &&
                   char.ToLowerInvariant(p[j]) == char.ToLowerInvariant(names[i][j])) j++;
            p = p.Substring(0, j);
        }
        return p;
    }

    /// <summary>用历史命令替换当前输入行（本地显示 + 缓冲，等 Enter 才发送）。</summary>
    private void NavigateHistory(string? command)
    {
        if (command == null) return;
        // 清空当前输入行显示
        int len = _currentInputBuffer.Length;
        if (len > 0)
        {
            _terminalBuffer?.MoveCursorLeft(len);
            _terminalBuffer?.DeleteChars(len);
        }
        _currentInputBuffer.Clear();
        if (command.Length > 0)
        {
            Echo(command);
            _currentInputBuffer.Append(command);
        }
        RequestDisplayRefresh();
    }

    private void TerminalEditor_TextInput(object? sender, TextInputEventArgs e)
    {
        if (!_isRunning || _service == null) return;
        // 输入法确认：组合文本已由 e.Text 携带，清除组合显示（渲染重建时自然移除）
        _imePreedit = null;
        if (string.IsNullOrEmpty(e.Text)) return;
        char inputChar = e.Text[0];
        if (inputChar < 32) return;

        // 开始编辑输入行：确保提示符在位（并取消兜底计时器，避免它稍后再补一个提示符）
        BeginInputLine();
        // 字符只做本地回显与缓冲，整行在 Enter 时一并提交（避免 shell 回显叠加成双重显示）
        Echo(e.Text);
        _currentInputBuffer.Append(e.Text);
        e.Handled = true;
    }

    /// <summary>回显文本到终端缓冲（与 shell 输出走同一渲染路径）。</summary>
    private void Echo(string text)
    {
        if (string.IsNullOrEmpty(text) || _terminalBuffer == null) return;
        _terminalBuffer.Write(text);
        RequestDisplayRefresh();
    }

    /// <summary>输入法组合文本更新：记录并在输入行末尾显示（不进缓冲）。</summary>
    private void OnImePreeditChanged(string? preeditText)
    {
        _imePreedit = string.IsNullOrEmpty(preeditText) ? null : preeditText;
        RequestDisplayRefresh();
    }

    // ========== 终端尺寸 ==========

    private void UpdateTerminalSizeFromViewport()
    {
        if (_terminalBuffer == null) return;

        var (charWidth, charHeight) = MeasureCharSize();
        if (charWidth <= 0 || charHeight <= 0) return;

        double viewportWidth = GetAvailableTextWidth();
        double viewportHeight = GetAvailableTextHeight();
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        int cols = Math.Max(20, (int)(viewportWidth / charWidth));
        int rows = Math.Max(5, (int)(viewportHeight / charHeight));
        cols = Math.Min(cols, 500);
        rows = Math.Min(rows, 300);

        if (cols == _terminalBuffer.Width && rows == _terminalBuffer.Height) return;
        _terminalBuffer.Resize(cols, rows);
        TerminalSizeText.Text = $"{cols}x{rows}";
    }

    /// <summary>
    /// 终端可用文本宽度（DIP）：优先取滚动视图的真实视口宽度——它已经扣掉了内边距与纵向滚动条。
    /// 用编辑器整体宽度估算会把每行算宽（纵向滚动条一出现就更宽），于是输出比可视区宽、
    /// 冒出横向滚动条并自动向右滚，左侧的提示符就被顶出视野了。
    /// </summary>
    private double GetAvailableTextWidth()
    {
        double viewport = FindEditorScrollViewer()?.Viewport.Width ?? 0;
        if (viewport > 1) return viewport;
        double fallback = TerminalEditor.Bounds.Width
                          - TerminalEditor.Padding.Left - TerminalEditor.Padding.Right - 18;
        return fallback > 1 ? fallback : TerminalEditor.Bounds.Width;
    }

    /// <summary>终端可用文本高度（DIP）：同上，优先用滚动视图视口高度。</summary>
    private double GetAvailableTextHeight()
    {
        double viewport = FindEditorScrollViewer()?.Viewport.Height ?? 0;
        if (viewport > 1) return viewport;
        double fallback = TerminalEditor.Bounds.Height
                          - TerminalEditor.Padding.Top - TerminalEditor.Padding.Bottom;
        return fallback > 1 ? fallback : TerminalEditor.Bounds.Height;
    }

    /// <summary>编辑器内部的滚动视图（AvaloniaEdit 模板里的 PART_ScrollViewer），取不到返回 null。</summary>
    private ScrollViewer? FindEditorScrollViewer()
    {
        try
        {
            foreach (var visual in TerminalEditor.GetVisualDescendants())
            {
                if (visual is ScrollViewer scrollViewer) return scrollViewer;
            }
        }
        catch
        {
            // 视觉树尚未建立：调用方回退到按控件尺寸估算
        }
        return null;
    }

    private (double Width, double Height) MeasureCharSize()
    {
        try
        {
            var typeface = new Typeface(TerminalEditor.FontFamily, TerminalEditor.FontStyle, TerminalEditor.FontWeight);
            var formatted = new FormattedText(
                "W",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                TerminalEditor.FontSize,
                Brushes.White);
            double w = formatted.WidthIncludingTrailingWhitespace;
            double h = formatted.Height;
            if (w > 0 && h > 0) return (w, h);
        }
        catch
        {
            // 回退估算
        }
        double size = TerminalEditor.FontSize > 0 ? TerminalEditor.FontSize : 14;
        return (size * 0.6, size * 1.2);
    }

    // ========== 复制 / 粘贴 ==========

    private async void CopySelectedText()
    {
        var selectedText = TerminalEditor.SelectedText;
        if (string.IsNullOrEmpty(selectedText)) return;
        _clipboard ??= TopLevel.GetTopLevel(this)?.Clipboard;
        if (_clipboard == null) return;
        try
        {
            // 剪贴板必须在 UI 线程调用（不能 Task.Run 跨线程），异常吞掉避免崩溃
            await _clipboard.SetTextAsync(selectedText);
        }
        catch
        {
            // 剪贴板被占用/不可用时忽略
        }
    }

    private async void PasteText()
    {
        _clipboard ??= TopLevel.GetTopLevel(this)?.Clipboard;
        if (_clipboard == null) return;

        try
        {
            using var data = await _clipboard.TryGetDataAsync();
            var text = data != null ? await data.TryGetTextAsync() : null;
            if (string.IsNullOrEmpty(text) || !_isRunning) return;

            // 粘贴即开始编辑输入行：先补提示符，并取消兜底计时器
            BeginInputLine();
            // 剪贴板的 CRLF 归一为 LF：shell 逐行读取，多余的回车会变成"空命令"
            var payload = text.Replace("\r\n", "\n").Replace('\r', '\n');
            Echo(payload);
            _currentInputBuffer.Append(payload);
        }
        catch
        {
            // 剪贴板不可用/被占用时忽略
        }
    }

    // ========== 右键菜单 ==========

    /// <summary>
    /// 右键菜单即将弹出：已选中文本时直接复制并取消菜单（终端习惯：拖选 → 右键即复制），
    /// 复制后清空选区，这样下一次右键就会弹出菜单，可用其中的"粘贴"。
    /// </summary>
    private void TerminalContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (string.IsNullOrEmpty(TerminalEditor.SelectedText)) return;
        // CopySelectedText 在第一个 await 之前就同步取好了文本，所以随后清选区不影响复制
        CopySelectedText();
        TerminalEditor.SelectionLength = 0;
        e.Cancel = true;
    }

    private void CopyMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        CopySelectedText();
    }

    private void PasteMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        PasteText();
    }

    private void SelectAllMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        TerminalEditor.SelectAll();
    }

    private void ClearTerminalMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        _terminalBuffer?.ClearScreen();
        TerminalEditor.Clear();
        // 清屏后补回提示符与当前输入行（与 Ctrl+L 一致），否则只剩一片空白、看不到命令行
        var pending = _currentInputBuffer.ToString();
        Echo(pending.Length > 0 ? Prompt + pending : Prompt);
        _promptShown = true;
    }

    /// <summary>把输入焦点交给终端编辑器。</summary>
    /// <summary>供 AI 命令调用：在终端中执行一行命令（等同用户输入后回车）。</summary>
    public void ExecuteCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        if (!_isRunning || _service == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _currentInputBuffer.Clear();
            Echo("\r\n");
            // 命令行已提交：结束当前提示符，等完成信号再显示下一个
            _promptShown = false;
            _commandRunning = true;
            _service.SendData(PrepareShellCommand(command) + "\n");
            _commandHistory?.AddCommand(command);
            UpdateWorkingDirectory(command);
            if (!HasShellCompletionSignal) SchedulePromptFallback();
            RequestDisplayRefresh();
        });
    }

    public void FocusTerminal()
    {
        TerminalEditor.Focus();
    }

    /// <summary>关闭标签页时调用：终止 shell 进程。</summary>
    public Task ShutdownAsync()
    {
        _isRunning = false;
        StopPromptTimer();
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }
}
