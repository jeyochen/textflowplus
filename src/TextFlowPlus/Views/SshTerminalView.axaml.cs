using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using TextFlowPlus.Services;
using TextFlowPlus.Services.Localization;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextView = AvaloniaEdit.Rendering.TextView;
using Newtonsoft.Json;
using TextFlowPlus.Services.Ssh;
using TextFlowPlus.Services.Terminal;
using Avalonia.Input.Platform;
using Avalonia.Platform;

namespace TextFlowPlus.Views;

/// <summary>
/// SSH 终端视图（UserControl，可嵌入主窗口标签页）。
/// 由 SshTerminalDialog 改造而来：不再以独立窗口弹出，
/// 而是作为主窗口 FileTabControl 中一个标签页的内容承载。
/// </summary>
public partial class SshTerminalView : UserControl
{
    /// <summary>请求主窗口关闭当前标签页（SSH 连接断开时触发）。</summary>
    public event Action? CloseRequested;

    // 终端服务（底层通信层）
    private SshTerminalService? _sshService;

    // 终端缓冲区（终端逻辑层）
    private TerminalBuffer? _terminalBuffer;

    // ANSI 解析器（终端逻辑层）
    private AnsiEscapeParser? _ansiParser;

    // 命令历史记录服务
    private CommandHistoryService? _commandHistory;

    // 当前输入行缓冲区
    private readonly StringBuilder _currentInputBuffer = new();

    // UI 相关
    private bool _isConnected;
    private bool _hasConnected;
    private bool _userDisconnecting;

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;
    private string? _privateKeyPath;
    private bool _isInInteractiveMode; // 是否在 vim/top 等交互模式下
    private IClipboard? _clipboard;

    // 颜色高亮渲染器
    private TerminalColorizer? _colorizer;

    /// <summary>显示刷新是否已排队（合并高频缓冲区变更，避免重复重建文档）。</summary>
    private bool _displayRefreshPending;

    /// <summary>终端专用 IME 客户端（只读编辑器默认不注册，需自行提供以支持中文组合输入）。</summary>
    private TerminalImeClient? _imeClient;

    /// <summary>输入法当前组合文本（拼音等），显示在输入行末尾，确认后由 TextInput 替换。</summary>
    private string? _imePreedit;

    public SshTerminalView()
    {
        InitializeComponent();
        InitializeTerminal();
    }

    public SshTerminalView(string host, int port, string username)
    {
        InitializeComponent();
        InitializeTerminal();
        HostTextBox.Text = host;
        PortTextBox.Text = port.ToString();
        UsernameTextBox.Text = username;
        // 加载保存的密码
        LoadSavedPassword(host, port, username);
    }

