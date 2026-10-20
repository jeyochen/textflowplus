using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextView = AvaloniaEdit.Rendering.TextView;
using Newtonsoft.Json;
using TextFlowPlus.Services.Ssh;
using TextFlowPlus.Services;
using Avalonia.Input.Platform;
using Avalonia.Platform;

namespace TextFlowPlus.Views;

public partial class SshTerminalDialog : Window
{
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

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;
    private string? _privateKeyPath;
    private bool _isInInteractiveMode; // 是否在 vim/top 等交互模式下
    private IClipboard? _clipboard;
    
    // 颜色高亮渲染器
    private TerminalColorizer? _colorizer;

    /// <summary>显示刷新是否已排队（合并高频缓冲区变更，避免重复重建文档）。</summary>
    private bool _displayRefreshPending;

    public SshTerminalDialog()
    {
        InitializeComponent();
        InitializeTerminal();
        SyncFontFromMainWindow();
    }

    public SshTerminalDialog(string host, int port, string username)
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
        TerminalEditor.FontFamily = new FontFamily("Consolas");
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
        // 窗口大小变化时调整终端
        this.Resized += OnWindowResized;
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
                var cs = ClientSize;
                w = cs.Width;
                h = cs.Height - 120;
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

            if (e.IsConnected && _sshService != null)
            {
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
        if (!string.Equals(document.Text, rendered, StringComparison.Ordinal))
        {
            document.Text = rendered;
            // 恢复选区必须放在重写之后、定位光标之前：实测定位光标不会清掉选区，
            // 反过来（先定位光标再恢复）会让光标跳进选区中间。
            RestoreSelectionAfterRewrite(savedSelStart, savedSelLength);
        }

        // 更新光标位置
        var docLines = document.Lines;
        if (docLines.Count > 0 && _terminalBuffer.CursorY < docLines.Count)
        {
            var line = docLines[_terminalBuffer.CursorY];
            TerminalEditor.CaretOffset = line.Offset + Math.Min(_terminalBuffer.CursorX, line.Length);
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

    /// <summary>主界面工具栏字体大小联动：设置终端字号并重算行列。</summary>
    public void SetTerminalFontSize(int size)
    {
        if (size < 8 || size > 40) return;
        TerminalEditor.FontSize = size;
        UpdateTerminalSizeFromViewport();
    }

    /// <summary>打开时同步主界面工具栏的字体大小。</summary>
    private void SyncFontFromMainWindow()
    {
        try
        {
            Window? main = null;
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var w in desktop.Windows)
                {
                    if (w is MainWindow mw) { main = mw; break; }
                }
            }
            if (main is MainWindow mw2 && mw2.FontSizeComboBox?.SelectedItem is ComboBoxItem cbi &&
                double.TryParse(cbi.Content?.ToString(), out double size))
            {
                SetTerminalFontSize((int)Math.Round(size));
            }
        }
        catch { }
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

    private string? GetKeyChar(KeyEventArgs e)
    {
        var key = e.Key;

        // 字母 - 使用 TextInput 事件处理，自动处理 Caps Lock 和键盘布局
        // 这里保持基本处理，实际字符由 TextInput 事件处理
        if (key >= Key.A && key <= Key.Z)
        {
            char c = (char)('a' + (key - Key.A));
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                c = char.ToUpper(c);
            return c.ToString();
        }

        // 数字
        if (key >= Key.D0 && key <= Key.D9)
        {
            if (e.KeyModifiers == KeyModifiers.None)
                return ((char)('0' + (key - Key.D0))).ToString();
            if (e.KeyModifiers == KeyModifiers.Shift)
            {
                var shiftChars = ")!@#$%^&*(";
                int idx = key - Key.D0;
                if (idx >= 0 && idx < shiftChars.Length)
                    return shiftChars[idx].ToString();
            }
        }

        // 小键盘数字
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        }

        // 特殊字符
        switch (key)
        {
            case Key.Space: return " ";
            case Key.OemPlus: return e.KeyModifiers == KeyModifiers.Shift ? "+" : "=";
            case Key.OemMinus: return e.KeyModifiers == KeyModifiers.Shift ? "_" : "-";
            case Key.OemComma: return e.KeyModifiers == KeyModifiers.Shift ? "<" : ",";
            case Key.OemPeriod: return e.KeyModifiers == KeyModifiers.Shift ? ">" : ".";
            case Key.Oem1: return e.KeyModifiers == KeyModifiers.Shift ? ":" : ";";
            case Key.Oem2: return e.KeyModifiers == KeyModifiers.Shift ? "?" : "/";
            case Key.Oem3: return e.KeyModifiers == KeyModifiers.Shift ? "~" : "`";
            case Key.OemOpenBrackets: return e.KeyModifiers == KeyModifiers.Shift ? "{" : "[";
            case Key.OemCloseBrackets: return e.KeyModifiers == KeyModifiers.Shift ? "}" : "]";
            case Key.OemQuotes: return e.KeyModifiers == KeyModifiers.Shift ? "\"" : "'";
            case Key.OemBackslash: return e.KeyModifiers == KeyModifiers.Shift ? "|" : "\\";
        }

        return null;
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        UpdateTerminalSizeFromViewport();
    }

