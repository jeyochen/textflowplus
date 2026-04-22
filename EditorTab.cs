using System.Text;
using ICSharpCode.AvalonEdit;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace TextFlow;

/// <summary>
/// 表示一个编辑器标签页的数据模型
/// </summary>
public class EditorTab
{
    /// <summary>
    /// 文件路径，未保存的新文件为 null
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 文件名称（用于标签显示）
    /// </summary>
    public string FileName => string.IsNullOrEmpty(FilePath) ? "未命名" : System.IO.Path.GetFileName(FilePath);

    /// <summary>
    /// 文本编辑器控件
    /// </summary>
    public TextEditor TextEditor { get; set; }

    /// <summary>
    /// 文件编码
    /// </summary>
    public Encoding Encoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// 是否已修改
    /// </summary>
    public bool IsModified { get; set; }

    /// <summary>
    /// 语言高亮名称
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// 标签页背景色
    /// </summary>
    public MediaBrush TabColor { get; set; } = Brushes.Transparent;

    /// <summary>
    /// 是否为二进制文件
    /// </summary>
    public bool IsBinary { get; set; } = false;

    /// <summary>
    /// 文件是否已加载完成（用于判断 TextChanged 是否应该触发修改标志）
    /// </summary>
    public bool IsLoaded { get; set; } = false;

    public EditorTab(TextEditor textEditor)
    {
        TextEditor = textEditor;
    }
}
