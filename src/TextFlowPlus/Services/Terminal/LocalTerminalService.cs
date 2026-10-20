using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TextFlowPlus.Services.Terminal;

/// <summary>
/// 本地命令行终端会话：启动系统 shell（Windows 用 PowerShell，macOS/Linux 用系统默认 shell），
/// 以**非交互 stdin 模式**运行：shell 只从标准输入整行读取命令执行、不回显输入，
/// 输出（含 ANSI 转义序列）按数据块通过 OutputReceived 上抛。输入行编辑/回显/提示符
/// 由终端视图负责（避免 shell 回显与本地回显叠加成双重显示）。
/// </summary>
public class LocalTerminalService : IDisposable
{
    private Process? _process;
    private readonly object _writeLock = new();
    private bool _disposed;
    private volatile bool _exited;

    /// <summary>Unix 下为启用 ls 颜色生成的 BASH_ENV/ZDOTDIR 临时文件（Dispose 时清理）。</summary>
    private string? _shellEnvFile;

    /// <summary>shell 输出（含 ANSI 转义序列，按数据块推送，可能在后台线程触发）。</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>shell 进程退出（可能在后台线程触发）。</summary>
    public event EventHandler? Exited;

    /// <summary>shell 展示名（用于状态栏，如 "PowerShell" / "bash" / "zsh"）。</summary>
    public string ShellName { get; }

    /// <summary>
    /// 目录回报哨兵前缀：PowerShell 循环在每条命令执行后输出该前缀 + 当前 $PWD，
    /// 供视图精确获知 shell 真实工作目录（不依赖对 cd 命令的文本解析）。
    /// 使用不可见控制字符包裹，避免与用户输出冲突。
    /// </summary>
    private const string CwdSentinel = "\u001b]1338;";

    /// <summary>通知 shell 回报一次当前工作目录（由视图在需要时调用）。</summary>
    private const string ReportCwdCommand = "$null=[Console]::Out.Write(\"$([char]27)]1338;$($PWD.Path)$([char]7)\")";

    /// <summary>目录回报哨兵前缀（视图据此从输出流中提取真实工作目录）。</summary>
    public static string CwdReportPrefix => CwdSentinel;

    /// <summary>是否为支持目录回报的 shell（当前为 Windows PowerShell）。</summary>
    public bool SupportsCwdReport => OperatingSystem.IsWindows();

    /// <summary>请求 shell 主动回报一次当前工作目录。</summary>
    public void RequestWorkingDirectoryReport()
    {
        if (!SupportsCwdReport) return;
        SendData(ReportCwdCommand + "\n");
    }

