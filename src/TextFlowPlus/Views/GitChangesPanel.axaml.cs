using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TextFlowPlus.Services.Git;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

/// <summary>Git 修改列表项（绑定用）。</summary>
public sealed class GitChangeItem
{
    public required string StatusText { get; init; }
    public required string DisplayPath { get; init; }
    public required string FullPath { get; init; }
    public IBrush StatusBrush { get; init; } = new SolidColorBrush(Colors.Gray);
}

/// <summary>
/// Git 修改面板：显示当前 Git 仓库中修改/新增/删除/未跟踪的文件列表。
/// 双击文件触发 <see cref="DiffRequested"/>（MainWindow 打开修改前后对比视图）。
/// </summary>
public partial class GitChangesPanel : UserControl
{
    /// <summary>用户双击修改文件（携带完整路径）。</summary>
    public event Action<string>? DiffRequested;

    /// <summary>用户单击选中文件（携带完整路径；可为 null）。</summary>
    public event Action<string>? FileClicked;

    /// <summary>操作结果反馈（MainWindow 注入状态栏）。</summary>
    public event Action<string>? StatusReported;

    private string? _repoRoot;
    private bool _isRemote;

    /// <summary>全部分支名（过滤前的原始列表）。</summary>
    private List<string> _allBranches = new();
    /// <summary>当前分支名。</summary>
    private string? _currentBranch;
    /// <summary>程序更新分支 UI 时置位，避免触发切换/过滤副作用。</summary>
    private bool _updatingBranchUi;

    /// <summary>当前修改数量（供状态栏/标题使用）。</summary>
    public int ChangeCount { get; private set; }

