namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// SSH 连接信息模型
/// </summary>
public class SshConnectionInfo
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int KeepAliveIntervalSeconds { get; set; } = 5;
    public string Encoding { get; set; } = "UTF-8";
    public int TerminalWidth { get; set; } = 80;
    public int TerminalHeight { get; set; } = 24;
}

/// <summary>
/// 最近连接的 SSH 主机信息
/// </summary>
public class SshRecentHostInfo
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime LastConnected { get; set; }
    public string? Password { get; set; }
}
