using System;
using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace TextFlowPlus.Services;

/// <summary>
/// 按缩进层级折叠 Python 代码块的折叠策略。
/// Python 不用大括号组织代码块，而是靠缩进，因此无法复用 BraceFoldingStrategy。
///
/// 规则：
/// - 遍历非空非注释行，计算行首缩进（空格数，Tab 按 4 计）；
/// - 当前行缩进 大于 上一非空非注释行缩进 → 开启一个新折叠块，块头是「上一个非空非注释行」
///   （即 def/class/if/for 等块头行），折叠起点为该块头行行尾（折叠后块头完整可见，与 {} 折叠一致）；
/// - 当前行缩进 回到 某个已开启块的块头缩进层级 → 该块结束，终点为当前行行首（保留收尾行可见）；
/// - 只有跨行的块才生成折叠区域。
/// </summary>
public static class IndentFoldingStrategy
{
    /// <summary>重新计算并应用折叠区域。</summary>
    public static void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        int firstErrorOffset = -1;
        var foldings = CreateFoldings(document);
        manager.UpdateFoldings(foldings, firstErrorOffset);
    }

    /// <summary>扫描文档，按缩进生成所有可折叠区域（按起点排序）。</summary>
    public static IEnumerable<NewFolding> CreateFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        if (document == null) return foldings;

        // 栈中记录每个未闭合块：块头缩进、块头行行尾偏移、块头行号
        var stack = new Stack<(int Indent, int Start, int Line)>();

        int lastIndent = 0;     // 上一个非空非注释行的缩进
        int lastLineEnd = 0;    // 上一个非空非注释行的行尾偏移
        int lastLineNo = 0;     // 上一个非空非注释行的行号

        for (int lineNumber = 1; lineNumber <= document.LineCount; lineNumber++)
        {
            var line = document.GetLineByNumber(lineNumber);
            string text = document.GetText(line);

            if (string.IsNullOrWhiteSpace(text)) continue;              // 空行/纯空白不参与层级变化
            if (text.TrimStart().StartsWith("#")) continue;             // 纯注释行不参与缩进结构

            int indent = CountIndent(text);

            if (indent > lastIndent)
            {
                // 缩进增加：块头 = 上一个非空非注释行（def/class/if/for 等），起点 = 块头行行尾
                if (lastLineNo > 0)
                    stack.Push((lastIndent, lastLineEnd, lastLineNo));
            }
            else if (indent < lastIndent)
            {
                // 缩进回退：闭合所有块头缩进 >= 当前缩进的块
                while (stack.Count > 0 && indent <= stack.Peek().Indent)
                {
                    var top = stack.Pop();
                    int end = line.Offset;   // 块结束：当前行行首
                    if (end > top.Start && line.LineNumber > top.Line)
                        foldings.Add(new NewFolding(top.Start, end) { Name = "..." });
                }
            }

            lastIndent = indent;
            lastLineEnd = line.EndOffset;
            lastLineNo = lineNumber;
        }

        // 文档结束：闭合所有未闭合块（终点为文档末尾）
        while (stack.Count > 0)
        {
            var top = stack.Pop();
            if (document.TextLength > top.Start)
            {
                int endLine = document.GetLineByOffset(Math.Max(top.Start, document.TextLength - 1)).LineNumber;
                if (endLine > top.Line)
                    foldings.Add(new NewFolding(top.Start, document.TextLength) { Name = "..." });
            }
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    /// <summary>计算行首缩进：空格计 1，Tab 计 4。</summary>
    private static int CountIndent(string text)
    {
        int n = 0;
        foreach (char c in text)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }
}