    public GitChangesPanel()
    {
        InitializeComponent();
        RefreshButton.Click += (_, _) => Refresh();
        CommitButton.Click += async (_, _) =>
        {
            try { await CommitClickedAsync(); }
            catch (Exception ex) { StatusReported?.Invoke(LangService.TFormat("提交失败：{0}", ex.Message)); }
        };
        PullButton.Click += (_, _) => RunGitOperationAsync("拉取", () => GitService.Pull(_repoRoot!));
        PushButton.Click += (_, _) => RunGitOperationAsync("推送", () => GitService.Push(_repoRoot!));
        ChangesList.AddHandler(InputElement.DoubleTappedEvent, OnListDoubleTapped, RoutingStrategies.Bubble);
        ChangesList.SelectionChanged += (_, _) =>
        {
            if (ChangesList.SelectedItem is GitChangeItem item) FileClicked?.Invoke(item.FullPath);
        };
        // 分支输入/选择：输入变化做模糊过滤；选中项变化做分支切换
        BranchComboBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) OnBranchFilterChanged();
        };
        BranchComboBox.SelectionChanged += OnBranchSelectionChanged;
        ApplyThemeColors();
        LangService.LanguageChanged += ApplyTexts;
        ApplyTexts();
    }

    /// <summary>点击提交：弹出提交信息输入框；取消则不提交，确定后执行 add -A + commit。</summary>
    private async Task CommitClickedAsync()
    {
        if (string.IsNullOrEmpty(_repoRoot))
        {
            StatusReported?.Invoke(LangService.T("未打开 Git 仓库"));
            return;
        }
        var message = await ShowCommitDialogAsync();
        if (string.IsNullOrEmpty(message))
        {
            StatusReported?.Invoke(LangService.T("已取消提交"));
            return;
        }
        await RunGitOperationAsync("提交", () => GitService.Commit(_repoRoot!, message));
    }

    /// <summary>弹出一个输入提交信息的对话框；确定返回文本，取消/关闭返回 null。</summary>
    private Task<string?> ShowCommitDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var win = new Window
        {
            Title = LangService.T("提交更改"),
            Width = 440,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var box = new TextBox
        {
            Watermark = LangService.T("输入提交信息（支持多行）"),
            AcceptsReturn = true,
            MinHeight = 60,
            MaxLines = 4,
            Margin = new Thickness(12, 6, 12, 8)
        };
        // 打开后把输入光标定位到提交信息框（构造期聚焦无效，窗口打开后才有效）
        win.Opened += (_, _) => box.Focus();
        var okButton = new Button
        {
            Content = LangService.T("确定"),
            Width = 84,
            FontSize = 12.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var cancelButton = new Button
        {
            Content = LangService.T("取消"),
            Width = 84,
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        okButton.Click += (_, _) =>
        {
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                StatusReported?.Invoke(LangService.T("提交信息不能为空"));
                return;
            }
            tcs.TrySetResult(text);
            win.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            win.Close();
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
            Children = { okButton, cancelButton }
        };
        var panel = new StackPanel { Children = { new TextBlock { Text = LangService.T("输入提交信息："), Margin = new Thickness(12, 12, 12, 0) }, box, buttons } };
        win.Content = panel;
        win.Closed += (_, _) => tcs.TrySetResult(null);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null) win.ShowDialog(owner);
        else win.Show();
        return tcs.Task;
    }

    /// <summary>后台执行 git 操作，完成后刷新列表并反馈结果。</summary>
    private async Task RunGitOperationAsync(string opName, Func<(bool Ok, string Message)> operation)
    {
        if (string.IsNullOrEmpty(_repoRoot))
        {
            StatusReported?.Invoke(LangService.T("未打开 Git 仓库"));
            return;
        }
        try
        {
            StatusReported?.Invoke(LangService.TFormat("正在{0}…", LangService.T(opName)));
            var (ok, message) = await Task.Run(operation);
            Refresh();
            StatusReported?.Invoke(ok ? LangService.TFormat("{0}成功：{1}", LangService.T(opName), message) : LangService.TFormat("{0}失败：{1}", LangService.T(opName), message));
        }
        catch (Exception ex)
        {
            StatusReported?.Invoke(LangService.TFormat("{0}失败：{1}", LangService.T(opName), ex.Message));
        }
    }

    /// <summary>按当前界面语言刷新标题与按钮提示。</summary>
    public void ApplyTexts()
    {
        ToolTip.SetTip(CommitButton, LangService.T("提交"));
        ToolTip.SetTip(PullButton, LangService.T("拉取"));
        ToolTip.SetTip(PushButton, LangService.T("推送"));
        ToolTip.SetTip(RefreshButton, LangService.T("刷新 Git 修改列表"));
        Refresh();
    }
    /// <summary>设置仓库根（null 或远程模式：清空列表并提示）。</summary>
    public void SetRepository(string? repoRoot, bool isRemote = false)
    {
        _repoRoot = repoRoot;
        _isRemote = isRemote;
        Refresh();
        bool hasRepo = !string.IsNullOrEmpty(repoRoot) && !isRemote;
        CommitButton.IsEnabled = hasRepo;
        PullButton.IsEnabled = hasRepo;
        PushButton.IsEnabled = hasRepo;
    }

    /// <summary>重新读取 git status 并刷新列表。</summary>
    public void Refresh()
    {
        if (string.IsNullOrEmpty(_repoRoot))
        {
            HeaderText.Text = LangService.T(_isRemote ? "Git（远程模式下不可用）" : "Git（未打开 Git 仓库）");
            ChangesList.ItemsSource = null;
            BranchComboBox.ItemsSource = null;
            return;
        }

        List<GitChange> changes;
        try
        {
            changes = GitService.GetChanges(_repoRoot!);
        }
        catch
        {
            changes = new List<GitChange>();
        }

        // 刷新分支列表与当前分支（保持当前选择，不触发切换）
        try
        {
            _allBranches = GitService.GetBranches(_repoRoot!);
            _currentBranch = GitService.GetCurrentBranch(_repoRoot!);
        }
        catch
        {
            _allBranches = new List<string>();
            _currentBranch = null;
        }
        _updatingBranchUi = true;
        BranchComboBox.ItemsSource = _allBranches;
        if (_currentBranch != null && _allBranches.Contains(_currentBranch))
        {
            BranchComboBox.SelectedItem = _currentBranch;
            BranchComboBox.Text = _currentBranch;
        }
        else
        {
            BranchComboBox.SelectedItem = null;
        }
        _updatingBranchUi = false;

        var items = new List<GitChangeItem>(changes.Count);
        foreach (var c in changes)
        {
            items.Add(new GitChangeItem
            {
                StatusText = c.StatusText,
                DisplayPath = ToDisplay(c.RelPath),
                FullPath = c.FullPath,
                StatusBrush = StatusBrush(c.StatusText)
            });
        }
        HeaderText.Text = LangService.TFormat("Git（{0}）", items.Count);
        ChangesList.ItemsSource = items;
        ChangeCount = items.Count;
    }

    /// <summary>输入变化：按关键字模糊过滤分支列表（包含匹配，忽略大小写），并打开下拉供选择。
    /// 过滤延迟到当前事件链结束后执行：Avalonia 在点击下拉项的事件处理中会更新 ComboBox.Text，
    /// 若同步替换 ItemsSource，事件继续枚举旧选中索引时会越界（ItemsSourceView.GetAt 崩溃）。</summary>
    private void OnBranchFilterChanged()
    {
        if (_updatingBranchUi || string.IsNullOrEmpty(_repoRoot)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_updatingBranchUi || string.IsNullOrEmpty(_repoRoot)) return;
            var filter = BranchComboBox.Text?.Trim() ?? "";
            var filtered = string.IsNullOrEmpty(filter)
                ? _allBranches
                : _allBranches.Where(b => b.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            _updatingBranchUi = true;
            BranchComboBox.ItemsSource = filtered;
            if (_currentBranch != null && filtered.Contains(_currentBranch)) BranchComboBox.SelectedItem = _currentBranch;
            else BranchComboBox.SelectedItem = null;
            _updatingBranchUi = false;
            if (!string.IsNullOrEmpty(filter)) BranchComboBox.IsDropDownOpen = true;
        });
    }

    /// <summary>选择分支：非程序更新且与当前分支不同则执行 git checkout 切换。</summary>
    private void OnBranchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingBranchUi || string.IsNullOrEmpty(_repoRoot)) return;
        if (BranchComboBox.SelectedItem is not string branch) return;
        if (branch == _currentBranch) return;
        _ = SwitchBranchAsync(branch);
    }

    /// <summary>后台执行 git checkout，完成后刷新修改列表与分支状态。</summary>
    private async Task SwitchBranchAsync(string branch)
    {
        if (string.IsNullOrEmpty(_repoRoot)) return;
        try
        {
            StatusReported?.Invoke(LangService.TFormat("正在切换到分支：{0}", branch));
            var (ok, message) = await Task.Run(() => GitService.Checkout(_repoRoot!, branch));
            if (ok)
            {
                _currentBranch = branch;
                StatusReported?.Invoke(LangService.TFormat("已切换到分支：{0}", branch));
            }
            else
            {
                StatusReported?.Invoke(LangService.TFormat("切换分支失败：{0}", message));
            }
        }
        catch (Exception ex)
        {
            StatusReported?.Invoke(LangService.TFormat("切换分支失败：{0}", ex.Message));
        }
        Refresh();
    }

    private static string ToDisplay(string relPath) => relPath.Replace('\\', '/');

    private static IBrush StatusBrush(string status) => status switch
    {
        "M" => new SolidColorBrush(Color.FromRgb(0xE5, 0xB8, 0x4B)), // 修改：黄
        "A" => new SolidColorBrush(Color.FromRgb(0x6F, 0xC8, 0x8A)), // 新增：绿
        "D" => new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x6A)), // 删除：红
        "R" => new SolidColorBrush(Color.FromRgb(0x7A, 0xB8, 0xE8)), // 重命名：蓝
        "U" => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), // 未跟踪：灰
        _ => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
    };

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ChangesList.SelectedItem is GitChangeItem item)
        {
            DiffRequested?.Invoke(item.FullPath);
        }
    }

    /// <summary>按当前主题刷新面板背景/前景/标题行背景（主题切换时由 FolderPanel 转发调用）。</summary>
    public void ApplyThemeColors()
    {
        // 内容区背景与左侧文件列表保持一致（EditorBackgroundBrush）
        var bg = ThemeBrush("EditorBackgroundBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
        var fg = ThemeBrush("EditorForegroundBrush", Color.FromRgb(0xE0, 0xE0, 0xE0));
        Background = bg;
        Foreground = fg;
        // 顶部标题行背景与面板色一致（覆盖 DynamicResource 兜底，确保切换主题时同步）
        HeaderBorder.Background = ThemeBrush("StatusBarBackgroundBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
        HeaderBorder.BorderBrush = ThemeBrush("PanelBorderBrush", Color.FromRgb(60, 60, 60));
        RefreshButton.Foreground = fg;
        CommitButton.Foreground = fg;
        PullButton.Foreground = fg;
        PushButton.Foreground = fg;
    }

    private static IBrush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current is { } app && app.RequestedThemeVariant is { } variant &&
            app.TryFindResource(key, variant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }
}
