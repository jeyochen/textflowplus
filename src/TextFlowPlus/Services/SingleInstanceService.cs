using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TextFlowPlus.Services;

/// <summary>
/// 单实例管理：命名互斥体仲裁“同一时刻只有一个人实例”，命名管道把
/// “右键 → 打开方式 → TextFlowPlus”等二次启动传入的文件/文件夹路径投递给已运行的实例。
/// - 主实例（new Mutex 成功取得所有权）：持锁并监听管道；
/// - 后续实例：检测到互斥体已存在 → 通过管道发送路径 → 立即退出。
/// 管道与互斥体都按用户名限定，避免跨用户会话串扰。
/// </summary>
public static class SingleInstanceService
{
    // 故意沿用改名前的名字（AIEditor）：升级过渡期内，若用户还开着旧版 AIEditor，
    // 新版启动会被正确识别为“已有实例”并把文件路径转交给它，避免两个版本同时运行
    // 争抢同一份配置文件。待旧版完全退场后可改为 TextFlowPlus。
    private const string BaseName = "AIEditor.SingleInstance";
    private static readonly string Namespace =
        string.IsNullOrWhiteSpace(Environment.UserName) ? "default" : Environment.UserName;
    private static string MutexName => $"{BaseName}.{Namespace}";
    private static string PipeName => $"{BaseName}.{Namespace}";

    private static Mutex? _primaryMutex;   // 主实例持有到进程退出（防 GC）

    /// <summary>尝试成为主实例。成功返回 true（唯一实例，应继续正常启动）；
    /// 否则说明已有实例在运行：把 paths 通过管道投递给它，并返回 false（调用方应立即退出）。</summary>
    public static bool TryAcquirePrimary(ICollection<string> paths, int notifyTimeoutMs = 3000)
    {
        try
        {
            _primaryMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew) return true;

            // Mutex 已存在：尝试立即获得所有权。
            // 旧实例若被强杀（Mutex 残留但无人持有）可在此接管，避免误判“已有实例”而退出。
            try
            {
                if (_primaryMutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个主实例异常退出（未释放互斥体）：我们接管成为主实例
            try { _primaryMutex ??= new Mutex(initiallyOwned: true, MutexName); } catch { }
            return true;
        }
        catch
        {
            // 其他异常（权限等）：按“已存在实例”处理，避免双开
            _primaryMutex = null;
            NotifyRunningInstance(paths, notifyTimeoutMs);
            return false;
        }

        // 已有主实例：投递路径后让调用方退出
        try { _primaryMutex.Dispose(); _primaryMutex = null; } catch { }
        NotifyRunningInstance(paths, notifyTimeoutMs);
        return false;
    }

    /// <summary>向已运行的主实例发送文件/文件夹路径（无路径时仅作为“激活/聚焦”信号）。</summary>
    public static bool NotifyRunningInstance(ICollection<string> paths, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p)) writer.WriteLine(p.Trim());
            }
            // 等主实例读完再返回，保证路径送达（WaitForPipeDrain 仅 Windows 支持）
            if (OperatingSystem.IsWindows())
            {
                client.WaitForPipeDrain();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>主实例：持续监听管道，把收到的每一行（文件/文件夹路径）回调给 onPath。
    /// 运行在独立线程；onPath 内如需操作 UI 请自行调度到 UI 线程。</summary>
    public static async Task StartServerAsync(Action<string> onPath, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    var path = line.Trim();
                    if (path.Length > 0) onPath(path);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 单次会话异常（客户端异常断开等）：稍候继续监听
                try { await Task.Delay(200, CancellationToken.None); } catch { }
            }
        }
    }
}
