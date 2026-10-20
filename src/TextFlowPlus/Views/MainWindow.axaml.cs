using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Search;
using AvaloniaEdit.Rendering;
using Newtonsoft.Json;
using TextFlowPlus.Models;
using TextFlowPlus.Services.Localization;
using TextFlowPlus.Services;
using TextFlowPlus.Services.Ssh;
using Microsoft.Extensions.AI;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Documents.TextModel;
using Avalonia.Controls.Documents.Serialization.Snapshot;
using Avalonia.Controls.Documents.Primitives.Actions;
// 与 AvaloniaEdit.Document.LogicalDirection 重名的富文本方向枚举，用别名消歧
using RichLogicalDirection = Avalonia.Controls.Documents.TextModel.LogicalDirection;

namespace TextFlowPlus.Views;

public partial class MainWindow : Window
{
    private List<EditorTab> _tabs = new();
    private double currentZoom = 1.0;
    private const int MaxRecentFiles = 10;

    // 防止 macOS 原生对话框崩溃: 用字段持有 Task 引用，避免 async 状态机被 GC 回收
    private Task? _fileDialogTask;
    private Task? _saveDialogTask;
    private Task? _pendingUiTask;
    private List<string> _recentFiles = new();

    // 查找/替换对话框引用（复用单例，避免 Ctrl+F/Ctrl+H 重复弹出；富文本 F3 需要触达）
    private FindDialog? _findDialog;
    private ReplaceDialog? _replaceDialog;

    // 标记程序正在内部保存文件，此时 FileSystemWatcher 触发的变化不应提示
    private static readonly HashSet<string> _internalSavingFiles = new(StringComparer.OrdinalIgnoreCase);

    // 记录最后一次内部保存的时间，用于忽略刚保存后 FileSystemWatcher 触发的事件
    private static readonly Dictionary<string, DateTime> _lastInternalSaveTimes = new(StringComparer.OrdinalIgnoreCase);

    // 日志监控相关字段
    private DispatcherTimer? _logMonitorTimer;
    private bool _isLogMonitoring;
    private string? _logMonitorFilePath;
    private long _lastFileLength;
    /// <summary>日志监控文档最大字符数：超出时丢弃前半部分旧内容，防止内存无限增长导致卡死。</summary>
    private const int LogMonitorMaxChars = 500_000;

#if VOICE_INPUT
    // 语音输入（AI 助手标题栏麦克风按钮：识别文本填入命令输入框，输入结束自动发送）相关
    private TextFlowPlus.Services.Voice.VoiceRecorder? _voiceRecorder;
    private bool _isVoiceInput;
#endif

    // ========== AI 助手停靠面板 ==========
    private AgentPanel _agentPanel = null!;
    /// <summary>agent 面板默认宽度（可通过垂直分隔条拖动调整）</summary>
    private const double AgentPanelWidth = 360;
    /// <summary>AI 助手面板当前宽度（可拖动分隔条调整，200~800；拖动结束保存，重建布局时沿用）。</summary>
    private double _agentPanelWidth = AgentPanelWidth;
    /// <summary>AI 助手面板最小/最大宽度。</summary>
    private const double AgentPanelMinWidth = 360;
    private const double AgentPanelMaxWidth = 800;

    // ========== 左侧文件夹面板 ==========
    private FolderPanel _folderPanel = null!;
    private bool _folderPanelVisible;
    /// <summary>文件夹面板默认宽度（可通过分隔条拖动调整）</summary>
    private const double FolderPanelWidth = 360;
    /// <summary>分隔条宽度（容纳圆形三角按钮；左右两侧面板共用，面板隐藏时仍保留该窄条）</summary>
    private const double SplitStripWidth = 20;
    // 防止 macOS 原生对话框崩溃: 持有 Task 引用
    private Task? _folderDialogTask;

    // ========== 远程连接（SFTP 远程文件夹浏览） ==========
    private SftpService? _remoteSftp;
    private Task? _remoteDialogTask;
    /// <summary>当前远程会话标识 user@host:port（用于远程标签去重与缓存目录）</summary>
    private string? _remoteSessionKey;
    /// <summary>防止对话框/远程操作 Task 被 GC（macOS）</summary>
    private Task? _remoteOpTask;

    /// <summary>防止本地文件夹操作（新建/重命名/删除）Task 被 GC（macOS 原生对话框回调）</summary>
    private Task? _localFileOpTask;

    private bool _agentPanelVisible = true;

    private static readonly IBrush[] TabColors = new IBrush[]
    {
        new SolidColorBrush(Color.FromRgb(255, 230, 230)), // 浅红
        new SolidColorBrush(Color.FromRgb(230, 255, 230)), // 浅绿
        new SolidColorBrush(Color.FromRgb(230, 230, 255)), // 浅蓝
        new SolidColorBrush(Color.FromRgb(255, 255, 230)), // 浅黄
        new SolidColorBrush(Color.FromRgb(255, 230, 255)), // 浅紫
        new SolidColorBrush(Color.FromRgb(230, 255, 255)), // 浅青
        new SolidColorBrush(Color.FromRgb(255, 240, 230)), // 浅橙
        new SolidColorBrush(Color.FromRgb(240, 230, 255)), // 浅紫罗兰
        new SolidColorBrush(Color.FromRgb(255, 235, 240)), // 浅粉
        new SolidColorBrush(Color.FromRgb(230, 245, 255)), // 浅天蓝
        new SolidColorBrush(Color.FromRgb(245, 255, 230)), // 浅柠檬
        new SolidColorBrush(Color.FromRgb(255, 245, 230)), // 浅桃红
        new SolidColorBrush(Color.FromRgb(235, 255, 240)), // 浅薄荷
        new SolidColorBrush(Color.FromRgb(250, 230, 240)), // 浅玫瑰
        new SolidColorBrush(Color.FromRgb(230, 240, 255)), // 浅灰蓝
        new SolidColorBrush(Color.FromRgb(240, 255, 235)), // 浅苹果
    };

    private int _colorIndex = 0;

    // ========== 主题（亮色 / 暗色） ==========

    private bool _isDark;

    /// <summary>所有分隔条引用（主题切换时刷新配色）</summary>
    private readonly List<Control> _splitStrips = new();



    /// <summary>标签页颜色：暗色主题下的深色变体（与亮色 16 色一一对应）</summary>
    private static readonly IBrush[] DarkTabColors = new IBrush[]
    {
        new SolidColorBrush(Color.FromRgb(72, 52, 52)),
        new SolidColorBrush(Color.FromRgb(50, 70, 52)),
        new SolidColorBrush(Color.FromRgb(50, 56, 78)),
        new SolidColorBrush(Color.FromRgb(72, 70, 50)),
        new SolidColorBrush(Color.FromRgb(70, 52, 70)),
        new SolidColorBrush(Color.FromRgb(50, 70, 70)),
        new SolidColorBrush(Color.FromRgb(72, 62, 50)),
        new SolidColorBrush(Color.FromRgb(62, 52, 72)),
        new SolidColorBrush(Color.FromRgb(70, 52, 60)),
        new SolidColorBrush(Color.FromRgb(50, 62, 72)),
        new SolidColorBrush(Color.FromRgb(66, 72, 50)),
        new SolidColorBrush(Color.FromRgb(72, 66, 50)),
        new SolidColorBrush(Color.FromRgb(52, 72, 60)),
        new SolidColorBrush(Color.FromRgb(70, 56, 60)),
        new SolidColorBrush(Color.FromRgb(52, 60, 72)),
        new SolidColorBrush(Color.FromRgb(58, 72, 54)),
    };

    private static readonly string ThemePrefPath = Services.AppPaths.LocalAppDataFile("theme.txt");
    private static readonly string LangPrefPath = Services.AppPaths.LocalAppDataFile("lang.txt");

    // ========== 字体颜色面板 ==========

    /// <summary>字体颜色预设（名称 → 颜色），用于工具栏字体颜色面板</summary>
    private static readonly (string Name, Color Color)[] FontColorPalette =
    {
        ("黑色", Colors.Black), ("深灰", Color.FromRgb(0x33, 0x33, 0x33)),
        ("灰色", Colors.Gray), ("浅灰", Color.FromRgb(0xC0, 0xC0, 0xC0)),
        ("白色", Colors.White), ("红色", Colors.Red),
        ("深红", Color.FromRgb(0x8B, 0, 0)), ("橙色", Colors.Orange),
        ("棕色", Color.FromRgb(0xA5, 0x2A, 0x2A)), ("黄色", Colors.Yellow),
        ("绿色", Colors.Lime), ("深绿", Colors.Green),
        ("青色", Colors.Cyan), ("蓝色", Colors.Blue),
        ("深蓝", Color.FromRgb(0, 0, 0x8B)), ("紫色", Colors.Purple),
        ("粉色", Color.FromRgb(0xFF, 0xC0, 0xCB)), ("玫红", Colors.Magenta),
        ("青绿", Color.FromRgb(0, 0x80, 0x80)), ("橄榄", Color.FromRgb(0x80, 0x80, 0)),
    };

