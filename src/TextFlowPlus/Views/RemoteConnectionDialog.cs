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
/// “打开远程窗口”对话框的结果：已建立的 SFTP 连接 + 用户选中的远程文件夹。
/// <see cref="Sftp"/> 的所有权随结果一并移交给调用方，由调用方负责后续释放。
/// </summary>
public sealed class RemoteDialogResult
{
    public RemoteDialogResult(RemoteConnectionInfo info, SftpService sftp, string folderPath)
    {
        Info = info;
        Sftp = sftp;
        FolderPath = folderPath;
    }

    public RemoteConnectionInfo Info { get; }
    public SftpService Sftp { get; }
    /// <summary>用户在浏览窗口中选中的远程文件夹绝对路径。</summary>
    public string FolderPath { get; }
}

/// <summary>
/// “打开远程窗口”对话框：
/// - 上方列出已保存的主机（双击直接连接，每项可编辑/删除）
/// - 下方输入 主机IP / 用户名 / 密码 / 端口
/// - “测试连接”按钮测试连通性；“确定”按钮在连接测试通过后连接远程主机，
///   未通过则提示修改连接信息
/// - 返回成功连接的 RemoteConnectionInfo（取消/失败返回 null）
/// </summary>
public class RemoteConnectionDialog : Window
{
    private readonly List<RemoteConnectionInfo> _connections = RemoteConnectionStore.Load();
    private readonly StackPanel _savedSection = new();
    private readonly ListBox _connectionsListBox = new()
    {
        MaxHeight = 150,
        SelectionMode = SelectionMode.Single
    };
    /// <summary>与 _connectionsListBox.Items 一一对应的连接对象（Items 里放的是 UI 控件，取不回数据）。</summary>
    private readonly List<RemoteConnectionInfo> _listedConnections = new();
    private readonly TextBox _hostTextBox = new();
    private readonly TextBox _usernameTextBox = new();
    private readonly TextBox _passwordTextBox = new() { PasswordChar = '*' };
    private readonly TextBox _portTextBox = new() { Text = "22" };
    private readonly TextBlock _statusText = new();

