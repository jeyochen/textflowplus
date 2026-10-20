using Avalonia;
using System;
using System.IO;
using System.Linq;
using TextFlowPlus.Services;

namespace TextFlowPlus;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 收集命令行参数中的有效文件/文件夹路径（文件关联、“打开方式 → TextFlowPlus”等）
        var paths = args
            .Where(a => !string.IsNullOrWhiteSpace(a) && (File.Exists(a) || Directory.Exists(a)))
            .Select(a => a.Trim())
            .ToList();

        // 单实例：若已有实例在运行，把路径交给它并立即退出
        if (!SingleInstanceService.TryAcquirePrimary(paths))
        {
            return;
        }

        var app = BuildAvaloniaApp();

        // 本实例是唯一实例：把自身参数中的路径交给窗口初始化后打开
        if (paths.Count > 0)
        {
            App.FilesToOpen.AddRange(paths);
        }

        app.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
