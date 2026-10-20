namespace TextFlowPlus.Models;

/// <summary>
/// 大模型下发的文档编辑动作类型。模型只能通过这些受控动作修改当前打开文档，
/// 不允许其直接输出整篇文档覆盖（防止长文档被截断/改写丢失内容）。
/// </summary>
public enum DocumentEditKind
{
    /// <summary>把文档中 oldText 的第 occurrence 次出现替换为 newText（默认全文替换）。</summary>
    ReplaceText,
    /// <summary>把指定行区间 [startLine, endLine] 整体替换为 newText。</summary>
    ReplaceLines,
    /// <summary>在指定行之后（afterLine=0 表示文档开头）插入 newText。</summary>
    InsertLines,
    /// <summary>删除指定行区间。</summary>
    DeleteLines,
    /// <summary>把整篇文档替换为 newText（仅在用户明确要求"整个文件重写/全部替换"时使用）。</summary>
    ReplaceDocument,
}

/// <summary>
/// 一条文档编辑动作（由大模型以 JSON 形式返回，本地按下标顺序依次应用）。
/// </summary>
public class DocumentEdit
{
    /// <summary>动作类型（JSON 中以字符串给出，如 "replace_text"）。</summary>
    public DocumentEditKind Kind { get; set; }

    /// <summary>原文本（ReplaceText 用；必须与文档中内容逐字一致）。</summary>
    public string? OldText { get; set; }

    /// <summary>新文本（ReplaceText / ReplaceLines / InsertLines / ReplaceDocument 使用）。</summary>
    public string? NewText { get; set; }

    /// <summary>起始行号（1 起始，含）。</summary>
    public int StartLine { get; set; }

    /// <summary>结束行号（1 起始，含）。</summary>
    public int EndLine { get; set; }

    /// <summary>插入锚点行号：在该行之后插入，0 表示插入到文档开头。</summary>
    public int AfterLine { get; set; }

    /// <summary>替换第几次出现（1 起始）；小于等于 0 表示全部替换。仅 ReplaceText 使用。</summary>
    public int Occurrence { get; set; }

    /// <summary>该动作的意图说明（用于在 agent 面板中列出改动清单）。</summary>
    public string? Reason { get; set; }

    /// <summary>应用结果描述（由本地执行器回填，用于汇总回复）。</summary>
    public string? Result { get; set; }
}

/// <summary>
/// 大模型返回的文档编辑计划：一句自然语言回复 + 若干编辑动作。
/// </summary>
public class DocumentEditPlan
{
    /// <summary>给用户的简短中文说明（可为空，本地会按改动清单兜底生成）。</summary>
    public string? Summary { get; set; }

    /// <summary>可选的疑问/澄清说明（模型无法确定修改目标时给出，此时 Edits 通常为空）。</summary>
    public string? Question { get; set; }

    /// <summary>待应用的编辑动作列表（按顺序应用）。</summary>
    public List<DocumentEdit> Edits { get; set; } = new();
}