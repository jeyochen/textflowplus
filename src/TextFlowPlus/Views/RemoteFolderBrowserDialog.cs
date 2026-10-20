using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TextFlowPlus.Services.Ssh;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

/// <summary>
/// 远程文件夹浏览窗口（类似系统的文件夹选择框）：
/// - 顶部为当前路径，可手动编辑后回车跳转；上一级按钮回到父目录
/// - 中间列出当前目录下的子文件夹（只列文件夹，不列文件）
/// - “打开”返回选中文件夹的路径；“取消”或直接关闭窗口返回 null（主窗口不做任何操作）
/// 只负责浏览与选择，不负责建立连接——SFTP 连接由调用方持有并传入。
/// </summary>
public class RemoteFolderBrowserDialog : Window
{
    private readonly SftpService _sftp;
    private readonly string _homePath;
    private readonly ListBox _folderList = new();
    private readonly TextBox _pathTextBox = new();
    private readonly TextBlock _statusText = new();

    /// <summary>当前显示的目录（绝对路径）。</summary>
    private string _currentPath;

    /// <summary>
    /// 构造浏览窗口。
    /// </summary>
    /// <param name="sftp">已连接的 SFTP 服务（由调用方负责生命周期）。</param>
    /// <param name="homePath">初始目录，一般为远程用户主目录。</param>
    public RemoteFolderBrowserDialog(SftpService sftp, string homePath)
    {
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
        _homePath = string.IsNullOrEmpty(homePath) ? "/" : homePath;
        _currentPath = _homePath;

        Width = 480;
        Height = 460;
        CanResize = true;
        MinWidth = 360;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = LangService.T("选择远程文件夹");
        Background = ThemeBrush("EditorBackgroundBrush");
        Content = BuildUi();

        _folderList.DoubleTapped += (_, _) => EnterSelectedFolder();
        _pathTextBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            NavigateTo(_pathTextBox.Text?.Trim() ?? "");
        };
        // 手动编辑路径也会影响“打开”的可用性（空路径时按钮要变灰）
        _pathTextBox.TextChanged += (_, _) => UpdateOkState();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
        };

        // 首次加载在窗口显示后执行（构造函数里控件尚未接入可视树）
        Opened += (_, _) => _ = LoadDirectoryAsync(_currentPath);
    }

    // ========== UI 构建 ==========

    private Control BuildUi()
    {
        var root = new DockPanel();

        var accentBar = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Color.FromRgb(31, 111, 235)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        DockPanel.SetDock(accentBar, Dock.Top);
        root.Children.Add(accentBar);

        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 14), Spacing = 9 };
        DockPanel.SetDock(body, Dock.Bottom);
        root.Children.Add(body);

        // —— 路径行：上一级 + 可编辑路径 ——
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        pathRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        var upButton = CreateSmallButton("⬆", () => NavigateTo(ParentPath(_currentPath)));
        ToolTip.SetTip(upButton, LangService.T("上一级"));
        upButton.VerticalAlignment = VerticalAlignment.Stretch;
        upButton.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(upButton, 0);
        pathRow.Children.Add(upButton);

        _pathTextBox.FontSize = 12.5;
        _pathTextBox.Padding = new Thickness(8, 5);
        Grid.SetColumn(_pathTextBox, 1);
        pathRow.Children.Add(_pathTextBox);
        body.Children.Add(pathRow);

        // —— 文件夹列表（占满剩余空间）——
        _folderList.SelectionMode = SelectionMode.Single;
        _folderList.Background = ThemeBrush("EditorBackgroundBrush");
        _folderList.BorderBrush = ThemeBrush("PanelBorderBrush");
        _folderList.BorderThickness = new Thickness(1);
        _folderList.CornerRadius = new CornerRadius(4);
        _folderList.SelectionChanged += (_, _) => UpdateOkState();
        root.Children.Add(_folderList);

        // —— 状态行 ——
        _statusText.FontSize = 11.5;
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.Foreground = ThemeBrush("TextSecondaryBrush");
        _statusText.Text = LangService.T("加载中…");
        body.Children.Add(_statusText);

        // —— 按钮行 ——
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        buttons.Children.Add(MessageBoxUtil.CreateMessageButton(LangService.T("取消"), primary: false, () => Close(null)));
        _okButton = MessageBoxUtil.CreateMessageButton(LangService.T("打开"), primary: true, OpenSelectedFolder);
        buttons.Children.Add(_okButton);
        body.Children.Add(buttons);

        return root;
    }

    // ========== 目录加载与导航 ==========

    /// <summary>加载指定目录下的子文件夹；失败时在状态行显示红色错误并保留当前路径。</summary>
    private async Task LoadDirectoryAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) path = "/";
        try
        {
            SetStatus(LangService.T("加载中…"), Color.FromRgb(130, 130, 130));

            var entries = await _sftp.ListDirectoryAsync(path);
            var dirs = entries
                .Where(en => en.IsDirectory && en.Name != "." && en.Name != "..")
                .OrderBy(en => en.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _currentPath = path;
            _pathTextBox.Text = path;

            _folderList.Items.Clear();
            foreach (var dir in dirs)
            {
                _folderList.Items.Add(BuildFolderItem(dir.Name, dir.FullName));
            }

            SetStatus(dirs.Count == 0
                ? LangService.T("此目录下没有子文件夹")
                : LangService.TFormat("共 {0} 个子文件夹", dirs.Count),
                Color.FromRgb(130, 130, 130));
        }
        catch (Exception ex)
        {
            SetStatus(LangService.TFormat("无法读取目录：{0}", ex.Message), Color.FromRgb(211, 47, 47));
        }
        UpdateOkState();
    }

    /// <summary>导航到指定目录（路径非法或读取失败时停留在原目录）。</summary>
    private void NavigateTo(string path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        _ = LoadDirectoryAsync(target);
    }

    /// <summary>双击文件夹项：进入该文件夹。</summary>
    private void EnterSelectedFolder()
    {
        if (SelectedFolderPath() is { } path) NavigateTo(path);
    }

    /// <summary>
    /// “打开”按钮：选中了文件夹就打开选中的那个；没有选中则打开路径框中当前显示的路径。
    /// 与系统文件夹选择框一致——不强制必须先点选列表项。
    /// </summary>
    private void OpenSelectedFolder()
    {
        var path = SelectedFolderPath();
        if (string.IsNullOrEmpty(path))
        {
            // 未选中列表项 → 采用路径框中的文本（用户可能手动输入/编辑过）
            path = _pathTextBox.Text?.Trim();
        }
        if (string.IsNullOrEmpty(path))
        {
            SetStatus(LangService.T("请选择文件夹或输入路径。"), Color.FromRgb(211, 47, 47));
            return;
        }
        Close(path);
    }

    private string? SelectedFolderPath() =>
        _folderList.SelectedItem is Control { Tag: string path } ? path : null;

    /// <summary>
    /// 刷新“打开”按钮可用性。只要路径框有内容就可用（未选中列表项时按路径框内容打开），
    /// 因此在列表加载/路径变化时都要调用——否则会出现"选了文件夹但按钮仍是禁用"的情况。
    /// </summary>
    private void UpdateOkState()
    {
        if (_okButton == null) return;
        var hasTarget =
            SelectedFolderPath() != null ||
            !string.IsNullOrWhiteSpace(_pathTextBox.Text);
        _okButton.IsEnabled = hasTarget;
    }

    private Border? _okButton;

    /// <summary>文件夹列表项：文件夹图标 + 名称，Tag 携带完整路径。</summary>
    private static Control BuildFolderItem(string name, string fullPath)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = "📁",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("LabelForegroundBrush")
        });

        return new Border
        {
            Child = row,
            Tag = fullPath,
            Padding = new Thickness(8, 5),
            // 必须是可命中的画刷：Transparent 也是可命中的，但不能用 null，
            // 否则整行不参与命中测试，点行边缘可能选不中。
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3)
        };
    }

    /// <summary>取父目录：/a/b → /a，/a → /，/ → /。</summary>
    private static string ParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return "/";           // 根目录
        var idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";                      // /a → /
        return trimmed.Substring(0, idx);
    }

    // ========== 交互辅助 ==========

    private static Button CreateSmallButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4),
            FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static IBrush ThemeBrush(string key)
    {
        if (Application.Current is { } app &&
            app.RequestedThemeVariant is { } variant &&
            app.TryFindResource(key, variant, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }
        var fallback = Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? Color.FromRgb(0x1E, 0x1E, 0x1E)
            : Colors.White;
        return new SolidColorBrush(fallback);
    }

    private void SetStatus(string text, Color color)
    {
        _statusText.Text = text;
        _statusText.Foreground = new SolidColorBrush(color);
    }
}