using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TextFlowPlus.Models;
using DiffType = TextFlowPlus.Models.DiffType;

namespace TextFlowPlus.Views;

public partial class FolderCompareDialog : Window
{
    private string? _leftFolderPath;
    private string? _rightFolderPath;
    private List<FolderDiffRange> _diffRanges = new();
    private List<FileInfo> _leftFiles = new();
    private List<FileInfo> _rightFiles = new();

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;

    public ObservableCollection<FileItem> LeftFileItems { get; } = new();
    public ObservableCollection<FileItem> RightFileItems { get; } = new();

    public FolderCompareDialog()
    {
        InitializeComponent();
        WireUpEvents();
    }

    private void WireUpEvents()
    {
        BrowseLeftButton.Click += (s, e) => _dialogTask = BrowseLeftFolderAsync();
        BrowseRightButton.Click += (s, e) => _dialogTask = BrowseRightFolderAsync();
        RecompareButton.Click += (s, e) => CompareFolders();
        MergeAllLeftToRightButton.Click += (s, e) => MergeAllLeftToRight();
        MergeAllRightToLeftButton.Click += (s, e) => MergeAllRightToLeft();
    }

    private async Task BrowseLeftFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择左侧文件夹"
        });
        if (folders.Count > 0)
        {
            _leftFolderPath = folders[0].Path.LocalPath;
            LeftFolderTextBox.Text = _leftFolderPath;
            LoadFolderFiles(_leftFolderPath, isLeft: true);
            CompareFolders();
        }
    }

    private async Task BrowseRightFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择右侧文件夹"
        });
        if (folders.Count > 0)
        {
            _rightFolderPath = folders[0].Path.LocalPath;
            RightFolderTextBox.Text = _rightFolderPath;
            LoadFolderFiles(_rightFolderPath, isLeft: false);
            CompareFolders();
        }
    }

    private void LoadFolderFiles(string? path, bool isLeft)
    {
        var fileItems = isLeft ? LeftFileItems : RightFileItems;
        fileItems.Clear();

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            var dirInfo = new DirectoryInfo(path);
            foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
            {
                fileItems.Add(new FileItem
                {
                    FileName = $"[DIR] {dir.Name}",
                    FileSize = "-",
                    ModifiedTime = dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    FullPath = dir.FullName,
                    Length = 0,
                    IsFolder = true
                });
            }
            foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
            {
                fileItems.Add(new FileItem
                {
                    FileName = file.Name,
                    FileSize = FormatFileSize(file.Length),
                    ModifiedTime = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    FullPath = file.FullName,
                    Length = file.Length,
                    IsFolder = false
                });
            }
            var files = dirInfo.GetFiles().ToList();
            if (isLeft) _leftFiles = files;
            else _rightFiles = files;

            RefreshFileListPanel(isLeft);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载文件夹失败：{ex.Message}";
        }
    }

    private void RefreshFileListPanel(bool isLeft)
    {
        var panel = isLeft ? LeftFileListPanel : RightFileListPanel;
        var fileItems = isLeft ? LeftFileItems : RightFileItems;
        panel.Children.Clear();

        foreach (var item in fileItems)
        {
            var border = new Border
            {
                Margin = new Thickness(2),
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(item.HighlightColor)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(80)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));

            var nameTb = new TextBlock { Text = item.FileName, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(nameTb, 0);
            grid.Children.Add(nameTb);

            var sizeTb = new TextBlock { Text = item.FileSize, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sizeTb, 1);
            grid.Children.Add(sizeTb);

            var timeTb = new TextBlock { Text = item.ModifiedTime, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(timeTb, 2);
            grid.Children.Add(timeTb);

            border.Child = grid;
            panel.Children.Add(border);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private void CompareFolders()
    {
        if (string.IsNullOrEmpty(_leftFolderPath) || string.IsNullOrEmpty(_rightFolderPath)) return;

        try
        {
            _diffRanges = ComputeDiff();
            DiffCountText.Text = _diffRanges.Count.ToString();
            BuildDiffList();
            HighlightDiffFiles();
            StatusText.Text = $"对比完成，发现 {_diffRanges.Count} 个差异文件";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"对比失败：{ex.Message}";
        }
    }

    private List<FolderDiffRange> ComputeDiff()
    {
        var diffs = new List<FolderDiffRange>();
        var leftDict = _leftFiles.ToDictionary(f => f.Name, f => f);
        var rightDict = _rightFiles.ToDictionary(f => f.Name, f => f);

        foreach (var leftFile in _leftFiles)
        {
            if (!rightDict.ContainsKey(leftFile.Name))
            {
                diffs.Add(new FolderDiffRange
                {
                    FileName = leftFile.Name,
                    DiffType = DiffType.Delete,
                    LeftPath = leftFile.FullName,
                    RightPath = null,
                    LeftLength = leftFile.Length,
                    RightLength = 0
                });
            }
            else
            {
                var rightFile = rightDict[leftFile.Name];
                if (!FilesAreIdentical(leftFile.FullName, rightFile.FullName))
                {
                    diffs.Add(new FolderDiffRange
                    {
                        FileName = leftFile.Name,
                        DiffType = DiffType.Modified,
                        LeftPath = leftFile.FullName,
                        RightPath = rightFile.FullName,
                        LeftLength = leftFile.Length,
                        RightLength = rightFile.Length
                    });
                }
            }
        }

        foreach (var rightFile in _rightFiles)
        {
            if (!leftDict.ContainsKey(rightFile.Name))
            {
                diffs.Add(new FolderDiffRange
                {
                    FileName = rightFile.Name,
                    DiffType = DiffType.Insert,
                    LeftPath = null,
                    RightPath = rightFile.FullName,
                    LeftLength = 0,
                    RightLength = rightFile.Length
                });
            }
        }

        return diffs;
    }

    private static bool FilesAreIdentical(string path1, string path2)
    {
        var info1 = new FileInfo(path1);
        var info2 = new FileInfo(path2);
        if (info1.Length != info2.Length) return false;

        using var stream1 = info1.OpenRead();
        using var stream2 = info2.OpenRead();
        int b1, b2;
        while ((b1 = stream1.ReadByte()) >= 0)
        {
            b2 = stream2.ReadByte();
            if (b1 != b2) return false;
        }
        return true;
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
                DiffType.Insert => "➕ 仅右侧",
                DiffType.Delete => "❌ 仅左侧",
                DiffType.Modified => "📝 内容不同",
                _ => "差异"
            };

            var infoText = new TextBlock
            {
                Text = $"{typeText}\n{diff.FileName}",
                VerticalAlignment = VerticalAlignment.Center,
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
            ToolTip.SetTip(leftToRightBtn, "复制到右侧");
            leftToRightBtn.Click += (s, e) => { if (s is Button b && b.Tag is FolderDiffRange d) CopyFileLeftToRight(d); };
            Grid.SetColumn(leftToRightBtn, 1);

            var rightToLeftBtn = new Button
            {
                Content = "⬅",
                Width = 30,
                Height = 24,
                Tag = diff
            };
            ToolTip.SetTip(rightToLeftBtn, "复制到左侧");
            rightToLeftBtn.Click += (s, e) => { if (s is Button b && b.Tag is FolderDiffRange d) CopyFileRightToLeft(d); };
            Grid.SetColumn(rightToLeftBtn, 2);

            grid.Children.Add(infoText);
            grid.Children.Add(leftToRightBtn);
            grid.Children.Add(rightToLeftBtn);
            border.Child = grid;
            DiffListPanel.Children.Add(border);
        }
    }

    private void HighlightDiffFiles()
    {
        foreach (var item in LeftFileItems) item.HighlightColor = Colors.Transparent;
        foreach (var item in RightFileItems) item.HighlightColor = Colors.Transparent;

        foreach (var diff in _diffRanges)
        {
            if (!string.IsNullOrEmpty(diff.LeftPath))
            {
                var leftItem = LeftFileItems.FirstOrDefault(i => i.FileName == diff.FileName && !i.IsFolder);
                if (leftItem != null)
                {
                    leftItem.HighlightColor = diff.DiffType == DiffType.Delete
                        ? Color.FromRgb(255, 200, 200)
                        : Color.FromRgb(255, 255, 150);
                }
            }
            if (!string.IsNullOrEmpty(diff.RightPath))
            {
                var rightItem = RightFileItems.FirstOrDefault(i => i.FileName == diff.FileName && !i.IsFolder);
                if (rightItem != null)
                {
                    rightItem.HighlightColor = diff.DiffType == DiffType.Insert
                        ? Color.FromRgb(200, 255, 200)
                        : Color.FromRgb(255, 255, 150);
                }
            }
        }

        RefreshFileListPanel(isLeft: true);
        RefreshFileListPanel(isLeft: false);
    }

    private void CopyFileLeftToRight(FolderDiffRange diff)
    {
        if (string.IsNullOrEmpty(diff.LeftPath) || string.IsNullOrEmpty(_rightFolderPath))
        {
            StatusText.Text = "左侧文件不存在";
            return;
        }
        try
        {
            string destPath = Path.Combine(_rightFolderPath, diff.FileName);
            File.Copy(diff.LeftPath, destPath, true);
            StatusText.Text = $"已复制 {diff.FileName} 到右侧文件夹";
            LoadFolderFiles(_rightFolderPath, isLeft: false);
            CompareFolders();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void CopyFileRightToLeft(FolderDiffRange diff)
    {
        if (string.IsNullOrEmpty(diff.RightPath) || string.IsNullOrEmpty(_leftFolderPath))
        {
            StatusText.Text = "右侧文件不存在";
            return;
        }
        try
        {
            string destPath = Path.Combine(_leftFolderPath, diff.FileName);
            File.Copy(diff.RightPath, destPath, true);
            StatusText.Text = $"已复制 {diff.FileName} 到左侧文件夹";
            LoadFolderFiles(_leftFolderPath, isLeft: true);
            CompareFolders();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void MergeAllLeftToRight()
    {
        if (string.IsNullOrEmpty(_leftFolderPath) || string.IsNullOrEmpty(_rightFolderPath))
        {
            StatusText.Text = "请先选择两个文件夹";
            return;
        }

        var result = ShowMessageBox("确认合并",
            "确定要将所有左侧独有的文件和修改的文件复制到右侧吗？\n\n右侧的同名文件将被覆盖！",
            MessageBoxType.YesNo);

        if (result != FolderMessageBoxResult.Yes) return;

        try
        {
            int copiedCount = 0;
            foreach (var diff in _diffRanges)
            {
                if ((diff.DiffType == DiffType.Delete || diff.DiffType == DiffType.Modified) && !string.IsNullOrEmpty(diff.LeftPath))
                {
                    string destPath = Path.Combine(_rightFolderPath!, diff.FileName);
                    File.Copy(diff.LeftPath, destPath, true);
                    copiedCount++;
                }
            }
            StatusText.Text = $"已复制 {copiedCount} 个文件到右侧文件夹";
            LoadFolderFiles(_rightFolderPath, isLeft: false);
            CompareFolders();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"合并失败：{ex.Message}";
        }
    }

    private void MergeAllRightToLeft()
    {
        if (string.IsNullOrEmpty(_leftFolderPath) || string.IsNullOrEmpty(_rightFolderPath))
        {
            StatusText.Text = "请先选择两个文件夹";
            return;
        }

        var result = ShowMessageBox("确认合并",
            "确定要将所有右侧独有的文件和修改的文件复制到左侧吗？\n\n左侧的同名文件将被覆盖！",
            MessageBoxType.YesNo);

        if (result != FolderMessageBoxResult.Yes) return;

        try
        {
            int copiedCount = 0;
            foreach (var diff in _diffRanges)
            {
                if ((diff.DiffType == DiffType.Insert || diff.DiffType == DiffType.Modified) && !string.IsNullOrEmpty(diff.RightPath))
                {
                    string destPath = Path.Combine(_leftFolderPath!, diff.FileName);
                    File.Copy(diff.RightPath, destPath, true);
                    copiedCount++;
                }
            }
            StatusText.Text = $"已复制 {copiedCount} 个文件到左侧文件夹";
            LoadFolderFiles(_leftFolderPath, isLeft: true);
            CompareFolders();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"合并失败：{ex.Message}";
        }
    }

    private FolderMessageBoxResult ShowMessageBox(string title, string message, MessageBoxType type)
    {
        var window = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 10)
                    }
                }
            }
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10
        };

        FolderMessageBoxResult result = FolderMessageBoxResult.No;

        if (type == MessageBoxType.YesNo)
        {
            var yesBtn = new Button { Content = "是", Width = 80, MinHeight = 30 };
            yesBtn.Click += (s, e) => { result = FolderMessageBoxResult.Yes; window.Close(); };
            var noBtn = new Button { Content = "否", Width = 80, MinHeight = 30 };
            noBtn.Click += (s, e) => { result = FolderMessageBoxResult.No; window.Close(); };
            buttonPanel.Children.Add(yesBtn);
            buttonPanel.Children.Add(noBtn);
        }
        else
        {
            var okBtn = new Button { Content = "确定", Width = 80, MinHeight = 30 };
            okBtn.Click += (s, e) => { result = FolderMessageBoxResult.OK; window.Close(); };
            buttonPanel.Children.Add(okBtn);
        }

        ((StackPanel)window.Content!).Children.Add(buttonPanel);
        window.ShowDialog(this);
        return result;
    }
}

public enum MessageBoxType
{
    OK,
    YesNo
}

public enum FolderMessageBoxResult
{
    OK,
    Yes,
    No
}
