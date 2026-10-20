using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TextFlowPlus.Services.Git;

/// <summary>Git 变更项：状态码（porcelain 前两位）+ 相对仓库根路径 + 完整路径。</summary>
public sealed record GitChange(string Status, string RelPath, string FullPath)
{
    /// <summary>状态显示字符：M=已修改 A=新增 D=删除 R=重命名 U=未跟踪。</summary>
    public string StatusText
    {
        get
        {
            char c = Status.Length > 0 ? Status[0] : '?';
            return c switch
            {
                'M' => "M",
                'A' => "A",
                'D' => "D",
                'R' => "R",
                'C' => "C",
                '?' => "U",
                _ => c.ToString()
            };
        }
    }
}

/// <summary>
/// Git 仓库操作（基于命令行 git，零托管依赖、跨平台）。
/// 打开文件夹后向上查找 .git 判断仓库；修改列表用 status --porcelain -z（NUL 分隔，路径不转义）。
/// </summary>
public static class GitService
{
    /// <summary>从任意路径向上查找仓库根（含 .git 目录的目录）；找不到返回 null。</summary>
    public static string? FindRepositoryRoot(string startPath)
    {
        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { /* 权限等错误：视为非仓库 */ }
        return null;
    }

    /// <summary>获取仓库全部变更（含未跟踪文件）。失败时返回空列表。</summary>
    public static List<GitChange> GetChanges(string repoRoot)
    {
        var result = new List<GitChange>();
        var (ok, stdout, _) = RunGit(repoRoot, "-c", "core.quotepath=false", "status", "--porcelain", "-z");
        if (!ok || string.IsNullOrEmpty(stdout)) return result;

        // -z 格式：每条记录 "XY path\0"，重命名/复制为 "XY old\0new\0"（新路径紧随其后）
        var recs = stdout.Split('\0');
        for (int k = 0; k < recs.Length; k++)
        {
            var rec = recs[k];
            if (rec.Length < 3) continue;
            string status = rec.Substring(0, 2);
            string path = rec.Substring(3);
            if ((status[0] == 'R' || status[0] == 'C') && k + 1 < recs.Length)
            {
                var next = recs[++k];
                if (!string.IsNullOrEmpty(next)) path = next;
            }
            if (path.Length == 0) continue;
            var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            result.Add(new GitChange(status, path, full));
        }
        return result;
    }

    /// <summary>获取文件的统一 diff 文本（--no-color，相对仓库根路径）。</summary>
    public static string GetDiffText(string repoRoot, string relPath)
    {
        var (_, stdout, _) = RunGit(repoRoot, "-c", "core.quotepath=false", "diff", "--no-color", "--", relPath);
        return stdout ?? "";
    }

    /// <summary>取 HEAD 中某文件的内容（新增文件/无 HEAD 返回 null）。</summary>
    public static string? GetHeadContent(string repoRoot, string relPath)
    {
        var (ok, stdout, _) = RunGit(repoRoot, "-c", "core.quotepath=false", "show", "HEAD:" + relPath);
        if (!ok || stdout == null) return null;
        // 二进制文件（含 NUL）不适合文本对比
        if (stdout.IndexOf('\0') >= 0) return null;
        return stdout;
    }

    /// <summary>提交所有更改（add -A + commit）。返回 (是否成功, 结果说明)。</summary>
    public static (bool Ok, string Message) Commit(string repoRoot, string message)
    {
        var (ok1, _, err1) = RunGit(repoRoot, "add", "-A");
        if (!ok1) return (false, "git add 失败：" + (TrimOut(err1) ?? "未知错误"));
        var (ok2, so2, err2) = RunGit(repoRoot, "commit", "-m", message);
        if (!ok2) return (false, "git commit 失败：" + (TrimOut(err2) ?? TrimOut(so2) ?? "未知错误"));
        return (true, TrimOut(so2) ?? "提交成功");
    }

    /// <summary>拉取远程更改。返回 (是否成功, 结果说明)。</summary>
    public static (bool Ok, string Message) Pull(string repoRoot)
    {
        var (ok, so, err) = RunGit(repoRoot, "pull");
        var msg = FirstNonEmpty(TrimOut(so), TrimOut(err), ok ? "拉取完成" : "拉取失败");
        return (ok, msg);
    }

    /// <summary>推送到远程。返回 (是否成功, 结果说明)。</summary>
    public static (bool Ok, string Message) Push(string repoRoot)
    {
        var (ok, so, err) = RunGit(repoRoot, "push");
        var msg = FirstNonEmpty(TrimOut(so), TrimOut(err), ok ? "推送完成" : "推送失败");
        return (ok, msg);
    }

    /// <summary>列出全部本地分支名（git branch --format）。失败返回空列表。</summary>
    public static List<string> GetBranches(string repoRoot)
    {
        var result = new List<string>();
        var (ok, stdout, _) = RunGit(repoRoot, "branch", "--format=%(refname:short)");
        if (!ok || string.IsNullOrEmpty(stdout)) return result;
        foreach (var line in stdout.Replace("\r", "").Split('\n'))
        {
            var name = line.Trim();
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }

    /// <summary>当前分支名（detached HEAD 或无仓库返回 null）。</summary>
    public static string? GetCurrentBranch(string repoRoot)
    {
        var (ok, stdout, _) = RunGit(repoRoot, "branch", "--show-current");
        if (!ok || string.IsNullOrWhiteSpace(stdout)) return null;
        return stdout.Trim();
    }

    /// <summary>切换到指定分支。返回 (是否成功, 结果说明)。</summary>
    public static (bool Ok, string Message) Checkout(string repoRoot, string branch)
    {
        var (ok, so, err) = RunGit(repoRoot, "checkout", branch);
        if (!ok) return (false, FirstNonEmpty(TrimOut(err), TrimOut(so), "切换失败"));
        return (true, TrimOut(so) ?? "切换完成");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return "";
    }

    private static string? TrimOut(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim();
    }

    /// <summary>读取当前工作区文件文本（不存在返回 null）。</summary>
    public static string? ReadWorkContent(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return null;
            return File.ReadAllText(fullPath);
        }
        catch { return null; }
    }

    /// <summary>运行 git，返回 (是否成功, stdout, stderr)。参数用 ArgumentList 传递，路径含空格/引号均安全。</summary>
    private static (bool Ok, string? Stdout, string? Stderr) RunGit(string repoRoot, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(repoRoot);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (false, null, null);
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, so, se);
        }
        catch
        {
            return (false, null, null);
        }
    }
}