    private void InitializeTerminal()
    {
        // 初始化终端编辑器
        TerminalEditor.IsReadOnly = true; // 禁止直接编辑，通过键盘事件输入
        TerminalEditor.Options.AllowScrollBelowDocument = true;
        TerminalEditor.Background = new SolidColorBrush(Color.FromRgb(12, 12, 12));
        TerminalEditor.Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204));
        // Consolas 优先（等宽），中文回退微软雅黑（Consolas 无中文字形，否则中文显示为方块）
        TerminalEditor.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
        TerminalEditor.FontSize = 14;
        TerminalEditor.ShowLineNumbers = false;

        // 初始化终端缓冲区
        _terminalBuffer = new TerminalBuffer(80, 24, 1000);
        _terminalBuffer.BufferChanged += OnBufferChanged;

        // 初始化 ANSI 解析器
        _ansiParser = new AnsiEscapeParser();
        _ansiParser.SetTerminalBuffer(_terminalBuffer);
        _ansiParser.TextOutput += OnAnsiTextOutput;
        _ansiParser.ClearScreen += OnAnsiClearScreen;

        // 初始化颜色渲染器
        _colorizer = new TerminalColorizer(_terminalBuffer);
        TerminalEditor.TextArea.TextView.LineTransformers.Add(_colorizer);

        // 设置选择文本时的背景色和前景色
        TerminalEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));
        TerminalEditor.TextArea.SelectionForeground = new SolidColorBrush(Color.FromRgb(255, 255, 255));

        // 右键不移动光标：否则右键会先折叠选区，导致"选中后右键复制"拿不到内容
        TerminalEditor.TextArea.RightClickMovesCaret = false;

        // 初始化命令历史记录
        _commandHistory = new CommandHistoryService(100);

        // 键盘事件
        TerminalEditor.TextArea.AddHandler(KeyDownEvent, TerminalEditor_KeyDown, handledEventsToo: true);

        // TextInput 事件 - 用于正确处理字符输入（包括 Caps Lock、键盘布局等）
        TerminalEditor.TextArea.AddHandler(TextInputEvent, TerminalEditor_TextInput, handledEventsToo: true);

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

        // 输入主机/端口/用户后自动尝试加载保存的密码
        HostTextBox.TextChanged += (_, _) => TryLoadSavedPassword();
        PortTextBox.TextChanged += (_, _) => TryLoadSavedPassword();
        UsernameTextBox.TextChanged += (_, _) => TryLoadSavedPassword();

        // 尺寸变化时调整终端（UserControl 无窗口 Resized，用 SizeChanged 等效）
        SizeChanged += (_, _) => UpdateTerminalSizeFromViewport();
    }

    /// <summary>
    /// 标签页关闭时调用：异步断开 SSH 连接、释放服务并解绑事件。
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_sshService != null)
            {
                await _sshService.DisconnectAsync();
                _sshService.Dispose();
                _sshService = null;
            }
        }
        catch { }

        if (_ansiParser != null)
        {
            _ansiParser.TextOutput -= OnAnsiTextOutput;
            _ansiParser.ClearScreen -= OnAnsiClearScreen;
        }
        if (_terminalBuffer != null)
        {
            _terminalBuffer.BufferChanged -= OnBufferChanged;
        }
    }

    private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    private async System.Threading.Tasks.Task ConnectAsync()
    {
        string host = HostTextBox.Text?.Trim() ?? "";
        string portText = PortTextBox.Text?.Trim() ?? "22";
        string username = UsernameTextBox.Text?.Trim() ?? "";
        string password = PasswordTextBox.Text ?? "";

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username))
        {
            StatusText.Text = "请输入主机 IP 和用户名";
            return;
        }

        if (!int.TryParse(portText, out int port) || port <= 0 || port > 65535)
        {
            StatusText.Text = "端口号无效";
            return;
        }

        if (!int.TryParse(TimeoutTextBox.Text, out int timeout) || timeout <= 0)
        {
            timeout = 10;
        }

        try
        {
            ConnectButton.IsEnabled = false;
            StatusText.Text = "正在连接...";

            // 先按编辑器实际可视区域计算行列数，再建 PTY：
            // 初始尺寸若写死 80x24，服务器的 `ls` 会一直按 80 列排版。
            var (initCols, initRows) = ComputeInitialTerminalSize();

            var connectionInfo = new SshConnectionInfo
            {
                Host = host,
                Port = port,
                Username = username,
                Password = string.IsNullOrEmpty(password) ? null : password,
                PrivateKeyPath = _privateKeyPath,
                TimeoutSeconds = timeout,
                KeepAliveIntervalSeconds = 5,
                TerminalWidth = initCols,
                TerminalHeight = initRows
            };

            _terminalBuffer?.Resize(initCols, initRows);
            TerminalSizeText.Text = $"{initCols}x{initRows}";

            _sshService = new SshTerminalService();
            _sshService.ConnectionStateChanged += OnConnectionStateChanged;
            _sshService.DataReceived += OnDataReceived;
            _sshService.ErrorOccurred += OnErrorOccurred;

            await _sshService.ConnectAsync(connectionInfo);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"连接失败：{ex.Message}";
            UpdateConnectionStatus(false);
            ConnectButton.IsEnabled = true;
        }
    }

    private void DisconnectButton_Click(object? sender, RoutedEventArgs e)
    {
        Disconnect();
    }

    private async void Disconnect()
    {
        _userDisconnecting = true;
        try
        {
            if (_sshService != null)
            {
                await _sshService.DisconnectAsync();
                _sshService.Dispose();
                _sshService = null;
            }
            _isConnected = false;
            UpdateConnectionStatus(false);
            StatusText.Text = "已断开连接";
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            HostInfoText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"断开连接失败：{ex.Message}";
        }
    }

    private void PrivateKeyButton_Click(object? sender, RoutedEventArgs e)
    {
        _dialogTask = BrowsePrivateKeyAsync();
    }

    private async Task BrowsePrivateKeyAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择私钥文件",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("PPK 文件") { Patterns = new[] { "*.ppk" } } }
        });

        if (files != null && files.Count > 0)
        {
            _privateKeyPath = files[0].Path.LocalPath;
            PrivateKeyPathText.Text = Path.GetFileName(_privateKeyPath);
        }
    }

    /// <summary>连接前按当前可视区域估算初始 PTY 尺寸（布局尚未完成时回退 80x24）。</summary>
    private (int Cols, int Rows) ComputeInitialTerminalSize()
    {
        try
        {
            var (charWidth, charHeight) = MeasureCharSize();
            double w = TerminalEditor.Bounds.Width;
            double h = TerminalEditor.Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                // UserControl 无 ClientSize，用自身 Bounds 兜底（高度扣除面板与状态栏）
                w = Bounds.Width;
                h = Bounds.Height - 120;
            }
            if (w > 0 && h > 0 && charWidth > 0 && charHeight > 0)
            {
                int c = Math.Clamp((int)(w / charWidth), 20, 500);
                int r = Math.Clamp((int)(h / charHeight), 5, 300);
                return (c, r);
            }
        }
        catch { /* 回退默认值 */ }
        return (80, 24);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isConnected = e.IsConnected;
            UpdateConnectionStatus(e.IsConnected);
            StatusText.Text = e.StatusMessage;
            // 已连接后断开：用户主动断开不关闭标签页；exit 断开自动关闭
            if (!e.IsConnected && _hasConnected)
            {
                bool userInitiated = _userDisconnecting;
                _userDisconnecting = false;
                if (!userInitiated)
                {
                    CloseRequested?.Invoke();
                    return;
                }
            }

            if (e.IsConnected && _sshService != null)
            {
                _userDisconnecting = false;
                _hasConnected = true;

                // 连接成功后按真实可视区域校准一次 PTY 尺寸（此时布局已完成，
                // 实测字符尺寸准确），确保 `ls` 多列排版宽度与窗口一致。
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateTerminalSizeFromViewport();
                    TerminalEditor.Focus();
                }, DispatcherPriority.Background);

                var info = _sshService.ConnectionInfo;
                if (info != null)
                {
                    HostInfoText.Text = $"{info.Username}@{info.Host}:{info.Port}";

                    // 只有勾选"保存密码"时才保存密码
                    string? passwordToSave = SavePasswordCheckBox.IsChecked == true ? info.Password : null;
                    AddToRecentHostsStatic(info.Host, info.Port, info.Username, passwordToSave);
                }
            }
        });
    }

    private void OnDataReceived(object? sender, TerminalOutputEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            System.Diagnostics.Debug.WriteLine($"[SSH RECV] Data: {System.Text.RegularExpressions.Regex.Escape(e.Data)}");
            _ansiParser?.ParseAndProcess(e.Data);
        });
    }

    private void OnErrorOccurred(object? sender, SshErrorEventArgs e)
    {
        // 把常见 SSH 错误翻译成可操作的中文提示：
        // 认证失败/主机不可达等只显示原始英文往往让用户不知道下一步该做什么。
        var friendly = DescribeSshError(e.Exception);
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = friendly ?? e.ErrorMessage;

            // 认证/连接类错误：同时写入终端区，避免用户只盯着状态栏错过关键信息
            if (friendly != null)
            {
                AppendLocalNotice(friendly);
            }
        });
    }

    /// <summary>把底层异常翻译为可操作提示；无法识别时返回 null（沿用原始消息）。</summary>
    private static string? DescribeSshError(Exception? ex)
    {
        if (ex == null) return null;
        return ex switch
        {
            Renci.SshNet.Common.SshAuthenticationException =>
                "❌ 认证失败：用户名、密码或私钥不正确。请检查凭据后重试（若服务器禁用了密码登录，请改用私钥）。",
            Renci.SshNet.Common.SshConnectionException sce when sce.Message.Contains("refused", StringComparison.OrdinalIgnoreCase) =>
                "❌ 连接被拒绝：目标主机的 SSH 端口未开放或被防火墙拦截。请确认端口号（默认 22）与服务状态。",
            System.Net.Sockets.SocketException =>
                "❌ 网络不可达：请检查主机地址、网络连接与防火墙设置。",
            TimeoutException =>
                "❌ 连接超时：主机无响应。请检查地址与网络，或调大超时时间。",
            _ => null,
        };
    }

    /// <summary>在终端区追加一条本地提示（以醒目前缀区分于远端输出）。</summary>
    private void AppendLocalNotice(string message)
    {
        if (_ansiParser == null || _terminalBuffer == null) return;
        // 换到新行后输出，避免与远端提示符粘连
        _ansiParser.ParseAndProcess("\r\n\x1b[33m" + message + "\x1b[0m\r\n");
        RequestDisplayRefresh();
    }

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        // 合并刷新：一次命令（如 ls -lR）会产生成百上千次缓冲区变更，
        // 每次都重建整篇文档会卡死 UI。这里只在 UI 空闲时刷新一次。
        RequestDisplayRefresh();
    }

    /// <summary>输入法组合文本更新：记录并在输入行末尾显示（不进缓冲）。</summary>
    private void OnImePreeditChanged(string? preeditText)
    {
        _imePreedit = string.IsNullOrEmpty(preeditText) ? null : preeditText;
        RequestDisplayRefresh();
    }

    /// <summary>请求刷新终端显示（自动合并同一批数据的高频变更）。</summary>
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

    private void OnAnsiTextOutput(object? sender, string text)
    {
        // 文本已通过 TerminalBuffer 处理
    }

    private void OnAnsiClearScreen(object? sender, EventArgs e)
    {
        // 缓冲区已由 TerminalBuffer.Clear() 清空；这里只重绘，不再单独清空编辑器文档，
        // 否则会绕过统一渲染路径造成内容与缓冲区不一致（闪烁/错行）。
        RequestDisplayRefresh();
    }

    private void UpdateTerminalDisplay()
    {
        if (_terminalBuffer == null) return;

        var document = TerminalEditor.Document;

        // 记录用户当前选区：整篇重写（document.Text = ...）会清掉部分选区，
        // 表现为"拖选文本后远端一有输出就复制不到内容"。重写后按原偏移恢复。
        int savedSelStart = TerminalEditor.SelectionStart;
        int savedSelLength = TerminalEditor.SelectionLength;
        var lines = _terminalBuffer.Lines;
        var sb = new StringBuilder(lines.Count * (_terminalBuffer.Width + 1));

        // 只渲染到最后一个有内容的行（尾部空行不渲染，避免文档无意义变长）
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
                // 行尾空格不写入文档：既减小文档体积，也避免行尾色块残留
                sb.Append(c.Char);
            }
        }

        // 每行去掉行尾空格后写入（TrimEnd 只作用于整段文本的末尾行，
        // 因此逐行拼接时按行处理）
        var rendered = sb.ToString();

        // 输入法组合文本（拼音等）显示在当前输入行末尾（不进缓冲，
        // 确认后由 TextInput 事件携带最终文本发送到服务器并清除组合显示）
        if (!string.IsNullOrEmpty(_imePreedit))
        {
            rendered += _imePreedit;
        }

        if (!string.Equals(document.Text, rendered, StringComparison.Ordinal))
        {
            document.Text = rendered;
            // 恢复选区必须放在重写之后、定位光标之前：实测定位光标不会清掉选区，
            // 反过来（先定位光标再恢复）会让光标跳进选区中间。
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

        // 触发颜色刷新
        _colorizer?.Invalidate();

        // 滚动到光标位置（仅当光标不在可视区时才滚动，避免每帧跳动）
        TerminalEditor.ScrollToLine(Math.Min(_terminalBuffer.CursorY + 1, document.LineCount));
    }

    /// <summary>
    /// 整篇重写后恢复用户选区：AvaloniaEdit 的 <c>Document.Text = ...</c> 是全量替换，
    /// 会把部分选区清成空选区（全选不受影响），导致用户"选中后远端一有输出就复制不到内容"。
    /// 偏移按新文档长度裁剪；没有选区时什么都不做（不干扰用户随后的鼠标操作）。
    /// </summary>
    private void RestoreSelectionAfterRewrite(int start, int length)
    {
        if (length <= 0) return;
        int textLength = TerminalEditor.Document.TextLength;
        int s = Math.Clamp(start, 0, textLength);
        int l = Math.Clamp(length, 0, textLength - s);
        if (l > 0) TerminalEditor.Select(s, l);
    }

    private void TerminalEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isConnected || _sshService == null) return;

        // 特殊按键处理
        switch (e.Key)
        {
            case Key.Enter:
                _sshService.SendKey("\r");
                // 保存命令到历史记录
                var command = _currentInputBuffer.ToString();
                if (!string.IsNullOrEmpty(command))
                {
                    _commandHistory?.AddCommand(command);
                    _currentInputBuffer.Clear();
                }
                e.Handled = true;
                return;

            case Key.Back:
                _sshService.SendKey("\b");
                // 本地缓冲区也删除一个字符
                if (_currentInputBuffer.Length > 0)
                    _currentInputBuffer.Remove(_currentInputBuffer.Length - 1, 1);
                e.Handled = true;
                return;

            case Key.Delete:
                _sshService.SendKey("\x1b[3~");
                e.Handled = true;
                return;

            case Key.Left:
                _sshService.SendKey("\x1b[D");
                e.Handled = true;
                return;

            case Key.Right:
                _sshService.SendKey("\x1b[C");
                e.Handled = true;
                return;

            case Key.Up:
                // 直接发送上箭头的ANSI转义序列
                // 使用 xterm 风格的箭头键序列
                _sshService.SendKey("\x1b[A");
                e.Handled = true;
                return;

            case Key.Down:
                // 直接发送下箭头的ANSI转义序列
                _sshService.SendKey("\x1b[B");
                e.Handled = true;
                return;

            case Key.Home:
                _sshService.SendKey("\x1b[H");
                e.Handled = true;
                return;

            case Key.End:
                _sshService.SendKey("\x1b[F");
                e.Handled = true;
                return;

            case Key.Tab:
                _sshService.SendKey("\t");
                e.Handled = true;
                return;

            case Key.Escape:
                _sshService.SendKey("\x1b");
                e.Handled = true;
                return;

            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                // 主流终端行为（Windows Terminal / VS Code）：有选中文本时复制，
                // 无选中时才把 Ctrl+C 作为中断信号发给远端。
                if (!string.IsNullOrEmpty(TerminalEditor.SelectedText))
                {
                    CopySelectedText();
                }
                else
                {
                    _sshService.SendKey("\x03");
                    _currentInputBuffer.Clear();
                }
                e.Handled = true;
                return;

            case Key.C when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                // Ctrl+Shift+C 复制
                CopySelectedText();
                e.Handled = true;
                return;

            case Key.V when e.KeyModifiers == KeyModifiers.Control:
                // Ctrl+V 粘贴到远端（与 Windows Terminal / VS Code 一致）
                PasteText();
                e.Handled = true;
                return;

            case Key.V when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                // Ctrl+Shift+V 粘贴
                PasteText();
                e.Handled = true;
                return;

            case Key.C when e.KeyModifiers == KeyModifiers.Meta:
                // macOS Cmd+C 复制
                CopySelectedText();
                e.Handled = true;
                return;

            case Key.V when e.KeyModifiers == KeyModifiers.Meta:
                // macOS Cmd+V 粘贴
                PasteText();
                e.Handled = true;
                return;

            case Key.D when e.KeyModifiers == KeyModifiers.Control:
                _sshService.SendKey("\x04");
                e.Handled = true;
                return;

            case Key.Z when e.KeyModifiers == KeyModifiers.Control:
                _sshService.SendKey("\x1a");
                e.Handled = true;
                return;

            case Key.L when e.KeyModifiers == KeyModifiers.Control:
                _sshService.SendKey("\x1b[2J\x1b[H");
                e.Handled = true;
                return;

            case Key.Insert:
                if (e.KeyModifiers == KeyModifiers.Control)
                {
                    // Ctrl+Insert 复制
                    CopySelectedText();
                    e.Handled = true;
                }
                else if (e.KeyModifiers == KeyModifiers.Shift)
                {
                    // Shift+Insert 粘贴
                    PasteText();
                    e.Handled = true;
                }
                return;
        }

        // 普通字符输入由 TextInput 事件处理，这里不需要处理
    }

    /// <summary>
    /// TextInput 事件处理 - 正确处理字符输入（包括 Caps Lock、键盘布局等）
    /// </summary>
    private void TerminalEditor_TextInput(object? sender, TextInputEventArgs e)
    {
        if (!_isConnected || _sshService == null) return;
        // 输入法确认：组合文本已由 e.Text 携带，清除组合显示（渲染重建时自然移除）
        _imePreedit = null;
        if (string.IsNullOrEmpty(e.Text)) return;

        // 只处理单个字符输入
        char inputChar = e.Text[0];

        // 过滤掉控制字符（通过 KeyDown 事件处理）
        if (inputChar < 32) return;

        // 发送字符到服务器
        _sshService.SendKey(e.Text);
        _currentInputBuffer.Append(e.Text);

        e.Handled = true;
    }

    /// <summary>
    /// 依据编辑器实际可用像素与**实测字符尺寸**计算行列数并下发 PTY window-change。
    /// 不能用写死的 8.4x16.8 估算：字号/字体/DPI 一变就会算错，
    /// 列数偏小会让服务器的 `ls` 按更窄宽度排版（表现为单列或提前折行）。
    /// </summary>
    /// <summary>主界面工具栏字体大小联动：设置终端字号并重算行列。</summary>
    /// <summary>供 AI 命令调用：在 SSH 终端中执行一行命令（发送整行并回车）。</summary>
    public void ExecuteCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        if (!_isConnected || _sshService == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _sshService.SendData(command);
            _sshService.SendKey("\r");
            _commandHistory?.AddCommand(command);
            _currentInputBuffer.Clear();
            RequestDisplayRefresh();
        });
    }

    public void SetTerminalFontSize(int size)
    {
        if (size < 8 || size > 40) return;
        TerminalEditor.FontSize = size;
        UpdateTerminalSizeFromViewport();
    }

    private void UpdateTerminalSizeFromViewport()
    {
        if (_terminalBuffer == null) return;

        var (charWidth, charHeight) = MeasureCharSize();
        if (charWidth <= 0 || charHeight <= 0) return;

        // 编辑区可视尺寸（扣除内边距/滚动条）
        double viewportWidth = TerminalEditor.Bounds.Width;
        double viewportHeight = TerminalEditor.Bounds.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            // UserControl 无 ClientSize，用自身 Bounds 兜底（高度扣除面板与状态栏）
            viewportWidth = Bounds.Width;
            viewportHeight = Bounds.Height - 120;
        }
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        int cols = Math.Max(20, (int)(viewportWidth / charWidth));
        int rows = Math.Max(5, (int)(viewportHeight / charHeight));

        // 上限保护：避免异常尺寸导致超大缓冲区
        cols = Math.Min(cols, 500);
        rows = Math.Min(rows, 300);

        if (cols == _terminalBuffer.Width && rows == _terminalBuffer.Height) return;

        _terminalBuffer.Resize(cols, rows);
        _sshService?.SetTerminalSize(cols, rows);
        TerminalSizeText.Text = $"{cols}x{rows}";
    }

    /// <summary>
    /// 实测单个字符的像素尺寸：用当前字体与字号格式化一个字符并测量。
    /// 拿不到（字体未加载/尚未布局）时回退到按字号估算。
    /// </summary>
    private (double Width, double Height) MeasureCharSize()
    {
        try
        {
            var typeface = new Typeface(TerminalEditor.FontFamily, TerminalEditor.FontStyle, TerminalEditor.FontWeight);
            var formatted = new FormattedText(
                "W",
                System.Globalization.CultureInfo.InvariantCulture,
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
            // 测量失败时走回退估算
        }

        // 回退：等宽字体字符宽度约为字号的 0.6，行高约为字号的 1.2
        double size = TerminalEditor.FontSize > 0 ? TerminalEditor.FontSize : 14;
        return (size * 0.6, size * 1.2);
    }

    private void UpdateConnectionStatus(bool connected)
    {
        ConnectionStatusText.Text = connected ? "已连接" : "未连接";
        ConnectionStatusDot.Fill = new SolidColorBrush(
            connected ? Color.FromRgb(0, 180, 0) : Color.FromRgb(128, 128, 128));
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        HostTextBox.IsEnabled = !connected;
        PortTextBox.IsEnabled = !connected;
        UsernameTextBox.IsEnabled = !connected;
        PasswordTextBox.IsEnabled = !connected;
        TimeoutTextBox.IsEnabled = !connected;
        PrivateKeyButton.IsEnabled = !connected;
    }

    // 复制选中文本（Ctrl+Shift+C 或鼠标右键菜单）
    private async void CopySelectedText()
    {
        var selectedText = TerminalEditor.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            StatusText.Text = "未选中内容：先用鼠标拖选要复制的文本";
            return;
        }

        _clipboard ??= TopLevel.GetTopLevel(this)?.Clipboard;
        if (_clipboard == null) return;

        try
        {
            // 剪贴板必须在 UI 线程调用（不能 Task.Run 跨线程），异常吞掉避免崩溃
            await _clipboard.SetTextAsync(selectedText);
            StatusText.Text = $"已复制 {selectedText.Length} 个字符";
        }
        catch (Exception ex)
        {
            // 剪贴板被其他进程占用/不可用时忽略，但给出提示便于排查
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    // 粘贴文本（Ctrl+V / Ctrl+Shift+V / 右键菜单"粘贴"）
    private async void PasteText()
    {
        _clipboard ??= TopLevel.GetTopLevel(this)?.Clipboard;
        if (_clipboard == null) return;

        try
        {
            using var data = await _clipboard.TryGetDataAsync();
            var text = data != null ? await data.TryGetTextAsync() : null;
            if (string.IsNullOrEmpty(text) || !_isConnected || _sshService == null) return;

            // 换行折算：剪贴板里的多行文本是 CRLF，而终端里"回车"是单个 CR。
            // 原样发送 CRLF 等于敲了两次回车（远端会多执行一条空命令），
            // 所以丢弃 CR、把 LF 换成 CR —— 与 Windows Terminal / xterm 的粘贴行为一致。
            var payload = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c == '\r') continue;
                payload.Append(c == '\n' ? '\r' : c);
            }

            // 整段一次性写入：逐字符发送在粘贴长文本时会产生成百上千次 SSH 写入
            _sshService.SendData(payload.ToString());
        }
        catch
        {
            // 剪贴板不可用/被占用时忽略；通道断开由 SendData 内部处理
        }
    }

    #region 右键菜单事件处理

    /// <summary>
    /// 右键菜单即将弹出：已选中文本时直接复制并取消菜单
    /// （终端习惯：拖选 → 右键即复制），复制后清空选区，
    /// 这样下一次右键就会弹出菜单，可用其中的"粘贴/全选/清除/断开"。
    /// </summary>
    private void TerminalContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (string.IsNullOrEmpty(TerminalEditor.SelectedText)) return;
        // CopySelectedText 在第一个 await 之前就同步取好了文本，所以随后清选区不影响复制
        CopySelectedText();
        TerminalEditor.SelectionLength = 0;
        e.Cancel = true;
    }

    /// <summary>
    /// 复制菜单项点击
    /// </summary>
    private void CopyMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        CopySelectedText();
    }

    /// <summary>
    /// 粘贴菜单项点击
    /// </summary>
    private void PasteMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        PasteText();
    }

    /// <summary>
    /// 全选菜单项点击
    /// </summary>
    private void SelectAllMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        TerminalEditor.SelectAll();
    }

    /// <summary>
    /// 清除终端菜单项点击
    /// </summary>
    private void ClearTerminalMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_terminalBuffer != null)
        {
            _terminalBuffer.ClearScreen();
        }
        TerminalEditor.Clear();
    }

    /// <summary>
    /// 断开连接菜单项点击
    /// </summary>
    private void DisconnectMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        DisconnectButton_Click(sender, e);
    }

    #endregion

    // ========== 最近连接 ==========
    private static List<SshRecentHostInfo> LoadRecentHosts()
    {
        try
        {
            var path = AppPaths.LocalAppDataFile("ssh_recent_hosts.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<List<SshRecentHostInfo>>(json) ?? new List<SshRecentHostInfo>();
            }
        }
        catch { }
        return new List<SshRecentHostInfo>();
    }

    private static void DeleteRecentHost(string host, int port, string username)
    {
        try
        {
            var path = AppPaths.LocalAppDataFile("ssh_recent_hosts.json");
            if (!File.Exists(path)) return;
            var hosts = JsonConvert.DeserializeObject<List<SshRecentHostInfo>>(File.ReadAllText(path)) ?? new List<SshRecentHostInfo>();
            hosts.RemoveAll(h => h.Host == host && h.Port == port && h.Username == username);
            File.WriteAllText(path, JsonConvert.SerializeObject(hosts));
            App.APP?.RefreshSshRecentHostsMenu();
        }
        catch { }
    }

    /// <summary>点击"最近连接"弹出下拉列表：每条记录支持连接/删除。</summary>
    private void RecentButton_Click(object? sender, RoutedEventArgs e)
    {
        var hosts = LoadRecentHosts();
        var flyout = new Flyout { Placement = PlacementMode.Bottom };
        var root = new StackPanel { Width = 340, MaxHeight = 440 };
        root.Children.Add(new TextBlock
        {
            Text = LangService.T("最近连接"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 8, 10, 4)
        });

        if (hosts.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = LangService.T("暂无最近连接记录"),
                Margin = new Thickness(10, 6),
                Foreground = Brushes.Gray
            });
        }
        else
        {
            var scroll = new ScrollViewer { MaxHeight = 380 };
            var list = new StackPanel();
            foreach (var h in hosts)
            {
                var row = new Grid
                {
                    Margin = new Thickness(6, 3),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Auto)
                    }
                };
                var info = new TextBlock
                {
                    Text = $"{h.Username}@{h.Host}:{h.Port}",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var connBtn = new Button
                {
                    Content = LangService.T("连接"),
                    Classes = { "primary" },
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(10, 3)
                };
                var delBtn = new Button
                {
                    Content = LangService.T("删除"),
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(10, 3)
                };
                string host = h.Host;
                int port = h.Port;
                string user = h.Username;
                string? pwd = string.IsNullOrEmpty(h.Password) ? null : PasswordProtector.Decrypt(h.Password);
                connBtn.Click += async (_, _) =>
                {
                    flyout.Hide();
                    await ConnectToRecent(host, port, user, pwd);
                };
                delBtn.Click += (_, _) =>
                {
                    flyout.Hide();
                    DeleteRecentHost(host, port, user);
                    RecentButton_Click(sender, e);
                };
                Grid.SetColumn(info, 0);
                Grid.SetColumn(connBtn, 1);
                Grid.SetColumn(delBtn, 2);
                row.Children.Add(info);
                row.Children.Add(connBtn);
                row.Children.Add(delBtn);
                list.Children.Add(row);
            }
            scroll.Content = list;
            root.Children.Add(scroll);
        }

        flyout.Content = root;
        FlyoutBase.SetAttachedFlyout(RecentButton, flyout);
        FlyoutBase.ShowAttachedFlyout(RecentButton);
    }

    /// <summary>从最近连接记录发起连接：无保存密码时提示输入。</summary>
    private async Task ConnectToRecent(string host, int port, string username, string? savedPassword)
    {
        HostTextBox.Text = host;
        PortTextBox.Text = port.ToString();
        UsernameTextBox.Text = username;
        string password = savedPassword ?? "";
        if (string.IsNullOrEmpty(password))
        {
            var result = await PromptPasswordAsync(host, username);
            if (result == null) return;
            password = result;
        }
        PasswordTextBox.Text = password;
        await ConnectAsync();
    }

    /// <summary>弹出密码输入窗口；取消返回 null。</summary>
    private async Task<string?> PromptPasswordAsync(string host, string username)
    {
        var owner = TopLevel.GetTopLevel(RecentButton) as Window;
        var win = new Window
        {
            Title = LangService.T("输入密码") + $" - {username}@{host}",
            Width = 380,
            Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = LangService.T("请输入密码"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{username}@{host}",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.Gray
        });
        var pwd = new TextBox { Watermark = LangService.T("密码"), PasswordChar = '*' };
        panel.Children.Add(pwd);
        // 打开后把输入光标定位到密码框（构造期聚焦无效，窗口打开后才有效）
        win.Opened += (_, _) => pwd.Focus();
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12
        };
        string? result = null;
        var ok = new Button { Content = LangService.T("确定"), Classes = { "primary" }, Width = 80 };
        var cancel = new Button { Content = LangService.T("取消"), Width = 80 };
        ok.Click += (_, _) => { result = pwd.Text ?? ""; win.Close(); };
        cancel.Click += (_, _) => { result = null; win.Close(); };
        pwd.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { result = pwd.Text ?? ""; win.Close(); }
        };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        panel.Children.Add(btnRow);
        win.Content = panel;
        if (owner != null) await win.ShowDialog(owner);
        else win.Show();
        return result;
    }

    // 添加最近连接记录
    public static void AddToRecentHostsStatic(string host, int port, string username, string? password = null)
    {
        try
        {
            var appDir = AppPaths.LocalAppDataDir;
            Directory.CreateDirectory(appDir);
            var path = Path.Combine(appDir, "ssh_recent_hosts.json");

            List<SshRecentHostInfo> hosts;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                hosts = JsonConvert.DeserializeObject<List<SshRecentHostInfo>>(json) ?? new List<SshRecentHostInfo>();
            }
            else
            {
                hosts = new List<SshRecentHostInfo>();
            }

            hosts.RemoveAll(h => h.Host == host && h.Port == port && h.Username == username);
            hosts.Insert(0, new SshRecentHostInfo
            {
                Host = host,
                Port = port,
                Username = username,
                LastConnected = DateTime.Now,
                Password = string.IsNullOrEmpty(password) ? null : PasswordProtector.Encrypt(password)
            });

            if (hosts.Count > 20)
            {
                hosts = hosts.Take(20).ToList();
            }

            var jsonOut = JsonConvert.SerializeObject(hosts);
            File.WriteAllText(path, jsonOut);

            App.APP?.RefreshSshRecentHostsMenu();
            System.Diagnostics.Debug.WriteLine($"Saved recent host: {username}@{host}:{port}, total: {hosts.Count}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save recent hosts error: {ex.Message}");
        }
    }

    /// <summary>输入主机等信息后自动尝试加载保存的密码（用户已手动输入密码时不覆盖）。</summary>
    private void TryLoadSavedPassword()
    {
        if (!string.IsNullOrEmpty(PasswordTextBox.Text)) return;
        var host = HostTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(host)) return;
        if (!int.TryParse(PortTextBox.Text?.Trim(), out int port)) port = 22;
        var user = UsernameTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(user)) return;
        LoadSavedPassword(host, port, user);
    }

    // 加载保存的密码
    private void LoadSavedPassword(string host, int port, string username)
    {
        try
        {
            var appDir = AppPaths.LocalAppDataDir;
            var path = Path.Combine(appDir, "ssh_recent_hosts.json");

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var hosts = JsonConvert.DeserializeObject<List<SshRecentHostInfo>>(json);
                var savedHost = hosts?.FirstOrDefault(h => h.Host == host && h.Port == port && h.Username == username);
                if (savedHost != null && !string.IsNullOrEmpty(savedHost.Password))
                {
                    PasswordTextBox.Text = PasswordProtector.Decrypt(savedHost.Password);
                    // 如果之前保存了密码，默认选中保存密码复选框
                    SavePasswordCheckBox.IsChecked = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load saved password error: {ex.Message}");
        }
    }
}
