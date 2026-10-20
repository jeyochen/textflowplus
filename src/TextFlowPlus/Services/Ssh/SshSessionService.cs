using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// SSH 会话模型
/// </summary>
public class SshSession
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string? EncryptedPassword { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int KeepAliveIntervalSeconds { get; set; } = 5;
    public string Encoding { get; set; } = "UTF-8";
    public string? Group { get; set; }
    public DateTime LastModified { get; set; } = DateTime.Now;
    public bool UsePasswordAuth => !string.IsNullOrEmpty(EncryptedPassword);
    public bool UseKeyAuth => !string.IsNullOrEmpty(PrivateKeyPath);
}

/// <summary>
/// SSH 会话管理服务
/// </summary>
public class SshSessionService
{
    private readonly string _sessionFilePath;
    private List<SshSession> _sessions = new();

    public event EventHandler? SessionsChanged;

    public SshSessionService()
    {
        var appDir = AppPaths.LocalAppDataDir;
        Directory.CreateDirectory(appDir);
        _sessionFilePath = Path.Combine(appDir, "ssh_sessions.json");
        LoadSessions();
    }

    public IReadOnlyList<SshSession> GetSessions()
    {
        return _sessions.AsReadOnly();
    }

    public IReadOnlyList<SshSession> GetSessionsByGroup(string? group)
    {
        if (string.IsNullOrEmpty(group))
            return _sessions.Where(s => string.IsNullOrEmpty(s.Group)).ToList().AsReadOnly();
        return _sessions.Where(s => s.Group == group).ToList().AsReadOnly();
    }

    public IEnumerable<string> GetGroups()
    {
        return _sessions.Where(s => !string.IsNullOrEmpty(s.Group))
                        .Select(s => s.Group!)
                        .Distinct()
                        .OrderBy(g => g);
    }

    public void AddSession(SshSession session)
    {
        session.LastModified = DateTime.Now;
        _sessions.Add(session);
        SaveSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSession(SshSession session)
    {
        var existing = _sessions.FirstOrDefault(s => s.Name == session.Name);
        if (existing != null)
        {
            session.LastModified = DateTime.Now;
            _sessions.Remove(existing);
            _sessions.Add(session);
            SaveSessions();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DeleteSession(string name)
    {
        var session = _sessions.FirstOrDefault(s => s.Name == name);
        if (session != null)
        {
            _sessions.Remove(session);
            SaveSessions();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public SshSession? GetSession(string name)
    {
        return _sessions.FirstOrDefault(s => s.Name == name);
    }

    public void ImportSessions(IEnumerable<SshSession> sessions, bool merge = true)
    {
        if (merge)
        {
            foreach (var session in sessions)
            {
                var existing = _sessions.FirstOrDefault(s => s.Name == session.Name);
                if (existing != null)
                    _sessions.Remove(existing);
                _sessions.Add(session);
            }
        }
        else
        {
            _sessions = sessions.ToList();
        }
        SaveSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExportSessions(string filePath)
    {
        var json = JsonConvert.SerializeObject(_sessions, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    private void LoadSessions()
    {
        try
        {
            if (File.Exists(_sessionFilePath))
            {
                var json = File.ReadAllText(_sessionFilePath);
                _sessions = JsonConvert.DeserializeObject<List<SshSession>>(json) ?? new List<SshSession>();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load sessions error: {ex.Message}");
            _sessions = new List<SshSession>();
        }
    }

    private void SaveSessions()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_sessions, Formatting.Indented);
            File.WriteAllText(_sessionFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save sessions error: {ex.Message}");
        }
    }
}
