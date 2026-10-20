using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using TextFlowPlus.Services.Localization;
using ShapesPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TextFlowPlus.Views;

/// <summary>
/// 左侧文件夹面板：
/// - 树形显示已打开文件夹内的目录与文件（懒加载：展开文件夹时才扫描其子项，适合大目录）
/// - 双击文件项触发 <see cref="FileOpenRequested"/>（由 MainWindow 在新标签页打开）
/// - 标题栏显示当前文件夹路径，并提供刷新按钮
/// - 隐藏 bin/obj/node_modules 等生成目录，并跳过隐藏/系统/符号链接（junction）目录防止循环
/// </summary>
public class FolderPanel : UserControl
{
    /// <summary>用户双击了某个文件（携带完整路径，MainWindow 负责在新标签页打开）</summary>
    public event Action<string>? FileOpenRequested;

    /// <summary>用户双击了某个远程文件（携带完整远程路径）</summary>
    public event Action<string>? RemoteFileOpenRequested;

    /// <summary>请求在远程目录下新建文件（携带目标目录完整路径，MainWindow 负责输入名称并执行）</summary>
    public event Action<string>? RemoteNewFileRequested;

    /// <summary>请求在远程目录下新建文件夹（携带目标目录完整路径）</summary>
    public event Action<string>? RemoteNewFolderRequested;

    /// <summary>请求删除远程文件/文件夹（携带完整远程路径与是否目录）</summary>
    public event Action<string, bool>? RemoteDeleteRequested;
    /// <summary>请求把远程文件/文件夹下载到本地（携带完整远程路径与是否目录）</summary>
    public event Action<string, bool>? RemoteDownloadRequested;

    /// <summary>请求在当前文件夹下新建本地文件（携带目标目录完整路径）</summary>
    public event Action<string>? LocalNewFileRequested;

    /// <summary>请求在当前文件夹下新建本地子文件夹（携带目标目录完整路径）</summary>
    public event Action<string>? LocalNewFolderRequested;

    /// <summary>请求删除本地文件/文件夹（携带完整路径与是否目录）</summary>
    public event Action<string, bool>? LocalDeleteRequested;

    /// <summary>请求重命名本地文件/文件夹（携带完整路径与是否目录）</summary>
    public event Action<string, bool>? LocalRenameRequested;

    /// <summary>操作结果反馈（状态栏提示；MainWindow 注入时可省略）</summary>
    public Action<string>? StatusReported { get; set; }

    /// <summary>当前打开的文件夹路径（未打开时为 null）</summary>
    public string? CurrentFolder => _currentFolder;

    /// <summary>当前浏览的远程根路径（未打开远程时为 null）</summary>
    public string? RemoteRoot => _remoteRoot;

    /// <summary>是否处于远程 SFTP 浏览模式</summary>
    public bool IsRemote => _remoteRoot != null;

    /// <summary>是否已打开任何文件夹（本地或远程，决定左侧列和分隔条是否显示）</summary>
    public bool HasRoot => _currentFolder != null || _remoteRoot != null;

    /// <summary>远程目录子项提供者（由 MainWindow 注入）：返回（目录全路径列表, 文件全路径列表）。</summary>
    public Func<string, Task<(List<string> Directories, List<string> Files)>>? RemoteListChildrenAsync { get; set; }