    public RemoteConnectionDialog()
    {
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = LangService.T("打开远程窗口");
        Background = ThemeBrush("EditorBackgroundBrush");
        Content = BuildUi();
        // 选中已保存的服务器 → 回填输入框。必须在下方 RebuildConnectionList() 之前挂接。
        _connectionsListBox.SelectionChanged += ConnectionsListBox_SelectionChanged;
        RebuildConnectionList();
        // 打开后把输入光标放到第一个输入框（主机 IP）；构造期间控件还未接入可视树，那时 Focus() 不生效
        Opened += (_, _) => _hostTextBox.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = OkButtonAsync();
            else if (e.Key == Key.Escape) Close();
        };
    }

    // ========== UI 构建 ==========

    private Control BuildUi()
    {
        var root = new DockPanel();

        // 顶部主题色条
        var accentBar = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Color.FromRgb(31, 111, 235)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        DockPanel.SetDock(accentBar, Dock.Top);
        root.Children.Add(accentBar);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 16), Spacing = 10 };
        DockPanel.SetDock(body, Dock.Bottom);
        root.Children.Add(body);

        // —— 已保存的主机列表 ——
        _savedSection.Spacing = 6;
        _savedSection.Children.Add(new TextBlock
        {
            Text = LangService.T("已保存的服务器（双击连接，或点 编辑/删除）"),
            FontSize = 12,
            Foreground = ThemeBrush("TextSecondaryBrush")
        });
        _savedSection.Children.Add(_connectionsListBox);
        body.Children.Add(_savedSection);

        // —— 连接信息表单 ——
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        form.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        AddFormRow(form, 0, LangService.T("主机 IP："), _hostTextBox);
        AddFormRow(form, 1, LangService.T("用户名："), _usernameTextBox);
        AddFormRow(form, 2, LangService.T("密码："), _passwordTextBox);
        AddFormRow(form, 3, LangService.T("端口："), _portTextBox);
        body.Children.Add(form);

        // —— 状态行 ——
        _statusText.Text = LangService.T("请输入主机 IP、用户名和密码，点击“测试连接”验证连通性。");
        _statusText.FontSize = 11.5;
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.Foreground = ThemeBrush("TextSecondaryBrush");
        body.Children.Add(_statusText);

        // —— 按钮行 ——
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        buttons.Children.Add(MessageBoxUtil.CreateMessageButton(LangService.T("测试连接"), primary: false, () => _ = TestButtonAsync()));
        buttons.Children.Add(MessageBoxUtil.CreateMessageButton(LangService.T("取消"), primary: false, () => Close()));
        buttons.Children.Add(MessageBoxUtil.CreateMessageButton(LangService.T("确定"), primary: true, () => _ = OkButtonAsync()));
        body.Children.Add(buttons);

        return root;
    }

    private static void AddFormRow(Grid grid, int row, string label, TextBox input)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = ThemeBrush("LabelForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(labelText, row);
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        input.Margin = new Thickness(8, 2, 0, 2);
        input.Padding = new Thickness(8, 5);
        input.FontSize = 12.5;
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    // ========== 已保存主机列表 ==========

    private void RebuildConnectionList()
    {
        _connectionsListBox.Items.Clear();
        _listedConnections.Clear();
        foreach (var connection in _connections.OrderByDescending(c => c.LastConnected))
        {
            _connectionsListBox.Items.Add(BuildConnectionItem(connection));
            _listedConnections.Add(connection);
        }
        _savedSection.IsVisible = _connections.Count > 0;
    }

    /// <summary>列表中选中某一项：把该服务器的信息填入下方输入框，方便直接连接或微调后再连。</summary>
    private void ConnectionsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = _connectionsListBox.SelectedIndex;
        if (index < 0 || index >= _listedConnections.Count) return;
        FillForm(_listedConnections[index]);
    }

    /// <summary>把连接信息填入表单（密码解密后回填；不触发任何连接动作）。</summary>
    private void FillForm(RemoteConnectionInfo connection)
    {
        _hostTextBox.Text = connection.Host;
        _usernameTextBox.Text = connection.Username;
        _passwordTextBox.Text = connection.Password;
        _portTextBox.Text = connection.Port.ToString();
        SetStatus(LangService.TFormat("已选择 {0}，点击“确定”连接并选择文件夹。", connection.DisplayName),
            Color.FromRgb(31, 111, 235));
    }

    /// <summary>单个主机列表项：左侧显示区（双击连接）+ 右侧 编辑/删除 按钮。</summary>
    private Control BuildConnectionItem(RemoteConnectionInfo connection)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // 显示区（双击 → 连接）
        var hitArea = new Border { Padding = new Thickness(8, 6), Background = Brushes.Transparent };
        hitArea.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _ = OpenFromListAsync(connection);
            }
        }, RoutingStrategies.Bubble);

        var display = new StackPanel { Spacing = 1 };
        display.Children.Add(new TextBlock
        {
            Text = connection.DisplayName,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush("LabelForegroundBrush")
        });
        display.Children.Add(new TextBlock
        {
            Text = LangService.TFormat("上次连接：{0:yyyy-MM-dd HH:mm}", connection.LastConnected),
            FontSize = 10.5,
            Foreground = ThemeBrush("TextSecondaryBrush")
        });
        hitArea.Child = display;
        Grid.SetColumn(hitArea, 0);
        grid.Children.Add(hitArea);

        // 编辑 / 删除
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(CreateSmallButton(LangService.T("编辑"), () => EditConnection(connection)));
        buttons.Children.Add(CreateSmallButton(LangService.T("删除"), () => DeleteConnection(connection)));
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var border = new Border
        {
            Child = grid,
            BorderBrush = ThemeBrush("PanelBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4)
        };
        border.PointerEntered += (_, _) => border.Background = ThemeBrush("SecondaryHoverBackgroundBrush");
        border.PointerExited += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    private static Border CreateSmallButton(string text, Action onClick)
    {
        var button = new Border
        {
            Padding = new Thickness(10, 3),
            Background = ThemeBrush("TextBoxBackgroundBrush"),
            BorderBrush = ThemeBrush("ButtonSecondaryBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = ThemeBrush("ButtonSecondaryForegroundBrush"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var hoverBg = new SolidColorBrush(Color.FromRgb(237, 245, 253));
        var hoverBorder = new SolidColorBrush(Color.FromRgb(156, 195, 240));
        var pressedBg = new SolidColorBrush(Color.FromRgb(216, 234, 252));
        button.PointerEntered += (_, _) => { button.Background = hoverBg; button.BorderBrush = hoverBorder; };
        button.PointerExited += (_, _) => { button.Background = ThemeBrush("TextBoxBackgroundBrush"); button.BorderBrush = ThemeBrush("ButtonSecondaryBorderBrush"); };
        button.PointerPressed += (_, _) => button.Background = pressedBg;
        button.AddHandler(InputElement.TappedEvent, (_, e) =>
        {
            e.Handled = true;
            onClick();
        }, RoutingStrategies.Bubble);
        return button;
    }

    private void EditConnection(RemoteConnectionInfo connection)
    {
        FillForm(connection);
        SetStatus(LangService.TFormat("正在编辑 {0}，修改后点击“确定”保存。", connection.DisplayName), Color.FromRgb(31, 111, 235));
    }

    private void DeleteConnection(RemoteConnectionInfo connection)
    {
        RemoteConnectionStore.Remove(connection);
        _connections.Clear();
        _connections.AddRange(RemoteConnectionStore.Load());
        RebuildConnectionList();
        SetStatus(LangService.TFormat("已删除 {0}", connection.DisplayName), Color.FromRgb(130, 130, 130));
    }

    // ========== 连接逻辑 ==========

    /// <summary>读取表单，校验并生成连接信息；校验失败返回 null 并显示错误。</summary>
    private RemoteConnectionInfo? CollectForm()
    {
        var host = _hostTextBox.Text?.Trim() ?? "";
        var username = _usernameTextBox.Text?.Trim() ?? "";
        var password = _passwordTextBox.Text ?? "";
        var portText = _portTextBox.Text?.Trim() ?? "22";

        if (string.IsNullOrEmpty(host))
        {
            SetStatus(LangService.T("请输入主机 IP 地址。"), Color.FromRgb(211, 47, 47));
            return null;
        }
        if (string.IsNullOrEmpty(username))
        {
            SetStatus(LangService.T("请输入用户名。"), Color.FromRgb(211, 47, 47));
            return null;
        }
        if (string.IsNullOrEmpty(password))
        {
            SetStatus(LangService.T("请输入密码。"), Color.FromRgb(211, 47, 47));
            return null;
        }
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
        {
            SetStatus(LangService.T("端口号无效（1-65535）。"), Color.FromRgb(211, 47, 47));
            return null;
        }

        return new RemoteConnectionInfo
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password
        };
    }

    private SshConnectionInfo ToSshConnectionInfo(RemoteConnectionInfo info) => new()
    {
        Host = info.Host,
        Port = info.Port,
        Username = info.Username,
        Password = info.Password,
        TimeoutSeconds = 10
    };

    /// <summary>测试连通性：成功返回 true 并显示绿色状态；失败显示红色错误（可选弹窗提示修改）。</summary>
    private async Task<bool> TestConnectionAsync(RemoteConnectionInfo info, bool promptOnFail)
    {
        try
        {
            SetStatus(LangService.TFormat("正在测试连接 {0} ...", info.DisplayName), Color.FromRgb(100, 100, 100));
            using var sftp = new SftpService();
            await sftp.ConnectAsync(ToSshConnectionInfo(info));
            SetStatus(LangService.TFormat("✅ 连接成功：{0}", info.DisplayName), Color.FromRgb(33, 150, 83));
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(LangService.TFormat("❌ 连接失败：{0}", ex.Message), Color.FromRgb(211, 47, 47));
            if (promptOnFail)
            {
                await MessageBoxUtil.Show(this, LangService.TFormat("连接测试未通过，请修改连接信息。\n\n{0}", ex.Message),
                    LangService.T("连接失败"), 0);
            }
            return false;
        }
    }

    private async Task TestButtonAsync()
    {
        var info = CollectForm();
        if (info == null) return;
        await TestConnectionAsync(info, promptOnFail: false);
    }

    /// <summary>
    /// “确定”：先建立连接，再弹出远程文件夹浏览窗口选择要打开的文件夹。
    /// 结果通过 <see cref="RemoteDialogResult"/> 一并回传（连接 + 选中的远程路径）。
    /// 浏览窗口被直接关闭 / 点取消 → 整个对话框不关闭也不提交（用户可继续改信息重试）。
    /// </summary>
    private async Task OkButtonAsync()
    {
        var info = CollectForm();
        if (info == null) return;

        SftpService? sftp = null;
        try
        {
            SetStatus(LangService.TFormat("正在连接 {0} ...", info.DisplayName), Color.FromRgb(100, 100, 100));
            sftp = new SftpService();
            await sftp.ConnectAsync(ToSshConnectionInfo(info));
        }
        catch (Exception ex)
        {
            sftp?.Dispose();
            SetStatus(LangService.TFormat("❌ 连接失败：{0}", ex.Message), Color.FromRgb(211, 47, 47));
            await MessageBoxUtil.Show(this, LangService.TFormat("连接测试未通过，请修改连接信息。\n\n{0}", ex.Message),
                LangService.T("连接失败"), 0);
            return;
        }

        // 连接成功 → 浏览并选择文件夹
        try
        {
            SetStatus(LangService.TFormat("✅ 连接成功：{0}", info.DisplayName), Color.FromRgb(33, 150, 83));

            var home = string.IsNullOrEmpty(sftp.WorkingDirectory) ? "/" : sftp.WorkingDirectory;
            var browser = new RemoteFolderBrowserDialog(sftp, home);
            var selected = await browser.ShowDialog<string?>(this);

            if (string.IsNullOrEmpty(selected))
            {
                // 用户直接关闭浏览窗口或点了取消：不做任何操作，保留连接对话框让用户继续操作
                SetStatus(LangService.T("已取消选择文件夹。"), Color.FromRgb(130, 130, 130));
                sftp.Dispose();
                return;
            }

            info.LastConnected = DateTime.Now;
            RemoteConnectionStore.Save(info);

            // 连接的所有权移交给主窗口（由它负责后续浏览与释放）
            Close(new RemoteDialogResult(info, sftp, selected));
        }
        catch (Exception ex)
        {
            sftp.Dispose();
            SetStatus(LangService.TFormat("❌ 连接失败：{0}", ex.Message), Color.FromRgb(211, 47, 47));
        }
    }

    private async Task OpenFromListAsync(RemoteConnectionInfo connection)
    {
        FillForm(connection);
        await OkButtonAsync();
    }

    // ========== 主题配色 ==========

    /// <summary>从当前主题资源取颜色画刷（与主窗口一致，显式按主题变体解析）。</summary>
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
