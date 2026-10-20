using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using TextFlowPlus.Models;

namespace TextFlowPlus.Views;

public partial class FileCompareDialog : Window
{
    private string? _leftFilePath;
    private string? _rightFilePath;
    private List<DiffRange> _diffRanges = new();
    private Models.TextMarkerService? _leftMarkers;
    private Models.TextMarkerService? _rightMarkers;

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;

    public FileCompareDialog()
    {
        InitializeComponent();
        WireUpEvents();
    }

    private void WireUpEvents()
    {
        BrowseLeftButton.Click += (s, e) => _dialogTask = BrowseLeftAsync();
        BrowseRightButton.Click += (s, e) => _dialogTask = BrowseRightAsync();
        RecompareButton.Click += (s, e) => CompareFiles();
        MergeAllLeftToRightButton.Click += (s, e) => MergeAllLeftToRight();
        MergeAllRightToLeftButton.Click += (s, e) => MergeAllRightToLeft();
    }

    private async Task BrowseLeftAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择左侧文件",
            AllowMultiple = false
        });
        if (files.Count > 0)
        {
            _leftFilePath = files[0].Path.LocalPath;
            LeftFileTextBox.Text = _leftFilePath;
            LoadFile(_leftFilePath, LeftTextEditor);
            CompareFiles();
        }
    }

    private async Task BrowseRightAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择右侧文件",
            AllowMultiple = false
        });
        if (files.Count > 0)
        {
            _rightFilePath = files[0].Path.LocalPath;
            RightFileTextBox.Text = _rightFilePath;
            LoadFile(_rightFilePath, RightTextEditor);
            CompareFiles();
        }
    }

    private void LoadFile(string? path, TextEditor editor)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            editor.Text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载文件失败：{ex.Message}";
        }
    }

    private void CompareFiles()
    {
        if (string.IsNullOrEmpty(LeftTextEditor.Text) || string.IsNullOrEmpty(RightTextEditor.Text)) return;

        try
        {
            string[] leftLines = LeftTextEditor.Text.Split('\n');
            string[] rightLines = RightTextEditor.Text.Split('\n');
            _diffRanges = ComputeDiff(leftLines, rightLines);
            DiffCountText.Text = _diffRanges.Count.ToString();
            HighlightDiffs(_diffRanges);
            BuildDiffList();
            StatusText.Text = $"对比完成，发现 {_diffRanges.Count} 处差异";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"对比失败：{ex.Message}";
        }
    }

    private List<DiffRange> ComputeDiff(string[] leftLines, string[] rightLines)
    {
        var diffs = new List<DiffRange>();
        int leftIndex = 0, rightIndex = 0;

        while (leftIndex < leftLines.Length || rightIndex < rightLines.Length)
        {
            if (leftIndex < leftLines.Length && rightIndex < rightLines.Length)
            {
                string leftLine = leftLines[leftIndex].TrimEnd('\r');
                string rightLine = rightLines[rightIndex].TrimEnd('\r');

                if (leftLine == rightLine)
                {
                    leftIndex++;
                    rightIndex++;
                }
                else
                {
                    int nextMatchLeft = -1, nextMatchRight = -1;
                    for (int i = leftIndex + 1; i < Math.Min(leftIndex + 20, leftLines.Length); i++)
                    {
                        for (int j = rightIndex; j < Math.Min(rightIndex + 20, rightLines.Length); j++)
                        {
                            if (leftLines[i].TrimEnd('\r') == rightLines[j].TrimEnd('\r'))
                            {
                                nextMatchLeft = i;
                                nextMatchRight = j;
                                break;
                            }
                        }
                        if (nextMatchLeft >= 0) break;
                    }

                    if (nextMatchLeft >= 0 && nextMatchRight >= 0)
                    {
                        diffs.Add(new DiffRange
                        {
                            LeftStart = leftIndex,
                            LeftEnd = nextMatchLeft,
                            RightStart = rightIndex,
                            RightEnd = nextMatchRight,
                            DiffType = DiffType.Modified,
                            Index = diffs.Count
                        });
                        leftIndex = nextMatchLeft;
                        rightIndex = nextMatchRight;
                    }
                    else
                    {
                        bool foundInLeft = false, foundInRight = false;
                        for (int i = leftIndex + 1; i < Math.Min(leftIndex + 10, leftLines.Length); i++)
                        {
                            if (leftLines[i].TrimEnd('\r') == rightLine) { foundInLeft = true; break; }
                        }
                        for (int i = rightIndex + 1; i < Math.Min(rightIndex + 10, rightLines.Length); i++)
                        {
                            if (rightLines[i].TrimEnd('\r') == leftLine) { foundInRight = true; break; }
                        }

                        if (foundInLeft && !foundInRight)
                        {
                            diffs.Add(new DiffRange
                            {
                                LeftStart = leftIndex,
                                LeftEnd = leftIndex,
                                RightStart = rightIndex,
                                RightEnd = rightIndex + 1,
                                DiffType = DiffType.Insert,
                                Index = diffs.Count
                            });
                            rightIndex++;
                        }
                        else if (foundInRight && !foundInLeft)
                        {
                            diffs.Add(new DiffRange
                            {
                                LeftStart = leftIndex,
                                LeftEnd = leftIndex + 1,
                                RightStart = rightIndex,
                                RightEnd = rightIndex,
                                DiffType = DiffType.Delete,
                                Index = diffs.Count
                            });
                            leftIndex++;
                        }
                        else
                        {
                            diffs.Add(new DiffRange
                            {
                                LeftStart = leftIndex,
                                LeftEnd = leftIndex + 1,
                                RightStart = rightIndex,
                                RightEnd = rightIndex + 1,
                                DiffType = DiffType.Modified,
                                Index = diffs.Count
                            });
                            leftIndex++;
                            rightIndex++;
                        }
                    }
                }
            }
            else if (leftIndex < leftLines.Length)
            {
                int start = leftIndex;
                while (leftIndex < leftLines.Length) leftIndex++;
                diffs.Add(new DiffRange
                {
                    LeftStart = start,
                    LeftEnd = leftIndex,
                    RightStart = rightIndex,
                    RightEnd = rightIndex,
                    DiffType = DiffType.Delete,
                    Index = diffs.Count
                });
            }
            else
            {
                int start = rightIndex;
                while (rightIndex < rightLines.Length) rightIndex++;
                diffs.Add(new DiffRange
                {
                    LeftStart = leftIndex,
                    LeftEnd = leftIndex,
                    RightStart = start,
                    RightEnd = rightIndex,
                    DiffType = DiffType.Insert,
                    Index = diffs.Count
                });
            }
        }
        return diffs;
    }

    private void BuildDiffList()
    {
        DiffListPanel.Children.Clear();

        foreach (var diff in _diffRanges)
        {
            var border = new Border
            {
                Margin = new Thickness(2),
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(3),
                Tag = diff
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            string typeText = diff.DiffType switch
            {
                DiffType.Insert => "➕ 插入",
                DiffType.Delete => "❌ 删除",
                DiffType.Modified => "📝 修改",
                _ => "差异"
            };

            int leftCount = diff.LeftEnd - diff.LeftStart;
            int rightCount = diff.RightEnd - diff.RightStart;
            string desc = leftCount == 0 ? $"{typeText} 右{diff.RightStart + 1}-{diff.RightEnd}行"
                : rightCount == 0 ? $"{typeText} 左{diff.LeftStart + 1}-{diff.LeftEnd}行"
                : $"{typeText} 左{diff.LeftStart + 1} 右{diff.RightStart + 1}";

            var infoText = new TextBlock
            {
                Text = desc,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(infoText, 0);

            var leftToRightBtn = new Button
            {
                Content = "➜",
                Width = 30,
                Height = 24,
                Margin = new Thickness(2, 0, 2, 0),
                Tag = diff
            };
            ToolTip.SetTip(leftToRightBtn, "合并到右侧");
            leftToRightBtn.Click += (s, e) => { if (s is Button b && b.Tag is DiffRange d) MergeLeftToRight(d); };
            Grid.SetColumn(leftToRightBtn, 1);

            var rightToLeftBtn = new Button
            {
                Content = "⬅",
                Width = 30,
                Height = 24,
                Tag = diff
            };
            ToolTip.SetTip(rightToLeftBtn, "合并到左侧");
            rightToLeftBtn.Click += (s, e) => { if (s is Button b && b.Tag is DiffRange d) MergeRightToLeft(d); };
            Grid.SetColumn(rightToLeftBtn, 2);

            grid.Children.Add(infoText);
            grid.Children.Add(leftToRightBtn);
            grid.Children.Add(rightToLeftBtn);
            border.Child = grid;

            // 鼠标悬停效果
            border.PointerEntered += (s, e) => { if (s is Border b) b.Background = new SolidColorBrush(Color.Parse("#20000000")); };
            border.PointerExited += (s, e) => { if (s is Border b) b.Background = Brushes.Transparent; };

            DiffListPanel.Children.Add(border);
        }
    }

    private void HighlightDiffs(List<DiffRange> diffs)
    {
        _leftMarkers?.Clear();
        _rightMarkers?.Clear();
        _leftMarkers = new Models.TextMarkerService(LeftTextEditor.TextArea);
        _rightMarkers = new Models.TextMarkerService(RightTextEditor.TextArea);

        foreach (var diff in diffs)
        {
            for (int j = diff.LeftStart; j < diff.LeftEnd; j++)
            {
                var line = LeftTextEditor.Document.GetLineByNumber(j + 1);
                if (line != null)
                    _leftMarkers.Create(line.Offset, line.Length, Colors.LightCoral);
            }
            for (int j = diff.RightStart; j < diff.RightEnd; j++)
            {
                var line = RightTextEditor.Document.GetLineByNumber(j + 1);
                if (line != null)
                    _rightMarkers.Create(line.Offset, line.Length, Colors.LightGreen);
            }
        }
    }

    private void MergeAllLeftToRight()
    {
        RightTextEditor.Text = LeftTextEditor.Text;
        SaveFile(_rightFilePath, RightTextEditor.Text);
        CompareFiles();
        StatusText.Text = "已合并所有左侧内容到右侧并保存";
    }

    private void MergeAllRightToLeft()
    {
        LeftTextEditor.Text = RightTextEditor.Text;
        SaveFile(_leftFilePath, LeftTextEditor.Text);
        CompareFiles();
        StatusText.Text = "已合并所有右侧内容到左侧并保存";
    }

    private void MergeLeftToRight(DiffRange diff)
    {
        var leftContent = GetLinesContent(LeftTextEditor.Document, diff.LeftStart, diff.LeftEnd);
        if (diff.RightEnd > diff.RightStart)
            DeleteLines(RightTextEditor.Document, diff.RightStart, diff.RightEnd - diff.RightStart);
        if (!string.IsNullOrEmpty(leftContent))
            InsertLines(RightTextEditor.Document, diff.RightStart, leftContent);
        SaveFile(_rightFilePath, RightTextEditor.Text);
        CompareFiles();
        StatusText.Text = "已合并差异（左→右）并保存";
    }

    private void MergeRightToLeft(DiffRange diff)
    {
        var rightContent = GetLinesContent(RightTextEditor.Document, diff.RightStart, diff.RightEnd);
        if (diff.LeftEnd > diff.LeftStart)
            DeleteLines(LeftTextEditor.Document, diff.LeftStart, diff.LeftEnd - diff.LeftStart);
        if (!string.IsNullOrEmpty(rightContent))
            InsertLines(LeftTextEditor.Document, diff.LeftStart, rightContent);
        SaveFile(_leftFilePath, LeftTextEditor.Text);
        CompareFiles();
        StatusText.Text = "已合并差异（右→左）并保存";
    }

    private void SaveFile(string? path, string content)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存文件失败：{ex.Message}";
        }
    }

    private static string GetLinesContent(IDocument doc, int startLine, int endLine)
    {
        if (startLine >= endLine) return "";
        var lines = new List<string>();
        for (int i = startLine; i < endLine && i < doc.LineCount; i++)
        {
            var line = doc.GetLineByNumber(i + 1);
            if (line != null) lines.Add(doc.GetText(line.Offset, line.Length));
        }
        return string.Join("\n", lines);
    }

    private static void DeleteLines(IDocument doc, int startLine, int count)
    {
        if (count <= 0 || startLine < 0 || startLine >= doc.LineCount) return;
        var startLineObj = doc.GetLineByNumber(startLine + 1);
        if (startLineObj == null) return;
        int endLineNum = Math.Min(startLine + count, doc.LineCount);
        var endLineObj = doc.GetLineByNumber(endLineNum);
        if (endLineObj == null) return;
        int endOffset = endLineObj.Offset + endLineObj.Length;
        if (endLineNum < doc.LineCount)
        {
            var nextLine = doc.GetLineByNumber(endLineNum + 1);
            if (nextLine != null) endOffset = nextLine.Offset;
        }
        doc.Replace(startLineObj.Offset, endOffset - startLineObj.Offset, "");
    }

    private static void InsertLines(IDocument doc, int lineNum, string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        if (lineNum < 0) lineNum = 0;
        if (lineNum > doc.LineCount) lineNum = doc.LineCount;
        int insertOffset;
        if (lineNum >= doc.LineCount)
        {
            insertOffset = doc.TextLength;
            if (insertOffset > 0) content = "\n" + content;
        }
        else
        {
            var line = doc.GetLineByNumber(lineNum + 1);
            insertOffset = line?.Offset ?? doc.TextLength;
        }
        doc.Insert(insertOffset, content + "\n");
    }
}
