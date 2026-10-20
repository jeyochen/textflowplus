using System;
using System.IO;

namespace TextFlowPlus.Services;

/// <summary>
/// 应用数据目录解析：统一集中所有配置/缓存目录名，并兼容改名前的旧目录。
///
/// 历史：本软件曾用名 <c>AIEditor</c>（更早为 TextFlowPlus），后统一改回
/// <c>TextFlowPlus</c>。为避免老用户升级后配置"凭空消失"，这里在读取时
/// 优先用新目录，新目录不存在而旧目录存在时回退到旧目录；写入时如果旧目录
/// 才是既有数据所在，则继续写旧目录，保证升级无缝、且不与旧版本互相覆盖。
/// </summary>
public static class AppPaths
{
    /// <summary>当前产品名（用于显示、安装包、User-Agent 等）。</summary>
    public const string ProductName = "TextFlowPlus";

    /// <summary>改名前的旧产品名，仅用于兼容读取既有用户数据。</summary>
    private const string LegacyProductName = "AIEditor";

    /// <summary>本地应用数据根目录：%LOCALAPPDATA%（Windows）/ ~/.local/share（Linux）/ ~/Library/Application Support（macOS）。</summary>
    public static string LocalAppDataRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>用户主目录。</summary>
    public static string UserProfileRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 解析某个配置子目录：优先返回已存在的新目录；新目录不存在但旧目录存在时
    /// 返回旧目录（兼容升级）；两者都不存在时返回新目录（供创建）。
    /// </summary>
    /// <param name="parentRoot">父目录（如 LocalApplicationData 或 UserProfile）。</param>
    /// <param name="childName">子目录名（如 "TextFlowPlus"）。</param>
    private static string ResolveDir(string parentRoot, string childName)
    {
        var current = Path.Combine(parentRoot, childName);
        var currentExists = Directory.Exists(current);

        // 收集所有候选旧目录名（大小写两种历史写法）
        var candidates = new List<string>();
        if (!string.Equals(childName, LegacyProductName, StringComparison.OrdinalIgnoreCase))
            candidates.Add(LegacyProductName);
        var legacyLower = LegacyProductName.ToLowerInvariant();
        if (!string.Equals(childName, legacyLower, StringComparison.OrdinalIgnoreCase))
            candidates.Add(legacyLower);

        // 只有一个目录存在：直接用它（新目录存在就用新目录，旧目录存在就沿用旧目录）
        var existingLegacy = candidates
            .Select(n => Path.Combine(parentRoot, n))
            .Where(Directory.Exists)
            .ToList();

        if (!currentExists && existingLegacy.Count == 0) return current;   // 都不存在 → 用新名（调用方创建）
        if (existingLegacy.Count == 0) return current;                     // 只有新目录 → 用新目录
        if (!currentExists) return existingLegacy[0];                      // 只有旧目录 → 沿用旧目录

        // 新旧目录同时存在：取"数据更新"的那个，避免升级后读到一年前的陈旧配置。
        // 这里用目录下最近写入的文件时间作为判据（都为空时按新目录优先）。
        var currentStamp = LatestWriteStamp(current);
        var legacyStamp = existingLegacy.Max(LatestWriteStamp);

        return legacyStamp > currentStamp ? existingLegacy[0] : current;
    }

    /// <summary>目录下（含直接子目录中的文件）最近一次写入时间；无文件时返回 DateTime.MinValue。</summary>
    private static DateTime LatestWriteStamp(string dir)
    {
        try
        {
            var stamp = Directory.GetFiles(dir)
                .Select(f => File.GetLastWriteTimeUtc(f))
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return stamp;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>本地应用数据下的应用目录（配置、会话、主题等）。</summary>
    public static string LocalAppDataDir => ResolveDir(LocalAppDataRoot, ProductName);

    /// <summary>
    /// 用户主目录下的应用目录（远程连接凭据 remote_connections.json 存放在此）。
    ///
    /// 注意：用户主目录下可能恰好存在一个同名目录但并非配置目录（例如把源码 clone 到
    /// <c>~/TextFlowPlus</c>）。因此这里只认"看起来像配置目录"的候选：目录中已有
    /// <c>remote_connections.json</c>，或目录为空（可安全新建）。找不到时返回新名目录供创建。
    /// </summary>
    public static string UserProfileDir
    {
        get
        {
            const string marker = "remote_connections.json";
            foreach (var name in new[] { ProductName, LegacyProductName, LegacyProductName.ToLowerInvariant() })
            {
                var dir = Path.Combine(UserProfileRoot, name);
                if (!Directory.Exists(dir)) continue;

                // 已含远程连接配置 → 明确是配置目录
                if (File.Exists(Path.Combine(dir, marker))) return dir;

                // 空目录 → 可视为配置目录（新建的）
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) return dir;
                }
                catch { /* 无法枚举则跳过 */ }
            }

            return Path.Combine(UserProfileRoot, ProductName);
        }
    }

    /// <summary>拼接本地应用数据目录下的文件路径（目录不存在时按新名返回，写入方自行创建）。</summary>
    public static string LocalAppDataFile(string fileName) =>
        Path.Combine(LocalAppDataDir, fileName);

    /// <summary>拼接用户主目录下应用目录中的文件路径。</summary>
    public static string UserProfileFile(string fileName) =>
        Path.Combine(UserProfileDir, fileName);

    /// <summary>临时缓存根目录（%TEMP%/TextFlowPlus）。</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), ProductName);

    /// <summary>确保目录存在并返回（用于写入前创建）。</summary>
    public static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* 权限等异常交由调用方处理 */ }
        return dir;
    }
}