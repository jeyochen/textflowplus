using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

public partial class UpdateAvailableDialog : Window
{
    private const string DownloadUrl = "https://www.app2s.cn";

    private readonly string _newVersion;

    public UpdateAvailableDialog(string newVersion)
    {
        InitializeComponent();
        _newVersion = newVersion ?? "";
        OkButton.Click += (s, e) => Close();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Title = LangService.T("发现新版本");

        if (string.IsNullOrWhiteSpace(_newVersion))
        {
            BodyText.Text = LangService.T("有新版请到以下地址下载最新安装包：");
        }
        else
        {
            BodyText.Text = LangService.TFormat("有新版本 v{0}，请到以下地址下载最新安装包：", _newVersion);
        }
        OkButton.Content = LangService.T("确定");
    }

    /// <summary>点击链接：用系统默认浏览器打开下载页面。</summary>
    private void DownloadLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DownloadUrl,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", DownloadUrl);
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = DownloadUrl,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 打开浏览器失败不阻塞对话框
        }
    }
}