    /// <summary>
    /// 依据编辑器实际可用像素与**实测字符尺寸**计算行列数并下发 PTY window-change。
    /// 不能用写死的 8.4x16.8 估算：字号/字体/DPI 一变就会算错，
    /// 列数偏小会让服务器的 `ls` 按更窄宽度排版（表现为单列或提前折行）。
    /// </summary>
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
            var clientSize = ClientSize;
            viewportWidth = clientSize.Width;
            viewportHeight = clientSize.Height - 120;   // 粗略扣除顶栏/输入区
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

    protected override void OnClosed(EventArgs e)
    {
        // 异步断开连接，避免卡死
        _ = Task.Run(async () =>
        {
            if (_sshService != null)
            {
                try
                {
                    await _sshService.DisconnectAsync();
                    _sshService.Dispose();
                }
                catch { }
            }
        });

        if (_ansiParser != null)
        {
            _ansiParser.TextOutput -= OnAnsiTextOutput;
            _ansiParser.ClearScreen -= OnAnsiClearScreen;
        }
        if (_terminalBuffer != null)
        {
            _terminalBuffer.BufferChanged -= OnBufferChanged;
        }
        base.OnClosed(e);
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

            if (hosts.Count > 10)
            {
                hosts = hosts.Take(10).ToList();
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

/// <summary>
/// 终端颜色渲染器 - 使用 AvaloniaEdit 的 DocumentColorizingTransformer 实现彩色显示
/// </summary>
public class TerminalColorizer : DocumentColorizingTransformer
{
    private readonly TerminalBuffer _terminalBuffer;
    private TextView? _textView;

    public TerminalColorizer(TerminalBuffer terminalBuffer)
    {
        _terminalBuffer = terminalBuffer;
    }

    public void Invalidate()
    {
        _textView?.Redraw();
    }

    protected override void OnAddToTextView(TextView textView)
    {
        base.OnAddToTextView(textView);
        _textView = textView;
    }

    protected override void OnRemoveFromTextView(TextView textView)
    {
        base.OnRemoveFromTextView(textView);
        _textView = null;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_terminalBuffer == null) return;

        int lineIndex = line.LineNumber - 1;
        if (lineIndex >= _terminalBuffer.Lines.Count) return;

        var coloredLine = _terminalBuffer.Lines[lineIndex];
        if (coloredLine == null) return;

        int startOffset = line.Offset;
        int lineLength = line.Length;

        // 文档行可能比缓冲区行长（如输入法组合文本临时追加在行尾），
        // 超过缓冲区行长的字符没有颜色信息，按默认颜色显示即可。
        int colorLimit = Math.Min(lineLength, coloredLine.Count);

        // 优化：批量处理相同颜色的连续字符
        int i = 0;
        while (i < colorLimit)
        {
            var currentChar = coloredLine[i];
            
            // 跳过没有颜色信息的字符（使用默认颜色）
            if (!currentChar.Foreground.HasValue && !currentChar.Background.HasValue)
            {
                i++;
                continue;
            }

            // 找到连续相同颜色的字符范围
            int start = i;
            while (i < colorLimit)
            {
                var nextChar = coloredLine[i];
                // 检查颜色是否相同
                if (nextChar.Foreground != currentChar.Foreground || 
                    nextChar.Background != currentChar.Background)
                {
                    break;
                }
                i++;
            }

            // 批量设置颜色
            int offset = startOffset + start;
            int length = i - start;

            if (currentChar.Foreground.HasValue)
            {
                base.ChangeLinePart(offset, offset + length,
                    element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(currentChar.Foreground.Value)));
            }

            if (currentChar.Background.HasValue)
            {
                base.ChangeLinePart(offset, offset + length,
                    element => element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(currentChar.Background.Value)));
            }
        }
    }
}
