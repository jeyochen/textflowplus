using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 远程主机连接信息（保存于用户目录 ~/aieditor/remote_connections.json）。
/// </summary>
public class RemoteConnectionInfo
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTime LastConnected { get; set; }

    /// <summary>列表显示名称：user@host:port</summary>
    [JsonIgnore]
    public string DisplayName => $"{Username}@{Host}:{Port}";

    /// <summary>唯一标识（用于编辑/删除/去重）：host:port:username</summary>
    [JsonIgnore]
    public string Key => $"{Host}:{Port}:{Username}";
}

/// <summary>
/// 远程连接信息存储：保存/读取用户主目录下应用目录（现为 TextFlowPlus，兼容改名前的
/// aieditor）中的 remote_connections.json。按 host:port:username 去重，Upsert 更新或新增，Remove 删除。
/// </summary>
public static class RemoteConnectionStore
{
    /// <summary>存储目录：用户目录下的 TextFlowPlus 文件夹（改名前的 aieditor 目录仍兼容读取）</summary>
    public static readonly string SaveDirectory = AppPaths.UserProfileDir;

    private static readonly string SaveFilePath = Path.Combine(SaveDirectory, "remote_connections.json");

    public static List<RemoteConnectionInfo> Load()
    {
        try
        {
            if (File.Exists(SaveFilePath))
            {
                var json = File.ReadAllText(SaveFilePath);
                return JsonConvert.DeserializeObject<List<RemoteConnectionInfo>>(json)
                       ?? new List<RemoteConnectionInfo>();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load remote connections error: {ex.Message}");
        }
        return new List<RemoteConnectionInfo>();
    }

    /// <summary>新增或更新（按 host:port:username 去重），并写入磁盘。</summary>
    public static void Save(RemoteConnectionInfo info)
    {
        var list = Load();
        var existing = list.FirstOrDefault(c => c.Key == info.Key);
        if (existing != null)
        {
            existing.Host = info.Host;
            existing.Port = info.Port;
            existing.Username = info.Username;
            existing.Password = info.Password;
            existing.LastConnected = info.LastConnected;
        }
        else
        {
            if (info.LastConnected == default) info.LastConnected = DateTime.Now;
            list.Add(info);
        }
        WriteAll(list);
    }

    /// <summary>按 host:port:username 删除。</summary>
    public static void Remove(RemoteConnectionInfo info)
    {
        var list = Load();
        list.RemoveAll(c => c.Key == info.Key);
        WriteAll(list);
    }

    private static void WriteAll(IEnumerable<RemoteConnectionInfo> list)
    {
        try
        {
            Directory.CreateDirectory(SaveDirectory);
            var json = JsonConvert.SerializeObject(list, Formatting.Indented);
            File.WriteAllText(SaveFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save remote connections error: {ex.Message}");
        }
    }
}
