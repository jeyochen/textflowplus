using System;
using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 本地端口转发服务
/// </summary>
public class SshPortForwardService : IDisposable
{
    private SshClient? _sshClient;
    private ForwardedPortLocal? _portForward;
    private bool _isDisposed;
    private bool _isStarted;

    public bool IsActive => _isStarted && _portForward != null;

    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// 创建本地端口转发
    /// </summary>
    public void CreatePortForward(
        SshConnectionInfo connectionInfo,
        uint hostPort,
        uint localPort)
    {
        if (_portForward != null && _isStarted)
        {
            throw new InvalidOperationException("端口转发已在运行");
        }

        var connectionInfoInternal = new Renci.SshNet.ConnectionInfo(
            connectionInfo.Host,
            connectionInfo.Port,
            connectionInfo.Username,
            new PasswordAuthenticationMethod(connectionInfo.Username, connectionInfo.Password ?? ""));

        _sshClient = new SshClient(connectionInfoInternal);
        _sshClient.Connect();

        _portForward = new ForwardedPortLocal(
            localPort,
            connectionInfo.Host,
            hostPort);

        _sshClient.AddForwardedPort(_portForward);
        _portForward.Start();
        _isStarted = true;
    }

    /// <summary>
    /// 停止端口转发
    /// </summary>
    public void StopPortForward()
    {
        if (_portForward != null && _isStarted)
        {
            _portForward.Stop();
            _isStarted = false;
        }
    }

    /// <summary>
    /// 启动端口转发
    /// </summary>
    public void StartPortForward()
    {
        if (_portForward != null && !_isStarted)
        {
            _portForward.Start();
            _isStarted = true;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;

        if (_portForward != null)
        {
            if (_isStarted)
                _portForward.Stop();
            _portForward.Dispose();
            _portForward = null;
        }

        if (_sshClient != null)
        {
            _sshClient.Disconnect();
            _sshClient.Dispose();
            _sshClient = null;
        }
    }
}

/// <summary>
/// SFTP 文件传输服务
/// </summary>
public class SftpService : IDisposable
{
    private SftpClient? _sftpClient;
    private bool _isConnected;
    private bool _isDisposed;

    public bool IsConnected => _isConnected;