    public MainWindow()
    {
        InitializeComponent();
        ApplyThemeSilent(LoadThemePreference());
        LoadAllCustomHighlightings();
        InitDragDrop();
        FontFamilyComboBox.SelectedIndex = 0;
        FontSizeComboBox.SelectedIndex = 6; // 16
        InitFontColorPanel();
        NewFile();
        LoadRecentFiles();
        RefreshRecentFilesMenu();
        InitFolderPanel();
        InitAgentPanel();
        InitLanguageSupport();
        // Ctrl+滚轮缩放编辑器字体（鼠标悬停在当前编辑区时生效）
        AddHandler(PointerWheelChangedEvent, MainWindow_PointerWheelChanged, handledEventsToo: true);

        // 启动后异步检查新版本：网络异常/无需升级均静默，不影响主界面
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var result = await Services.UpdateChecker.CheckAsync().ConfigureAwait(true);
                if (result.HasUpdate && Services.UpdateChecker.IsNewerVersion(result.NewVersion))
                {
                    var dialog = new UpdateAvailableDialog(result.NewVersion);
                    await dialog.ShowDialog(this);
                }
            }
            catch
            {
                // 弹窗失败不影响使用
            }
        }, DispatcherPriority.Background);
    }
    /// <summary>菜单/工具栏控件的原始中文文本快照（用于切换语言时刷新）。</summary>
    private readonly Dictionary<object, string> _uiTextZh = new();

    private void InitLanguageSupport()
    {
        LoadLanguagePreference();
        SnapshotUiTexts();
        ApplyUiLanguage();
    }

    private static void LoadLanguagePreference()
    {
        try
        {
            if (!File.Exists(LangPrefPath)) return;
            var code = File.ReadAllText(LangPrefPath).Trim().ToLowerInvariant();
            // 兼容既有取值："en" / "zh"；新增 "ja"
            var lang = code switch
            {
                "en" => AppLanguage.English,
                "ja" => AppLanguage.Japanese,
                _ => AppLanguage.Chinese
            };
            if (lang != AppLanguage.Chinese) LangService.SetLanguage(lang);
        }
        catch
        {
            // 读取失败按中文处理
        }
    }

    private static void SaveLanguagePreference(string code)
    {
        try
        {
            var dir = Path.GetDirectoryName(LangPrefPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LangPrefPath, code);
        }
        catch
        {
            // 持久化失败不影响本次切换
        }
    }

    private void UiLanguage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var code = mi.Tag?.ToString() ?? "zh";
        var lang = code switch
        {
            "en" => AppLanguage.English,
            "ja" => AppLanguage.Japanese,
            _ => AppLanguage.Chinese
        };
        LangService.SetLanguage(lang);
        SaveLanguagePreference(code);
        ApplyUiLanguage();
    }

    /// <summary>快照菜单与工具栏中所有中文原文文本。</summary>
    private void SnapshotUiTexts()
    {
        _uiTextZh.Clear();
        foreach (var mi in EnumerateMenuItems(MainMenuControl))
        {
            if (mi.Header is string s && !string.IsNullOrEmpty(s) && !_uiTextZh.ContainsKey(mi))
            {
                _uiTextZh[mi] = s;
            }
        }
        foreach (var c in EnumerateVisualChildren<Avalonia.Controls.Control>(MainToolBar))
        {
            if (ToolTip.GetTip(c) is string tip && !string.IsNullOrEmpty(tip) && !_uiTextZh.ContainsKey(c))
            {
                _uiTextZh[c] = tip;
            }
        }
        // Status bar ToolTips (e.g. click-to-copy) are also language-refreshed
        foreach (var c in EnumerateVisualChildren<Avalonia.Controls.Control>(MainStatusBar))
        {
            if (ToolTip.GetTip(c) is string tip && !string.IsNullOrEmpty(tip) && !_uiTextZh.ContainsKey(c))
            {
                _uiTextZh[c] = tip;
            }
        }
    }

    /// <summary>按当前语言刷新所有已快照的菜单/工具栏文本。</summary>
    private void ApplyUiLanguage()
    {
        foreach (var kv in _uiTextZh)
        {
            switch (kv.Key)
            {
                case MenuItem mi:
                    mi.Header = LangService.T(kv.Value);
                    break;
                case Avalonia.Controls.Control c:
                    ToolTip.SetTip(c, LangService.T(kv.Value));
                    break;
            }
        }
        UiChineseMenuItem.IsChecked = LangService.Language == AppLanguage.Chinese;
        UiEnglishMenuItem.IsChecked = LangService.Language == AppLanguage.English;
        UiJapaneseMenuItem.IsChecked = LangService.Language == AppLanguage.Japanese;
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(Menu root)
    {
        foreach (var item in root.Items)
        {
            if (item is MenuItem mi)
            {
                yield return mi;
                foreach (var sub in EnumerateMenuItemChildren(mi)) yield return sub;
            }
        }
    }

    private static IEnumerable<MenuItem> EnumerateMenuItemChildren(MenuItem parent)
    {
        foreach (var item in parent.Items)
        {
            if (item is MenuItem mi)
            {
                yield return mi;
                foreach (var sub in EnumerateMenuItemChildren(mi)) yield return sub;
            }
        }
    }

    private IEnumerable<T> EnumerateVisualChildren<T>(Avalonia.Visual parent) where T : class
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is T t) yield return t;
            foreach (var deep in EnumerateVisualChildren<T>(child)) yield return deep;
        }
    }

    // ========== Editor Creation ==========
    private TextEditor CreateNewTextEditor()
    {
        var editor = new TextEditor
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16 * currentZoom,
            ShowLineNumbers = true,
            WordWrap = true,   // 自动折行（视图菜单可切换）
            TextArea = { SelectionBrush = new SolidColorBrush(Color.Parse("#ADD8E6")) },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Width = double.NaN,
            Height = double.NaN,
            Options =
            {
                ShowTabs = false,
                ShowSpaces = false,
                ShowEndOfLine = false,
                HighlightCurrentLine = true,
                EnableHyperlinks = false,
                EnableRectangularSelection = true,
                AllowScrollBelowDocument = true
            },
            ContextMenu = CreateEditorContextMenu()
        };

        // 编辑器配色：创建时取当前主题色；主题切换时由 UpdateEditorColors 统一刷新
        editor.Background = ThemeBrush("EditorBackgroundBrush");
        editor.Foreground = ThemeBrush("EditorForegroundBrush");
        editor.TextArea.SelectionBrush = ThemeBrush("EditorSelectionBrush");

        // 启用 IME 支持以显示拼音输入法候选框
        editor.Options.EnableImeSupport = true;
        
        // 确保编辑器控件可以获得焦点并支持IME
        editor.Focusable = true;
        editor.TextArea.Focusable = true;

        // 启用 Avalonia 框架级别的 IME 支持
        InputMethod.SetIsInputMethodEnabled(editor, true);
        InputMethod.SetIsInputMethodEnabled(editor.TextArea, true);

        // 接管 IME client：AvaloniaEdit 默认 SupportsPreedit=false 且 SetPreeditText 为空实现，
        // 导致拼音不可见；用自定义 client + Popup 在光标处显示拼音（Bubble 阶段覆盖默认 client）
        var imeSupport = new EditorImeSupport(editor);
        editor.Tag = imeSupport;   // 保持引用，防止被 GC
        editor.AddHandler(
            InputElement.TextInputMethodClientRequestedEvent,
            (object? s, TextInputMethodClientRequestedEventArgs e) => e.Client = imeSupport,
            RoutingStrategies.Bubble);

        // 拦截编辑器默认按键命令：AvaloniaEdit 内置查找框（Ctrl+F / F3 / Shift+F3）会抢在窗口之前
        // 处理按键并弹出默认查找面板；在 Tunnel 阶段拦截并转交应用自己的查找/替换对话框。
        editor.AddHandler(InputElement.KeyDownEvent, EditorShortcutInterceptor, RoutingStrategies.Tunnel);

        return editor;
    }

    /// <summary>编辑器按键拦截（Tunnel 阶段，优先于 AvaloniaEdit 内置命令绑定）：
    /// Ctrl+F → 应用“查找”对话框；Ctrl+H → 应用“替换”对话框；F3/Shift+F3 → 吞掉，避免唤出内置查找框。</summary>
    private void EditorShortcutInterceptor(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.F:
                    e.Handled = true;
                    Find_Click(null, null!);
                    return;
                case Key.H:
                    e.Handled = true;
                    Replace_Click(null, null!);
                    return;
            }
        }
        else if (e.Key is Key.F3)
        {
            // 禁用 AvaloniaEdit 内置的“查找下一个/上一个”默认命令
            e.Handled = true;
        }
    }

    /// <summary>富文本编辑器按键拦截：Ctrl+F/Ctrl+H/F3 与应用查找/替换对话框保持一致。</summary>
    private void RichEditorShortcutInterceptor(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.F:
                    e.Handled = true;
                    Find_Click(null, null!);
                    return;
                case Key.H:
                    e.Handled = true;
                    Replace_Click(null, null!);
                    return;
            }
        }
        else if (e.Key is Key.F3)
        {
            e.Handled = true;
            // 查找下一个：查找对话框已打开就直接触发，否则新开一个
            if (_findDialog != null && _findDialog.IsVisible)
                _findDialog.FindNext();
            else
                Find_Click(null, null!);
        }
    }

    /// <summary>创建标签页容器：文本编辑器常驻，富文本/图片查看器按模式惰性加入并切换可见性。</summary>
    private Grid CreateEditorContainer(EditorTab tab)
    {
        var grid = new Grid();
        grid.Children.Add(tab.TextEditor);
        tab.EditorContainer = grid;
        return grid;
    }

    /// <summary>获取（或惰性创建）标签页的富文本编辑器，并把它加入容器。</summary>
    private Avalonia.Controls.RichTextEditor GetOrCreateRichEditor(EditorTab tab)
    {
        if (tab.RichEditor != null) return tab.RichEditor;

        var rich = new Avalonia.Controls.RichTextEditor
        {
            ShowToolbar = true,
            ShowPageBounds = false,
            FontFamily = tab.TextEditor.FontFamily,
            FontSize = 16 * currentZoom,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsVisible = false,
        };
        // 编辑器配色：创建时取当前主题色；主题切换时由 UpdateEditorColors 统一刷新
        rich.Background = ThemeBrush("EditorBackgroundBrush");
        rich.Foreground = ThemeBrush("EditorForegroundBrush");
        rich.Document = new FlowDocument();
        rich.AddHandler(InputElement.KeyDownEvent, RichEditorShortcutInterceptor, RoutingStrategies.Tunnel);
        // 内容变化 → 修改标记（ContentChanged 覆盖输入、粘贴、格式调整、撤销/重做）
        rich.ContentChanged += (_, _) =>
        {
            if (!tab.IsLoaded) return;
            Tab_TextChanged(tab);
            ScheduleRichStatsRefresh(tab);
        };
        rich.SelectionChanged += (_, _) =>
        {
            if (tab == GetCurrentTab())
            {
                UpdateRichStatsFromSelection(tab);
            }
        };

        tab.RichEditor = rich;
        tab.EditorContainer?.Children.Add(rich);
        return rich;
    }

    /// <summary>获取（或惰性创建）标签页的图片查看器。</summary>
    private Avalonia.Controls.FlowDocumentScrollViewer GetOrCreateImageViewer(EditorTab tab)
    {
        if (tab.ImageViewer != null) return tab.ImageViewer;

        var viewer = new Avalonia.Controls.FlowDocumentScrollViewer
        {
            Document = new FlowDocument(),
            IsVisible = false,
            IsSelectionEnabled = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        tab.ImageViewer = viewer;
        tab.EditorContainer?.Children.Add(viewer);
        return viewer;
    }

    /// <summary>切换标签页编辑器模式（文本↔富文本↔图片查看），并刷新菜单状态。</summary>
    private void SetTabMode(EditorTab tab, EditorMode mode)
    {
        tab.Mode = mode;
        tab.TextEditor.IsVisible = mode == EditorMode.Text;
        if (tab.RichEditor != null) tab.RichEditor.IsVisible = mode == EditorMode.RichText;
        if (tab.ImageViewer != null) tab.ImageViewer.IsVisible = mode == EditorMode.ImageViewer;

        if (tab == GetCurrentTab())
        {
            UpdateMenuStatesForTab(tab);
            UpdateFileStats(tab);
        }
    }

    /// <summary>按标签页模式启用/禁用菜单项：富文本/图片模式禁用编码、语言、JSON、格式化等纯文本功能。</summary>
    private void UpdateMenuStatesForTab(EditorTab tab)
    {
        bool isText = tab.Mode == EditorMode.Text;
        bool isRich = tab.Mode == EditorMode.RichText;
        bool isImage = tab.Mode == EditorMode.ImageViewer;

        // 纯文本专属功能
        EncodingMenu.IsEnabled = isText;
        LanguageMenu.IsEnabled = isText;
        JsonSubMenu.IsEnabled = isText;
        FormatCodeMenuItem.IsEnabled = isText;
        ShowTabsMenuItem.IsEnabled = isText;
        ShowSpacesMenuItem.IsEnabled = isText;
        ShowEndOfLineMenuItem.IsEnabled = isText;

        // 图片模式只读：编辑类功能禁用（撤销/重做/剪切/粘贴/替换/保存另存除外）
        bool editable = !isImage;
        UndoMenuItem.IsEnabled = editable;
        RedoMenuItem.IsEnabled = editable;
        CutMenuItem.IsEnabled = editable;
        PasteMenuItem.IsEnabled = editable;
        SaveMenuItem.IsEnabled = !isImage;
        SaveAsMenuItem.IsEnabled = !isImage;

        // 富文本模式：查找/替换/全选/复制均可用（官方控件内实现）
        FindMenuItem.IsEnabled = true;
        ReplaceMenuItem.IsEnabled = editable;
        CopyMenuItem.IsEnabled = true;
        SelectAllMenuItem.IsEnabled = true;
    }

    /// <summary>富文本统计防抖刷新定时器（避免每次输入都遍历文档树）。</summary>
    private DispatcherTimer? _richStatsTimer;

    private void ScheduleRichStatsRefresh(EditorTab tab)
    {
        _richStatsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _richStatsTimer.Stop();
        _richStatsTimer.Tick += (_, _) =>
        {
            _richStatsTimer.Stop();
            UpdateFileStats(tab);
        };
        _richStatsTimer.Start();
    }

    /// <summary>把富文本选区解析为文本偏移区间 (start, end)，无选区时返回 (0, 0)。</summary>
    private static (int Start, int End) GetSelectionOffsetRange(
        Avalonia.Controls.TextSelection? sel, Avalonia.Controls.Documents.TextModel.TextDocument td)
    {
        if (sel == null) return (0, 0);
        int start = td.ContentStart.GetOffsetToPosition(sel.Start);
        int end = td.ContentStart.GetOffsetToPosition(sel.End);
        return (Math.Min(start, end), Math.Max(start, end));
    }

    private void UpdateRichStatsFromSelection(EditorTab tab)
    {
        if (tab.Mode != EditorMode.RichText || tab != GetCurrentTab()) return;
        var rich = tab.RichEditor;
        if (rich?.Document == null) return;
        try
        {
            var td = rich.Document.EnsureTextDocument();
            var (start, end) = GetSelectionOffsetRange(rich.Selection, td);
            if (end > start)
            {
                CursorPosText.Text = $"Sel: {start + 1}–{end}";
            }
        }
        catch
        {
            // 文档尚未就绪，跳过
        }
    }

    private ContextMenu CreateEditorContextMenu()
    {
        var menu = new ContextMenu();

        // 创建菜单项列表（包含 Separator 作为 IControl）
        var menuItems = new List<object>
        {
            new MenuItem { Header = "撤销 (_U)", InputGesture = new KeyGesture(Key.Z, KeyModifiers.Control) },
            new MenuItem { Header = "重做 (_R)", InputGesture = new KeyGesture(Key.Y, KeyModifiers.Control) },
            new Separator(),
            new MenuItem { Header = "剪切 (_T)", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) },
            new MenuItem { Header = "复制 (_C)", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) },
            new MenuItem { Header = "粘贴 (_P)", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) },
            new Separator(),
            new MenuItem { Header = "全选 (_A)", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) },
            new Separator(),
            new MenuItem { Header = "查找 (_F)", InputGesture = new KeyGesture(Key.F, KeyModifiers.Control) },
            new MenuItem { Header = "替换 (_R)", InputGesture = new KeyGesture(Key.H, KeyModifiers.Control) },
            new Separator(),
            new MenuItem { Header = "格式化代码 (_K)", InputGesture = new KeyGesture(Key.K, KeyModifiers.Control) }
        };

        // 为每个菜单项添加点击事件并添加到菜单
        foreach (var item in menuItems)
        {
            if (item is MenuItem menuItem)
            {
                menuItem.Click += ContextMenuItem_Click;
            }
            menu.Items.Add(item);
        }

        return menu;
    }

    private void ContextMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;

        var header = menuItem.Header?.ToString() ?? "";

        // 根据菜单项标题执行相应操作
        if (header.StartsWith("撤销"))
            Undo_Click(null, null!);
        else if (header.StartsWith("重做"))
            Redo_Click(null, null!);
        else if (header.StartsWith("剪切"))
            Cut_Click(null, null!);
        else if (header.StartsWith("复制"))
            Copy_Click(null, null!);
        else if (header.StartsWith("粘贴"))
            Paste_Click(null, null!);
        else if (header.StartsWith("全选"))
            SelectAll_Click(null, null!);
        else if (header.StartsWith("查找"))
            Find_Click(null, null!);
        else if (header.StartsWith("替换"))
            Replace_Click(null, null!);
        else if (header.StartsWith("格式化"))
            FormatCode_Click(null, null!);
    }

    // ========== Tab Management ==========

    public TextEditor? GetCurrentTextEditor()
    {
        if (FileTabControl.SelectedItem is TabItem selectedTab &&
            selectedTab.Tag is EditorTab editorTab)
        {
            return editorTab.TextEditor;
        }
        return null;
    }

    public EditorTab? GetCurrentTab()
    {
        if (FileTabControl == null) return null;
        if (FileTabControl.SelectedItem is TabItem selectedTab)
        {
            return selectedTab.Tag as EditorTab;
        }
        return null;
    }

    /// <summary>
    /// 应用错别字高亮：把 agent 返回的（1 起始行/列）错别字换算为当前文档偏移并精确标记，
    /// 同时清空其他标签页的旧标记。可跨线程调用（自动调度到 UI 线程）。
    /// </summary>
    private void ApplyTypoHighlights(IReadOnlyList<TypoItem> typos)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyTypoHighlights(typos));
            return;
        }

        foreach (var t in _tabs) t.TypoColorizer?.Clear();

        var editor = GetCurrentTextEditor();
        var colorizer = _tabs.FirstOrDefault(t => t.TextEditor == editor)?.TypoColorizer;
        if (editor == null || colorizer == null) return;

        var ranges = new List<(int Start, int Length)>();
        var seen = new HashSet<int>();
        foreach (var typo in typos)
        {
            var line = editor.Document.GetLineByNumber(typo.Line);
            if (line == null) continue;
            int col = Math.Max(0, typo.Column - 1);
            if (col >= line.Length) continue;   // 列号超出行尾：容错跳过
            int start = line.Offset + col;
            int len = Math.Max(1, typo.Original?.Length ?? 1);
            if (len > line.Length - col) len = line.Length - col;
            if (len <= 0) continue;

            // 与文档实际内容一致性校验：列位置对不上时尝试行内查找原文，找不到则跳过
            string lineText = editor.Document.GetText(line.Offset, line.Length);
            bool matches = !string.IsNullOrEmpty(typo.Original)
                && col + typo.Original.Length <= lineText.Length
                && string.CompareOrdinal(lineText, col, typo.Original, 0, typo.Original.Length) == 0;
            if (!matches)
            {
                if (string.IsNullOrEmpty(typo.Original)) continue;
                int idx = lineText.IndexOf(typo.Original, StringComparison.Ordinal);
                if (idx < 0) continue;
                start = line.Offset + idx;
                len = Math.Min(typo.Original.Length, line.Length - idx);
                if (len <= 0) continue;
            }
            if (seen.Add(start)) ranges.Add((start, len));
        }

        colorizer.SetRanges(ranges);
        editor.TextArea.TextView.Redraw();
    }

    /// <summary>扫描文档，高亮所有与当前选中文字相同的区间；选区过短或含换行时清除高亮。</summary>
    private void UpdateSelectionMatches(EditorTab tab)
    {
        var textEditor = tab.TextEditor;
        var colorizer = tab.MatchColorizer;
        if (colorizer == null) return;

        var selection = textEditor.SelectedText;

        // 无选区、选中太短（单个字符/空格）或含换行的多行选区不做匹配，避免满屏闪烁
        if (string.IsNullOrEmpty(selection) || selection.Length < 2
            || selection.Contains('\r') || selection.Contains('\n'))
        {
            if (colorizer.HasMarkers)
            {
                colorizer.Clear();
                textEditor.TextArea.TextView.Redraw();
            }
            return;
        }

        var text = textEditor.Document.Text;
        var ranges = new List<(int Start, int Length)>();
        // 最大匹配数保护：超大文档或高频短词时避免区间过多拖慢渲染
        const int MaxMatches = 1000;
        int idx = 0;
        while (ranges.Count < MaxMatches
               && (idx = text.IndexOf(selection, idx, StringComparison.Ordinal)) >= 0)
        {
            ranges.Add((idx, selection.Length));
            idx += selection.Length;
        }

        colorizer.SetRanges(ranges);
        textEditor.TextArea.TextView.Redraw();
    }

    private EditorTab CreateNewTab()
    {
        var textEditor = CreateNewTextEditor();
        textEditor.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        textEditor.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        var tab = new EditorTab(textEditor)
        {
            TabColor = GetTabColor(_colorIndex),
            TabColorIndex = _colorIndex % TabColors.Length
        };
        // 错别字高亮：每标签页一个着色器，常驻行渲染管道
        var typoColorizer = new TypoHighlightColorizer();
        tab.TypoColorizer = typoColorizer;
        textEditor.TextArea.TextView.LineTransformers.Add(typoColorizer);
        // 选中文字着色：工具栏字体颜色面板为选中文字着色
        var fontColorizer = new SelectionColorColorizer();
        tab.FontColorizer = fontColorizer;
        textEditor.TextArea.TextView.LineTransformers.Add(fontColorizer);
        // 选中匹配高亮：选中文字时把文档中所有相同文字高亮显示（同 Typo 模式，常驻渲染管道）
        var matchColorizer = new SelectionMatchColorizer();
        tab.MatchColorizer = matchColorizer;
        textEditor.TextArea.TextView.LineTransformers.Add(matchColorizer);
        // 选区变化后防抖扫描文档：拖动选区时避免每次移动都全文档搜索
        tab.MatchHighlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        tab.MatchHighlightTimer.Tick += (_, _) => UpdateSelectionMatches(tab);
        textEditor.TextArea.SelectionChanged += (_, _) =>
        {
            tab.MatchHighlightTimer?.Stop();
            tab.MatchHighlightTimer?.Start();
        };
        // 文档被编辑后旧标记失效 → 自动清除（避免高亮错位指向错误的字）
        textEditor.Document.TextChanged += (_, _) =>
        {
            bool needRedraw = false;
            if (tab.TypoColorizer is { HasMarkers: true })
            {
                tab.TypoColorizer.Clear();
                needRedraw = true;
            }
            if (tab.MatchColorizer is { HasMarkers: true })
            {
                tab.MatchColorizer.Clear();
                needRedraw = true;
            }
            if (needRedraw)
                textEditor.TextArea.TextView.Redraw();
        };

        // 大括号折叠：此处仅对"已确定是代码文件"的标签页即时安装。
            // 注意打开文件时 CreateNewTab() 先于 tab.FilePath 赋值执行，此刻扩展名尚不可知，
            // 因此真正打开文件后需再调用一次 EnsureBraceFolding(tab)（幂等）补装。
            EnsureBraceFolding(tab);
        //textEditor.TextArea.SelectionBrush =  TabColors[_colorIndex % TabColors.Length];
        _colorIndex++;

        var closeButton = CreateTabCloseButton(tab);

        var headerPanel = CreateTabHeader(tab.FileName, closeButton);

        var tabItem = new TabItem
        {
            Header = headerPanel,
            Tag = tab,
            Content = CreateEditorContainer(tab),
            Background = tab.TabColor
        };
        headerPanel.ContextFlyout = CreateTabContextMenu(tab, tabItem);

        _tabs.Add(tab);
        FileTabControl.Items.Add(tabItem);
        FileTabControl.SelectedItem = tabItem;

        textEditor.TextChanged += (s, e) =>
        {
            if (tab.IsLoaded) Tab_TextChanged(tab);
        };
        textEditor.TextChanged += (s, e) => UpdateFileStats(tab);
        textEditor.TextArea.Caret.PositionChanged += (s, e) => Caret_PositionChanged(tab);

        UpdateFileStats(tab);
        UpdateTitle();

        return tab;
    }

    // ========== 大括号折叠 ==========

    /// <summary>
    /// 幂等地为标签页准备大括号折叠。
    /// 打开文件的流程是 CreateNewTab()（此时 FilePath 还未赋值）→ 赋值 FilePath → 设置语言，
    /// 所以 CreateNewTab 里判断不出扩展名；在文件加载完成后需再调用本方法补一次。
    /// 已经安装过（FoldingManager 非空）或不是代码文件则直接返回。
    /// </summary>
    private void EnsureBraceFolding(EditorTab tab)
    {
        if (tab.FoldingManager != null) return;       // 已安装，避免重复加边栏
        if (!ShouldEnableFolding(tab)) return;        // 非代码文件，不启用
        InstallBraceFolding(tab);
    }

    /// <summary>
    /// 为标签页安装大括号折叠：FoldingManager + 左侧折叠边栏（显示 +/- 折叠按钮）。
    /// 文档变更（含打开文件后一次性填充文本）会防抖重算折叠区域，避免每敲一个字都全文档扫描。
    /// 只对代码文件启用（见 ShouldEnableFolding）：非代码文件不装边栏，界面保持干净。
    /// </summary>
    private void InstallBraceFolding(EditorTab tab)
    {
        try
        {
            if (!ShouldEnableFolding(tab)) return;    // 双重保险
            if (tab.FoldingManager != null) return;

            var editor = tab.TextEditor;
            var manager = FoldingManager.Install(editor.TextArea);
            tab.FoldingManager = manager;

            // 左侧折叠边栏：插到行号左侧
            var margin = new FoldingMargin { FoldingManager = manager };
            editor.TextArea.LeftMargins.Insert(0, margin);
            tab.FoldingMargin = margin;

            // 防抖重算：打开大文件/快速输入时不做全文档扫描
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                UpdateBraceFoldings(tab);
            };
            tab.FoldingUpdateTimer = timer;

            editor.Document.TextChanged += (_, _) =>
            {
                timer.Stop();
                timer.Start();
            };

            // 主题相关：折叠标记颜色随主题刷新
            ApplyFoldingMarginColors(margin, _isDark);

            UpdateBraceFoldings(tab);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InstallBraceFolding failed: {ex.Message}");
        }
    }

    /// <summary>重算某个标签页的大括号折叠区域。</summary>
    private static void UpdateBraceFoldings(EditorTab tab)
    {
        if (tab.FoldingManager == null) return;
        try
        {
            bool isJson = string.Equals(tab.Language, "JSON", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(tab.FilePath)
                    && string.Equals(Path.GetExtension(tab.FilePath), ".json", StringComparison.OrdinalIgnoreCase));
            bool isPython = string.Equals(tab.Language, "Python", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(tab.FilePath)
                    && string.Equals(Path.GetExtension(tab.FilePath), ".py", StringComparison.OrdinalIgnoreCase));
            if (isPython)
                Services.IndentFoldingStrategy.UpdateFoldings(tab.FoldingManager, tab.TextEditor.Document);
            else
                Services.BraceFoldingStrategy.UpdateFoldings(tab.FoldingManager, tab.TextEditor.Document, isJson);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateBraceFoldings failed: {ex.Message}");
        }
    }

    /// <summary>按主题设置折叠标记（+/-）及其背景色。</summary>
    private static void ApplyFoldingMarginColors(FoldingMargin margin, bool dark)
    {
        margin.FoldingMarkerBrush = new SolidColorBrush(dark
            ? Color.FromRgb(0x9A, 0x9A, 0x9A)
            : Color.FromRgb(0x60, 0x60, 0x60));
        margin.FoldingMarkerBackgroundBrush = new SolidColorBrush(dark
            ? Color.FromRgb(0x2D, 0x2D, 0x2D)
            : Color.FromRgb(0xF0, 0xF0, 0xF0));
        margin.SelectedFoldingMarkerBrush = new SolidColorBrush(dark
            ? Color.FromRgb(0xE0, 0xE0, 0xE0)
            : Color.FromRgb(0x20, 0x20, 0x20));
        margin.SelectedFoldingMarkerBackgroundBrush = new SolidColorBrush(dark
            ? Color.FromRgb(0x50, 0x50, 0x50)
            : Color.FromRgb(0xD0, 0xE4, 0xF8));
    }

    /// <summary>折叠当前光标所在块。</summary>
    private void FoldCurrentBlock()
    {
        var tab = GetCurrentTab();
        if (tab?.FoldingManager == null) return;
        try
        {
            var offset = tab.TextEditor.CaretOffset;
            // 光标可能位于折叠区域起点之后，向上找包含该偏移的最内层可折叠区域
            var containing = tab.FoldingManager.GetFoldingsContaining(offset)
                .Where(f => !f.IsFolded)
                .OrderBy(f => f.EndOffset - f.StartOffset)
                .FirstOrDefault();
            if (containing != null)
            {
                containing.IsFolded = true;
                StatusText.Text = "已折叠当前块";
                return;
            }
            // 光标不在任何块内：向后找最近的折叠起点
            var next = tab.FoldingManager.GetNextFolding(offset);
            if (next != null && !next.IsFolded)
            {
                next.IsFolded = true;
                StatusText.Text = "已折叠下一块";
            }
            else StatusText.Text = "当前位置没有可折叠的块";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FoldCurrentBlock failed: {ex.Message}");
        }
    }

    /// <summary>展开当前光标所在块（含所在的最外层折叠）。</summary>
    private void UnfoldCurrentBlock()
    {
        var tab = GetCurrentTab();
        if (tab?.FoldingManager == null) return;
        try
        {
            var offset = tab.TextEditor.CaretOffset;
            var folded = tab.FoldingManager.GetFoldingsContaining(offset)
                .Where(f => f.IsFolded)
                .ToList();
            if (folded.Count == 0)
            {
                // 光标不在折叠区内：展开起点在最靠近光标之前的那个折叠
                folded = tab.FoldingManager.AllFoldings
                    .Where(f => f.IsFolded && f.StartOffset >= offset)
                    .OrderBy(f => f.StartOffset)
                    .Take(1)
                    .ToList();
            }
            if (folded.Count == 0) { StatusText.Text = "当前位置没有可展开的块"; return; }
            foreach (var f in folded) f.IsFolded = false;
            StatusText.Text = $"已展开 {folded.Count} 个块";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UnfoldCurrentBlock failed: {ex.Message}");
        }
    }

    /// <summary>折叠全部块。</summary>
    private void FoldAllBlocks()
    {
        var tab = GetCurrentTab();
        if (tab?.FoldingManager == null) return;
        int n = 0;
        foreach (var f in tab.FoldingManager.AllFoldings) { if (!f.IsFolded) { f.IsFolded = true; n++; } }
        StatusText.Text = $"已折叠 {n} 个块";
    }

    /// <summary>展开全部块。</summary>
    private void UnfoldAllBlocks()
    {
        var tab = GetCurrentTab();
        if (tab?.FoldingManager == null) return;
        int n = 0;
        foreach (var f in tab.FoldingManager.AllFoldings) { if (f.IsFolded) { f.IsFolded = false; n++; } }
        StatusText.Text = $"已展开 {n} 个块";
    }

    /// <summary>切换（折叠/展开）全部块。</summary>
    private void ToggleFoldAllBlocks()
    {
        var tab = GetCurrentTab();
        if (tab?.FoldingManager == null) return;
        bool anyFolded = tab.FoldingManager.AllFoldings.Any(f => f.IsFolded);
        if (anyFolded) UnfoldAllBlocks();
        else FoldAllBlocks();
    }

    private void SetupFileWatcher(EditorTab tab)
    {
        if (string.IsNullOrEmpty(tab.FilePath)) return;

        tab.FileWatcher?.Dispose();

        var dirPath = Path.GetDirectoryName(tab.FilePath);
        if (string.IsNullOrEmpty(dirPath)) return;

        var watcher = new FileSystemWatcher
        {
            Path = dirPath,
            Filter = Path.GetFileName(tab.FilePath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };

        watcher.Changed += (sender, e) =>
        {
            if (string.IsNullOrEmpty(tab.FilePath)) return;

            // 如果文件正在内部保存中，忽略此次变化
            if (_internalSavingFiles.Contains(tab.FilePath)) return;

            // 如果文件刚被内部保存过（2 秒内），忽略此次变化
            if (_lastInternalSaveTimes.TryGetValue(tab.FilePath, out var lastSaveTime) &&
                DateTime.UtcNow - lastSaveTime < TimeSpan.FromSeconds(2))
            {
                _lastInternalSaveTimes.Remove(tab.FilePath);
                return;
            }

            if (tab.IsExternalChanging || tab.IsShowingReloadPrompt) return;

            try
            {
                var fileInfo = new FileInfo(tab.FilePath);
                if (fileInfo.LastWriteTimeUtc <= tab.LastWriteTimeUtc) return;

                tab.IsShowingReloadPrompt = true;

                Dispatcher.UIThread.Post(async () =>
                {
                    if (tab.IsModified)
                    {
                        tab.IsShowingReloadPrompt = false;
                        return;
                    }

                    var result = await ShowMessageBox(
                        $"文件 \"{tab.FileName}\" 已被外部程序修改，是否重新加载最新内容？",
                        "文件已更改",
                        MessageBoxButtons.YesNo);

                    if (result == MessageBoxResult.Yes)
                    {
                        tab.IsExternalChanging = true;
                        try
                        {
                            await ReloadFileContentAsync(tab, fileInfo);
                            StatusText.Text = $"已重新加载文件：{tab.FileName}";
                        }
                        finally
                        {
                            tab.IsExternalChanging = false;
                            tab.IsShowingReloadPrompt = false;
                        }
                    }
                    else
                    {
                        tab.IsShowingReloadPrompt = false;
                        tab.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                    }
                });
            }
            catch (Exception ex)
            {
                tab.IsShowingReloadPrompt = false;
                System.Diagnostics.Debug.WriteLine($"File watcher error: {ex.Message}");
            }
        };

        watcher.EnableRaisingEvents = true;
        tab.FileWatcher = watcher;
    }

    /// <summary>按标签页模式重新加载文件内容（外部变更后重载共用入口）。</summary>
    private async Task ReloadFileContentAsync(EditorTab tab, FileInfo fileInfo)
    {
        if (tab.Mode == EditorMode.RichText)
        {
            string path = tab.FilePath!;
            string ext = Path.GetExtension(path).ToLower();
            string loadPath = path;
            // .doc/.dot 转换标签：原文件被外部改过，重新转换覆盖缓存
            if (RichDocumentService.WordLegacyExts.Contains(ext) && !string.IsNullOrEmpty(tab.DocCachePath))
            {
                loadPath = RichDocumentService.GetDocCachePath(path);
                bool ok = await Task.Run(() => RichDocumentService.TryConvertToDocx(path, loadPath));
                if (!ok) return;
                tab.DocCachePath = loadPath;
            }
            var doc = await RichDocumentService.LoadDocumentAsync(loadPath);
            if (tab.RichEditor != null) tab.RichEditor.Document = doc;
        }
        else if (tab.Mode == EditorMode.ImageViewer)
        {
            var doc = await Task.Run(() => RichDocumentService.BuildImageViewerDocument(tab.FilePath!));
            if (tab.ImageViewer != null) tab.ImageViewer.Document = doc;
        }
        else
        {
            ReadFileWithEncodingDetection(tab.FilePath!, tab.TextEditor);
        }
        tab.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        tab.IsModified = false;
        UpdateTabHeader(tab);
        UpdateFileStats(tab);
    }

    private void Tab_TextChanged(EditorTab tab)
    {
        if (!tab.IsModified)
        {
            tab.IsModified = true;
            UpdateTabHeader(tab);
            UpdateTitle();
        }
    }

    private void Caret_PositionChanged(EditorTab tab)
    {
        // 富文本/图片模式不订阅文本光标事件
        if (tab.Mode != EditorMode.Text) return;
        if (tab == GetCurrentTab())
        {
            int lineNum = tab.TextEditor.TextArea.Caret.Line;
            int columnNum = tab.TextEditor.TextArea.Caret.Column;
            CursorPosText.Text = $"Ln: {lineNum}, Col: {columnNum}";
        }
        UpdateFileStats(tab);
    }

    private void UpdateFileStats(EditorTab tab)
    {
        if (tab.Mode == EditorMode.RichText)
        {
            var rich = tab.RichEditor;
            if (rich?.Document == null) return;
            try
            {
                var snapshot = rich.Document.EnsureTextDocument().CreateSnapshot();
                int charCount = snapshot.TextLength;
                int paraCount = CountParagraphs(rich.Document);
                CursorPosText.Text = "";
                LineCountText.Text = $"段落: {paraCount}";
                ByteCountText.Text = $"字符: {charCount}";
                EncodingText.Text = "富文本";
            }
            catch
            {
                // 文档尚未就绪，跳过统计
            }
            return;
        }
        if (tab.Mode == EditorMode.ImageViewer)
        {
            CursorPosText.Text = "";
            LineCountText.Text = "图片";
            ByteCountText.Text = "";
            EncodingText.Text = "只读查看";
            return;
        }
        string text = tab.TextEditor.Text;
        int lineCount = tab.TextEditor.Document.LineCount;
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(text);
        LineCountText.Text = $"Lines: {lineCount}";
        ByteCountText.Text = $"Bytes: {byteCount}";
        EncodingText.Text = $"Encoding: {GetEncodingName(tab.Encoding)}";
    }

    /// <summary>统计文档段落数（递归进列表项、表格单元格）。</summary>
    private static int CountParagraphs(FlowDocument doc)
    {
        int count = 0;
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph:
                        count++;
                        break;
                    case List list:
                        foreach (var item in list.ListItems)
                            if (item is ListItem li && li.Blocks != null) Walk(li.Blocks);
                        break;
                    case Table table:
                        foreach (var rowGroup in table.RowGroups)
                            foreach (var row in rowGroup.Rows)
                                foreach (var cell in row.Cells)
                                    if (cell.Blocks != null) Walk(cell.Blocks);
                        break;
                }
            }
        }
        Walk(doc.Blocks);
        return count;
    }

    private string GetEncodingName(System.Text.Encoding encoding)
    {
        if (encoding is UTF8Encoding utf8)
        {
            if (utf8.GetPreamble().Length > 0) return "UTF-8 with BOM";
            return "UTF-8";
        }
        if (encoding is UnicodeEncoding) return "UTF-16";
        if (encoding is UTF32Encoding) return "UTF-32";
        if (encoding.CodePage == 936 || encoding.WebName.ToLower() == "gb2312") return "GB2312";
        if (Equals(encoding, System.Text.Encoding.Default)) return "ANSI";
        return encoding.EncodingName;
    }

    private void UpdateTabHeader(EditorTab tab)
    {
        foreach (TabItem tabItem in FileTabControl.Items)
        {
            if (tabItem.Tag == tab)
            {
                string modifiedMark = tab.IsModified ? " *" : "";
                if (tabItem.Header is StackPanel headerPanel &&
                    headerPanel.Children.Count > 0 &&
                    headerPanel.Children[0] is TextBlock textBlock)
                {
                    textBlock.Text = tab.FileName + modifiedMark;
                }
                else
                {
                    tabItem.Header = tab.FileName + modifiedMark;
                }
                break;
            }
        }
    }

    public void UpdateTabHeaderPublic(EditorTab tab)
    {
        UpdateTabHeader(tab);
        UpdateTitle();
    }

    private void FileTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            FilePathText.Text = tab.FilePath ?? "";
            UpdateZoomDisplay();
            UpdateMenuStatesForTab(tab);
            if (tab.Mode == EditorMode.Text && tab.TextEditor.SyntaxHighlighting == null && !string.IsNullOrEmpty(tab.FilePath))
            {
                string fileExtension = Path.GetExtension(tab.FilePath).ToLower();
                SetLanguageByExtension(fileExtension, tab);
            }
            else if (tab.Mode == EditorMode.Text && !string.IsNullOrEmpty(tab.Language))
            {
                // 如果已有语法高亮，更新语言菜单显示
                UpdateLanguageMenu(tab.Language);
            }
            UpdateFileStats(tab);
        }
        else
        {
            // SSH 终端等非文件标签：清空文件路径显示
            FilePathText.Text = "";
        }
    }

    private void CloseTabButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var parent = button.Parent;
            while (parent != null && parent is not TabItem)
            {
                parent = (parent as Control)?.Parent;
            }
            if (parent is TabItem tabItem)
            {
                CloseTab(tabItem);
            }
        }
    }

    private void CloseTab(TabItem tabItem) => CloseTabAsync(tabItem);

    private async void CloseTabAsync(TabItem tabItem)
    {
        // 非文件标签的 Tag 是 SimpleTabTag 包装（携带视图 + 调色板索引），先取出真实视图
        var tag = tabItem.Tag;
        if (tag is SimpleTabTag simple) tag = simple.View;

        // SSH 终端标签：断开连接后直接移除，不走文件保存流程
        if (tag is SshTerminalView sshView)
        {
            await sshView.ShutdownAsync();
            FileTabControl.Items.Remove(tabItem);
            return;
        }

        // 本地终端标签：终止 shell 进程后直接移除
        if (tag is LocalTerminalView localView)
        {
            await localView.ShutdownAsync();
            FileTabControl.Items.Remove(tabItem);
            return;
        }

        // 工具标签（HTTP 调试 / 信息摘要 / 加密解密等）：非文件标签直接移除（实现 IDisposable 的先释放资源）
        if (tag is not EditorTab)
        {
            if (tag is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            FileTabControl.Items.Remove(tabItem);
            return;
        }

        if (tag is not EditorTab tab) return;

        if (tab.IsModified)
        {
            var result = await ShowMessageBox($"文件 \"{tab.FileName}\" 已修改，是否保存？", "确认",
                MessageBoxButtons.YesNoCancel);
            if (result == MessageBoxResult.Yes)
            {
                await SaveTabAsync(tab);
                if (tab.IsModified) return;
            }
            else if (result == MessageBoxResult.Cancel)
            {
                return;
            }
        }

        tab.FileWatcher?.Dispose();
        tab.FileWatcher = null;

        tab.MatchHighlightTimer?.Stop();
        tab.MatchHighlightTimer = null;

        _tabs.Remove(tab);
        FileTabControl.Items.Remove(tabItem);

        if (_tabs.Count == 0)
        {
            NewFile();
        }
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (FileTabControl.SelectedItem is TabItem tabItem)
        {
            CloseTab(tabItem);
        }
    }

    /// <summary>
    /// 取出标签页 Tag 里承载的真实对象：文件标签为 EditorTab，
    /// 非文件标签（对比 / 终端）为 SimpleTabTag 包装内的视图。
    /// </summary>
    private static object? GetTabTag(TabItem item) =>
        item.Tag is SimpleTabTag simple ? simple.View : item.Tag;

    /// <summary>
    /// 非文件标签（Git 对比 / 终端）的标签页 Tag：携带视图对象与调色板索引。
    /// 用引用传递视图，保证关闭按钮 / 右键菜单能稳定定位所属标签页，
    /// 同时让主题切换时能按索引重算底色。
    /// </summary>
    private sealed class SimpleTabTag
    {
        public SimpleTabTag(object view, int colorIndex)
        {
            View = view;
            ColorIndex = colorIndex;
        }

        /// <summary>标签页内容视图（DiffView / LocalTerminalView / SshTerminalView）</summary>
        public object View { get; }

        /// <summary>调色板索引（主题切换时据此重算 Background）</summary>
        public int ColorIndex { get; }
    }

    /// <summary>
    /// 统一创建标签页头部的关闭按钮（文件 / Git 对比 / 终端等所有标签共用同一尺寸与配色）。
    /// 点按后按 Tag 定位所属标签页并走统一的 CloseTab 收尾流程。
    /// </summary>
    private Button CreateTabCloseButton(object tag)
    {
        var closeButton = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            FontSize = 12,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            BorderThickness = new Thickness(0),
        };
        closeButton.Click += (_, _) =>
        {
            foreach (TabItem item in FileTabControl.Items)
            {
                if (ReferenceEquals(item.Tag, tag))
                {
                    CloseTab(item);
                    break;
                }
            }
        };
        return closeButton;
    }

    /// <summary>
    /// 统一创建标签页头部（标题文字 + 关闭按钮），文件 / 对比 / 终端标签共用同一字号字重。
    /// </summary>
    private StackPanel CreateTabHeader(string title, Button closeButton)
    {
        var headerPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        headerPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        headerPanel.Children.Add(closeButton);
        return headerPanel;
    }

    /// <summary>
    /// 生成下一个标签页底色：与文件标签共用同一套 16 色轮转调色板，
    /// 让对比 / 终端标签在主题切换时也能跟随刷新。
    /// </summary>
    private IBrush NextTabColor(out int colorIndex)
    {
        colorIndex = _colorIndex % TabColors.Length;
        var brush = GetTabColor(_colorIndex);
        _colorIndex++;
        return brush;
    }

    /// <summary>
    /// 非文件标签（Git 对比 / 终端）的右键菜单：只提供「关闭」与「关闭其他标签」，
    /// 与文件标签保持同样的菜单项顺序与语言刷新行为。
    /// </summary>
    private MenuFlyout CreateSimpleTabContextMenu(TabItem tabItem)
    {
        var menu = new MenuFlyout();
        var closeItem = new MenuItem { Header = LangService.T("关闭 (_W)") };
        closeItem.Click += (_, _) => CloseTab(tabItem);
        var closeOthersItem = new MenuItem { Header = LangService.T("关闭其他标签") };
        closeOthersItem.Click += (_, _) => CloseOtherTabs(tabItem);

        menu.Items.Add(closeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(closeOthersItem);

        // 打开菜单前按当前界面语言刷新文本
        menu.Opening += (_, _) =>
        {
            closeItem.Header = LangService.T("关闭 (_W)");
            closeOthersItem.Header = LangService.T("关闭其他标签");
        };
        return menu;
    }

    /// <summary>构建标签页右键菜单（保存 / 另存为 / 打开文件位置 / 复制 / 关闭其他标签）。</summary>
    private MenuFlyout CreateTabContextMenu(EditorTab tab, TabItem tabItem)
    {
        var menu = new MenuFlyout();

        var saveItem = new MenuItem { Header = "保存 (_S)" };
        saveItem.Click += (_, _) => _pendingUiTask = SaveTabAsync(tab);

        var saveAsItem = new MenuItem { Header = "另存为 (_A)..." };
        saveAsItem.Click += (_, _) => _saveDialogTask = SaveAsTabAsync(tab);

        var duplicateItem = new MenuItem { Header = "复制文件" };
        duplicateItem.Click += (_, _) => DuplicateTab(tab);

        var openLocationItem = new MenuItem { Header = "打开文件位置" };
        openLocationItem.Click += (_, _) => OpenFileLocation(tab);

        var closeOthersItem = new MenuItem { Header = "关闭其他标签" };
        closeOthersItem.Click += (_, _) => CloseOtherTabs(tabItem);

        menu.Items.Add(saveItem);
        menu.Items.Add(saveAsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(duplicateItem);
        menu.Items.Add(openLocationItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(closeOthersItem);

        // 打开菜单前刷新语言文本 + 根据当前文件路径动态决定「打开文件位置」是否可用
        menu.Opening += (_, _) =>
        {
            saveItem.Header = LangService.T("保存 (_S)");
            saveAsItem.Header = LangService.T("另存为 (_A)...");
            duplicateItem.Header = LangService.T("复制文件");
            openLocationItem.Header = LangService.T("打开文件位置");
            closeOthersItem.Header = LangService.T("关闭其他标签");
            openLocationItem.IsEnabled = !string.IsNullOrEmpty(tab.FilePath);
        };

        return menu;
    }

    /// <summary>在系统文件管理器中打开文件所在目录（Windows 下同时选中该文件）。</summary>
    private void OpenFileLocation(EditorTab tab)
    {
        if (string.IsNullOrEmpty(tab.FilePath)) return;

        try
        {
            var path = tab.FilePath;
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", $"-R \"{path}\"");
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
            StatusText.Text = $"已打开文件位置：{path}";
        }
        catch (Exception ex)
        {
            ShowMessageBox($"打开文件位置失败：{ex.Message}", "错误", MessageBoxButtons.OK).ContinueWith(_ => { });
        }
    }

    /// <summary>复制当前标签页内容到一个新的未命名标签页。</summary>
    private void DuplicateTab(EditorTab tab)
    {
        var newTab = CreateNewTab();
        if (tab.Mode == EditorMode.RichText && tab.RichEditor?.Document != null)
        {
            var doc = tab.RichEditor.Document.Clone();
            var rich = GetOrCreateRichEditor(newTab);
            GetOrCreateImageViewer(newTab);
            rich.Document = doc;
            SetTabMode(newTab, EditorMode.RichText);
            newTab.TextEditor.Text = GetRichTextPlainText(tab);
        }
        else if (tab.Mode == EditorMode.ImageViewer && tab.RichEditor?.Document != null)
        {
            var doc = tab.RichEditor.Document.Clone();
            var viewer = GetOrCreateImageViewer(newTab);
            GetOrCreateRichEditor(newTab);
            viewer.Document = doc;
            SetTabMode(newTab, EditorMode.ImageViewer);
            newTab.TextEditor.Text = "";
        }
        else
        {
            newTab.TextEditor.Text = tab.TextEditor.Text;
        }
        newTab.Language = tab.Language;
        newTab.Encoding = tab.Encoding;
        newTab.IsBinary = tab.IsBinary;
        newTab.TextEditor.SyntaxHighlighting = tab.TextEditor.SyntaxHighlighting;
        newTab.IsLoaded = true;
        newTab.IsModified = true;
        UpdateTabHeader(newTab);
        UpdateTitle();
        StatusText.Text = $"已复制：{tab.FileName}";
    }

    /// <summary>关闭除指定标签页之外的所有其他标签页（遇到修改且用户取消时中断）。</summary>
    private async void CloseOtherTabs(TabItem currentTabItem)
    {
        FileTabControl.SelectedItem = currentTabItem;
        var toClose = new List<TabItem>();
        foreach (TabItem item in FileTabControl.Items)
        {
            if (item != currentTabItem)
            {
                toClose.Add(item);
            }
        }

        foreach (var item in toClose)
        {
            // 非文件标签（Git 对比 / 终端 / 工具标签）的 Tag 可能是 SimpleTabTag 包装，先取出真实对象
            var itemTag = item.Tag;
            if (itemTag is SimpleTabTag simpleItemTag) itemTag = simpleItemTag.View;

            // 非文件标签（对比 / 终端 / 工具标签）：无修改内容，统一走 CloseTab 收尾
            // （终端会先 ShutdownAsync 终止 shell / 断开 SSH 连接），不参与保存确认
            if (itemTag is not EditorTab tab)
            {
                CloseTab(item);
                continue;
            }

            if (tab.IsModified)
            {
                var result = await ShowMessageBox($"文件 \"{tab.FileName}\" 已修改，是否保存？", "确认",
                    MessageBoxButtons.YesNoCancel);
                if (result == MessageBoxResult.Yes)
                {
                    await SaveTabAsync(tab);
                    if (tab.IsModified) continue; // 保存失败则跳过该标签
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    return; // 用户取消，停止关闭其余标签
                }
            }

            tab.FileWatcher?.Dispose();
            tab.FileWatcher = null;
            tab.MatchHighlightTimer?.Stop();
            tab.MatchHighlightTimer = null;
            _tabs.Remove(tab);
            FileTabControl.Items.Remove(item);
        }

        // 仅当标签栏里真的一个标签都不剩时才补一个空白文件标签；
        // 若保留下来的是终端等非文件标签，就不再凭空新建文件标签
        if (FileTabControl.Items.Count == 0)
        {
            NewFile();
        }
    }

    // ========== File Operations ==========

    private void NewFile_Click(object? sender, RoutedEventArgs e) => NewFile();
    private EditorTab NewFile()
    {
        var tab = CreateNewTab();
        tab.Language = "C#";
        tab.TextEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        tab.IsLoaded = true;
        StatusText.Text = "New file created";
        UpdateTitle();
        UpdateFileStats(tab);
        return tab;
    }

    /// <summary>
    /// 查找可复用的空白标签页：尚未保存到磁盘（无 FilePath）且内容为空、未被修改。
    /// 供 agent 的 NewFile / CreateFile 工具去重使用。
    /// </summary>
    private EditorTab? FindReusableBlankTab()
    {
        foreach (var tab in _tabs)
        {
            if (!string.IsNullOrEmpty(tab.FilePath)) continue;   // 已保存到磁盘的不算空白
            if (tab.Mode != EditorMode.Text) continue;            // 富文本/图片模式不参与
            if (tab.IsModified) continue;                         // 用户或模型已写入内容
            if (!string.IsNullOrEmpty(tab.TextEditor.Text)) continue;
            return tab;
        }
        return null;
    }

    /// <summary>
    /// 本次 agent 会话中"已创建"的未保存标签页。
    /// 模型可能在多轮工具循环里反复请求新建（NewFile / CreateFile），
    /// 仅靠"当前是否空白"无法识别——第一次 InsertText 之后标签页就不再空白，
    /// 再次 NewFile 会新建第二个标签并重复写入同样内容，产生两个内容相同的文件。
    /// 因此记录已认领的标签页，在其被保存（获得 FilePath）或被关闭前始终复用。
    /// </summary>
    private EditorTab? _agentClaimedTab;

    /// <summary>
    /// 上一次 agent 写入内容后光标停留的位置。
    /// 用于区分"模型自己接着写（可安全整体重写）"与"用户手动移动过光标（应尊重其位置做插入）"。
    /// </summary>
    private int _agentLastCaretOffset;

    /// <summary>
    /// 返回 agent 应复用的标签页：优先取本次会话已认领且仍有效的标签页，
    /// 其次取任何现成的空白标签页。返回 null 表示需要真正新建。
    /// </summary>
    private EditorTab? FindAgentReusableTab()
    {
        // 已认领的标签页仍有效（还开着且是文本模式）→ 继续复用它。
        // 不限制是否有 FilePath：CreateFile 打开落地文件后也会认领该标签页，
        // 模型随后若再请求新建，应复用它而不是再开一个（否则又会出现重复文件）。
        if (_agentClaimedTab != null)
        {
            if (_tabs.Contains(_agentClaimedTab) && _agentClaimedTab.Mode == EditorMode.Text)
            {
                return _agentClaimedTab;
            }
            _agentClaimedTab = null;   // 已被关闭，释放认领
        }

        return FindReusableBlankTab();
    }

    /// <summary>
    /// 查找可被 CreateFile 新文件"取代"的占位标签页。
    /// 只允许两种情况，避免误关用户正在编辑的其他文件：
    /// 1) 本次会话认领的、且尚未落盘（无 FilePath）的标签页；
    /// 2) 任何真正空白的标签页（未落盘、无内容、未修改）。
    /// 承载了磁盘文件的标签页一律不返回——否则创建 b.cpp 会把 a.cpp 关掉。
    /// </summary>
    private EditorTab? FindConsumableTabForNewFile()
    {
        if (_agentClaimedTab != null &&
            _tabs.Contains(_agentClaimedTab) &&
            _agentClaimedTab.Mode == EditorMode.Text &&
            string.IsNullOrEmpty(_agentClaimedTab.FilePath))
        {
            return _agentClaimedTab;
        }
        return FindReusableBlankTab();
    }

    /// <summary>
    /// agent 的 NewFile 工具实现：优先复用本次会话已认领的标签页或现成空白标签页，否则才真正新建。
    /// 返回给模型的结果里带上当前文件名，使其知道文件已就绪、无需重复调用。
    /// </summary>
    private string AgentNewFile()
    {
        var existing = FindAgentReusableTab();
        if (existing != null)
        {
            _agentClaimedTab = existing;
            ActivateTab(existing);
            StatusText.Text = "已复用现有文档";
            return $"已复用现有文档（无需重复新建）：{existing.FileName}。" +
                   "内容已就绪，请勿再次调用 NewFile；如需追加内容请用 InsertText。";
        }

        var tab = NewFile();
        _agentClaimedTab = tab;
        _agentLastCaretOffset = -1;   // 新标签页无上次写入位置，避免误判为"模型续写"
        return $"已新建文件：{tab.FileName}。请勿重复调用本工具；如需写入内容请用 InsertText。";
    }

    /// <summary>
    /// agent 的 CreateFile 工具实现：按文件名一步创建文件（可选带初始内容），
    /// 自动按扩展名设置语法高亮；未指定目录时落在当前打开的文件夹下。
    /// 若同名文件已打开则复用该标签页，避免重复创建。
    /// </summary>
    private string AgentCreateFile(string fileName, string? content)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "创建失败：文件名不能为空。";

        fileName = fileName.Trim().Trim('"');

        // 解析目标路径：绝对路径直接用；相对路径落到当前打开的文件夹
        string targetPath;
        try
        {
            if (Path.IsPathRooted(fileName))
            {
                targetPath = Path.GetFullPath(fileName);
            }
            else
            {
                var baseDir = _folderPanel?.CurrentFolder;
                if (string.IsNullOrEmpty(baseDir))
                {
                    // 没有打开文件夹：无法确定落盘位置，退化为创建一个指定了名字的未保存标签页
                    return AgentCreateFileAsUnsavedTab(fileName, content);
                }
                targetPath = Path.GetFullPath(Path.Combine(baseDir, fileName));
            }
        }
        catch (Exception ex)
        {
            return $"创建失败：路径无效（{ex.Message}）。";
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(dir)) return "创建失败：无法确定目标目录。";

        // 同名文件已打开：直接激活并返回，不重复创建
        var alreadyOpen = _tabs.FirstOrDefault(t =>
            !string.IsNullOrEmpty(t.FilePath) &&
            string.Equals(t.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen != null)
        {
            ActivateTab(alreadyOpen);
            return $"该文件已打开，已切换到标签页：{Path.GetFileName(targetPath)}";
        }

        // 磁盘上已存在同名文件：不覆盖，改为打开它
        if (File.Exists(targetPath))
        {
            OpenFile(targetPath);
            return $"该文件已存在，已打开：{Path.GetFileName(targetPath)}（未覆盖原内容）";
        }

        try
        {
            Directory.CreateDirectory(dir);
            var text = content ?? "";
            File.WriteAllText(targetPath, text, new UTF8Encoding(false));

            // 记下可被本工具"消耗"的标签页：只有真正的空白标签或本次会话刚认领的未保存标签才可以
            // 被新文件取代。绝不能把已承载其他文件的标签页当作可清理对象（否则创建 b.cpp 会误关 a.cpp）。
            var staleTab = FindConsumableTabForNewFile();

            OpenFile(targetPath);

            // 新文件已打开，原先的空白/占位标签页不再需要；仅当它不是刚打开的这个标签时才关闭
            if (staleTab != null && !ReferenceEquals(staleTab, GetCurrentTab()))
            {
                CloseTabByTab(staleTab);
            }

            // 打开后把语言按扩展名设好（OpenFile 内部已按扩展名设置，这里兜底确保准确）
            var tab = GetCurrentTab();
            if (tab != null)
            {
                SetLanguageByExtension(Path.GetExtension(targetPath).ToLowerInvariant(), tab);
                tab.IsModified = false;
                UpdateTabHeader(tab);
                // 认领该标签页：模型后续若再请求新建，应复用它而不是再开一个
                _agentClaimedTab = tab;
            }

            StatusText.Text = $"已创建文件：{Path.GetFileName(targetPath)}";
            var sizeNote = text.Length == 0 ? "（空文件）" : $"（已写入 {text.Length} 个字符）";
            return $"已创建文件：{targetPath} {sizeNote}。语言已按扩展名设为 " +
                   $"{ExtensionToLanguage(Path.GetExtension(targetPath).ToLowerInvariant())}，" +
                   $"请勿重复调用本工具；如需修改内容请用 InsertText。";
        }
        catch (Exception ex)
        {
            return $"创建文件失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 未打开文件夹时的退化路径：创建一个指定了显示名的「未命名」标签页（不落盘，需用户另存为）。
    /// </summary>
    private string AgentCreateFileAsUnsavedTab(string fileName, string? content)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();

        // 未打开文件夹时无法落盘，只能建"未保存文档"。
        // 这里不做按扩展名的模糊查重（未保存标签页的 FileName 恒为"未命名"，扩展名不可靠），
        // 而是复用本次会话已认领的标签页/现成空白标签页，避免模型反复调用时产生多个重复文档。
        var existing = FindAgentReusableTab();
        if (existing != null)
        {
            _agentClaimedTab = existing;
            ActivateTab(existing);
            // 仅在尚未写入内容时补写，避免覆盖已有内容
            if (!string.IsNullOrEmpty(content) && string.IsNullOrEmpty(existing.TextEditor.Text))
            {
                existing.TextEditor.Text = content;
                existing.IsModified = true;
                UpdateTabHeader(existing);
            }
            SetLanguageByExtension(ext, existing);
            StatusText.Text = $"已复用未保存文档（目标文件名 {fileName}）";
            return $"已复用当前文档（目标文件名 {fileName}），未重复创建。请勿重复调用本工具；" +
                   $"请提示用户用「另存为」保存为 {fileName}。";
        }

        var tab = NewFile();
        _agentClaimedTab = tab;
        SetLanguageByExtension(ext, tab);
        if (!string.IsNullOrEmpty(content))
        {
            tab.TextEditor.Text = content;
            tab.IsModified = true;
            UpdateTabHeader(tab);
        }
        StatusText.Text = $"已创建未保存文档（{fileName}）——未打开文件夹，请用「另存为」保存为 {fileName}";
        return $"未打开文件夹，已创建未保存文档并设定语言为 {ExtensionToLanguage(ext)}。" +
               $"请提示用户用「另存为」保存为 {fileName}；请勿重复调用本工具。";
    }

    // 使用 void + 存储 Task 的字段防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        _fileDialogTask = OpenFileDialogAsync();
    }

    private async Task OpenFileDialogAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开文件",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("所有支持的文件")
                    {
                        Patterns = new[] { "*.txt", "*.bin", "*.cs", "*.c", "*.cpp", "*.cc", "*.h", "*.hpp", "*.csx", "*.csproj", "*.sln", "*.xaml", "*.json", "*.md", "*.markdown", "*.yaml", "*.yml", "*.go", "*.rs", "*.java", "*.js", "*.ts", "*.html", "*.htm", "*.css", "*.vue", "*.py", "*.proto", "*.nsi", "*.nsh" }
                    },
                    new FilePickerFileType("文本文件 (*.txt)") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("二进制文件 (*.bin)") { Patterns = new[] { "*.bin" } },
                    new FilePickerFileType("C# 文件 (*.cs)") { Patterns = new[] { "*.cs", "*.csx", "*.csproj" } },
                    new FilePickerFileType("C/C++ 文件 (*.c;*.cpp;*.cc;*.h;*.hpp;*.cu)") { Patterns = new[] { "*.c", "*.cpp", "*.cc", "*.h", "*.hpp", "*.cu" } },
                    new FilePickerFileType("Go 文件 (*.go)") { Patterns = new[] { "*.go" } },
                    new FilePickerFileType("Rust 文件 (*.rs)") { Patterns = new[] { "*.rs" } },
                    new FilePickerFileType("Java 文件 (*.java)") { Patterns = new[] { "*.java" } },
                    new FilePickerFileType("Python 文件 (*.py)") { Patterns = new[] { "*.py" } },
                    new FilePickerFileType("JavaScript 文件 (*.js)") { Patterns = new[] { "*.js" } },
                    new FilePickerFileType("TypeScript 文件 (*.ts)") { Patterns = new[] { "*.ts" } },
                    new FilePickerFileType("HTML 文件 (*.html;*.htm)") { Patterns = new[] { "*.html", "*.htm" } },
                    new FilePickerFileType("CSS 文件 (*.css)") { Patterns = new[] { "*.css" } },
                    new FilePickerFileType("Vue 文件 (*.vue)") { Patterns = new[] { "*.vue" } },
                    new FilePickerFileType("JSON 文件 (*.json)") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("XAML 文件 (*.xaml)") { Patterns = new[] { "*.xaml" } },
                    new FilePickerFileType("Markdown 文件 (*.md;*.markdown)") { Patterns = new[] { "*.md", "*.markdown" } },
                    new FilePickerFileType("YAML 文件 (*.yaml;*.yml)") { Patterns = new[] { "*.yaml", "*.yml" } },
                    new FilePickerFileType("Proto 文件 (*.proto)") { Patterns = new[] { "*.proto" } },
                    new FilePickerFileType("NSIS 脚本 (*.nsi;*.nsh)") { Patterns = new[] { "*.nsi", "*.nsh" } },
                    new FilePickerFileType("解决方案文件 (*.sln)") { Patterns = new[] { "*.sln" } },
                    new FilePickerFileType("所有文件 (*.*)") { Patterns = new[] { "*.*" } }
                }
            });

            foreach (var file in files)
            {
                OpenFile(file.Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenFileDialog error: {ex}");
        }
    }

    /// <summary>按路径打开文件或文件夹（供命令行参数、“打开方式 → TextFlowPlus”等外部入口调用）。</summary>
    public void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path)) OpenFolder(path);
            else if (File.Exists(path)) OpenFile(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenPath error: {ex}");
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            // 同一文件已打开：直接激活现有标签页，不重复创建
            try
            {
                string full = Path.GetFullPath(path);
                var existing = _tabs.FirstOrDefault(t =>
                    !string.IsNullOrEmpty(t.FilePath) &&
                    string.Equals(Path.GetFullPath(t.FilePath), full, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    ActivateTab(existing);
                    StatusText.Text = $"已打开：{Path.GetFileName(path)}";
                    return;
                }
            }
            catch { /* 路径规范化失败则继续走新建流程 */ }

            FileInfo fileInfo = new FileInfo(path);            if (fileInfo.Length > 500 * 1024 * 1024)
            {
                ShowMessageBox(
                    $"File \"{path}\" is larger than 500MB and cannot be opened.\nFile size: {fileInfo.Length / (1024 * 1024)} MB\nMaximum supported: 500 MB",
                    "File Too Large", MessageBoxButtons.OK).ContinueWith(_ => { });
                return;
            }

            string fileExtension = Path.GetExtension(path).ToLower();
            bool isBinary = fileExtension == ".bin";

            var tab = CreateNewTab();
            tab.FilePath = path;

            if (isBinary)
            {
                OpenBinaryFile(path, tab);
                FinishOpenFile(tab, fileInfo, path);
            }
            else if (RichDocumentService.IsRichDocument(fileExtension))
            {
                // 富文本文档（docx/docm/dotx/doc/dot/rtf）：异步加载，避免大文档卡 UI
                _pendingUiTask = OpenRichDocumentTabAsync(tab, path);
                return;
            }
            else if (RichDocumentService.IsImage(fileExtension))
            {
                // 图片文件：只读查看
                _pendingUiTask = OpenImageViewerTabAsync(tab, path);
                return;
            }
            else
            {
                try
                {
                    tab.Encoding = ReadFileWithEncodingDetection(path, tab.TextEditor);
                }
                catch (Exception readEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Read error: {readEx.Message}");
                    tab.TextEditor.Text = $"Error reading file: {readEx.Message}";
                }
                try
                {
                    SetLanguageByExtension(fileExtension, tab);
                }
                catch (Exception syntaxEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Syntax highlighting error: {syntaxEx.Message}");
                    tab.TextEditor.SyntaxHighlighting = null;
                }
                FinishOpenFile(tab, fileInfo, path);
            }
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Failed to open file: {ex.Message}", "Error", MessageBoxButtons.OK).ContinueWith(_ => { });
        }
    }

    /// <summary>打开文件收尾：状态、最近文件、文件监视、模式相关菜单刷新。远程标签跳过最近文件与文件监视。</summary>
    private void FinishOpenFile(EditorTab tab, FileInfo fileInfo, string path)
    {
        tab.IsLoaded = true;
        tab.IsModified = false;
        tab.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        UpdateTabHeader(tab);
        // 此刻 FilePath 与语法高亮都已确定，是判断"是否代码文件"并补装大括号折叠的正确时机
        EnsureBraceFolding(tab);
        FilePathText.Text = tab.FilePath ?? "";
        UpdateFileStats(tab);
        StatusText.Text = $"Opened file: {Path.GetFileName(path)}";
        if (string.IsNullOrEmpty(tab.RemotePath))
        {
            AddToRecentFiles(path);
            SetupFileWatcher(tab);
        }
        UpdateTitle();
        UpdateMenuStatesForTab(tab);
    }

    /// <summary>富文本文档标签页加载：.docx/.rtf 直接用对应序列化器；.doc/.dot 先转换到 docx 缓存。</summary>
    private async Task OpenRichDocumentTabAsync(EditorTab tab, string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLower();
            string loadPath = path;

            if (RichDocumentService.WordLegacyExts.Contains(ext))
            {
                if (!RichDocumentService.HasConverterAvailable())
                {
                    await ShowMessageBox(
                        $"无法打开 Word 97-2003 文档 (.{ext[1..]})：未检测到转换工具。\n\n" +
                        "请安装 LibreOffice（免费）或 Microsoft Word 后重试；\n" +
                        "也可以先用 Word“另存为 .docx”再打开。",
                        "缺少转换工具", MessageBoxButtons.OK);
                    CloseTabByTab(tab);
                    return;
                }
                StatusText.Text = $"正在转换 {Path.GetFileName(path)} …";
                var cachePath = RichDocumentService.GetDocCachePath(path);
                bool ok = await Task.Run(() => RichDocumentService.TryConvertToDocx(path, cachePath));
                if (!ok)
                {
                    await ShowMessageBox($"文档转换失败：{Path.GetFileName(path)}",
                        "转换失败", MessageBoxButtons.OK);
                    CloseTabByTab(tab);
                    return;
                }
                tab.DocCachePath = cachePath;
                loadPath = cachePath;
                StatusText.Text = $"正在加载 {Path.GetFileName(path)} …";
            }

            var doc = await RichDocumentService.LoadDocumentAsync(loadPath);
            var rich = GetOrCreateRichEditor(tab);
            GetOrCreateImageViewer(tab);   // 保证容器内顺序恒定
            rich.Document = doc;
            SetTabMode(tab, EditorMode.RichText);
            FinishOpenFile(tab, new FileInfo(path), path);
            StatusText.Text = $"已打开富文本文档：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Open rich doc error: {ex.Message}");
            string message = RichDocumentService.IsLicenseError(ex)
                ? RichDocumentService.LicenseErrorMessage
                : $"打开富文本文档失败：{ex.Message}\n\n该文件可能已损坏或格式不受支持。";
            await ShowMessageBox(message, "打开失败", MessageBoxButtons.OK);
            CloseTabByTab(tab);
        }
    }

    /// <summary>图片标签页：构建单图查看文档（只读）。</summary>
    private async Task OpenImageViewerTabAsync(EditorTab tab, string path)
    {
        try
        {
            var doc = await Task.Run(() => RichDocumentService.BuildImageViewerDocument(path));
            var viewer = GetOrCreateImageViewer(tab);
            GetOrCreateRichEditor(tab);   // 保证容器内顺序恒定
            viewer.Document = doc;
            SetTabMode(tab, EditorMode.ImageViewer);
            FinishOpenFile(tab, new FileInfo(path), path);
            StatusText.Text = $"已打开图片：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Open image error: {ex.Message}");
            await ShowMessageBox($"打开图片失败：{ex.Message}", "打开失败", MessageBoxButtons.OK);
            CloseTabByTab(tab);
        }
    }

    /// <summary>按 EditorTab 定位 TabItem 并关闭（加载失败时清理占位标签）。</summary>
    private void CloseTabByTab(EditorTab tab)
    {
        foreach (TabItem item in FileTabControl.Items)
        {
            if (item.Tag == tab)
            {
                CloseTab(item);
                return;
            }
        }
    }

    private System.Text.Encoding ReadFileWithEncodingDetection(string path, TextEditor editor)
    {
        byte[] bytes = File.ReadAllBytes(path);

        // UTF-8 with BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            editor.Text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return new UTF8Encoding(true);
        }
        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            editor.Text = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return System.Text.Encoding.Unicode;
        }
        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            editor.Text = System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return System.Text.Encoding.BigEndianUnicode;
        }
        // UTF-32 LE BOM
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            editor.Text = System.Text.Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
            return System.Text.Encoding.UTF32;
        }

        // Register code pages provider for GB2312/GBK
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        System.Text.Encoding gbEncoding = System.Text.Encoding.GetEncoding(936); // GB2312/GBK

        // Try UTF-8 (no BOM) first - most common for modern files
        // 用严格字节校验判断无 BOM UTF-8（不再使用“包含锟/拷等字即判乱码”的黑名单启发式：
        // 它们会把合法的 UTF-8 文件误判为 GBK，保存时被转码导致 vim 中文乱码）
        if (LooksLikeValidUtf8Bytes(bytes))
        {
            editor.Text = System.Text.Encoding.UTF8.GetString(bytes);
            return new UTF8Encoding(false);
        }

        // Try GB2312/GBK for ANSI encoded Chinese text
        try
        {
            string gbText = gbEncoding.GetString(bytes);
            if (HasChineseChars(gbText) && IsValidGb2312(bytes))
            {
                editor.Text = gbText;
                return gbEncoding;
            }
        }
        catch { }

        // ANSI 回退：使用系统 ANSI 代码页（中文 Windows = 936/GBK）。
        // 注意 .NET Core 上 Encoding.Default 恒为 UTF-8，直接用它做回退会导致再次转码。
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var ansiEncoding = System.Text.Encoding.GetEncoding(
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        editor.Text = ansiEncoding.GetString(bytes);
        return ansiEncoding;
    }

    /// <summary>严格校验字节序列是否为合法 UTF-8，且解码后大部分为可打印字符（用于无 BOM UTF-8 判定）。
    /// UTF-8Encoding(throwOnInvalidBytes: true) 会拒绝任何非法序列，避免把 GBK 文件误当 UTF-8；
    /// 同时不再对“锟/拷”等正常汉字做乱码黑名单判定，避免把合法 UTF-8 文件误判为 GBK。</summary>
    private static bool LooksLikeValidUtf8Bytes(byte[] bytes)
    {
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;   // 含非法 UTF-8 字节序列
        }

        // Check if text contains mostly printable characters
        int printableCount = 0;
        foreach (char c in text)
        {
            if (!char.IsControl(c))
                printableCount++;
        }
        return printableCount > text.Length * 0.85; // 85% should be printable
    }

    private static bool IsValidGb2312(byte[] bytes)
    {
        // Check for typical GB2312/GBK patterns
        // GB2312 lead byte range: 0x81-0xFE, trail byte: 0x40-0x7E or 0x80-0xFE
        int chineseCharCount = 0;
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] >= 0x81 && bytes[i] <= 0xFE)
            {
                byte trail = bytes[i + 1];
                if ((trail >= 0x40 && trail <= 0x7E) || (trail >= 0x80 && trail <= 0xFE))
                {
                    chineseCharCount++;
                    i++; // Skip trail byte
                }
            }
        }
        // If we found at least a few potential Chinese chars, consider it valid GB2312
        return chineseCharCount >= 2;
    }

    private static bool HasChineseChars(string text)
    {
        foreach (char c in text)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    private static void OpenBinaryFile(string path, EditorTab tab)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string hexContent = BitConverter.ToString(bytes);
        tab.TextEditor.Text = hexContent;
        tab.Language = "Binary";
        tab.IsBinary = true;
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try { System.Text.Encoding.UTF8.GetString(bytes); return true; }
        catch { return false; }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab == null) return;
        if (tab.Mode == EditorMode.ImageViewer)
        {
            StatusText.Text = "图片查看模式为只读，无法保存";
            return;
        }
        _pendingUiTask = SaveTabAsync(tab);
    }

    private async Task SaveTabAsync(EditorTab tab)
    {
        // 远程标签页：直接走远程回传保存（下载缓存 → 回传 SFTP）
        if (await SaveRemoteTabAsync(tab)) return;

        if (string.IsNullOrEmpty(tab.FilePath))
        {
            await SaveAsTabAsync(tab);
            return;
        }
        try
        {
            // 标记文件正在内部保存中
            _internalSavingFiles.Add(tab.FilePath);

            if (tab.IsBinary)
                SaveBinaryFile(tab.FilePath, tab);
            else if (tab.Mode != EditorMode.Text)
                await SaveRichDocumentToPathAsync(tab, tab.FilePath);
            else
                SaveWithEncoding(tab.FilePath, tab.Encoding, tab.TextEditor.Text);

            // 等待文件系统更新元数据后读取最新的 LastWriteTimeUtc
            await Task.Delay(100);
            var fileInfo = new FileInfo(tab.FilePath);
            tab.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;

            // 记录保存时间，用于忽略刚保存后 FileSystemWatcher 触发的事件
            _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;

            tab.IsModified = false;
            UpdateTabHeader(tab);
            StatusText.Text = LangService.TFormat("已保存文件：{0}", Path.GetFileName(tab.FilePath));
            // 本地文件保存后刷新 Git 修改列表（当前文件夹为 Git 仓库时）
            _folderPanel.RefreshGitChanges();
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"保存文件失败：{ex.Message}", "错误", MessageBoxButtons.OK);
        }
        finally
        {
            // 移除内部保存标记
            _internalSavingFiles.Remove(tab.FilePath);
        }
    }

    private void SaveAs_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab == null) return;
        if (tab.Mode == EditorMode.ImageViewer)
        {
            StatusText.Text = "图片查看模式为只读，无法另存为";
            return;
        }
        _saveDialogTask = SaveAsTabAsync(tab);
    }

    private void SaveAsTab(EditorTab tab)
    {
        _saveDialogTask = SaveAsTabAsync(tab);
    }

    private async Task SaveAsTabAsync(EditorTab tab)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                // 不再提供 Word 文档 (*.docx) 与 RTF (*.rtf)：保存对话框去掉 docx/doc/rtf 格式支持
                new FilePickerFileType("文本文件 (*.txt)") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("二进制文件 (*.bin)") { Patterns = new[] { "*.bin" } },
                new FilePickerFileType("C# 文件 (*.cs)") { Patterns = new[] { "*.cs", "*.csx", "*.csproj" } },
                new FilePickerFileType("C/C++ 文件 (*.c;*.cpp;*.cc;*.h;*.hpp;*.cu)") { Patterns = new[] { "*.c", "*.cpp", "*.cc", "*.h", "*.hpp", "*.cu" } },
                new FilePickerFileType("Go 文件 (*.go)") { Patterns = new[] { "*.go" } },
                new FilePickerFileType("Rust 文件 (*.rs)") { Patterns = new[] { "*.rs" } },
                new FilePickerFileType("Java 文件 (*.java)") { Patterns = new[] { "*.java" } },
                new FilePickerFileType("Python 文件 (*.py)") { Patterns = new[] { "*.py" } },
                new FilePickerFileType("JavaScript 文件 (*.js)") { Patterns = new[] { "*.js" } },
                new FilePickerFileType("TypeScript 文件 (*.ts)") { Patterns = new[] { "*.ts" } },
                new FilePickerFileType("HTML 文件 (*.html;*.htm)") { Patterns = new[] { "*.html", "*.htm" } },
                new FilePickerFileType("CSS 文件 (*.css)") { Patterns = new[] { "*.css" } },
                new FilePickerFileType("Vue 文件 (*.vue)") { Patterns = new[] { "*.vue" } },
                new FilePickerFileType("JSON 文件 (*.json)") { Patterns = new[] { "*.json" } },
                new FilePickerFileType("XAML 文件 (*.xaml)") { Patterns = new[] { "*.xaml" } },
                new FilePickerFileType("Markdown 文件 (*.md;*.markdown)") { Patterns = new[] { "*.md", "*.markdown" } },
                new FilePickerFileType("YAML 文件 (*.yaml;*.yml)") { Patterns = new[] { "*.yaml", "*.yml" } },
                new FilePickerFileType("Proto 文件 (*.proto)") { Patterns = new[] { "*.proto" } },
                new FilePickerFileType("NSIS 脚本 (*.nsi;*.nsh)") { Patterns = new[] { "*.nsi", "*.nsh" } },
                new FilePickerFileType("解决方案文件 (*.sln)") { Patterns = new[] { "*.sln" } },
                new FilePickerFileType("所有文件 (*.*)") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
        {
            try
            {
                // 标记文件正在内部保存中
                var filePath = file.Path.LocalPath;
                _internalSavingFiles.Add(filePath);

                string newExt = Path.GetExtension(filePath).ToLower();
                bool newIsRich = RichDocumentService.IsRichDocument(newExt);

                if (tab.IsBinary)
                {
                    SaveBinaryFile(file.Path.LocalPath, tab);
                }
                else if (newIsRich)
                {
                    // 目标为富文本文档：文本标签用纯文本构造 FlowDocument；富文本标签用当前文档
                    if (tab.Mode == EditorMode.Text)
                        await SavePlainTextAsRichDocumentAsync(filePath, tab.TextEditor.Text);
                    else
                        await SaveRichDocumentToPathAsync(tab, filePath);
                }
                else if (tab.Mode != EditorMode.Text)
                {
                    // 富文本文档另存为纯文本：仅保留文字内容（不含格式）
                    SaveWithEncoding(filePath, tab.Encoding, GetRichTextPlainText(tab));
                }
                else
                {
                    SaveWithEncoding(filePath, tab.Encoding, tab.TextEditor.Text);
                }

                tab.FilePath = filePath;

                // 按新扩展名切换编辑器模式（另存为切换文档类型）
                if (!tab.IsBinary && newExt != ".bin")
                {
                    if (newIsRich && tab.Mode != EditorMode.RichText)
                    {
                        var rich = GetOrCreateRichEditor(tab);
                        rich.Document = BuildDocumentFromPlainText(tab.TextEditor.Text);
                        SetTabMode(tab, EditorMode.RichText);
                    }
                    else if (!newIsRich && !RichDocumentService.IsImage(newExt) && tab.Mode != EditorMode.Text)
                    {
                        tab.TextEditor.Text = GetRichTextPlainText(tab);
                        SetTabMode(tab, EditorMode.Text);
                    }
                }

                // 等待文件系统更新元数据后读取最新的 LastWriteTimeUtc
                await Task.Delay(100);
                var fileInfo = new FileInfo(filePath);
                tab.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;

                // 记录保存时间，用于忽略刚保存后 FileSystemWatcher 触发的事件
                _lastInternalSaveTimes[filePath] = DateTime.UtcNow;

                tab.IsModified = false;
                UpdateTabHeader(tab);
                FilePathText.Text = tab.FilePath;
                StatusText.Text = $"Saved file: {Path.GetFileName(tab.FilePath)}";
                UpdateTitle();
                SetupFileWatcher(tab);   // 路径已变化，重新挂文件监视
                UpdateMenuStatesForTab(tab);
            }
            catch (Exception ex)
            {
                ShowMessageBox($"Failed to save file: {ex.Message}", "Error", MessageBoxButtons.OK).ContinueWith(_ => { });
            }
            finally
            {
                // 移除内部保存标记
                if (tab.FilePath != null)
                {
                    _internalSavingFiles.Remove(tab.FilePath);
                }
            }
        }
    }

    /// <summary>富文本文档保存到指定路径（.doc/.dot 先写 docx 缓存再转回原格式；.docx/.rtf 直接序列化）。</summary>
    private async Task SaveRichDocumentToPathAsync(EditorTab tab, string targetPath)
    {
        var doc = tab.RichEditor?.Document ?? throw new InvalidOperationException("富文本编辑器未初始化");
        string ext = Path.GetExtension(targetPath).ToLower();
        if (RichDocumentService.WordLegacyExts.Contains(ext) && !string.IsNullOrEmpty(tab.DocCachePath))
        {
            // 先更新 docx 转换缓存，再转换覆盖 .doc/.dot 目标
            await RichDocumentService.SaveDocumentAsync(doc, tab.DocCachePath);
            StatusText.Text = $"正在保存 {Path.GetFileName(targetPath)} …";
            bool ok = await Task.Run(() => RichDocumentService.TryConvertToLegacyDoc(tab.DocCachePath, targetPath));
            if (!ok)
            {
                throw new IOException(
                    $"无法将文档转换回 .{ext[1..]} 格式（未检测到 LibreOffice 或 Word）。\n" +
                    "内容已保留在缓存，可用“另存为 .docx”保存。");
            }
        }
        else
        {
            await RichDocumentService.SaveDocumentAsync(doc, targetPath);
        }
    }

    /// <summary>把纯文本保存为富文本文档（文本模式另存为 docx/rtf 时使用）。</summary>
    private async Task SavePlainTextAsRichDocumentAsync(string path, string text)
    {
        var doc = BuildDocumentFromPlainText(text);
        await RichDocumentService.SaveDocumentAsync(doc, path);
    }

    /// <summary>用纯文本构造 FlowDocument（默认段落，继承编辑器默认样式）。</summary>
    private static FlowDocument BuildDocumentFromPlainText(string text)
    {
        var doc = new FlowDocument();
        string? remaining = text?.Replace("\r\n", "\n") ?? "";
        string[] paras = remaining.Replace("\r", "\n").Split('\n');
        foreach (var para in paras)
        {
            var p = new Paragraph();
            p.Inlines.Add(new RichRun(para));
            doc.Blocks.Add(p);
        }
        if (doc.Blocks.Count == 0)
        {
            var p = new Paragraph();
            p.Inlines.Add(new RichRun(""));
            doc.Blocks.Add(p);
        }
        return doc;
    }

    /// <summary>取富文本文档的纯文本内容（不含格式；用于另存为文本/编码校验）。</summary>
    private static string GetRichTextPlainText(EditorTab tab)
    {
        var doc = tab.RichEditor?.Document;
        if (doc == null) return "";
        try
        {
            var snapshot = doc.EnsureTextDocument().CreateSnapshot();
            return snapshot.GetText(0, snapshot.TextLength).ToString();
        }
        catch
        {
            return "";
        }
    }

    private static void SaveBinaryFile(string path, EditorTab tab)
    {
        string hexText = tab.TextEditor.Text.Replace("-", "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
        byte[] bytes = new byte[hexText.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hexText.Substring(i * 2, 2), 16);
        File.WriteAllBytes(path, bytes);
    }

    private static void SaveWithEncoding(string path, System.Text.Encoding encoding, string text)
    {
        using var writer = new StreamWriter(path, append: false, encoding: encoding);
        writer.Write(text);
    }

    /// <summary>等待文件系统刷新元数据后读取最新的 LastWriteTimeUtc（非阻塞轮询，不卡 UI 线程）。</summary>
    private static DateTime GetFreshLastWriteTimeUtc(string path)
    {
        // 最多重试 5 次 × 10ms = 50ms，远短于 Thread.Sleep(100) 的 100ms 阻塞
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var fi = new FileInfo(path);
                fi.Refresh();
                if (fi.Exists) return fi.LastWriteTimeUtc;
            }
            catch { }
            System.Threading.Thread.SpinWait(10000); // ~微秒级自旋，不出让线程
        }
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch { return DateTime.UtcNow; }
    }

    private static System.Text.Encoding? _gb2312Encoding;
    private static System.Text.Encoding Gb2312Encoding
    {
        get
        {
            if (_gb2312Encoding == null)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                _gb2312Encoding = System.Text.Encoding.GetEncoding(936);
            }
            return _gb2312Encoding;
        }
    }

    private void SaveAsAnsi_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null) SaveFileWithEncoding(tab, Gb2312Encoding, "GB2312");
    }

    private void SaveAsUtf8_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null) SaveFileWithEncoding(tab, new UTF8Encoding(false), "UTF-8");
    }

    private void SaveAsUtf8Bom_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null) SaveFileWithEncoding(tab, new UTF8Encoding(true), "UTF-8 with BOM");
    }

    private void SaveFileWithEncoding(EditorTab tab, System.Text.Encoding encoding, string encodingName)
    {
        _saveDialogTask = SaveFileWithEncodingAsync(tab, encoding, encodingName);
    }

    private async Task SaveFileWithEncodingAsync(EditorTab tab, System.Text.Encoding encoding, string encodingName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"另存为 {encodingName} 编码",
            ShowOverwritePrompt = true,
            SuggestedFileName = !string.IsNullOrEmpty(tab.FileName) ? tab.FileName : "Untitled"
        });

        if (file != null)
        {
            try
            {
                SaveWithEncoding(file.Path.LocalPath, encoding, tab.TextEditor.Text);
                StatusText.Text = $"已使用 {encodingName} 编码保存到 {file.Path.LocalPath}";
            }
            catch (Exception ex)
            {
                ShowMessageBox($"保存文件失败：{ex.Message}", "错误", MessageBoxButtons.OK).ContinueWith(_ => { });
                StatusText.Text = "保存失败";
            }
        }
    }

    private async void ConvertToAnsi_Click(object? sender, RoutedEventArgs e) => await ConvertCurrentTabEncodingAsync(Gb2312Encoding, "GB2312");
    private async void ConvertToUtf8_Click(object? sender, RoutedEventArgs e) => await ConvertCurrentTabEncodingAsync(new UTF8Encoding(false), "UTF-8");
    private async void ConvertToUtf8Bom_Click(object? sender, RoutedEventArgs e) => await ConvertCurrentTabEncodingAsync(new UTF8Encoding(true), "UTF-8 with BOM");

    /// <summary>转换当前文件编码（远程标签页转码后回传 SFTP）。异步执行，避免 UI 线程阻塞。</summary>
    private async Task ConvertCurrentTabEncodingAsync(System.Text.Encoding targetEncoding, string encodingName)
    {
        var tab = GetCurrentTab();
        if (tab == null) return;
        if (string.IsNullOrEmpty(tab.TextEditor.Text))
        {
            StatusText.Text = "Current file is empty, cannot convert encoding";
            return;
        }
        if (string.IsNullOrEmpty(tab.FilePath))
        {
            await ShowMessageBox("Please save the file first before converting encoding.", "Warning", MessageBoxButtons.OK);
            return;
        }
        try
        {
            tab.Encoding = targetEncoding;
            SaveWithEncoding(tab.FilePath, targetEncoding, tab.TextEditor.Text);

            // 远程标签页：转码写回缓存后回传 SFTP（await 等待，不阻塞 UI、不 sync-over-async）
            if (IsRemoteTab(tab))
            {
                var remote = _remoteSftp;
                if (remote == null || !remote.IsConnected)
                {
                    await ShowMessageBox("远程连接已断开：编码已修改但未能上传，请重新连接后保存。",
                        "提示", MessageBoxButtons.OK);
                }
                else
                {
                    StatusText.Text = $"正在上传转码结果 {tab.RemotePath} ...";
                    await remote.UploadFileAsync(tab.FilePath, tab.RemotePath);
                }
            }

            tab.IsModified = false;
            UpdateTabHeader(tab);
            UpdateTitle();
            EncodingText.Text = $"Encoding: {GetEncodingName(tab.Encoding)}";
            StatusText.Text = $"Converted to {encodingName} encoding and saved";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"Encoding conversion failed: {ex.Message}", "Error", MessageBoxButtons.OK);
            StatusText.Text = "Encoding conversion failed";
        }
    }

    private async void Exit_Click(object? sender, RoutedEventArgs e)
    {
        var modifiedTabs = _tabs.Where(t => t.IsModified).ToList();
        if (modifiedTabs.Count > 0)
        {
            var result = await ShowMessageBox(
                $"有 {modifiedTabs.Count} 个文件已修改但未保存，是否保存？", "确认",
                MessageBoxButtons.YesNoCancel);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var tab in modifiedTabs)
                {
                    await SaveTabAsync(tab);
                    if (tab.IsModified) return;
                }
            }
            else if (result == MessageBoxResult.Cancel)
            {
                return;
            }
        }
        Close();
    }

    // ========== Recent Files ==========

    private string RecentFilePath
    {
        get
        {
            var appDir = Services.AppPaths.LocalAppDataDir;
            Directory.CreateDirectory(appDir);
            return Path.Combine(appDir, "recent_files.json");
        }
    }

    private void LoadRecentFiles()
    {
        try
        {
            var path = RecentFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _recentFiles = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
            }
        }
        catch { _recentFiles = new List<string>(); }
    }

    private void SaveRecentFiles()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_recentFiles);
            File.WriteAllText(RecentFilePath, json);
        }
        catch { }
    }

    private void AddToRecentFiles(string path)
    {
        _recentFiles.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > MaxRecentFiles)
            _recentFiles = _recentFiles.Take(MaxRecentFiles).ToList();
        SaveRecentFiles();
        RefreshRecentFilesMenu();
    }

    private void RefreshRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        if (_recentFiles.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
            return;
        }
        foreach (var filePath in _recentFiles)
        {
            var item = new MenuItem { Header = $"  {filePath}" };
            ToolTip.SetTip(item, filePath);
            item.Click += (s, e) => OpenRecentFile(filePath);
            RecentFilesMenu.Items.Add(item);
        }
        RecentFilesMenu.Items.Add(new Separator());
        var clearItem = new MenuItem { Header = "  Clear recent files" };
        clearItem.Click += (s, e) =>
        {
            _recentFiles.Clear();
            SaveRecentFiles();
            RefreshRecentFilesMenu();
        };
        RecentFilesMenu.Items.Add(clearItem);
    }

    private async void OpenRecentFile(string path)
    {
        if (File.Exists(path))
        {
            OpenFile(path);
        }
        else
        {
            var result = await ShowMessageBox(
                $"文件 \"{path}\" 不存在，是否从列表中移除？",
                "文件不存在", MessageBoxButtons.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                _recentFiles.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
                SaveRecentFiles();
                RefreshRecentFilesMenu();
            }
        }

        // 关闭文件菜单及其子菜单，确保点击最近文件后菜单收起
        CloseFileMenu();
    }

    private void CloseFileMenu()
    {
        // 从 RecentFilesMenu 向上遍历视觉树，找到所有祖先 MenuItem 并关闭它们的子菜单
        var current = RecentFilesMenu;
        while (current != null)
        {
            current.IsSubMenuOpen = false;
            current = current.Parent as MenuItem;
        }

        // 如果直接父级不是 MenuItem（可能是中间容器），遍历逻辑树查找
        var parent = RecentFilesMenu.Parent;
        while (parent != null && parent is not Menu and not Window)
        {
            if (parent is MenuItem mi)
            {
                mi.IsSubMenuOpen = false;
            }
            parent = (parent as Control)?.Parent;
        }
    }

    // ========== Edit Operations ==========

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { } rich)
        {
            if (rich.CanUndo) { rich.Undo(); StatusText.Text = "Undone"; }
            return;
        }
        var editor = GetCurrentTextEditor();
        if (editor != null && editor.CanUndo) { editor.Undo(); StatusText.Text = "Undone"; }
    }

    private void Redo_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { } rich)
        {
            if (rich.CanRedo) { rich.Redo(); StatusText.Text = "Redone"; }
            return;
        }
        var editor = GetCurrentTextEditor();
        if (editor != null && editor.CanRedo) { editor.Redo(); StatusText.Text = "Redone"; }
    }

    private async void Cut_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { } rich)
        {
            if (EditorActions.Cut.CanExecute(rich)) EditorActions.Cut.Execute(rich);
            return;
        }
        var editor = GetCurrentTextEditor();
        if (editor != null && !string.IsNullOrEmpty(editor.SelectedText))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var data = new Avalonia.Input.DataTransfer(); data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, editor.SelectedText)); await clipboard.SetDataAsync(data);
                editor.SelectedText = string.Empty;
                StatusText.Text = "Cut to clipboard";
                if (tab != null) { tab.IsModified = true; UpdateTabHeader(tab); UpdateTitle(); }
            }
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { } rich)
        {
            if (EditorActions.Copy.CanExecute(rich)) EditorActions.Copy.Execute(rich);
            return;
        }
        var editor = GetCurrentTextEditor();
        if (editor != null && !string.IsNullOrEmpty(editor.SelectedText))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var data = new Avalonia.Input.DataTransfer(); data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, editor.SelectedText)); await clipboard.SetDataAsync(data);
                StatusText.Text = "Copied to clipboard";
            }
        }
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { } rich)
        {
            if (EditorActions.Paste.CanExecute(rich)) EditorActions.Paste.Execute(rich);
            else await PasteTextIntoRichTextAsync(rich, tab);
            return;
        }
        var editor = GetCurrentTextEditor();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (editor != null && clipboard != null)
        {
            using var data = await clipboard.TryGetDataAsync(); var text = data != null ? await data.TryGetTextAsync() : null;
            if (text != null)
            {
                editor.SelectedText = text;
                StatusText.Text = "Pasted from clipboard";
                if (tab != null) { tab.IsModified = true; UpdateTabHeader(tab); UpdateTitle(); }
            }
        }
    }

    /// <summary>备用粘贴：官方 Paste 动作不可用时，把剪贴板纯文本插入富文本光标位置。</summary>
    private async Task PasteTextIntoRichTextAsync(Avalonia.Controls.RichTextEditor rich, EditorTab tab)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        using var data = await clipboard.TryGetDataAsync();
        var text = data != null ? await data.TryGetTextAsync() : null;
        if (string.IsNullOrEmpty(text)) return;

        var doc = rich.Document;
        if (doc == null) return;
        var td = doc.EnsureTextDocument();
        var (start, end) = GetSelectionOffsetRange(rich.Selection, td);
        var range = new TextRange(
            td.ContentStart.CreatePointer(start, RichLogicalDirection.Forward),
            td.ContentStart.CreatePointer(Math.Max(end, start), RichLogicalDirection.Forward));
        range.ReplaceText(text);
        tab.IsModified = true;
        UpdateTabHeader(tab);
        UpdateTitle();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { } rich)
        {
            rich.SelectAll();
            StatusText.Text = "Selected all";
            return;
        }
        var editor = GetCurrentTextEditor();
        editor?.SelectAll();
        StatusText.Text = "Selected all";
    }

    private void Find_Click(object? sender, RoutedEventArgs e)
    {
        // 复用已打开的对话框：避免 Ctrl+F 重复弹出多个窗口
        if (_findDialog == null || !_findDialog.IsVisible)
        {
            _findDialog = new FindDialog(this);
            _findDialog.Show(this);
        }
        else
        {
            _findDialog.Activate();
            _findDialog.FindTextBox.Focus();
        }
    }

    // ========== 富文本查找/替换（FindDialog / ReplaceDialog 共用） ==========

    /// <summary>当前标签页是否为富文本模式。</summary>
    public bool IsCurrentRichTextTab => GetCurrentTab() is { Mode: EditorMode.RichText };

    /// <summary>取当前富文本文档全文（纯文本，不含格式）。非富文本标签返回 null。</summary>
    public string? GetCurrentRichTextContent()
    {
        var tab = GetCurrentTab();
        if (tab is not { Mode: EditorMode.RichText } || tab.RichEditor?.Document == null) return null;
        return GetRichTextPlainText(tab);
    }

    /// <summary>取当前富文本文档全部字符数（用于防越界）。</summary>
    public int GetCurrentRichTextLength()
    {
        var tab = GetCurrentTab();
        if (tab?.RichEditor?.Document == null) return 0;
        try { return tab.RichEditor.Document.EnsureTextDocument().CreateSnapshot().TextLength; }
        catch { return 0; }
    }

    /// <summary>取当前富文本选中区文本（无选区/非富文本标签返回空串）。供替换对话框判断。</summary>
    public string GetCurrentRichTextSelectionText()
    {
        var tab = GetCurrentTab();
        var doc = tab?.RichEditor?.Document;
        if (doc == null) return "";
        try
        {
            var td = doc.EnsureTextDocument();
            var (start, end) = GetSelectionOffsetRange(tab!.RichEditor!.Selection, td);
            return end > start ? td.CreateSnapshot().GetText(start, end - start).ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>选中富文本 [start, start+length) 区间并聚焦。</summary>
    public void SelectRichTextRange(int start, int length, bool focus = true)
    {
        var tab = GetCurrentTab();
        var doc = tab?.RichEditor?.Document;
        if (doc == null || start < 0 || length <= 0) return;
        var td = doc.EnsureTextDocument();
        int total;
        try { total = td.CreateSnapshot().TextLength; } catch { return; }
        if (start >= total) return;
        int end = Math.Min(start + length, total);
        tab!.RichEditor!.Select(
            td.ContentStart.CreatePointer(start, RichLogicalDirection.Forward),
            td.ContentStart.CreatePointer(end, RichLogicalDirection.Forward));
        if (focus) tab.RichEditor.Focus();
    }

    /// <summary>替换富文本 [start, start+length) 区间为 replacement。返回是否成功。</summary>
    public bool ReplaceRichTextRange(int start, int length, string replacement)
    {
        var tab = GetCurrentTab();
        var doc = tab?.RichEditor?.Document;
        if (doc == null || start < 0 || length < 0 || replacement == null) return false;
        var td = doc.EnsureTextDocument();
        int total;
        try { total = td.CreateSnapshot().TextLength; } catch { return false; }
        if (start > total) return false;
        int end = Math.Min(start + length, total);
        var range = new TextRange(
            td.ContentStart.CreatePointer(start, RichLogicalDirection.Forward),
            td.ContentStart.CreatePointer(end, RichLogicalDirection.Forward));
        try { range.ReplaceText(replacement); }
        catch { return false; }
        tab.IsModified = true;
        UpdateTabHeader(tab);
        UpdateTitle();
        return true;
    }

    /// <summary>替换当前富文本选中区。无选区/非富文本标签返回 false。</summary>
    public bool ReplaceCurrentRichTextSelection(string replacement)
    {
        var tab = GetCurrentTab();
        var doc = tab?.RichEditor?.Document;
        if (doc == null || replacement == null) return false;
        var td = doc.EnsureTextDocument();
        var (start, end) = GetSelectionOffsetRange(tab!.RichEditor!.Selection, td);
        if (end <= start) return false;
        var range = new TextRange(
            td.ContentStart.CreatePointer(start, RichLogicalDirection.Forward),
            td.ContentStart.CreatePointer(end, RichLogicalDirection.Forward));
        try { range.ReplaceText(replacement); }
        catch { return false; }
        tab.IsModified = true;
        UpdateTabHeader(tab);
        UpdateTitle();
        return true;
    }

    private void GoToLine_Click(object? sender, RoutedEventArgs e)
    {
        var goToLineDialog = new GoToLineDialog(this);
        goToLineDialog.Show(this);
    }

    private void Replace_Click(object? sender, RoutedEventArgs e)
    {
        if (_replaceDialog == null || !_replaceDialog.IsVisible)
        {
            _replaceDialog = new ReplaceDialog(this);
            _replaceDialog.Show(this);
        }
        else
        {
            _replaceDialog.Activate();
            _replaceDialog.FindTextBox.Focus();
        }
    }

    private void ShowTabs_Click(object? sender, RoutedEventArgs e)
    {
        ShowTabsMenuItem.IsChecked = !ShowTabsMenuItem.IsChecked;
        var show = ShowTabsMenuItem.IsChecked;
        foreach (var tab in _tabs) tab.TextEditor.Options.ShowTabs = show;
    }

    private void ShowSpaces_Click(object? sender, RoutedEventArgs e)
    {
        ShowSpacesMenuItem.IsChecked = !ShowSpacesMenuItem.IsChecked;
        var show = ShowSpacesMenuItem.IsChecked;
        foreach (var tab in _tabs) tab.TextEditor.Options.ShowSpaces = show;
    }

    private void ShowEndOfLine_Click(object? sender, RoutedEventArgs e)
    {
        ShowEndOfLineMenuItem.IsChecked = !ShowEndOfLineMenuItem.IsChecked;
        var show = ShowEndOfLineMenuItem.IsChecked;
        foreach (var tab in _tabs) tab.TextEditor.Options.ShowEndOfLine = show;
    }

    /// <summary>列编辑：切换矩形（块）选择模式。开启后鼠标拖拽即可竖向选择多列文本并同时编辑。</summary>
    private void ColumnEdit_Click(object? sender, RoutedEventArgs e)
    {
        ColumnEditMenuItem.IsChecked = !ColumnEditMenuItem.IsChecked;
        var enabled = ColumnEditMenuItem.IsChecked;
        // 编辑器已默认开启 EnableRectangularSelection；列编辑模式 = 把矩形选择触发修饰键
        // 从默认 Alt 改为"无"，使普通鼠标拖拽即为矩形选择；关闭时恢复 Alt。
        SetBoxSelectionModifiers(enabled ? KeyModifiers.None : KeyModifiers.Alt);
        StatusText.Text = LangService.T(enabled
            ? "列编辑已开启：鼠标拖拽可竖向选择多列文本"
            : "列编辑已关闭");
    }

    /// <summary>设置矩形选择触发修饰键（AvaloniaEdit 为 private set，经反射调整）。</summary>
    private static void SetBoxSelectionModifiers(KeyModifiers modifiers)
    {
        try
        {
            var prop = typeof(HotkeyConfiguration).GetProperty(nameof(HotkeyConfiguration.BoxSelectionModifiers));
            prop?.SetValue(null, modifiers);
        }
        catch
        {
            // 反射失败时保持默认（Alt+拖拽），不影响程序运行
        }
    }

    private void WordWrap_Click(object? sender, RoutedEventArgs e)
    {
        WordWrapMenuItem.IsChecked = !WordWrapMenuItem.IsChecked;
        var wrap = WordWrapMenuItem.IsChecked;
        foreach (var tab in _tabs) tab.TextEditor.WordWrap = wrap;
    }

    // ========== 折叠菜单命令 ==========

    private void FoldCurrent_Click(object? sender, RoutedEventArgs e) => FoldCurrentBlock();
    private void UnfoldCurrent_Click(object? sender, RoutedEventArgs e) => UnfoldCurrentBlock();
    private void FoldAll_Click(object? sender, RoutedEventArgs e) => FoldAllBlocks();
    private void UnfoldAll_Click(object? sender, RoutedEventArgs e) => UnfoldAllBlocks();
    private void ToggleFoldAll_Click(object? sender, RoutedEventArgs e) => ToggleFoldAllBlocks();

    private void FilePathText_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null && !string.IsNullOrEmpty(tab.FilePath))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var data = new Avalonia.Input.DataTransfer();
                data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, tab.FilePath));
                clipboard.SetDataAsync(data).ContinueWith(_ => { });
                StatusText.Text = "文件路径已复制到剪贴板";
            }
        }
    }

    private void GoToStart_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { Document: { } } rich)
        {
            var td = rich.Document.EnsureTextDocument();
            var start = td.ContentStart;
            rich.Select(start, start);
            rich.Focus();
            StatusText.Text = "Export to document started";
            return;
        }
        var editor = GetCurrentTextEditor();
        if (editor != null)
        {
            editor.TextArea.Caret.Offset = 0;
            editor.TextArea.Caret.BringCaretToView();
            StatusText.Text = "Export to document started";
        }
    }

    private void GoToEnd_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab is { Mode: EditorMode.RichText } && tab.RichEditor is { Document: { } } rich)
        {
            var td = rich.Document.EnsureTextDocument();
            var end = td.ContentEnd;
            rich.Select(end, end);
            rich.Focus();
            StatusText.Text = "Export to document completed";
            return;
        }
        var editor = GetCurrentTextEditor();
        if (editor != null)
        {
            editor.TextArea.Caret.Offset = editor.Document.TextLength;
            editor.TextArea.Caret.BringCaretToView();
            StatusText.Text = "Export to document completed";
        }
    }

    // ========== Zoom ==========

    /// <summary>Ctrl+滚轮放大/缩小编辑器字体（鼠标悬停在当前编辑区时生效，普通滚轮保持滚动）。</summary>
    private void MainWindow_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        var tab = GetCurrentTab();
        if (tab == null) return;
        bool overText = tab.TextEditor != null && tab.TextEditor.Bounds.Contains(e.GetPosition(tab.TextEditor));
        bool overRich = tab.RichEditor != null && tab.RichEditor.IsVisible && tab.RichEditor.Bounds.Contains(e.GetPosition(tab.RichEditor));
        if (!overText && !overRich) return;
        e.Handled = true;
        double step = e.Delta.Y > 0 ? 0.1 : -0.1;
        double newZoom = Math.Clamp(currentZoom + step, 0.5, 2.0);
        if (Math.Abs(newZoom - currentZoom) < 0.001) return;
        currentZoom = newZoom;
        ApplyZoom();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        if (currentZoom < 2.0) { currentZoom += 0.1; ApplyZoom(); }
    }

    private void ZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        if (currentZoom > 0.5) { currentZoom -= 0.1; ApplyZoom(); }
    }

    private void ZoomReset_Click(object? sender, RoutedEventArgs e)
    {
        currentZoom = 1.0; ApplyZoom();
    }

    private void ApplyZoom()
    {
        double baseSize;
        if (FontSizeComboBox.SelectedItem is ComboBoxItem cbi && double.TryParse(cbi.Content?.ToString(), out double size))
            baseSize = size;
        else
            baseSize = 16;
        foreach (var tab in _tabs)
        {
            tab.TextEditor.FontSize = baseSize * currentZoom;
            if (tab.RichEditor != null) tab.RichEditor.FontSize = baseSize * currentZoom;
        }
        UpdateZoomDisplay();
    }

    private void UpdateZoomDisplay()
    {
        ZoomText.Text = $"{(int)(currentZoom * 100)}%";
    }

    // ========== Font ==========

    private void FontFamilyComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tabs == null || _tabs.Count == 0) return;
        if (FontFamilyComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            string fontName = item.Content.ToString()!;
            foreach (var tab in _tabs)
            {
                tab.TextEditor.FontFamily = new FontFamily(fontName);
                if (tab.RichEditor != null) tab.RichEditor.FontFamily = new FontFamily(fontName);
            }
        }
    }

    private void FontSizeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 主界面工具栏字体大小联动已打开的 SSH 终端（未打开文档时也生效）
        double baseSize = 16;
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem cbi && double.TryParse(cbi.Content?.ToString(), out double sz))
            baseSize = sz;
        int terminalSize = (int)Math.Round(baseSize * currentZoom);
        // 联动独立 SSH 窗口
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var w in desktop.Windows)
            {
                if (w is SshTerminalDialog ssh) ssh.SetTerminalFontSize(terminalSize);
            }
        }
        // 联动主窗口 Tab 中的 SSH 终端
        if (FileTabControl != null)
        {
            foreach (TabItem item in FileTabControl.Items)
            {
                if (item.Tag is SshTerminalView sshView) sshView.SetTerminalFontSize(terminalSize);
            }
        }
        if (_tabs == null || _tabs.Count == 0) return;
        ApplyZoom();
    }

    // ========== 字体颜色 ==========

    /// <summary>初始化字体颜色面板：填充预设色板并显示当前颜色（默认黑色）</summary>
    private void InitFontColorPanel()
    {
        FontColorGrid.ItemsSource = FontColorPalette
            .Select(p => new SolidColorBrush(p.Color))
            .ToArray();
        UpdateFontColorDisplay(Colors.Black);
    }

    /// <summary>工具栏-字体颜色按钮：开/关颜色面板</summary>
    private void FontColorButton_Click(object? sender, RoutedEventArgs e)
    {
        FontColorPopup.IsOpen = !FontColorPopup.IsOpen;
    }

    /// <summary>色板点击：给当前编辑器中选中的文字应用颜色（代码/富文本）并收起面板</summary>
    private void FontColorSwatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SolidColorBrush brush })
        {
            ApplyFontColor(brush.Color);
            FontColorPopup.IsOpen = false;
        }
    }

    /// <summary>应用字体颜色：给当前编辑器中选中的文字着色（代码编辑器用着色器，富文本用 TextRange）。
    /// 无选区时提示先选中文字。</summary>
    private void ApplyFontColor(Color color)
    {
        var tab = GetCurrentTab();
        if (tab == null) return;
        var brush = new SolidColorBrush(color);
        UpdateFontColorDisplay(color);

        if (tab.Mode == EditorMode.RichText && tab.RichEditor is { } rich && rich.Document != null)
        {
            var td = rich.Document.EnsureTextDocument();
            var (start, end) = GetSelectionOffsetRange(rich.Selection, td);
            if (end <= start)
            {
                StatusText.Text = "请先选中要设置颜色的文字";
                return;
            }
            var range = new TextRange(
                td.ContentStart.CreatePointer(start, RichLogicalDirection.Forward),
                td.ContentStart.CreatePointer(end, RichLogicalDirection.Forward));
            range.ApplyPropertyValue(RichTextElement.ForegroundProperty, brush);
            tab.IsModified = true;
            UpdateTabHeader(tab);
            UpdateTitle();
            StatusText.Text = "字体颜色：已为选中文字设置为 " + GetColorName(color);
            return;
        }

        int selStart = tab.TextEditor.SelectionStart;
        int selLen = tab.TextEditor.SelectionLength;
        if (selLen <= 0)
        {
            StatusText.Text = "请先选中要设置颜色的文字";
            return;
        }
        tab.FontColorizer?.SetRange(selStart, selLen, brush);
        tab.TextEditor.TextArea.TextView.Redraw();
        StatusText.Text = "字体颜色：已为选中文字设置为 " + GetColorName(color);
    }

    /// <summary>更新按钮色条与面板当前颜色显示</summary>
    private void UpdateFontColorDisplay(Color color)
    {
        FontColorSwatch.Background = new SolidColorBrush(color);
        CurrentColorPreview.Background = new SolidColorBrush(color);
        CurrentColorText.Text = GetColorName(color);
    }

    /// <summary>颜色 → 中文名（预设之外的返回十六进制值）</summary>
    private static string GetColorName(Color color)
    {
        foreach (var p in FontColorPalette)
        {
            if (p.Color == color) return p.Name;
        }
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    // ========== Code Formatting ==========

    private void FormatCode_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null) FormatCode(tab);
    }

    /// <summary>按文档类型执行格式化：工具栏 / Ctrl+K / 右键菜单 / agent 本地命令共用入口，返回结果描述文本。</summary>
    private string FormatCode(EditorTab tab)
    {
        try
        {
            // 标记文件正在内部修改中
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                _internalSavingFiles.Add(tab.FilePath);
            }

            // 按文件扩展名识别文档类型（格式化命令按当前文件扩展名执行对应类型格式化：
            // 本地文件取真实扩展名；远程文件取缓存文件名（保留原扩展名））。
            // 扩展名未知/无扩展名时退回当前高亮语言
            string? currentLanguage = null;
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                var byExt = ExtensionToLanguage(Path.GetExtension(tab.FilePath));
                if (byExt != "PlainText") currentLanguage = byExt;
            }
            if (string.IsNullOrEmpty(currentLanguage))
            {
                var byHighlight = GetTabLanguage(tab);
                if (!string.IsNullOrEmpty(byHighlight) && byHighlight != "PlainText") currentLanguage = byHighlight;
            }
            if (string.IsNullOrEmpty(currentLanguage)) currentLanguage = "PlainText";

            if (string.IsNullOrWhiteSpace(tab.TextEditor.Text))
            {
                StatusText.Text = "文档为空，无需格式化";
                return "📄 当前文档为空，无需格式化。";
            }

            string? formattedText = FormatByLanguage(currentLanguage, tab.TextEditor.Text);
            if (formattedText != null)
            {
                tab.TextEditor.Text = formattedText;
                // 更新最后写入时间，避免触发外部修改提示
                if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
                {
                    tab.LastWriteTimeUtc = GetFreshLastWriteTimeUtc(tab.FilePath);
                    // 记录修改时间，用于忽略刚修改后 FileSystemWatcher 触发的事件
                    _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;
                }
                tab.IsModified = true;
                UpdateTabHeader(tab);
                UpdateTitle();
                StatusText.Text = $"已按 {currentLanguage} 格式完成格式化";
                return $"✅ 已按 {currentLanguage} 格式完成格式化。";
            }

            StatusText.Text = $"当前文档类型（{currentLanguage}）暂不支持自动格式化";
            return $"⏭ 当前文档类型（{currentLanguage}）暂不支持自动格式化，可通过“语言”菜单指定语言后重试。";
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Formatting failed: {ex.Message}", "Error", MessageBoxButtons.OK).ContinueWith(_ => { });
            StatusText.Text = "Formatting failed";
            return $"❌ 格式化失败：{ex.Message}";
        }
        finally
        {
            // 移除内部修改标记
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                _internalSavingFiles.Remove(tab.FilePath);
            }
        }
    }

    /// <summary>根据语言名执行格式化，返回格式化后的文本；不支持返回 null。</summary>
    private static string? FormatByLanguage(string language, string text) => language switch
    {
        "JSON" => FormatJson(text),
        "XAML" or "XML" => FormatXaml(text),
        "Markdown" => FormatMarkdown(text),
        "YAML" => FormatYaml(text),
        "C#" or "C++" or "Go" or "Rust" or "Java" or "Proto" => FormatCStyleCode(text),
        "JavaScript" or "TypeScript" => FormatCStyleCode(text),
        "Python" => FormatPythonCode(text),
        "HTML" => FormatHtml(text),
        "CSS" => FormatCss(text),
        "Vue" => FormatVue(text),
        "NSIS" => FormatNsis(text),
        _ => null
    };

    /// <summary>扩展名 → 语言名（打开文件高亮、格式化命令按扩展名分发共用；未知返回 PlainText）。</summary>
    private static string ExtensionToLanguage(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" or ".hh" or ".hxx" or ".ipp" or ".inl" or ".cu" or ".cuh" => "C++",
            ".csx" or ".csproj" or ".sln" or ".xaml" => "XAML",
            ".json" => "JSON",
            ".xml" => "XML",
            ".md" or ".markdown" => "Markdown",
            ".yaml" or ".yml" => "YAML",
            ".go" => "Go",
            ".rs" => "Rust",
            ".java" => "Java",
            ".js" or ".jsx" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript",
            ".html" or ".htm" => "HTML",
            ".css" => "CSS",
            ".vue" => "Vue",
            ".py" => "Python",
            ".proto" => "Proto",
            ".nsi" or ".nsh" => "NSIS",
            ".log" => "Log",
            _ => "PlainText"
        };
    }

    /// <summary>
    /// 判定扩展名是否属于"代码文件"（用于决定是否启用大括号折叠）。
    /// 只有以 {} 组织代码块的语言才返回 true：XML/YAML/Markdown/纯文本等排除在外，
    /// 避免在配置文件、文档里冒出无意义的折叠标记。JSON 也启用（同时按 {} 与 [] 折叠）。
    /// 注意 .xaml/.csproj/.sln 归入 XAML，属于代码/标记混合，这里一并启用。
    /// </summary>
    private static bool IsCodeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        return extension.ToLowerInvariant() switch
        {
            ".json" => true,
            ".py" => true,
            ".cs" => true,
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" or ".hh" or ".hxx"
                or ".ipp" or ".inl" or ".cu" or ".cuh" => true,
            ".java" => true,
            ".js" or ".jsx" => true,
            ".ts" or ".tsx" => true,
            ".go" => true,
            ".rs" => true,
            ".css" => true,          // CSS 规则块：选择器 { ... }
            ".vue" => true,          // Vue SFC 内含 script/style 块
            ".proto" => true,        // message/enum 定义块
            ".csx" or ".csproj" or ".sln" or ".xaml" => true,
            ".nsi" or ".nsh" => true, // NSIS 脚本：Section/Function 块
            _ => false               // .json/.xml/.yaml/.md/.html/.py/.txt 等不启用
        };
    }

    /// <summary>
    /// 判定标签页是否应启用大括号折叠：已知扩展名走代码文件白名单；
    /// 未命名标签页（无扩展名）按当前语法高亮语言兜底判断，便于用户手动切到 C#/C++ 后也能折叠。
    /// </summary>
    private static bool ShouldEnableFolding(EditorTab tab)
    {
        if (!string.IsNullOrEmpty(tab.FilePath))
            return IsCodeExtension(Path.GetExtension(tab.FilePath));

        // 未保存的新文档：看用户设置的语法高亮语言
        var lang = tab.Language ?? tab.TextEditor.SyntaxHighlighting?.Name;
        return lang switch
        {
            "C#" or "C++" or "Java" or "JavaScript" or "TypeScript" or "Go" or "Rust"
                or "CSS" or "Vue" or "Proto" or "XAML" or "NSIS" or "JSON" or "Python" => true,
            _ => false
        };
    }

    /// <summary>agent 本地命令"格式化"入口：返回结果文本供面板显示。</summary>
    private string FormatCurrentDocumentForAgent()
    {
        var tab = GetCurrentTab();
        return tab == null ? "当前没有打开的文档，无法格式化。" : FormatCode(tab);
    }

    // ========== agent 格式化（本地格式化器 + 大模型回退） ==========

    /// <summary>判定当前文档的语言名（扩展名优先，其次当前语法高亮；与格式化命令同一判定口径）。</summary>
    private string ResolveCurrentLanguage(EditorTab tab)
    {
        string? language = null;
        if (!string.IsNullOrEmpty(tab.FilePath))
        {
            var byExt = ExtensionToLanguage(Path.GetExtension(tab.FilePath));
            if (byExt != "PlainText") language = byExt;
        }
        if (string.IsNullOrEmpty(language))
        {
            var byHighlight = GetTabLanguage(tab);
            if (!string.IsNullOrEmpty(byHighlight) && byHighlight != "PlainText") language = byHighlight;
        }
        return language ?? "PlainText";
    }

    /// <summary>
    /// 本地格式化器是否支持当前文档类型（供 agent 决定是否回退到"大模型格式化"）。
    /// 纯文本/无扩展名的新文件本地无法格式化，交给模型按语言规范处理。
    /// </summary>
    private bool CanFormatCurrentDocumentLocally()
    {
        var tab = GetCurrentTab();
        if (tab == null || tab.Mode != EditorMode.Text) return false;
        return FormatByLanguage(ResolveCurrentLanguage(tab), " ") != null;
    }

    /// <summary>
    /// 用大模型返回的排版结果替换当前文档内容（供 agent 的"大模型格式化"回退路径调用）。
    /// 与文档编辑共用写回逻辑：单次撤销、保留光标、标记修改状态。
    /// </summary>
    private string ApplyAgentFormattedText(string formatted)
    {
        var tab = GetCurrentTab();
        if (tab == null) return "❌ 当前没有打开的文档。";
        return ApplyAgentDocumentEdits(new[]
        {
            new DocumentEdit { Kind = DocumentEditKind.ReplaceDocument, NewText = formatted, Reason = "按文件类型重新排版" }
        });
    }

    // ========== agent 文档编辑（大模型读写当前打开文档） ==========

    /// <summary>agent 可编辑的文档上下文（供大模型判断文档类型并定位修改范围）。</summary>
    public class AgentDocumentContext
    {
        /// <summary>是否成功取到可编辑文档。</summary>
        public bool Available { get; set; }

        /// <summary>不可编辑时的原因（供面板提示）。</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>文件名（含扩展名）。</summary>
        public string FileName { get; set; } = "";

        /// <summary>完整路径（未保存的新文件为空）。</summary>
        public string FilePath { get; set; } = "";

        /// <summary>文件扩展名（含点，小写）。</summary>
        public string Extension { get; set; } = "";

        /// <summary>识别出的语言名（JSON/C#/Markdown 等；未知为 PlainText）。</summary>
        public string Language { get; set; } = "PlainText";

        /// <summary>文档模式：Text / RichText / ImageViewer。</summary>
        public string Mode { get; set; } = "Text";

        /// <summary>总行数。</summary>
        public int LineCount { get; set; }

        /// <summary>总字符数。</summary>
        public int CharCount { get; set; }

        /// <summary>是否为远程文件标签页。</summary>
        public bool IsRemote { get; set; }

        /// <summary>文档是否已被截断（超长文档只把前 N 行交给模型）。</summary>
        public bool Truncated { get; set; }

        /// <summary>交给模型的文档正文（可能为带行号前缀的形式）。</summary>
        public string Content { get; set; } = "";
    }

    /// <summary>交给大模型的文档最大行数（超出部分仅做整体分析，不参与精确行号编辑）。</summary>
    private const int AgentDocMaxLines = 400;

    /// <summary>交给大模型的文档最大字符数（防止超出小模型上下文）。</summary>
    private const int AgentDocMaxChars = 12000;

    /// <summary>
    /// 取当前文档的 agent 编辑上下文（UI 线程调用）。
    /// 文档正文以"行号前缀"形式提供，便于模型给出准确的行号定位。
    /// </summary>
    private AgentDocumentContext GetCurrentDocumentContextForAgent()
    {
        var ctx = new AgentDocumentContext();
        var tab = GetCurrentTab();
        if (tab == null)
        {
            ctx.UnavailableReason = "当前没有打开的文档。";
            return ctx;
        }
        if (tab.Mode == EditorMode.ImageViewer)
        {
            ctx.UnavailableReason = "当前标签页是图片查看模式，不支持文本编辑。";
            return ctx;
        }

        ctx.Available = true;
        ctx.FileName = tab.FileName;
        ctx.FilePath = tab.FilePath ?? "";
        ctx.Extension = string.IsNullOrEmpty(tab.FilePath)
            ? ""
            : Path.GetExtension(tab.FilePath).ToLowerInvariant();
        ctx.Mode = tab.Mode.ToString();
        ctx.IsRemote = IsRemoteTab(tab);

        // 语言：扩展名优先，其次当前高亮语言（与格式化命令同一判定口径）
        var language = string.IsNullOrEmpty(ctx.Extension) ? "PlainText" : ExtensionToLanguage(ctx.Extension);
        if (language == "PlainText")
        {
            var byHighlight = GetTabLanguage(tab);
            if (!string.IsNullOrEmpty(byHighlight)) language = byHighlight;
        }
        ctx.Language = language;

        var text = tab.Mode == EditorMode.RichText
            ? GetRichTextPlainText(tab)
            : (tab.TextEditor?.Text ?? "");

        ctx.CharCount = text.Length;
        ctx.LineCount = CountTextLines(text);

        // 带行号前缀交给模型：模型能直接引用行号，避免"靠内容猜位置"导致误改
        var (content, truncated) = BuildNumberedContent(text);
        ctx.Content = content;
        ctx.Truncated = truncated;
        return ctx;
    }

    /// <summary>统计文本行数（末行无换行符也算一行；空文本为 0 行）。</summary>
    private static int CountTextLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        if (text[^1] == '\n') count--;
        return count;
    }

    /// <summary>把文档正文转换为带行号前缀的形式，并按行列上限截断。</summary>
    private static (string Content, bool Truncated) BuildNumberedContent(string text)
    {
        if (string.IsNullOrEmpty(text)) return ("（当前文档为空）", false);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        int total = lines.Length;
        if (total > 0 && lines[^1].Length == 0 && normalized.EndsWith("\n", StringComparison.Ordinal))
            total--;   // 末尾换行不产生额外空行

        var sb = new StringBuilder(Math.Min(text.Length + total * 6, AgentDocMaxChars + 512));
        bool truncated = false;
        for (int i = 0; i < total; i++)
        {
            if (i >= AgentDocMaxLines || sb.Length >= AgentDocMaxChars)
            {
                truncated = true;
                break;
            }
            sb.Append(i + 1).Append("│ ").Append(lines[i]).Append('\n');
        }
        return (sb.ToString().TrimEnd('\n'), truncated);
    }

    /// <summary>
    /// 应用大模型下发的文档编辑动作（UI 线程调用）。
    /// 全部动作执行完毕后一次性写回编辑器（单次撤销单元，用户可 Ctrl+Z 整体撤销），
    /// 并保持光标位置尽量不跳动。
    /// </summary>
    private string ApplyAgentDocumentEdits(IReadOnlyList<DocumentEdit> edits)
    {
        var tab = GetCurrentTab();
        if (tab == null) return "❌ 当前没有打开的文档，无法修改。";
        if (tab.Mode == EditorMode.ImageViewer) return "❌ 当前标签页是图片查看模式，不支持文本编辑。";
        if (edits == null || edits.Count == 0) return "⏭ 没有需要应用的修改。";

        try
        {
            if (!string.IsNullOrEmpty(tab.FilePath)) _internalSavingFiles.Add(tab.FilePath);

            bool isRich = tab.Mode == EditorMode.RichText;
            var editor = tab.TextEditor;
            string original = isRich ? GetRichTextPlainText(tab) : editor.Text;

            var result = DocumentEditApplier.Apply(original, edits);

            var sb = new StringBuilder();
            foreach (var line in result.Details) sb.AppendLine(line);

            if (!result.Changed)
            {
                StatusText.Text = "agent 未对文档做出改动";
                sb.Insert(0, "⚠️ 没有产生实际改动：\n");
                return sb.ToString().TrimEnd();
            }

            // 写回文档：替换前记录光标偏移，写回后按比例/边界钳制恢复，避免跳回文件头
            if (isRich)
            {
                ReplaceRichTextDocument(tab, result.Text);
            }
            else
            {
                int caret = editor.CaretOffset;
                editor.Document.Text = result.Text;
                editor.CaretOffset = Math.Min(caret, editor.Document.TextLength);
                editor.TextArea.Caret.BringCaretToView();
                editor.Focus();
            }

            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
            {
                tab.LastWriteTimeUtc = GetFreshLastWriteTimeUtc(tab.FilePath);
                _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;
            }
            tab.IsModified = true;
            UpdateTabHeader(tab);
            UpdateTitle();
            UpdateFileStats(tab);

            StatusText.Text = $"agent 已修改文档（{result.AppliedCount} 处改动）";
            var header = $"✅ 已修改当前文档：成功 {result.AppliedCount} 项";
            if (result.FailedCount > 0) header += $"，{result.FailedCount} 项未应用";
            header += "（可用 Ctrl+Z 整体撤销）\n";
            sb.Insert(0, header);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"agent 修改文档失败：{ex.Message}";
            return $"❌ 修改文档失败：{ex.Message}";
        }
        finally
        {
            if (!string.IsNullOrEmpty(tab.FilePath)) _internalSavingFiles.Remove(tab.FilePath);
        }
    }

    /// <summary>用纯文本整体替换富文本文档内容（段落结构由 BuildDocumentFromPlainText 统一构造）。</summary>
    private void ReplaceRichTextDocument(EditorTab tab, string text)
    {
        var rich = tab.RichEditor;
        if (rich?.Document == null) return;

        var rebuilt = BuildDocumentFromPlainText(text);
        // FlowDocument.Blocks 需整体替换：先清空再逐段迁入（保持与打开文件一致的段落结构）
        rich.Document.Blocks.Clear();
        foreach (var block in rebuilt.Blocks.ToList())
        {
            rebuilt.Blocks.Remove(block);
            rich.Document.Blocks.Add(block);
        }
        rich.Focus();
    }

    /// <summary>
    /// agent 本地命令"保存"入口：有修改且已有路径 → 直接静默保存（不弹任何窗口）；
    /// 新文件（无路径）→ 才打开保存窗口；无修改 → 提示无需保存。返回结果文本供面板显示。
    /// 保存核心逻辑与 SaveTabAsync 保持一致（含 FileSystemWatcher 抑制与元数据刷新），
    /// 但失败时不弹模态错误框，由返回文本提示。
    /// </summary>
    private string SaveCurrentDocumentForAgent()
    {
        var tab = GetCurrentTab();
        if (tab == null) return "当前没有打开的文件，无需保存。";

        if (string.IsNullOrEmpty(tab.FilePath))
        {
            // 新文件：打开保存窗口（保持 Task 引用，防止 macOS 原生对话框回调被 GC 导致崩溃）
            _saveDialogTask = SaveAsTabAsync(tab);
            return "📝 当前是新文件，已打开保存窗口，请选择保存位置。";
        }

        if (!tab.IsModified)
            return "✅ 文档没有未保存的修改，无需保存。";

        // 远程标签页：写回缓存并上传 SFTP
        if (IsRemoteTab(tab))
        {
            try
            {
                var remote = _remoteSftp;
                if (remote == null || !remote.IsConnected)
                    return "❌ 远程连接已断开，无法保存远程文件，请重新连接。";
                if (tab.IsBinary)
                    SaveBinaryFile(tab.FilePath, tab);
                else
                    SaveWithEncoding(tab.FilePath, tab.Encoding, tab.TextEditor.Text);
                // 避免在 UI 线程上 sync-over-async 阻塞（GetAwaiter().GetResult() 可导致死锁/卡死）
                Task.Run(() => remote.UploadFileAsync(tab.FilePath, tab.RemotePath)).GetAwaiter().GetResult();
                tab.IsModified = false;
                UpdateTabHeader(tab);
                return $"✅ 已保存远程文件：{tab.RemotePath}";
            }
            catch (Exception ex)
            {
                return $"❌ 保存远程文件失败：{ex.Message}";
            }
        }

        try
        {
            // 标记文件正在内部保存中（抑制 FileSystemWatcher 触发外部修改提示）
            _internalSavingFiles.Add(tab.FilePath);
            if (tab.IsBinary)
                SaveBinaryFile(tab.FilePath, tab);
            else if (tab.Mode != EditorMode.Text)
            {
                // 富文本文档序列化在后台线程执行（含 .doc 转换，可能较慢），等待完成后再刷新元数据
                Task.Run(() => SaveRichDocumentToPathAsync(tab, tab.FilePath)).GetAwaiter().GetResult();
            }
            else
                SaveWithEncoding(tab.FilePath, tab.Encoding, tab.TextEditor.Text);

            // 等待文件系统更新元数据后读取最新的 LastWriteTimeUtc
            tab.LastWriteTimeUtc = GetFreshLastWriteTimeUtc(tab.FilePath);
            // 记录保存时间，用于忽略刚保存后 FileSystemWatcher 触发的事件
            _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;

            tab.IsModified = false;
            UpdateTabHeader(tab);
            StatusText.Text = LangService.TFormat("已保存文件：{0}", Path.GetFileName(tab.FilePath));
            return $"✅ 已保存：{tab.FilePath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
            return $"❌ 保存失败：{ex.Message}";
        }
        finally
        {
            // 移除内部保存标记
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                _internalSavingFiles.Remove(tab.FilePath);
            }
        }
    }

    /// <summary>
    /// 工具栏/菜单命令的本地路由（agent 面板注入）：用户口头命令 → 对应工具栏按钮的动作，
    /// 命中返回结果描述文本；未命中返回 null 交由模型处理。无需模型 function calling，确定性执行。
    /// </summary>
    // ========== AI 终端 / SSH 命令 ==========
    private bool TryExtractTerminalCommand(string text, string low, out string command)
    {
        command = "";
        string t = text.Trim();
        string rest = "";
        if (low.StartsWith("terminal ")) rest = t.Substring(9);
        else if (low.StartsWith("term ")) rest = t.Substring(5);
        else if (low.StartsWith("终端")) rest = t.Substring(2);
        else if (low.Contains("在终端执行")) rest = t.Substring(t.IndexOf("在终端执行") + 5);
        else if (low.Contains("终端执行")) rest = t.Substring(t.IndexOf("终端执行") + 4);
        else return false;
        command = rest.Trim().TrimStart(':', '：', ' ');
        if (command.StartsWith("执行")) command = command.Substring(2).Trim().TrimStart(':', '：', ' ');
        else if (command.StartsWith("运行")) command = command.Substring(2).Trim().TrimStart(':', '：', ' ');
        return !string.IsNullOrEmpty(command);
    }

    private bool TryExtractSshCommand(string text, string low, out string command)
    {
        command = "";
        string t = text.Trim();
        string rest = "";
        if (low.StartsWith("ssh ")) rest = t.Substring(4);
        else if (low.StartsWith("ssh终端")) rest = t.Substring(5);
        else if (low.Contains("在ssh连接执行")) rest = t.Substring(t.IndexOf("在ssh连接执行") + 7);
        else if (low.Contains("在ssh终端执行")) rest = t.Substring(t.IndexOf("在ssh终端执行") + 7);
        else if (low.Contains("ssh执行")) rest = t.Substring(t.IndexOf("ssh执行") + 4);
        else return false;
        command = rest.Trim().TrimStart(':', '：', ' ');
        if (command.StartsWith("执行")) command = command.Substring(2).Trim().TrimStart(':', '：', ' ');
        else if (command.StartsWith("运行")) command = command.Substring(2).Trim().TrimStart(':', '：', ' ');
        return !string.IsNullOrEmpty(command);
    }

    /// <summary>通用"执行 xxx"：自动路由到已打开的终端或 SSH 连接。</summary>
    private bool TryExtractExecuteCommand(string text, string low, out string command)
    {
        command = "";
        string t = text.Trim();
        string rest = "";
        if (low.StartsWith("执行命令 ")) rest = t.Substring(5);
        else if (low.StartsWith("执行 ")) rest = t.Substring(3);
        else if (low.StartsWith("运行 ")) rest = t.Substring(3);
        else if (low.StartsWith("run ")) rest = t.Substring(4);
        else return false;
        command = rest.Trim().TrimStart(':', '：', ' ');
        return !string.IsNullOrEmpty(command);
    }

    private string ExecuteInLocalTerminal(string command)
    {
        var selected = FileTabControl.SelectedItem as TabItem;
        if (selected != null && GetTabTag(selected) is LocalTerminalView termView)
        {
            termView.ExecuteCommand(command);
            return $"✅ 已在终端执行：{command}";
        }
        foreach (TabItem item in FileTabControl.Items)
        {
            if (GetTabTag(item) is LocalTerminalView t)
            {
                t.ExecuteCommand(command);
                return $"✅ 已在终端执行：{command}";
            }
        }
        return "❌ 未找到终端标签页，请先通过 视图 → 终端 打开终端";
    }

    private string ExecuteInSshTerminal(string command)
    {
        var selected = FileTabControl.SelectedItem as TabItem;
        if (selected != null && GetTabTag(selected) is SshTerminalView sshView)
        {
            sshView.ExecuteCommand(command);
            return $"✅ 已在 SSH 终端执行：{command}";
        }
        foreach (TabItem item in FileTabControl.Items)
        {
            if (GetTabTag(item) is SshTerminalView s)
            {
                s.ExecuteCommand(command);
                return $"✅ 已在 SSH 终端执行：{command}";
            }
        }
        return "❌ 未找到 SSH 终端标签页，请先通过 扩展功能 → SSH 连接 打开";
    }

    /// <summary>通用执行：当前选中的终端/SSH 优先，否则找第一个终端，再找第一个 SSH。</summary>
    private string ExecuteOnAvailableTerminal(string command)
    {
        var selected = FileTabControl.SelectedItem as TabItem;
        if (selected != null)
        {
            if (GetTabTag(selected) is LocalTerminalView termView)
            {
                termView.ExecuteCommand(command);
                return $"✅ 已在终端执行：{command}";
            }
            if (GetTabTag(selected) is SshTerminalView sshView)
            {
                sshView.ExecuteCommand(command);
                return $"✅ 已在 SSH 终端执行：{command}";
            }
        }
        foreach (TabItem item in FileTabControl.Items)
        {
            if (GetTabTag(item) is LocalTerminalView t)
            {
                t.ExecuteCommand(command);
                return $"✅ 已在终端执行：{command}";
            }
        }
        foreach (TabItem item in FileTabControl.Items)
        {
            if (GetTabTag(item) is SshTerminalView s)
            {
                s.ExecuteCommand(command);
                return $"✅ 已在 SSH 终端执行：{command}";
            }
        }
        return "❌ 没有打开的终端或 SSH 连接，请先打开终端（视图 → 终端）或 SSH 连接（扩展功能 → SSH 连接）";
    }

    private string? RouteAgentToolCommand(string input)
    {
        var text = input.Trim();
        var low = text.ToLowerInvariant();

        // ---- 终端 / SSH 命令：把命令发送到对应的终端标签页 ----
        if (TryExtractTerminalCommand(text, low, out var termCmd))
            return RunTool(() => ExecuteInLocalTerminal(termCmd), $"🖥 已在终端执行：{termCmd}");
        if (TryExtractSshCommand(text, low, out var sshCmd))
            return RunTool(() => ExecuteInSshTerminal(sshCmd), $"🔌 已在 SSH 终端执行：{sshCmd}");
        // "ssh" / "ssh连接"：打开 SSH 连接窗口
        var compactLow = low.Replace(" ", "").Replace("　", "");
        if (compactLow == "ssh" || compactLow == "ssh连接" || compactLow == "打开ssh" ||
            compactLow == "打开ssh连接" || compactLow == "新建ssh" || compactLow == "新建ssh连接" ||
            compactLow == "连接ssh")
            return RunTool(() => OpenSshTab(null), "🔌 已打开 SSH 连接窗口");
        if (TryExtractExecuteCommand(text, low, out var execCmd))
            return RunTool(() => ExecuteOnAvailableTerminal(execCmd), $"🖥 已执行：{execCmd}");

        // ---- 简洁易记命令（令牌式；不区分大小写，可带 / 前缀，如 /new；输入 help 查看全部）----
        var token = text.Trim();
        if (token.StartsWith('/')) token = token.Substring(1);
        var cmd = token.Replace(" ", "").ToLowerInvariant();
        switch (cmd)
        {
            case "new" or "newfile": return RunTool(() => NewFile_Click(null, null!), "✅ 已新建文档");
            case "open" or "openfile": return RunTool(() => OpenFile_Click(null, null!), "📂 已打开文件选择窗口");
            case "save" or "savefile": return SaveCurrentDocumentForAgent();
            case "close" or "closetab": return RunTool(() => CloseTab_Click(null, null!), "🗑 已关闭当前标签页");
            case "undo" or "u": return RunTool(() => Undo_Click(null, null!), "↩️ 已撤销");
            case "redo" or "re": return RunTool(() => Redo_Click(null, null!), "↪️ 已重做");
            case "cut" or "x": return RunTool(() => Cut_Click(null, null!), "✂️ 已剪切选中内容");
            case "copy" or "c": return RunTool(() => Copy_Click(null, null!), "📋 已复制选中内容");
            case "paste" or "v": return RunTool(() => Paste_Click(null, null!), "📥 已粘贴");
            case "find" or "f": return RunTool(() => Find_Click(null, null!), "🔍 已打开查找窗口");
            case "replace": return RunTool(() => Replace_Click(null, null!), "🔁 已打开替换窗口");
            case "format" or "fmt": return FormatCurrentDocumentForAgent();
            case "top" or "home" or "start": return RunTool(() => GoToStart_Click(null, null!), "✅ 已跳转到文档开头");
            case "end": return RunTool(() => GoToEnd_Click(null, null!), "✅ 已跳转到文档结尾");
            case "diff" or "filecompare": return RunTool(() => FileCompare_Click(null, null!), "🆚 已打开文件对比");
            case "ddiff" or "foldercmp": return RunTool(() => FolderCompare_Click(null, null!), "🆚 已打开文件夹对比");
            case "zin" or "zoomin" or "zoom+": return RunTool(() => ZoomIn_Click(null, null!), "🔍 已放大");
            case "zout" or "zoomout" or "zoom-": return RunTool(() => ZoomOut_Click(null, null!), "🔍 已缩小");
            case "log" or "logs" or "monitor": return RunTool(() => LogMonitor_Click(null, null!), "📜 已打开日志监控");
            case "agent" or "ai" or "panel":
                return RunTool(() => SetAgentPanelVisible(!_agentPanelVisible), "🤖 已切换 AI 助手面板");
            case "help" or "?" or "cmds" or "命令":
                return ToolbarCommandHelpText;
            default:
                break;
        }
        // 行号令牌：line42 / l42 / g42 → 跳转到第 42 行（超出范围自动钳制）
        var lineToken = Regex.Match(cmd, @"^(?:line|l|g)(\d+)$");
        if (lineToken.Success && int.TryParse(lineToken.Groups[1].Value, out var tokenLine))
            return GoToDocumentLine(tokenLine);
        if (text == "帮助" || text.Contains("命令列表") || text.Contains("有哪些命令"))
            return ToolbarCommandHelpText;

        // ---- 文件/文件夹（对应菜单"打开文件夹 / 打开远程窗口"）----
        // 注意：必须在“打开文件”分支之前判断——"打开文件夹"包含“打开文件”子串
        if (low.Contains("打开文件夹") || low.Contains("打开目录") || low.Contains("打开本地文件夹"))
            return RunTool(() => OpenFolder_Click(null, null!), "📁 已打开文件夹选择窗口");
        if (low.Contains("打开远程窗口") || low.Contains("打开远程") || low.Contains("远程窗口")
            || (low.Contains("远程") && low.Contains("连接")))
            return RunTool(() => OpenRemoteWindow_Click(null, null!), "🖥 已打开远程连接窗口");

        // ---- 文档 ----
        if (text == "新建" || low.Contains("新建文件") || low.Contains("新建空白"))
            return RunTool(() => NewFile_Click(null, null!), "✅ 已新建文档");
        // 打开文件：排除文件夹/目录，避免"打开文件夹"被误判为"打开文件"
        if ((low == "打开" || low.Contains("打开文件") || low.Contains("打开文档"))
            && !low.Contains("文件夹") && !low.Contains("目录"))
            return RunTool(() => OpenFile_Click(null, null!), "📂 已打开文件选择窗口");
        if (low.Contains("关闭当前文件") || low.Contains("关闭当前标签") || (low.Contains("关闭文件") && !low.Contains("保存")))
            return RunTool(() => CloseTab_Click(null, null!), "🗑 已关闭当前标签页");
        if (low.Contains("另存为") || low.Contains("保存为"))
            return RunTool(() => SaveAs_Click(null, null!), "📝 已打开“另存为”窗口");

        // ---- 跳转到第 X 行（边界钳制：超过文件尾部→最后一行；小于 1 → 第一行）----
        var lineJump = Regex.Match(text, @"^(?:跳转|跳到|跳|转到|前往)(?:到)?第?(\d+)\s*行$");
        if (lineJump.Success && int.TryParse(lineJump.Groups[1].Value, out var targetLine))
            return GoToDocumentLine(targetLine);

        // ---- 跳转（对应工具栏"跳转到开头/结尾"按钮）----
        if (low.Contains("跳转到开头") || low.Contains("跳到开头") || low.Contains("跳转开头") || low.Contains("回到开头"))
            return RunTool(() => GoToStart_Click(null, null!), "✅ 已跳转到文档开头");
        if (low.Contains("跳转到结尾") || low.Contains("跳到结尾") || low.Contains("跳转到尾部") || low.Contains("跳到尾部")
            || low.Contains("跳到末尾") || low.Contains("跳到最后") || low.Contains("转到末尾"))
            return RunTool(() => GoToEnd_Click(null, null!), "✅ 已跳转到文档结尾");

        // ---- 缩放（对应工具栏"放大/缩小"按钮）----
        if (low.Contains("放大"))
            return RunTool(() => ZoomIn_Click(null, null!), "🔍 已放大");
        if (low.Contains("缩小"))
            return RunTool(() => ZoomOut_Click(null, null!), "🔍 已缩小");
        if (low.Contains("重置缩放") || low.Contains("恢复默认大小") || low.Contains("恢复默认字体"))
            return RunTool(() => ZoomReset_Click(null, null!), "✅ 已重置缩放");

        // ---- 编辑（对应工具栏撤销/重做/剪切/复制/粘贴/全选/查找/替换）----
        if (low == "撤销") return RunTool(() => Undo_Click(null, null!), "↩️ 已撤销");
        if (low == "重做") return RunTool(() => Redo_Click(null, null!), "↪️ 已重做");
        if (low == "剪切" || low.Contains("剪切选中")) return RunTool(() => Cut_Click(null, null!), "✂️ 已剪切选中内容");
        if (low == "复制" || low.Contains("复制选中")) return RunTool(() => Copy_Click(null, null!), "📋 已复制选中内容");
        if (low == "粘贴") return RunTool(() => Paste_Click(null, null!), "📥 已粘贴");
        if (low == "全选") return RunTool(() => SelectAll_Click(null, null!), "✅ 已全选");
        if (low == "查找" || low.Contains("查找文本") || low == "搜索")
            return RunTool(() => Find_Click(null, null!), "🔍 已打开查找窗口");
        if (low == "替换" || low.Contains("替换文本"))
            return RunTool(() => Replace_Click(null, null!), "🔁 已打开替换窗口");

        // ---- 视图（对应工具栏/菜单切换项）----
        if (low.Contains("显示工具栏") || low.Contains("隐藏工具栏") || low.Contains("打开工具栏"))
            return RunTool(() => Toolbar_Click(null, null!), "✅ 已切换工具栏显示");
        if (low.Contains("显示状态栏") || low.Contains("隐藏状态栏") || low.Contains("打开状态栏"))
            return RunTool(() => Statusbar_Click(null, null!), "✅ 已切换状态栏显示");
        if (low.Contains("显示AI助手") || low.Contains("打开AI助手") || low.Contains("展开助手面板"))
            return RunTool(() => SetAgentPanelVisible(true), "🤖 已打开 AI 助手面板");
        if (low.Contains("隐藏AI助手") || low.Contains("关闭AI助手") || low.Contains("收起助手面板"))
            return RunTool(() => SetAgentPanelVisible(false), "🤖 已隐藏 AI 助手面板");

        // ---- 菜单/工具栏其余功能：输入功能名直接执行 ----
        if (low == "退出" || low.Contains("退出程序") || low.Contains("退出编辑器") || low.Contains("退出应用"))
            return RunTool(() => Exit_Click(null, null!), "👋 正在退出程序");
        if (low.Contains("自动换行") || low.Contains("切换换行"))
            return RunTool(() => WordWrap_Click(null, null!), "✅ 已切换自动换行");
        if (low.Contains("折叠当前") || low.Contains("折叠当前块"))
            return RunTool(() => FoldCurrent_Click(null, null!), "📁 已折叠当前代码块");
        if (low.Contains("展开当前") || low.Contains("展开当前块"))
            return RunTool(() => UnfoldCurrent_Click(null, null!), "📂 已展开当前代码块");
        if (low.Contains("折叠所有") || low.Contains("全部折叠") || low.Contains("折叠全部"))
            return RunTool(() => FoldAll_Click(null, null!), "📁 已折叠所有代码块");
        if (low.Contains("展开所有") || low.Contains("全部展开") || low.Contains("展开全部"))
            return RunTool(() => UnfoldAll_Click(null, null!), "📂 已展开所有代码块");
        if (low.Contains("切换折叠") || low.Contains("切换展开"))
            return RunTool(() => ToggleFoldAll_Click(null, null!), "✅ 已切换折叠/展开");
        if (low.Contains("亮色主题") || low.Contains("浅色主题") || low == "亮色" || low == "浅色" || low.Contains("白天模式"))
            return RunTool(() => ThemeLight_Click(null, null!), "🎨 已切换到亮色主题");
        if (low.Contains("暗色主题") || low.Contains("深色主题") || low == "暗色" || low == "深色" || low.Contains("夜间模式"))
            return RunTool(() => ThemeDark_Click(null, null!), "🎨 已切换到暗色主题");
        if (low == "英文" || low.Contains("切换英文") || low.Contains("英文界面") || low.Contains("切换到英文"))
            return RunTool(() => UiLanguage_Click(new MenuItem { Tag = "en" }, null!), "🌐 已切换到英文界面");
        if (low == "中文" || low.Contains("切换中文") || low.Contains("中文界面") || low.Contains("切换到中文"))
            return RunTool(() => UiLanguage_Click(new MenuItem { Tag = "zh" }, null!), "🌐 已切换到中文界面");
        if (low == "http" || low.Contains("http协议") || low.Contains("http 协议") || low.Contains("http请求") || low.Contains("http 请求") || low.Contains("打开http"))
            return RunTool(() => HttpDebugger_Click(null, null!), "🌐 已打开 HTTP 协议");
        if (low.Contains("信息摘要") || low.Contains("摘要计算") || low.Contains("计算摘要") || low.Contains("哈希") || low.Contains("hash"))
            return RunTool(() => HashCalc_Click(null, null!), "🔐 已打开信息摘要计算");
        if (low.Contains("加密") || low.Contains("解密") || low.Contains("crypto"))
            return RunTool(() => Crypto_Click(null, null!), "🔒 已打开加密/解密");
        if (low.Contains("远程文件夹") || low.Contains("打开远程文件夹"))
            return RunTool(() => OpenRemoteWindow_Click(null, null!), "🖥 已打开远程文件夹");
        if (low.Contains("显示制表符") || low.Contains("隐藏制表符"))
            return RunTool(() => ShowTabs_Click(null, null!), "✅ 已切换显示制表符");
        if (low.Contains("显示空格") || low.Contains("隐藏空格"))
            return RunTool(() => ShowSpaces_Click(null, null!), "✅ 已切换显示空格");
        if (low.Contains("行尾标记") || low.Contains("显示行尾"))
            return RunTool(() => ShowEndOfLine_Click(null, null!), "✅ 已切换显示行尾标记");
        if (low.Contains("列编辑"))
            return RunTool(() => ColumnEdit_Click(null, null!), "✅ 已切换列编辑");
        if (low.Contains("json格式化") || low.Contains("json 格式化") || low.Contains("格式化json"))
            return RunTool(() => JsonFormat_Click(null, null!), "✅ JSON 已格式化");
        if (low.Contains("json压缩") || low.Contains("json 压缩") || low.Contains("压缩json"))
            return RunTool(() => JsonMinify_Click(null, null!), "✅ JSON 已压缩");
        if (low.Contains("json检查") || low.Contains("json语法") || low.Contains("检查json"))
            return RunTool(() => JsonValidate_Click(null, null!), "✅ JSON 语法检查完成");
        if (low.Contains("转换为utf-8") || low.Contains("转utf8") || low.Contains("转成utf8") || low.Contains("改成utf8"))
            return RunTool(() => ConvertToUtf8_Click(null, null!), "✅ 已转换为 UTF-8");
        if (low.Contains("转换为gb2312") || low.Contains("转gb2312") || low.Contains("转成gb2312") || low.Contains("改成gbk") || low.Contains("改成gb2312"))
            return RunTool(() => ConvertToAnsi_Click(null, null!), "✅ 已转换为 GB2312");
        if (low.Contains("以utf-8保存") || low.Contains("utf8保存") || low.Contains("utf-8保存"))
            return RunTool(() => SaveAsUtf8_Click(null, null!), "✅ 已以 UTF-8 编码保存");
        if (low.Contains("以gb2312保存") || low.Contains("gb2312保存") || low.Contains("gbk保存"))
            return RunTool(() => SaveAsAnsi_Click(null, null!), "✅ 已以 GB2312 编码保存");
        if (low.Contains("关于") || low == "about")
            return RunTool(() => About_Click(null, null!), "ℹ️ 已打开关于对话框");
        // 语言切换：如"语言 python" / "切换到 c++" / "设为 java" / "高亮 go"
        var langPairs = new (string Alias, string Tag)[]
        {
            ("c++", "C++"), ("c#", "C#"), ("csharp", "C#"), ("go", "Go"), ("golang", "Go"),
            ("rust", "Rust"), ("java", "Java"), ("python", "Python"), ("javascript", "JavaScript"),
            ("typescript", "TypeScript"), ("html", "HTML"), ("css", "CSS"), ("vue", "Vue"),
            ("xaml", "XAML"), ("yaml", "YAML"), ("yml", "YAML"), ("json", "JSON"),
            ("markdown", "Markdown"), ("md", "Markdown"), ("proto", "Proto"), ("nsis", "NSIS"),
            ("log", "Log"), ("日志", "Log"),
            ("纯文本", "PlainText"), ("text", "PlainText")
        };
        if (low.Contains("语言") || low.Contains("切换到") || low.Contains("设为") || low.Contains("高亮"))
        {
            foreach (var (alias, tag) in langPairs)
            {
                if (low.Contains("语言" + alias) || low.Contains("切换到" + alias) || low.Contains("设为" + alias)
                    || low.Contains("高亮" + alias) || low.Contains("语法" + alias))
                {
                    var langTag = tag;
                    return RunTool(() => Language_Click(new MenuItem { Tag = langTag }, null!), $"✅ 已切换语言为 {langTag}");
                }
            }
        }

        // ---- 工具（对应工具栏/菜单按钮）----
        if (low.Contains("文件对比") || low.Contains("对比文件") || low.Contains("对比两个文件"))
            return RunTool(() => FileCompare_Click(null, null!), "🆚 已打开文件对比");
        if (low.Contains("文件夹对比") || low.Contains("目录对比"))
            return RunTool(() => FolderCompare_Click(null, null!), "🆚 已打开文件夹对比");
        if (low.Contains("日志监控") || low.Contains("查看日志") || low.Contains("日志监视"))
            return RunTool(() => LogMonitor_Click(null, null!), "📜 已打开日志监控");
        if (low.Contains("信息摘要") || low.Contains("哈希") || low.Contains("摘要计算"))
            return RunTool(() => HashCalc_Click(null, null!), "🔐 已打开信息摘要计算");
        if (low.Contains("加密解密") || low.Contains("加密/解密") || low.Contains("加密工具"))
            return RunTool(() => Crypto_Click(null, null!), "🔒 已打开加密/解密");
        if (low.Contains("http调试") || low.Contains("http工具") || low.Contains("接口调试"))
            return RunTool(() => HttpDebugger_Click(null, null!), "🌐 已打开 HTTP 调试器");
        if (low.Contains("ssh连接") || low.Contains("新建ssh") || low.Contains("ssh 连接"))
            return RunTool(() => SshConnectNew_Click(null, null!), "🔌 已打开 SSH 连接");

        // ---- 编码（对应菜单"以 XX 编码保存 / 转换为 XX"）----
        if (low.Contains("以gb2312保存") || low.Contains("以ansi保存") || (low.Contains("gb2312") && low.Contains("保存")))
            return RunTool(() => SaveAsAnsi_Click(null, null!), "✅ 已以 GB2312 编码保存");
        if (low.Contains("以utf8bom保存") || ((low.Contains("utf8") || low.Contains("utf-8")) && low.Contains("bom") && low.Contains("保存")))
            return RunTool(() => SaveAsUtf8Bom_Click(null, null!), "✅ 已以 UTF-8 with BOM 编码保存");
        if (low.Contains("以utf8保存") || ((low.Contains("utf8") || low.Contains("utf-8")) && low.Contains("保存") && !low.Contains("bom")))
            return RunTool(() => SaveAsUtf8_Click(null, null!), "✅ 已以 UTF-8 编码保存");
        if (low.Contains("转换为gb2312") || low.Contains("转为gb2312") || low.Contains("转换成gb2312"))
            return RunTool(() => ConvertToAnsi_Click(null, null!), "✅ 已转换为 GB2312 编码");
        if (low.Contains("转换为utf8bom") || ((low.Contains("转换") || low.Contains("转为")) && (low.Contains("utf8") || low.Contains("utf-8")) && low.Contains("bom")))
            return RunTool(() => ConvertToUtf8Bom_Click(null, null!), "✅ 已转换为 UTF-8 with BOM 编码");
        if (low.Contains("转换为utf8") || low.Contains("转为utf8") || ((low.Contains("转换") || low.Contains("转为")) && (low.Contains("utf8") || low.Contains("utf-8"))))
            return RunTool(() => ConvertToUtf8_Click(null, null!), "✅ 已转换为 UTF-8 编码");

        // ---- 视图（对应菜单"文件夹列表 / 自动换行 / 显示选项"）----
        if (low.Contains("文件夹列表") || low.Contains("文件树") || (low.Contains("文件夹") && low.Contains("显示")))
            return RunTool(() => FolderTreeMenu_Click(null, null!), "📂 已切换文件夹列表显示");
        if (low.Contains("自动换行") || low == "换行" || low == "切换换行")
            return RunTool(() => WordWrap_Click(null, null!), "↩️ 已切换自动换行");
        if (low.Contains("制表符") && (low.Contains("显示") || low.Contains("隐藏") || low.Contains("切换")))
            return RunTool(() => ShowTabs_Click(null, null!), "✅ 已切换制表符显示");
        if (low.Contains("空格") && (low.Contains("显示") || low.Contains("隐藏") || low.Contains("切换"))
            && !low.Contains("制表符") && !low.Contains("行尾"))
            return RunTool(() => ShowSpaces_Click(null, null!), "✅ 已切换空格显示");
        if (low.Contains("行尾") && (low.Contains("显示") || low.Contains("隐藏") || low.Contains("切换")))
            return RunTool(() => ShowEndOfLine_Click(null, null!), "✅ 已切换行尾标记显示");

        // ---- 语言（对应"语言"菜单：语言 C++ / 使用Python语言 / 语法高亮json 等）----
        var langMatch = Regex.Match(text, @"^(?:设置|切换)?(?:语言|语法高亮)(?:设为|为|到|改成|改为)?\s*([^\s,，]+)$");
        if (!langMatch.Success)
            langMatch = Regex.Match(text, @"^(?:改为|换成|设置成|使用|用)([^\s,，]+?)(?:语言|语法高亮)$");
        if (langMatch.Success)
        {
            var langName = NormalizeLanguageName(langMatch.Groups[1].Value);
            if (langName == null)
                return $"❌ 无法识别语言“{langMatch.Groups[1].Value}”。可选：C#、C++、Go、Rust、Java、Python、JavaScript、TypeScript、HTML、CSS、Vue、XAML、YAML、JSON、Markdown、Proto、NSIS、日志、纯文本。";
            return RunTool(() =>
            {
                var tab = GetCurrentTab();
                if (tab != null) SetLanguage(langName, tab);
            }, $"✅ 已设置语言为 {langName}");
        }

        // ---- JSON（对应 JSON 菜单，本地直达）----
        if (low.Contains("json压缩") || low.Contains("压缩json") || low.Contains("压缩json数据"))
            return RunTool(() => JsonMinify_Click(null, null!), "✅ JSON 已压缩");
        if (low.Contains("json语法检查") || low.Contains("检查json") || low.Contains("json校验") || low.Contains("校验json"))
            return RunTool(() => JsonValidate_Click(null, null!), "✅ JSON 语法检查完成");

        // ---- 字体家族（对应工具栏字体下拉；"字体 N"数字由助手本地路由处理字号）----
        if (low.Contains("字体大小") || low.Contains("字体大") || low.Contains("字体小"))
            return "📏 字号请使用“放大/缩小”或“字体 N”（N 为数字，如 字体 16）命令。";
        var fontMatch = Regex.Match(text, @"^(?:字体|设置字体)(?:为|设为|设置成|改成|改为)?\s*([^\d\s][\u4e00-\u9fff\w\- ]*)$");
        if (fontMatch.Success)
        {
            var family = fontMatch.Groups[1].Value.Trim();
            if (ApplyFontFamily(family)) return $"✅ 已设置字体为 {family}";
            return $"❌ 未找到字体“{family}”。可用字体：Consolas、Courier New、Lucida Console、Source Code Pro、Fira Code、JetBrains Mono、Cascadia Code、宋体、微软雅黑、等线。";
        }

        // ---- 关于 / 退出（对应菜单）----
        // 同时接受新旧名称（AIEditor 为改名前的名字），老用户的口语习惯继续可用
        if (low == "关于" || low.Contains("关于textflowplus") || low.Contains("关于aieditor") || low.Contains("软件信息") || low.Contains("版本信息"))
            return RunTool(() => About_Click(null, null!), "ℹ️ 已打开关于对话框");
        if ((low == "退出" || low == "quit" || low == "exit" || low.Contains("退出程序") || low.Contains("关闭程序") || low.Contains("关闭textflowplus") || low.Contains("关闭aieditor"))
            && !low.Contains("退出ai助手") && !low.Contains("关闭ai助手"))
            return RunTool(() => Exit_Click(null, null!), "👋 正在退出 TextFlowPlus");

        return null;
    }

    /// <summary>把语言别名归一化为菜单标准语言名；无法识别返回 null。</summary>
    private static string? NormalizeLanguageName(string raw)
    {
        var s = raw.Trim().ToLowerInvariant().Replace(" ", "").Trim('.');
        return s switch
        {
            "c" or "c++" or "c语言" or "cpp" or "cxx" => "C++",
            "c#" or "csharp" => "C#",
            "go" or "golang" => "Go",
            "rust" or "rs" => "Rust",
            "java" => "Java",
            "python" or "py" => "Python",
            "javascript" or "js" => "JavaScript",
            "typescript" or "ts" => "TypeScript",
            "html" or "htm" => "HTML",
            "css" => "CSS",
            "vue" => "Vue",
            "xaml" => "XAML",
            "yaml" or "yml" => "YAML",
            "json" => "JSON",
            "markdown" or "md" => "Markdown",
            "proto" => "Proto",
            "nsis" => "NSIS",
            "log" or "logs" or "日志" or "log4net" => "Log",
            "纯文本" or "plaintext" or "text" or "txt" => "PlainText",
            _ => null
        };
    }

    /// <summary>按名称在工具栏字体下拉中选择字体并应用；未找到返回 false。</summary>
    private bool ApplyFontFamily(string family)
    {
        foreach (var item in FontFamilyComboBox.Items)
        {
            var name = (item as ComboBoxItem)?.Content?.ToString()?.Trim() ?? item?.ToString();
            if (string.Equals(name, family.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                FontFamilyComboBox.SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    /// <summary>执行工具栏动作并返回统一结果文本（异常转为失败提示，不弹模态框）。</summary>
    private static string? RunTool(Action action, string successMessage)
    {
        try
        {
            action();
            return successMessage;
        }
        catch (Exception ex)
        {
            return $"❌ 执行失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把光标设置到文档指定行（1 起始）：超过文件尾部 → 最后一行；小于 1 → 第一行。
    /// 与"转到行"对话框同逻辑（CaretOffset + ScrollToLine + Focus），返回结果文本。
    /// </summary>
    private string? GoToDocumentLine(int targetLine)
    {
        var editor = GetCurrentTextEditor();
        if (editor == null) return "当前没有打开的文档，无法跳转。";
        int totalLines = editor.Document.LineCount;
        int clamped = targetLine < 1 ? 1 : (targetLine > totalLines ? totalLines : targetLine);
        editor.CaretOffset = editor.Document.GetLineByNumber(clamped).Offset;
        editor.ScrollToLine(clamped);
        editor.Focus();
        StatusText.Text = $"已跳转到第 {clamped} 行";
        if (targetLine > totalLines)
            return $"✅ 文档共 {totalLines} 行，已跳转到最后一行（第 {clamped} 行）";
        if (targetLine < 1)
            return "✅ 已跳转到第一行";
        return $"✅ 已跳转到第 {clamped} 行";
    }

    /// <summary>工具栏命令一览（启动时展示 + 输入 help / 命令 / 命令列表 查看；令牌不区分大小写，可带 / 前缀）。</summary>
    private const string ToolbarCommandHelpText = """
        📋 工具栏命令一览

        文件
          new    新建文档
          open   打开文件
          folder 打开文件夹
          remote 打开远程窗口
          save   保存当前文档
          close  关闭当前标签页
          flist  列出最近打开的文件
        编辑
          undo   撤销
          redo   重做
          cut    剪切选中内容
          copy   复制选中内容
          paste  粘贴
          find   打开查找
          replace 打开替换
        格式化
          format 格式化当前文档（按文件类型自动选择语言格式化；
                 本地格式化器不支持的文件类型自动交由大模型排版）
        修改当前文档（直接用自然语言说即可，agent 会把文档交给大模型并安全应用修改）
          "把第 3 行改成 xxx"        按行号修改
          "把所有的 foo 替换成 bar"   文本替换
          "删除第 5-7 行"            删除行
          "在开头/第 2 行后插入 xxx"  插入内容
          "帮我修正/排查错误"         检查并修复
          （改动可整体 Ctrl+Z 撤销；模型找不到目标时会先反问你）
        跳转
          top    跳转到文档开头
          end    跳转到文档结尾
          line N 跳转到第 N 行
        对比
          diff   文件对比
          ddiff  文件夹对比
        视图
          zin    放大
          zout   缩小
          agent  显示/隐藏 AI 助手面板
          folderlist 显示/隐藏文件夹列表（或说"文件夹列表"）
          wrap   切换自动换行（或说"自动换行"）
          tabs/spaces/eol 显示/隐藏制表符·空格·行尾标记
        语言/字体
          "语言 C++" / "使用Python语言" / "语法高亮json"：设置文档语言
          "语言 日志" 日志格式着色（时间戳/线程/级别/来源/正文按级别着色）
          "字体 宋体" 字体家族；"字体 16" 字号
        工具
          log    日志监控
          hash   信息摘要计算
          crypto 加密/解密
          http   HTTP 协议调试
          ssh    新建 SSH 连接
        其他
          about  关于对话框
          quit   退出 TextFlowPlus
          clear  清空对话
          help   查看本清单

        也可以直接用自然语言，如"跳转到尾部""格式化""保存""检查错别字"。
        """;

    private static string? GetTabLanguage(EditorTab tab)
    {
        if (tab.TextEditor.SyntaxHighlighting == null) return null;
        string name = tab.TextEditor.SyntaxHighlighting.Name;
        return name switch
        {
            "C#" => "C#",
            "C++" => "C++",
            "Go" => "Go",
            "Rust" => "Rust",
            "Java" => "Java",
            "Proto" => "Proto",
            "JavaScript" or "JavaScript-Mode" => "JavaScript",
            "TypeScript" or "TypeScript-Mode" => "TypeScript",
            "HTML" or "HTML-Mode" => "HTML",
            "CSS" or "CSS-Mode" => "CSS",
            "Vue" or "Vue-Mode" => "Vue",
            "Python" => "Python",
            "JSON" or "JSON-Mode" => "JSON",
            "XML" or "XML-Mode" => "XML",
            "XAML" or "XAML-Mode" => "XAML",
            "YAML" or "YAML-Mode" => "YAML",
            "Markdown" or "Markdown-Mode" => "Markdown",
            "NSIS" or "NSIS-Mode" => "NSIS",
            "Log" or "Log-Mode" => "Log",
            _ => null
        };
    }

    private static string FormatJson(string json)
    {
        var parsed = JsonConvert.DeserializeObject(json);
        return JsonConvert.SerializeObject(parsed, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    }

    private static string FormatXaml(string xaml)
    {
        var lines = xaml.Split('\n');
        var result = new StringBuilder();
        int indent = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { result.AppendLine(); continue; }
            if (trimmed.StartsWith("</") || trimmed.StartsWith("/>") || trimmed == ">")
                indent = Math.Max(0, indent - 1);
            result.AppendLine(new string(' ', indent * 4) + trimmed);
            if (trimmed.StartsWith("<") && !trimmed.StartsWith("</") && !trimmed.EndsWith("/>") && !trimmed.StartsWith("<!"))
                indent++;
        }
        return result.ToString();
    }

    private static string FormatMarkdown(string md)
    {
        var lines = md.Split('\n');
        var result = new StringBuilder();
        foreach (var line in lines)
            result.AppendLine(line.TrimEnd());
        return result.ToString();
    }

    private static string FormatYaml(string yaml)
    {
        var lines = yaml.Split('\n');
        var result = new StringBuilder();
        int indent = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { result.AppendLine(); continue; }
            if (trimmed.StartsWith("- "))
            {
                result.AppendLine(new string(' ', indent * 2) + trimmed);
            }
            else if (trimmed.EndsWith(":"))
            {
                result.AppendLine(new string(' ', indent * 2) + trimmed);
                indent++;
            }
            else if (trimmed.StartsWith("-") || trimmed.Contains(": "))
            {
                result.AppendLine(new string(' ', indent * 2) + trimmed);
            }
            else
            {
                result.AppendLine(new string(' ', indent * 2) + trimmed);
            }
        }
        return result.ToString();
    }

    private static string FormatCStyleCode(string code)
    {
        var lines = code.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]") || trimmed.StartsWith(")"))
                indentLevel = Math.Max(0, indentLevel - 1);
            if (string.IsNullOrEmpty(trimmed))
                formatted.AppendLine();
            else
                formatted.AppendLine(new string(' ', indentLevel * 4) + trimmed);
            if (trimmed.EndsWith("{") || trimmed.EndsWith("[") || trimmed.EndsWith("(") && !trimmed.EndsWith(");"))
                indentLevel++;
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]") || trimmed.StartsWith(")"))
                indentLevel = Math.Max(0, indentLevel - 1);
        }
        return formatted.ToString();
    }

    private static string FormatPythonCode(string code)
    {
        var lines = code.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                formatted.AppendLine();
                continue;
            }
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]") || trimmed.StartsWith(")"))
                indentLevel = Math.Max(0, indentLevel - 1);
            formatted.AppendLine(new string(' ', indentLevel * 4) + trimmed);
            if (trimmed.EndsWith(":") && !trimmed.StartsWith("#"))
                indentLevel++;
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]") || trimmed.StartsWith(")"))
                indentLevel = Math.Max(0, indentLevel - 1);
        }
        return formatted.ToString();
    }

    private static string FormatHtml(string html)
    {
        var lines = html.Split('\n');
        var result = new StringBuilder();
        int indent = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { result.AppendLine(); continue; }
            if (trimmed.StartsWith("</") || trimmed.StartsWith("/>") || (trimmed.StartsWith(">") && indent > 0))
                indent = Math.Max(0, indent - 1);
            result.AppendLine(new string(' ', indent * 4) + trimmed);
            if (trimmed.StartsWith("<") && !trimmed.StartsWith("</") && !trimmed.EndsWith("/>") &&
                !trimmed.StartsWith("<!") && !trimmed.StartsWith("<?") && !trimmed.Contains("/>"))
            {
                if (!trimmed.Contains(">") && trimmed.EndsWith(">"))
                    indent++;
                else if (!trimmed.Contains("</") && trimmed.Contains(">"))
                    indent++;
            }
            if (trimmed.StartsWith("</"))
                indent = Math.Max(0, indent - 1);
        }
        return result.ToString();
    }

    private static string FormatCss(string css)
    {
        var lines = css.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]"))
                indentLevel = Math.Max(0, indentLevel - 1);
            if (string.IsNullOrEmpty(trimmed))
                formatted.AppendLine();
            else
                formatted.AppendLine(new string(' ', indentLevel * 4) + trimmed);
            if (trimmed.EndsWith("{") || trimmed.EndsWith("["))
                indentLevel++;
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]"))
                indentLevel = Math.Max(0, indentLevel - 1);
        }
        return formatted.ToString();
    }

    /// <summary>
    /// Vue 单文件组件格式化：把 &lt;template&gt; / &lt;script&gt; / &lt;style&gt; 三段分别交给
    /// HTML / JS / CSS 格式化器处理后再拼回，标签之外的文本原样保留。
    /// 注意：原实现是按字符遍历、每个非 '&lt;' 字符单独占一行输出，
    /// 会把 script/style 的内容炸成“一行一个字符”，故重写为分段处理。
    /// </summary>
    private static string FormatVue(string vue)
    {
        var result = new StringBuilder();
        int pos = 0;

        while (pos < vue.Length)
        {
            int lt = vue.IndexOf('<', pos);
            if (lt < 0)
            {
                result.Append(vue, pos, vue.Length - pos);
                break;
            }

            var m = Regex.Match(vue.Substring(lt), @"^<(script|style|template)\b", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                // 不是顶层块标签：原样输出到该 '<' 为止，继续往后扫描
                result.Append(vue, pos, lt - pos + 1);
                pos = lt + 1;
                continue;
            }

            // 已确认是顶层块，规范化它前面的空白：块之间只保留一个空行，文件开头空白忽略
            if (lt > pos)
            {
                var gap = vue.Substring(pos, lt - pos);
                if (gap.Trim().Length == 0)
                {
                    if (result.Length > 0 && gap.IndexOf('\n') >= 0)
                        result.AppendLine();
                }
                else
                {
                    result.Append(gap);     // 块之外的文本（注释等）原样保留
                }
            }

            string name = m.Groups[1].Value.ToLowerInvariant();
            int openEnd = vue.IndexOf('>', lt);
            if (openEnd < 0)
            {
                result.Append(vue, lt, vue.Length - lt);
                break;
            }

            // 自闭合（如 <script src="..." />）原样输出
            if (vue[openEnd - 1] == '/')
            {
                result.Append(vue, lt, openEnd - lt + 1);
                pos = openEnd + 1;
                continue;
            }

            int close = FindVueClosingTag(vue, name, openEnd + 1);
            if (close < 0)
            {
                result.Append(vue, lt, vue.Length - lt);
                break;
            }
            int closeEnd = vue.IndexOf('>', close);
            if (closeEnd < 0)
            {
                result.Append(vue, lt, vue.Length - lt);
                break;
            }

            string openTag = vue.Substring(lt, openEnd - lt + 1);
            string inner = vue.Substring(openEnd + 1, close - openEnd - 1);
            string closeTag = vue.Substring(close, closeEnd - close + 1);

            string body = name switch
            {
                "script" => FormatCStyleCode(inner),
                "style" => FormatCss(inner),
                _ => FormatHtml(inner)
            };

            result.AppendLine(openTag.Trim());
            var trimmedBody = body.Trim('\r', '\n');
            if (trimmedBody.Length > 0)
                result.AppendLine(trimmedBody);
            result.AppendLine(closeTag.Trim());

            pos = closeEnd + 1;
        }

        // 统一收尾：去掉多余的行尾空行，保证反复格式化结果稳定（幂等）
        var text = result.ToString().TrimEnd('\r', '\n');
        return text.Length > 0 ? text + "\n" : text;
    }

    /// <summary>
    /// 查找与起始标签配对的结束标签位置；对 &lt;template&gt; 做层级计数，
    /// 以便正确跳过模板内部的嵌套 &lt;template v-if&gt; 块。
    /// 找不到时返回 -1。
    /// </summary>
    private static int FindVueClosingTag(string text, string name, int from)
    {
        int depth = 1;
        int i = from;
        while (i < text.Length)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0) return -1;

            bool isClose = lt + 1 < text.Length && text[lt + 1] == '/';
            int nameStart = isClose ? lt + 2 : lt + 1;

            if (nameStart + name.Length <= text.Length &&
                string.Compare(text, nameStart, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                nameStart + name.Length < text.Length &&
                IsVueTagBoundary(text[nameStart + name.Length]))
            {
                if (isClose)
                {
                    depth--;
                    if (depth == 0) return lt;
                }
                else
                {
                    int gt = text.IndexOf('>', lt);
                    if (gt < 0) return -1;
                    if (text[gt - 1] != '/') depth++;   // 自闭合标签不计入层级
                    i = gt + 1;
                    continue;
                }
            }
            i = lt + 1;
        }
        return -1;
    }

    private static bool IsVueTagBoundary(char c) => c is ' ' or '\t' or '\r' or '\n' or '>' or '/';

    private static string FormatNsis(string nsis)
    {
        var lines = nsis.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                formatted.AppendLine();
                continue;
            }
            if (trimmed.StartsWith("}") || trimmed.StartsWith("!macroend") || trimmed.StartsWith("FunctionEnd") || trimmed.StartsWith("SectionEnd"))
                indentLevel = Math.Max(0, indentLevel - 1);
            formatted.AppendLine(new string(' ', indentLevel * 4) + trimmed);
            if (trimmed.StartsWith("{") || trimmed.StartsWith("!macro") || trimmed.StartsWith("Function") || trimmed.StartsWith("Section"))
                indentLevel++;
        }
        return formatted.ToString();
    }

    private static string FormatGenericCode(string code)
    {
        var lines = code.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("}") || trimmed.StartsWith("]") || trimmed.StartsWith(")"))
                indentLevel = Math.Max(0, indentLevel - 1);
            if (string.IsNullOrEmpty(trimmed))
                formatted.AppendLine();
            else
                formatted.AppendLine(new string(' ', indentLevel * 4) + trimmed);
            if (trimmed.EndsWith("{") || trimmed.EndsWith("[") || trimmed.EndsWith("("))
                indentLevel++;
        }
        return formatted.ToString();
    }

    // ========== JSON Operations ==========

    private void JsonFormat_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            try
            {
                // 标记文件正在内部修改中
                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    _internalSavingFiles.Add(tab.FilePath);
                }

                tab.TextEditor.Text = FormatJson(tab.TextEditor.Text);
                // 更新最后写入时间，避免触发外部修改提示
                if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
                {
                    tab.LastWriteTimeUtc = GetFreshLastWriteTimeUtc(tab.FilePath);
                    // 记录修改时间，用于忽略刚修改后 FileSystemWatcher 触发的事件
                    _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;
                }
                tab.IsModified = true; UpdateTabHeader(tab); UpdateTitle();
                StatusText.Text = "JSON formatted successfully";
            }
            catch (Exception ex) { StatusText.Text = $"JSON formatting failed: {ex.Message}"; }
            finally
            {
                // 移除内部修改标记
                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    _internalSavingFiles.Remove(tab.FilePath);
                }
            }
        }
    }

    private void JsonMinify_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            try
            {
                // 标记文件正在内部修改中
                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    _internalSavingFiles.Add(tab.FilePath);
                }

                var parsed = JsonConvert.DeserializeObject(tab.TextEditor.Text);
                tab.TextEditor.Text = JsonConvert.SerializeObject(parsed, Formatting.None);
                // 更新最后写入时间，避免触发外部修改提示
                if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
                {
                    tab.LastWriteTimeUtc = GetFreshLastWriteTimeUtc(tab.FilePath);
                    // 记录修改时间，用于忽略刚修改后 FileSystemWatcher 触发的事件
                    _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;
                }
                tab.IsModified = true; UpdateTabHeader(tab); UpdateTitle();
                StatusText.Text = "JSON minified";
            }
            catch (Exception ex) { StatusText.Text = $"JSON minify failed: {ex.Message}"; }
            finally
            {
                // 移除内部修改标记
                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    _internalSavingFiles.Remove(tab.FilePath);
                }
            }
        }
    }

    private async void JsonValidate_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            try
            {
                JsonConvert.DeserializeObject(tab.TextEditor.Text);
                await ShowMessageBox("JSON 语法检查通过", "JSON 验证", MessageBoxButtons.OK);
            }
            catch (JsonException ex)
            {
                string lineInfo = "";
                var match = Regex.Match(ex.Message, @"line\s*(\d+),\s*position\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                    lineInfo = $" (Ln: {match.Groups[1].Value}, Col: {match.Groups[2].Value})";
                await ShowMessageBox($"JSON 语法错误{lineInfo}: {ex.Message.Split(':')[0]}", "JSON 验证", MessageBoxButtons.OK);
            }
        }
    }

    // ========== Speech and TTS ==========

    private void Language_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string language)
        {
            var tab = GetCurrentTab();
            if (tab != null) SetLanguage(language, tab);
        }
    }

    private void SetLanguage(string language, EditorTab tab)
    {
        tab.Language = language;
        if (language == "PlainText")
        {
            tab.TextEditor.SyntaxHighlighting = null;
        }
        else
        {
            // 尝试获取语法高亮定义，支持自定义高亮和内置高亮
            var highlighting = HighlightingManager.Instance.GetDefinition(language);
            if (highlighting == null)
            {
                // 如果找不到，尝试使用带 "-Mode" 后缀的名称
                highlighting = HighlightingManager.Instance.GetDefinition($"{language}-Mode");
            }
            if (highlighting != null)
                tab.TextEditor.SyntaxHighlighting = highlighting;
        }
        UpdateLanguageMenu(language);
        string result = PerformSyntaxCheck(tab);
        StatusText.Text = result;
        // 用户手动把未命名文档切成代码语言时，补装大括号折叠（已装过则幂等返回）
        EnsureBraceFolding(tab);
    }

    private void SetLanguageByExtension(string extension, EditorTab tab)
    {
        string language = ExtensionToLanguage(extension);
        SetLanguage(language, tab);
        UpdateLanguageMenu(language);
    }

    private void UpdateLanguageMenu(string language)
    {
        // 更新语言菜单中的项 - 在 Header 后面添加黑色圆点
        foreach (var item in LanguageMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag)
            {
                var header = item.Header?.ToString() ?? "";
                // 移除可能已有的圆点
                if (header.EndsWith(" ●"))
                {
                    header = header.Substring(0, header.Length - 2);
                }
                // 如果当前语言，添加圆点
                if (tag == language)
                {
                    item.Header = header + " ●";
                }
                else
                {
                    item.Header = header;
                }
            }
        }

        // 查找语言菜单下的 JSON 子菜单并更新其中的"JSON 语法高亮"项
        foreach (var item in LanguageMenu.Items.OfType<MenuItem>())
        {
            if (item.Header?.ToString() == "JSON")
            {
                foreach (var sub in item.Items.OfType<MenuItem>())
                {
                    if (sub.Tag is string tag && tag == "JSON")
                    {
                        var header = sub.Header?.ToString() ?? "";
                        if (header.EndsWith(" ●"))
                        {
                            header = header.Substring(0, header.Length - 2);
                        }
                        sub.Header = language == "JSON" ? header + " ●" : header;
                    }
                }
                break;
            }
        }
    }

    private string PerformSyntaxCheck(EditorTab tab)
    {
        string? language = tab.Language;
        string text = tab.TextEditor.Text;
        if (string.IsNullOrEmpty(text)) return $"Set to {language} syntax highlighting (empty file)";
        try
        {
            return language switch
            {
                "JSON" => ValidateJson(text),
                "YAML" => ValidateYaml(text),
                _ => $"Set to {language} syntax highlighting"
            };
        }
        catch (Exception ex) { return $"Syntax check failed: {ex.Message}"; }
    }

    private static string ValidateJson(string json)
    {
        try
        {
            JsonConvert.DeserializeObject(json);
            return "JSON syntax validation passed";
        }
        catch (JsonException ex)
        {
            string lineInfo = "";
            var match = Regex.Match(ex.Message, @"line\s*(\d+),\s*position\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                lineInfo = $" (Ln: {match.Groups[1].Value}, Col: {match.Groups[2].Value})";
            return $"JSON syntax error{lineInfo}: {ex.Message.Split(':')[0]}";
        }
    }

    private static string ValidateYaml(string yaml)
    {
        var lines = yaml.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart(' ');
            int currentIndent = line.Length - trimmed.Length;
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
            if (currentIndent % 2 != 0 && currentIndent > 0)
                return $"YAML Error: Line {i + 1} indentation is not a multiple of 2";
        }
        return "YAML basic syntax check passed (minified check)";
    }

    // ========== Syntax Highlighting ==========

    private void LoadAllCustomHighlightings()
    {
        var definitions = new (string Name, string Resource)[]
        {
            ("JSON-Mode", "TextFlowPlus.Highlighting.JSON-Mode.xshd"),
            ("XAML-Mode", "TextFlowPlus.Highlighting.XAML-Mode.xshd"),
            ("Markdown-Mode", "TextFlowPlus.Highlighting.Markdown-Mode.xshd"),
            ("YAML-Mode", "TextFlowPlus.Highlighting.YAML-Mode.xshd"),
            ("Go-Mode", "TextFlowPlus.Highlighting.Go-Mode.xshd"),
            ("Rust-Mode", "TextFlowPlus.Highlighting.Rust-Mode.xshd"),
            ("Java-Mode", "TextFlowPlus.Highlighting.Java-Mode.xshd"),
            ("CSS-Mode", "TextFlowPlus.Highlighting.CSS-Mode.xshd"),
            ("HTML-Mode", "TextFlowPlus.Highlighting.HTML-Mode.xshd"),
            ("Vue-Mode", "TextFlowPlus.Highlighting.Vue-Mode.xshd"),
            ("Proto-Mode", "TextFlowPlus.Highlighting.Proto-Mode.xshd"),
            ("JavaScript-Mode", "TextFlowPlus.Highlighting.JavaScript-Mode.xshd"),
            ("TypeScript-Mode", "TextFlowPlus.Highlighting.TypeScript-Mode.xshd"),
            ("NSIS-Mode", "TextFlowPlus.Highlighting.NSIS-Mode.xshd"),
            ("Log-Mode", "TextFlowPlus.Highlighting.Log-Mode.xshd"),
        };

        var asm = Assembly.GetExecutingAssembly();
        foreach (var (name, resource) in definitions)
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var reader = new System.Xml.XmlTextReader(stream);
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                string highlightingName = name.Replace("-Mode", "");
                string[] extensions = highlightingName switch
                {
                    "Markdown" => new[] { ".md", ".markdown" },
                    "YAML" => new[] { ".yaml", ".yml" },
                    "JavaScript" => new[] { ".js", ".jsx" },
                    "TypeScript" => new[] { ".ts", ".tsx" },
                    "HTML" => new[] { ".html", ".htm" },
                    "C#" => new[] { ".cs", ".csx", ".csproj" },
                    "C++" => new[] { ".c", ".cpp", ".cc", ".h", ".hpp", ".cu" },
                    "NSIS" => new[] { ".nsi", ".nsh" },
                    _ => new[] { $".{highlightingName.ToLower()}" }
                };
                HighlightingManager.Instance.RegisterHighlighting(highlightingName, extensions, definition);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load highlighting {name}: {ex.Message}");
            }
        }
        // 自定义定义注册完成后重新应用当前主题的语法高亮配色（启动时 ApplyThemeSilent 先于注册，需在此补一次，否则 JSON 等自定义语言标点仍为黑色）
        ApplyHighlightingTheme(_isDark);
    }

    // ========== Image Menu ==========

    private void Toolbar_Click(object? sender, RoutedEventArgs e)
    {
        ToolbarMenuItem.IsChecked = !ToolbarMenuItem.IsChecked;
        var toolbar = this.FindControl<Control>("MainToolBar");
        if (toolbar != null) toolbar.IsVisible = ToolbarMenuItem.IsChecked;
    }

    private void Statusbar_Click(object? sender, RoutedEventArgs e)
    {
        StatusbarMenuItem.IsChecked = !StatusbarMenuItem.IsChecked;
        var statusBar = this.FindControl<Control>("MainStatusBar");
        if (statusBar != null) statusBar.IsVisible = StatusbarMenuItem.IsChecked;
    }

    // ========== Extended Features ==========

    // ========== AI 助手停靠面板 ==========

    private void InitAgentPanel()
    {
        _agentPanel = new AgentPanel();
        // 语音输入按钮已移到 AI 助手标题栏（设置按钮 ⚙ 之前），点击事件接到主窗口
        _agentPanel.VoiceInputClicked += (_, _) => VoiceInput_Click(null, null!);
        _agentPanel.SetCommandTools(BuildAgentCommandTools());
        // 启动后预连接模型：避免发送第一个指令时才冷启动连接导致指令等待/超时不执行
        _agentPanel.WarmUpModel();
        _agentPanel.GetRecentFilesText = BuildRecentFilesListText;
        _agentPanel.SetFontSizeHandler = SetCurrentFontSize;
        // 错别字检查：文档快照在 UI 线程捕获（文档模型不可跨线程访问），高亮由本方法应用
        _agentPanel.GetCurrentEditorText = () =>
        {
            var tab = GetCurrentTab();
            if (tab == null) return "";
            if (tab.Mode != EditorMode.Text) return GetRichTextPlainText(tab);
            return GetCurrentTextEditor()?.Text ?? "";
        };
        _agentPanel.ApplyTypoHighlights = ApplyTypoHighlights;
        _agentPanel.FormatCurrentDocument = FormatCurrentDocumentForAgent;
        _agentPanel.SaveCurrentDocument = SaveCurrentDocumentForAgent;
        _agentPanel.LocalCommandRouter = RouteAgentToolCommand;
        // 文档编辑：大模型读写当前打开文档（取上下文 / 应用结构化编辑动作）
        _agentPanel.GetDocumentContext = GetCurrentDocumentContextForAgent;
        _agentPanel.ApplyDocumentEdits = ApplyAgentDocumentEdits;
        // 格式化：本地格式化器不支持的文件类型回退到"大模型按文件类型格式化"
        _agentPanel.CanFormatCurrentDocumentLocally = CanFormatCurrentDocumentLocally;
        _agentPanel.ApplyFormatResult = ApplyAgentFormattedText;
        _agentPanel.CloseRequested += (_, _) => SetAgentPanelVisible(false);
        _agentPanel.FileClickRequested += path =>
        {
            try
            {
                OpenFile(path);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"打开文件失败：{ex.Message}";
            }
        };
        ApplyDockLayout();
    }

    private void AgentMenu_Click(object? sender, RoutedEventArgs e)
    {
        SetAgentPanelVisible(!_agentPanelVisible);
    }

    /// <summary>工具栏 🤖 按钮：显示/隐藏 agent 停靠窗口。</summary>
    private void AgentToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        SetAgentPanelVisible(!_agentPanelVisible);
    }

    /// <summary>显示/隐藏 AI 助手面板（统一走 ApplyDockLayout 重建布局）。</summary>
    private void SetAgentPanelVisible(bool visible)
    {
        _agentPanelVisible = visible;
        AgentMenuItem.IsChecked = visible;
        ApplyDockLayout();
    }

    // ========== 左侧文件夹面板 ==========

    private void InitFolderPanel()
    {
        _folderPanel = new FolderPanel();
        _folderPanel.FileOpenRequested += path =>
        {
            try
            {
                OpenFile(path);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"打开文件失败：{ex.Message}";
            }
        };
        _folderPanel.RemoteFileOpenRequested += path =>
        {
            _ = OpenRemoteFileAsync(path);
        };
        // 远程右键：新建文件 / 新建文件夹 / 删除（操作后刷新文件夹列表）
        _folderPanel.RemoteNewFileRequested += folderPath => _remoteOpTask = CreateRemoteFileAsync(folderPath);
        _folderPanel.RemoteNewFolderRequested += folderPath => _remoteOpTask = CreateRemoteFolderAsync(folderPath);
        _folderPanel.RemoteDeleteRequested += (path, isFolder) => _remoteOpTask = DeleteRemotePathAsync(path, isFolder);
        _folderPanel.RemoteDownloadRequested += (path, isFolder) => _remoteOpTask = DownloadRemotePathAsync(path, isFolder);
        // 本地新建/删除/重命名（左侧文件夹工具栏按钮与右键菜单）
        _folderPanel.LocalNewFileRequested += folderPath => _localFileOpTask = CreateLocalFileAsync(folderPath);
        _folderPanel.LocalNewFolderRequested += folderPath => _localFileOpTask = CreateLocalFolderAsync(folderPath);
        _folderPanel.LocalDeleteRequested += (path, isFolder) => _localFileOpTask = DeleteLocalPathAsync(path, isFolder);
        _folderPanel.LocalRenameRequested += (path, isFolder) => _localFileOpTask = RenameLocalPathAsync(path, isFolder);
        _folderPanel.StatusReported = message => StatusText.Text = message;
        // Git 修改列表：双击文件 → 打开修改前后对比视图
        _folderPanel.GitPanel.DiffRequested += path =>
        {
            try
            {
                OpenGitDiffTab(path);
            }
            catch (Exception ex)
            {
                StatusText.Text = LangService.TFormat("打开对比失败：{0}", ex.Message);
            }
        };
    }

    /// <summary>在右侧标签页打开 Git 修改文件的“修改前/修改后”对比视图（VS2022 风格）。
    /// 若该文件的对比标签页已打开，则直接切换过去，不重复新建。</summary>
    private void OpenGitDiffTab(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) return;

        // 同一文件的对比标签页已打开：直接激活该标签页（并把其内容滚到可见），不再新建
        var existingTab = FindOpenDiffTab(fullPath);
        if (existingTab != null)
        {
            FileTabControl.SelectedItem = existingTab;
            var repoRoot0 = Services.Git.GitService.FindRepositoryRoot(dir);
            var relPath0 = repoRoot0 != null
                ? Path.GetRelativePath(repoRoot0, fullPath).Replace('\\', '/')
                : Path.GetFileName(fullPath);
            StatusText.Text = LangService.TFormat("对比：{0}（已打开的对比窗口）", relPath0);
            return;
        }

        var repoRoot = Services.Git.GitService.FindRepositoryRoot(dir);
        if (repoRoot == null) return;
        var relPath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        var head = Services.Git.GitService.GetHeadContent(repoRoot, relPath);
        var work = Services.Git.GitService.ReadWorkContent(fullPath);
        var diffView = new DiffView(Path.GetFileName(fullPath), head ?? "", work ?? "", fullPath);
        diffView.StatusReported += message => StatusText.Text = message;
        var diffName = Path.GetFileName(fullPath);
        // 头部与关闭按钮：与文件标签共用同一套样式（尺寸 / 字号 / 字重 / 关闭行为）
        var tab = new TabItem
        {
            Tag = diffView,
            Content = diffView,
            // 底色沿用文件标签的 16 色轮转调色板，主题切换时由 UpdateTabColors 一并刷新
            Background = NextTabColor(out var diffColorIndex)
        };
        tab.Tag = new SimpleTabTag(diffView, diffColorIndex);
        var diffHeader = CreateTabHeader($"{diffName}（对比）", CreateTabCloseButton(tab.Tag));
        // 右键菜单：关闭 / 关闭其他标签（与文件标签一致）
        diffHeader.ContextFlyout = CreateSimpleTabContextMenu(tab);
        tab.Header = diffHeader;

        FileTabControl.Items.Add(tab);
        FileTabControl.SelectedItem = tab;
        StatusText.Text = LangService.TFormat("对比：{0}", relPath);
    }

    /// <summary>
    /// 查找指定文件已打开的 Git 对比标签页（同一文件只保留一个对比窗口）。
    /// 路径按平台比较：Windows 忽略大小写；文件可能已被重命名/删除，故同时用文件名兜底匹配。
    /// 返回 null 表示没有已打开的对比标签页。
    /// </summary>
    private TabItem? FindOpenDiffTab(string fullPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // 标准路径形式，避免 ./ 或大小写差异导致同一文件匹配不上
        string normalizedTarget;
        try { normalizedTarget = Path.GetFullPath(fullPath); }
        catch { normalizedTarget = fullPath; }

        foreach (TabItem item in FileTabControl.Items)
        {
            if (item.Content is not DiffView diff) continue;
            if (string.IsNullOrEmpty(diff.FilePath)) continue;
            string normalizedDiff;
            try { normalizedDiff = Path.GetFullPath(diff.FilePath); }
            catch { normalizedDiff = diff.FilePath; }
            if (string.Equals(normalizedDiff, normalizedTarget, comparison))
            {
                return item;
            }
        }

        // 兜底：路径未能对上时按文件名匹配（同一仓库内同名文件极少同时出现在修改列表中）
        var fileName = Path.GetFileName(fullPath);
        foreach (TabItem item in FileTabControl.Items)
        {
            if (item.Content is DiffView diff &&
                string.Equals(Path.GetFileName(diff.FilePath), fileName, comparison))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>菜单/工具栏“打开文件夹”。</summary>
    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        _folderDialogTask = OpenFolderDialogAsync();
    }

    private async Task OpenFolderDialogAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "打开文件夹"
            });
            if (folders.Count > 0)
            {
                OpenFolder(folders[0].Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenFolderDialog error: {ex}");
            StatusText.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    /// <summary>打开文件夹：填充左侧文件夹树并显示文件夹列表列。</summary>
    public void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            StatusText.Text = $"文件夹不存在：{path}";
            return;
        }
        _folderPanel.OpenFolder(path);
        var repoRoot = Services.Git.GitService.FindRepositoryRoot(path);
        StatusText.Text = repoRoot != null
            ? LangService.TFormat("打开文件夹：{0}（Git 仓库，{1} 处修改）", path, _folderPanel.GitPanel.ChangeCount)
            : LangService.TFormat("打开文件夹：{0}", path);
        SetFolderPanelVisible(true);
    }

    /// <summary>视图菜单“文件夹列表”勾选项：无打开的文件夹（本地或远程）时先弹出打开文件夹对话框。</summary>
    private void FolderTreeMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (!_folderPanelVisible && !_folderPanel.HasRoot)
        {
            _folderDialogTask = OpenFolderDialogAsync();
            return;
        }
        SetFolderPanelVisible(!_folderPanelVisible);
    }

    /// <summary>显示/隐藏左侧文件夹列表列（分隔条上的三角箭头与视图菜单共用）。</summary>
    private void SetFolderPanelVisible(bool visible)
    {
        if (visible && !_folderPanel.HasRoot)
        {
            _folderDialogTask = OpenFolderDialogAsync();
            return;
        }
        _folderPanelVisible = visible;
        UpdateFolderCheck();
        ApplyDockLayout();
    }

    private void UpdateFolderCheck()
    {
        if (FolderTreeMenuItem != null) FolderTreeMenuItem.IsChecked = _folderPanelVisible;
    }

    /// <summary>统一停靠布局：左文件夹面板（可选）→ 文件夹分隔条（可选）→ 编辑区 →
    /// agent 分隔条（常驻）→ agent 面板（可选），底部状态栏横跨全部列。
    /// 两侧分隔条始终保留：即使对应面板隐藏，窄条上的三角箭头仍可点击重新显示。</summary>
    private void ApplyDockLayout()
    {
        DockHost.RowDefinitions.Clear();
        DockHost.ColumnDefinitions.Clear();
        DockHost.Children.Clear();

        DockHost.RowDefinitions.Add(new RowDefinition(GridLength.Star));    // 面板+编辑区行
        DockHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto));    // 状态栏行

        int nextCol = 0;
        bool folderShown = _folderPanelVisible && _folderPanel.HasRoot;

        // 1) 文件夹面板（按需显示）
        if (folderShown)
        {
            DockHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(FolderPanelWidth)) { MinWidth = 140 });
            _folderPanel.IsVisible = true;
            _folderPanel.Width = double.NaN;
            _folderPanel.Height = double.NaN;
            _folderPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _folderPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            Grid.SetRow(_folderPanel, 0);
            Grid.SetColumn(_folderPanel, nextCol);
            DockHost.Children.Add(_folderPanel);
            nextCol++;
        }
        else
        {
            _folderPanel.IsVisible = false;
        }

        // 2) 文件夹分隔条（打开过文件夹后始终存在，容纳三角箭头与拖动分隔器）
        if (_folderPanel.HasRoot)
        {
            DockHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SplitStripWidth)));
            var strip = CreateFolderSplitStrip(folderShown);
            Grid.SetRow(strip, 0);
            Grid.SetColumn(strip, nextCol);
            DockHost.Children.Add(strip);
            nextCol++;
        }

        // 3) 编辑区（最小宽度保护：防止面板拖宽时编辑区被压到 0，导致固定列溢出压缩、分隔条消失）
        DockHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 160 });
        Grid.SetRow(FileTabControl, 0);
        Grid.SetColumn(FileTabControl, nextCol);
        DockHost.Children.Add(FileTabControl);
        nextCol++;

        // 4) agent 分隔条（常驻：容纳三角箭头与拖动分隔器，隐藏时箭头仍在，可点击重新显示）
        DockHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SplitStripWidth)));
        int agentPanelCol = nextCol + 1;
        var agentStrip = CreateAgentSplitStrip(_agentPanelVisible,
            onGetWidth: () => _agentPanelWidth,
            onSetWidth: w =>
            {
                // 最大宽度受窗口宽度限制：始终给编辑区（最小160）与分隔条留出空间，避免溢出后分割条被压缩消失
                double maxW = Math.Min(AgentPanelMaxWidth, Math.Max(AgentPanelMinWidth, DockHost.Bounds.Width - 240));
                _agentPanelWidth = Math.Clamp(w, AgentPanelMinWidth, maxW);
                if (agentPanelCol < DockHost.ColumnDefinitions.Count)
                    DockHost.ColumnDefinitions[agentPanelCol].Width = new GridLength(_agentPanelWidth);
            });
        Grid.SetRow(agentStrip, 0);
        Grid.SetColumn(agentStrip, nextCol);
        DockHost.Children.Add(agentStrip);
        nextCol++;

        // 5) agent 面板（按需显示）
        if (_agentPanelVisible)
        {
            DockHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(_agentPanelWidth))
            { MinWidth = AgentPanelMinWidth, MaxWidth = AgentPanelMaxWidth });
            _agentPanel.IsVisible = true;
            _agentPanel.Width = double.NaN;
            _agentPanel.Height = double.NaN;
            _agentPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _agentPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            Grid.SetRow(_agentPanel, 0);
            Grid.SetColumn(_agentPanel, nextCol);
            DockHost.Children.Add(_agentPanel);
            nextCol++;
        }
        else
        {
            _agentPanel.IsVisible = false;
        }

        // 6) 状态栏（横跨全部列）
        Grid.SetRow(MainStatusBar, 1);
        Grid.SetColumnSpan(MainStatusBar, nextCol);
        DockHost.Children.Add(MainStatusBar);
    }

    /// <summary>创建文件夹分隔条（面板在左：显示时箭头指向左，点击隐藏；隐藏时箭头指向右，点击显示）。</summary>
    private Control CreateFolderSplitStrip(bool folderShown) => CreateSplitStrip(
        panelShown: folderShown,
        pointLeft: true,
        hideTip: "隐藏文件夹列表",
        showTip: "显示文件夹列表",
        toggle: () => SetFolderPanelVisible(!folderShown));

    /// <summary>创建 AI 助手分隔条（面板在右：显示时箭头指向右，点击隐藏；隐藏时箭头指向左，点击显示）。</summary>
    private Control CreateAgentSplitStrip(bool agentShown, Func<double> onGetWidth, Action<double> onSetWidth) => CreateSplitStrip(
        panelShown: agentShown,
        pointLeft: false,
        hideTip: "隐藏AI助手面板",
        showTip: "显示AI助手面板",
        toggle: () => SetAgentPanelVisible(!agentShown),
        onGetWidth: onGetWidth,
        onSetWidth: onSetWidth);

    // ========== 主题（亮色 / 暗色） ==========

    private void ThemeLight_Click(object? sender, RoutedEventArgs e) => ApplyTheme(false);

    private void ThemeDark_Click(object? sender, RoutedEventArgs e) => ApplyTheme(true);

    /// <summary>切换应用主题：亮色/暗色；更新菜单勾选、持久化偏好、刷新已打开标签页颜色。</summary>
    private void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        ApplyHighlightingTheme(isDark);
        RefreshEditorHighlightingRender();
        Application.Current!.RequestedThemeVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        ThemeLightMenuItem.IsChecked = !isDark;
        ThemeDarkMenuItem.IsChecked = isDark;
        SaveThemePreference(isDark);
        UpdateEditorColors();
        UpdateTabColors();
        if (_folderPanel != null) _folderPanel.ApplyThemeColors();
        if (_agentPanel != null) _agentPanel.ApplyThemeColors();
        foreach (var s in _splitStrips)
        {
            if (s.Tag is Action refresh) refresh();
        }
    }

    /// <summary>主题切换后刷新所有已打开编辑器的语法高亮渲染。</summary>
    private void RefreshEditorHighlightingRender()
    {
        foreach (var tab in _tabs)
        {
            if (tab.TextEditor != null)
                tab.TextEditor.TextArea.TextView.Redraw();
        }
    }

    /// <summary>深色主题下内置语法高亮配色（浅色主题设计）对比度不足，按颜色名覆盖为深色友好配色；切回亮色时恢复默认。</summary>
    private static readonly Dictionary<string, Color> DarkHighlightColors = new()
    {
        ["comment"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["string"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["stringinterpolation"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["char"] = Color.FromRgb(0xD1, 0x69, 0x69),
        ["preprocessor"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["punctuation"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        // JSON 的括号/大括号/中括号（JSON-Mode.xshd 里为 #000000，深色主题下必须换成浅色）
        ["bracket"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        ["valuetypekeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["referencetypekeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["methodcall"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["numberliteral"] = Color.FromRgb(0xB5, 0xCE, 0xA8),
        ["thisorbasereference"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["nullorvaluekeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["keywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["gotokeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["contextkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["exceptionkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["checkedkeyword"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["unsafekeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["operatorkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["parametermodifiers"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["modifiers"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["visibility"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["namespacekeywords"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["getsetaddremove"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["truefalse"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["typekeywords"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["semantickeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["keyword"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["number"] = Color.FromRgb(0xB5, 0xCE, 0xA8),
        // JSON 的键名与字符串值（JSON-Mode.xshd 里为深蓝 #0000FF / 深橙 #A05000，深色主题下换成亮色）
        ["key"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["stringvalue"] = Color.FromRgb(0xCE, 0x91, 0x78),
        // ---- JS / TS / NSIS 等前端脚本定义（xshd 里大量使用 #000000 与深蓝/深紫，深色主题下需补色）----
        ["operator"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        ["type"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["typekeyword"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["function"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["variable"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["class"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["interface"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["property"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["method"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["parameter"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["decorator"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["boolean"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["null"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["template"] = Color.FromRgb(0xB5, 0xCE, 0xA8),
        // ---- Markdown / Vue 等剩余 #000000 与深蓝 ----
        ["bold"] = Color.FromRgb(0xE0, 0xE0, 0xE0),
        ["italic"] = Color.FromRgb(0xE0, 0xE0, 0xE0),
        ["linktext"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        // ---- 其余语言里同样偏暗的颜色（Markdown / NSIS / Proto / YAML）----
        // 注：Markdown 的 Highlight 虽在 xshd 里声明了 #FFFF00 黄底，但 AvaloniaEdit 不会
        // 应用该 background（实测渲染时 Background 为 inherit），深色主题下黑字实际是黑底黑字，
        // 因此这里必须换成浅色前景。
        ["highlight"] = Color.FromRgb(0xFF, 0xD7, 0x66),
        ["code"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["list"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["directive"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["label"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["modifier"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["special"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["image"] = Color.FromRgb(0xCE, 0x91, 0x78),
        // ---- C++ 内置定义 ----

        ["character"] = Color.FromRgb(0xD1, 0x69, 0x69),
        ["digits"] = Color.FromRgb(0xB5, 0xCE, 0xA8),
        ["compoundkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["this"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["operators"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["namespace"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["friend"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["booleanconstants"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["loopkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["jumpkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["exceptionhandling"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["controlflow"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        // ---- 其余内置/自定义语言补充 ----

        ["methodname"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["functioncall"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["functionkeywords"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["curlybraces"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        ["literals"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        ["datatypes"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["othertypes"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["referencetypes"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["valuetypes"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["typekeyword"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["void"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["package"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["controlstatements"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["iterationstatements"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["selectionstatements"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["jumpstatements"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["exceptionhandlingstatements"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["dateliteral"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["regex"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["value"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["markupextension"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["entities"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["entity"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["entityreference"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["xmlstring"] = Color.FromRgb(0xCE, 0x91, 0x78),
        ["doccomment"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["doctype"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["javadoctags"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["filename"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["fieldname"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["position"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["unknownattribute"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["xmldeclaration"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["htmltag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["tag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["xmltag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["tags"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["scripttag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["javascripttag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["jscripttag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["vbscripttag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["unknownscripttag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["styletag"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["selector"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["header"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading1"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading2"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading3"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading4"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading5"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["heading6"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["link"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["removedtext"] = Color.FromRgb(0x9E, 0x9E, 0x9E),
        ["slash"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        // ---- 日志（Log-Mode.xshd，浅色下为灰/青/蓝/红，深色下换成亮色）----
        ["logtimestamp"] = Color.FromRgb(0x9E, 0x9E, 0x9E),
        ["logthreadid"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["logsource"] = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["logmessagemark"] = Color.FromRgb(0x9E, 0x9E, 0x9E),
        ["loglevelerror"] = Color.FromRgb(0xF4, 0x47, 0x47),
        ["loglevelwarn"] = Color.FromRgb(0xFF, 0xA6, 0x57),
        ["loglevelinfo"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["logleveldebug"] = Color.FromRgb(0x80, 0x80, 0x80),
        ["logerrortext"] = Color.FromRgb(0xE0, 0x6C, 0x75),
        ["logwarntext"] = Color.FromRgb(0xD7, 0xBA, 0x7D),
    };
    /// <summary>
    /// 颜色名 -> 默认画刷缓存（首次覆盖前记录，切回亮色时恢复）。
    /// 键为「定义名 + 颜色名」：不同语言可能用同名颜色但取不同值
    /// （例如 Punctuation 在 JSON/JS/TS/NSIS 中是黑色，在其它定义中不是），
    /// 只按颜色名做键会让后者的亮色默认值被前者覆盖掉。
    /// </summary>
    private static readonly Dictionary<string, HighlightingBrush> DefaultHighlightBrushes = new();

    private static void ApplyHighlightingTheme(bool dark)
    {
        // 每个定义单独 try/catch：某些内置定义（如 TeX）在遍历 NamedHighlightingColors 时会抛异常，
        // 若共用一个 try 包裹整个循环，异常会中断循环，导致其后的定义（含 Json/JSON 等）全部拿不到深色配色——
        // 这正是深色主题下 JSON 的括号/逗号冒号仍为黑色的原因。
        foreach (var def in HighlightingManager.Instance.HighlightingDefinitions)
        {
            ApplyHighlightingThemeToDefinitionSafe(def, dark);
        }
        // 自定义语言（RegisterHighlighting 注册的定义不在 HighlightingDefinitions，需按名显式获取）
        foreach (var name in new[] { "JSON", "YAML", "Markdown", "Go", "Rust", "Java", "JavaScript", "TypeScript", "HTML", "CSS", "Vue", "XAML", "Proto", "NSIS", "Log" })
        {
            IHighlightingDefinition? d = null;
            try { d = HighlightingManager.Instance.GetDefinition(name); }
            catch { /* 取定义失败则跳过该语言 */ }
            if (d == null) continue;
            ApplyHighlightingThemeToDefinitionSafe(d, dark);
        }
    }

    /// <summary>对单个定义应用主题配色，异常只影响该定义，不波及后续语言。</summary>
    private static void ApplyHighlightingThemeToDefinitionSafe(IHighlightingDefinition def, bool dark)
    {
        try
        {
            ApplyHighlightingThemeToDefinition(def, dark);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyHighlightingTheme failed for '{def?.Name}': {ex.Message}");
        }
    }

    /// <summary>对单个语法定义应用主题配色（深色用覆盖表，亮色恢复默认画刷）。</summary>
    private static void ApplyHighlightingThemeToDefinition(IHighlightingDefinition def, bool dark)
    {
        foreach (var c in def.NamedHighlightingColors)
        {
            if (c is not HighlightingColor hc || hc.Foreground == null) continue;
            var key = def.Name + "\u0000" + c.Name.ToLowerInvariant();
            if (!DefaultHighlightBrushes.ContainsKey(key))
                DefaultHighlightBrushes[key] = hc.Foreground;
            hc.Foreground = dark && DarkHighlightColors.TryGetValue(c.Name.ToLowerInvariant(), out var col)
                ? new SimpleHighlightingBrush(col)
                : DefaultHighlightBrushes[key];
        }
    }

    private void ApplyThemeSilent(bool isDark)
    {
        _isDark = isDark;
        ApplyHighlightingTheme(isDark);
        Application.Current!.RequestedThemeVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        ThemeLightMenuItem.IsChecked = !isDark;
        ThemeDarkMenuItem.IsChecked = isDark;
    }

    /// <summary>从当前主题资源取颜色画刷（显式传入当前主题变体，解析 App.axaml 的 ThemeDictionaries）。</summary>
    private static IBrush ThemeBrush(string key)
    {
        if (Application.Current is { } app &&
            app.RequestedThemeVariant is { } variant &&
            app.TryFindResource(key, variant, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }
        // 防御回退：按主题返回对应底色，避免编辑器等区域变成灰色
        var fallback = Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? Color.FromRgb(0x1E, 0x1E, 0x1E)
            : Colors.White;
        return new SolidColorBrush(fallback);
    }

    /// <summary>刷新所有编辑器配色（主题切换时调用；用户手动设置的字体颜色优先保留）。</summary>
    private void UpdateEditorColors()
    {
        foreach (var tab in _tabs)
        {
            if (tab.TextEditor is { } ed)
            {
                ed.Background = ThemeBrush("EditorBackgroundBrush");
                ed.Foreground = ThemeBrush("EditorForegroundBrush");
                ed.TextArea.SelectionBrush = ThemeBrush("EditorSelectionBrush");
            }
            if (tab.RichEditor is { } rich)
            {
                rich.Background = ThemeBrush("EditorBackgroundBrush");
                rich.Foreground = ThemeBrush("EditorForegroundBrush");
            }
            // 折叠边栏标记颜色随主题刷新
            if (tab.FoldingMargin is { } foldMargin)
            {
                ApplyFoldingMarginColors(foldMargin, _isDark);
            }
        }
    }

    /// <summary>刷新所有已打开标签页的颜色（主题切换时调用）。</summary>
    private void UpdateTabColors()
    {
        foreach (var tab in _tabs)
        {
            tab.TabColor = GetTabColor(tab.TabColorIndex);
        }
        foreach (TabItem item in FileTabControl.Items)
        {
            if (item.Tag is EditorTab tab)
            {
                item.Background = tab.TabColor;
            }
            // 非文件标签（Git 对比 / 终端）：按记录的调色板索引重算底色，跟随主题切换
            else if (item.Tag is SimpleTabTag simple)
            {
                item.Background = GetTabColor(simple.ColorIndex);
            }
        }
    }

    /// <summary>按当前主题取标签页颜色（亮/暗两套色板）。</summary>
    private IBrush GetTabColor(int index)
    {
        var colors = _isDark ? DarkTabColors : TabColors;
        return colors[Math.Abs(index) % colors.Length];
    }

    private static bool LoadThemePreference()
    {
        try
        {
            if (File.Exists(ThemePrefPath))
            {
                return File.ReadAllText(ThemePrefPath).Trim() == "dark";
            }
        }
        catch
        {
            // 读取失败按亮色处理
        }
        return false;
    }

    private static void SaveThemePreference(bool isDark)
    {
        try
        {
            var dir = Path.GetDirectoryName(ThemePrefPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ThemePrefPath, isDark ? "dark" : "light");
        }
        catch
        {
            // 持久化失败不影响本次切换
        }
    }

    /// <summary>创建通用分隔条：
    /// - 浅灰渐变背景 + 居中的 1px 细分割线（面板隐藏时同样保留）
    /// - 中央圆形 chevron 按钮：白底圆角 + 柔和投影，悬停变蓝、按下加深；
    ///   面板隐藏时箭头为蓝色（提示可点击展开）；整个窄条在悬停时带淡蓝高光
    /// - 面板显示时可沿窄条拖动分隔条两侧宽度（GridSplitter，透明填充）</summary>
    private Control CreateSplitStrip(bool panelShown, bool pointLeft, string hideTip, string showTip, Action toggle,
        Func<double>? onGetWidth = null, Action<double>? onSetWidth = null)
    {
        // ---- 配色：基础色取自当前主题资源（随亮/暗主题切换），交互蓝调保持原观感 ----
        var blue = Color.FromRgb(31, 111, 235);
        var circleBorderShown = new SolidColorBrush(Color.FromRgb(204, 204, 204));
        var circleBorderHidden = new SolidColorBrush(Color.FromRgb(178, 212, 245));
        var circleBorderHover = new SolidColorBrush(Color.FromRgb(130, 180, 240));
        var circleBgHover = new SolidColorBrush(Color.FromRgb(237, 245, 253));
        var circleBgPressed = new SolidColorBrush(Color.FromRgb(216, 234, 252));
        var chevronActiveBrush = new SolidColorBrush(blue);

        bool stripHovered = false;
        bool circleHovered = false;

        // ---- 分隔条本体 ----
        var strip = new Grid
        {
            Width = SplitStripWidth,
            // 透明背景：参与命中测试使空白区域可拖动，同时不遮挡视觉
            Background = Brushes.Transparent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        // 居中细分割线（按钮背后上下露出的部分形成分割视觉）
        var divider = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        strip.Children.Add(divider);

        // ---- 圆形三角按钮 ----
        bool chevronLeft = pointLeft == panelShown;   // 指向“收起”方向
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = chevronLeft
                ? StreamGeometry.Parse("M 5.5 1.5 L 1.5 5.5 L 5.5 9.5")
                : StreamGeometry.Parse("M 1.5 1.5 L 5.5 5.5 L 1.5 9.5"),
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 7,
            Height = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var circle = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Child = chevron,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = 4,
                OffsetX = 0,
                OffsetY = 1,
                Opacity = 0.12,
                Color = Colors.Black
            }
        };
        strip.Children.Add(circle);
        ToolTip.SetTip(circle, panelShown ? hideTip : showTip);
        // 圆形切换按钮用"手型"光标：它是 strip 的子元素，而 strip 整体被设为左右双向箭头，
        // 不覆盖的话鼠标移到按钮上仍显示双向箭头（会让人误以为此处可拖动，实际此处是点击收起/展开）。
        circle.Cursor = new Cursor(StandardCursorType.Hand);
        // 可拖动调整宽度（仅面板显示且提供宽度回调时启用）
        // 注意：不能在此处用 GridSplitter——它只能调整 strip 自身列，无法调整 DockHost 的列；
        // 改为手动 Pointer 拖动，实时修改 DockHost 目标列宽。
        if (panelShown && onGetWidth != null && onSetWidth != null)
        {
            strip.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            bool dragging = false;
            double dragStartX = 0, dragStartWidth = 0;
            strip.PointerPressed += (_, e) =>
            {
                // 圆形切换按钮区域（顶部箭头）不启动拖动
                var p = e.GetPosition(circle);
                if (p.X >= 0 && p.X <= circle.Width && p.Y >= 0 && p.Y <= circle.Height) return;
                dragging = true;
                // 用窗口坐标计算位移（strip 自身仅 20px 宽，相对坐标在捕获后不可靠）
                dragStartX = e.GetPosition(TopLevel.GetTopLevel(strip)!).X;
                dragStartWidth = onGetWidth();
                e.Pointer.Capture(strip);
                e.Handled = true;
            };
            strip.PointerMoved += (_, e) =>
            {
                if (!dragging) return;
                var rootX = e.GetPosition(TopLevel.GetTopLevel(strip)!).X;
                // 分割条是面板左边界：向左拖(根X减小)=面板变宽，向右拖=面板变窄
                onSetWidth(dragStartWidth - (rootX - dragStartX));
                e.Handled = true;
            };
            strip.PointerReleased += (_, e) =>
            {
                if (!dragging) return;
                dragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
            strip.PointerCaptureLost += (_, _) => dragging = false;
        }


        // 刷新配色：基础色取当前主题资源；悬停/按下保持原交互蓝调
        Action refreshStrip = () =>
        {
            strip.Background = ThemeBrush(stripHovered ? "StripHoverBrush" : "StripBackgroundBrush");
            divider.Background = ThemeBrush("StripDividerBrush");
            circle.Background = circleHovered ? circleBgHover : ThemeBrush("StripCircleBrush");
            circle.BorderBrush = circleHovered
                ? circleBorderHover
                : (panelShown ? circleBorderShown : ThemeBrush("StripCircleBorderBrush"));
            chevron.Stroke = panelShown
                ? (stripHovered ? ThemeBrush("StripHoverBrush") : ThemeBrush("StripChevronBrush"))
                : chevronActiveBrush;
            if (circleHovered) chevron.Stroke = chevronActiveBrush;
        };
        refreshStrip();
        strip.Tag = refreshStrip;
        _splitStrips.Add(strip);

        // 悬停：分隔条淡蓝高光；按钮变蓝、按下加深；点击圆形按钮切换显示/隐藏
        strip.PointerEntered += (_, _) => { stripHovered = true; refreshStrip(); };
        strip.PointerExited += (_, _) => { stripHovered = false; refreshStrip(); };
        circle.PointerEntered += (_, _) => { circleHovered = true; refreshStrip(); };
        circle.PointerExited += (_, _) => { circleHovered = false; refreshStrip(); };
        circle.PointerPressed += (_, _) => circle.Background = circleBgPressed;
        circle.PointerReleased += (_, _) => circle.Background = circleBgHover;
        circle.AddHandler(InputElement.TappedEvent, (_, _) => toggle(), RoutingStrategies.Bubble);

        return strip;
    }

    // ========== 打开远程窗口（SFTP 远程文件夹浏览） ==========

    /// <summary>文件菜单“打开远程窗口”：弹出连接对话框，成功后把远程主目录显示在左侧文件夹列。</summary>
    private void OpenRemoteWindow_Click(object? sender, RoutedEventArgs e)
    {
        _remoteDialogTask = ShowRemoteConnectionDialogAsync();
    }

    private async Task ShowRemoteConnectionDialogAsync()
    {
        try
        {
            var dialog = new RemoteConnectionDialog();
            var result = await dialog.ShowDialog<RemoteDialogResult?>(this);
            if (result == null) return;   // 取消或连接未通过
            await ConnectRemoteAsync(result);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"远程连接失败：{ex.Message}";
        }
    }

    /// <summary>接管对话框中已建立的连接，并把用户选中的远程文件夹打开到左侧文件夹列。</summary>
    private async Task ConnectRemoteAsync(RemoteDialogResult result)
    {
        try
        {
            var info = result.Info;

            _remoteSftp?.Dispose();
            _remoteSftp = result.Sftp;
            _remoteSessionKey = $"{info.Username}@{info.Host}:{info.Port}";

            var home = string.IsNullOrEmpty(result.FolderPath) ? "/" : result.FolderPath;
            _folderPanel.RemoteListChildrenAsync = ListRemoteDirectoryChildrenAsync;
            _folderPanel.OpenRemoteFolder(home, $"{info.Username}@{info.Host}:{home}");
            SetFolderPanelVisible(true);

            StatusText.Text = $"已连接 {info.Username}@{info.Host}，已打开：{home}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"远程连接失败：{ex.Message}";
            await MessageBoxUtil.Show(this, $"打开远程文件夹失败：\n\n{ex.Message}",
                "远程连接失败", 0);
        }
    }

    /// <summary>远程目录子项提供者：SFTP 列出目录与文件（目录在前，去除 . .. 及生成目录）。</summary>
    private async Task<(List<string> Directories, List<string> Files)> ListRemoteDirectoryChildrenAsync(string path)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        var remote = _remoteSftp;
        if (remote == null || !remote.IsConnected) return (dirs, files);

        var entries = await remote.ListDirectoryAsync(path);
        foreach (var entry in entries)
        {
            if (entry.Name == "." || entry.Name == "..") continue;
            if (entry.IsDirectory)
            {
                if (FolderPanel.IsExcludedFolderName(entry.Name)) continue;
                dirs.Add(entry.FullName);
            }
            else if (entry.IsRegularFile)
            {
                files.Add(entry.FullName);
            }
        }
        return (dirs, files);
    }

    // ========== 远程文件：下载打开 / 编辑 / 回传保存 ==========

    /// <summary>双击左侧远程文件：下载到本地缓存并以新标签页打开（可编辑、保存时回传）。</summary>
    private async Task OpenRemoteFileAsync(string remotePath)
    {
        try
        {
            var remote = _remoteSftp;
            if (remote == null || !remote.IsConnected)
            {
                StatusText.Text = "远程连接已断开，请重新连接后再打开文件";
                return;
            }

            // 同一远程文件已打开：直接激活现有标签页（避免重复下载）
            var existing = _tabs.FirstOrDefault(t =>
                t.RemotePath == remotePath && t.RemoteKey == _remoteSessionKey);
            if (existing != null)
            {
                ActivateTab(existing);
                StatusText.Text = $"已打开：{remotePath}";
                return;
            }

            // 下载到本地缓存目录：%TEMP%\AIEditorRemote\user@host_port\远程路径
            var cachePath = BuildRemoteCachePath(remotePath);
            var cacheDir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(cacheDir)) Directory.CreateDirectory(cacheDir);

            StatusText.Text = $"正在下载 {remotePath} ...";
            await remote.DownloadFileAsync(remotePath, cachePath);
            if (!File.Exists(cachePath))
            {
                throw new IOException("下载失败：本地缓存文件未生成");
            }

            OpenRemoteTab(cachePath, remotePath);
            StatusText.Text = $"已打开远程文件：{remotePath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开远程文件失败：{ex.Message}";
            await ShowMessageBox($"打开远程文件失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>远程路径 → 本地缓存路径（按会话隔离，防止不同主机文件相互覆盖）。</summary>
    private string BuildRemoteCachePath(string remotePath)
    {
        var key = _remoteSessionKey;
        if (string.IsNullOrEmpty(key)) key = "remote";
        key = key.Replace(':', '_').Replace('@', '_').Replace('/', '_');
        var relative = remotePath.Replace('/', Path.DirectorySeparatorChar)
                                 .TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine(Path.GetTempPath(), "AIEditorRemote", key, relative);
    }

    /// <summary>用本地缓存文件创建远程标签页（远程元数据先于文件监视设置，避免缓存文件被外部监视）。</summary>
    private EditorTab OpenRemoteTab(string cachePath, string remotePath)
    {
        var fileInfo = new FileInfo(cachePath);
        string fileExtension = Path.GetExtension(cachePath).ToLower();
        bool isBinary = fileExtension == ".bin";

        var tab = CreateNewTab();   // 已加入 _tabs 与标签栏并选中
        tab.FilePath = cachePath;
        tab.RemotePath = remotePath;
        tab.RemoteKey = _remoteSessionKey;

        if (isBinary)
        {
            OpenBinaryFile(cachePath, tab);
        }
        else if (RichDocumentService.IsRichDocument(fileExtension))
        {
            // 远程富文本文档：复用本地富文本加载流程（转换缓存在 %TEMP%/TextFlowPlus/doc-cache，与远程缓存分离）
            _pendingUiTask = OpenRichDocumentTabAsync(tab, cachePath);
            return FinalizeRemoteTab(tab, fileInfo, remotePath);
        }
        else if (RichDocumentService.IsImage(fileExtension))
        {
            _pendingUiTask = OpenImageViewerTabAsync(tab, cachePath);
            return FinalizeRemoteTab(tab, fileInfo, remotePath);
        }
        else
        {
            try
            {
                tab.Encoding = ReadFileWithEncodingDetection(cachePath, tab.TextEditor);
            }
            catch (Exception readEx)
            {
                System.Diagnostics.Debug.WriteLine($"Read error: {readEx.Message}");
                tab.TextEditor.Text = $"Error reading file: {readEx.Message}";
            }
            try
            {
                SetLanguageByExtension(fileExtension, tab);
            }
            catch (Exception syntaxEx)
            {
                System.Diagnostics.Debug.WriteLine($"Syntax highlighting error: {syntaxEx.Message}");
                tab.TextEditor.SyntaxHighlighting = null;
            }
        }

        return FinalizeRemoteTab(tab, fileInfo, remotePath);
    }

    /// <summary>远程标签打开收尾（不建文件监视、不加入最近打开、显示远程路径）。</summary>
    private EditorTab FinalizeRemoteTab(EditorTab tab, FileInfo fileInfo, string remotePath)
    {
        tab.IsLoaded = true;
        tab.IsModified = false;
        tab.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        UpdateTabHeader(tab);
        FilePathText.Text = remotePath;    // 底部显示远程路径而非缓存路径
        UpdateFileStats(tab);
        UpdateTitle();
        UpdateMenuStatesForTab(tab);
        return tab;
    }

    /// <summary>激活指定标签页（远程去重命中时复用）。</summary>
    private void ActivateTab(EditorTab tab)
    {
        foreach (TabItem item in FileTabControl.Items)
        {
            if (item.Tag == tab)
            {
                FileTabControl.SelectedItem = item;
                return;
            }
        }
    }

    // ========== 本地新建 / 重命名 / 删除（文件夹树工具栏与右键菜单） ==========

    /// <summary>新建本地文件：在目标目录下创建空文件，创建后立即在编辑器中打开。</summary>
    private async Task CreateLocalFileAsync(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                StatusText.Text = $"目录不存在：{folderPath}";
                return;
            }

            var name = await NameInputDialog.ShowAsync(this, "新建文件",
                $"在 {folderPath} 下新建文件：", "新建文件.txt");
            if (string.IsNullOrEmpty(name)) return;

            var target = Path.Combine(folderPath, name);

            // 名称非法（含路径分隔符等）由对话框拦截；这里再做一次防御性校验
            if (!IsValidLocalChildPath(folderPath, target, out var invalidReason))
            {
                await ShowMessageBox($"无法创建文件：\n\n{invalidReason}", "错误", MessageBoxButtons.OK);
                return;
            }

            if (Directory.Exists(target))
            {
                await ShowMessageBox($"已存在同名文件夹：\n\n{target}", "无法创建", MessageBoxButtons.OK);
                return;
            }
            if (File.Exists(target))
            {
                await ShowMessageBox($"已存在同名文件：\n\n{target}\n\n请换一个名称。", "无法创建", MessageBoxButtons.OK);
                return;
            }

            using (File.Create(target)) { }   // 创建空文件后立即释放句柄
            _folderPanel.RefreshAndSelect(target);
            StatusText.Text = $"已创建文件：{target}";

            // 新建后直接在标签页打开，便于立即编辑
            try { OpenFile(target); }
            catch (Exception ex) { StatusText.Text = $"已创建文件，但打开失败：{ex.Message}"; }
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"新建文件失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>新建本地子文件夹。</summary>
    private async Task CreateLocalFolderAsync(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                StatusText.Text = $"目录不存在：{folderPath}";
                return;
            }

            var name = await NameInputDialog.ShowAsync(this, "新建文件夹",
                $"在 {folderPath} 下新建文件夹：", "新建文件夹");
            if (string.IsNullOrEmpty(name)) return;

            var target = Path.Combine(folderPath, name);

            if (!IsValidLocalChildPath(folderPath, target, out var invalidReason))
            {
                await ShowMessageBox($"无法创建文件夹：\n\n{invalidReason}", "错误", MessageBoxButtons.OK);
                return;
            }
            if (Directory.Exists(target))
            {
                await ShowMessageBox($"已存在同名文件夹：\n\n{target}\n\n请换一个名称。", "无法创建", MessageBoxButtons.OK);
                return;
            }
            if (File.Exists(target))
            {
                await ShowMessageBox($"已存在同名文件：\n\n{target}", "无法创建", MessageBoxButtons.OK);
                return;
            }

            Directory.CreateDirectory(target);
            RefreshFolderTreeContaining(folderPath, target);
            StatusText.Text = $"已创建文件夹：{target}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"新建文件夹失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>
    /// 重命名本地文件/文件夹：只改名称、不换目录（含路径分隔符的输入会被拒绝）。
    /// 成功后同步更新已打开标签页的路径与标题，避免标签页指向旧路径。
    /// </summary>
    private async Task RenameLocalPathAsync(string path, bool isFolder)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                StatusText.Text = $"路径不存在：{path}";
                _folderPanel.Refresh();
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
            {
                StatusText.Text = "无法重命名：路径没有父目录";
                return;
            }

            var oldName = Path.GetFileName(path);
            var kind = isFolder ? "文件夹" : "文件";
            var input = await NameInputDialog.ShowAsync(this, $"重命名{kind}",
                $"重命名{kind}（只改名称，不能改所在目录）：", oldName);
            if (string.IsNullOrEmpty(input)) return;

            // 名称未变化：无需操作
            if (string.Equals(input, oldName, StringComparison.Ordinal)) return;

            var target = Path.Combine(parent, input);
            if (!IsValidLocalChildPath(parent, target, out var invalidReason))
            {
                await ShowMessageBox($"无法重命名：\n\n{invalidReason}", "错误", MessageBoxButtons.OK);
                return;
            }

            // 目标已存在：直接报错，避免静默覆盖造成数据丢失
            if (File.Exists(target) || Directory.Exists(target))
            {
                await ShowMessageBox($"已存在同名文件或文件夹：\n\n{target}\n\n请换一个名称。", "无法重命名", MessageBoxButtons.OK);
                return;
            }

            // 文件正在打开时先关闭其 FileSystemWatcher，避免重命名触发“外部修改”提示
            DetachWatcherForRename(path);

            if (isFolder) Directory.Move(path, target);
            else File.Move(path, target);

            // 同步更新已打开标签页（含其子文件：目录重命名时按前缀替换）
            UpdateOpenTabsAfterRename(path, target, isFolder);

            RefreshFolderTreeContaining(parent, target);
            StatusText.Text = $"已重命名为：{target}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"重命名失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>删除本地文件/文件夹（文件夹递归删除，删除前弹确认框）。</summary>
    private async Task DeleteLocalPathAsync(string path, bool isFolder)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                StatusText.Text = $"路径不存在：{path}";
                _folderPanel.Refresh();
                return;
            }

            var kind = isFolder ? "文件夹" : "文件";
            var result = await ShowMessageBox(
                $"确定删除{kind}？\n\n{path}\n\n" +
                (isFolder ? "将递归删除其全部内容，此操作不可恢复。" : "此操作不可恢复。"),
                "删除确认", MessageBoxButtons.YesNo);
            if (result != MessageBoxResult.Yes) return;

            var parent = Path.GetDirectoryName(path) ?? "";

            DetachWatcherForRename(path);

            if (isFolder) Directory.Delete(path, recursive: true);
            else File.Delete(path);

            // 关闭引用了被删除路径的标签页（含目录下所有文件），避免留下指向不存在文件的标签
            CloseTabsUnderDeletedPath(path, isFolder);

            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) RefreshFolderTreeContaining(parent, parent);
            else _folderPanel.Refresh();

            StatusText.Text = $"已删除{kind}：{path}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"删除{((isFolder) ? "文件夹" : "文件")}失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>校验目标路径确实是 parentDir 的直接子项（阻止 ../ 跳出与非法字符）。</summary>
    private static bool IsValidLocalChildPath(string parentDir, string targetPath, out string reason)
    {
        reason = "";
        var name = Path.GetFileName(targetPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "名称为空。";
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            reason = "名称包含非法字符（如 \\ / : * ? \" &lt; &gt; |）。";
            return false;
        }
        if (name == "." || name == "..")
        {
            reason = "名称不能为 . 或 ..";
            return false;
        }
        // 归一化后必须仍位于父目录之内（防止通过相对路径跳出）
        var fullParent = Path.GetFullPath(parentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(targetPath);
        var expectedParent = Path.GetDirectoryName(fullTarget)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(fullParent, expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            reason = "只能在同一目录内改名，不能改变所在目录。";
            return false;
        }
        return true;
    }

    /// <summary>重命名/删除前摘掉该路径（及目录下文件）的 FileSystemWatcher，避免误报“文件被外部修改”。</summary>
    private void DetachWatcherForRename(string path)
    {
        foreach (var tab in _tabs)
        {
            if (string.IsNullOrEmpty(tab.FilePath)) continue;
            bool affected = IsPathSelfOrUnder(tab.FilePath, path);
            if (!affected) continue;
            try
            {
                if (tab.FileWatcher != null)
                {
                    tab.FileWatcher.EnableRaisingEvents = false;
                    tab.FileWatcher.Dispose();
                    tab.FileWatcher = null;
                }
            }
            catch { /* 释放失败不影响后续操作 */ }
        }
    }

    /// <summary>判断 candidate 是否等于 basePath 或位于其下（目录场景）。</summary>
    private static bool IsPathSelfOrUnder(string candidate, string basePath)
    {
        if (string.Equals(candidate, basePath, StringComparison.OrdinalIgnoreCase)) return true;
        var normalizedBase = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>重命名后同步更新已打开标签页的 FilePath / RemotePath 缓存键与标题。</summary>
    private void UpdateOpenTabsAfterRename(string oldPath, string newPath, bool isFolder)
    {
        foreach (var tab in _tabs)
        {
            if (string.IsNullOrEmpty(tab.FilePath)) continue;

            string? updated = null;
            if (isFolder)
            {
                if (IsPathSelfOrUnder(tab.FilePath, oldPath))
                {
                    var relative = tab.FilePath.Substring(oldPath.Length).TrimStart(Path.DirectorySeparatorChar);
                    updated = string.IsNullOrEmpty(relative) ? newPath : Path.Combine(newPath, relative);
                }
            }
            else if (string.Equals(tab.FilePath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                updated = newPath;
            }

            if (updated == null) continue;

            // 旧键上的时间戳记录迁移到新键，防止后续保存被误判为外部修改
            if (_lastInternalSaveTimes.TryGetValue(tab.FilePath, out var stamp))
            {
                _lastInternalSaveTimes.Remove(tab.FilePath);
                _lastInternalSaveTimes[updated] = stamp;
            }
            tab.FilePath = updated;
            UpdateTabHeader(tab);
        }
        UpdateTitle();
    }

    /// <summary>删除后关闭引用了该路径的标签页（目录场景含其下全部文件）。</summary>
    private void CloseTabsUnderDeletedPath(string deletedPath, bool isFolder)
    {
        var affected = _tabs
            .Where(t => !string.IsNullOrEmpty(t.FilePath) &&
                        (isFolder
                            ? IsPathSelfOrUnder(t.FilePath!, deletedPath)
                            : string.Equals(t.FilePath, deletedPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var tab in affected)
        {
            try
            {
                if (tab.FileWatcher != null)
                {
                    tab.FileWatcher.EnableRaisingEvents = false;
                    tab.FileWatcher.Dispose();
                    tab.FileWatcher = null;
                }
            }
            catch { /* 忽略释放异常 */ }

            if (tab.IsModified)
            {
                // 有未保存修改：保留标签页内容，仅解除文件绑定，避免用户改动被静默丢弃
                tab.FilePath = null;
                tab.IsModified = true;
                UpdateTabHeader(tab);
                continue;
            }

            if (tab.EditorContainer?.Parent is TabItem item && FileTabControl.Items.Contains(item))
            {
                FileTabControl.Items.Remove(item);
            }
            _tabs.Remove(tab);
        }

        // 全部标签页被移除时保留一个空白编辑区，避免出现无标签的空白窗口
        if (_tabs.Count == 0) NewFile();
        else FileTabControl.SelectedItem ??= FileTabControl.Items.FirstOrDefault();
    }

    /// <summary>刷新包含指定路径的文件夹树（优先定位到该路径的所在目录并重新选中）。</summary>
    private void RefreshFolderTreeContaining(string folderToRefresh, string? pathToSelect)
    {
        _folderPanel.RefreshAndSelect(pathToSelect ?? folderToRefresh);
    }

    // ========== 远程新建 / 删除（文件夹树右键菜单） ==========

    private async Task CreateRemoteFileAsync(string folderPath)
    {
        try
        {
            var remote = _remoteSftp;
            if (remote == null || !remote.IsConnected)
            {
                StatusText.Text = "远程连接已断开，请重新连接后再操作";
                return;
            }
            var name = await NameInputDialog.ShowAsync(this, "新建远程文件", $"在 {folderPath} 下新建文件：", "新建文件.txt");
            if (string.IsNullOrEmpty(name)) return;
            var target = JoinRemotePath(folderPath, name);
            await remote.CreateFileAsync(target);
            _folderPanel.RefreshRemoteFolder(folderPath);
            StatusText.Text = $"已创建远程文件：{target}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"新建远程文件失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    private async Task CreateRemoteFolderAsync(string folderPath)
    {
        try
        {
            var remote = _remoteSftp;
            if (remote == null || !remote.IsConnected)
            {
                StatusText.Text = "远程连接已断开，请重新连接后再操作";
                return;
            }
            var name = await NameInputDialog.ShowAsync(this, "新建远程文件夹", $"在 {folderPath} 下新建文件夹：", "新建文件夹");
            if (string.IsNullOrEmpty(name)) return;
            var target = JoinRemotePath(folderPath, name);
            await remote.CreateDirectoryAsync(target);
            _folderPanel.RefreshRemoteFolder(folderPath);
            StatusText.Text = $"已创建远程文件夹：{target}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"新建远程文件夹失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    private async Task DeleteRemotePathAsync(string path, bool isFolder)
    {
        try
        {
            var remote = _remoteSftp;
            if (remote == null || !remote.IsConnected)
            {
                StatusText.Text = "远程连接已断开，请重新连接后再操作";
                return;
            }
            var kind = isFolder ? "文件夹" : "文件";
            var result = await ShowMessageBox(
                $"确定删除远程{kind}？\n\n{path}\n\n" +
                (isFolder ? "将递归删除其全部内容，此操作不可恢复。" : "此操作不可恢复。"),
                "删除确认", MessageBoxButtons.YesNo);
            if (result != MessageBoxResult.Yes) return;

            if (isFolder) await remote.DeleteDirectoryRecursiveAsync(path);
            else await remote.DeleteFileAsync(path);

            _folderPanel.RefreshRemoteFolder(RemoteParentPath(path));
            StatusText.Text = $"已删除远程{kind}：{path}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"删除远程{(isFolder ? "文件夹" : "文件")}失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>拼接远程路径（保证单一斜杠）。</summary>
    /// <summary>下载远程文件/文件夹到本地：文件 → 保存对话框；文件夹 → 选择目标目录后递归下载。</summary>
    private async Task DownloadRemotePathAsync(string remotePath, bool isFolder)
    {
        try
        {
            var remote = _remoteSftp;
            if (remote == null || !remote.IsConnected)
            {
                StatusText.Text = "远程连接已断开，请重新连接后再下载";
                return;
            }

            string itemName = remotePath.TrimEnd('/');
            int idx = itemName.LastIndexOf('/');
            itemName = idx >= 0 ? itemName[(idx + 1)..] : itemName;

            if (isFolder)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择下载目标文件夹",
                });
                if (folders == null || folders.Count == 0) return;
                var targetDir = Path.Combine(folders[0].Path.LocalPath, itemName);
                StatusText.Text = $"正在下载文件夹 {itemName} ...";
                await DownloadRemoteFolderRecursiveAsync(remote, remotePath, targetDir);
                StatusText.Text = $"已下载文件夹到：{targetDir}";
            }
            else
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "下载文件到",
                    SuggestedFileName = itemName,
                    ShowOverwritePrompt = true,
                });
                if (file == null) return;
                var localPath = file.Path.LocalPath;
                StatusText.Text = $"正在下载 {itemName} ...";
                await remote.DownloadFileAsync(remotePath, localPath);
                StatusText.Text = $"已下载：{localPath}";
            }
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"下载失败：\n\n{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    /// <summary>递归下载远程目录到本地目录（保持目录结构）。</summary>
    private async Task DownloadRemoteFolderRecursiveAsync(SftpService remote, string remoteDir, string localDir)
    {
        Directory.CreateDirectory(localDir);
        var entries = await remote.ListDirectoryAsync(remoteDir);
        foreach (var entry in entries)
        {
            if (entry.Name == "." || entry.Name == "..") continue;
            var childRemote = entry.FullName;
            var childLocal = Path.Combine(localDir, entry.Name);
            if (entry.IsDirectory)
            {
                await DownloadRemoteFolderRecursiveAsync(remote, childRemote, childLocal);
            }
            else if (entry.IsRegularFile)
            {
                await remote.DownloadFileAsync(childRemote, childLocal);
            }
        }
    }

    private static string JoinRemotePath(string basePath, string name) =>
        basePath.TrimEnd('/') + "/" + name.TrimStart('/');

    /// <summary>远程路径的父目录路径。</summary>
    private static string RemoteParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed[..idx];
    }

    /// <summary>是否为远程标签页（保存时需回传 SFTP）。</summary>
    private static bool IsRemoteTab(EditorTab tab) => !string.IsNullOrEmpty(tab.RemotePath);

    /// <summary>保存远程标签页：写回本地缓存（保留编码）→ SFTP 上传。非远程标签返回 false 交本地保存。</summary>
    private async Task<bool> SaveRemoteTabAsync(EditorTab tab)
    {
        if (!IsRemoteTab(tab)) return false;
        if (string.IsNullOrEmpty(tab.FilePath) || string.IsNullOrEmpty(tab.RemotePath)) return true;

        var remote = _remoteSftp;
        if (remote == null || !remote.IsConnected)
        {
            await ShowMessageBox($"远程连接已断开，无法保存远程文件：{tab.RemotePath}\n请先重新连接远程主机。",
                "保存失败", MessageBoxButtons.OK);
            return true;
        }

        _internalSavingFiles.Add(tab.FilePath);
        try
        {
            StatusText.Text = $"正在保存远程文件 {tab.RemotePath} ...";
            if (tab.IsBinary) SaveBinaryFile(tab.FilePath, tab);
            else if (tab.Mode != EditorMode.Text)
            {
                // 富文本文档：按格式序列化到本地缓存（.doc/.dot 先转 docx 再转回原格式）
                await SaveRichDocumentToPathAsync(tab, tab.FilePath);
            }
            else
            {
                SaveWithEncoding(tab.FilePath, tab.Encoding, tab.TextEditor.Text);
                // 编码完整性校验：保存结果必须能按原编码无损还原（防止部分字符无法表示被替换为 ?）
                var check = tab.Encoding.GetString(File.ReadAllBytes(tab.FilePath));
                if (check.StartsWith('\uFEFF')) check = check.Substring(1);   // 去掉 BOM 前缀再比较
                if (check != tab.TextEditor.Text)
                {
                    await ShowMessageBox(
                        $"部分字符无法用当前编码（{tab.Encoding.WebName}）无损保存，保存后这些字符会变为 “?”。\n" +
                        "遇到中文乱码时，可在“编码”菜单中选择“以 UTF-8 编码保存”。",
                        "编码警告", MessageBoxButtons.OK);
                }
            }
            await remote.UploadFileAsync(tab.FilePath, tab.RemotePath);

            _lastInternalSaveTimes[tab.FilePath] = DateTime.UtcNow;
            tab.LastWriteTimeUtc = new FileInfo(tab.FilePath).LastWriteTimeUtc;
            tab.IsModified = false;
            UpdateTabHeader(tab);
            StatusText.Text = $"已保存远程文件：{tab.RemotePath}";
            return true;
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"保存远程文件失败：{ex.Message}", "错误", MessageBoxButtons.OK);
            return true;
        }
        finally
        {
            _internalSavingFiles.Remove(tab.FilePath);
        }
    }

    // ---------- 命令工具（工具栏/菜单栏命令 → AI function calling） ----------

    /// <summary>构建 agent 命令工具列表：包装工具栏/菜单栏的全部命令。</summary>
    private List<AITool> BuildAgentCommandTools()
    {
        var tools = new List<AITool>();

        // 文件
        // NewFile 走 AgentNewFile：已有空白文档时复用，避免模型在多轮工具循环里重复创建
        tools.Add(AIFunctionFactory.Create(() => Dispatcher.UIThread.Invoke(AgentNewFile),
            name: "NewFile",
            description: "新建一个空白文件标签页。注意：若当前已存在空白文档则会直接复用它（不会重复创建），" +
                         "因此本工具重复调用是安全的；创建一个文件只需调用一次，建好后请用 InsertText 写入内容。"));

        // 按文件名一步创建文件（首选）；相对路径落在当前打开的文件夹下。
        // 注意：content 必须带默认值才会被标记为可选参数（仅声明为 string? 仍会进 required，
        // 模型不传时会抛 ArgumentException），故此处用 = null 默认值。
        tools.Add(AIFunctionFactory.Create((string fileName, string? content = null) =>
            Dispatcher.UIThread.Invoke(() => AgentCreateFile(fileName, content)),
            name: "CreateFile",
            description: "按文件名创建文件并打开，可同时写入初始内容。fileName 例如 \"main.cpp\"、" +
                         "\"utils/helper.h\"（相对路径落在当前打开的文件夹下）或绝对路径。" +
                         "语法高亮会按扩展名（.cpp/.h -> C++、.py -> Python、.cs -> C# 等）自动设置。" +
                         "若同名文件已打开或已存在，则直接打开、不覆盖。创建文件请优先用本工具而不是 NewFile。" +
                         "本工具自带内容写入能力，调用后不要再调用 NewFile 或重复调用本工具。"));
        tools.Add(CommandTool("OpenFile", "打开文件（弹出文件选择对话框）", () => OpenFile_Click(null, null!)));
        tools.Add(CommandTool("OpenFolder", "打开文件夹（弹出文件夹选择对话框并在左侧显示文件列表）", () => OpenFolder_Click(null, null!)));
        tools.Add(CommandTool("ToggleFolderPanel", "显示/隐藏左侧文件夹列表", () => FolderTreeMenu_Click(null, null!)));
        tools.Add(CommandTool("SaveFile", "保存当前文件", () => Save_Click(null, null!)));
        tools.Add(CommandTool("SaveFileAs", "当前文件另存为", () => SaveAs_Click(null, null!)));
        tools.Add(CommandTool("CloseTab", "关闭当前标签页", () => CloseTab_Click(null, null!)));

        // 编辑
        tools.Add(CommandTool("Undo", "撤销", () => Undo_Click(null, null!)));
        tools.Add(CommandTool("Redo", "重做", () => Redo_Click(null, null!)));
        tools.Add(CommandTool("Cut", "剪切选中文本", () => Cut_Click(null, null!)));
        tools.Add(CommandTool("Copy", "复制选中文本", () => Copy_Click(null, null!)));
        tools.Add(CommandTool("Paste", "粘贴", () => Paste_Click(null, null!)));
        tools.Add(CommandTool("SelectAll", "全选", () => SelectAll_Click(null, null!)));
        tools.Add(CommandTool("Find", "查找（弹出查找对话框）", () => Find_Click(null, null!)));
        tools.Add(CommandTool("Replace", "替换（弹出替换对话框）", () => Replace_Click(null, null!)));
        tools.Add(CommandTool("FormatCode", "格式化当前文档代码", () => FormatCode_Click(null, null!)));
        tools.Add(CommandTool("GoToStart", "光标跳到文档开头", () => GoToStart_Click(null, null!)));
        tools.Add(CommandTool("GoToEnd", "光标跳到文档结尾", () => GoToEnd_Click(null, null!)));

        // 视图
        tools.Add(CommandTool("ZoomIn", "放大字体", () => ZoomIn_Click(null, null!)));
        tools.Add(CommandTool("ZoomOut", "缩小字体", () => ZoomOut_Click(null, null!)));
        tools.Add(CommandTool("ZoomReset", "重置缩放", () => ZoomReset_Click(null, null!)));
        tools.Add(CommandTool("ToggleToolbar", "显示/隐藏工具栏", () => Toolbar_Click(null, null!)));
        tools.Add(CommandTool("ToggleStatusbar", "显示/隐藏状态栏", () => Statusbar_Click(null, null!)));
        tools.Add(CommandTool("ToggleAgentPanel", "显示/隐藏 AI 助手面板", () => SetAgentPanelVisible(!_agentPanelVisible)));

        // 显示选项
        tools.Add(CommandTool("ShowTabs", "显示/隐藏制表符", () => ShowTabs_Click(null, null!)));
        tools.Add(CommandTool("ShowSpaces", "显示/隐藏空格", () => ShowSpaces_Click(null, null!)));
        tools.Add(CommandTool("ShowEndOfLine", "显示/隐藏行尾标记", () => ShowEndOfLine_Click(null, null!)));
        tools.Add(CommandTool("ToggleWordWrap", "切换自动换行", () => WordWrap_Click(null, null!)));

        // 编码
        tools.Add(CommandTool("SaveAsAnsi", "以 GB2312 编码保存当前文件", () => SaveAsAnsi_Click(null, null!)));
        tools.Add(CommandTool("SaveAsUtf8", "以 UTF-8 编码保存当前文件", () => SaveAsUtf8_Click(null, null!)));
        tools.Add(CommandTool("SaveAsUtf8Bom", "以 UTF-8 with BOM 编码保存当前文件", () => SaveAsUtf8Bom_Click(null, null!)));
        tools.Add(CommandTool("ConvertToAnsi", "将当前文件转换为 GB2312 编码", () => ConvertToAnsi_Click(null, null!)));
        tools.Add(CommandTool("ConvertToUtf8", "将当前文件转换为 UTF-8 编码", () => ConvertToUtf8_Click(null, null!)));
        tools.Add(CommandTool("ConvertToUtf8Bom", "将当前文件转换为 UTF-8 with BOM 编码", () => ConvertToUtf8Bom_Click(null, null!)));

        // JSON
        tools.Add(CommandTool("JsonFormat", "JSON 格式化", () => JsonFormat_Click(null, null!)));
        tools.Add(CommandTool("JsonMinify", "JSON 压缩", () => JsonMinify_Click(null, null!)));
        tools.Add(CommandTool("JsonValidate", "JSON 语法检查", () => JsonValidate_Click(null, null!)));

        // 扩展功能
        tools.Add(CommandTool("FileCompare", "文件对比", () => FileCompare_Click(null, null!)));
        tools.Add(CommandTool("FolderCompare", "文件夹对比", () => FolderCompare_Click(null, null!)));
        tools.Add(CommandTool("HashCalc", "信息摘要计算", () => HashCalc_Click(null, null!)));
        tools.Add(CommandTool("Crypto", "加密/解密", () => Crypto_Click(null, null!)));
        tools.Add(CommandTool("HttpDebugger", "HTTP 协议调试", () => HttpDebugger_Click(null, null!)));
        tools.Add(CommandTool("LogMonitor", "日志监控", () => LogMonitor_Click(null, null!)));
        tools.Add(CommandTool("SshConnect", "新建 SSH 连接", () => SshConnectNew_Click(null, null!)));
        tools.Add(CommandTool("OpenRemoteWindow", "打开远程连接窗口（输入服务器信息连接后浏览并编辑远程文件）", () => OpenRemoteWindow_Click(null, null!)));

        // 其他
        tools.Add(CommandTool("About", "打开关于对话框", () => About_Click(null, null!)));
        tools.Add(CommandTool("Exit", "退出 TextFlowPlus", () => Exit_Click(null, null!)));

        // 带参数工具
        tools.Add(AIFunctionFactory.Create((string language) =>
        {
            // GetCurrentTab() 读取 TabItem.Tag（UI 对象），必须在 UI 线程内调用；
            // 若在工具线程上先取 tab 再 Invoke，会抛 "different thread owns it"。
            return Dispatcher.UIThread.Invoke(() =>
            {
                var tab = GetCurrentTab();
                if (tab == null) return "当前没有打开的文档";
                SetLanguage(language, tab);
                return $"已将语言设置为 {language}";
            });
        }, name: "SetLanguage", description: "设置当前文档的语法高亮语言（C#/C++/Go/Rust/Java/Python/JavaScript/TypeScript/HTML/CSS/Vue/XAML/YAML/JSON/Markdown/Proto/NSIS/Log/PlainText）"));

        tools.Add(AIFunctionFactory.Create((string text) =>
        {
            Dispatcher.UIThread.Invoke(() => InsertAgentText(text));
            return "已将文本插入当前文档光标位置";
        }, name: "InsertText",
           description: "把指定文本插入当前文档光标位置（若存在选区则替换选区）。" +
                        "创建文件后再写入内容的正确流程是：先调用一次 NewFile，然后只调用本工具写入全部内容，" +
                        "中途不要再调用 NewFile。"));

        // 字体大小：输入"字体 XX"或"字体设置到 XX"时调用，size 必须是数字
        tools.Add(AIFunctionFactory.Create((int size) =>
        {
            return Dispatcher.UIThread.Invoke(() => SetCurrentFontSize(size));
        }, name: "SetFontSize", description: "把当前文档的字体大小设置为 size（size 必须是数字，单位 pt，如 16、18、20；当用户说'字体 XX'或'字体设置到 XX'时调用，XX 为数字）"));

        // 字体家族：输入"字体 宋体 / 设置字体为 Consolas"时调用（与字号 SetFontSize 区分）
        tools.Add(AIFunctionFactory.Create((string family) =>
        {
            var ok = Dispatcher.UIThread.Invoke(() => ApplyFontFamily(family));
            return ok ? $"已将字体设置为 {family}" : $"未找到字体：{family}（可用：Consolas、Courier New、Lucida Console、Source Code Pro、Fira Code、JetBrains Mono、Cascadia Code、宋体、微软雅黑、等线）";
        }, name: "SetFontFamily", description: "把当前编辑器字体家族设置为 family（如 Consolas、宋体、微软雅黑、等线、Courier New）；当用户说'字体 XX'且 XX 是字体名称时调用，XX 是数字时用 SetFontSize"));

        // 最近文件列表：输入"打开最近文件/文件列表/flist"时调用，返回带 📄 前缀的行，点击后打开
        tools.Add(AIFunctionFactory.Create(() =>
        {
            return Dispatcher.UIThread.Invoke(BuildRecentFilesListText);
        }, name: "ListRecentFiles", description: "列出最近打开的文件列表（触发词：打开最近文件 / 文件列表 / flist）。返回的每行以 📄 开头，用户点击对应行会在新标签页打开该文件"));

        tools.Add(AIFunctionFactory.Create(() =>
        {
            // 文档/控件访问必须在 UI 线程内完成（工具运行在模型后台线程上）
            return Dispatcher.UIThread.Invoke(() =>
            {
                var editor = GetCurrentTextEditor();
                if (editor == null) return "当前没有打开的文档";
                var content = editor.Text;
                if (string.IsNullOrEmpty(content)) return "（当前文档为空）";
                if (content.Length > 3000) content = content.Substring(0, 3000) + "\n…（内容过长已截断）";
                return content;
            });
        }, name: "GetDocumentText", description: "获取当前文档的文本内容（最多 3000 字符，供分析/总结）"));

        tools.Add(AIFunctionFactory.Create(() =>
        {
            return Dispatcher.UIThread.Invoke(() =>
            {
                var editor = GetCurrentTextEditor();
                if (editor == null) return "（当前没有打开的文档）";
                var sel = editor.SelectedText;
                return string.IsNullOrEmpty(sel) ? "（当前没有选中文本）" : sel;
            });
        }, name: "GetSelectedText", description: "获取当前选中的文本内容"));

        return tools;
    }

    /// <summary>把命令包装为 AI 工具：在 UI 线程同步执行命令并返回结果文本。</summary>
    private AITool CommandTool(string name, string description, Action action)
    {
        return AIFunctionFactory.Create(() =>
        {
            Dispatcher.UIThread.Invoke(action);
            return $"✅ 已执行：{description}";
        }, name: name, description: description);
    }

    /// <summary>把文本插入当前文档光标位置（若存在选区则替换选区）。供 agent 工具调用。
    /// 富文本模式在光标处插入普通 Run（继承当前格式）；图片模式无操作。</summary>
    private void InsertAgentText(string text)
    {
        var tab = GetCurrentTab();
        if (tab == null || string.IsNullOrEmpty(text)) return;

        if (tab.Mode == EditorMode.RichText && tab.RichEditor is { } rich && rich.Document != null)
        {
            var td = rich.Document.EnsureTextDocument();
            var (start, end) = GetSelectionOffsetRange(rich.Selection, td);
            var range = new TextRange(
                td.ContentStart.CreatePointer(start, RichLogicalDirection.Forward),
                td.ContentStart.CreatePointer(Math.Max(end, start), RichLogicalDirection.Forward));
            range.ReplaceText(text);
            rich.Focus();
            StatusText.Text = "已插入 agent 文本";
            return;
        }
        if (tab.Mode == EditorMode.ImageViewer) return;

        var editor = tab.TextEditor;
        var selStart = editor.SelectionStart;
        var selLen = editor.SelectionLength;
        if (selLen > 0)
        {
            editor.Document.Replace(selStart, selLen, text);
            editor.CaretOffset = selStart + text.Length;
        }
        else if (ShouldReplaceWholeDocument(tab, editor, text))
        {
            // 模型在已认领的标签页里再次写入整篇内容（多轮工具循环中常见）：
            // 若继续按光标插入会把内容叠加成两份，这里改为整体替换，保证结果就是模型给出的那一份。
            editor.Document.Text = text;
            editor.CaretOffset = text.Length;
        }
        else
        {
            var caretOffset = editor.CaretOffset;
            editor.Document.Insert(caretOffset, text);
            editor.CaretOffset = caretOffset + text.Length;
        }
        editor.Focus();
        editor.TextArea.Caret.BringCaretToView();

        // 记录本次写入后的光标位置，供下次判断"模型续写"还是"用户手动定位"
        _agentLastCaretOffset = editor.CaretOffset;

        // 标记为已修改：否则该标签页看起来仍是"空白文档"，
        // 会被 FindReusableBlankTab 判定为可复用，导致模型再次请求新建时内容重复写入。
        tab.IsModified = true;
        UpdateTabHeader(tab);
    }

    /// <summary>
    /// 判断本次 InsertText 是否应视为"重写整篇文档"而非"在光标处插入"。
    /// 只在确有把握时替换，避免破坏正常的追加式插入：
    /// 1) 目标正是本次 agent 会话认领的标签页（模型自己刚创建/刚写入的那个）；
    /// 2) 该标签页尚未落盘（无 FilePath，即还没保存成磁盘文件）；
    /// 3) 光标仍停在上次 agent 写入后的位置（用户一旦手动挪动光标，说明他想在那里插入，必须尊重）；
    /// 4) 文档已有内容，且待写入文本"看起来是同一次重写"——
    ///    即新文本的开头与现有内容一致（模型重发同一份内容），
    ///    或现有内容是待写入文本的前缀（模型在补全/覆盖同一份文件）。
    /// 这样既能拦住"整篇重复叠加"，又不会误伤 line1/line2 这种真正的逐段追加。
    /// </summary>
    private bool ShouldReplaceWholeDocument(EditorTab tab, TextEditor editor, string text)
    {
        if (!string.IsNullOrEmpty(tab.FilePath)) return false;
        if (!ReferenceEquals(tab, _agentClaimedTab)) return false;
        if (string.IsNullOrEmpty(editor.Text)) return false;
        if (string.Equals(editor.Text, text, StringComparison.Ordinal)) return false;  // 完全重复，天然幂等
        if (editor.CaretOffset != _agentLastCaretOffset) return false;                 // 用户挪过光标

        var existing = editor.Text;
        // 模型重发同一份内容（新文本以现有内容开头），或现有内容是新文本的前缀（同一文件的补全/覆盖）
        return text.StartsWith(existing, StringComparison.Ordinal)
            || existing.StartsWith(text, StringComparison.Ordinal);
    }

    /// <summary>构建最近文件列表文本（与"文件 → 最近打开"菜单同一数据源 _recentFiles，行以 📄 开头供点击）。</summary>
    private string BuildRecentFilesListText()
    {
        if (_recentFiles.Count == 0) return "最近没有打开过的文件记录。";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"最近打开的文件（共 {_recentFiles.Count} 个，点击文件名打开）：");
        foreach (var f in _recentFiles) sb.AppendLine("📄 " + f);
        return sb.ToString().TrimEnd();
    }

    /// <summary>设置当前文档字体大小（供 agent 工具与本地命令共用）。</summary>
    private string SetCurrentFontSize(int size)
    {
        if (size <= 0 || size > 200)
            return $"字体大小 {size} 无效（需为 1-200 的数字）";
        var editor = GetCurrentTextEditor();
        if (editor == null) return "当前没有打开的文档";
        editor.FontSize = size;
        return $"已将当前文档字体大小设置为 {size}";
    }

    /// <summary>在主窗口新建一个工具标签页（HTTP 调试 / 信息摘要 / 加密解密等），替代原有独立弹窗。</summary>
    private void OpenToolTab(string title, Control content)
    {
        var tabItem = new TabItem
        {
            Tag = content,
            Content = content,
        };

        // 关闭按钮（与文件标签一致）
        var closeButton = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            FontSize = 12,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            BorderThickness = new Thickness(0),
        };

        var headerPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        headerPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        headerPanel.Children.Add(closeButton);

        // 标签右键菜单：关闭标签
        var contextMenu = new MenuFlyout();
        var closeItem = new MenuItem { Header = "关闭标签" };
        closeItem.Click += (s, e) => CloseTab(tabItem);
        contextMenu.Items.Add(closeItem);
        headerPanel.ContextFlyout = contextMenu;

        tabItem.Header = headerPanel;

        // 视图内“取消/关闭”按钮请求关闭标签页
        if (content is ICloseRequested closeable)
        {
            closeable.CloseRequested += () => CloseTab(tabItem);
        }

        closeButton.Click += (s, e) => CloseTab(tabItem);

        FileTabControl.Items.Add(tabItem);
        FileTabControl.SelectedItem = tabItem;
    }
    private void HttpDebugger_Click(object? sender, RoutedEventArgs e)
    {
        OpenToolTab("HTTP 调试", new HttpDebuggerView());
    }

    private void SpeechToText_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SpeechToTextDialog();
        dialog.Show(this);
    }

    // ========== 语音输入（AI 助手标题栏麦克风按钮，识别结果直接插入当前文档） ==========

    private void VoiceInput_Click(object? sender, RoutedEventArgs e)
    {
#if VOICE_INPUT
        if (_isVoiceInput)
        {
            // 结束语音输入 → 自动发送输入框内容
            StopVoiceInput(autoSend: true);
        }
        else
        {
            StartVoiceInput();
        }
#else
        StatusText.Text = "语音输入功能仅 Windows 可用";
#endif
    }

#if VOICE_INPUT
    private void StartVoiceInput()
    {
        try
        {
            StopVoiceInput();   // 确保旧实例已释放
            _voiceRecorder = new TextFlowPlus.Services.Voice.VoiceRecorder();
            _voiceRecorder.ErrorOccurred += msg =>
                Dispatcher.UIThread.Post(() => StatusText.Text = msg);
            _voiceRecorder.StatusChanged += msg =>
                Dispatcher.UIThread.Post(() => StatusText.Text = msg);
            _voiceRecorder.UtteranceRecognized += text =>
            {
                var t = text;
                // 识别结果追加到命令输入框 → 立即自动发送 → 输入框随发送清空
                Dispatcher.UIThread.Post(() =>
                {
                    _agentPanel.AppendVoiceText(t);
                    _agentPanel.SendInputText();
                });
            };

            if (!_voiceRecorder.Start())
            {
                _voiceRecorder.Dispose();
                _voiceRecorder = null;
                return;
            }

            _isVoiceInput = true;
            _agentPanel.VoiceButton.Background = new SolidColorBrush(Color.FromRgb(255, 80, 80));
            _agentPanel.VoiceButton.Content = "⏹";
            ToolTip.SetTip(_agentPanel.VoiceButton, "停止语音输入");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"语音输入启动失败：{ex.Message}";
        }
    }

    /// <summary>停止语音输入。autoSend=true 时（用户主动结束输入）稍等片刻让最后一句识别结果落入输入框后自动发送。</summary>
    private void StopVoiceInput(bool autoSend = false)
    {
        if (_voiceRecorder != null)
        {
            try { _voiceRecorder.Stop(); } catch { }
            try { _voiceRecorder.Dispose(); } catch { }
            _voiceRecorder = null;
        }
        _isVoiceInput = false;
        _agentPanel.VoiceButton.Background = Brushes.Transparent;
        _agentPanel.VoiceButton.Content = "🎤";
        ToolTip.SetTip(_agentPanel.VoiceButton, "语音输入（识别结果自动发送并清空命令输入框）");
        if (autoSend)
        {
            _ = SendVoiceTextAfterAsync();
        }
    }

    /// <summary>稍后自动发送命令输入框内容（给正在识别中的最后一句留出返回时间）。</summary>
    private async Task SendVoiceTextAfterAsync()
    {
        await Task.Delay(500);
        if (_isVoiceInput) return;   // 期间又开始了新一轮输入则不发送
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => _agentPanel.SendInputText());
        }
        else
        {
            _agentPanel.SendInputText();
        }
    }

#endif

    private void FileCompare_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new FileCompareDialog();
        dialog.Show(this);
    }

    private void FolderCompare_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new FolderCompareDialog();
        dialog.Show(this);
    }

    private void HashCalc_Click(object? sender, RoutedEventArgs e)
    {
        OpenToolTab("信息摘要计算", new HashCalcView());
    }

    private void Crypto_Click(object? sender, RoutedEventArgs e)
    {
        OpenToolTab("加密/解密", new CryptoView());
    }

    private void About_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        dialog.ShowDialog(this);
    }

    /// <summary>菜单栏→赞助：弹出赞助窗口（微信支付 / 支付宝二维码）。</summary>
    private void Sponsor_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SponsorDialog();
        dialog.ShowDialog(this);
    }

    /// <summary>意见反馈页面的地址（url=aieditor 标识来源，username=anonymous 为匿名用户）。
    /// 注意：url 参数是后端约定的来源标识，改名后保持 aieditor 以免统计口径断裂。</summary>
    private const string FeedbackUrl = "https://login.app2s.cn/feedback?url=aieditor&username=anonymous";

    /// <summary>关于菜单→意见反馈：用系统默认浏览器打开反馈页面。</summary>
    private void Feedback_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = FeedbackUrl,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", FeedbackUrl);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = FeedbackUrl,
                    UseShellExecute = true
                });
            }
            StatusText.Text = "已在浏览器中打开意见反馈页面";
        }
        catch (Exception ex)
        {
            ShowMessageBox($"打开意见反馈页面失败：{ex.Message}", "错误", MessageBoxButtons.OK).ContinueWith(_ => { });
        }
    }


    /// <summary>关于菜单→检查新版本：手动调用 check-version 接口，有新版则弹出升级提示。</summary>
    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = LangService.T("正在检查新版本…");
            var result = await Services.UpdateChecker.CheckAsync();
            if (result.HasUpdate && Services.UpdateChecker.IsNewerVersion(result.NewVersion))
            {
                var dialog = new UpdateAvailableDialog(result.NewVersion);
                await dialog.ShowDialog(this);
            }
            else
            {
                await ShowMessageBox(LangService.T("当前已是最新版本"), LangService.T("检查新版本"), MessageBoxButtons.OK);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"{LangService.T("检查新版本失败")}：{ex.Message}", LangService.T("检查新版本"), MessageBoxButtons.OK);
        }
    }
    // ========== Local Terminal ==========
    /// <summary>视图菜单→终端：在主窗口新建一个本地命令行标签页（Windows 用 PowerShell，macOS/Linux 用系统默认 shell）。</summary>
    private void Terminal_Click(object? sender, RoutedEventArgs e)
    {
        OpenLocalTerminalTab();
    }

    private void OpenLocalTerminalTab()
    {
        // 支持多个终端标签：每次点击都新建一个，标题带序号区分
        int terminalCount = FileTabControl.Items.OfType<TabItem>().Count(item => GetTabTag(item) is LocalTerminalView);

        var view = new LocalTerminalView();
        string title = terminalCount == 0 ? LangService.T("终端") : LangService.TFormat("终端 {0}", terminalCount + 1);

        // 头部 / 关闭按钮 / 底色 / 右键菜单：与文件标签共用同一套样式
        var tabItem = new TabItem
        {
            Content = view,
            Background = NextTabColor(out var localColorIndex)
        };
        var tag = new SimpleTabTag(view, localColorIndex);
        tabItem.Tag = tag;
        var headerPanel = CreateTabHeader(title, CreateTabCloseButton(tag));
        headerPanel.ContextFlyout = CreateSimpleTabContextMenu(tabItem);
        tabItem.Header = headerPanel;

        view.CloseRequested += () => CloseTab(tabItem);
        FileTabControl.Items.Add(tabItem);
        FileTabControl.SelectedItem = tabItem;
        view.FocusTerminal();
    }

    // ========== SSH Terminal ==========
    /// <summary>在主窗口新建一个 SSH 终端标签页（替代原有独立弹窗）。</summary>
    private void OpenSshTab(SshRecentHostInfo? hostInfo)
    {
        var view = hostInfo == null
            ? new SshTerminalView()
            : new SshTerminalView(hostInfo.Host, hostInfo.Port, hostInfo.Username);

        // 标签标题
        string title = hostInfo == null
            ? "SSH 连接"
            : $"SSH: {hostInfo.Username}@{hostInfo.Host}:{hostInfo.Port}";

        // 头部 / 关闭按钮 / 底色 / 右键菜单：与文件标签共用同一套样式
        var tabItem = new TabItem
        {
            Content = view,
            Background = NextTabColor(out var sshColorIndex)
        };
        var tag = new SimpleTabTag(view, sshColorIndex);
        tabItem.Tag = tag;
        var headerPanel = CreateTabHeader(title, CreateTabCloseButton(tag));
        headerPanel.ContextFlyout = CreateSimpleTabContextMenu(tabItem);
        tabItem.Header = headerPanel;

        FileTabControl.Items.Add(tabItem);
        FileTabControl.SelectedItem = tabItem;
        view.CloseRequested += () => CloseTab(tabItem);
    }

    private void SshConnectNew_Click(object? sender, RoutedEventArgs e)
    {
        OpenSshTab(null);
    }

    private string SshRecentFilePath
    {
        get
        {
            var appDir = Services.AppPaths.LocalAppDataDir;
            Directory.CreateDirectory(appDir);
            return Path.Combine(appDir, "ssh_recent_hosts.json");
        }
    }

    private List<SshRecentHostInfo> LoadSshRecentHosts()
    {
        try
        {
            var path = SshRecentFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var hosts = JsonConvert.DeserializeObject<List<SshRecentHostInfo>>(json);
                return hosts ?? new List<SshRecentHostInfo>();
            }
        }
        catch { }
        return new List<SshRecentHostInfo>();
    }

    private void SaveSshRecentHosts(List<SshRecentHostInfo> hosts)
    {
        try
        {
            var json = JsonConvert.SerializeObject(hosts);
            File.WriteAllText(SshRecentFilePath, json);
        }
        catch { }
    }

    public void AddToSshRecentHosts(string host, int port, string username)
    {
        var hosts = LoadSshRecentHosts();

        // 移除已存在的相同主机记录
        hosts.RemoveAll(h => h.Host == host && h.Port == port && h.Username == username);

        // 添加到开头
        hosts.Insert(0, new SshRecentHostInfo
        {
            Host = host,
            Port = port,
            Username = username,
            LastConnected = DateTime.Now
        });

        // 只保留最近 10 个
        if (hosts.Count > 10)
        {
            hosts = hosts.Take(10).ToList();
        }

        SaveSshRecentHosts(hosts);
        RefreshSshRecentHostsMenu();
    }

    public void RefreshSshRecentHostsMenu()
    {
        // 最近连接已改为 SSH 连接窗口内的列表，不再需要菜单刷新
    }

    private void OpenSshConnection(SshRecentHostInfo hostInfo)
    {
        OpenSshTab(hostInfo);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshSshRecentHostsMenu();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // 先取消关闭，等待用户确认
        e.Cancel = true;

        // 释放远程 SFTP 连接
        _remoteSftp?.Dispose();
        _remoteSftp = null;

        var modifiedTabs = _tabs.Where(t => t.IsModified).ToList();
        if (modifiedTabs.Count > 0)
        {
            var result = await ShowMessageBox(
                $"有 {modifiedTabs.Count} 个文件已修改但未保存，是否保存？", "确认",
                MessageBoxButtons.YesNoCancel);

            if (result == MessageBoxResult.Cancel)
            {
                // 用户取消，保持窗口打开
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                foreach (var tab in modifiedTabs)
                {
                    await SaveTabAsync(tab);
                    if (tab.IsModified)
                    {
                        // 保存失败，保持窗口打开
                        return;
                    }
                }
            }
            // No 则直接关闭，不保存
        }

        // 关闭前终止所有终端会话（本地 shell 与 SSH），避免残留进程
        foreach (TabItem item in FileTabControl.Items)
        {
            if (GetTabTag(item) is LocalTerminalView localView)
            {
                await localView.ShutdownAsync();
            }
            else if (GetTabTag(item) is SshTerminalView sshView)
            {
                await sshView.ShutdownAsync();
            }
        }

        // 关闭前停止语音输入（释放麦克风）
#if VOICE_INPUT
        if (_isVoiceInput) StopVoiceInput();
#endif

        // 确认关闭，使用 Environment.Exit() 确保程序真正退出
        base.OnClosing(e);
        Environment.Exit(0);
    }

    // ========== Window Operations ==========

    protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyModifiers == KeyModifiers.Control)
            {
                switch (e.Key)
                {
                    case Key.N: NewFile(); break;
                    case Key.O: OpenFile_Click(null, null!); break;
                    case Key.S: Save_Click(null, null!); break;
                    case Key.W: CloseTab_Click(null, null!); break;
                    case Key.F: Find_Click(null, null!); break;
                    case Key.H: Replace_Click(null, null!); break;
                    case Key.G: GoToLine_Click(null, null!); break;
                    case Key.OemPlus: case Key.Add: ZoomIn_Click(null, null!); break;
                    case Key.OemMinus: case Key.Subtract: ZoomOut_Click(null, null!); break;
                    case Key.D0: case Key.NumPad0: ZoomReset_Click(null, null!); break;
                    case Key.K: FormatCode_Click(null, null!); break;
                }
            }
            // 折叠快捷键（VS Code 风格）：
            // Ctrl+Shift+[ / ] 折叠、展开当前块；Ctrl+Alt+[ / ] 折叠、展开全部块。
            // 注意要把编辑器内的折叠也纳入处理，故放在窗口级 OnKeyDown 并标记 Handled。
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                switch (e.Key)
                {
                    case Key.OemOpenBrackets: FoldCurrentBlock(); e.Handled = true; break;
                    case Key.OemCloseBrackets: UnfoldCurrentBlock(); e.Handled = true; break;
                }
            }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
            {
                switch (e.Key)
                {
                    case Key.OemOpenBrackets: FoldAllBlocks(); e.Handled = true; break;
                    case Key.OemCloseBrackets: UnfoldAllBlocks(); e.Handled = true; break;
                }
            }
        }

    // ========== Drag and Drop ==========

    private void InitDragDrop()
    {
        AddHandler(DragDrop.DragEnterEvent, DragEnter);
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
    }

    private void DragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Drop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles() ?? Enumerable.Empty<IStorageItem>();
            foreach (var file in files)
            {
                OpenFile(file.Path.LocalPath);
            }
        }
    }

    // ========== Window Title ==========

    public void UpdateTitle()
    {
        var tab = GetCurrentTab();
        string modified = tab?.IsModified == true ? "*" : "";
        string fileName = tab?.FileName ?? "Untitled";
        // 终端标签：Tag 内为 LocalTerminalView / SshTerminalView，非文件标签
        if (tab == null && FileTabControl.SelectedItem is TabItem sel && GetTabTag(sel) is LocalTerminalView)
        {
            fileName = "终端";
        }
        Title = $"TextFlowPlus - {fileName}{modified} ({_tabs.Count} tabs)";
    }

    #region 日志监控功能 (Tail -f)

    private void LogMonitor_Click(object sender, RoutedEventArgs e)
    {
        _fileDialogTask = LogMonitorAsync();
    }

    private async Task LogMonitorAsync()
    {
        // 如果正在监控，先停止
        if (_isLogMonitoring)
        {
            StopLogMonitor();
            return;
        }

        // 弹出文件对话框选择日志文件
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择日志文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("日志文件") { Patterns = new[] { "*.log", "*.txt", "*.log.*" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (files == null || files.Count == 0)
        {
            return;
        }

        var selectedPath = files[0].Path.LocalPath;

        try
        {
            // 以只读方式打开文件并读取内容
            string content;
            using (var fs = new FileStream(selectedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            // 创建只读的文本编辑器（黑屏白字主题）
            var textEditor = new TextEditor
            {
                IsReadOnly = true,  // 设置编辑器为只读
                FontFamily = new FontFamily("Consolas, Monaco, Menlo, monospace"),
                FontSize = 12,
                WordWrap = true,   // 自动折行（日志/查看内容长时更易读）
                ShowLineNumbers = true,
                Background = Brushes.Black,
                Foreground = Brushes.White
            };
            textEditor.Document.Text = content;

            // 添加日志着色器
            textEditor.TextArea.TextView.LineTransformers.Add(new LogHighlighter());

            // 创建新标签页打开日志文件（只读模式）
            var tab = new EditorTab(textEditor)
            {
                FilePath = selectedPath,
                IsModified = false,
                TabColor = GetTabColor(_colorIndex),
                TabColorIndex = _colorIndex % TabColors.Length
            };
            _colorIndex++;

            // 创建关闭按钮
            var closeButton = new Button
            {
                Content = "×",
                Width = 18,
                Height = 18,
                FontSize = 12,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
            };
            closeButton.Click += (s, e) =>
            {
                // 停止监控并关闭标签页
                StopLogMonitor();
                foreach (TabItem item in FileTabControl.Items)
                {
                    if (item.Tag == tab)
                    {
                        CloseTab(item);
                        break;
                    }
                }
            };

            // 创建带关闭按钮的标签页头部
            var headerPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            headerPanel.Children.Add(new TextBlock
            {
                Text = tab.FileName,
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            headerPanel.Children.Add(closeButton);

            // 创建标签页项
            var tabItem = new TabItem
            {
                Header = headerPanel,
                Tag = tab,
                Content = tab.TextEditor,
                Background = tab.TabColor
            };

            _tabs.Add(tab);
            FileTabControl.Items.Add(tabItem);
            FileTabControl.SelectedItem = tabItem;

            // 滚动到最后一行
            ScrollToLastNLines(tab, 1);

            // 开始监控
            _logMonitorFilePath = selectedPath;
            _lastFileLength = GetFileLengthSafe(selectedPath);

            _logMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _logMonitorTimer.Tick += LogMonitorTimer_Tick;
            _logMonitorTimer.Start();

            _isLogMonitoring = true;
            LogMonitorButton.SetValue(ToolTip.TipProperty, "停止日志监控");
            StatusText.Text = $"开始监控日志文件：{Path.GetFileName(selectedPath)}";
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"打开日志文件失败：{ex.Message}", "错误", MessageBoxButtons.OK);
        }
    }

    private void LogMonitorTimer_Tick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_logMonitorFilePath) || !File.Exists(_logMonitorFilePath))
        {
            StopLogMonitor();
            StatusText.Text = "日志文件不存在，已停止监控";
            return;
        }

        try
        {
            var currentLength = GetFileLengthSafe(_logMonitorFilePath);

            // 如果文件变小了（可能被截断），从头读取
            if (currentLength < _lastFileLength)
            {
                _lastFileLength = 0;
            }

            if (currentLength > _lastFileLength)
            {
                // 读取新增的内容
                var newContent = ReadFileTail(_logMonitorFilePath, _lastFileLength);
                if (!string.IsNullOrEmpty(newContent))
                {
                    var tab = GetCurrentTab();
                    if (tab != null && tab.FilePath == _logMonitorFilePath)
                    {
                        // 使用 Document 方式追加内容
                        var offset = tab.TextEditor.Document.TextLength;
                        tab.TextEditor.Document.Insert(offset, newContent);

                        // 防止文档无限增长：超出上限时丢弃前半部分旧内容
                        var totalLen = tab.TextEditor.Document.TextLength;
                        if (totalLen > LogMonitorMaxChars)
                        {
                            var removeCount = totalLen - LogMonitorMaxChars / 2;
                            // 对齐到行尾，避免截断半行
                            var removeEnd = removeCount;
                            // 向后找到第一个换行符，确保从行首开始保留
                            var line = tab.TextEditor.Document.GetLineByOffset(removeEnd);
                            removeEnd = line.Offset + line.Length;
                            if (removeEnd < totalLen)
                            {
                                tab.TextEditor.Document.Remove(0, removeEnd);
                            }
                        }

                        // 滚动到最后一行，光标始终停留在最后
                        ScrollToLastNLines(tab, 1);
                    }
                }
                _lastFileLength = currentLength;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"日志监控读取失败：{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"堆栈：{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 滚动到文档最后 N 行
    /// </summary>
    private void ScrollToLastNLines(EditorTab tab, int n)
    {
        var totalLines = tab.TextEditor.Document.LineCount;
        var targetLine = Math.Max(1, totalLines - n + 1);

        // 滚动到目标行
        tab.TextEditor.ScrollToLine(targetLine);

        // 设置光标到最后
        tab.TextEditor.CaretOffset = tab.TextEditor.Document.TextLength;
    }

    private void StopLogMonitor()
    {
        if (_logMonitorTimer != null)
        {
            _logMonitorTimer.Stop();
            _logMonitorTimer = null;
        }
        _isLogMonitoring = false;
        _logMonitorFilePath = null;
        LogMonitorButton.SetValue(ToolTip.TipProperty, "日志监控 (Tail -f)");
        StatusText.Text = "已停止日志监控";
    }

    private long GetFileLengthSafe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Length;
        }
        catch
        {
            return 0;
        }
    }

    private string ReadFileTail(string path, long startPosition)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = startPosition;
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取日志文件失败：{ex.Message}");
            return string.Empty;
        }
    }

    #endregion

    // ========== Helper Methods ==========

    private async Task<MessageBoxResult> ShowMessageBox(string text, string title, MessageBoxButtons buttons)
    {
        // 使用简单的消息框实现，直接使用 Window 作为所有者
        var avButtons = buttons switch
        {
            MessageBoxButtons.OK => 0,
            MessageBoxButtons.OKCancel => 1,
            MessageBoxButtons.YesNo => 2,
            MessageBoxButtons.YesNoCancel => 3,
            _ => 0
        };

        // 使用 Avalonia 内置的 MessageBox 静态方法
        var result = await MessageBoxUtil.Show(this, text, title, avButtons);
        return result switch
        {
            0 => MessageBoxResult.OK,
            1 => MessageBoxResult.Cancel,
            2 => MessageBoxResult.Yes,
            3 => MessageBoxResult.No,
            _ => MessageBoxResult.None
        };
    }
}

/// <summary>
/// 日志着色器 - 根据日志级别显示不同颜色
/// </summary>
public class LogHighlighter : DocumentColorizingTransformer
{
    // 日志级别颜色映射
    private static readonly Dictionary<string, Color> _levelColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        { "DEBUG", Color.FromRgb(170, 170, 170) },      // 灰色
        { "INFO", Color.FromRgb(0, 255, 0) },           // 绿色
        { "WARN", Color.FromRgb(255, 255, 0) },         // 黄色
        { "WARNING", Color.FromRgb(255, 255, 0) },      // 黄色
        { "ERROR", Color.FromRgb(255, 0, 0) },          // 红色
        { "FATAL", Color.FromRgb(255, 0, 128) },        // 紫红
        { "TRACE", Color.FromRgb(128, 128, 255) },      // 蓝色
    };

    protected override void ColorizeLine(DocumentLine line)
    {
        int lineStart = line.Offset;
        string lineText = CurrentContext.Document.GetText(line);

        // 查找日志级别
        foreach (var kvp in _levelColors)
        {
            string level = kvp.Key;
            int index = lineText.IndexOf(level);

            if (index >= 0)
            {
                // 检查是否是独立的日志级别（前后是空格或行首/行尾）
                bool isWordBoundary = true;
                if (index > 0)
                {
                    char prevChar = lineText[index - 1];
                    if (!char.IsWhiteSpace(prevChar) && prevChar != '[' && prevChar != '(')
                    {
                        isWordBoundary = false;
                    }
                }
                if (index + level.Length < lineText.Length)
                {
                    char nextChar = lineText[index + level.Length];
                    if (!char.IsWhiteSpace(nextChar) && nextChar != ']' && nextChar != ')' && nextChar != ':')
                    {
                        isWordBoundary = false;
                    }
                }

                if (isWordBoundary)
                {
                    // 设置日志级别颜色
                    base.ChangeLinePart(
                        lineStart + index,
                        lineStart + index + level.Length,
                        element =>
                        {
                            element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(kvp.Value));
                        });
                }
            }
        }

        // 高亮显示日期时间（格式：YYYY-MM-DD HH:MM:SS）
        Match dateTimeMatch = Regex.Match(lineText, @"\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}");
        if (dateTimeMatch.Success)
        {
            base.ChangeLinePart(
                lineStart + dateTimeMatch.Index,
                lineStart + dateTimeMatch.Index + dateTimeMatch.Length,
                element =>
                {
                    element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(255, 165, 0))); // 橙色
                });
        }
    }
}

// 美化后的 MessageBox 工具类：
// 自适应尺寸窗口 + 顶部主题色条 + 彩色图标（确认/询问 ❓、提示 ℹ️）+ 现代右对齐按钮区，
// 圆角按钮：主按钮（确定/是）蓝底白字带投影，次按钮（取消,否}). 支持 Entert/Esc 快捷键。
public static class MessageBoxUtil
{
    private static readonly Color AccentColor = Color.FromRgb(31, 111, 235);

    /// <summary>从当前主题资源取颜色画刷（显式按主题变体解析，与主窗口一致）。</summary>
    public static IBrush ThemeBrush(string key)
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

    public static async Task<int> Show(Window owner, string text, string title, int buttons)
    {
        var result = -1;

        var window = new Window
        {
            Title = title,
            MinWidth = 360,
            MaxWidth = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = ThemeBrush("EditorBackgroundBrush")
        };

        bool isQuestion = buttons == 2 || buttons == 3;

        // 顶部主题色条
        var accentBar = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(AccentColor),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        DockPanel.SetDock(accentBar, Dock.Top);

        // 消息区：彩色图标 + 自动换行文本
        var messagePanel = new Grid { Margin = new Thickness(22, 20, 22, 0) };
        messagePanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        messagePanel.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        var iconText = new TextBlock
        {
            Text = isQuestion ? "\u2753" : "\u2139\uFE0F",   // ❓ / ℹ️
            FontSize = 30,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0)
        };
        Grid.SetColumn(iconText, 0);
        messagePanel.Children.Add(iconText);

        var messageText = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            FontSize = 13,
            LineHeight = 21,
            Foreground = ThemeBrush("LabelForegroundBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(messageText, 1);
        messagePanel.Children.Add(messageText);

        // 按钮区（右对齐，主操作在右）
        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(22, 18, 22, 20)
        };
        DockPanel.SetDock(buttonPanel, Dock.Bottom);

        void CloseWith(int r)
        {
            result = r;
            window.Close();
        }

        switch (buttons)
        {
            case 0: // OK
                buttonPanel.Children.Add(CreateMessageButton("确定", primary: true, () => CloseWith(0)));
                break;
            case 1: // OKCancel
                buttonPanel.Children.Add(CreateMessageButton("取消", primary: false, () => CloseWith(1)));
                buttonPanel.Children.Add(CreateMessageButton("确定", primary: true, () => CloseWith(0)));
                break;
            case 2: // YesNo
                buttonPanel.Children.Add(CreateMessageButton("否", primary: false, () => CloseWith(3)));
                buttonPanel.Children.Add(CreateMessageButton("是", primary: true, () => CloseWith(2)));
                break;
            case 3: // YesNoCancel
                buttonPanel.Children.Add(CreateMessageButton("否", primary: false, () => CloseWith(3)));
                buttonPanel.Children.Add(CreateMessageButton("取消", primary: false, () => CloseWith(1)));
                buttonPanel.Children.Add(CreateMessageButton("是", primary: true, () => CloseWith(2)));
                break;
        }

        var root = new DockPanel();
        root.Children.Add(accentBar);
        root.Children.Add(buttonPanel);
        root.Children.Add(messagePanel);
        window.Content = root;

        // 键盘：Enter 触发主操作（确定/是），Esc 关闭（等价取消/否）
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CloseWith(buttons == 0 ? 0 : 2);
            }
            else if (e.Key == Key.Escape)
            {
                CloseWith(buttons switch
                {
                    1 or 3 => 1,   // 取消
                    2 => 3,        // 否
                    _ => 0         // 仅确定
                });
            }
        };

        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>创建对话框圆角按钮：主按钮（蓝底白字 + 投影）与次按钮（白底灰边），悬停变亮、按下加深。</summary>
    public static Border CreateMessageButton(string text, bool primary, Action onClick)
    {
        var blue = Color.FromRgb(31, 111, 235);
        // 次要按钮底色/文字取当前主题语义资源（暗色下不再白底）
        var normalBg = primary ? (IBrush)new SolidColorBrush(blue) : ThemeBrush("TextBoxBackgroundBrush");
        var normalBorder = primary ? (IBrush)new SolidColorBrush(blue) : ThemeBrush("ButtonSecondaryBorderBrush");
        var hoverBg = primary ? (IBrush)new SolidColorBrush(Color.FromRgb(48, 129, 240)) : ThemeBrush("SecondaryHoverBackgroundBrush");
        var hoverBorder = primary ? (IBrush)new SolidColorBrush(Color.FromRgb(48, 129, 240)) : new SolidColorBrush(Color.FromRgb(156, 195, 240));
        var pressedBg = primary ? (IBrush)new SolidColorBrush(Color.FromRgb(21, 92, 200)) : new SolidColorBrush(Color.FromRgb(226, 234, 245));
        var pressedBorder = primary ? (IBrush)new SolidColorBrush(Color.FromRgb(21, 92, 200)) : new SolidColorBrush(Color.FromRgb(156, 195, 240));
        var textBrush = primary ? (IBrush)Brushes.White : ThemeBrush("ButtonSecondaryForegroundBrush");

        var content = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = textBrush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var button = new Border
        {
            MinWidth = 84,
            Height = 34,
            Padding = new Thickness(18, 0),
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Effect = primary
                ? new DropShadowEffect
                {
                    BlurRadius = 8,
                    OffsetX = 0,
                    OffsetY = 1,
                    Opacity = 0.2,
                    Color = Colors.Black
                }
                : null
        };

        button.PointerEntered += (_, _) => { button.Background = hoverBg; button.BorderBrush = hoverBorder; };
        button.PointerExited += (_, _) => { button.Background = normalBg; button.BorderBrush = normalBorder; };
        button.PointerPressed += (_, _) => { button.Background = pressedBg; button.BorderBrush = pressedBorder; };
        button.PointerReleased += (_, _) => { button.Background = hoverBg; button.BorderBrush = hoverBorder; };
        button.AddHandler(InputElement.TappedEvent, (_, _) => onClick(), RoutingStrategies.Bubble);
        return button;
    }
}

// Temporary message box and helper classes
public enum MessageBoxButtons { OK, OKCancel, YesNo, YesNoCancel }
public enum MessageBoxResult { None, OK, Cancel, Yes, No }
