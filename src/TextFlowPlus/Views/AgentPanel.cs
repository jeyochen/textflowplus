using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using TextFlowPlus.Services.Localization;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;
using TextFlowPlus.Models;
using OpenAI;
using System.ClientModel;

namespace TextFlowPlus.Views;

/// <summary>
/// AI 助手停靠面板（参考 MachineAgent 的 ChatClientAgent 实现）。
/// - 聊天界面：消息列表 + 输入框 + 发送
/// - 使用 Microsoft.Agents.AI 的 ChatClientAgent + OllamaChatClient（OpenAI 兼容端点），
///   function calling 调用 TextFlowPlus 工具栏/菜单栏命令（由 MainWindow 注入工具）
/// - 配置（API URI / 模型名 / API Key）持久化到 %LOCALAPPDATA%\TextFlowPlus\agent_settings.json（兼容旧的 AIEditor 目录）；
///   设置界面底部有已设置模型列表（只显示 URL，不含默认模型信息），支持编辑/删除/应用
/// - 标题栏拖拽停靠逻辑由 MainWindow 处理（TitleBar / CloseButton 公开）
/// </summary>
public class AgentPanel : UserControl
{
    // ========== 对外接口（MainWindow 使用） ==========
    /// <summary>可拖动标题栏（MainWindow 挂指针事件实现停靠拖拽）</summary>
    public Border TitleBar { get; }
    /// <summary>关闭按钮（点击隐藏面板）</summary>
    public Button CloseButton { get; }
    /// <summary>面板关闭请求（MainWindow 隐藏面板并更新菜单勾选）</summary>
    public event EventHandler? CloseRequested;
    /// <summary>用户点击消息中的文件项（MainWindow 在新标签页打开该文件）</summary>
    public event Action<string>? FileClickRequested;
    /// <summary>本地命令"最近文件"提供者（MainWindow 注入，返回可点击文件列表文本）</summary>
    public Func<string>? GetRecentFilesText { get; set; }
    /// <summary>本地命令"字体 XX"处理器（MainWindow 注入，返回结果文本）</summary>
    public Func<int, string>? SetFontSizeHandler { get; set; }
    /// <summary>获取当前编辑器全文（MainWindow 注入；UI 线程调用，无编辑器时返回空串）</summary>
    public Func<string>? GetCurrentEditorText { get; set; }
    /// <summary>应用错别字高亮（MainWindow 注入；条目为 1 起始行/列坐标，跨线程安全由注入方保证）</summary>
    public Action<IReadOnlyList<TypoItem>>? ApplyTypoHighlights { get; set; }
    /// <summary>按当前文档类型执行格式化并返回结果文本（MainWindow 注入）</summary>
    public Func<string>? FormatCurrentDocument { get; set; }
    /// <summary>
    /// 本地格式化器是否支持当前文档类型（MainWindow 注入；UI 线程调用）。
    /// 返回 false 时回退到"大模型按文件类型格式化"。
    /// </summary>
    public Func<bool>? CanFormatCurrentDocumentLocally { get; set; }
    /// <summary>
    /// 用大模型给出的排版结果替换当前文档内容（MainWindow 注入；UI 线程调用，返回结果文本）。
    /// </summary>
    public Func<string, string>? ApplyFormatResult { get; set; }
    /// <summary>
    /// 取当前文档的 agent 编辑上下文（MainWindow 注入；UI 线程调用）。
    /// 返回 null 表示当前不可编辑（无文档/图片模式），面板会给出提示。
    /// </summary>
    public Func<MainWindow.AgentDocumentContext>? GetDocumentContext { get; set; }
    /// <summary>
    /// 应用大模型下发的文档编辑动作（MainWindow 注入；UI 线程调用），返回结果文本供面板显示。
    /// </summary>
    public Func<IReadOnlyList<DocumentEdit>, string>? ApplyDocumentEdits { get; set; }
    /// <summary>保存当前文档并返回结果文本（MainWindow 注入；有修改直接保存，新文件才弹保存窗口）</summary>
    public Func<string>? SaveCurrentDocument { get; set; }
    /// <summary>通用本地命令路由器（MainWindow 注入）：输入用户命令文本，命中返回结果文本，未命中返回 null</summary>
    public Func<string, string?>? LocalCommandRouter { get; set; }

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

