using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Collections.Generic;
using System.Threading;
using TextFlowPlus.Services;

namespace TextFlowPlus;

public partial class App : Application
{
    public static Views.MainWindow? APP { get; set; }
    public static List<string> FilesToOpen { get; set; } = new List<string>();

    private static CancellationTokenSource? _singleInstanceCts;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = App.APP = new Views.MainWindow();

            // 初始化完成后打开命令行参数中指定的文件/文件夹
            foreach (var filePath in FilesToOpen)
            {
                App.APP?.OpenPath(filePath);
            }
            FilesToOpen.Clear();

            // 单实例：主实例监听命名管道，“打开方式 → TextFlowPlus”二次启动投递的路径在此打开
            _singleInstanceCts = new CancellationTokenSource();
            _ = SingleInstanceService.StartServerAsync(
                path => Avalonia.Threading.Dispatcher.UIThread.Post(() => App.APP?.OpenPath(path)),
                _singleInstanceCts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
