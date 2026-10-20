using System;
using System.IO;
using Newtonsoft.Json;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 终端外观配置模型
/// </summary>
public class TerminalAppearanceSettings
{
    // 字体设置
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 14;

    // 颜色设置
    public string BackgroundColor { get; set; } = "#0C0C0C";
    public string ForegroundColor { get; set; } = "#CCCCCC";
    public string CursorColor { get; set; } = "#FFFFFF";

    // ANSI 颜色配置
    public string[] AnsiColors { get; set; } = new[]
    {
        "#000000", "#CD3131", "#0DBC79", "#E5E510",
        "#2472C8", "#BC3FBC", "#0FA8D5", "#E5E5E5",
        "#666666", "#F14C4C", "#23D18B", "#F5F543",
        "#3B8EEA", "#D670FF", "#29B8DB", "#E5E5E5"
    };

    // 布局设置
    public int LineHeight { get; set; } = 1;
    public int PaddingX { get; set; } = 4;
    public int PaddingY { get; set; } = 4;
    public int ScrollbackLines { get; set; } = 1000;

    // 光标设置
    public bool ShowCursor { get; set; } = true;
    public bool BlinkCursor { get; set; } = true;
    public string CursorStyle { get; set; } = "block"; // block, underline, bar
}

/// <summary>
/// 终端外观配置服务
/// </summary>
public class TerminalAppearanceService
{
    private readonly string _settingsFilePath;
    private TerminalAppearanceSettings _settings = new();

    public event EventHandler<TerminalAppearanceSettings>? SettingsChanged;

    public TerminalAppearanceService()
    {
        var appDir = AppPaths.LocalAppDataDir;
        Directory.CreateDirectory(appDir);
        _settingsFilePath = Path.Combine(appDir, "terminal_appearance.json");
        LoadSettings();
    }

    public TerminalAppearanceSettings GetSettings()
    {
        return _settings;
    }

    public void UpdateSettings(TerminalAppearanceSettings newSettings)
    {
        _settings = newSettings;
        SaveSettings();
        SettingsChanged?.Invoke(this, _settings);
    }

    public void ResetToDefaults()
    {
        _settings = new TerminalAppearanceSettings();
        SaveSettings();
        SettingsChanged?.Invoke(this, _settings);
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonConvert.DeserializeObject<TerminalAppearanceSettings>(json);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load terminal appearance error: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save terminal appearance error: {ex.Message}");
        }
    }
}
