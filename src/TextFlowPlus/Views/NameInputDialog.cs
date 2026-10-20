using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace TextFlowPlus.Views;

/// <summary>
/// 名称输入对话框（新建远程文件/文件夹时输入名称）：
/// 顶部主题色条 + 标签 + 文本框 + 取消/确定；校验非法字符（/ \ 等）。
/// 确定返回输入的名称，取消或空名返回 null。
/// </summary>
public class NameInputDialog : Window
{
    private readonly TextBox _nameBox = new() { Padding = new Thickness(8, 5), FontSize = 12.5 };
    private readonly TextBlock _errorText;

    /// <summary>弹出对话框，返回输入的名称（取消/非法输入返回 null）。</summary>
    public static async Task<string?> ShowAsync(Window owner, string title, string label, string defaultValue)
    {
        var dialog = new NameInputDialog(title, label, defaultValue);
        return await dialog.ShowDialog<string?>(owner);
    }

    public NameInputDialog(string title, string label, string defaultValue)
    {
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = title;
        // 背景不在此处写死：由 App.axaml 的 Window 样式按当前主题（深色 #1E1E1E / 亮色 #FFF）统一提供

        var root = new DockPanel();

        // 顶部主题色条
        var accentBar = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Color.FromRgb(31, 111, 235)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        DockPanel.SetDock(accentBar, Dock.Top);
        root.Children.Add(accentBar);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 16), Spacing = 10 };
        DockPanel.SetDock(body, Dock.Bottom);
        root.Children.Add(body);

        body.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = MessageBoxUtil.ThemeBrush("LabelForegroundBrush")
        });

        _nameBox.Text = defaultValue;
        body.Children.Add(_nameBox);

        _errorText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MessageBoxUtil.ThemeBrush("ErrorForegroundBrush"),
            IsVisible = false
        };
        body.Children.Add(_errorText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        buttons.Children.Add(MessageBoxUtil.CreateMessageButton("取消", primary: false, () => Close()));
        buttons.Children.Add(MessageBoxUtil.CreateMessageButton("确定", primary: true, () => Confirm()));
        body.Children.Add(buttons);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Confirm();
            else if (e.Key == Key.Escape) Close();
        };

        Opened += (_, _) => _nameBox.Focus();
        Content = root;
    }

    private void Confirm()
    {
        var name = _nameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ShowError("名称不能为空。");
            return;
        }
        if (name == "." || name == "..")
        {
            ShowError("名称不能为 . 或 ..");
            return;
        }
        // 远程路径分隔符与非法控制字符
        if (name.Contains('/') || name.Contains('\\') || name.Contains('\0') ||
            name.Any(c => c < 0x20) || name.Any(char.IsControl))
        {
            ShowError("名称包含非法字符（如 / \\ 或控制字符）。");
            return;
        }
        // 去掉首尾空格后交付（Trim 已在上面完成）
        Close(name);
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }
}
