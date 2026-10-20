using System;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TextFlowPlus.Services;

/// <summary>版本检查结果。</summary>
/// <param name="HasUpdate">是否需要升级。</param>
/// <param name="NewVersion">服务端返回的新版本号（可能为空）。</param>
public readonly record struct UpdateCheckResult(bool HasUpdate, string NewVersion)
{
    public static readonly UpdateCheckResult NoUpdate = new(false, "");
}

/// <summary>
/// 启动时检查新版本：POST https://admin.app2s.cn/api/update/check-version
///  请求体 { version, name, platform }；响应 {"update": false} 无需升级，
///  {"update": true, "version": "x.y.z"} 需要升级；其余情况视为异常，静默忽略。
/// </summary>
public static class UpdateChecker
{
    private const string CheckUrl = "https://admin.app2s.cn/api/update/check-version";

    /// <summary>当前产品标识（已随改名同步为 textflowplus）。</summary>
    private const string ProductName = "textflowplus";

    /// <summary>
    /// 改名前的产品标识。服务端若尚未登记 textflowplus，按新标识查询会拿不到记录，
    /// 用户就会静默失去更新提示 —— 因此新标识未命中的情况下回退用旧标识再查一次，
    /// 保证过渡期内更新检查不中断。服务端登记好新标识后，这个回退可以安全移除。
    /// </summary>
    private const string LegacyProductName = "aieditor";

    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>获取当前程序实际版本号（取 csproj 中 FileVersion，形如 1.0.0）。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
                {
                    return fvi.FileVersion!.Trim();
                }
                return asm.GetName().Version?.ToString() ?? "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    /// <summary>按当前操作系统返回平台标识：windows / macos / ubuntu。</summary>
    public static string CurrentPlatform
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "macos";
            return "ubuntu";
        }
    }

    /// <summary>
    /// 判断服务端返回的版本号是否严格大于当前版本。
    /// 解析失败或为空时返回 true（信任接口的 update 标记）；相等或更小时返回 false。
    /// </summary>
    public static bool IsNewerVersion(string? newVersion)
    {
        if (string.IsNullOrWhiteSpace(newVersion)) return true;
        try
        {
            if (!Version.TryParse(newVersion.Trim(), out var remote)) return true;
            if (!Version.TryParse(CurrentVersion, out var local)) return true;
            return remote > local;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 异步检查是否有新版本。返回结果含是否需要升级与新版本号。
    /// 任何网络/解析异常都被吞掉，返回 <see cref="UpdateCheckResult.NoUpdate"/>。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        // 先用新标识查询；服务端尚未登记新标识时，回退用旧标识再查一次，
        // 避免改名造成更新提示静默中断。
        var result = await QueryAsync(ProductName, ct).ConfigureAwait(false);
        if (result.HasUpdate) return result;

        return await QueryAsync(LegacyProductName, ct).ConfigureAwait(false);
    }

    /// <summary>按指定产品标识查询一次版本信息；任何异常都退化为"无更新"。</summary>
    private static async Task<UpdateCheckResult> QueryAsync(string productName, CancellationToken ct)
    {
        try
        {
            var reqBody = JsonSerializer.Serialize(new
            {
                version = CurrentVersion,
                name = productName,
                platform = CurrentPlatform
            });

            using var content = new StringContent(reqBody, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(CheckUrl, content, ct).ConfigureAwait(false);
            // 非 2xx 一律视为异常，不处理
            if (!resp.IsSuccessStatusCode) return UpdateCheckResult.NoUpdate;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return UpdateCheckResult.NoUpdate;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return UpdateCheckResult.NoUpdate;
            if (!root.TryGetProperty("update", out var upd)) return UpdateCheckResult.NoUpdate;
            if (upd.ValueKind != JsonValueKind.True) return UpdateCheckResult.NoUpdate;

            string newVer = "";
            if (root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
            {
                newVer = ver.GetString() ?? "";
            }
            return new UpdateCheckResult(true, newVer);
        }
        catch
        {
            // 异常不处理
            return UpdateCheckResult.NoUpdate;
        }
    }
}