    /// <summary>登录后的工作目录（通常为主目录）；未连接时返回 "/"。</summary>
    public string WorkingDirectory => _sftpClient?.WorkingDirectory ?? "/";

    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// 连接 SFTP 服务器
    /// </summary>
    public async System.Threading.Tasks.Task ConnectAsync(SshConnectionInfo connectionInfo)
    {
        if (_isConnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        var connectionInfoInternal = new Renci.SshNet.ConnectionInfo(
            connectionInfo.Host,
            connectionInfo.Port,
            connectionInfo.Username,
            new PasswordAuthenticationMethod(connectionInfo.Username, connectionInfo.Password ?? ""));

        _sftpClient = new SftpClient(connectionInfoInternal);

        // ConfigureAwait(false)：内部续体不依赖调用线程上下文，STFTP 层自身不触碰 UI，
        // 防止 UI 线程上的 sync-over-async（如 .GetAwaiter().GetResult()）互相等待卡死
        await System.Threading.Tasks.Task.Run(() => _sftpClient!.Connect()).ConfigureAwait(false);
        _isConnected = true;
        ConnectionStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async System.Threading.Tasks.Task DisconnectAsync()
    {
        if (!_isConnected || _sftpClient == null) return;

        await System.Threading.Tasks.Task.Run(() =>
        {
            _sftpClient!.Disconnect();
            _sftpClient.Dispose();
            _sftpClient = null;
        }).ConfigureAwait(false);

        _isConnected = false;
        ConnectionStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// 列出目录内容
    /// </summary>
    public async System.Threading.Tasks.Task<List<ISftpFile>> ListDirectoryAsync(string path)
    {
        if (!_isConnected || _sftpClient == null)
        {
            throw new InvalidOperationException("未连接到 SFTP 服务器");
        }

        return await System.Threading.Tasks.Task.Run(() =>
        {
            var files = _sftpClient!.ListDirectory(path);
            return files.OrderBy(f => f.Name).ToList();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载文件
    /// </summary>
    public async System.Threading.Tasks.Task DownloadFileAsync(string remotePath, string localPath, IProgress<double>? progress = null)
    {
        if (!_isConnected || _sftpClient == null)
        {
            throw new InvalidOperationException("未连接到 SFTP 服务器");
        }

        await System.Threading.Tasks.Task.Run(() =>
        {
            using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write);
            var fileSize = _sftpClient!.GetAttributes(remotePath).Size;
            var downloaded = 0L;
            var buffer = new byte[32768];

            using var input = _sftpClient.OpenRead(remotePath);
            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                fs.Write(buffer, 0, bytesRead);
                downloaded += bytesRead;

                if (progress != null && fileSize > 0)
                {
                    progress.Report((double)downloaded / fileSize * 100);
                }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 上传文件
    /// </summary>
    public async System.Threading.Tasks.Task UploadFileAsync(string localPath, string remotePath, IProgress<double>? progress = null)
    {
        if (!_isConnected || _sftpClient == null)
        {
            throw new InvalidOperationException("未连接到 SFTP 服务器");
        }

        await System.Threading.Tasks.Task.Run(() =>
        {
            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            var fileSize = fs.Length;
            var uploaded = 0L;
            var buffer = new byte[32768];

            using var output = _sftpClient!.OpenWrite(remotePath);
            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, bytesRead);
                uploaded += bytesRead;

                if (progress != null && fileSize > 0)
                {
                    progress.Report((double)uploaded / fileSize * 100);
                }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除文件
    /// </summary>
    public async System.Threading.Tasks.Task DeleteFileAsync(string remotePath)
    {
        if (!_isConnected || _sftpClient == null) return;

        await System.Threading.Tasks.Task.Run(() =>
        {
            _sftpClient!.Delete(remotePath);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建目录
    /// </summary>
    public async System.Threading.Tasks.Task CreateDirectoryAsync(string remotePath)
    {
        if (!_isConnected || _sftpClient == null) return;

        await System.Threading.Tasks.Task.Run(() =>
        {
            _sftpClient!.CreateDirectory(remotePath);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建空文件（OpenWrite 会自动创建/截断目标）
    /// </summary>
    public async System.Threading.Tasks.Task CreateFileAsync(string remotePath)
    {
        if (!_isConnected || _sftpClient == null)
        {
            throw new InvalidOperationException("未连接到 SFTP 服务器");
        }

        await System.Threading.Tasks.Task.Run(() =>
        {
            using var stream = _sftpClient!.OpenWrite(remotePath);
            // 不写入任何字节：即创建空文件
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 递归删除目录（含全部子文件与子目录）
    /// </summary>
    public async System.Threading.Tasks.Task DeleteDirectoryRecursiveAsync(string remotePath)
    {
        if (!_isConnected || _sftpClient == null)
        {
            throw new InvalidOperationException("未连接到 SFTP 服务器");
        }

        await System.Threading.Tasks.Task.Run(() =>
        {
            DeleteDirectoryRecursiveCore(_sftpClient!, remotePath);
        }).ConfigureAwait(false);
    }

    private static void DeleteDirectoryRecursiveCore(SftpClient client, string remotePath)
    {
        foreach (var entry in client.ListDirectory(remotePath))
        {
            if (entry.Name == "." || entry.Name == "..") continue;
            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursiveCore(client, entry.FullName);
            }
            else
            {
                client.Delete(entry.FullName);
            }
        }
        client.DeleteDirectory(remotePath);
    }

    /// <summary>
    /// 删除目录
    /// </summary>
    public async System.Threading.Tasks.Task DeleteDirectoryAsync(string remotePath)
    {
        if (!_isConnected || _sftpClient == null) return;

        await System.Threading.Tasks.Task.Run(() =>
        {
            _sftpClient!.DeleteDirectory(remotePath);
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        _sftpClient?.Disconnect();
        _sftpClient?.Dispose();
    }
}

/// <summary>
/// SSH 终端日志服务
/// </summary>
public class SshTerminalLogService
{
    private StreamWriter? _logWriter;
    private bool _isLogging;
    private readonly object _lock = new();

    public bool IsLogging => _isLogging;
    public string? LogFilePath { get; private set; }

    /// <summary>
    /// 开始日志记录
    /// </summary>
    public void StartLogging(string filePath)
    {
        if (_isLogging)
        {
            StopLogging();
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logWriter = new StreamWriter(filePath, true, Encoding.UTF8);
        _logWriter.AutoFlush = true;
        _isLogging = true;
        LogFilePath = filePath;
    }

    /// <summary>
    /// 写入日志
    /// </summary>
    public void WriteLog(string text)
    {
        if (!_isLogging || _logWriter == null) return;

        lock (_lock)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _logWriter.WriteLine($"[{timestamp}] {text}");
        }
    }

    /// <summary>
    /// 停止日志记录
    /// </summary>
    public void StopLogging()
    {
        if (!_isLogging) return;

        _logWriter?.Dispose();
        _logWriter = null;
        _isLogging = false;
        LogFilePath = null;
    }
}