    /// <summary>忽略的目录名（大小写不敏感，隐藏/符号链接目录也一律跳过）</summary>
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode", ".gradle", ".cache", "__pycache__",
        "node_modules", "bin", "obj"
    };

    /// <summary>目录名是否应被排除（本地/远程共用）：生成目录或点开头的隐藏目录。</summary>
    public static bool IsExcludedFolderName(string name) =>
        ExcludedFolderNames.Contains(name) || (name.Length > 0 && name[0] == '.');

    private readonly TreeView _treeView = new();
    private readonly TextBlock _pathText = new();
    private readonly GitChangesPanel _gitPanel = new();
    private readonly TabControl _tabControl = new();
    private TabItem? _fileTab;
    private TabItem? _gitTab;
    private string? _currentFolder;
    private string? _remoteRoot;
    /// <summary>树重建代数：异步远程加载完成后若代数不一致则丢弃结果（树已被刷新）。</summary>
    private int _remoteGeneration;

    /// <summary>新建/删除/重命名工具栏按钮（随选择状态启用或禁用）。</summary>
    private readonly Button _newFileButton = new();
    private readonly Button _newFolderButton = new();
    private readonly Button _renameButton = new();
    private readonly Button _deleteButton = new();

    // 需要随主题刷新的面板元素（背景/边框/前景色由 ApplyThemeColors 统一设置）
    private readonly Border _titleBar = new();
    private readonly Border _actionBar = new();
    private readonly Button _refreshButton = new();
    private readonly ShapesPath _refreshIcon = new();
    // ========== 选中项状态 ==========

    /// <summary>当前选中的节点数据（未选中时为 null）。</summary>
    private FolderTreeItem? SelectedItem =>
        (_treeView.SelectedItem as TreeViewItem)?.Tag as FolderTreeItem;

    /// <summary>当前是否有选中项（决定重命名/删除按钮是否可用）。</summary>
    private bool HasSelection => SelectedItem != null;

    /// <summary>选中项的完整路径（未选中返回 null）。</summary>
    public string? SelectedPath => SelectedItem?.FullPath;

    /// <summary>
    /// 新建操作的落点目录：
    /// - 选中文件夹 → 该文件夹本身
    /// - 选中文件 → 该文件所在目录
    /// - 未选中 → 根目录（本地为当前文件夹，远程为远程根）
    /// </summary>
    public string? TargetFolderPath
    {
        get
        {
            var sel = SelectedItem;
            if (sel != null)
            {
                if (sel.IsFolder) return sel.FullPath;
                var parent = IsRemote ? RemoteParentPath(sel.FullPath) : Path.GetDirectoryName(sel.FullPath);
                if (!string.IsNullOrEmpty(parent)) return parent;
            }
            return IsRemote ? _remoteRoot : _currentFolder;
        }
    }

    /// <summary>取远程路径的父目录（保证不返回空串；根目录返回自身）。</summary>
    private static string RemoteParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed.Substring(0, idx);
    }

    /// <summary>设置状态栏文本（未注入时静默忽略）。</summary>
    private void Report(string message) => StatusReported?.Invoke(message);

    /// <summary>重建树并尽力恢复原先选中的路径（刷新后选中项不会丢失）。</summary>
    public void RefreshAndSelect(string? pathToSelect)
    {
        Refresh();
        if (string.IsNullOrEmpty(pathToSelect)) return;
        // 树重建后节点需重新产生：延迟到布局完成后再查找并选中
        Dispatcher.UIThread.Post(() =>
        {
            var node = FindNode(_treeView.Items, pathToSelect!);
            if (node != null)
            {
                node.IsSelected = true;
                node.BringIntoView();
            }
            UpdateButtonStates();
        }, DispatcherPriority.Background);
    }

    /// <summary>重新启用/禁用工具栏按钮（依据当前选择与模式）。</summary>
    private void UpdateButtonStates()
    {
        bool has = HasSelection;
        // 新建始终可用（未选中时落到根目录）
        _newFileButton.IsEnabled = HasRoot;
        _newFolderButton.IsEnabled = HasRoot;
        // 重命名/删除需要选中项；远程模式暂不支持重命名
        _renameButton.IsEnabled = has && !IsRemote;
        _deleteButton.IsEnabled = has;
    }

    public FolderPanel()
    {
        BuildUi();
        // Git 面板的提交/拉取/推送结果反馈转发到状态栏
        _gitPanel.StatusReported += Report;
    }

    /// <summary>打开文件夹：重建树（根节点展开，第一层子项立即加载）。</summary>
    public void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        _currentFolder = path;
        _remoteRoot = null;
        _pathText.Text = path;
        BuildTree();
        // 检测是否为 Git 仓库：是则启用“Git 修改”标签并刷新修改列表
        string? repoRoot = null;
        try
        {
            repoRoot = Services.Git.GitService.FindRepositoryRoot(path);
        }
        catch { /* 检测失败按非仓库处理 */ }
        _gitPanel.SetRepository(repoRoot);
        SetGitTabVisible(repoRoot != null);
    }

    /// <summary>通过增删 TabItem 控制“Git 修改”标签显示（避免 IsVisible 在 TabControl 中不生效）。</summary>
    private void SetGitTabVisible(bool visible)
    {
        if (_gitTab == null) return;
        bool contains = _tabControl.Items.Contains(_gitTab);
        if (visible && !contains) _tabControl.Items.Add(_gitTab);
        else if (!visible && contains) _tabControl.Items.Remove(_gitTab);
    }

    /// <summary>打开远程文件夹：根路径为远程目录（一般为登录主目录），display 为标题栏显示文本。</summary>
    public void OpenRemoteFolder(string rootPath, string display)
    {
        if (string.IsNullOrEmpty(rootPath)) return;
        _remoteRoot = rootPath;
        _currentFolder = null;
        _pathText.Text = display;
        BuildTree();
        _gitPanel.SetRepository(null, isRemote: true);
        SetGitTabVisible(false);
    }

    /// <summary>重新扫描当前文件夹（刷新按钮 / 右键刷新可调用；远程模式下重建根目录）。</summary>
    public void Refresh()
    {
        if (!HasRoot) return;
        BuildTree();
        _gitPanel.Refresh();
    }

    /// <summary>手动刷新 Git 修改列表（编辑保存后由主窗口调用）。</summary>
    public void RefreshGitChanges() => _gitPanel.Refresh();

    /// <summary>Git 修改面板（主窗口在此挂双击对比事件）。</summary>
    public GitChangesPanel GitPanel => _gitPanel;

    // ========== UI 构建 ==========

    private void BuildUi()
    {
        var root = new DockPanel();

        // 标题栏：文件夹图标 + 路径 + 刷新按钮
        _titleBar.Padding = new Thickness(6, 4);
        _titleBar.BorderThickness = new Thickness(0, 0, 0, 1);
        DockPanel.SetDock(_titleBar, Dock.Top);
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var titleText = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.Children.Add(new TextBlock
        {
            Text = "\U0001F4C2",   // 📂（彩色：打开的文件夹）
            FontSize = 14,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        _pathText.Text = LangService.T("未打开文件夹");
        _pathText.FontSize = 12.5;
        _pathText.VerticalAlignment = VerticalAlignment.Center;
        _pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.Children.Add(_pathText);

        // 刷新按钮：矢量循环箭头图标（与 Git 修改面板图标按钮同尺寸 26×22 / 图标 14×14）
        _refreshButton.Width = 26;
        _refreshButton.Height = 22;
        _refreshButton.Padding = new Thickness(0);
        _refreshButton.Margin = new Thickness(6, 0, 0, 0);
        _refreshButton.Background = Brushes.Transparent;
        _refreshButton.BorderThickness = new Thickness(0);
        _refreshButton.HorizontalAlignment = HorizontalAlignment.Right;
        _refreshButton.VerticalAlignment = VerticalAlignment.Center;
        _refreshButton.Click += (_, _) => RefreshAndSelect(SelectedPath);
        ToolTip.SetTip(_refreshButton, "刷新文件夹列表");
        _refreshButton.Tag = "刷新文件夹列表";
        _refreshIcon.Data = StreamGeometry.Parse("M 8,2.8 A 5.2,5.2 0 1 1 9.8,3.4 M 9.8,3.4 L 12.6,3.0 M 9.8,3.4 L 11.0,5.9");
        _refreshIcon.StrokeThickness = 1.8;
        _refreshIcon.StrokeLineCap = PenLineCap.Round;
        _refreshIcon.Width = 14;
        _refreshIcon.Height = 14;
        _refreshIcon.Stretch = Stretch.Uniform;
        _refreshIcon.VerticalAlignment = VerticalAlignment.Center;
        _refreshIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _refreshButton.Content = _refreshIcon;

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(_refreshButton, 1);
        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(_refreshButton);
        _titleBar.Child = titleGrid;

        // 操作工具栏：新建文件 / 新建文件夹 / 重命名 / 删除（作用于当前选中项或其所在目录）
        _actionBar.Padding = new Thickness(4, 3);
        _actionBar.BorderThickness = new Thickness(0, 0, 0, 1);
        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };
        actionPanel.Children.Add(CreateActionButton(_newFileButton, "📄+", "新建文件（在当前选中文件夹下创建，未选中时创建在根目录）", OnNewFileClicked));
        actionPanel.Children.Add(CreateActionButton(_newFolderButton, "📁+", "新建文件夹（在当前选中文件夹下创建子文件夹）", OnNewFolderClicked));
        actionPanel.Children.Add(CreateActionButton(_renameButton, "✏", "重命名选中项（F2）", OnRenameClicked));
        actionPanel.Children.Add(CreateActionButton(_deleteButton, "🗑", "删除选中项（Delete）", OnDeleteClicked));
        _actionBar.Child = actionPanel;
        DockPanel.SetDock(_actionBar, Dock.Top);

        // 文件树（支持多选关闭，保证删除/重命名目标唯一）
        _treeView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _treeView.VerticalAlignment = VerticalAlignment.Stretch;
        _treeView.SelectionMode = SelectionMode.Single;
        _treeView.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble);
        _treeView.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        _treeView.SelectionChanged += (_, _) => UpdateButtonStates();
        // F2 重命名 / Delete 删除（焦点在树内时生效）
        _treeView.KeyDown += OnTreeKeyDown;
        // 空白处右键：针对根目录的新建/刷新
        _treeView.ContextRequested += OnTreeContextRequested;

        // 标签容器：文件树 / Git 修改（Git 修改标签仅在打开 Git 仓库时通过增删显示）。
        // VS2022 风格：标签栏在左侧面板底部。
        _tabControl.HorizontalAlignment = HorizontalAlignment.Stretch;
        _tabControl.VerticalAlignment = VerticalAlignment.Stretch;
        // 标签栏在文件列表框上方
        _tabControl.TabStripPlacement = Dock.Top;
        _tabControl.SelectionChanged += (_, _) => UpdateTabSelectionVisual();
        _tabControl.Items.Add(_fileTab = new TabItem { Header = "文件", Content = _treeView });
        _gitTab = new TabItem { Header = "Git", Content = _gitPanel };

        root.Children.Add(_titleBar);
        root.Children.Add(_actionBar);
        root.Children.Add(_tabControl);
        Content = root;
        ApplyThemeColors();
        UpdateButtonStates();
        UpdateTabSelectionVisual();
        LangService.LanguageChanged += ApplyTexts;
        ApplyTexts();
    }

    /// <summary>按当前界面语言刷新面板内固定文本（标签标题/按钮提示/未打开提示）。</summary>
    public void ApplyTexts()
    {
        if (_fileTab != null) _fileTab.Header = LangService.T("文件");
        if (_gitTab != null) _gitTab.Header = LangService.T("Git");
        if (_refreshButton.Tag is string rt) ToolTip.SetTip(_refreshButton, LangService.T(rt));
        if (_actionBar.Child is StackPanel sp)
        {
            foreach (var child in sp.Children)
            {
                if (child is Button b && b.Tag is string s) ToolTip.SetTip(b, LangService.T(s));
            }
        }
        if (string.IsNullOrEmpty(_pathText.Text) || _pathText.Text == "未打开文件夹")
        {
            _pathText.Text = LangService.T("未打开文件夹");
        }
    }
    /// <summary>
    /// 标签选中视觉（立体感）：选中标签用编辑区背景色与内容区连成一体（视觉凸起），
    /// 未选中透明显示面板底色（视觉凹陷）；字体放大便于阅读。
    /// </summary>
    private void UpdateTabSelectionVisual()
    {
        var editorBg = ThemeBrush("EditorBackgroundBrush");
        var editorFg = ThemeBrush("EditorForegroundBrush");
        var idleFg = ThemeBrush("TextSecondaryBrush");
        foreach (TabItem item in _tabControl.Items)
        {
            bool selected = ReferenceEquals(_tabControl.SelectedItem, item);
            item.FontSize = 14;
            item.Padding = new Thickness(18, 8);
            item.Background = selected ? editorBg : Brushes.Transparent;
            item.Foreground = selected ? editorFg : idleFg;
            item.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    /// <summary>创建工具栏小按钮并绑定点击处理。</summary>
    private static Button CreateActionButton(Button button, string glyph, string tooltip, Action onClick)
    {
        button.Content = glyph;
        button.Width = 30;
        button.Height = 23;
        button.Padding = new Thickness(0);
        button.FontSize = 12;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
                button.Tag = tooltip; // 存中文原文，切换语言时刷新
        ToolTip.SetTip(button, LangService.T(tooltip));
        button.Click += (_, _) => onClick();
        return button;
    }

    // ========== 工具栏动作 ==========

    /// <summary>新建文件：目标目录取 TargetFolderPath；远程模式走远程事件，本地走本地事件。</summary>
    private void OnNewFileClicked()
    {
        var target = TargetFolderPath;
        if (string.IsNullOrEmpty(target))
        {
            Report(LangService.T("请先打开一个文件夹"));
            return;
        }
        if (IsRemote) RemoteNewFileRequested?.Invoke(target);
        else LocalNewFileRequested?.Invoke(target);
    }

    /// <summary>新建文件夹：目标目录取 TargetFolderPath。</summary>
    private void OnNewFolderClicked()
    {
        var target = TargetFolderPath;
        if (string.IsNullOrEmpty(target))
        {
            Report(LangService.T("请先打开一个文件夹"));
            return;
        }
        if (IsRemote) RemoteNewFolderRequested?.Invoke(target);
        else LocalNewFolderRequested?.Invoke(target);
    }

    /// <summary>重命名：仅本地模式支持（远程 SFTP 重命名未实现）。</summary>
    private void OnRenameClicked()
    {
        var sel = SelectedItem;
        if (sel == null)
        {
            Report(LangService.T("请先选择要重命名的文件或文件夹"));
            return;
        }
        if (IsRemote)
        {
            Report(LangService.T("远程模式暂不支持重命名"));
            return;
        }
        LocalRenameRequested?.Invoke(sel.FullPath, sel.IsFolder);
    }

    /// <summary>删除：本地/远程分别触发对应事件（删除前由 MainWindow 弹确认框）。</summary>
    private void OnDeleteClicked()
    {
        var sel = SelectedItem;
        if (sel == null)
        {
            Report(LangService.T("请先选择要删除的文件或文件夹"));
            return;
        }
        if (IsRemote) RemoteDeleteRequested?.Invoke(sel.FullPath, sel.IsFolder);
        else LocalDeleteRequested?.Invoke(sel.FullPath, sel.IsFolder);
    }

    /// <summary>树内按键：F2 重命名，Delete 删除。</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            OnRenameClicked();
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            OnDeleteClicked();
        }
    }

    /// <summary>树上右键：节点自带菜单（节点上右键不重复处理），空白处提供根目录新建/刷新。</summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // 命中节点时由该节点的 ContextFlyout 处理，这里不覆盖
        if (FindAncestor<TreeViewItem>(e.Source as Control)?.Tag is FolderTreeItem) return;

        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("📄 新建文件"), OnNewFileClicked));
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("📁 新建文件夹"), OnNewFolderClicked));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("🔄 刷新"), () => RefreshAndSelect(SelectedPath)));
        flyout.ShowAt(_treeView);
        e.Handled = true;
    }

    // ========== 右键菜单 ==========

    /// <summary>本地文件夹节点右键菜单：新建文件/新建文件夹/重命名/删除/刷新。</summary>
    private MenuFlyout CreateLocalFolderFlyout(FolderTreeItem data)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("📄 新建文件"), () => LocalNewFileRequested?.Invoke(data.FullPath)));
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("📁 新建文件夹"), () => LocalNewFolderRequested?.Invoke(data.FullPath)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("✏ 重命名"), () => LocalRenameRequested?.Invoke(data.FullPath, true)));
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("🗑 删除文件夹"), () => LocalDeleteRequested?.Invoke(data.FullPath, true)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("🔄 刷新"), () => RefreshAndSelect(data.FullPath)));
        return flyout;
    }

    /// <summary>本地文件节点右键菜单：重命名/删除。</summary>
    private MenuFlyout CreateLocalFileFlyout(FolderTreeItem data)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("✏ 重命名"), () => LocalRenameRequested?.Invoke(data.FullPath, false)));
        flyout.Items.Add(CreateLocalMenuItem(LangService.T("🗑 删除文件"), () => LocalDeleteRequested?.Invoke(data.FullPath, false)));
        return flyout;
    }

    private static MenuItem CreateLocalMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header, FontSize = 12 };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>按完整路径在树中查找节点（本地/远程通用）。</summary>
    private static TreeViewItem? FindNode(IList items, string path)
    {
        foreach (var obj in items)
        {
            if (obj is not TreeViewItem item) continue;
            if (item.Tag is FolderTreeItem { FullPath: var fp } && string.Equals(fp, path, StringComparison.OrdinalIgnoreCase))
                return item;
            var found = FindNode(item.Items, path);
            if (found != null) return found;
        }
        return null;
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

    /// <summary>按当前主题刷新面板配色：面板与文件树背景跟随编辑区（EditorBackgroundBrush），
    /// 标题栏/工具栏用面板语义色。主题切换时由主窗口调用。</summary>
    public void ApplyThemeColors()
    {
        Background = ThemeBrush("EditorBackgroundBrush");
        _treeView.Background = ThemeBrush("EditorBackgroundBrush");
        _titleBar.Background = ThemeBrush("PanelBackgroundBrush");
        _titleBar.BorderBrush = ThemeBrush("PanelBorderBrush");
        _actionBar.Background = ThemeBrush("PanelBackgroundBrush");
        _actionBar.BorderBrush = ThemeBrush("PanelBorderBrush");
        _refreshButton.Foreground = ThemeBrush("TextSecondaryBrush");
        _refreshIcon.Stroke = _refreshButton.Foreground;
        // 标签栏背景与面板色一致；工具栏按钮边框随主题
        _tabControl.Background = ThemeBrush("PanelBackgroundBrush");
        if (_actionBar.Child is StackPanel actionPanel)
        {
            var borderBrush = ThemeBrush("PanelBorderBrush");
            foreach (var child in actionPanel.Children)
            {
                if (child is Button b) b.BorderBrush = borderBrush;
            }
        }
        UpdateTabSelectionVisual();
        // Git 修改面板背景/标题行背景随主题同步
        _gitPanel.ApplyThemeColors();
    }

    // ========== 树构建（懒加载） ==========

    private void BuildTree()
    {
        _remoteGeneration++;
        _treeView.Items.Clear();

        // 远程模式：根目录异步加载
        if (IsRemote)
        {
            if (string.IsNullOrEmpty(_remoteRoot)) return;

            var rootData = new FolderTreeItem(_remoteRoot, isFolder: true, name: _remoteRoot);
            var rootNode = new TreeViewItem
            {
                Header = CreateHeader(rootData),
                Tag = rootData,
                IsExpanded = true
            };
            _treeView.Items.Add(rootNode);
            // 根节点保持展开
            Dispatcher.UIThread.Post(() => rootNode.IsExpanded = true);
            // 根目录本身也是远程文件夹：挂右键菜单
            if (RemoteListChildrenAsync != null)
            {
                rootNode.ContextFlyout = CreateRemoteFolderFlyout(rootData, rootNode);
            }

            var generation = _remoteGeneration;
            _ = LoadRemoteChildrenAsync(rootNode, rootData, generation);
            return;
        }

        if (string.IsNullOrEmpty(_currentFolder) || !Directory.Exists(_currentFolder)) return;

        var localRootData = new FolderTreeItem(_currentFolder, isFolder: true, name: _currentFolder);
        var localRootNode = new TreeViewItem
        {
            Header = CreateHeader(localRootData),
            Tag = localRootData,
            IsExpanded = true
        };
        LoadLocalChildren(localRootNode, localRootData);
        _treeView.Items.Add(localRootNode);
        // 根节点保持展开
        Dispatcher.UIThread.Post(() => localRootNode.IsExpanded = true);
    }

    /// <summary>展开事件：首次展开时加载子项（本地同步，远程异步懒加载）。</summary>
    private void OnNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem node && node.Tag is FolderTreeItem { IsFolder: true } data
            && !data.ChildrenLoaded)
        {
            if (IsRemote)
            {
                var generation = _remoteGeneration;
                _ = LoadRemoteChildrenAsync(node, data, generation);
            }
            else
            {
                LoadLocalChildren(node, data);
            }
        }
    }

    /// <summary>把 data 的本地子项扫描结果填入 node（文件夹为空时显示“（空）”，文件夹项保留惰性占位）。</summary>
    private void LoadLocalChildren(TreeViewItem node, FolderTreeItem data)
    {
        data.ChildrenLoaded = true;
        node.Items.Clear();
        foreach (var child in ScanDirectory(data.FullPath))
        {
            node.Items.Add(CreateNode(child));
        }
        if (node.Items.Count == 0)
        {
            node.Items.Add(CreateHintNode(LangService.T("（空）")));
        }
    }

    /// <summary>异步加载远程子项（懒加载）：先显示“加载中…”，完成后填入目录/文件；树被刷新则丢弃结果。</summary>
    private async Task LoadRemoteChildrenAsync(TreeViewItem node, FolderTreeItem data, int generation)
    {
        data.ChildrenLoaded = true;
        node.Items.Clear();
        node.Items.Add(CreateHintNode(LangService.T("加载中…")));

        var lister = RemoteListChildrenAsync;
        if (lister == null)
        {
            if (generation != _remoteGeneration) return;
            node.Items.Clear();
            node.Items.Add(CreateHintNode("（未配置远程列表）"));
            return;
        }

        try
        {
            var (dirs, files) = await lister(data.FullPath);
            if (generation != _remoteGeneration) return;   // 树已重建，丢弃过期结果

            dirs = dirs.OrderBy(name => Path.GetFileName(name) ?? name, StringComparer.OrdinalIgnoreCase).ToList();
            files = files.OrderBy(name => Path.GetFileName(name) ?? name, StringComparer.OrdinalIgnoreCase).ToList();

            node.Items.Clear();
            foreach (var dir in dirs)
            {
                if (IsExcludedFolderName(Path.GetFileName(dir) ?? dir)) continue;
                node.Items.Add(CreateNode(new FolderTreeItem(dir, isFolder: true)));
            }
            foreach (var file in files)
            {
                node.Items.Add(CreateNode(new FolderTreeItem(file, isFolder: false)));
            }
            if (node.Items.Count == 0)
            {
                node.Items.Add(CreateHintNode(LangService.T("（空）")));
            }
        }
        catch (Exception ex)
        {
            if (generation != _remoteGeneration) return;
            node.Items.Clear();
            node.Items.Add(CreateHintNode($"（加载失败：{ex.Message}）"));
        }
    }

    private static TreeViewItem CreateHintNode(string text) => new()
    {
        Header = new TextBlock { Text = text, Foreground = Brushes.Gray, FontSize = 12.5 },
        IsEnabled = false,
        Tag = null
    };

    private TreeViewItem CreateNode(FolderTreeItem data)
    {
        var node = new TreeViewItem
        {
            Header = CreateHeader(data),
            Tag = data
        };
        if (IsRemote)
        {
            // 远程模式右键菜单：文件夹 → 新建文件/新建文件夹/删除/刷新，文件 → 删除
            node.ContextFlyout = data.IsFolder
                ? CreateRemoteFolderFlyout(data, node)
                : CreateRemoteFileFlyout(data);
        }
        else
        {
            // 本地模式右键菜单：文件夹 → 新建/重命名/删除/刷新，文件 → 重命名/删除
            node.ContextFlyout = data.IsFolder
                ? CreateLocalFolderFlyout(data)
                : CreateLocalFileFlyout(data);
        }
        if (data.IsFolder)
        {
            // 惰性占位：保证展开箭头存在，首次展开时由 OnNodeExpanded 替换为真实子项
            node.Items.Add(CreateHintNode(LangService.T("加载中…")));
        }
        return node;
    }

    // ========== 远程右键菜单 ==========

    private MenuFlyout CreateRemoteFolderFlyout(FolderTreeItem data, TreeViewItem node)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("📄 新建文件"), () => RemoteNewFileRequested?.Invoke(data.FullPath)));
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("📁 新建文件夹"), () => RemoteNewFolderRequested?.Invoke(data.FullPath)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("🗑 删除文件夹"), () => RemoteDeleteRequested?.Invoke(data.FullPath, true)));
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("⬇ 下载文件夹"), () => RemoteDownloadRequested?.Invoke(data.FullPath, true)));
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("🔄 刷新"), () => ReloadRemoteNode(node)));
        return flyout;
    }

    private MenuFlyout CreateRemoteFileFlyout(FolderTreeItem data)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("⬇ 下载文件"), () => RemoteDownloadRequested?.Invoke(data.FullPath, false)));
        flyout.Items.Add(CreateRemoteMenuItem(LangService.T("🗑 删除文件"), () => RemoteDeleteRequested?.Invoke(data.FullPath, false)));
        return flyout;
    }

    private static MenuItem CreateRemoteMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header, FontSize = 12 };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>强制重载某个远程文件夹节点的子项（新建/删除后刷新显示）。</summary>
    private void ReloadRemoteNode(TreeViewItem node)
    {
        if (node.Tag is FolderTreeItem { IsFolder: true } data)
        {
            data.ChildrenLoaded = false;
            _ = LoadRemoteChildrenAsync(node, data, _remoteGeneration);
        }
    }

    /// <summary>按完整路径重载远程文件夹节点（MainWindow 在新建/删除后调用；节点未加载则跳过）。</summary>
    public void RefreshRemoteFolder(string folderPath)
    {
        if (!IsRemote) return;
        var node = FindRemoteNode(_treeView.Items, folderPath);
        if (node != null) ReloadRemoteNode(node);
    }

    private static TreeViewItem? FindRemoteNode(IList items, string path)
    {
        foreach (var obj in items)
        {
            if (obj is not TreeViewItem item) continue;
            if (item.Tag is FolderTreeItem { FullPath: var fp } && fp == path) return item;
            var found = FindRemoteNode(item.Items, path);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>节点标题：文件夹图标 + 名称。</summary>
    private Control CreateHeader(FolderTreeItem data)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };
        panel.Children.Add(new TextBlock
        {
            Text = data.IsFolder ? "\U0001F4C1" : "📄",   // 📁（彩色文件夹）/ 📄
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        // 文件/文件夹名不设置前景色：继承树默认前景，随亮/暗主题自动切换
        panel.Children.Add(new TextBlock
        {
            Text = data.Name,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    /// <summary>双击：本地文件在 MainWindow 打开新标签页；远程文件触发 RemoteFileOpenRequested；
    /// 文件夹双击由 TreeViewItem 处理展开/折叠。</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Control;
        var item = FindAncestor<TreeViewItem>(source);
        if (item?.Tag is FolderTreeItem { IsFolder: false } file)
        {
            if (IsRemote)
            {
                RemoteFileOpenRequested?.Invoke(file.FullPath);
            }
            else
            {
                FileOpenRequested?.Invoke(file.FullPath);
            }
        }
    }

    private static T? FindAncestor<T>(Control? start) where T : Control
    {
        while (start != null)
        {
            if (start is T t) return t;
            start = start.Parent as Control;
        }
        return null;
    }

    // ========== 目录扫描 ==========

    private static IEnumerable<FolderTreeItem> ScanDirectory(string path)
    {
        var items = new List<FolderTreeItem>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                if (IsExcludedDirectory(dir)) continue;
                items.Add(new FolderTreeItem(dir, isFolder: true));
            }
            foreach (var file in Directory.EnumerateFiles(path))
            {
                items.Add(new FolderTreeItem(file, isFolder: false));
            }
        }
        catch (Exception)
        {
            // 无权限等：跳过，返回已收集到的项
        }

        return items
            .OrderByDescending(i => i.IsFolder)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcludedDirectory(string dirPath)
    {
        try
        {
            var info = new DirectoryInfo(dirPath);
            if (IsExcludedFolderName(info.Name)) return true;
            var attrs = info.Attributes;
            if ((attrs & FileAttributes.Hidden) != 0) return true;
            if ((attrs & FileAttributes.System) != 0) return true;
            if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ========== 内部数据模型 ==========

    /// <summary>文件树节点数据：完整路径 + 是否目录 + 子项（懒加载缓存）。</summary>
    private sealed class FolderTreeItem
    {
        public FolderTreeItem(string fullPath, bool isFolder, string? name = null)
        {
            FullPath = fullPath;
            IsFolder = isFolder;
            Name = name ?? Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(Name)) Name = fullPath;
        }

        public string FullPath { get; }
        public string Name { get; }
        public bool IsFolder { get; }
        public bool ChildrenLoaded { get; set; }
    }
}
