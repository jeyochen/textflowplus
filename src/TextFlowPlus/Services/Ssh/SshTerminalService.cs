using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// SSH 底层通信层 - 封装 SSH.NET
/// 负责连接管理、数据流读写、心跳检测、断线重连
/// </summary>
public class SshTerminalService : IDisposable
{
    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private SshConnectionInfo? _connectionInfo;
    private bool _isConnected;
    private bool _isDisposed;
    private CancellationTokenSource? _readCancellationTokenSource;
    private CancellationTokenSource? _keepAliveCancellationTokenSource;
    private Task? _readTask;
    private Task? _keepAliveTask;
    private Encoding _encoding = Encoding.UTF8;

    // 事件
    public event EventHandler<TerminalOutputEventArgs>? DataReceived;
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<SshErrorEventArgs>? ErrorOccurred;

    public bool IsConnected => _isConnected;
    public SshConnectionInfo? ConnectionInfo => _connectionInfo;

    /// <summary>
    /// 连接 SSH 服务器
    /// </summary>
    public async Task ConnectAsync(SshConnectionInfo connectionInfo)
    {
        if (_isConnected)
        {
            await DisconnectAsync();
        }

        _connectionInfo = connectionInfo;
        _readCancellationTokenSource = new CancellationTokenSource();
        _keepAliveCancellationTokenSource = new CancellationTokenSource();

        try
        {
            // 设置编码
            if (!string.IsNullOrEmpty(connectionInfo.Encoding))
            {
                try
                {
                    _encoding = Encoding.GetEncoding(connectionInfo.Encoding);
                }
                catch
                {
                    _encoding = Encoding.UTF8;
                }
            }

            // 构建认证方式
            var authMethods = new List<AuthenticationMethod>();

            // 1. 密码认证
            if (!string.IsNullOrEmpty(connectionInfo.Password))
            {
                authMethods.Add(new PasswordAuthenticationMethod(connectionInfo.Username, connectionInfo.Password));
            }

            // 2. 私钥认证 - 使用 SSH.NET 2025.1.0 的 PrivateKeyAuthenticationMethod
            if (!string.IsNullOrEmpty(connectionInfo.PrivateKeyPath))
            {
                try
                {
                    // SSH.NET 2025.1.0 使用 PrivateFile 和 passphrase 构造函数
                    var keyFile = string.IsNullOrEmpty(connectionInfo.PrivateKeyPassphrase) ?
                        new PrivateKeyFile(connectionInfo.PrivateKeyPath) :
                        new PrivateKeyFile(connectionInfo.PrivateKeyPath, connectionInfo.PrivateKeyPassphrase);

                    // 使用 PrivateKeyFile 作为 IPrivateKeySource
                    authMethods.Add(new PrivateKeyAuthenticationMethod(connectionInfo.Username, keyFile));
                }
                catch (Exception ex)
                {
                    OnErrorOccurred(new SshErrorEventArgs(ex, $"加载私钥失败：{ex.Message}"));
                }
            }

            if (authMethods.Count == 0)
            {
                throw new Exception("至少需要一种认证方式（密码或私钥）");
            }

            var connectionInfoInternal = new Renci.SshNet.ConnectionInfo(
                connectionInfo.Host,
                connectionInfo.Port,
                connectionInfo.Username,
                authMethods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(connectionInfo.TimeoutSeconds)
            };

            _sshClient = new SshClient(connectionInfoInternal);

            // 连接
            await Task.Run(() => _sshClient!.Connect());

            // 创建 ShellStream - 使用 xterm-256color 终端类型以支持更多颜色。
            // 注意：不要额外发送 "export TERM=..."，PTY 请求已携带终端类型，
            // 再发一次会被 shell 回显，在终端里留下多余的命令噪音。
            _shellStream = _sshClient.CreateShellStream(
                "xterm-256color",
                (uint)connectionInfo.TerminalWidth,
                (uint)connectionInfo.TerminalHeight,
                0,
                0,
                4096);

            _isConnected = true;
            OnConnectionStateChanged(true, "已连接");

            // 启动后台读取线程
            _readTask = Task.Run(() => ReadLoopAsync(_readCancellationTokenSource.Token));

            // 启动心跳检测
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_keepAliveCancellationTokenSource.Token));
        }
        catch (Exception ex)
        {
            _isConnected = false;
            OnConnectionStateChanged(false, $"连接失败：{ex.Message}");
            OnErrorOccurred(new SshErrorEventArgs(ex, ex.Message));
            throw;
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        _isConnected = false;

        // 取消读取和心跳
        try
        {
            _readCancellationTokenSource?.Cancel();
            _keepAliveCancellationTokenSource?.Cancel();
        }
        catch { }

        // 等待任务完成
        if (_readTask != null)
        {
            try { await _readTask; } catch { }
        }
        if (_keepAliveTask != null)
        {
            try { await _keepAliveTask; } catch { }
        }

        // 释放资源
        _shellStream?.Dispose();
        _shellStream = null;

        _sshClient?.Disconnect();
        _sshClient?.Dispose();
        _sshClient = null;

        OnConnectionStateChanged(false, "已断开");
    }

    /// <summary>
    /// 发送数据到服务器
    /// </summary>
    public void SendData(string data)
    {
        // 未连接时静默忽略：UI 输入路径（键盘/粘贴）在断开瞬间可能仍有按键到达，
        // 此时抛异常会冒泡到事件处理器导致未捕获崩溃。
        if (!_isConnected || _shellStream == null)
        {
            return;
        }

        try
        {
            var bytes = _encoding.GetBytes(data);
            _shellStream.Write(bytes, 0, bytes.Length);
            _shellStream.Flush();
        }
        catch (ObjectDisposedException)
        {
            // 服务器端 shell 会话已结束（如执行 exit），ShellStream 已被释放：
            // 同步转为断开状态，避免后续按键继续触发同一异常导致崩溃。
            HandleShellClosed();
        }
        catch (InvalidOperationException)
        {
            // 通道关闭后写入也可能抛 InvalidOperationException，同样视为会话结束。
            HandleShellClosed();
        }
        catch (Exception ex)
        {
            HandleShellClosed();
            OnErrorOccurred(new SshErrorEventArgs(ex, $"发送数据失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 发送按键数据
    /// </summary>
    public void SendKey(string keySequence)
    {
        SendData(keySequence);
    }

    /// <summary>
    /// 调整终端大小（PTY window-change）。
    /// 必须真正把新尺寸发给服务器：`ls` 的多列排版、`vim`/`top` 的界面宽度都由**服务器端**依据
    /// PTY 尺寸计算，只改本地缓冲区会导致窗口明明很宽、`ls` 仍按 80 列换行（单列显示）。
    /// </summary>
    public void SetTerminalSize(int width, int height)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        // 记录最新尺寸（重连时按此尺寸建 PTY）
        if (_connectionInfo != null)
        {
            _connectionInfo.TerminalWidth = width;
            _connectionInfo.TerminalHeight = height;
        }

        if (!_isConnected || _shellStream == null) return;

        try
        {
            // ChangeWindowSize(columns, rows, widthPx, heightPx)：走 SSH window-change 请求，
            // 服务器据此重新计算 `ls` 列数等排版信息
            _shellStream.ChangeWindowSize((uint)width, (uint)height, 0, 0);
        }
        catch (Exception ex)
        {
            OnErrorOccurred(new SshErrorEventArgs(ex, $"调整终端大小失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 后台读取循环
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        // 增量解码器：UTF-8 多字节字符（中文/emoji）可能被 TCP 分片切断，
        // 直接 GetString 会把半个字符解成 U+FFFD（乱码），因此保留跨包的未完成字节。
        var decoder = _encoding.GetDecoder();

        while (!cancellationToken.IsCancellationRequested && _isConnected && _shellStream != null)
        {
            try
            {
                if (_shellStream.Length > 0)
                {
                    int bytesToRead = (int)Math.Min(_shellStream.Length, buffer.Length);
                    int bytesRead = _shellStream.Read(buffer, 0, bytesToRead);

                    if (bytesRead > 0)
                    {
                        // flush:false —— 末尾不完整的字符留到下一批数据再解码
                        int charCount = decoder.GetCharCount(buffer, 0, bytesRead, flush: false);
                        if (charCount > 0)
                        {
                            var chars = new char[charCount];
                            decoder.GetChars(buffer, 0, bytesRead, chars, 0, flush: false);
                            var text = new string(chars);
                            OnDataReceived(new TerminalOutputEventArgs(text, buffer.Take(bytesRead).ToArray()));
                        }
                        else
                        {
                            OnDataReceived(new TerminalOutputEventArgs(string.Empty, buffer.Take(bytesRead).ToArray()));
                        }
                    }
                }
                else
                {
                    await Task.Delay(15, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    if (ex is ObjectDisposedException or SshConnectionException)
                    {
                        // shell 会话结束（如 exit）：静默转为断开状态，不当作错误提示
                        HandleShellClosed();
                    }
                    else
                    {
                        HandleShellClosed();
                        OnErrorOccurred(new SshErrorEventArgs(ex, $"读取数据失败：{ex.Message}"));
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// 心跳检测循环
    /// </summary>
    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isConnected)
        {
            try
            {
                await Task.Delay(_connectionInfo?.KeepAliveIntervalSeconds * 1000 ?? 5000, cancellationToken);

                if (_isConnected && _sshClient != null)
                {
                    // 心跳检测 - 通过发送空数据保持连接
                    try
                    {
                        _sshClient.SendKeepAlive();
                    }
                    catch
                    {
                        // 忽略心跳失败
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    OnErrorOccurred(new SshErrorEventArgs(ex, $"心跳检测失败：{ex.Message}"));
                }
            }
        }
    }

    private void OnDataReceived(TerminalOutputEventArgs e)
    {
        DataReceived?.Invoke(this, e);
    }

    private void OnConnectionStateChanged(bool isConnected, string statusMessage)
    {
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(isConnected, statusMessage));
    }

    private void OnErrorOccurred(SshErrorEventArgs e)
    {
        ErrorOccurred?.Invoke(this, e);
    }

    /// <summary>shell 会话已结束（服务器端关闭，如执行 exit）：同步更新为断开状态并通知 UI。</summary>
    private void HandleShellClosed()
    {
        if (_isConnected)
        {
            _isConnected = false;
            OnConnectionStateChanged(false, "连接已断开");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        _readCancellationTokenSource?.Dispose();
        _keepAliveCancellationTokenSource?.Dispose();
        _shellStream?.Dispose();
        _sshClient?.Dispose();
    }
}
