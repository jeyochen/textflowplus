using System;
using System.Collections.Generic;
using System.Text;

namespace TextFlowPlus.Models;

/// <summary>
/// 文档编辑执行结果：应用后的文本 + 每个动作的执行情况。
/// </summary>
public class DocumentEditResult
{
    /// <summary>编辑后的文档文本。</summary>
    public string Text { get; set; } = "";

    /// <summary>成功应用的动作数。</summary>
    public int AppliedCount { get; set; }

    /// <summary>失败（未命中/参数非法）的动作数。</summary>
    public int FailedCount { get; set; }

    /// <summary>逐条动作的执行结果描述，用于 agent 面板汇总。</summary>
    public List<string> Details { get; } = new();

    /// <summary>是否存在文本变化。</summary>
    public bool Changed { get; set; }
}

/// <summary>
/// 文档编辑动作执行器：把大模型返回的 <see cref="DocumentEdit"/> 列表按顺序应用到文本上。
/// 纯文本变换（不接触 UI / 编辑器控件），便于离线验证与跨模式复用。
/// 行号统一按 1 起始、闭区间理解；越界行号自动钳制，保证不会因模型给出错误行号而抛异常。
/// </summary>
public static class DocumentEditApplier
{
    /// <summary>
    /// 按顺序应用编辑动作。
    /// </summary>
    /// <param name="original">原始文档文本。</param>
    /// <param name="edits">编辑动作列表。</param>
    /// <returns>应用结果（含逐条明细）。</returns>
    public static DocumentEditResult Apply(string original, IReadOnlyList<DocumentEdit> edits)
    {
        var result = new DocumentEditResult { Text = original ?? "" };
        if (edits == null || edits.Count == 0) return result;

        var text = result.Text;

        // 动作按顺序应用；每个动作执行后都重新按最新文本计算行号，
        // 因此模型给出的行号应基于"上一步编辑后的文档"，与常见 agent 习惯一致。
        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var label = $"#{i + 1} {Describe(edit)}";
            try
            {
                var outcome = ApplyOne(text, edit, out var newText);
                if (outcome == null)
                {
                    result.FailedCount++;
                    result.Details.Add($"⚠️ {label}：{edit.Result ?? "未命中，已跳过"}");
                    continue;
                }

                if (!string.Equals(newText, text, StringComparison.Ordinal)) result.Changed = true;
                text = newText!;
                result.AppliedCount++;
                result.Details.Add($"✅ {label}：{outcome}");
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Details.Add($"❌ {label}：{ex.Message}");
            }
        }