    /// <summary>按当前主题刷新面板配色：面板与消息区背景跟随编辑区（EditorBackgroundBrush）。
    /// 主题切换时由主窗口调用。消息气泡用的是本地值（不随主题资源变化），
    /// 因此这里按新主题把已显示的消息整体重绘一遍。</summary>
    public void ApplyThemeColors()
    {
        // 主题切换由主窗口在 UI 线程发起，但保险起见仍统一调度到 UI 线程
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyThemeColors);
            return;
        }
        Background = ThemeBrush("EditorBackgroundBrush");
        _messagesScroll.Background = ThemeBrush("EditorBackgroundBrush");

        // 重绘既有消息（消息文本很小，重建开销可忽略），使气泡在深色主题下不再是一块刺眼亮色。
        // 流式消息此时取其当前已到达的文本重建，重建后重新挂上渲染目标以便继续增量更新。
        _messagesPanel.Children.Clear();
        foreach (var entry in _entries)
        {
            if (entry.Stream != null)
            {
                entry.Text = entry.Stream.Body;
                ResetStreamTargets(entry);
            }
            _messagesPanel.Children.Add(BuildMessageVisual(entry));
        }
        ScrollToEnd();
    }

    /// <summary>清空流式消息上的渲染目标引用（主题重绘前调用，避免指向已脱离可视树的旧控件）。</summary>
    private static void ResetStreamTargets(ChatEntry entry)
    {
        entry.BodyHost = null;
        entry.ThinkingHost = null;
        entry.ThinkingText = null;
        entry.ThinkingHeader = null;
        entry.ThinkingScroll = null;
        entry.ThinkingBox = null;
        entry.RenderedBody = null;   // 控件已重建，需按当前文本重新渲染一次
    }

    /// <summary>按当前界面语言刷新标题与按钮文本。</summary>
    public void ApplyTexts()
    {
        if (_titleText != null) _titleText.Text = LangService.T("🤖 AI 助手");
        ToolTip.SetTip(_voiceButton, LangService.T("语音输入（点击开始/停止，识别结果插入当前文档）"));
        ToolTip.SetTip(_configToggle, LangService.T("模型设置"));
        ToolTip.SetTip(_thinkToggle, LangService.T("显示思考过程（模型支持推理链时实时展示）"));
        ToolTip.SetTip(_stopButton, LangService.T("停止生成"));
        _sendButton.Content = LangService.T("发送");
        _stopButton.Content = LangService.T("停止");
        ApplyThinkToggleVisual();
    }

    /// <summary>思考开关的视觉状态：开启时高亮为强调色，关闭时保持标题栏半透明。</summary>
    private void ApplyThinkToggleVisual()
    {
        _thinkToggle.Opacity = _thinkToggle.IsChecked == true ? 1.0 : 0.45;
        ToolTip.SetTip(_thinkToggle, _thinkToggle.IsChecked == true
            ? LangService.T("思考过程：开（点击关闭）")
            : LangService.T("思考过程：关（点击开启）"));
    }

    // ========== 流式生成控制 ==========

    /// <summary>切换"生成中"界面状态：生成时隐藏发送、显示停止按钮。</summary>
    private void SetBusyUi(bool busy)
    {
        _sendButton.IsVisible = !busy;
        _stopButton.IsVisible = busy;
    }

    /// <summary>取消当前流式请求（"停止"按钮 / 关闭会话）。</summary>
    private void CancelCurrentRequest()
    {
        try { _streamCts?.Cancel(); }
        catch (ObjectDisposedException) { /* 请求已自然结束 */ }
    }

    /// <summary>把一段文本作为一条流式消息显示，并返回可增量写入的消息对象。</summary>
    private StreamingMessage BeginStreamingMessage(string role, string initialText = "", bool stretch = false)
    {
        var msg = new StreamingMessage { Role = role, Stretch = stretch };
        var entry = new ChatEntry { Role = role, Stretch = stretch, Stream = msg };
        if (initialText.Length > 0) msg.AppendBody(initialText);
        // 先落地可视树，再挂回调：确保 Sink 首次触发时目标控件已存在
        AddEntry(entry);
        msg.Sink = OnStreamPaint;
        return msg;
    }

    // ========== 消息区配色与字体 ==========

    /// <summary>消息区正文/界面字体（macOS 上回退苹方，Windows 上回退微软雅黑）。</summary>
    private const string MessageFontFamily = "Segoe UI, Microsoft YaHei UI, PingFang SC, Hiragino Sans GB";
    /// <summary>代码字体（Consolas 无中文字形，回退微软雅黑，避免中文显示为方块）。</summary>
    private const string CodeFontFamily = "Consolas, Menlo, Microsoft YaHei UI, Courier New";
    /// <summary>消息正文字号（比原先的 13.5 放大，长文阅读更省力）。</summary>
    private const double MessageFontSize = 15;
    /// <summary>消息正文行高（15px 字号的舒适行距，避免中英文挤在一起）。</summary>
    private const double MessageLineHeight = 23;
    /// <summary>代码块字号与行高。</summary>
    private const double CodeFontSize = 14;
    private const double CodeLineHeight = 21;

    /// <summary>距底部多少像素内仍视为"在底部"，用于自动跟随判断。</summary>
    private const double AutoFollowThreshold = 24;

    /// <summary>
    /// 消息区配色：同一套语义色在浅色/深色下分别取值。
    /// 深色下必须使用深色气泡 + 浅色文字，否则大模型输出的长段回答会变成刺眼的亮色块。
    /// </summary>
    private sealed record ChatTheme(
        Color AssistantBg, Color AssistantBorder, Color AssistantFg,
        Color SystemBg, Color SystemBorder, Color SystemFg,
        Color UserBg, Color UserBorder, Color UserFg,
        Color CodeBg, Color CodeBorder, Color CodeFg, Color CodeLabel,
        Color InlineCodeFg, Color QuoteBar, Color QuoteFg,
        Color Accent, Color Muted, Color Divider,
        Color ThinkBg, Color ThinkBorder, Color ThinkFg, Color ThinkLabel)
    {
        /// <summary>浅色主题：白底上以极浅灰蓝气泡 + 细边框划分层次，正文近黑保证对比度。
        /// 思考区用略带暖黄的浅底，与正文气泡区分开（一眼可辨"这是推理过程，不是答案"）。</summary>
        public static readonly ChatTheme Light = new(
            AssistantBg: Color.FromRgb(0xF2, 0xF5, 0xF9), AssistantBorder: Color.FromRgb(0xDC, 0xE3, 0xEC), AssistantFg: Color.FromRgb(0x16, 0x1B, 0x22),
            SystemBg: Color.FromRgb(0xFF, 0xF6, 0xDF), SystemBorder: Color.FromRgb(0xEF, 0xDD, 0xA4), SystemFg: Color.FromRgb(0x6B, 0x52, 0x00),
            UserBg: Color.FromRgb(0x2D, 0x5A, 0xA0), UserBorder: Color.FromRgb(0x24, 0x4C, 0x8A), UserFg: Colors.White,
            CodeBg: Color.FromRgb(0xE9, 0xED, 0xF3), CodeBorder: Color.FromRgb(0xD5, 0xDD, 0xE7), CodeFg: Color.FromRgb(0x1F, 0x29, 0x37), CodeLabel: Color.FromRgb(0x86, 0x90, 0x9F),
            InlineCodeFg: Color.FromRgb(0xB5, 0x31, 0x5D),
            QuoteBar: Color.FromRgb(0xC3, 0xCD, 0xDA), QuoteFg: Color.FromRgb(0x50, 0x5B, 0x69),
            Accent: Color.FromRgb(0x0A, 0x5B, 0xD3), Muted: Color.FromRgb(0x6A, 0x74, 0x82), Divider: Color.FromRgb(0xD8, 0xDE, 0xE6),
            ThinkBg: Color.FromRgb(0xF7, 0xF4, 0xEC), ThinkBorder: Color.FromRgb(0xE4, 0xDC, 0xC8), ThinkFg: Color.FromRgb(0x5C, 0x55, 0x45), ThinkLabel: Color.FromRgb(0x8A, 0x7D, 0x62));

        /// <summary>深色主题：气泡略亮于 #1E1E1E 面板底，正文浅灰白，代码块反而更暗以形成"凹陷"层次。
        /// 思考区用更暗的暖灰底，不与正文气泡抢视觉重心。</summary>
        public static readonly ChatTheme Dark = new(
            AssistantBg: Color.FromRgb(0x26, 0x29, 0x2D), AssistantBorder: Color.FromRgb(0x37, 0x3C, 0x42), AssistantFg: Color.FromRgb(0xE4, 0xE7, 0xEA),
            SystemBg: Color.FromRgb(0x33, 0x30, 0x1F), SystemBorder: Color.FromRgb(0x4A, 0x45, 0x2C), SystemFg: Color.FromRgb(0xEA, 0xDC, 0xA4),
            UserBg: Color.FromRgb(0x2D, 0x5A, 0xA0), UserBorder: Color.FromRgb(0x3C, 0x74, 0xC4), UserFg: Colors.White,
            CodeBg: Color.FromRgb(0x1A, 0x1D, 0x20), CodeBorder: Color.FromRgb(0x37, 0x3C, 0x42), CodeFg: Color.FromRgb(0xD7, 0xDC, 0xE3), CodeLabel: Color.FromRgb(0x7E, 0x88, 0x94),
            InlineCodeFg: Color.FromRgb(0xF0, 0x7E, 0xB0),
            QuoteBar: Color.FromRgb(0x4A, 0x51, 0x58), QuoteFg: Color.FromRgb(0xB0, 0xB8, 0xC0),
            Accent: Color.FromRgb(0x6C, 0xB6, 0xFF), Muted: Color.FromRgb(0x98, 0xA1, 0xAB), Divider: Color.FromRgb(0x3A, 0x3F, 0x45),
            ThinkBg: Color.FromRgb(0x1F, 0x20, 0x1C), ThinkBorder: Color.FromRgb(0x3A, 0x3A, 0x30), ThinkFg: Color.FromRgb(0xB6, 0xAE, 0x99), ThinkLabel: Color.FromRgb(0x8C, 0x84, 0x6E));

        /// <summary>当前主题对应的一套配色。</summary>
        public static ChatTheme Current =>
            Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Dark : Light;
    }

    private static IBrush Brush(Color color) => new SolidColorBrush(color);

    // ========== 配置 ==========
    /// <summary>默认模型服务（内部使用，不向用户显示）。用户未自定义配置时使用。</summary>
    private const string DefaultApiUri = "http://8.130.49.113:11435";
    private const string DefaultModelName = "qwen2.5:3b";
    private const string DefaultApiKey = "";
    // 用户自定义配置（为空表示未设置 → 使用默认模型）
    private string _apiUri = "";
    private string _modelName = "";
    private string _apiKey = "";
    private readonly string _settingsPath = Services.AppPaths.LocalAppDataFile("agent_settings.json");

    /// <summary>实际使用的 API URI：用户未设置时回退默认值。</summary>
    private string EffectiveApiUri => string.IsNullOrWhiteSpace(_apiUri) ? DefaultApiUri : _apiUri;
    /// <summary>实际使用的模型名：用户未设置时回退默认值。</summary>
    private string EffectiveModelName => string.IsNullOrWhiteSpace(_modelName) ? DefaultModelName : _modelName;

    // ========== Agent ==========
    private ChatClientAgent? _agent;
    private readonly object _agentLock = new();
    private List<AITool> _commandTools = new();
    private readonly List<ChatMessage> _history = new();
    /// <summary>聊天历史最大保留条数（超出时丢弃最旧消息对，防止 token 无限增长导致模型超时/OOM/卡死）。</summary>
    private const int MaxHistoryMessages = 40;
    private bool _busy;
    private bool _warmUpStarted;
    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(120);

    /// <summary>是否启用思考过程输出（模型支持 reasoning 时生效；可在设置中关闭）。</summary>
    private bool _showThinking = true;

    /// <summary>当前正在进行的流式请求的取消源（"停止"按钮据此中断生成）。</summary>
    private CancellationTokenSource? _streamCts;

    /// <summary>当前活跃的流式消息（停止时用于定稿，避免残留半截气泡）。</summary>
    private StreamingMessage? _activeStream;

    /// <summary>配置是否已从磁盘加载完成（加载前禁止回写，避免覆盖既有配置）。</summary>
    private bool _configLoaded;

    /// <summary>
    /// 是否自动跟随最新消息（流式输出时滚动到底）。
    /// 用户手动向上滚动查看历史后置为 false，滚回底部后恢复 true，
    /// 避免"正在读上文却被流式内容不断拽到底部"。
    /// </summary>
    private bool _autoFollow = true;

    // ========== 命令历史（输入框上下箭头翻阅） ==========
    private readonly List<string> _commandHistory = new();
    private int _historyIndex = -1;
    private string? _pendingInput;   // 离开末尾位置时暂存未提交的输入

    // ========== UI 控件 ==========
    private readonly StackPanel _messagesPanel;
    private readonly ScrollViewer _messagesScroll;

    /// <summary>已显示的消息（主题切换时按新配色整体重绘；重绘不改变会话历史 _history）。</summary>
    private readonly List<ChatEntry> _entries = new();

    /// <summary>
    /// 一条已显示的消息。除静态消息外，还承载"流式消息"的落地状态：
    /// - <see cref="Stream"/> 非空表示这条消息正在（或曾经）流式输出，
    ///   <see cref="BodyVisual"/> / <see cref="ThinkingHost"/> 等为增量渲染的目标控件；
    /// - 主题切换重绘时用 <see cref="Text"/> 最终文本重建即可，无需保留控件。
    /// </summary>
    private sealed class ChatEntry
    {
        public string Role { get; init; } = "assistant";
        public string Text { get; set; } = "";
        public bool Stretch { get; init; }

        /// <summary>流式消息对象（静态消息为 null）。</summary>
        public StreamingMessage? Stream { get; init; }

        // ---- 以下为流式渲染的实时目标控件（仅 UI 线程访问）----

        /// <summary>正文本体容器：流式期间整体重排 Markdown。</summary>
        public StackPanel? BodyHost { get; set; }

        /// <summary>思考区外层（可折叠 <c>Expander</c>）。</summary>
        public Expander? ThinkingHost { get; set; }

        /// <summary>思考区文本控件（实时追加）。</summary>
        public SelectableTextBlock? ThinkingText { get; set; }

        /// <summary>思考区标题（显示字数/状态）。</summary>
        public TextBlock? ThinkingHeader { get; set; }

        /// <summary>思考区滚动容器（跟随最新内容自动滚到底）。</summary>
        public ScrollViewer? ThinkingScroll { get; set; }

        /// <summary>思考区容器（定稿后若无有效思考内容则隐藏）。</summary>
        public Border? ThinkingBox { get; set; }

        /// <summary>上一次已渲染进 <see cref="BodyHost"/> 的正文，用于跳过无变化的重排。</summary>
        public string? RenderedBody { get; set; }
    }

    private readonly TextBox _inputBox;
    private readonly TextBlock? _titleText;
    private readonly Button _sendButton;
    /// <summary>停止生成按钮：流式输出期间可见，点击中断当前请求。</summary>
    private readonly Button _stopButton;
    /// <summary>思考过程开关（标题栏 🧠）：控制是否请求/显示模型推理链。</summary>
    private readonly ToggleButton _thinkToggle;
    private readonly Grid _configArea;
    private readonly TextBox _uriBox;
    private readonly TextBox _modelBox;
    private readonly TextBox _apiKeyBox;
    private readonly ToggleButton _configToggle;
    private readonly Button _voiceButton;
    /// <summary>已设置的模型列表（不含默认模型信息；URI 为空或等于默认服务地址的条目不进入列表）。</summary>
    private readonly List<AgentSettings> _savedModels = new();
    private StackPanel _savedModelsPanel = null!;

    /// <summary>语音输入按钮（位于标题栏设置按钮 ⚙ 之前；视觉状态由 MainWindow 更新）。</summary>
    public Button VoiceButton => _voiceButton;

    /// <summary>语音输入按钮被点击时触发（MainWindow 注入开始/停止逻辑）。</summary>
    public event EventHandler? VoiceInputClicked;

    public AgentPanel()
    {
        // 宽度由停靠列宽控制（MainWindow ApplyDockLayout 设置 Width=NaN 拉伸填充），最小 300px
        MinWidth = 300;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // ---- 标题栏 ----
        TitleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 90, 160))
        };
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _titleText = new TextBlock
        {
            Text = LangService.T("🤖 AI 助手"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_titleText!, 0);
        titleGrid.Children.Add(_titleText!);

        // 语音输入按钮（位于设置按钮前面；点击行为由 MainWindow 注入）
        _voiceButton = new Button
        {
            Content = "🎤",
            Width = 30,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 2, 0)
        };
        ToolTip.SetTip(_voiceButton, LangService.T("语音输入（点击开始/停止，识别结果插入当前文档）"));
        _voiceButton.Click += (_, _) => VoiceInputClicked?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(_voiceButton, 1);
        titleGrid.Children.Add(_voiceButton);

        // 思考过程开关：选中时请求模型输出推理链并实时展示（模型不支持时自动降级为状态步骤）
        _thinkToggle = new ToggleButton
        {
            Content = "🧠",
            Width = 30,
            Height = 28,
            IsChecked = _showThinking,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 2, 0)
        };
        ToolTip.SetTip(_thinkToggle, LangService.T("显示思考过程（模型支持推理链时实时展示）"));
        _thinkToggle.IsCheckedChanged += (_, _) =>
        {
            _showThinking = _thinkToggle.IsChecked == true;
            ApplyThinkToggleVisual();
            // 立即持久化：下次启动保持用户选择。
            // LoadConfig 回填开关时会触发本回调，但那时尚未加载完成，不能反写磁盘（否则会清掉已有配置）。
            if (!_configLoaded) return;
            try { SaveConfigToDisk(); } catch { /* 写盘失败不影响本次会话 */ }
        };
        ApplyThinkToggleVisual();
        Grid.SetColumn(_thinkToggle, 2);
        titleGrid.Children.Add(_thinkToggle);

        _configToggle = new ToggleButton
        {
            Content = "⚙",
            Width = 30,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 2, 0)
        };
        ToolTip.SetTip(_configToggle, LangService.T("模型设置"));
        Grid.SetColumn(_configToggle, 3);
        titleGrid.Children.Add(_configToggle);

        CloseButton = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(CloseButton, 4);
        titleGrid.Children.Add(CloseButton);

        TitleBar.Child = titleGrid;
        root.Children.Add(TitleBar);

        // ---- 消息区 ----
        _messagesPanel = new StackPanel { Spacing = 8, Margin = new Thickness(8, 8, 8, 8) };
        _messagesScroll = new ScrollViewer
        {
            Content = _messagesPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            // 背景色由 ApplyThemeColors 按当前主题设置（与编辑区一致）
        };
        // 用户向上翻阅历史时暂停自动跟随，滚回底部附近再恢复
        _messagesScroll.ScrollChanged += (_, _) =>
        {
            var offset = _messagesScroll.Offset.Y;
            var remaining = _messagesScroll.Extent.Height - _messagesScroll.Viewport.Height - offset;
            _autoFollow = remaining <= AutoFollowThreshold;
        };
        Grid.SetRow(_messagesScroll, 1);
        root.Children.Add(_messagesScroll);

        // ---- 配置区（默认折叠） ----
        _configArea = new Grid
        {
            IsVisible = false,
            Margin = new Thickness(6)
        };
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 0: 标题行（右上角关闭按钮）
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 1: API URI
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 2: 模型名
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 3: API Key
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 4: 保存/默认按钮行
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 5: 已设置模型标题
        _configArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 6: 已设置模型列表
        _configArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _configArea.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        // 标题行：文案 + 右上角关闭按钮（点击折叠设置界面）
        var configTitleGrid = new Grid();
        configTitleGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        configTitleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var configTitle = new TextBlock
        {
            Text = "模型设置",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(configTitle, 0);
        configTitleGrid.Children.Add(configTitle);
        var configCloseBtn = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 22,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(configCloseBtn, "关闭模型设置");
        configCloseBtn.Click += (_, _) => _configToggle.IsChecked = false;
        Grid.SetColumn(configCloseBtn, 1);
        configTitleGrid.Children.Add(configCloseBtn);
        Grid.SetColumnSpan(configTitleGrid, 2);
        Grid.SetRow(configTitleGrid, 0);
        _configArea.Children.Add(configTitleGrid);

        _uriBox = AddConfigRow(1, "API URI");
        ToolTip.SetTip(_uriBox, "OpenAI 兼容端点基础地址，如 http://host:port（自动补 /v1）；" +
            "或 Ollama 地址 http://host:11434。填了 API Key 时按 OpenAI 兼容协议连接。");
        _modelBox = AddConfigRow(2, "模型名");
        _apiKeyBox = AddConfigRow(3, "API Key");
        _uriBox.PlaceholderText = "（留空使用默认模型）";
        _modelBox.PlaceholderText = "（留空使用默认模型）";
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var saveBtn = new Button { Content = "保存并连接" };
        saveBtn.Click += (_, _) => SaveConfig();
        var defaultBtn = new Button { Content = "默认模型", Margin = new Thickness(6, 0, 0, 0) };
        defaultBtn.Click += (_, _) => UseDefaultModel();
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(defaultBtn);
        Grid.SetRow(btnRow, 4);
        Grid.SetColumn(btnRow, 1);
        _configArea.Children.Add(btnRow);

        // 已设置的模型列表（只显示 URL；默认模型的信息绝不显示）
        var savedTitle = new TextBlock
        {
            Text = "— 已设置的模型 —",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)),
            Margin = new Thickness(0, 6, 0, 2)
        };
        Grid.SetRow(savedTitle, 5);
        Grid.SetColumnSpan(savedTitle, 2);
        _configArea.Children.Add(savedTitle);

        _savedModelsPanel = new StackPanel { Spacing = 2 };
        var savedScroll = new ScrollViewer
        {
            Content = _savedModelsPanel,
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(savedScroll, 6);
        Grid.SetColumnSpan(savedScroll, 2);
        _configArea.Children.Add(savedScroll);

        _configToggle.IsCheckedChanged += (_, _) =>
        {
            _configArea.IsVisible = _configToggle.IsChecked == true;
            if (_configToggle.IsChecked == true)
            {
                _uriBox.Text = _apiUri;
                _modelBox.Text = _modelName;
                _apiKeyBox.Text = _apiKey;
            }
        };

        Grid.SetRow(_configArea, 2);
        root.Children.Add(_configArea);

        // ---- 输入区 ----
        // 左右边距各 5px（合计 10px）：输入框 + 发送按钮 = agent 面板宽度 - 10px，
        // 输入框使用 Star 列，随面板宽度变化自动伸缩
        var inputPanel = new Grid { Margin = new Thickness(5, 2, 5, 6) };
        inputPanel.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        inputPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        _inputBox = new TextBox
        {
            PlaceholderText = "输入命令或问题，如：帮我格式化代码 / 查找 json",
            AcceptsReturn = false,
            FontSize = 14,
            Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        // 显式启用输入法（IME），确保输入法组合态（拼音预编辑）正常处理
        InputMethod.SetIsInputMethodEnabled(_inputBox, true);
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendMessage();
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                NavigateHistory(-1);
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                NavigateHistory(1);
            }
        };
        _sendButton = new Button
        {
            Content = LangService.T("发送"),
            Width = 56,
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _sendButton.Click += (_, _) => SendMessage();

        // 停止按钮：仅流式生成期间可见（不占位时不显示，避免拥挤）
        _stopButton = new Button
        {
            Content = LangService.T("停止"),
            Width = 56,
            IsVisible = false,
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(_stopButton, LangService.T("停止生成"));
        _stopButton.Click += (_, _) => CancelCurrentRequest();

        Grid.SetColumn(_sendButton, 1);
        inputPanel.Children.Add(_inputBox);
        inputPanel.Children.Add(_sendButton);
        inputPanel.Children.Add(_stopButton);
        Grid.SetColumn(_stopButton, 1);

        Grid.SetRow(inputPanel, 3);
        root.Children.Add(inputPanel);

        Content = root;
        ApplyThemeColors();
        LangService.LanguageChanged += ApplyTexts;
        ApplyTexts();

        LoadConfig();
        AddMessage("assistant", "你好，我是TextFlowPlus小助手 🤖\n我可以帮你执行各种命令，例如：\n· 新建/打开/保存文件\n· 文件对比/哈希计算/加密解密\n· 直接修改当前打开的文档：如“把第 3 行改成 xxx”\n  “把所有 foo 替换成 bar”“删除第 5-7 行”“帮我修正这段代码”\n· 格式化当前文档（按文件类型自动选择）\n· 更多命令输入 help 查看\n· 也可以输入文字直接执行\n", stretch: true);
    }

    // ========== 配置 ==========

    private TextBox AddConfigRow(int row, string label)
    {
        var tb = new TextBox { FontSize = 12, Height = 28 };
        var lb = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(lb, row);
        Grid.SetColumn(lb, 0);
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, 1);
        _configArea.Children.Add(lb);
        _configArea.Children.Add(tb);
        return tb;
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
            var cfg = JsonSerializer.Deserialize<AgentSettings>(json);
            if (cfg != null)
            {
                if (!string.IsNullOrWhiteSpace(cfg.ApiUri)) _apiUri = cfg.ApiUri;
                if (!string.IsNullOrWhiteSpace(cfg.ModelName)) _modelName = cfg.ModelName;
                _apiKey = cfg.ApiKey ?? "";
                _showThinking = cfg.ShowThinking;
                if (_thinkToggle != null) _thinkToggle.IsChecked = _showThinking;
                if (cfg.SavedModels != null)
                {
                    _savedModels.AddRange(cfg.SavedModels.Where(IsListableSavedModel));
                }
            }
            // 归一化：用户配置恰好等于默认模型信息时视为"未设置"，
            // 确保默认模型的 URL 和模型名称绝不在界面显示
            if (string.Equals(_apiUri, DefaultApiUri, StringComparison.OrdinalIgnoreCase)) _apiUri = "";
            if (string.Equals(_modelName, DefaultModelName, StringComparison.OrdinalIgnoreCase)) _modelName = "";

            // 兼容旧配置文件：列表中还没有任何记录但当前为自定义模型时，自动补录一条
            if (_savedModels.Count == 0 && !string.IsNullOrWhiteSpace(_apiUri))
            {
                _savedModels.Add(new AgentSettings { ApiUri = _apiUri, ModelName = _modelName, ApiKey = _apiKey });
            }
            RenderSavedModels();
        }
        catch { /* 配置损坏则使用默认值 */ }
        finally { _configLoaded = true; }
    }

    private void SaveConfig()
    {
        try
        {
            _apiUri = _uriBox.Text?.Trim() ?? "";
            _modelName = _modelBox.Text?.Trim() ?? "";
            _apiKey = _apiKeyBox.Text ?? "";

            // 维护已设置模型列表：URI 非空且非默认地址才入列表；同 URI 视为编辑更新，否则新增
            if (IsListableSavedModel(new AgentSettings { ApiUri = _apiUri }))
            {
                var existing = _savedModels.FirstOrDefault(m =>
                    string.Equals(m.ApiUri, _apiUri, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.ModelName = _modelName;
                    existing.ApiKey = _apiKey;
                }
                else
                {
                    _savedModels.Add(new AgentSettings { ApiUri = _apiUri, ModelName = _modelName, ApiKey = _apiKey });
                }
            }

            SaveConfigToDisk();
            RenderSavedModels();
            _agent = null;   // 配置变更后重建
            _history.Clear();
            if (string.IsNullOrWhiteSpace(_modelName) && string.IsNullOrWhiteSpace(_apiUri))
                AddMessage("system", "✅ 已保存配置（将使用默认模型）");
            else
                AddMessage("system", $"✅ 已保存模型配置：{_modelName}");
        }
        catch (Exception ex)
        {
            AddMessage("system", $"❌ 保存配置失败：{ex.Message}");
        }
        finally
        {
            _configToggle.IsChecked = false;   // 保存后关闭设置界面
        }
    }

    /// <summary>使用默认模型：清空用户自定义配置（不向用户展示默认模型信息）。</summary>
    private void UseDefaultModel()
    {
        try
        {
            _uriBox.Clear();
            _modelBox.Clear();
            _apiKeyBox.Clear();
            _apiUri = "";
            _modelName = "";
            _apiKey = "";
            SaveConfigToDisk();
            _agent = null;   // 下次连接使用默认模型
            _history.Clear();
            AddMessage("system", "✅ 已使用默认模型");
        }
        catch (Exception ex)
        {
            AddMessage("system", $"❌ 切换默认模型失败：{ex.Message}");
        }
        finally
        {
            _configToggle.IsChecked = false;   // 使用默认模型后关闭设置界面
        }
    }

    private void SaveConfigToDisk()
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new AgentSettings
        {
            ApiUri = _apiUri,
            ModelName = _modelName,
            ApiKey = _apiKey,
            SavedModels = new List<AgentSettings>(_savedModels),
            ShowThinking = _showThinking
        }));
    }

    // ========== 已设置的模型列表 ==========

    /// <summary>条目是否可进入列表：URI 非空且不等于默认模型服务地址（默认模型信息不外显）。</summary>
    private static bool IsListableSavedModel(AgentSettings m)
    {
        return !string.IsNullOrWhiteSpace(m.ApiUri)
            && !string.Equals(m.ApiUri.Trim(), DefaultApiUri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>重绘已设置的模型列表：每行显示 URL + 编辑/删除/应用按钮。</summary>
    private void RenderSavedModels()
    {
        _savedModelsPanel.Children.Clear();
        if (_savedModels.Count == 0)
        {
            _savedModelsPanel.Children.Add(new TextBlock
            {
                Text = "（暂无已保存的模型）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                Margin = new Thickness(0, 2, 0, 2)
            });
            return;
        }
        for (int i = 0; i < _savedModels.Count; i++)
        {
            var model = _savedModels[i];
            var index = i;   // 闭包按项捕获索引

            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var urlText = new TextBlock
            {
                Text = model.ApiUri,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(urlText, string.IsNullOrWhiteSpace(model.ModelName)
                ? model.ApiUri
                : $"{model.ModelName}\n{model.ApiUri}");
            Grid.SetColumn(urlText, 0);
            row.Children.Add(urlText);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            btnPanel.Children.Add(MakeMiniButton("编辑", () => EditSavedModel(index)));
            btnPanel.Children.Add(MakeMiniButton("删除", () => DeleteSavedModel(index)));
            btnPanel.Children.Add(MakeMiniButton("应用", () => ApplySavedModel(index)));
            Grid.SetColumn(btnPanel, 1);
            row.Children.Add(btnPanel);

            _savedModelsPanel.Children.Add(row);
        }
    }

    /// <summary>创建列表操作小按钮（编辑/删除/应用）。</summary>
    private static Button MakeMiniButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 10.5,
            Height = 20,
            Padding = new Thickness(6, 0),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>编辑：把该模型的配置回填到上方输入框，用户修改后点"保存并连接"即更新列表并应用。</summary>
    private void EditSavedModel(int index)
    {
        if (index < 0 || index >= _savedModels.Count) return;
        var m = _savedModels[index];
        _uriBox.Text = m.ApiUri;
        _modelBox.Text = m.ModelName;
        _apiKeyBox.Text = m.ApiKey ?? "";
        _uriBox.Focus();
    }

    /// <summary>删除：从已设置模型列表移除并持久化（不影响当前正在使用的连接）。</summary>
    private void DeleteSavedModel(int index)
    {
        if (index < 0 || index >= _savedModels.Count) return;
        _savedModels.RemoveAt(index);
        try { SaveConfigToDisk(); }
        catch { /* 删除无需提示写盘失败 */ }
        RenderSavedModels();
    }

    /// <summary>应用：把该模型设为当前使用模型并关闭模型设置窗口。</summary>
    private void ApplySavedModel(int index)
    {
        if (index < 0 || index >= _savedModels.Count) return;
        var m = _savedModels[index];
        _apiUri = m.ApiUri;
        _modelName = m.ModelName;
        _apiKey = m.ApiKey ?? "";
        _uriBox.Text = _apiUri;   // 输入框同步为当前模型，下次打开设置依然一致
        _modelBox.Text = _modelName;
        _apiKeyBox.Text = _apiKey;
        try
        {
            SaveConfigToDisk();
            _agent = null;   // 下次连接使用该模型
            _history.Clear();
            AddMessage("system", string.IsNullOrWhiteSpace(_modelName)
                ? $"✅ 已应用模型：{_apiUri}"
                : $"✅ 已应用模型：{_modelName}");
        }
        catch (Exception ex)
        {
            AddMessage("system", $"❌ 应用模型失败：{SanitizeError(ex.Message)}");
        }
        finally
        {
            _configToggle.IsChecked = false;   // 应用后关闭模型设置窗口
        }
    }

    // ========== Agent ==========

    /// <summary>注入命令工具列表（MainWindow 构建：工具栏/菜单栏命令 + 插入文本等）。</summary>
    public void SetCommandTools(IEnumerable<AITool> tools)
    {
        _commandTools = tools.ToList();
    }

    /// <summary>获取 agent 系统指令（TextFlowPlus 场景）。</summary>
    private static string GetAgentInstructions()
    {
        return """
            你是 TextFlowPlus 文本编辑器内置的 AI 助手。用户会直接对你说话（自然语言命令或问题）。

            【重要】始终使用中文回复，简洁明了。

            【修改当前打开的文档（重要能力）】
            当用户要求**改动当前文档的内容**时（例如"把第 3 行改成 xxx""把所有的 foo 替换成 bar"
            "删除第 5-7 行""在开头插入一行""这段代码有错，帮我修正""把这段话翻译成英文并写入文档"
            "排查错误并修复"），程序会自动把当前文档（带行号）交给你，并以结构化 JSON 编辑动作的方式
            应用你的修改。此时：
            - 不要调用任何工具，也不要回答"我无法直接修改文档"；
            - 直接给出编辑动作（程序会提供专门的编辑指令与文档内容）；
            - 修改必须是**用户明确要求**的最小改动，不要顺手改写其它内容。
            用户说"修改/改成/替换/插入/删除/重写/润色/修正/修复"等针对文档内容的动词时，
            一律按"修改当前文档"处理，而不是当作闲聊或只给出建议。

            【格式化】
            - 用户说"格式化 / 格式化代码 / 整理格式"→ 内置本地命令：按当前文档类型
              （JSON/XAML/Markdown/YAML/C#/C++/Go/Rust/Java/Proto/JS/TS/Python/HTML/CSS/Vue/NSIS）
              自动选择对应语言的格式化，不要调用工具；
            - 当前文件类型本地格式化器不支持时（如 .txt/.ini/.conf/.log/.sql/.bat/.ps1 等），
              程序会自动回退到"由你按该文件类型规范重新排版"，此时同样不要调用工具。

            【执行编辑器命令】
            当用户提出执行类需求时，调用对应的命令工具（function calling）：
            - 新建/打开/保存/另存为/关闭文件 → 用户说"保存/保存文件"→ 内置本地命令：有修改直接静默保存，新文件才弹保存窗口（不要调用工具）；另存为/关闭 → SaveFileAs / CloseTab
            - 编辑：撤销/重做/剪切/复制/粘贴/全选 → Undo / Redo / Cut / Copy / Paste / SelectAll
            - 查找/替换 → Find / Replace（工具会弹出对应对话框）
            - 用户说"格式化 / 格式化代码 / 整理格式"→ 内置本地命令：按当前文档类型自动选择对应语言的格式化；本地不支持的类型会自动回退给模型处理，不要调用工具
            - 跳转 → GoToStart / GoToEnd
            - 缩放 → ZoomIn / ZoomOut / ZoomReset
            - JSON 操作 → JsonFormat / JsonMinify / JsonValidate
            - 对比 → FileCompare / FolderCompare
            - 工具 → HashCalc / Crypto / HttpDebugger / LogMonitor / SshConnect / OpenRemoteWindow
            - 编码 → SaveAsAnsi / SaveAsUtf8 / SaveAsUtf8Bom / ConvertToAnsi / ConvertToUtf8 / ConvertToUtf8Bom
            - 显示选项 → ShowTabs / ShowSpaces / ShowEndOfLine / ToggleToolbar / ToggleStatusbar / ToggleFolderPanel / ToggleWordWrap
            - 文件 → OpenFolder（打开本地文件夹）/ OpenRemoteWindow（打开远程连接窗口）
            - 语言 → SetLanguage(language)，参数为 C#/C++/Go/Rust/Java/Python/JavaScript/TypeScript/HTML/CSS/Vue/XAML/YAML/JSON/Markdown/Proto/NSIS/Log/PlainText 之一；用户也可直接说“语言 C++”“使用Python语言”“语法高亮json”“日志格式高亮”
            - 把指定内容写进当前文档 → InsertText(text)
            - 用户说"字体 XX"或"字体设置到 XX"（XX 是数字）→ SetFontSize(size)，size 必须是数字；非数字不要调用
            - 用户说"字体 XX"且 XX 是字体名称（Consolas/宋体/微软雅黑/等线等）→ SetFontFamily(family)
            - 用户说"打开最近文件 / 文件列表 / flist"→ ListRecentFiles，返回的列表行以 📄 开头，用户可点击打开

            【内置本地路由（无需调用工具，直接说命令即可）】
            - 跳转：跳转到开头/结尾（或尾部）、跳转到第 N 行（超出范围自动钳制）、放大/缩小/重置缩放
            - 文档：新建/打开文件/打开文件夹/打开远程窗口/关闭文件/另存为
            - 编辑：撤销/重做/剪切/复制/粘贴/全选/查找/替换
            - 视图：显示/隐藏工具栏·状态栏·AI助手·文件夹列表、自动换行、显示/隐藏制表符·空格·行尾标记
            - 工具：文件对比/文件夹对比/日志监控/信息摘要(哈希)/加密解密/HTTP调试/SSH连接
            - 编码：以 GB2312/UTF-8/UTF-8 BOM 编码保存、转换为对应编码
            - JSON：JSON 格式化/JSON 压缩/JSON 语法检查
            - 语言：语言 C++、使用Python语言、语法高亮json（可直接设置当前文档语言）
            - 字体：字体 宋体（设置字体家族）、字体 16（设置字号）
            - 其他：关于（关于对话框）、退出/关闭程序（退出 TextFlowPlus）
            - 清空：用户输入 clear / 清空 / 清屏 → 清除 agent 窗口全部内容（无需调用工具）

            【简洁命令（令牌式，输入 help 查看全部）】
            - new/open/save/close、undo/redo/cut/copy/paste、find/replace、format、top/end/lineN、diff/ddiff、zin/zout、log、agent、clear/cls、help
            - 令牌不区分大小写，可带 / 前缀（如 /new）；用户输入这些令牌时直接执行对应工具栏功能
            - 用户说"检查错别字 / 错别字检查 / 检查错字 / 校对"→ 这是内置本地命令，由程序直接处理（调用模型校对并高亮错字），不要调用工具、也不要自行回复检查结果

            【聊天回答】
            如果用户纯粹提问或闲聊，直接回答即可。涉及编辑器使用问题也可以直接解答。

            【规则】
            不要编造不存在的工具调用。工具执行结果会以文本返回，正常回复用户即可。
            """;
    }

    /// <summary>确保 agent 已构建（用户配置优先，未设置则使用默认模型，默认信息不向用户展示）。
    /// 线程安全：可能在后台线程（预热、发送、校对）与 UI 线程（配置变更后置空）并发调用。
    /// 协议选择：配置了 API Key → OpenAI 兼容网关（/v1/chat/completions + Bearer 鉴权）；
    /// 未配置 Key → Ollama 原生协议（/api/chat，默认模型与纯 Ollama 服务）。</summary>
    private void EnsureAgent()
    {
        if (_agent != null) return;
        lock (_agentLock)
        {
            if (_agent != null) return;
            try
            {
                var chatClient = BuildChatClient();
                _agent = new ChatClientAgent(
                    chatClient,
                    name: "TextFlowPlus 助手",
                    description: "TextFlowPlus 文本编辑器内置 AI 助手，可通过 function calling 执行编辑器工具栏/菜单栏命令",
                    instructions: GetAgentInstructions(),
                    tools: _commandTools);
                // 仅当用户自定义过模型名时显示模型名，默认模型信息不外露
                AddMessage("system", string.IsNullOrWhiteSpace(_modelName) ? "✅ 已连接模型" : $"✅ 已连接模型：{_modelName}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"模型连接配置无效：{SanitizeError(ex.Message)}", ex);
            }
        }
    }

    /// <summary>按用户配置构建聊天客户端：有 API Key 用 OpenAI 兼容客户端，否则用 Ollama 客户端。</summary>
    private IChatClient BuildChatClient()
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            // OpenAI 兼容网关（one-api / new-api / Ollama /v1 等）：OpenAIClient 请求 {端点}/v1/chat/completions 并附带 Bearer Key
            var endpoint = NormalizeOpenAiEndpoint(EffectiveApiUri);
            var openAiClient = new OpenAIClient(new ApiKeyCredential(_apiKey), new OpenAIClientOptions { Endpoint = endpoint });
            return openAiClient.GetChatClient(EffectiveModelName).AsIChatClient();
        }

        // Ollama 原生协议（默认模型服务与纯 Ollama 部署）
        return new OllamaChatClient(new Uri(EffectiveApiUri), modelId: EffectiveModelName);
    }

    /// <summary>把用户填写的 API 地址归一化为 OpenAI SDK 要求的端点：
    /// OpenAI SDK 把端点视为 API root 并拼接 /chat/completions，因此确保以 /v1 结尾
    /// （http://host:port → http://host:port/v1；http://host:port/v1 保持原样）。</summary>
    private static Uri NormalizeOpenAiEndpoint(string apiUri)
    {
        var uri = new Uri(apiUri.Trim().TrimEnd('/'));
        if (!uri.AbsolutePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri(uri, "v1");
        }
        return uri;
    }

    /// <summary>程序启动后预连接模型：构建 agent 并发送一次极简请求，触发模型加载/连接，
    /// 避免用户发送第一个指令时才冷启动连接（导致第一条指令等待或超时不执行）。</summary>
    public void WarmUpModel()
    {
        if (_warmUpStarted) return;
        _warmUpStarted = true;
        _ = WarmUpModelAsync();
    }

    private async Task WarmUpModelAsync()
    {
        try
        {
            // 等窗口渲染完成、命令工具注册完毕后再预热
            await Task.Delay(1200).ConfigureAwait(false);
            AddMessage("system", "⏳ 正在连接模型…");
            EnsureAgent();
            if (_agent == null) return;

            // 极简 ping：仅触发连接与模型加载，结果丢弃，不写入聊天历史
            var warmMessages = new List<ChatMessage> { new(ChatRole.User, "ping") };
            var options = new ChatOptions { MaxOutputTokens = 4 };
            using var cts = new CancellationTokenSource(ModelTimeout);
            await _agent.ChatClient.GetResponseAsync(warmMessages, options, cts.Token).ConfigureAwait(false);
            // EnsureAgent 已输出 “✅ 已连接模型”，此处不再重复
        }
        catch (Exception ex)
        {
            AddMessage("system", $"⚠️ 模型预热失败（发送指令时将自动重试）：{SanitizeError(ex.Message)}");
        }
    }

    /// <summary>过滤错误消息中的默认模型 URL，避免默认服务地址外泄。</summary>
    private static string SanitizeError(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        return message.Replace(DefaultApiUri, "默认模型服务");
    }

    /// <summary>把语音识别文本追加到命令输入框末尾（空格分隔），光标移到末尾并聚焦。</summary>
    public void AppendVoiceText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        var current = _inputBox.Text ?? "";   // TextBox.Text 可为 null（空输入框）
        if (current.Length > 0 && !current.EndsWith(" "))
        {
            current += " ";
        }
        current += trimmed;
        _inputBox.Text = current;
        _inputBox.CaretIndex = _inputBox.Text?.Length ?? 0;
        _inputBox.Focus();
    }

    /// <summary>发送命令输入框当前内容（等价于点击发送/回车；空内容或忙时忽略）。</summary>
    public void SendInputText()
    {
        if (string.IsNullOrWhiteSpace(_inputBox.Text)) return;
        SendMessage();
    }

    /// <summary>左键点击发送按钮：发送当前输入框内容。</summary>
    private void SendMessage()
    {
        var input = _inputBox.Text?.Trim();
        if (string.IsNullOrEmpty(input)) return;
        if (_busy)
        {
            // 忙时不静默丢弃：提示用户，输入框内容保留（语音自动发送被跳过时可手动重发）
            AddMessage("system", "⏳ AI 助手正在处理上一条命令，请稍后再发送。");
            return;
        }

        // 记录命令历史（相邻重复不重复记录，最多保留 100 条）
        if (_commandHistory.Count == 0 || _commandHistory[^1] != input)
        {
            _commandHistory.Add(input);
            if (_commandHistory.Count > 100) _commandHistory.RemoveAt(0);
        }
        _historyIndex = _commandHistory.Count;
        _pendingInput = null;

        // 本地命令：清空 agent 窗口全部内容（clear / 清空 / 清屏），连命令气泡也不留下
        if (IsClearCommand(input))
        {
            _inputBox.Clear();
            ClearConversation();
            return;
        }

        _inputBox.Clear();
        AddMessage("user", input);

        // 本地命令：错别字检查（用独立会话调用模型，不写入聊天历史，避免污染对话上下文）
        if (IsTypoCheckCommand(input))
        {
            // 文本文档模型不可跨线程访问：在 UI 线程捕获快照后再交给后台任务
            var snapshot = GetCurrentEditorText?.Invoke() ?? "";
            _ = Task.Run(() => CheckTyposAsync(snapshot));
            return;
        }

        // 本地命令路由（不依赖模型）：flist/文件列表/字体 XX 等直接处理，
        // 避免小模型 function calling 不稳定导致命令无响应
        if (TryRouteLocalCommand(input, out var localReply))
        {
            _history.Add(new ChatMessage(ChatRole.User, input));
            _history.Add(new ChatMessage(ChatRole.Assistant, localReply));
            TrimHistory();
            return;
        }

        // 文档编辑意图：把当前打开文档（带行号）交给大模型，由模型产出结构化编辑动作后本地安全应用。
        // 必须在通用对话流程之前判断，否则"把第 3 行改成 XXX"会被当成普通问答而不会真正改文档。
        if (IsDocumentEditIntent(input))
        {
            _ = Task.Run(() => ProcessDocumentEditAsync(input));
            return;
        }

        _ = Task.Run(() => ProcessAsync(input));
    }

    // ========== 文档编辑（大模型修改当前打开文档） ==========

    /// <summary>
    /// 识别"要改动当前文档内容"的意图（而非打开对话框、纯问答等）。
    /// 命中后走文档编辑专用流程：把当前文档（带行号）交给模型，
    /// 模型返回结构化编辑动作，由本地安全执行（内容级校验 + 单次撤销）。
    /// </summary>
    private static bool IsDocumentEditIntent(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return false;

        // 纯令牌/极短命令（如 "替换"、"格式化"、"save"）属于本地命令范畴，不当作文档编辑意图，
        // 避免与工具栏令牌命令抢语义（本地路由本身在本方法之前执行，这里是二次兜底）。
        if (s.Length <= 3 && !s.Contains(' ') && !s.Contains('行') && !s.Contains('第')) return false;

        // 明确的编辑动词
        string[] verbs =
        {
            "修改", "改成", "改为", "更正", "修正", "订正", "改一下", "改掉", "换成",
            "替换", "替代", "改成", "写入", "插入", "增加", "添加", "追加", "补充",
            "删除", "删掉", "去掉", "移除", "删去", "清理掉",
            "重写", "改写", "润色", "优化", "完善", "扩写", "续写", "精简", "缩略",
            "翻译成", "翻译为", "转成", "转换为", "整理成", "转写",
            "统一", "规范化", "补全", "补齐", "修正", "排查并修复", "修复",
            "帮我把", "帮我将", "把文档", "把文件", "把这段", "把当前",
            "在文档", "在文件", "在开头", "在结尾", "在第一行", "在最后",
            "每一行", "所有行", "每行", "全部替换", "全文替换",
        };
        foreach (var v in verbs)
            if (s.Contains(v, StringComparison.Ordinal)) return true;

        // 英文动词
        var lower = s.ToLowerInvariant();
        string[] enVerbs = { "replace", "rewrite", "change the", "edit the", "insert ", "delete ", "fix the" };
        foreach (var v in enVerbs)
            if (lower.Contains(v, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// 文档编辑系统指令：要求模型严格输出结构化 JSON 编辑动作，
    /// 且原文片段必须逐字来自文档，避免臆造内容造成误改。
    /// </summary>
    private static string GetDocumentEditInstructions(string language, string fileName, string ext) => $$"""
        你是 TextFlowPlus 内嵌的文档编辑助手。用户会要求你修改当前打开的文档。
        现在请针对**当前打开的文档**给出精确的修改方案。

        【输出格式】
        只输出一个 JSON 对象，不要输出任何解释文字、注释或 Markdown 代码块标记：
        {
          "summary": "一句话说明你做了什么改动",
          "question": "",
          "edits": [
            {"kind":"replace_text","oldText":"文档中真实存在的原文","newText":"替换后的内容","occurrence":0,"reason":"改动原因"},
            {"kind":"replace_lines","startLine":3,"endLine":5,"newText":"整体替换后的新内容","reason":"原因"},
            {"kind":"insert_lines","afterLine":10,"newText":"要插入的内容","reason":"原因"},
            {"kind":"delete_lines","startLine":7,"endLine":8,"reason":"原因"}
          ]
        }

        【动作类型说明】
        - replace_text：把文档中 oldText 替换为 newText。occurrence 指定替换第几次出现（1 起始），填 0 表示全部替换。
        - replace_lines：把第 startLine 到 endLine 行（含两端，行号 1 起始）整体替换为 newText。
        - insert_lines：在第 afterLine 行之后插入 newText；afterLine 填 0 表示插入到文档开头。
        - delete_lines：删除第 startLine 到 endLine 行。
        - replace_document：把整篇文档替换为 newText。**仅在用户明确要求"整个文件重写/全部重写"时才使用。**

        【必须遵守的规则】
        1. oldText 必须是文档中**逐字存在**的原文（可直接复制文档内容），不要凭记忆转述、不要改动标点或空格。
           如不确定原文，改用 replace_lines 按行号定位。
        2. 行号以文档中标注的"行号│内容"的行号为准，从 1 开始。
        3. 多个互不重叠的改动，请在 edits 中用多条动作一次给出（从后往前或互不影响均可，我会按顺序安全应用）。
        4. 用户没有要求改动的部分，**绝对不要**顺手改写、不要"优化"措辞、不要调整格式。
        5. 只做用户明确要求的事；找不到目标时，把 question 写上你的疑问并让 edits 为空数组。
        6. newText 中可以包含 \n 表示换行。
        7. 一律使用中文填写 summary 和 reason。

        【当前文档信息】
        - 文件名：{{fileName}}
        - 文档类型：{{language}}{{(!string.IsNullOrEmpty(ext) ? $"（扩展名 {ext}）" : "")}}

        请根据随后提供的文档内容（每行前缀为行号）给出修改方案。
        """;

    /// <summary>
    /// 文档编辑主流程：取当前文档上下文 → 调用模型产出编辑动作 → 本地安全应用 → 汇总回复。
    /// 使用独立会话（不写入聊天历史），避免文档正文污染多轮对话上下文。
    /// </summary>
    private async Task ProcessDocumentEditAsync(string input)
    {
        _busy = true;
        Dispatcher.UIThread.Post(() => SetBusyUi(true));
        try
        {
            // 文档模型不可跨线程访问：必须在 UI 线程取上下文快照
            MainWindow.AgentDocumentContext? ctx = null;
            Dispatcher.UIThread.Invoke(() => ctx = GetDocumentContext?.Invoke());

            if (ctx == null || !ctx.Available)
            {
                var reason = ctx?.UnavailableReason ?? "当前没有可编辑的文档。";
                AddMessage("assistant", $"📄 {reason}");
                return;
            }
            if (string.IsNullOrWhiteSpace(ctx.Content) || ctx.Content == "（当前文档为空）")
            {
                AddMessage("assistant", "📄 当前文档为空，请先输入内容或打开文件。");
                return;
            }

            EnsureAgent();
            var instructions = GetDocumentEditInstructions(ctx.Language, ctx.FileName, ctx.Extension);
            var userContent = new StringBuilder();
            userContent.AppendLine($"用户的修改要求：{input}");
            userContent.AppendLine();
            userContent.AppendLine($"当前文档（共 {ctx.LineCount} 行，格式为“行号│内容”）：");
            userContent.AppendLine(ctx.Content);
            if (ctx.Truncated)
            {
                userContent.AppendLine();
                userContent.AppendLine($"（注意：文档较长，上面只提供了前 {AgentDocPromptLineLimit} 行的内容，"
                    + "超出部分未展示。若要修改未展示的部分，请在 question 中说明，不要臆造内容。）");
            }

            using var cts = new CancellationTokenSource(ModelTimeout);
            ChatResponse response;
            try
            {
                response = await _agent!.ChatClient.GetResponseAsync(
                    new List<ChatMessage>
                    {
                        new(ChatRole.System, instructions),
                        new(ChatRole.User, userContent.ToString())
                    },
                    new ChatOptions { MaxOutputTokens = 4096 },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => AddMessage("system", "⏰ 模型响应超时，请重试或简化修改要求。"));
                return;
            }

            var plan = ParseEditPlan(response?.Text ?? "");
            if (plan == null)
            {
                Dispatcher.UIThread.Post(() => AddMessage("assistant",
                    "⚠️ 模型没有返回可识别的修改方案，请换一种说法重试（例如明确说明要改哪一行、改成什么）。"));
                return;
            }

            // 模型有疑问且没给动作：直接把问题抛给用户
            if (plan.Edits.Count == 0)
            {
                var msg = !string.IsNullOrWhiteSpace(plan.Question)
                    ? $"❓ {plan.Question!.Trim()}"
                    : (!string.IsNullOrWhiteSpace(plan.Summary)
                        ? $"ℹ️ {plan.Summary!.Trim()}"
                        : "ℹ️ 模型认为当前不需要修改文档。");
                Dispatcher.UIThread.Post(() => AddMessage("assistant", msg));
                return;
            }

            // 在 UI 线程应用编辑（编辑器文档访问必须在 UI 线程）
            string applyResult = "";
            Dispatcher.UIThread.Invoke(() => applyResult = ApplyDocumentEdits?.Invoke(plan.Edits) ?? "❌ 文档编辑器未就绪。");

            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrWhiteSpace(plan.Summary))
                    AddMessage("assistant", plan.Summary!.Trim());
                AddMessage("assistant", applyResult);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => AddMessage("system", $"❌ 修改文档失败：{SanitizeError(ex.Message)}"));
        }
        finally
        {
            _busy = false;
            Dispatcher.UIThread.Post(() => SetBusyUi(false));
        }
    }

    /// <summary>提示里声明的可用文档行数上限（与 MainWindow.AgentDocMaxLines 保持一致）。</summary>
    private const int AgentDocPromptLineLimit = 400;

    /// <summary>解析模型返回的编辑计划：容忍 ```json 围栏与前后多余文字。</summary>
    private static DocumentEditPlan? ParseEditPlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int s = raw.IndexOf('{');
        int e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw.Substring(s, e - s + 1));
            var root = doc.RootElement;
            var plan = new DocumentEditPlan
            {
                Summary = GetStringProp(root, "summary"),
                Question = GetStringProp(root, "question"),
            };

            if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in edits.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var edit = ParseEdit(item);
                    if (edit != null) plan.Edits.Add(edit);
                }
            }
            return plan;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析单条编辑动作；类型名无法识别时返回 null。</summary>
    private static DocumentEdit? ParseEdit(JsonElement item)
    {
        var kindRaw = GetStringProp(item, "kind") ?? GetStringProp(item, "type") ?? GetStringProp(item, "action");
        if (string.IsNullOrWhiteSpace(kindRaw)) return null;

        var kind = kindRaw.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_") switch
        {
            "replace_text" or "replace" or "replace_all" => DocumentEditKind.ReplaceText,
            "replace_lines" or "replace_line_range" or "replace_range" => DocumentEditKind.ReplaceLines,
            "insert_lines" or "insert" or "insert_after" or "insert_text" => DocumentEditKind.InsertLines,
            "delete_lines" or "delete" or "remove_lines" or "remove" => DocumentEditKind.DeleteLines,
            "replace_document" or "rewrite_document" or "replace_all_document" => DocumentEditKind.ReplaceDocument,
            _ => (DocumentEditKind?)null,
        };
        if (kind == null) return null;

        var edit = new DocumentEdit
        {
            Kind = kind.Value,
            // 兼容模型可能使用的多种字段命名
            OldText = GetStringProp(item, "oldText") ?? GetStringProp(item, "old_text")
                ?? GetStringProp(item, "original") ?? GetStringProp(item, "search"),
            NewText = GetStringProp(item, "newText") ?? GetStringProp(item, "new_text")
                ?? GetStringProp(item, "replacement") ?? GetStringProp(item, "replace") ?? GetStringProp(item, "content"),
            StartLine = GetIntProp(item, "startLine", "start_line", "start", "from"),
            EndLine = GetIntProp(item, "endLine", "end_line", "end", "to"),
            AfterLine = GetIntProp(item, "afterLine", "after_line", "after", "line"),
            Occurrence = GetIntProp(item, "occurrence", "index", "nth"),
            Reason = GetStringProp(item, "reason") ?? GetStringProp(item, "description"),
        };

        // 行区间类动作允许只给 startLine（视为单行）
        if (edit.Kind is DocumentEditKind.ReplaceLines or DocumentEditKind.DeleteLines && edit.EndLine <= 0)
            edit.EndLine = edit.StartLine;

        return edit;
    }

    /// <summary>读取字符串属性（属性不存在或非字符串返回 null）。</summary>
    private static string? GetStringProp(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    /// <summary>读取整数属性（支持数字与数字字符串；缺失返回 0）。</summary>
    private static int GetIntProp(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        }
        return 0;
    }

    // ========== 大模型按文件类型格式化（本地格式化器不支持的类型） ==========

    /// <summary>
    /// 本地格式化器不支持当前文件类型时，交给大模型按其文件类型/语言规范重新排版。
    /// 与文档编辑一样使用独立会话；模型返回 <c>{"formatted":"..."}</c>，
    /// 由 MainWindow 侧替换文档内容（单次撤销）。
    /// </summary>
    private async Task FormatDocumentWithModelAsync()
    {
        _busy = true;
        Dispatcher.UIThread.Post(() => SetBusyUi(true));
        try
        {
            MainWindow.AgentDocumentContext? ctx = null;
            Dispatcher.UIThread.Invoke(() => ctx = GetDocumentContext?.Invoke());

            if (ctx == null || !ctx.Available)
            {
                AddMessage("assistant", $"📄 {ctx?.UnavailableReason ?? "当前没有可格式化的文档。"}");
                return;
            }
            if (string.IsNullOrWhiteSpace(ctx.Content) || ctx.Content == "（当前文档为空）")
            {
                AddMessage("assistant", "📄 当前文档为空，无需格式化。");
                return;
            }

            // 交给模型的正文不带行号前缀（格式化需要原样文本，行号会污染内容）
            string source = "";
            Dispatcher.UIThread.Invoke(() => source = GetCurrentEditorText?.Invoke() ?? "");
            if (string.IsNullOrWhiteSpace(source))
            {
                AddMessage("assistant", "📄 当前文档为空，无需格式化。");
                return;
            }

            const int MaxFormatChars = 8000;
            bool truncated = source.Length > MaxFormatChars;
            if (truncated)
            {
                AddMessage("assistant", "⚠️ 文档较长，格式化仅处理前 8000 字符。");
                source = source.Substring(0, MaxFormatChars);
            }

            EnsureAgent();
            var instructions = GetModelFormatInstructions(ctx.Language, ctx.FileName, ctx.Extension);

            using var cts = new CancellationTokenSource(ModelTimeout);
            ChatResponse response;
            try
            {
                response = await _agent!.ChatClient.GetResponseAsync(
                    new List<ChatMessage>
                    {
                        new(ChatRole.System, instructions),
                        new(ChatRole.User, source)
                    },
                    new ChatOptions { MaxOutputTokens = 8192 },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => AddMessage("system", "⏰ 模型响应超时，请重试。"));
                return;
            }

            var formatted = ParseFormattedText(response?.Text ?? "");
            if (formatted == null)
            {
                Dispatcher.UIThread.Post(() => AddMessage("assistant",
                    "⚠️ 模型没有返回可用的格式化结果，请重试。"));
                return;
            }
            if (string.Equals(formatted, source, StringComparison.Ordinal))
            {
                Dispatcher.UIThread.Post(() => AddMessage("assistant", "ℹ️ 文档格式已经是规范的，无需改动。"));
                return;
            }

            string applyResult = "";
            Dispatcher.UIThread.Invoke(() =>
                applyResult = ApplyFormatResult?.Invoke(formatted) ?? "❌ 文档编辑器未就绪。");

            Dispatcher.UIThread.Post(() =>
            {
                AddMessage("assistant", $"✅ 已由大模型按 {ctx.Language} 格式完成格式化。");
                AddMessage("assistant", applyResult);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => AddMessage("system", $"❌ 格式化失败：{SanitizeError(ex.Message)}"));
        }
        finally
        {
            _busy = false;
            Dispatcher.UIThread.Post(() => SetBusyUi(false));
        }
    }

    /// <summary>
    /// 让模型按其文件类型/语言规范重新排版，仅输出排版后的完整文本（JSON 包裹，便于提取）。
    /// </summary>
    private static string GetModelFormatInstructions(string language, string fileName, string ext) => $$"""
        你是代码与文档格式化引擎。请严格按照该文件类型/语言的通用排版规范，重新整理下面这段内容。

        【当前文件】
        - 文件名：{{fileName}}
        - 文档类型：{{language}}{{(!string.IsNullOrEmpty(ext) ? $"（扩展名 {ext}）" : "")}}

        【排版要求】
        1. 只做"格式/排版"层面的调整：缩进、换行、层级、对齐、空行、标点全半角等。
        2. **严禁改变内容的语义**：不得增删改任何文字、标识符、数值、字符串内容、注释文字。
        3. 不得添加解释、说明、前后缀、Markdown 代码块标记（不要输出 ```）。
        4. 保留原有的换行风格与语言语法有效性（例如 JSON/YAML/代码必须仍然可解析）。
        5. 按该语言社区通用规范缩进（如 C 系语言 4 空格、YAML 2 空格、JSON 2 空格）；
           若原文缩进风格明显统一，则沿用原风格。

        【输出格式】
        只输出一个 JSON 对象，把排版后的完整内容放在 formatted 字段中（换行用 \n 转义），不要输出其他任何文字：
        {"formatted":"格式化后的完整内容"}
        """;

    /// <summary>从模型回复中提取 formatted 字段（容忍代码块围栏与前后多余文字）。</summary>
    private static string? ParseFormattedText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        int s = raw.IndexOf('{');
        int e = raw.LastIndexOf('}');
        if (s >= 0 && e > s)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw.Substring(s, e - s + 1));
                var value = GetStringProp(doc.RootElement, "formatted");
                if (value != null) return value;
            }
            catch
            {
                // 不是 JSON：继续按纯文本回退处理
            }
        }

        // 回退：模型直接输出了排版结果（去除 ``` 围栏）
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
                text = text.Substring(firstNewline + 1, lastFence - firstNewline - 1).TrimEnd('\r', '\n');
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>上下箭头翻阅历史命令：Up 显示上一条，Down 显示下一条，回到末尾恢复未提交输入。</summary>
    private void NavigateHistory(int direction)
    {
        if (_commandHistory.Count == 0) return;

        var newIndex = _historyIndex + direction;

        // 边界：Up 最小到 0（最早命令），Down 最大到 Count（末尾空输入）
        if (direction < 0)
        {
            if (newIndex < 0) return;
        }
        else
        {
            if (newIndex > _commandHistory.Count) return;
        }

        // 离开末尾位置时暂存当前未提交的输入
        if (_historyIndex == _commandHistory.Count && _pendingInput == null)
        {
            _pendingInput = _inputBox.Text;
        }

        _historyIndex = newIndex;
        if (_historyIndex >= _commandHistory.Count)
        {
            _inputBox.Text = _pendingInput ?? "";
            _pendingInput = null;
        }
        else
        {
            _inputBox.Text = _commandHistory[_historyIndex];
        }
        _inputBox.CaretIndex = _inputBox.Text.Length;
    }

    /// <summary>本地命令路由：flist/文件列表/打开最近文件 → 最近文件列表；字体 XX → 设置字体大小。</summary>
    private bool TryRouteLocalCommand(string input, out string reply)
    {
        reply = "";

        // 最近文件：flist / 文件列表 / 打开最近文件 / 最近文件
        var lower = input.Trim().ToLower();
        if (lower == "flist" || input.Contains("文件列表") || input.Contains("打开最近文件") || input.Contains("最近文件"))
        {
            reply = GetRecentFilesText?.Invoke() ?? "最近没有打开过的文件记录。";
            AddMessage("assistant", reply);
            return true;
        }

        // 字体 XX / 字体设置到 XX（XX 必须是数字，否则不处理）
        var m = Regex.Match(input.Trim(), @"^字体(?:设置到|为|调整到)?\s*(\d+)\s*$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var size))
        {
            reply = SetFontSizeHandler?.Invoke(size) ?? $"已将字体大小设置为 {size}";
            AddMessage("assistant", reply);
            return true;
        }

        // 保存：有修改直接静默保存；新文件（未保存过）才弹保存窗口（本地直通）
        if (lower == "保存" || lower == "保存文件" || lower == "保存当前文件" || lower == "保存文档" || lower == "save")
        {
            reply = SaveCurrentDocument?.Invoke() ?? "当前没有打开的文件，无需保存。";
            AddMessage("assistant", reply);
            return true;
        }

        // 格式化：按当前文档类型分发对应语言格式化（本地直通，不依赖模型 function calling）。
        // 本地格式化器不支持该文件类型时，回退到"大模型按文件类型格式化"（异步，立即返回）。
        if (lower.Contains("格式化") || lower.Contains("整理格式"))
        {
            bool localSupported = true;
            try { localSupported = CanFormatCurrentDocumentLocally?.Invoke() ?? true; }
            catch { localSupported = true; }

            if (!localSupported)
            {
                _ = Task.Run(() => FormatDocumentWithModelAsync());
                return true;
            }

            reply = FormatCurrentDocument?.Invoke() ?? "当前没有打开的文档，无法格式化。";
            AddMessage("assistant", reply);
            return true;
        }

        // 通用本地命令路由：工具栏/菜单命令直达（跳转/缩放/对比/日志/编码等），命中即执行
        if (LocalCommandRouter != null)
        {
            var routed = LocalCommandRouter(input);
            if (routed != null)
            {
                AddMessage("assistant", routed);
                return true;
            }
        }

        return false;
    }

    // ========== 错别字检查（本地命令，独立调用模型，不写聊天历史） ==========

    /// <summary>识别错别字检查命令：检查错别字 / 错别字检查 / 检查错字 / 校对 / check typo。</summary>
    private static bool IsTypoCheckCommand(string input)
    {
        var lower = input.Trim().ToLowerInvariant();
        return lower.Contains("检查错别字") || lower.Contains("错别字检查") || lower.Contains("检查错字")
            || lower == "校对" || lower == "check typo" || lower == "typo check";
    }

    /// <summary>校对指令（要求模型严格只输出 JSON；行/列 1 起始，按字符计列）。</summary>
    private const string TypoCheckInstructions = """
        你是专业的中文校对专家。下面是一段（可能是任意语言的）文档文本，请找出其中的错别字。

        【要求】
        1. 只报告真正的错别字（用字错误、谐音错字、字形相近写错等），不要报告风格、标点、语法或表达习惯问题。
        2. 给出的 line 和 column 必须是 1 起始：line 是行号，column 是该行内的字符序号（一个汉字算 1 列）。
        3. original 必须是文档中真实存在的原文片段（错字）本身；suggestion 给出正确写法；reason 用一句话说明原因。
        4. 行号、列号必须与原文严格对应，不确定的位置宁可放弃也不要编造。
        5. 只输出一个 JSON 对象，不要输出任何其他文字、注释或 Markdown 代码块标记。格式：
        {"typos":[{"line":1,"column":5,"original":"己","suggestion":"已","reason":"应为'已经'的'已'"}]}
        如果没有错别字，输出：{"typos":[]}
        """;

    /// <summary>错别字检查主流程：调用大模型校对快照文本，把结果高亮到编辑器并汇总回复。</summary>
    private async Task CheckTyposAsync(string snapshot)
    {
        _busy = true;
        Dispatcher.UIThread.Post(() => SetBusyUi(true));
        try
        {
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ApplyTypoHighlights?.Invoke(Array.Empty<TypoItem>());
                    AddMessage("assistant", "📄 当前文档为空，没有可检查的内容。");
                });
                return;
            }

            // 跨平台：统一换行符（\r\n → \n、旧版 Mac \r → \n），
            // 使模型对行号/列号的计数与 AvaloniaEdit 文档解析（兼容三种换行）严格一致，
            // 避免 Windows(CRLF) / macOS/Linux(LF) / 旧 Mac(CR) 文件导致行号偏移
            var normalized = snapshot.Replace("\r\n", "\n").Replace('\r', '\n');

            // 超长文档截断，避免超出小模型上下文窗口
            const int MaxChars = 6000;
            bool truncated = normalized.Length > MaxChars;
            var text = truncated ? normalized.Substring(0, MaxChars) : normalized;

            EnsureAgent();
            using var cts = new CancellationTokenSource(ModelTimeout);
            ChatResponse response;
            try
            {
                response = await _agent!.ChatClient.GetResponseAsync(
                    new List<ChatMessage>
                    {
                        new(ChatRole.System, TypoCheckInstructions),
                        new(ChatRole.User, text)
                    },
                    new ChatOptions { MaxOutputTokens = 2048 },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => AddMessage("system", "⏰ 模型响应超时，请重试。"));
                return;
            }

            var typos = new List<TypoItem>();
            foreach (var it in ParseTypoJson(response?.Text ?? ""))
            {
                if (it.Line < 1 || it.Column < 1 ||
                    string.IsNullOrWhiteSpace(it.Original) || string.IsNullOrWhiteSpace(it.Suggestion)) continue;
                typos.Add(new TypoItem
                {
                    Line = it.Line,
                    Column = it.Column,
                    Original = it.Original.Trim(),
                    Suggestion = it.Suggestion.Trim(),
                    Reason = string.IsNullOrWhiteSpace(it.Reason) ? null : it.Reason.Trim()
                });
            }

            Dispatcher.UIThread.Post(() =>
            {
                ApplyTypoHighlights?.Invoke(typos);
                if (typos.Count == 0)
                {
                    AddMessage("assistant", truncated
                        ? "🔍 已检查文档前 6000 字符，未发现明显错别字（文档较长，仅检查了截断部分）。"
                        : "🔍 未发现明显错别字 ✅");
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"🔍 错别字检查完成：共发现 {typos.Count} 处，已在编辑器中高亮标记：");
                    for (int i = 0; i < typos.Count; i++)
                    {
                        var t = typos[i];
                        var reason = string.IsNullOrEmpty(t.Reason) ? "" : $"（{t.Reason}）";
                        sb.AppendLine($"{i + 1}. 第{t.Line}行第{t.Column}列：「{t.Original}」→ 应为「{t.Suggestion}」{reason}");
                    }
                    if (truncated) sb.AppendLine("（提示：文档较长，仅检查了前 6000 字符）");
                    AddMessage("assistant", sb.ToString().TrimEnd());
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ApplyTypoHighlights?.Invoke(Array.Empty<TypoItem>());
                AddMessage("system", $"❌ 错别字检查失败：{SanitizeError(ex.Message)}");
            });
        }
        finally
        {
            _busy = false;
            Dispatcher.UIThread.Post(() => SetBusyUi(false));
        }
    }

    private sealed class TypoJsonEntry
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public string? Original { get; set; }
        public string? Suggestion { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class TypoJsonResponse
    {
        public List<TypoJsonEntry>? Typos { get; set; }
    }

    /// <summary>从模型回复中提取 JSON 对象并解析（容忍 ```json 围栏与前后多余文字），失败返回空列表。</summary>
    private static List<TypoJsonEntry> ParseTypoJson(string? raw)
    {
        var result = new List<TypoJsonEntry>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        int s = raw.IndexOf('{');
        int e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return result;
        try
        {
            var doc = JsonSerializer.Deserialize<TypoJsonResponse>(
                raw.Substring(s, e - s + 1),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (doc?.Typos != null) result.AddRange(doc.Typos);
        }
        catch
        {
            // 解析失败返回空结果，上层按"未发现问题"处理
        }
        return result;
    }

    private async Task ProcessAsync(string input)
    {
        _busy = true;
        // _history 仅在后台线程（本方法）读写，避免与 UI 线程并发
        _history.Add(new ChatMessage(ChatRole.User, input));

        // 流式消息：正文逐字显示，思考过程实时刷新
        var stream = BeginStreamingMessage("assistant", stretch: true);
        _activeStream = stream;
        Dispatcher.UIThread.Post(() => SetBusyUi(true));

        using var timeoutCts = new CancellationTokenSource(ModelTimeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        _streamCts = cts;
        bool streamInterrupted = false;
        try
        {
            EnsureAgent();

            // 手动 function calling 工具循环（流式版）：
            // - 助手文本逐字增量推送到气泡（打字机效果）；
            // - 推理链（reasoning）单独推送到思考区；
            // - 工具调用过程实时显示，避免自动循环把中间过程全部吞掉。
            var chat = _agent!.ChatClient.AsBuilder().Build();

            var messages = new List<ChatMessage>(_history);
            string text = "";
            const int MaxToolIterations = 8;
            bool toolInvoked = false;
            // 一旦发现网关不接受 reasoning 参数就永久降级，避免每轮都白试一次
            bool reasoningAllowed = _showThinking;

            for (int iteration = 0; iteration < MaxToolIterations; iteration++)
            {
                // 每一轮的工具调用增量缓存（流式下 FunctionCallContent 可能是分片）
                var callAccumulator = new ToolCallAccumulator();
                var bodyBuilder = new StringBuilder();

                try
                {
                    await foreach (var update in StreamTurnAsync(
                        chat, messages, _commandTools, reasoningAllowed, cts.Token, stream, bodyBuilder, callAccumulator))
                    {
                    }
                }
                catch (ReasoningNotSupportedException)
                {
                    // 网关不认识 reasoning / think：去掉该参数重试本轮，思考区自动不显示
                    reasoningAllowed = false;
                    iteration--;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    // 区分"用户点停止"与"整体超时"，两者提示不同
                    streamInterrupted = true;
                }

                if (streamInterrupted) break;

                var toolCalls = callAccumulator.Build();
                if (toolCalls.Count == 0)
                {
                    text = bodyBuilder.ToString();
                    break;
                }

                // 有工具调用：本轮模型可能同时输出了"我要调用工具"的说明文字。
                // 该文字不属于最终答案，从气泡里撤掉，只保留工具执行过程提示。
                if (bodyBuilder.Length > 0)
                {
                    stream.SetBody("");
                    Dispatcher.UIThread.Post(() => AddMessage("system", bodyBuilder.ToString().Trim()));
                }

                var assistantMsg = new ChatMessage(ChatRole.Assistant, "");
                foreach (var call in toolCalls) assistantMsg.Contents.Add(call);
                messages.Add(assistantMsg);

                foreach (var call in toolCalls)
                {
                    var toolName = call.Name;
                    toolInvoked = true;
                    Dispatcher.UIThread.Post(() => AddMessage("system", $"⚙️ 正在执行：{toolName} …"));

                    var tool = _commandTools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
                    string result;
                    try
                    {
                        if (tool is AIFunction fn)
                        {
                            var fnArgs = new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object>());
                            var raw = await fn.InvokeAsync(fnArgs, cts.Token).ConfigureAwait(false);
                            result = raw?.ToString() ?? "";
                        }
                        else
                        {
                            result = $"工具 {toolName} 不可执行（未注册）。";
                        }
                    }
                    catch (Exception ex)
                    {
                        result = $"工具执行失败：{ex.Message}";
                    }

                    Dispatcher.UIThread.Post(() => AddMessage("system", $"✅ 已执行：{toolName}"));
                    var toolMsg = new ChatMessage(ChatRole.Tool, "");
                    toolMsg.Contents.Add(new FunctionResultContent(call.CallId, result));
                    messages.Add(toolMsg);
                }
            }

            if (streamInterrupted)
            {
                bool timedOut = timeoutCts.IsCancellationRequested;
                // 保留已生成的部分内容：超时/停止都不应丢掉用户已经看到的东西
                var partial = stream.Body;
                var notice = timedOut
                    ? "⏰ 模型响应超时，已保留已生成的部分内容。"
                    : "⏹️ 已停止生成。";

                if (!string.IsNullOrWhiteSpace(partial))
                {
                    _history.Add(new ChatMessage(ChatRole.Assistant, partial));
                    TrimHistory();
                    stream.Commit();
                    Dispatcher.UIThread.Post(() =>
                    {
                        CommitStreamEntry(stream);
                        AddMessage("system", notice);
                    });
                }
                else
                {
                    // 没有任何正文产出：直接替换占位提示
                    stream.SetBody(notice);
                    stream.Commit();
                    Dispatcher.UIThread.Post(() => CommitStreamEntry(stream));
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = toolInvoked
                    ? "（工具已执行，模型未返回文字内容）"
                    : "（模型未返回内容）";
            }

            // 定稿：把最终文本写回气泡（含工具调用后的最终答复）
            stream.SetBody(text);
            stream.Commit();
            Dispatcher.UIThread.Post(() => CommitStreamEntry(stream));

            _history.Add(new ChatMessage(ChatRole.Assistant, text));
            // 截断历史：超出上限时丢弃最旧的消息对，防止 token 无限增长导致模型超时/OOM/卡死
            TrimHistory();
        }
        catch (Exception ex)
        {
            stream.SetBody($"❌ {SanitizeError(ex.Message)}");
            stream.Commit();
            Dispatcher.UIThread.Post(() => CommitStreamEntry(stream));
        }
        finally
        {
            _busy = false;
            _activeStream = null;
            _streamCts = null;
            cts.Dispose();
            Dispatcher.UIThread.Post(() => SetBusyUi(false));
        }
    }

    /// <summary>
    /// 流式执行一轮对话：把正文增量、思考过程增量、工具调用分片分别落到对应去处。
    /// 若网关因不认识 reasoning / think 参数而拒绝请求，抛出
    /// <see cref="ReasoningNotSupportedException"/> 供调用方降级重试。
    /// </summary>
    private static async IAsyncEnumerable<int> StreamTurnAsync(
        IChatClient chat,
        List<ChatMessage> messages,
        List<AITool> tools,
        bool withReasoning,
        CancellationToken token,
        StreamingMessage stream,
        StringBuilder bodyBuilder,
        ToolCallAccumulator callAccumulator)
    {
        var options = BuildChatOptions(tools, withReasoning);

        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            enumerator = chat.GetStreamingResponseAsync(messages, options, token).GetAsyncEnumerator(token);
        }
        catch (Exception ex) when (withReasoning && IsReasoningRejection(ex))
        {
            throw new ReasoningNotSupportedException(ex);
        }

        await using (enumerator)
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    update = enumerator.Current;
                }
                catch (Exception ex) when (withReasoning && IsReasoningRejection(ex))
                {
                    throw new ReasoningNotSupportedException(ex);
                }

                // 1) 正文增量
                var bodyDelta = update.Text;
                if (!string.IsNullOrEmpty(bodyDelta))
                {
                    bodyBuilder.Append(bodyDelta);
                    stream.AppendBody(bodyDelta);
                }

                // 2) 思考过程增量（不同提供方分别落在 TextReasoningContent / 原始 JSON 字段）
                foreach (var piece in ExtractReasoning(update))
                {
                    stream.AppendThinking(piece);
                }

                // 3) 工具调用增量（可能跨多个分片）
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fcc) callAccumulator.Add(fcc);
                }

                yield return 0;
            }
        }
    }

    /// <summary>请求被网关以"不支持的参数"拒绝（典型是 400 + reasoning/think 相关提示）。</summary>
    private static bool IsReasoningRejection(Exception ex)
    {
        // 保留取消语义：取消不是"参数不被支持"
        if (ex is OperationCanceledException) return false;

        var message = ex.ToString();
        if (message.Length == 0) return false;

        bool mentionsReasoning =
            message.Contains("reasoning", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("think", StringComparison.OrdinalIgnoreCase);
        bool looksLikeBadRequest =
            message.Contains("400") ||
            message.Contains("BadRequest", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid_request", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unexpected", StringComparison.OrdinalIgnoreCase);

        return mentionsReasoning && looksLikeBadRequest;
    }

    /// <summary>网关不接受推理参数：用于触发"去掉 reasoning 参数重试"。</summary>
    private sealed class ReasoningNotSupportedException : Exception
    {
        public ReasoningNotSupportedException(Exception inner) : base(inner.Message, inner) { }
    }

    /// <summary>
    /// 构建聊天选项：携带工具，并在开启思考时请求推理链输出。
    /// 注意：部分网关不认识 reasoning / think 参数并会直接报 400，
    /// 因此调用方在失败时会用 <paramref name="withReasoning"/>=false 重试一次。
    /// </summary>
    private static ChatOptions BuildChatOptions(IReadOnlyList<AITool> tools, bool withReasoning)
    {
        var options = new ChatOptions { Tools = tools.ToList() };
        if (withReasoning)
        {
            // 尽力请求推理链输出。实测（M.E.AI.OpenAI 9.6.0-preview）OpenAI 兼容客户端
            // 不会把 Reasoning / AdditionalProperties 序列化进请求体，因此这两项对
            // OpenAI 兼容网关通常"发不出去"；Ollama 原生客户端则会用到 think。
            // 这不妨碍展示思考过程：只要模型/网关本来就返回 reasoning_content
            //（如 deepseek-r1、qwen3 及默认开启思考的网关），下面的提取逻辑就能拿到。
            // 真正的兜底是"没有思考内容就不显示思考区"，不会误导用户。
            options.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.Medium,
                Output = ReasoningOutput.Full
            };
            options.AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = true };
        }
        return options;
    }

    /// <summary>
    /// 从流式增量中提取思考过程文本。不同提供方的承载方式不同，这里做三级兼容：
    /// 1) 标准 <see cref="TextReasoningContent"/>（M.E.AI 归一化后的推理内容）；
    /// 2) 原始响应体里的 <c>reasoning_content</c> / <c>reasoning</c> 等字段；
    /// 3) OpenAI SDK 的强类型分片 — 它不把 reasoning_content 暴露成属性，
    ///    必须靠 <see cref="TryPersistToJson"/> 回写原始 JSON 才能拿到（实测有效）。
    /// </summary>
    private static IEnumerable<string> ExtractReasoning(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is TextReasoningContent reasoning &&
                !string.IsNullOrEmpty(reasoning.Text))
            {
                yield return reasoning.Text;
            }
        }

        // 兼容：原始返回体里带 reasoning_content / thinking 字段的网关
        var extra = ExtractReasoningFromRaw(update.RawRepresentation);
        if (!string.IsNullOrEmpty(extra)) yield return extra;
    }

    /// <summary>从原始响应对象里尽力读取 reasoning_content / thinking / reasoning 文本字段。</summary>
    private static string? ExtractReasoningFromRaw(object? raw)
    {
        if (raw == null) return null;
        try
        {
            switch (raw)
            {
                case System.Text.Json.JsonElement je:
                    return ReadReasoningJson(je);
                case System.Text.Json.JsonDocument jd:
                    return ReadReasoningJson(jd.RootElement);
                case string s when s.Length > 0 && s[0] == '{':
                    using (var doc = System.Text.Json.JsonDocument.Parse(s))
                        return ReadReasoningJson(doc.RootElement);
            }

            // OpenAI SDK 的强类型分片（StreamingChatCompletionUpdate）不会把 reasoning_content
            // 暴露成属性，但底层保留着原始 JSON：用 IPersistableModel 回写即可拿到。
            var persisted = TryPersistToJson(raw);
            if (persisted != null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(persisted);
                return ReadReasoningJson(doc.RootElement);
            }
        }
        catch
        {
            // 原始体格式未知：忽略即可，不影响正文流式
        }
        return null;
    }

    /// <summary>
    /// 把实现了 System.ClientModel.Primitives.IPersistableModel&lt;T&gt; 的原始响应对象
    /// 重新序列化为 JSON 字符串（拿回强类型属性之外、网关特有的字段，如 reasoning_content）。
    /// 未实现该接口时返回 null。
    /// </summary>
    private static string? TryPersistToJson(object raw)
    {
        var type = raw.GetType();
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            if (iface.GetGenericTypeDefinition() != typeof(System.ClientModel.Primitives.IPersistableModel<>)) continue;
            // 仅处理 IPersistableModel<自身类型>，避免匹配到别的泛型实参
            if (iface.GetGenericArguments()[0] != type) continue;

            var write = iface.GetMethod("Write", new[] { typeof(System.ClientModel.Primitives.ModelReaderWriterOptions) });
            if (write == null) continue;

            var options = new System.ClientModel.Primitives.ModelReaderWriterOptions("J");
            var result = write.Invoke(raw, new object[] { options });
            return result switch
            {
                BinaryData bd => bd.ToString(),
                string str => str,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>在 JSON 对象（或其 choices[0].delta）中查找推理文本字段。</summary>
    private static string? ReadReasoningJson(System.Text.Json.JsonElement root)
    {
        foreach (var name in ReasoningFieldNames)
        {
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty(name, out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        // choices[0].delta / message 里再找一层
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
            root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == System.Text.Json.JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            foreach (var holder in new[] { "delta", "message" })
            {
                if (first.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    first.TryGetProperty(holder, out var h))
                {
                    var nested = ReadReasoningJson(h);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
        }
        return null;
    }

    /// <summary>各网关常用的推理文本字段名。</summary>
    private static readonly string[] ReasoningFieldNames =
        { "reasoning_content", "reasoning", "thinking", "thought" };

    /// <summary>
    /// 工具调用增量累加器：流式协议下同一个调用的 name/arguments 可能分散在多个分片，
    /// 按 index（缺失则按 CallId）合并，最终还原成完整调用。
    /// </summary>
    private sealed class ToolCallAccumulator
    {
        /// <summary>可变缓冲：FunctionCallContent 的 Name/CallId 是只读的，无法直接改已有实例，
        /// 因此先按分片累积到本结构，最后再构造完整的 FunctionCallContent。</summary>
        private sealed class Buffer
        {
            public string? Name;
            public string? CallId;
            public Dictionary<string, object>? Arguments;
        }

        private readonly List<Buffer> _ordered = new();
        private readonly Dictionary<string, int> _byKey = new();
        private int _fallbackIndex;

        public void Add(FunctionCallContent fcc)
        {
            // 分片索引：优先用提供方给的 index，其次 CallId，最后按到达顺序
            var key = fcc.AdditionalProperties != null &&
                      fcc.AdditionalProperties.TryGetValue("index", out var idx) && idx != null
                ? $"i:{idx}"
                : (!string.IsNullOrEmpty(fcc.CallId) ? $"c:{fcc.CallId}" : $"n:{_fallbackIndex++}");

            if (!_byKey.TryGetValue(key, out var pos))
            {
                _byKey[key] = _ordered.Count;
                _ordered.Add(new Buffer());
                pos = _ordered.Count - 1;
            }

            var buf = _ordered[pos];
            // 名称/CallId 通常只在首个非空分片里出现，取第一个非空值
            if (string.IsNullOrEmpty(buf.Name) && !string.IsNullOrEmpty(fcc.Name))
                buf.Name = fcc.Name;
            if (string.IsNullOrEmpty(buf.CallId) && !string.IsNullOrEmpty(fcc.CallId))
                buf.CallId = fcc.CallId;

            // 参数分片：合并字典（同名键用后到的值覆盖）
            if (fcc.Arguments is { Count: > 0 })
            {
                buf.Arguments ??= new Dictionary<string, object>();
                foreach (var kv in fcc.Arguments) buf.Arguments[kv.Key] = kv.Value;
            }
        }

        /// <summary>产出已完整（有名称）的调用列表。</summary>
        public List<FunctionCallContent> Build()
        {
            var result = new List<FunctionCallContent>();
            foreach (var buf in _ordered)
            {
                if (string.IsNullOrEmpty(buf.Name)) continue;
                result.Add(new FunctionCallContent(
                    buf.CallId ?? $"call_{result.Count}",
                    buf.Name,
                    buf.Arguments ?? new Dictionary<string, object>()));
            }
            return result;
        }
    }

    /// <summary>截断聊天历史：超出上限时丢弃最旧消息对，防止 token 无限增长导致模型超时/OOM/卡死。</summary>
    private void TrimHistory()
    {
        while (_history.Count > MaxHistoryMessages)
        {
            // 一次丢弃最旧的一对（user/assistant），保持对话结构
            _history.RemoveAt(0);
            if (_history.Count > 0) _history.RemoveAt(0);
        }
    }

    // ========== 消息显示 ==========

    private void AddMessage(string role, string text, bool stretch = false)
    {
        // 线程安全：UI 集合（_messagesPanel.Children）只能在 UI 线程修改。
        // EnsureAgent 等在后台线程可能直接调用本方法，跨线程修改会损坏
        // AvaloniaList 内部状态（崩溃：InsertRange 索引越界），这里统一调度到 UI 线程。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddMessage(role, text, stretch));
            return;
        }

        AddEntry(new ChatEntry { Role = role, Text = text, Stretch = stretch });
    }

    /// <summary>把一条消息（静态或流式）加入消息区并滚到底。</summary>
    private void AddEntry(ChatEntry entry)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddEntry(entry));
            return;
        }
        _entries.Add(entry);
        _messagesPanel.Children.Add(BuildMessageVisual(entry));
        ScrollToEnd();
    }

    /// <summary>滚动到底部。延迟一拍执行，确保刚加入的可视树已完成测量（否则滚不到真正底部）。</summary>
    private void ScrollToEnd()
    {
        _messagesScroll.ScrollToEnd();
        Dispatcher.UIThread.Post(() => _messagesScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 智能滚动：仅当用户本来就在底部附近时才自动跟随，
    /// 用户向上翻阅历史时不打断（流式输出时这一点尤其重要）。
    /// </summary>
    private void ScrollToEndIfFollowing()
    {
        if (!_autoFollow) return;
        ScrollToEnd();
    }

    /// <summary>
    /// 按当前主题构建一条消息的气泡控件。
    /// 大模型回答走轻量 Markdown 渲染（标题/列表/代码块/引用/行内样式），
    /// 用户输入与系统提示按纯文本渲染（避免把命令行、路径里的符号当 Markdown 解析）。
    /// 流式消息额外挂上"思考过程"折叠区与正文容器，供增量更新。
    /// </summary>
    private Control BuildMessageVisual(ChatEntry entry)
    {
        var t = ChatTheme.Current;
        bool isUser = entry.Role == "user";
        bool isSystem = entry.Role == "system";
        bool isAssistant = !isUser && !isSystem;

        var bg = isSystem ? t.SystemBg : isUser ? t.UserBg : t.AssistantBg;
        var border = isSystem ? t.SystemBorder : isUser ? t.UserBorder : t.AssistantBorder;
        var fg = isSystem ? t.SystemFg : isUser ? t.UserFg : t.AssistantFg;

        var bubble = new Border
        {
            // 不设固定最大宽度：消息随 AI 面板宽度自适应重排（文本块已启用换行）
            Padding = isAssistant ? new Thickness(12, 9) : new Thickness(11, 7),
            CornerRadius = new CornerRadius(9),
            Background = Brush(bg),
            BorderBrush = Brush(border),
            BorderThickness = new Thickness(1),
            // AI 回答与欢迎提示一样撑满面板宽度，随面板宽度自适应重排；用户消息右对齐保持紧凑
            HorizontalAlignment = entry.Stretch || isAssistant ? HorizontalAlignment.Stretch
                : isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        var content = new StackPanel { Spacing = 5 };

        // 思考过程区（仅在助手流式消息上出现；有真实推理内容时才显示）
        if (entry.Stream != null && isAssistant)
        {
            var thinkBox = BuildThinkingBox(entry, t);
            if (thinkBox != null) content.Children.Add(thinkBox);
        }

        var bodyHost = new StackPanel { Spacing = 5 };
        if (entry.Stream != null && isAssistant)
        {
            // 流式：正文本体交由 RenderStreamBody 增量重排
            entry.BodyHost = bodyHost;
            content.Children.Add(bodyHost);
            RenderStreamBody(entry, t);
        }
        else if (isAssistant)
        {
            RenderMarkdownTo(content, entry.Text, t);
        }
        else
        {
            AppendPlainLines(content, entry.Text, fg, t);
        }

        // 气泡底部一行操作按钮：用户消息提供"编辑"，AI 回答提供"复制"
        var bubbleContent = new StackPanel { Spacing = 6 };
        bubbleContent.Children.Add(content);
        if (isUser) bubbleContent.Children.Add(BuildActionRow("✎ 编辑", t, () =>
        {
            _inputBox.Text = entry.Text;
            _inputBox.CaretIndex = entry.Text.Length;
            _inputBox.Focus();
        }));
        else if (isAssistant) bubbleContent.Children.Add(BuildActionRow("📋 复制", t, null, entry.Text));
        bubble.Child = bubbleContent;
        return bubble;
    }

    // ========== 思考过程区 ==========

    /// <summary>
    /// 构建思考过程折叠区（🧠 可展开）。
    /// 流式期间自动展开并跟随最新内容；定稿后若思考内容过短（模型没真正推理）则整块隐藏。
    /// </summary>
    private static Border? BuildThinkingBox(ChatEntry entry, ChatTheme t)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = MessageFontSize - 1.5,
            FontFamily = new FontFamily(MessageFontFamily),
            LineHeight = MessageLineHeight - 2,
            Foreground = Brush(t.ThinkFg)
        };

        var scroll = new ScrollViewer
        {
            Content = text,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var header = new TextBlock
        {
            Text = LangService.T("🧠 思考中…"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(t.ThinkLabel),
            VerticalAlignment = VerticalAlignment.Center
        };

        // 用 Expander 提供折叠：展开时露出推理链，收起后只留一行标题
        var expander = new Expander
        {
            Header = header,
            Content = scroll,
            IsExpanded = true,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var box = new Border
        {
            Background = Brush(t.ThinkBg),
            BorderBrush = Brush(t.ThinkBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6),
            // 尚未收到任何思考内容时先隐藏，避免闪出一个空框
            IsVisible = false,
            Child = expander
        };

        entry.ThinkingBox = box;
        entry.ThinkingHost = expander;
        entry.ThinkingText = text;
        entry.ThinkingHeader = header;
        entry.ThinkingScroll = scroll;
        return box;
    }

    // ========== 流式增量渲染 ==========

    /// <summary>
    /// 把流式消息的当前文本渲染到目标控件。
    /// 由 <see cref="StreamingMessage.Sink"/> 在 UI 线程回调，节流后约 16fps 触发。
    /// 正文整体重排 Markdown（保证半截代码块/列表也能正确显示），思考区只追加不重排（成本更低）。
    /// </summary>
    private void OnStreamPaint(StreamingMessage msg)
    {
        var entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Stream, msg));
        if (entry == null) return;

        // 主题切换后控件被重建：目标已失效则跳过本轮（重绘时已按当前文本重建）
        if (entry.BodyHost == null && entry.ThinkingBox == null) return;

        var t = ChatTheme.Current;
        entry.Text = msg.Body;

        RenderStreamBody(entry, t);
        RenderStreamThinking(entry, msg, t);
        ScrollToEndIfFollowing();
    }

    /// <summary>渲染流式正文：先在独立面板里解析 Markdown，再整体替换，避免半截可视树。</summary>
    private void RenderStreamBody(ChatEntry entry, ChatTheme t)
    {
        var host = entry.BodyHost;
        if (host == null) return;

        var text = entry.Text;
        // 文本没变化就不重建：思考增量触发的重绘不应白白重排一遍正文（长回答下开销可观）
        if (entry.RenderedBody == text) return;
        entry.RenderedBody = text;

        if (string.IsNullOrEmpty(text))
        {
            // 尚未产出正文：显示一个轻量光标占位，告诉用户"正在生成"
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "▍",
                FontSize = MessageFontSize,
                FontFamily = new FontFamily(MessageFontFamily),
                LineHeight = MessageLineHeight,
                Foreground = Brush(t.Muted)
            });
            return;
        }

        // 复用 Markdown 渲染：渲染到一个临时面板后整体换入
        var rebuilt = new StackPanel { Spacing = 5 };
        RenderMarkdownTo(rebuilt, text, t);
        host.Children.Clear();
        foreach (var child in rebuilt.Children.ToList())
        {
            rebuilt.Children.Remove(child);
            host.Children.Add(child);
        }
    }

    /// <summary>渲染思考区：仅追加新到达的文本，并让滚动条跟随最新一行。</summary>
    private static void RenderStreamThinking(ChatEntry entry, StreamingMessage msg, ChatTheme t)
    {
        var box = entry.ThinkingBox;
        var textBlock = entry.ThinkingText;
        if (box == null || textBlock == null) return;

        var thinking = msg.Thinking;
        if (!msg.HasThinking || thinking.Length == 0)
        {
            box.IsVisible = false;
            return;
        }

        box.IsVisible = true;
        textBlock.Text = thinking;
        if (entry.ThinkingHeader != null)
        {
            entry.ThinkingHeader.Text = msg.IsCommitted
                ? string.Format(LangService.T("🧠 已深度思考（{0} 字）"), thinking.Length)
                : string.Format(LangService.T("🧠 思考中…（{0} 字）"), thinking.Length);
        }
        // 思考区跟随最新推理内容滚动
        entry.ThinkingScroll?.ScrollToEnd();
    }

    /// <summary>流式结束后定稿：隐藏无意义的思考区、刷新标题、补上复制按钮的最终文本。</summary>
    private void CommitStreamEntry(StreamingMessage msg)
    {
        var entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Stream, msg));
        if (entry == null) return;

        if (entry.ThinkingBox != null)
        {
            // 思考内容过短说明模型并未真正推理（或只是回声），不展示以免误导
            bool meaningful = msg.HasThinking && msg.Thinking.Trim().Length >= MinThinkingChars;
            entry.ThinkingBox.IsVisible = meaningful;
            if (meaningful && entry.ThinkingHost != null)
            {
                // 正文开始后就自动收起，避免长推理链把答案挤出视野；用户仍可点开查看
                entry.ThinkingHost.IsExpanded = false;
            }
            if (meaningful && entry.ThinkingHeader != null)
            {
                entry.ThinkingHeader.Text = string.Format(
                    LangService.T("🧠 已深度思考（{0} 字）"), msg.Thinking.Length);
            }
        }

        entry.Text = msg.Body;
        RenderStreamBody(entry, ChatTheme.Current);
    }

    /// <summary>思考内容少于此字符数时视为无有效推理，不展示思考区。</summary>
    private const int MinThinkingChars = 8;

    /// <summary>消息右下角的次要操作行（编辑 / 复制）；copyText 非空时点击复制到剪贴板。</summary>
    private StackPanel BuildActionRow(string text, ChatTheme t, Action? onClick, string? copyText = null)
    {
        var icon = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Brush(t.Muted),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (copyText != null)
        {
            icon.PointerPressed += async (_, _) =>
            {
                try
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top?.Clipboard == null) return;
                    var data = new Avalonia.Input.DataTransfer();
                    data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, copyText));
                    await top.Clipboard.SetDataAsync(data);
                    icon.Text = "✓ 已复制";
                }
                catch { /* 剪贴板不可用时忽略 */ }
            };
        }
        else if (onClick != null)
        {
            icon.PointerPressed += (_, _) => onClick();
        }
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { icon }
        };
    }

    /// <summary>纯文本按行渲染（用户消息与系统提示）：以 "📄 " 开头的行渲染为可点击文件项。</summary>
    private void AppendPlainLines(StackPanel parent, string text, Color fg, ChatTheme t)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("📄 ", StringComparison.Ordinal))
            {
                parent.Children.Add(BuildFileLink(line, t));
                continue;
            }
            var block = NewTextBlock(fg);
            block.Text = line;
            parent.Children.Add(block);
        }
    }

    /// <summary>可点击的文件项（本地命令"最近文件"输出，点击在新标签页打开）。</summary>
    private Control BuildFileLink(string line, ChatTheme t)
    {
        var path = line.Substring(2).Trim();
        var link = NewTextBlock(t.Accent);
        link.Text = line;
        link.Cursor = new Cursor(StandardCursorType.Hand);
        link.TextDecorations = TextDecorations.Underline;
        link.PointerPressed += (_, _) => FileClickRequested?.Invoke(path);
        return link;
    }

    // ========== 大模型输出的轻量 Markdown 渲染 ==========

    /// <summary>行内标记：**粗体** / *斜体* / `行内代码` / ~~删除线~~ / [文字](链接)。
    /// 不解析下划线形式（_斜体_ / __粗体__），否则 snake_case 标识符会被误判。</summary>
    private static readonly Regex InlineMarkupRegex = new(
        @"\*\*(?<bold>.+?)\*\*|(?<!\*)\*(?<italic>[^*\n]+?)\*(?!\*)|`(?<code>[^`\n]+?)`|~~(?<strike>[^~\n]+?)~~|\[(?<linkText>[^\]\n]+?)\]\((?<linkUrl>[^)\s]+)\)",
        RegexOptions.Compiled);

    /// <summary>行内片段：文本 + 样式（粗体/斜体/删除线/行内代码/链接地址）。</summary>
    private readonly record struct InlineSpan(string Text, bool Bold, bool Italic, bool Strike, bool Code, string? Url);

    /// <summary>
    /// 把大模型输出按轻量 Markdown 渲染成消息块：
    /// 代码块、标题、列表、引用、分隔线各自成块，行内粗体/斜体/行内代码/链接转成富文本。
    /// 目的是不让 ``` 与 ** 这些标记原样显示给用户，同时拉开段落间距、放大字号。
    /// </summary>
    private void RenderMarkdownTo(StackPanel parent, string text, ChatTheme t)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool pendingBlank = false;   // 上一个被跳过的空行 → 给下一段加上段间距
        int i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) { pendingBlank = true; i++; continue; }

            // ``` 代码块（未闭合时读到结尾）：围栏与语言标签都不显示给用户
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var lang = trimmed.TrimStart('`').Trim();
                i++;
                var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i].TrimEnd('\r'));
                    i++;
                }
                if (i < lines.Length) i++;   // 跳过结束围栏
                parent.Children.Add(BuildCodeBlock(code.ToString(), lang, t));
                pendingBlank = false;
                continue;
            }

            // 分隔线
            if (IsHorizontalRule(trimmed))
            {
                parent.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 5),
                    Background = Brush(t.Divider)
                });
                pendingBlank = false;
                i++;
                continue;
            }

            // # 标题（# 号不显示）
            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '#') level++;
            if (level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' ')
            {
                parent.Children.Add(BuildHeading(trimmed.Substring(level + 1).Trim(), level, t));
                pendingBlank = false;
                i++;
                continue;
            }

            // > 引用（连续行合并成一段）
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    var q = lines[i].TrimStart().Substring(1);
                    if (q.StartsWith(" ")) q = q.Substring(1);
                    quote.Add(q.TrimEnd());
                    i++;
                }
                parent.Children.Add(BuildQuote(quote, t));
                pendingBlank = false;
                continue;
            }

            // - / * / + 无序列表
            if (TryMatchBullet(raw, out _))
            {
                var items = new List<string>();
                while (i < lines.Length && TryMatchBullet(lines[i], out var item))
                {
                    items.Add(item);
                    i++;
                }
                parent.Children.Add(BuildList(items, false, t));
                pendingBlank = false;
                continue;
            }

            // 1. / 1) 有序列表
            if (TryMatchOrdered(raw, out _))
            {
                var items = new List<string>();
                while (i < lines.Length && TryMatchOrdered(lines[i], out var item))
                {
                    items.Add(item);
                    i++;
                }
                parent.Children.Add(BuildList(items, true, t));
                pendingBlank = false;
                continue;
            }

            // 📄 文件项（"最近文件"列表输出）
            if (trimmed.StartsWith("📄 ", StringComparison.Ordinal))
            {
                parent.Children.Add(BuildFileLink(trimmed, t));
                pendingBlank = false;
                i++;
                continue;
            }

            // 普通文本行：逐行成块，保留行首缩进（命令清单等靠缩进对齐），空行转成段间距
            var block = NewTextBlock(t.AssistantFg);
            AppendInlines(block, raw.TrimEnd('\r').TrimEnd(), t);
            if (pendingBlank) block.Margin = new Thickness(0, 6, 0, 0);
            parent.Children.Add(block);
            pendingBlank = false;
            i++;
        }
    }

    private static bool IsHorizontalRule(string trimmed)
        => trimmed.Length >= 3
            && (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_'));

    private static bool TryMatchBullet(string line, out string item)
    {
        var m = Regex.Match(line, @"^\s{0,3}[-*+]\s+(.*)$");
        item = m.Success ? m.Groups[1].Value.Trim() : "";
        return m.Success;
    }

    private static bool TryMatchOrdered(string line, out string item)
    {
        var m = Regex.Match(line, @"^\s{0,3}\d+[.)]\s+(.*)$");
        item = m.Success ? m.Groups[1].Value.Trim() : "";
        return m.Success;
    }

    /// <summary>标题：字号随级别递减，整体加粗（# 号已剥离）。</summary>
    private static Control BuildHeading(string text, int level, ChatTheme t)
    {
        var block = NewTextBlock(t.AssistantFg);
        block.FontSize = level switch { 1 => 18.5, 2 => 17.5, 3 => 16.5, _ => 15.5 };
        block.LineHeight = block.FontSize + 8;
        block.FontWeight = FontWeight.Bold;
        block.Margin = new Thickness(0, 3, 0, 1);
        AppendInlines(block, text, t);
        return block;
    }

    /// <summary>引用块：左侧竖线 + 次要色文字。</summary>
    private static Control BuildQuote(IReadOnlyList<string> lines, ChatTheme t)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var line in lines)
        {
            var block = NewTextBlock(t.QuoteFg);
            block.FontStyle = FontStyle.Italic;
            AppendInlines(block, line, t);
            panel.Children.Add(block);
        }
        return new Border
        {
            BorderBrush = Brush(t.QuoteBar),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(2, 2, 0, 2),
            Child = panel
        };
    }

    /// <summary>列表：项目符号列 + 正文列（正文列换行时保持悬挂缩进）。</summary>
    private static Control BuildList(IReadOnlyList<string> items, bool ordered, ChatTheme t)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(2, 1, 0, 1) };
        for (int n = 0; n < items.Count; n++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var marker = new TextBlock
            {
                // 有序列表按实际序号显示，序号与随后的正文左对齐
                Text = ordered ? $"{n + 1}." : "•",
                FontSize = MessageFontSize,
                FontFamily = new FontFamily(MessageFontFamily),
                LineHeight = MessageLineHeight,
                Foreground = Brush(t.Muted),
                TextAlignment = TextAlignment.Right,
                MinWidth = 20,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(marker, 0);

            var block = NewTextBlock(t.AssistantFg);
            AppendInlines(block, items[n], t);
            Grid.SetColumn(block, 1);

            row.Children.Add(marker);
            row.Children.Add(block);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>代码块：等宽字体 + 独立底色/边框（深色主题下比气泡更暗，形成层次），并标出语言。</summary>
    private static Control BuildCodeBlock(string code, string lang, ChatTheme t)
    {
        var panel = new StackPanel { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(lang))
        {
            panel.Children.Add(new TextBlock
            {
                Text = lang.Trim().ToLowerInvariant(),
                FontSize = 11.5,
                FontFamily = new FontFamily(CodeFontFamily),
                Foreground = Brush(t.CodeLabel)
            });
        }
        panel.Children.Add(new SelectableTextBlock
        {
            Text = code.Length == 0 ? " " : code,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(CodeFontFamily),
            FontSize = CodeFontSize,
            LineHeight = CodeLineHeight,
            Foreground = Brush(t.CodeFg)
        });
        return new Border
        {
            Background = Brush(t.CodeBg),
            BorderBrush = Brush(t.CodeBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 2),
            Child = panel
        };
    }

    /// <summary>创建消息区文本块（正文样式：换行 + 主题前景色 + 统一字号行高）。</summary>
    private static SelectableTextBlock NewTextBlock(Color fg) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = MessageFontSize,
        FontFamily = new FontFamily(MessageFontFamily),
        LineHeight = MessageLineHeight,
        Foreground = Brush(fg)
    };

    /// <summary>把含行内标记的文本按样式拆成 Run 追加到文本块。</summary>
    private static void AppendInlines(SelectableTextBlock block, string text, ChatTheme t)
    {
        var spans = ParseInline(text);
        var urls = new List<string>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeight.SemiBold;
            if (span.Italic) run.FontStyle = FontStyle.Italic;
            if (span.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (span.Code)
            {
                run.FontFamily = new FontFamily(CodeFontFamily);
                run.FontSize = MessageFontSize - 1;
                run.Foreground = Brush(t.InlineCodeFg);
            }
            if (span.Url != null)
            {
                // 不启动外部程序：链接以强调色 + 下划线显示，完整地址通过悬停提示查看
                run.Foreground = Brush(t.Accent);
                run.TextDecorations = TextDecorations.Underline;
                if (!urls.Contains(span.Url)) urls.Add(span.Url);
            }
            block.Inlines!.Add(run);
        }

        // 含链接时把完整地址放进悬停提示，方便用户自行复制
        if (urls.Count > 0) ToolTip.SetTip(block, string.Join("\n", urls));
    }

    /// <summary>拆分行内标记（不成对的标记原样保留，避免把普通文本吃掉）。</summary>
    private static List<InlineSpan> ParseInline(string text)
    {
        var spans = new List<InlineSpan>();
        int pos = 0;
        foreach (Match m in InlineMarkupRegex.Matches(text))
        {
            if (m.Index > pos)
                spans.Add(new InlineSpan(text.Substring(pos, m.Index - pos), false, false, false, false, null));

            var g = m.Groups;
            if (g["code"].Success) spans.Add(new InlineSpan(g["code"].Value, false, false, false, true, null));
            else if (g["bold"].Success) spans.Add(new InlineSpan(g["bold"].Value, true, false, false, false, null));
            else if (g["italic"].Success) spans.Add(new InlineSpan(g["italic"].Value, false, true, false, false, null));
            else if (g["strike"].Success) spans.Add(new InlineSpan(g["strike"].Value, false, false, true, false, null));
            else if (g["linkText"].Success) spans.Add(new InlineSpan(g["linkText"].Value, false, false, false, false, g["linkUrl"].Value));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            spans.Add(new InlineSpan(text.Substring(pos), false, false, false, false, null));
        return spans;
    }

    /// <summary>识别清空命令：clear / cls / 清空 / 清屏。</summary>
    private static bool IsClearCommand(string input)
    {
        var low = input.Trim().ToLowerInvariant();
        return low == "clear" || low == "cls" || low == "清屏" || low.Contains("清空");
    }

    /// <summary>清空 agent 窗口所有内容（消息区 + 会话历史 + 输入框），不留下任何残留消息。</summary>
    public void ClearConversation()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ClearConversation);
            return;
        }
        _history.Clear();
        _entries.Clear();
        _messagesPanel.Children.Clear();
        _inputBox.Clear();
        _pendingInput = null;
        _historyIndex = 0;
    }

    /// <summary>重置会话（清空历史与消息区，给出确认提示）。</summary>
    public void ResetConversation()
    {
        ClearConversation();
        AddMessage("assistant", "会话已重置。");
    }

}

/// <summary>Agent 模型配置（持久化，仅保存用户自定义值；为空表示使用默认模型）。
/// SavedModels 为已设置模型列表，不含默认模型信息。</summary>
public class AgentSettings
{
    public string ApiUri { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string? ApiKey { get; set; }
    public List<AgentSettings> SavedModels { get; set; } = new();

    /// <summary>是否请求并显示模型的思考过程（推理链）。默认开启。</summary>
    public bool ShowThinking { get; set; } = true;
}