    static LocalTerminalService()
    {
        // 注册代码页提供程序：Windows PowerShell 5.1 的 stdin 输入编码为系统 ANSI(GBK)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public LocalTerminalService()
    {
        ShellName = ResolveShellName();
    }

    /// <summary>解析系统 shell：Windows → PowerShell；macOS/Linux → $SHELL 或 /bin/bash。</summary>
    public static string ResolveShellPath()
    {
        if (OperatingSystem.IsWindows())
            return "powershell.exe";
        var shell = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell;
    }

    public static string ResolveShellName()
    {
        return OperatingSystem.IsWindows() ? "PowerShell" : Path.GetFileName(ResolveShellPath());
    }

    /// <summary>启动 shell 进程并开始异步读取输出。</summary>
    public void Start()
    {
        if (_process != null) return;
        var shellPath = ResolveShellPath();
        var psi = new ProcessStartInfo
        {
            FileName = shellPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = GetHomeDirectory(),
        };

        if (OperatingSystem.IsWindows())
        {
            // PowerShell 以「循环读行脚本」方式运行（-Command 非交互模式，不回显输入）：
            // [Console]::In.ReadLine() 逐行读取 stdin（不等 EOF），Invoke-Expression 执行该行。
            // 这样既避免了交互模式（ConsoleHost）把输入回显到 stdout 造成双重显示，
            // 也避免了普通 -Command - 模式要等 stdin 关闭（EOF）才执行命令的问题。
            // 自定义 ls/dir：管道模式（无 tty）下 Get-ChildItem 不输出颜色，
            // 覆盖为逐项输出 ANSI 颜色（目录青色 / 可执行绿色 / 压缩包红色），并支持 -a、-l。
            const string lsFunc =
                "Remove-Item alias:ls -ErrorAction SilentlyContinue; Remove-Item alias:dir -ErrorAction SilentlyContinue; " +
                "function ls { $esc=[char]27; $path='.'; $showHidden=$false; $long=$false; " +
                "foreach($a in $args){ if($a -is [string]){ if($a.StartsWith('-')){ " +
                "if($a -match 'a'){$showHidden=$true}; if($a -match 'l'){$long=$true} } else { $path=$a } } } " +
                "Get-ChildItem -Force $path | Where-Object { $showHidden -or -not $_.Name.StartsWith('.') } | ForEach-Object { " +
                "$colored = if($_.PSIsContainer){ \"$esc[36m$($_.Name)$esc[0m\" } " +
                "elseif($_.Extension -match '^\\.(exe|bat|cmd|com|ps1|py|sh)$'){ \"$esc[32m$($_.Name)$esc[0m\" } " +
                "elseif($_.Extension -match '^\\.(zip|gz|tar|7z|rar|bz2|xz|tgz)$'){ \"$esc[31m$($_.Name)$esc[0m\" } else { $_.Name }; " +
                "if($long){ $type=if($_.PSIsContainer){'d'}else{'-'}; $size=if($_.PSIsContainer){''}else{[string]$_.Length}; " +
                "'{0} {1,10} {2} {3}' -f $type,$size,$_.LastWriteTime.ToString('MM-dd HH:mm'),$colored } else { $colored } } }; " +
                "function dir { ls @args }; ";
            const string loopScript = lsFunc +
                "$OutputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                // 每条命令执行后回报当前目录（OSC 1338 风格哨兵），供视图显示到提示符里
                "function __AiedReportCwd { [Console]::Out.Write(\"$([char]27)]1338;$($PWD.Path)$([char]7)\") }; " +
                "while($true){ try { $line=[Console]::In.ReadLine(); if($null -eq $line){ break }; " +
                // 输出经 Out-String -Stream 逐行写 Console.Out 并 Flush：
                // PowerShell 成功流在管道下默认缓冲，pwd 等输出会滞留到下一次命令才 flush；
                // Console.Out 是即时 flush 的，逐行显式写出可保证输出实时上抛。
                "if($line.Length -gt 0){ Invoke-Expression $line 2>&1 | Out-String -Stream | ForEach-Object { [Console]::Out.WriteLine($_); [Console]::Out.Flush() } } } catch { Write-Error $_ } finally { __AiedReportCwd } }";
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(loopScript);
            // stdin 输入编码按 PowerShell 5.1 默认的 ANSI(GBK)，保证中文命令正确；输出已由脚本切为 UTF-8
            psi.StandardInputEncoding = Encoding.GetEncoding(936);
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }
        else
        {
            // macOS/Linux：bash/zsh 非交互模式从 stdin 逐行读取执行命令（不回显输入）。
            // 通过 BASH_ENV（bash）/ ZDOTDIR+zshenv（zsh）启用 ls 颜色：
            // 非 tty 下 ls 默认不加色，强制输出 ANSI 颜色（目录蓝 / 可执行绿 / 压缩包红）。
            var shellName = Path.GetFileName(ResolveShellPath());
            var sb = new StringBuilder();
            if (OperatingSystem.IsMacOS())
            {
                // BSD ls 用 -G 强制颜色
                sb.AppendLine("setopt expand_aliases 2>/dev/null; shopt -s expand_aliases 2>/dev/null;");
                sb.AppendLine("alias ls='ls -G'");
            }
            else
            {
                sb.AppendLine("setopt expand_aliases 2>/dev/null; shopt -s expand_aliases 2>/dev/null;");
                sb.AppendLine("alias ls='ls --color=always'");
                sb.AppendLine("export LS_COLORS='di=01;34:ln=01;36:ex=01;32:*.tar=01;31:*.gz=01;31:*.zip=01;31:*.bz2=01;31:*.7z=01;31:*.rar=01;31'");
            }
            if (string.Equals(shellName, "zsh", StringComparison.OrdinalIgnoreCase))
            {
                var zdotDir = Path.Combine(Path.GetTempPath(), "aieditor_zdotdir_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(zdotDir);
                _shellEnvFile = Path.Combine(zdotDir, ".zshenv");
                File.WriteAllText(_shellEnvFile, sb.ToString(), new UTF8Encoding(false));
                psi.EnvironmentVariables["ZDOTDIR"] = zdotDir;
            }
            else
            {
                _shellEnvFile = Path.Combine(Path.GetTempPath(), "aieditor_bashenv_" + Guid.NewGuid().ToString("N") + ".sh");
                File.WriteAllText(_shellEnvFile, sb.ToString(), new UTF8Encoding(false));
                psi.EnvironmentVariables["BASH_ENV"] = _shellEnvFile;
            }
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.StandardInputEncoding = Encoding.UTF8;
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 shell 进程");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _exited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        };

        // 按数据块异步读取（不能用 ReadLine：提示符不带换行会滞留在行读取器里不显示）
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(ReadLoopAsyncError);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var reader = _process?.StandardOutput;
            if (reader == null) return;
            var buf = new char[4096];
            while (!_exited)
            {
                int n = await reader.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                OutputReceived?.Invoke(this, new string(buf, 0, n));
            }
        }
        catch (Exception)
        {
            // 进程退出/管道关闭：静默结束
        }
    }

    private async Task ReadLoopAsyncError()
    {
        try
        {
            var reader = _process?.StandardError;
            if (reader == null) return;
            var buf = new char[4096];
            while (!_exited)
            {
                int n = await reader.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                OutputReceived?.Invoke(this, new string(buf, 0, n));
            }
        }
        catch (Exception)
        {
            // 静默结束
        }
    }

    /// <summary>发送原始数据到 shell 标准输入（线程安全）。</summary>
    public void SendData(string data)
    {
        if (string.IsNullOrEmpty(data) || _exited || _process == null) return;
        lock (_writeLock)
        {
            try
            {
                _process.StandardInput.Write(data);
                _process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // 进程已退出：忽略
            }
        }
    }

    /// <summary>终止 shell 进程并释放资源。</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exited = true;
        try
        {
            if (_process != null && !_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch { }
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (Exception)
        {
            // 忽略终止失败
        }
        try { _process?.Dispose(); } catch { }
        _process = null;
        try
        {
            if (_shellEnvFile != null)
            {
                if (File.Exists(_shellEnvFile)) File.Delete(_shellEnvFile);
                var parent = Path.GetDirectoryName(_shellEnvFile);
                if (parent != null && parent.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                {
                    Directory.Delete(parent);
                }
                _shellEnvFile = null;
            }
        }
        catch { /* 清理失败忽略 */ }
    }

    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Environment.CurrentDirectory : home;
    }
}
