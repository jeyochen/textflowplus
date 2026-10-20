namespace TextFlowPlus.Models;

/// <summary>
/// 错别字条目（由 agent 大模型识别返回，行/列均为 1 起始的字符坐标）。
/// </summary>
public class TypoItem
{
    /// <summary>行号（1 起始）</summary>
    public int Line { get; set; }

    /// <summary>列号（1 起始，按字符计，一个汉字算 1 列）</summary>
    public int Column { get; set; }

    /// <summary>原文中的错字 / 错词</summary>
    public string Original { get; set; } = "";

    /// <summary>正确写法</summary>
    public string Suggestion { get; set; } = "";

    /// <summary>原因说明（可选）</summary>
    public string? Reason { get; set; }

    /// <summary>人类可读描述（用于 agent 面板回复）。</summary>
    public override string ToString() =>
        $"第{Line}行第{Column}列：「{Original}」→ 应为「{Suggestion}」" +
        (string.IsNullOrEmpty(Reason) ? "" : $"（{Reason}）");
}
