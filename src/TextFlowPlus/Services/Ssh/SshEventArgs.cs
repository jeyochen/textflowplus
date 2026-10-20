namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 终端输出数据事件参数
/// </summary>
public class TerminalOutputEventArgs : EventArgs
{
    public string Data { get; }
    public byte[] RawBytes { get; }

    public TerminalOutputEventArgs(string data, byte[] rawBytes)
    {
        Data = data;
        RawBytes = rawBytes;
    }
}

/// <summary>
/// 连接状态变化事件参数
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string StatusMessage { get; }

    public ConnectionStateChangedEventArgs(bool isConnected, string statusMessage)
    {
        IsConnected = isConnected;
        StatusMessage = statusMessage;
    }
}

/// <summary>
/// SSH 连接异常事件参数
/// </summary>
public class SshErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public string ErrorMessage { get; }

    public SshErrorEventArgs(Exception exception, string errorMessage)
    {
        Exception = exception;
        ErrorMessage = errorMessage;
    }
}
