using System;
using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace TextFlowPlus.Services;

/// <summary>
/// 按大括号（{ }）折叠代码块的折叠策略。
/// AvaloniaEdit 只内置了 XmlFoldingStrategy，没有通用的括号折叠，故自行实现。
///
/// 规则：
/// - 扫描整篇文档，遇到 { 压栈，遇到 } 出栈，配对成功即产生一个可折叠区域；
/// - 折叠区域的起点是 { 所在行的行尾（折叠后该行内容仍可见，折叠标记显示在行尾之后）；
/// - 折叠区域的终点是 } 所在行的行首（保留收尾括号那一行可见）；
/// - 只有跨行的块才折叠：同一行内的 { ... } 不产生折叠，避免行尾出现无意义的标记；
/// - 字符串与注释中的括号需忽略，否则 "{" 这类字符会破坏配对。
/// </summary>
public static class BraceFoldingStrategy
{
    /// <summary>重新计算并应用折叠区域。firstErrorOffset 用于定位无法配对的括号。</summary>
    /// <param name="foldSquareBrackets">是否同时按 [ ] 折叠（JSON 等数据格式）。</param>
    public static void UpdateFoldings(FoldingManager manager, TextDocument document, bool foldSquareBrackets = false)
    {
        int firstErrorOffset = -1;
        var newFoldings = CreateNewFoldings(document, out firstErrorOffset, foldSquareBrackets);
        manager.UpdateFoldings(newFoldings, firstErrorOffset);
    }

    /// <summary>扫描文档，生成所有可折叠的大括号区域（按起点排序）。</summary>
    /// <param name="foldSquareBrackets">是否同时按 [ ] 折叠（JSON 等数据格式）。</param>
    public static IEnumerable<NewFolding> CreateNewFoldings(TextDocument document, out int firstErrorOffset, bool foldSquareBrackets = false)
    {
        firstErrorOffset = -1;
        var foldings = new List<NewFolding>();
        if (document == null) return foldings;

        var text = document.Text;
        if (string.IsNullOrEmpty(text)) return foldings;

        // 栈中记录每个未闭合 { 的偏移量
        var openBraces = new Stack<int>();
        // 栈中记录每个未闭合 [ 的偏移量（JSON 等数据格式按数组折叠）
        var openSquares = new Stack<int>();

        // 词法状态：避免把字符串/注释里的括号当成代码括号
        bool inLineComment = false;      // //
        bool inBlockComment = false;     // /* */
        bool inString = false;           // "..." / '...' / `...`
        bool inVerbatimString = false;   // @"..."（C#，内部 "" 表示一个引号）
        bool inRawString = false;        // """..."""（C# 11 原始字符串，简化处理）
        bool inTemplateString = false;   // `...`（JS/TS 模板字符串，可跨行）
        char stringQuote = '"';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            // 行注释：遇到换行即结束
            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }

            // 块注释：遇到 */ 结束
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }

            // 字符串内部：处理转义与结束引号
            if (inString)
            {
                if (inRawString)
                {
                    // 原始字符串：连续三个引号结束
                    if (c == '"' && next == '"' && i + 2 < text.Length && text[i + 2] == '"')
                    {
                        inRawString = false;
                        inString = false;
                        i += 2;
                    }
                    continue;
                }

                if (inVerbatimString)
                {
                    // @"..." 中 "" 表示一个转义引号，单个 " 结束
                    if (c == '"')
                    {
                        if (next == '"') { i++; continue; }
                        inVerbatimString = false;
                        inString = false;
                    }
                    continue;
                }

                // 模板字符串可跨行，内部 { } 常是插值表达式，为避免误判整段跳过
                if (inTemplateString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '`') { inTemplateString = false; inString = false; }
                    continue;
                }

                if (c == '\\') { i++; continue; }   // 跳过被转义的字符
                if (c == stringQuote) { inString = false; }
                continue;
            }

            // 不在字符串/注释中，识别进入字符串或注释
            switch (c)
            {
                case '/':
                    if (next == '/') { inLineComment = true; i++; continue; }
                    if (next == '*') { inBlockComment = true; i++; continue; }
                    break;

                case '@':
                    // C# 逐字字符串 @"..."
                    if (next == '"')
                    {
                        inString = true;
                        inVerbatimString = true;
                        i++;
                        continue;
                    }
                    break;

                case '"':
                    // C# 11 原始字符串 """..."""
                    if (next == '"' && i + 2 < text.Length && text[i + 2] == '"')
                    {
                        inString = true;
                        inRawString = true;
                        i += 2;
                        continue;
                    }
                    inString = true;
                    inVerbatimString = false;
                    inTemplateString = false;
                    stringQuote = '"';
                    continue;

                case '\'':
                    inString = true;
                    inVerbatimString = false;
                    inTemplateString = false;
                    stringQuote = '\'';
                    continue;

                case '`':
                    // JS/TS 模板字符串（可跨行）
                    inString = true;
                    inVerbatimString = false;
                    inTemplateString = true;
                    continue;

                case '{':
                    openBraces.Push(i);
                    continue;

                case '}':
                    if (openBraces.Count == 0)
                    {
                        // 多余的右括号：记录首个错误位置供 FoldingManager 定位
                        if (firstErrorOffset < 0) firstErrorOffset = i;
                        continue;
                    }
                    int open = openBraces.Pop();
                    AddFoldingIfMultiLine(document, foldings, open, i);
                    continue;

                case '[':
                    // JSON 等数据格式按方括号折叠
                    if (foldSquareBrackets) openSquares.Push(i);
                    continue;

                case ']':
                    if (foldSquareBrackets)
                    {
                        if (openSquares.Count == 0)
                        {
                            if (firstErrorOffset < 0) firstErrorOffset = i;
                            continue;
                        }
                        int openSquare = openSquares.Pop();
                        AddFoldingIfMultiLine(document, foldings, openSquare, i);
                    }
                    continue;
            }
        }

        // 未闭合的 { ：记录首个错误位置
        if (firstErrorOffset < 0 && openBraces.Count > 0)
            firstErrorOffset = openBraces.Peek();

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    /// <summary>只有跨行的块才生成折叠；折叠区间为「{ 所在行行尾」到「} 所在行行首」。</summary>
    private static void AddFoldingIfMultiLine(TextDocument document, List<NewFolding> foldings, int openOffset, int closeOffset)
    {
        var openLine = document.GetLineByOffset(openOffset);
        var closeLine = document.GetLineByOffset(closeOffset);

        // 同一行内的 {}：不折叠
        if (openLine.LineNumber >= closeLine.LineNumber) return;

        int start = openLine.EndOffset;        // { 所在行的行尾
        int end = closeLine.Offset;            // } 所在行的行首

        // 区间必须有效且至少跨一行
        if (end <= start) return;

        foldings.Add(new NewFolding(start, end) { Name = "..." });
    }
}