        result.Text = text;
        return result;
    }

    /// <summary>应用单个动作；成功返回结果描述，未命中返回 null（并写入 edit.Result 以便上层展示）。</summary>
    private static string? ApplyOne(string text, DocumentEdit edit, out string? newText)
    {
        newText = null;
        switch (edit.Kind)
        {
            case DocumentEditKind.ReplaceText:
                return ApplyReplaceText(text, edit, out newText);
            case DocumentEditKind.ReplaceLines:
                return ApplyReplaceLines(text, edit, out newText);
            case DocumentEditKind.InsertLines:
                return ApplyInsertLines(text, edit, out newText);
            case DocumentEditKind.DeleteLines:
                return ApplyDeleteLines(text, edit, out newText);
            case DocumentEditKind.ReplaceDocument:
                if (edit.NewText == null) { edit.Result = "缺少 newText"; return null; }
                // 整篇替换时沿用原文档的行尾风格（CRLF 文档被模型改成 LF 会造成整文件 diff）
                newText = AdaptNewlines(NormalizeNewlines(edit.NewText), text);
                return $"整篇文档已替换（{CountLines(newText)} 行）";
            default:
                edit.Result = $"不支持的动作类型：{edit.Kind}";
                return null;
        }
    }

    /// <summary>把 oldText 的第 occurrence 次（或全部）出现替换为 newText。</summary>
    private static string? ApplyReplaceText(string text, DocumentEdit edit, out string? newText)
    {
        newText = null;
        var oldText = edit.OldText;
        if (string.IsNullOrEmpty(oldText)) { edit.Result = "缺少 oldText"; return null; }

        // oldText 必须真实存在，避免模型臆造内容导致误改
        int first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
        {
            edit.Result = "文档中未找到该原文（可能已修改或模型引用不准确）";
            return null;
        }

        var replacement = edit.NewText ?? "";

        // occurrence <= 0：全部替换
        if (edit.Occurrence <= 0)
        {
            int count = CountOccurrences(text, oldText);
            newText = text.Replace(oldText, replacement);
            return $"已替换全部 {count} 处";
        }

        // 定位第 occurrence 次出现
        int index = first;
        for (int n = 1; n < edit.Occurrence && index >= 0; n++)
            index = text.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal);
        if (index < 0)
        {
            edit.Result = $"第 {edit.Occurrence} 处未找到（共 {CountOccurrences(text, oldText)} 处）";
            return null;
        }

        newText = string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + oldText.Length));
        return $"已替换第 {edit.Occurrence} 处";
    }

    /// <summary>把行区间 [StartLine, EndLine] 整体替换为 newText。</summary>
    private static string? ApplyReplaceLines(string text, DocumentEdit edit, out string? newText)
    {
        newText = null;
        if (!TryGetLineRange(text, edit.StartLine, edit.EndLine, out var start, out var end, out _, out var error))
        {
            edit.Result = error;
            return null;
        }

        var replacement = AdaptNewlines(NormalizeNewlines(edit.NewText ?? ""), text, start);
        var newline = UsesCrlf(start > 0 ? text.Substring(0, start) : text) ? "\r\n" : "\n";

        // 被替换区间是否以换行结尾（末行通常没有）：决定替换文本是否需要补换行。
        // 补的换行必须沿用文档行尾风格，否则 CRLF 文档会混入 LF。
        bool rangeEndsWithNewline = end > start && text[end - 1] == '\n';
        if (replacement.Length > 0 && !replacement.EndsWith("\n", StringComparison.Ordinal) && rangeEndsWithNewline)
            replacement += newline;
        else if (replacement.Length == 0 && rangeEndsWithNewline)
            replacement = newline;

        newText = string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));

        int replacedLines = CountLinesInRange(text, start, end);
        return $"第 {edit.StartLine}-{edit.EndLine} 行已替换（原 {replacedLines} 行 → 新 {CountLines(replacement)} 行）";
    }

    /// <summary>在 AfterLine 行之后插入 newText（AfterLine=0 插入到文档开头）。</summary>
    private static string? ApplyInsertLines(string text, DocumentEdit edit, out string? newText)
    {
        newText = null;
        var insert = AdaptNewlines(NormalizeNewlines(edit.NewText ?? ""), text);
        if (insert.Length == 0) { edit.Result = "缺少 newText"; return null; }

        // 补的换行沿用文档行尾风格（CRLF 文档不应混入 LF）
        var newline = UsesCrlf(text) ? "\r\n" : "\n";
        int totalLines = CountLines(text);

        // 锚点行：0 表示文档开头；超出总行数则钳制到末行之后
        int anchor = edit.AfterLine;
        if (anchor < 0) anchor = 0;
        bool atStart = anchor == 0;
        bool atEnd = !atStart && anchor >= totalLines;

        if (atStart)
        {
            newText = insert + (insert.EndsWith("\n", StringComparison.Ordinal) ? "" : newline) + text;
        }
        else if (atEnd)
        {
            // 追加到文档末尾：原文档末尾缺少换行时先补一个，避免与插入内容粘连
            var prefix = text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal) ? newline : "";
            newText = text + prefix + insert;
            if (!newText.EndsWith("\n", StringComparison.Ordinal)) newText += newline;
        }
        else
        {
            // 插入到第 anchor 行"之后"：位置是该行行尾换行符之后，即第 anchor+1 行的起始偏移
            // （直接用 GetLineStartOffset(text, anchor) 会插到 anchor 行之前，属错误定位）
            int offset = GetLineStartOffset(text, anchor + 1);
            var tail = insert.EndsWith("\n", StringComparison.Ordinal) ? insert : insert + newline;
            newText = string.Concat(text.AsSpan(0, offset), tail, text.AsSpan(offset));
        }

        var where = atStart ? "文档开头" : (atEnd ? "文档末尾" : $"第 {anchor} 行之后");
        return $"已在{where}插入 {CountLines(insert)} 行";
    }

    /// <summary>删除行区间 [StartLine, EndLine]。</summary>
    private static string? ApplyDeleteLines(string text, DocumentEdit edit, out string? newText)
    {
        newText = null;
        if (!TryGetLineRange(text, edit.StartLine, edit.EndLine, out var start, out var end, out _, out var error))
        {
            edit.Result = error;
            return null;
        }
        int deleted = CountLinesInRange(text, start, end);
        newText = string.Concat(text.AsSpan(0, start), text.AsSpan(end));
        return $"已删除第 {edit.StartLine}-{edit.EndLine} 行（共 {deleted} 行）";
    }

    /// <summary>
    /// 由行号区间求字符偏移区间 [start, end)：end 含该行行尾换行符，
    /// 因此"删除这几行"不会留下空行。
    /// </summary>
    private static bool TryGetLineRange(string text, int startLine, int endLine, out int start, out int end,
        out int lineCount, out string error)
    {
        start = end = 0;
        lineCount = CountLines(text);
        error = "";

        if (startLine < 1 || endLine < 1)
        {
            error = "行号必须从 1 开始";
            return false;
        }
        if (lineCount == 0)
        {
            error = "文档为空";
            return false;
        }

        // 越界行号钳制到合法范围（模型偶发给出行号偏差时不至于失败）
        int from = Math.Min(startLine, lineCount);
        int to = Math.Min(Math.Max(endLine, from), lineCount);

        start = GetLineStartOffset(text, from);
        int nextLineStart = to < lineCount ? GetLineStartOffset(text, to + 1) : text.Length;
        end = nextLineStart;

        // 钳制到行尾（含换行符）
        if (end < start) end = start;
        return true;
    }

    /// <summary>取第 line 行（1 起始）的起始字符偏移；越界返回文本长度。</summary>
    private static int GetLineStartOffset(string text, int line)
    {
        if (line <= 1) return 0;
        int current = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                current++;
                if (current == line) return i + 1;
            }
        }
        return text.Length;
    }

    /// <summary>统计字符区间内包含的行数。</summary>
    private static int CountLinesInRange(string text, int start, int end)
    {
        if (end <= start) return 0;
        int count = 0;
        for (int i = start; i < end && i < text.Length; i++)
            if (text[i] == '\n') count++;
        // 区间末尾没有换行符时仍有最后一行
        if (end > start && text[end - 1] != '\n') count++;
        return count;
    }

    /// <summary>统计文本行数（末行无换行符也算一行）。</summary>
    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        // 文本以换行结尾时，末行为空的"额外一行"不计入
        if (text[^1] == '\n') count--;
        return count;
    }

    /// <summary>统计子串出现次数。</summary>
    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>统一换行符为 \n（模型返回的文本可能含 \r\n）。</summary>
    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// 让插入/替换文本的换行符与目标文档保持一致：
    /// 优先按"被替换区间之前的内容"判断（该区间自身可能已含行尾），退回按整篇文档判断。
    /// </summary>
    private static string AdaptNewlines(string s, string target, int probeEnd = -1)
    {
        if (s.Length == 0) return s;
        // 探测范围优先取区间之前（不受被替换内容行尾影响），否则取整篇文档
        var probe = probeEnd > 0 ? target.Substring(0, probeEnd) : target;
        return UsesCrlf(probe) ? s.Replace("\n", "\r\n") : s;
    }

    /// <summary>判断文本是否使用 CRLF 行尾（以第一个换行符为准）。</summary>
    private static bool UsesCrlf(string text)
    {
        int i = text.IndexOf('\n');
        return i > 0 && text[i - 1] == '\r';
    }

    /// <summary>动作的人类可读描述（用于改动清单）。</summary>
    public static string Describe(DocumentEdit edit)
    {
        var sb = new StringBuilder();
        switch (edit.Kind)
        {
            case DocumentEditKind.ReplaceText:
                sb.Append($"替换文本「{Preview(edit.OldText)}」→「{Preview(edit.NewText)}」");
                if (edit.Occurrence > 0) sb.Append($"(第 {edit.Occurrence} 处)");
                break;
            case DocumentEditKind.ReplaceLines:
                sb.Append($"替换第 {edit.StartLine}-{edit.EndLine} 行");
                break;
            case DocumentEditKind.InsertLines:
                sb.Append(edit.AfterLine <= 0 ? "在文档开头插入" : $"在第 {edit.AfterLine} 行后插入");
                break;
            case DocumentEditKind.DeleteLines:
                sb.Append($"删除第 {edit.StartLine}-{edit.EndLine} 行");
                break;
            case DocumentEditKind.ReplaceDocument:
                sb.Append("替换整篇文档");
                break;
            default:
                sb.Append("未知动作");
                break;
        }
        if (!string.IsNullOrWhiteSpace(edit.Reason)) sb.Append($"（{edit.Reason!.Trim()}）");
        return sb.ToString();
    }

    /// <summary>把长文本压缩为单行预览，避免改动清单被长内容淹没。</summary>
    private static string Preview(string? s, int max = 40)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var one = NormalizeNewlines(s).Replace("\n", "⏎");
        return one.Length <= max ? one : one.Substring(0, max) + "…";
    }
}