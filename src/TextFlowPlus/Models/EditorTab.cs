using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using Avalonia.Controls.Documents;
using System.IO;

namespace TextFlowPlus.Models;

/// <summary>编辑器工作模式：文本/代码（AvaloniaEdit）或富文本/图片（Avalonia Documents）。</summary>
public enum EditorMode
{
    /// <summary>纯文本/代码编辑（AvaloniaEdit TextEditor，保留全部现有功能）</summary>
    Text,
    /// <summary>富文本编辑（FlowDocument + RichTextEditor，支持 .docx/.rtf/.doc）</summary>
    RichText,
    /// <summary>图片只读查看</summary>
    ImageViewer,
}

/// <summary>
/// 表示一个编辑器标签页的数据模型
/// </summary>
public class EditorTab
{
    public string? FilePath { get; set; }

    /// <summary>远程文件完整路径（非 null 表示远程标签页：FilePath 为本地缓存路径，保存时回传 SFTP）</summary>
    public string? RemotePath { get; set; }

    /// <summary>远程会话标识 user@host:port（用于打开去重与缓存目录隔离）</summary>
    public string? RemoteKey { get; set; }

    public string FileName => string.IsNullOrEmpty(FilePath) ? "未命名" : System.IO.Path.GetFileName(FilePath);

    public TextEditor TextEditor { get; set; }

    /// <summary>大括号折叠管理器（每个标签页一个；为 null 表示该标签页未启用折叠）</summary>
    public AvaloniaEdit.Folding.FoldingManager? FoldingManager { get; set; }

    /// <summary>左侧折叠边栏（显示 +/- 折叠按钮）</summary>
    public AvaloniaEdit.Folding.FoldingMargin? FoldingMargin { get; set; }

    /// <summary>折叠区域重算的防抖计时器（文档变更后延迟重算）</summary>
    public Avalonia.Threading.DispatcherTimer? FoldingUpdateTimer { get; set; }

    /// <summary>富文本编辑器（富文本/图片模式时使用，惰性创建）</summary>
    public Avalonia.Controls.RichTextEditor? RichEditor { get; set; }

    /// <summary>承装两种编辑器的容器 Grid（标签页 Content，按模式切换可见性）</summary>
    public Avalonia.Controls.Grid? EditorContainer { get; set; }

    /// <summary>图片查看器（图片模式时使用，惰性创建）</summary>
    public Avalonia.Controls.FlowDocumentScrollViewer? ImageViewer { get; set; }

    /// <summary>当前模式（按打开文件类型分派）</summary>
    public EditorMode Mode { get; set; } = EditorMode.Text;

    /// <summary>.doc/.dot 老格式转换后的临时 docx 缓存路径（保存时回写原格式）</summary>
    public string? DocCachePath { get; set; }

    /// <summary>富文本模式加载完成的 FlowDocument（RichEditor.Document 的引用）</summary>
    public FlowDocument? Document => RichEditor?.Document;

    public Encoding Encoding { get; set; } = Encoding.UTF8;

    public bool IsModified { get; set; }

    public string? Language { get; set; }

    public IBrush TabColor { get; set; } = Brushes.Transparent;

    /// <summary>标签页颜色索引（对应亮/暗两套色板，主题切换时按索引取色）</summary>
    public int TabColorIndex { get; set; }

    public bool IsBinary { get; set; } = false;

    public bool IsLoaded { get; set; } = false;

    public FileSystemWatcher? FileWatcher { get; set; }

    public bool IsExternalChanging { get; set; } = false;

    public DateTime LastWriteTimeUtc { get; set; }

    public bool IsShowingReloadPrompt { get; set; } = false;

    /// <summary>错别字高亮着色器（每标签页一个，注册在编辑器渲染管道中）</summary>
    public TypoHighlightColorizer? TypoColorizer { get; set; }

    /// <summary>选中文字着色器（每标签页一个，工具栏字体颜色面板为选中文字着色）</summary>
    public SelectionColorColorizer? FontColorizer { get; set; }

    /// <summary>选中匹配高亮着色器（每标签页一个，注册在编辑器渲染管道中）</summary>
    public SelectionMatchColorizer? MatchColorizer { get; set; }

    /// <summary>选中匹配高亮防抖定时器（选区变化后延迟扫描文档，避免拖动选区时频繁全文档搜索）</summary>
    public DispatcherTimer? MatchHighlightTimer { get; set; }

    public EditorTab(TextEditor textEditor)
    {
        TextEditor = textEditor;
    }
}
