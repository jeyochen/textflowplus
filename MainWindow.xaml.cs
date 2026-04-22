using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using Newtonsoft.Json;
using System.Xml;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace TextFlow;

public partial class MainWindow : Window
{
    private List<EditorTab> _tabs = new();
    private List<EditorTab> _rightTabs = new();
    private double currentZoom = 1.0;
    private bool _isSplitView = false;

    public MainWindow()
    {
        InitializeComponent();
        TestResource.Test();
        LoadCustomJsonHighlighting();
        LoadCustomXAMLHighlighting();
        LoadCustomMarkdownHighlighting();
        LoadCustomYamlHighlighting();
        InitDragDrop(); // 初始化拖拽支持

        // 设置字体框默认为第一个（Consolas）
        FontFamilyComboBox.SelectedIndex = 0;

        // 设置字体大小默认为 16
        FontSizeComboBox.SelectedIndex = 6; // 16 在列表中的索引

        NewFile(); // 启动时创建一个新标签
    }

    /// <summary>
    /// 获取当前选中标签页的 TextEditor
    /// </summary>
    public TextEditor GetCurrentTextEditor()
    {
        if (_isSplitView)
        {
            // 分栏模式下，根据焦点决定
            var focusedElement = System.Windows.Input.Keyboard.FocusedElement;
            if (focusedElement != null)
            {
                // 检查焦点是否在右侧 TabControl 中
                var rightTab = GetRightTab();
                if (rightTab != null && rightTab.TextEditor.IsKeyboardFocusWithin)
                {
                    return rightTab.TextEditor;
                }
            }
        }
        if (FileTabControl.SelectedItem is TabItem selectedTab &&
            selectedTab.Tag is EditorTab editorTab)
        {
            return editorTab.TextEditor;
        }
        return null!;
    }

    /// <summary>
    /// 获取当前选中的 EditorTab
    /// </summary>
    public EditorTab? GetCurrentTab()
    {
        if (FileTabControl.SelectedItem is TabItem selectedTab)
        {
            return selectedTab.Tag as EditorTab;
        }
        return null;
    }

    /// <summary>
    /// 获取右侧面板当前选中的 EditorTab
    /// </summary>
    public EditorTab? GetRightTab()
    {
        if (RightTabControl.SelectedItem is TabItem selectedTab)
        {
            return selectedTab.Tag as EditorTab;
        }
        return null;
    }

    /// <summary>
    /// 预定义的标签页颜色池
    /// </summary>
    private static readonly MediaBrush[] TabColors = new MediaBrush[]
    {
        System.Windows.Media.Brushes.LightCoral,      // 浅红色
        System.Windows.Media.Brushes.LightSalmon,     // 浅肉色
        System.Windows.Media.Brushes.LightYellow,     // 浅黄色
        System.Windows.Media.Brushes.LightGreen,      // 浅绿色
        System.Windows.Media.Brushes.LightBlue,       // 浅蓝色
        System.Windows.Media.Brushes.Lavender,        // 淡紫色
        System.Windows.Media.Brushes.Pink,            // 粉色
        System.Windows.Media.Brushes.PeachPuff,       // 桃色
        System.Windows.Media.Brushes.PaleGreen,       // 淡绿色
        System.Windows.Media.Brushes.SkyBlue,         // 天蓝色
    };

    /// <summary>
    /// 当前已使用的颜色索引
    /// </summary>
    private int _colorIndex = 0;

    /// <summary>
    /// 创建一个新的 TextEditor 实例
    /// </summary>
    private TextEditor CreateNewTextEditor()
    {
        var editor = new TextEditor
        {
            FontFamily = new MediaFontFamily("Consolas"),
            FontSize = 16 * currentZoom,
            ShowLineNumbers = true,
            WordWrap = false,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Options =
            {
                ShowTabs = false,
                ShowSpaces = false,
                ShowEndOfLine = false,
                HighlightCurrentLine = true,
                EnableHyperlinks = false,
                EnableRectangularSelection = true,
                AllowScrollBelowDocument = true
            }
        };

        // 安装搜索面板
        SearchPanel.Install(editor);

        return editor;
    }

    #region 标签页操作

    /// <summary>
    /// 创建新文件标签
    /// </summary>
    private EditorTab CreateNewTab(bool rightPanel = false)
    {
        var textEditor = CreateNewTextEditor();
        var tab = new EditorTab(textEditor)
        {
            TabColor = TabColors[_colorIndex % TabColors.Length]
        };
        _colorIndex++;

        var tabItem = new TabItem
        {
            Header = tab.FileName,
            Tag = tab,
            Content = textEditor,
            Background = tab.TabColor
        };

        if (rightPanel)
        {
            _rightTabs.Add(tab);
            RightTabControl.Items.Add(tabItem);
            RightTabControl.SelectedItem = tabItem;

            // 绑定事件（右侧面板）
            textEditor.TextChanged += (s, e) =>
            {
                if (tab.IsLoaded) RightTab_TextChanged(tab);
            };
            textEditor.TextChanged += (s, e) => UpdateRightFileStats(tab);
            textEditor.TextArea.Caret.PositionChanged += (s, e) => RightCaret_PositionChanged(tab);

            UpdateRightFileStats(tab);
        }
        else
        {
            _tabs.Add(tab);
            FileTabControl.Items.Add(tabItem);
            FileTabControl.SelectedItem = tabItem;

            // 绑定事件
            textEditor.TextChanged += (s, e) =>
            {
                if (tab.IsLoaded) Tab_TextChanged(tab);
            };
            textEditor.TextChanged += (s, e) => UpdateFileStats(tab);
            textEditor.TextArea.Caret.PositionChanged += (s, e) => Caret_PositionChanged(tab);

            UpdateFileStats(tab);
        }

        UpdateTitle();

        return tab;
    }

    private EditorTab CreateNewTab()
    {
        return CreateNewTab(false);
    }

    private void Tab_TextChanged(EditorTab tab)
    {
        if (!tab.IsModified)
        {
            tab.IsModified = true;
            UpdateTabHeader(tab);
            UpdateTitle();
        }
    }

    private void Caret_PositionChanged(EditorTab tab)
    {
        if (tab == GetCurrentTab())
        {
            int lineNum = tab.TextEditor.TextArea.Caret.Line;
            int columnNum = tab.TextEditor.TextArea.Caret.Column;
            LeftCursorPosText.Text = $"行：{lineNum}, 列：{columnNum}";
        }
        UpdateFileStats(tab);
    }

    private void UpdateFileStats(EditorTab tab)
    {
        string text = tab.TextEditor.Text;
        int lineCount = tab.TextEditor.Document.LineCount;
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(text);

        LeftLineCountText.Text = $"行数：{lineCount}";
        LeftByteCountText.Text = $"字节：{byteCount}";
        LeftEncodingText.Text = $"编码：{GetEncodingName(tab.Encoding)}";
    }

    private string GetEncodingName(Encoding encoding)
    {
        if (encoding is UTF8Encoding utf8)
        {
            // 检查是否有 BOM
            if (utf8.GetPreamble().Length > 0)
                return "UTF-8 with BOM";
            return "UTF-8";
        }
        if (encoding is UnicodeEncoding)
            return "UTF-16";
        if (encoding is UTF32Encoding)
            return "UTF-32";
        if (encoding.CodePage == 936 || encoding.WebName.ToLower() == "gb2312")
            return "GB2312";
        if (encoding == Encoding.Default)
            return "ANSI";
        return encoding.EncodingName;
    }

    private void UpdateTabHeader(EditorTab tab)
    {
        if (FileTabControl.Items.Cast<TabItem>().FirstOrDefault(x => x.Tag == tab) is TabItem tabItem)
        {
            string modifiedMark = tab.IsModified ? " *" : "";
            tabItem.Header = tab.FileName + modifiedMark;
        }
    }

    public void UpdateTabHeaderPublic(EditorTab tab)
    {
        UpdateTabHeader(tab);
    }

    #region 右侧面板事件处理

    private void RightTab_TextChanged(EditorTab tab)
    {
        if (!tab.IsModified)
        {
            tab.IsModified = true;
            UpdateRightTabHeader(tab);
        }
    }

    private void RightCaret_PositionChanged(EditorTab tab)
    {
        if (_isSplitView)
        {
            int lineNum = tab.TextEditor.TextArea.Caret.Line;
            int columnNum = tab.TextEditor.TextArea.Caret.Column;
            RightCursorPosText.Text = $"行：{lineNum}, 列：{columnNum}";
        }
        UpdateRightFileStats(tab);
    }

    private void UpdateRightFileStats(EditorTab tab)
    {
        string text = tab.TextEditor.Text;
        int lineCount = tab.TextEditor.Document.LineCount;
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(text);

        RightLineCountText.Text = $"行数：{lineCount}";
        RightByteCountText.Text = $"字节：{byteCount}";
        RightEncodingText.Text = $"编码：{GetEncodingName(tab.Encoding)}";
    }

    private void UpdateRightTabHeader(EditorTab tab)
    {
        if (RightTabControl.Items.Cast<TabItem>().FirstOrDefault(x => x.Tag == tab) is TabItem tabItem)
        {
            string modifiedMark = tab.IsModified ? " *" : "";
            tabItem.Header = tab.FileName + modifiedMark;
        }
    }

    private void RightTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSplitView)
        {
            var tab = GetRightTab();
            if (tab != null)
            {
                RightFilePathText.Text = tab.FilePath ?? "";
                UpdateRightZoomDisplay();
                UpdateRightFileStats(tab);
            }
        }
    }

    private void UpdateRightZoomDisplay()
    {
        RightZoomText.Text = $"{(int)(currentZoom * 100)}%";
    }

    #endregion

    private void FileTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            LeftFilePathText.Text = tab.FilePath ?? "";
            UpdateZoomDisplay();

            // 应用当前标签的语法高亮
            if (tab.TextEditor.SyntaxHighlighting != null)
            {
                // 已有高亮，直接使用
            }
            else if (!string.IsNullOrEmpty(tab.FilePath))
            {
                // 根据文件扩展名设置高亮
                string fileExtension = Path.GetExtension(tab.FilePath).ToLower();
                SetLanguageByExtension(fileExtension, tab);
            }

            // 更新文件统计信息
            UpdateFileStats(tab);
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        // 从视觉树查找 TabItem
        var button = (DependencyObject)sender;
        var parent = VisualTreeHelper.GetParent(button);
        TabItem? tabItem = null;
        bool isRightPanel = false;

        while (parent != null && parent is not TabItem)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is TabItem ti)
        {
            tabItem = ti;
            // 检查该 TabItem 是否属于右侧 TabControl
            var rightTabControl = FindVisualParent<TabControl>(button);
            isRightPanel = (rightTabControl == RightTabControl);
        }

        if (tabItem != null)
        {
            if (isRightPanel)
            {
                CloseRightTab(tabItem);
            }
            else
            {
                CloseTab(tabItem);
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typedParent)
            {
                return typedParent;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void CloseTab(TabItem tabItem)
    {
        if (tabItem.Tag is not EditorTab tab)
            return;

        if (tab.IsModified)
        {
            var result = MessageBox.Show($"文件 \"{tab.FileName}\" 已修改，是否保存？", "确认",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                SaveTab(tab);
            }
            else if (result == MessageBoxResult.Cancel)
            {
                return;
            }
        }

        // 移除标签
        _tabs.Remove(tab);
        FileTabControl.Items.Remove(tabItem);

        // 如果关闭的是最后一个标签，创建一个新标签
        if (_tabs.Count == 0 && !_isSplitView)
        {
            NewFile();
        }
    }

    private void CloseRightTab(TabItem tabItem)
    {
        if (tabItem.Tag is not EditorTab tab)
            return;

        if (tab.IsModified)
        {
            var result = MessageBox.Show($"文件 \"{tab.FileName}\" 已修改，是否保存？", "确认",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                SaveTab(tab);
            }
            else if (result == MessageBoxResult.Cancel)
            {
                return;
            }
        }

        // 移除标签
        _rightTabs.Remove(tab);
        RightTabControl.Items.Remove(tabItem);

        // 如果关闭的是最后一个标签，创建一个新标签
        if (_rightTabs.Count == 0 && _isSplitView)
        {
            CreateNewTab(true);
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        // 从视觉树查找 TabItem，判断是左侧还是右侧
        var parent = VisualTreeHelper.GetParent((DependencyObject)sender);
        TabItem? tabItem = null;
        bool isRightPanel = false;

        while (parent != null && parent is not TabItem)
        {
            // 检查是否在右侧 TabControl 中
            if (parent is TabControl tc && tc == RightTabControl)
            {
                isRightPanel = true;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is TabItem ti)
        {
            tabItem = ti;
        }

        if (tabItem != null)
        {
            if (isRightPanel)
            {
                CloseRightTab(tabItem);
            }
            else
            {
                CloseTab(tabItem);
            }
        }
    }

    #endregion

    #region 文件操作

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        NewFile();
    }

    private EditorTab NewFile()
    {
        var tab = CreateNewTab();
        tab.Language = "C#";
        tab.TextEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        LeftStatusText.Text = "已新建文件";
        UpdateTitle();
        // 更新状态栏显示新文件的信息
        UpdateFileStats(tab);
        return tab;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "所有支持的文件 (*.cs;*.c;*.cpp;*.cc;*.json;*.py;*.js;*.txt;*.xaml;*.md;*.yaml;*.yml;*.bin)|*.cs;*.c;*.cpp;*.cc;*.json;*.py;*.js;*.txt;*.xaml;*.md;*.yaml;*.yml;*.bin|" +
                     "C# 文件 (*.cs)|*.cs|" +
                     "C/C++ 文件 (*.c;*.cpp;*.cc)|*.c;*.cpp;*.cc|" +
                     "Python 文件 (*.py)|*.py|" +
                     "JavaScript 文件 (*.js)|*.js|" +
                     "JSON 文件 (*.json)|*.json|" +
                     "XAML 文件 (*.xaml)|*.xaml|" +
                     "Markdown 文件 (*.md;*.markdown)|*.md;*.markdown|" +
                     "YAML 文件 (*.yaml;*.yml)|*.yaml;*.yml|" +
                     "文本文件 (*.txt)|*.txt|" +
                     "二进制文件 (*.bin)|*.bin|" +
                     "所有文件 (*.*)|*.*",
            Title = "打开文件",
            Multiselect = true
        };

        if (openFileDialog.ShowDialog() == true)
        {
            foreach (var fileName in openFileDialog.FileNames)
            {
                OpenFile(fileName);
            }
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            // 检查文件大小，超过 500MB 不予打开
            FileInfo fileInfo = new FileInfo(path);
            if (fileInfo.Length > 500 * 1024 * 1024)
            {
                MessageBox.Show($"文件 \"{path}\" 大于 500MB，无法打开。\n" +
                                $"文件大小：{fileInfo.Length / (1024 * 1024)} MB\n" +
                                $"最大支持：500 MB", "文件过大", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fileExtension = Path.GetExtension(path).ToLower();

            // 判断是否为二进制文件
            bool isBinary = fileExtension == ".bin";

            // 分栏模式下默认在右侧打开新文件
            bool openInRight = _isSplitView && GetRightTab() != null && _rightTabs.Count < _tabs.Count;
            var tab = CreateNewTab(openInRight);
            tab.FilePath = path;

            if (isBinary)
            {
                // 二进制文件使用特殊模式显示
                OpenBinaryFile(path, tab);
            }
            else
            {
                try
                {
                    // 文本文件正常读取
                    tab.Encoding = ReadFileWithEncodingDetection(path, tab.TextEditor);
                }
                catch (Exception readEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Read error: {readEx.Message}");
                    tab.TextEditor.Text = $"Error reading file: {readEx.Message}";
                }

                // 根据文件扩展名设置语言
                try
                {
                    SetLanguageByExtension(fileExtension, tab);
                }
                catch (Exception syntaxEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Syntax highlighting error: {syntaxEx.Message}");
                    // 如果语法高亮失败，继续使用纯文本
                    tab.TextEditor.SyntaxHighlighting = null;
                }
            }

            // 文件加载完成，标记为已加载且未修改
            tab.IsLoaded = true;
            tab.IsModified = false;
            UpdateTabHeader(tab);
            UpdateTitle();

            if (openInRight)
            {
                UpdateRightTabHeader(tab);
                RightFilePathText.Text = tab.FilePath;
                UpdateRightFileStats(tab);
            }
            else
            {
                UpdateTabHeader(tab);
                LeftFilePathText.Text = tab.FilePath;
                UpdateFileStats(tab);
            }
            LeftStatusText.Text = $"已打开文件：{Path.GetFileName(path)}";
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Encoding ReadFileWithEncodingDetection(string path, TextEditor editor)
    {
        byte[] bytes = File.ReadAllBytes(path);

        // 检测 BOM - UTF-8 with BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            editor.Text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return new UTF8Encoding(true);
        }

        // 检测 UTF-16 BOM (Little Endian)
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            editor.Text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.Unicode;
        }

        // 检测 UTF-16 BOM (Big Endian)
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            editor.Text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.BigEndianUnicode;
        }

        // 检测 UTF-32 BOM (Little Endian)
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            editor.Text = Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
            return Encoding.UTF32;
        }

        // 先尝试 UTF-8（无 BOM）- 最常见的无 BOM 编码
        try
        {
            string utf8Text = Encoding.UTF8.GetString(bytes);
            if (IsValidUtf8(bytes) && !utf8Text.Contains('\uFFFD'))
            {
                editor.Text = utf8Text;
                return new UTF8Encoding(false);
            }
        }
        catch { }

        // 再尝试 GB2312/GBK 编码（仅当文件包含中文字符时）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gb2312 = Encoding.GetEncoding(936);
        try
        {
            string gbText = gb2312.GetString(bytes);
            // 检查是否包含典型的中文字符范围，且不包含替换字符
            if (!gbText.Contains('\uFFFD') && HasChineseChars(gbText))
            {
                editor.Text = gbText;
                return gb2312;
            }
        }
        catch { }

        // 默认使用 ANSI
        editor.Text = Encoding.Default.GetString(bytes);
        return Encoding.Default;
    }

    /// <summary>
    /// 检查文本是否包含中文字符
    /// </summary>
    private bool HasChineseChars(string text)
    {
        foreach (char c in text)
        {
            // CJK Unified Ideographs 范围
            if (c >= 0x4E00 && c <= 0x9FFF)
                return true;
        }
        return false;
    }

    private void OpenBinaryFile(string path, EditorTab tab)
    {
        byte[] bytes = File.ReadAllBytes(path);

        // 二进制文件以十六进制格式显示
        string hexContent = BitConverter.ToString(bytes);
        tab.TextEditor.Text = hexContent;
        tab.Language = "Binary";
        tab.IsBinary = true;

        LeftStatusText.Text = $"已打开二进制文件：{Path.GetFileName(path)} ({bytes.Length} 字节)";
    }

    private bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            SaveTab(tab);
        }
    }

    private void SaveTab(EditorTab tab)
    {
        if (string.IsNullOrEmpty(tab.FilePath))
        {
            SaveAsTab(tab);
            return;
        }

        try
        {
            if (tab.IsBinary)
            {
                // 二进制文件保存：将十六进制文本转换回字节
                SaveBinaryFile(tab.FilePath, tab);
            }
            else
            {
                SaveWithEncoding(tab.FilePath, tab.Encoding, tab.TextEditor.Text);
            }
            tab.IsModified = false;
            UpdateTabHeader(tab);
            LeftStatusText.Text = $"已保存文件：{Path.GetFileName(tab.FilePath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            SaveAsTab(tab);
        }
    }

    private void SaveAsTab(EditorTab tab)
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "C# 文件 (*.cs)|*.cs|" +
                     "C/C++ 文件 (*.c;*.cpp;*.cc)|*.c;*.cpp;*.cc|" +
                     "Python 文件 (*.py)|*.py|" +
                     "JavaScript 文件 (*.js)|*.js|" +
                     "JSON 文件 (*.json)|*.json|" +
                     "XAML 文件 (*.xaml)|*.xaml|" +
                     "Markdown 文件 (*.md;*.markdown)|*.md;*.markdown|" +
                     "YAML 文件 (*.yaml;*.yml)|*.yaml;*.yml|" +
                     "文本文件 (*.txt)|*.txt|" +
                     "二进制文件 (*.bin)|*.bin|" +
                     "所有文件 (*.*)|*.*",
            Title = "另存为"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                if (tab.IsBinary)
                {
                    SaveBinaryFile(saveFileDialog.FileName, tab);
                }
                else
                {
                    SaveWithEncoding(saveFileDialog.FileName, tab.Encoding, tab.TextEditor.Text);
                }
                tab.FilePath = saveFileDialog.FileName;
                tab.IsModified = false;
                UpdateTabHeader(tab);
                LeftFilePathText.Text = tab.FilePath;
                LeftStatusText.Text = $"已保存文件：{Path.GetFileName(tab.FilePath)}";
                UpdateTitle();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SaveBinaryFile(string path, EditorTab tab)
    {
        // 将十六进制文本转换回字节数组
        string hexText = tab.TextEditor.Text.Replace("-", "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
        byte[] bytes = new byte[hexText.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hexText.Substring(i * 2, 2), 16);
        }
        File.WriteAllBytes(path, bytes);
    }

    private void SaveWithEncoding(string path, Encoding encoding, string text)
    {
        // 使用 StreamWriter 来正确写入编码（包括 BOM）
        // 注意：StreamWriter 的构造函数会自动处理 BOM
        using (var writer = new StreamWriter(path, append: false, encoding: encoding))
        {
            writer.Write(text);
        }
    }


    // GB2312 编码（简体中文）- 使用 CodePage 936
    private static Encoding? _gb2312Encoding;
    private static Encoding Gb2312Encoding
    {
        get
        {
            if (_gb2312Encoding == null)
            {
                // 注册编码提供程序以支持 GB2312
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _gb2312Encoding = Encoding.GetEncoding(936);
            }
            return _gb2312Encoding;
        }
    }

    private void SaveAsAnsi_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            SaveTabWithSpecificEncoding(tab, Gb2312Encoding);
        }
    }

    private void SaveAsUtf8_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            SaveTabWithSpecificEncoding(tab, new UTF8Encoding(false));
        }
    }

    private void SaveAsUtf8Bom_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            SaveTabWithSpecificEncoding(tab, new UTF8Encoding(true));
        }
    }

    private void SaveTabWithSpecificEncoding(EditorTab tab, Encoding encoding)
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "所有文件 (*.*)|*.*",
            Title = $"以 {encoding.EncodingName} 编码另存为"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                SaveWithEncoding(saveFileDialog.FileName, encoding, tab.TextEditor.Text);
                tab.FilePath = saveFileDialog.FileName;
                tab.Encoding = encoding;
                tab.IsModified = false;
                UpdateTabHeader(tab);
                LeftStatusText.Text = $"已 {encoding.EncodingName} 编码保存";
                UpdateTitle();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ConvertToAnsi_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            ConvertTabEncoding(tab, Gb2312Encoding, "GB2312");
        }
    }

    private void ConvertToUtf8_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            ConvertTabEncoding(tab, new UTF8Encoding(false), "UTF-8");
        }
    }

    private void ConvertToUtf8Bom_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            ConvertTabEncoding(tab, new UTF8Encoding(true), "UTF-8 with BOM");
        }
    }

    private void ConvertTabEncoding(EditorTab tab, Encoding targetEncoding, string encodingName)
    {
        if (string.IsNullOrEmpty(tab.TextEditor.Text))
        {
            LeftStatusText.Text = "当前文件为空，无法转换编码";
            return;
        }

        if (string.IsNullOrEmpty(tab.FilePath))
        {
            MessageBox.Show("请先保存文件后再进行编码转换。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // .NET 中字符串始终是 UTF-16，所以只需要用目标编码保存文件即可
            // 文本内容不需要转换

            // 1. 更新编码设置
            tab.Encoding = targetEncoding;

            // 2. 用目标编码保存文件（文本内容不变，只是字节表示改变）
            SaveWithEncoding(tab.FilePath, targetEncoding, tab.TextEditor.Text);

            // 3. 更新状态
            tab.IsModified = false;
            UpdateTabHeader(tab);
            UpdateTitle();
            UpdateEncodingDisplay(tab);
            LeftStatusText.Text = $"已转换为 {encodingName} 编码并保存";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"编码转换失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            LeftStatusText.Text = "编码转换失败";
        }
    }

    /// <summary>
    /// 更新编码显示
    /// </summary>
    private void UpdateEncodingDisplay(EditorTab tab)
    {
        string encodingName = GetEncodingName(tab.Encoding);
        if (_tabs.Contains(tab))
        {
            LeftEncodingText.Text = $"编码：{encodingName}";
        }
        else if (_rightTabs.Contains(tab))
        {
            RightEncodingText.Text = $"编码：{encodingName}";
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        // 检查所有已修改的标签
        var modifiedTabs = _tabs.Where(t => t.IsModified).ToList();
        if (modifiedTabs.Count > 0)
        {
            var result = MessageBox.Show(
                $"有 {modifiedTabs.Count} 个文件已修改但未保存，是否保存？", "确认",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var tab in modifiedTabs)
                {
                    SaveTab(tab);
                }
            }
            else if (result == MessageBoxResult.Cancel)
            {
                return;
            }
        }
        Application.Current.Shutdown();
    }

    #endregion

    #region 编辑操作

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null && editor.CanUndo)
        {
            editor.Undo();
            LeftStatusText.Text = "已撤销";
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null && editor.CanRedo)
        {
            editor.Redo();
            LeftStatusText.Text = "已重做";
        }
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null && !string.IsNullOrEmpty(editor.SelectedText))
        {
            Clipboard.SetText(editor.SelectedText);
            editor.SelectedText = string.Empty;
            LeftStatusText.Text = "已剪切内容到剪贴板";
            var tab = GetCurrentTab();
            if (tab != null)
            {
                tab.IsModified = true;
                UpdateTabHeader(tab);
                UpdateTitle();
            }
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null && !string.IsNullOrEmpty(editor.SelectedText))
        {
            Clipboard.SetText(editor.SelectedText);
            LeftStatusText.Text = "已复制内容到剪贴板";
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null && Clipboard.ContainsText())
        {
            editor.SelectedText = Clipboard.GetText();
            LeftStatusText.Text = "已从剪贴板粘贴";
            var tab = GetCurrentTab();
            if (tab != null)
            {
                tab.IsModified = true;
                UpdateTabHeader(tab);
                UpdateTitle();
            }
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        editor?.SelectAll();
        LeftStatusText.Text = "已全选";
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null)
        {
            // 直接聚焦编辑器，让用户使用 Ctrl+F 触发查找
            editor.Focus();
            // 使用 Command 方式触发查找
            ApplicationCommands.Find.Execute(null, editor.TextArea);
        }
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentTextEditor();
        if (editor != null)
        {
            var replaceDialog = new ReplaceDialog(this);
            replaceDialog.Owner = this;
            replaceDialog.ShowDialog();
        }
    }

    private void ShowTabs_Click(object sender, RoutedEventArgs e)
    {
        var show = ShowTabsMenuItem.IsChecked;
        foreach (var tab in _tabs)
        {
            tab.TextEditor.Options.ShowTabs = show;
        }
        foreach (var tab in _rightTabs)
        {
            tab.TextEditor.Options.ShowTabs = show;
        }
    }

    private void ShowSpaces_Click(object sender, RoutedEventArgs e)
    {
        var show = ShowSpacesMenuItem.IsChecked;
        foreach (var tab in _tabs)
        {
            tab.TextEditor.Options.ShowSpaces = show;
        }
        foreach (var tab in _rightTabs)
        {
            tab.TextEditor.Options.ShowSpaces = show;
        }
    }

    private void ShowEndOfLine_Click(object sender, RoutedEventArgs e)
    {
        var show = ShowEndOfLineMenuItem.IsChecked;
        foreach (var tab in _tabs)
        {
            tab.TextEditor.Options.ShowEndOfLine = show;
        }
        foreach (var tab in _rightTabs)
        {
            tab.TextEditor.Options.ShowEndOfLine = show;
        }
    }

    private void FormatCode_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            FormatCode(tab);
        }
    }

    private void FormatCode(EditorTab tab)
    {
        try
        {
            string? currentLanguage = GetTabLanguage(tab);
            string? formattedText = currentLanguage switch
            {
                "JSON" => FormatJson(tab.TextEditor.Text),
                "XAML" => FormatXaml(tab.TextEditor.Text),
                "C#" or "C++" or "JavaScript" or "Python" => FormatGenericCode(tab.TextEditor.Text, currentLanguage),
                _ => null
            };

            if (formattedText != null)
            {
                tab.TextEditor.Text = formattedText;
                tab.IsModified = true;
                UpdateTabHeader(tab);
                UpdateTitle();
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = $"已格式化 {currentLanguage} 代码";
                }
                else
                {
                    RightStatusText.Text = $"已格式化 {currentLanguage} 代码";
                }
            }
            else
            {
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = $"暂不支持格式化 {currentLanguage} 代码";
                }
                else
                {
                    RightStatusText.Text = $"暂不支持格式化 {currentLanguage} 代码";
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"格式化失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = "格式化失败";
            }
            else
            {
                RightStatusText.Text = "格式化失败";
            }
        }
    }

    private string? GetTabLanguage(EditorTab tab)
    {
        if (tab.TextEditor.SyntaxHighlighting == null)
            return null;

        string name = tab.TextEditor.SyntaxHighlighting.Name;
        return name switch
        {
            "C#" => "C#",
            "C++" => "C++",
            "JavaScript" => "JavaScript",
            "Python" => "Python",
            "JSON" => "JSON",
            "XAML" => "XAML",
            "YAML" => "YAML",
            _ => null
        };
    }

    /// <summary>
    /// 执行语法检查，返回检查结果
    /// </summary>
    private string PerformSyntaxCheck(EditorTab tab)
    {
        string? language = tab.Language;
        string text = tab.TextEditor.Text;

        if (string.IsNullOrEmpty(text))
        {
            return $"已设置为 {language} 语法高亮（空文件）";
        }

        try
        {
            var result = language switch
            {
                "JSON" => ValidateJson(text),
                "YAML" => ValidateYaml(text),
                "XAML" => $"已设置为 {language} 语法高亮（XAML 语法检查暂不支持）",
                _ => $"已设置为 {language} 语法高亮"
            };
            return result;
        }
        catch (Exception ex)
        {
            return $"语法检查失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 验证 JSON 语法
    /// </summary>
    private string ValidateJson(string json)
    {
        try
        {
            JsonConvert.DeserializeObject(json);
            return "JSON 语法检查通过";
        }
        catch (JsonException ex)
        {
            // 尝试从错误消息中提取行号和列号
            string lineInfo = "";
            var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"line\s*(\d+),\s*position\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                int lineNumber = int.Parse(match.Groups[1].Value);
                int linePosition = int.Parse(match.Groups[2].Value);
                lineInfo = $"（第 {lineNumber} 行，第 {linePosition} 列）";
            }
            return $"JSON 语法错误{lineInfo}: {ex.Message.Split(':')[0]}";
        }
    }

    /// <summary>
    /// 验证 YAML 语法（简化版）
    /// </summary>
    private string ValidateYaml(string yaml)
    {
        // YAML 语法检查需要第三方库，这里只做基本的缩进检查
        var lines = yaml.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart(' ');
            int currentIndent = line.Length - trimmed.Length;

            // 跳过空行和注释
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
            {
                continue;
            }

            // 检查缩进是否为 2 的倍数（YAML 推荐使用 2 空格缩进）
            if (currentIndent % 2 != 0 && currentIndent > 0)
            {
                return $"YAML 警告：第 {i + 1} 行缩进不是 2 的倍数";
            }
        }

        return "YAML 基本格式检查通过（缩进检查）";
    }

    private string FormatJson(string json)
    {
        var parsed = JsonConvert.DeserializeObject(json);
        return JsonConvert.SerializeObject(parsed, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });
    }

    private string FormatGenericCode(string code, string language)
    {
        var lines = code.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("}") || trimmed.StartsWith("]") || trimmed.StartsWith(")"))
            {
                indentLevel = Math.Max(0, indentLevel - 1);
            }

            if (trimmed.Length > 0)
            {
                formatted.Append(new string(' ', indentLevel * 4));
                formatted.AppendLine(trimmed);
            }
            else
            {
                formatted.AppendLine();
            }

            if (trimmed.EndsWith("{") || trimmed.EndsWith("[") || trimmed.EndsWith("(") ||
                trimmed.StartsWith("class ") || trimmed.StartsWith("function ") ||
                trimmed.StartsWith("def ") || trimmed.StartsWith("if ") ||
                trimmed.StartsWith("for ") || trimmed.StartsWith("while "))
            {
                indentLevel++;
            }
        }

        return formatted.ToString();
    }

    private string FormatXaml(string xaml)
    {
        var lines = xaml.Split('\n');
        var formatted = new StringBuilder();
        int indentLevel = 0;
        bool isInContent = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // 跳过空行和 XML 声明
            if (trimmed.Length == 0 || trimmed.StartsWith("<?xml"))
            {
                if (trimmed.Length > 0)
                {
                    formatted.AppendLine(trimmed);
                }
                continue;
            }

            // 检查是否是结束标签
            if (trimmed.StartsWith("</") || trimmed.StartsWith("/>"))
            {
                indentLevel = Math.Max(0, indentLevel - 1);
            }

            // 检查是否是自闭合标签
            if (trimmed.EndsWith("/>") || trimmed.StartsWith("</"))
            {
                // 自闭合或结束标签，不增加缩进
            }
            // 检查是否是开始标签
            else if (trimmed.StartsWith("<"))
            {
                isInContent = trimmed.Contains(">") && !trimmed.EndsWith(">");
            }

            // 添加缩进
            formatted.Append(new string(' ', indentLevel * 2));
            formatted.AppendLine(trimmed);

            // 更新缩进级别
            if (trimmed.StartsWith("<") && !trimmed.StartsWith("</") && !trimmed.EndsWith("/>") && !trimmed.Contains("/>"))
            {
                // 检查是否有闭合标签在同一行
                if (!trimmed.Contains(">") || trimmed.IndexOf('>') < trimmed.LastIndexOf('>'))
                {
                    indentLevel++;
                }
            }

            if (trimmed.StartsWith("</"))
            {
                indentLevel = Math.Max(0, indentLevel - 1);
            }
        }

        return formatted.ToString();
    }

    #endregion

    #region 视图操作

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        currentZoom = Math.Min(currentZoom + 0.1, 2.0);
        UpdateZoom();
        var currentTab = GetCurrentTab();
        if (currentTab != null && _tabs.Contains(currentTab))
        {
            LeftStatusText.Text = $"放大：{LeftZoomText.Text}";
        }
        else
        {
            RightStatusText.Text = $"放大：{RightZoomText.Text}";
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        currentZoom = Math.Max(currentZoom - 0.1, 0.5);
        UpdateZoom();
        var currentTab = GetCurrentTab();
        if (currentTab != null && _tabs.Contains(currentTab))
        {
            LeftStatusText.Text = $"缩小：{LeftZoomText.Text}";
        }
        else
        {
            RightStatusText.Text = $"缩小：{RightZoomText.Text}";
        }
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        currentZoom = 1.0;
        UpdateZoom();
        var currentTab = GetCurrentTab();
        if (currentTab != null && _tabs.Contains(currentTab))
        {
            LeftStatusText.Text = "缩放已重置";
        }
        else
        {
            RightStatusText.Text = "缩放已重置";
        }
    }

    private void UpdateZoom()
    {
        foreach (var tab in _tabs)
        {
            tab.TextEditor.FontSize = 14 * currentZoom;
        }
        foreach (var tab in _rightTabs)
        {
            tab.TextEditor.FontSize = 14 * currentZoom;
        }
        UpdateZoomDisplay();
        UpdateRightZoomDisplay();
    }

    private void UpdateZoomDisplay()
    {
        LeftZoomText.Text = $"{(int)(currentZoom * 100)}%";
    }

    #region 字体设置

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontFamilyComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            try
            {
                var fontFamily = new MediaFontFamily(item.Content.ToString());
                foreach (var tab in _tabs)
                {
                    tab.TextEditor.FontFamily = fontFamily;
                }
                foreach (var tab in _rightTabs)
                {
                    tab.TextEditor.FontFamily = fontFamily;
                }
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = $"字体已设置为：{item.Content}";
                }
                else
                {
                    RightStatusText.Text = $"字体已设置为：{item.Content}";
                }
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"设置字体失败：{ex.Message}", "错误", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            }
        }
    }

    private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontSizeComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            if (double.TryParse(item.Content.ToString(), out double fontSize))
            {
                foreach (var tab in _tabs)
                {
                    tab.TextEditor.FontSize = fontSize;
                }
                foreach (var tab in _rightTabs)
                {
                    tab.TextEditor.FontSize = fontSize;
                }
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = $"字号已设置为：{fontSize}";
                }
                else
                {
                    RightStatusText.Text = $"字号已设置为：{fontSize}";
                }
            }
        }
        else if (FontSizeComboBox.IsEditable && !string.IsNullOrEmpty(FontSizeComboBox.Text))
        {
            // 支持用户手动输入字号
            if (double.TryParse(FontSizeComboBox.Text, out double customFontSize))
            {
                foreach (var tab in _tabs)
                {
                    tab.TextEditor.FontSize = customFontSize;
                }
                foreach (var tab in _rightTabs)
                {
                    tab.TextEditor.FontSize = customFontSize;
                }
            }
        }
    }

    #endregion

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag != null)
        {
            var tab = GetCurrentTab();
            if (tab != null)
            {
                SetLanguage(menuItem.Tag.ToString(), tab);
            }
        }
    }

    private void SetLanguage(string? language, EditorTab tab)
    {
        if (string.IsNullOrEmpty(language) || language == "纯文本")
        {
            tab.TextEditor.SyntaxHighlighting = null;
            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = "已设置为纯文本";
            }
            else
            {
                RightStatusText.Text = "已设置为纯文本";
            }
            tab.Language = "纯文本";
            UpdateLanguageMenu("纯文本");
            return;
        }

        // 将显示名称映射到实际的语法高亮名称
        string highlightingName = language switch
        {
            "C/C++" => "C++",
            "C#" => "C#",
            "Python" => "Python",
            "JavaScript" => "JavaScript",
            "JSON" => "JSON",
            "XAML" => "XAML",
            "YAML" => "YAML",
            _ => language
        };

        var highlighting = HighlightingManager.Instance.GetDefinition(highlightingName);

        if (highlighting != null)
        {
            tab.TextEditor.SyntaxHighlighting = highlighting;
            tab.Language = language;

            // 设置完语法高亮后，对支持的語言执行语法检查
            var syntaxCheckResult = PerformSyntaxCheck(tab);

            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = syntaxCheckResult;
            }
            else
            {
                RightStatusText.Text = syntaxCheckResult;
            }
        }
        else
        {
            // 尝试直接使用语言名称
            highlighting = HighlightingManager.Instance.GetDefinition(language);
            if (highlighting != null)
            {
                tab.TextEditor.SyntaxHighlighting = highlighting;
                tab.Language = language;
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = $"已设置为 {language} 语法高亮";
                }
                else
                {
                    RightStatusText.Text = $"已设置为 {language} 语法高亮";
                }
            }
            else
            {
                tab.TextEditor.SyntaxHighlighting = null;
                tab.Language = null;
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = $"未找到 {language} 的高亮定义";
                }
                else
                {
                    RightStatusText.Text = $"未找到 {language} 的高亮定义";
                }
            }
        }

        UpdateLanguageMenu(language);
    }

    private void UpdateLanguageMenu(string? language)
    {
        // 更新语言菜单项的选中状态（略，保持原有逻辑）
    }

    private void SetLanguageByExtension(string extension, EditorTab tab)
    {
        string? language = extension switch
        {
            ".cs" => "C#",
            ".c" or ".cpp" or ".cc" => "C++",
            ".js" => "JavaScript",
            ".py" => "Python",
            ".json" => "JSON",
            ".xaml" or ".xaml.cs" => "XAML",
            ".md" or ".markdown" => "Markdown",
            ".yaml" or ".yml" => "YAML",
            _ => null
        };

        if (extension == ".json")
        {
            // JSON 文件自动应用语法高亮
            var highlighting = HighlightingManager.Instance.GetDefinition("JSON");
            if (highlighting != null)
            {
                tab.TextEditor.SyntaxHighlighting = highlighting;
                tab.Language = "JSON";
            }
        }
        else if (extension == ".md" || extension == ".markdown")
        {
            // Markdown 文件自动应用语法高亮
            var highlighting = HighlightingManager.Instance.GetDefinition("Markdown");
            System.Diagnostics.Debug.WriteLine($"Looking for Markdown highlighting, found: {highlighting?.Name ?? "null"}");
            if (highlighting != null)
            {
                tab.TextEditor.SyntaxHighlighting = highlighting;
                tab.Language = "Markdown";
                System.Diagnostics.Debug.WriteLine("Markdown highlighting applied!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Markdown highlighting NOT found!");
            }
        }
        else if (extension == ".yaml" || extension == ".yml")
        {
            // YAML 文件自动应用语法高亮
            var highlighting = HighlightingManager.Instance.GetDefinition("YAML");
            if (highlighting != null)
            {
                tab.TextEditor.SyntaxHighlighting = highlighting;
                tab.Language = "YAML";
                System.Diagnostics.Debug.WriteLine("YAML highlighting applied!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("YAML highlighting NOT found!");
            }
        }
        else if (language != null)
        {
            SetLanguage(language, tab);
        }
        else
        {
            tab.TextEditor.SyntaxHighlighting = null;
            tab.Language = null;
        }
    }

    private void Toolbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.IsChecked)
        {
            MainToolBar.Visibility = Visibility.Visible;
            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = "工具栏已显示";
            }
            else
            {
                RightStatusText.Text = "工具栏已显示";
            }
        }
        else
        {
            MainToolBar.Visibility = Visibility.Collapsed;
            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = "工具栏已隐藏";
            }
            else
            {
                RightStatusText.Text = "工具栏已隐藏";
            }
        }
    }

    private void Statusbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.IsChecked)
        {
            MainStatusBar.Visibility = Visibility.Visible;
            LeftStatusBar.Visibility = Visibility.Visible;
            RightStatusBar.Visibility = Visibility.Visible;
            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = "状态栏已显示";
            }
            else
            {
                RightStatusText.Text = "状态栏已显示";
            }
        }
        else
        {
            MainStatusBar.Visibility = Visibility.Collapsed;
            LeftStatusBar.Visibility = Visibility.Collapsed;
            RightStatusBar.Visibility = Visibility.Collapsed;
            var currentTab = GetCurrentTab();
            if (currentTab != null && _tabs.Contains(currentTab))
            {
                LeftStatusText.Text = "状态栏已隐藏";
            }
            else
            {
                RightStatusText.Text = "状态栏已隐藏";
            }
        }
    }

    #endregion

    #region 关于

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "TextFlow v1.0\n\n这是一个基于 C# 和 WPF 的多标签文本编辑器。\n\n功能包括：\n" +
            "- 多标签页同时编辑多个文件\n" +
            "- 文件操作（新建、打开、保存、另存为）\n" +
            "- 编辑功能（撤销、重做、剪切、复制、粘贴、查找、替换、格式化）\n" +
            "- 代码高亮支持（C/C++、C#、Python、JavaScript、JSON、XAML）\n" +
            "- 代码格式化（JSON、C#、C++、JavaScript、Python、XAML）\n" +
            "- 编码支持（ANSI、UTF-8、UTF-8 with BOM）\n" +
            "- 编码转换（ANSI 转 UTF-8、UTF-8 转 ANSI 等）\n" +
            "- 视图切换（工具栏、状态栏、缩放）\n" +
            "- 分栏对比模式（同时查看两个文件）\n\n" +
            "© 2026",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        var currentTab = GetCurrentTab();
        if (currentTab != null && _tabs.Contains(currentTab))
        {
            LeftStatusText.Text = "打开关于对话框";
        }
        else
        {
            RightStatusText.Text = "打开关于对话框";
        }
    }

    #endregion

    #region 分栏视图

    private void SplitView_Click(object sender, RoutedEventArgs e)
    {
        ToggleSplitView();
    }

    private void ToggleSplitView()
    {
        _isSplitView = !_isSplitView;

        if (_isSplitView)
        {
            // 启用分栏模式
            MainSplitter.Visibility = Visibility.Visible;
            RightPanel.Visibility = Visibility.Visible;
            RightStatusBar.Visibility = Visibility.Visible;
            LeftPanel.SetValue(Grid.ColumnSpanProperty, 1);

            // 如果右侧没有标签，创建一个
            if (_rightTabs.Count == 0)
            {
                CreateNewTab(true);
            }

            LeftStatusText.Text = "已启用分栏对比模式";
        }
        else
        {
            // 禁用分栏模式
            MainSplitter.Visibility = Visibility.Collapsed;
            RightPanel.Visibility = Visibility.Collapsed;
            RightStatusBar.Visibility = Visibility.Collapsed;
            LeftPanel.SetValue(Grid.ColumnSpanProperty, 3);

            LeftStatusText.Text = "已退出分栏对比模式";
        }
    }

    #endregion

    #region JSON 操作

    private void JsonFormat_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            try
            {
                string json = tab.TextEditor.Text;
                var parsed = JsonConvert.DeserializeObject(json);
                if (parsed != null)
                {
                    string formatted = JsonConvert.SerializeObject(parsed, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
                    tab.TextEditor.Text = formatted;
                    tab.IsModified = true;
                    UpdateTabHeader(tab);
                    UpdateTitle();
                    var currentTab = GetCurrentTab();
                    if (currentTab != null && _tabs.Contains(currentTab))
                    {
                        LeftStatusText.Text = "JSON 已格式化";
                    }
                    else
                    {
                        RightStatusText.Text = "JSON 已格式化";
                    }
                }
            }
            catch (JsonException ex)
            {
                // 尝试从错误消息中提取行号和列号
                string lineInfo = "";
                int lineNumber = -1;
                int linePosition = -1;

                // Newtonsoft.Json 的 JsonException 可能包含行号信息
                if (ex.Message.Contains("line"))
                {
                    // 尝试解析 "line X, position Y" 格式
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"line\s*(\d+),\s*position\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        lineNumber = int.Parse(match.Groups[1].Value);
                        linePosition = int.Parse(match.Groups[2].Value);
                        lineInfo = $"错误位置：第 {lineNumber} 行，第 {linePosition} 列，也可能在该行的上一行。";
                    }
                }

                string errorMsg = string.IsNullOrEmpty(lineInfo)
                    ? "JSON 格式错误，请检查语法。"
                    : lineInfo;
                MessageBox.Show(errorMsg, "JSON 格式化失败", MessageBoxButton.OK, MessageBoxImage.Error);
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = "JSON 格式错误";
                }
                else
                {
                    RightStatusText.Text = "JSON 格式错误";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"格式化失败：{ex.Message}", "JSON 格式化失败", MessageBoxButton.OK, MessageBoxImage.Error);
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = "JSON 格式化失败";
                }
                else
                {
                    RightStatusText.Text = "JSON 格式化失败";
                }
            }
        }
    }

    private void JsonMinify_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            try
            {
                string json = tab.TextEditor.Text;
                var parsed = JsonConvert.DeserializeObject(json);
                if (parsed != null)
                {
                    string minified = JsonConvert.SerializeObject(parsed, Newtonsoft.Json.Formatting.None);
                    tab.TextEditor.Text = minified;
                    tab.IsModified = true;
                    UpdateTabHeader(tab);
                    UpdateTitle();
                    var currentTab = GetCurrentTab();
                    if (currentTab != null && _tabs.Contains(currentTab))
                    {
                        LeftStatusText.Text = "JSON 已压缩";
                    }
                    else
                    {
                        RightStatusText.Text = "JSON 已压缩";
                    }
                }
            }
            catch (JsonException ex)
            {
                // 尝试从错误消息中提取行号和列号
                string lineInfo = "";
                int lineNumber = -1;
                int linePosition = -1;

                // Newtonsoft.Json 的 JsonException 可能包含行号信息
                if (ex.Message.Contains("line"))
                {
                    // 尝试解析 "line X, position Y" 格式
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"line\s*(\d+),\s*position\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        lineNumber = int.Parse(match.Groups[1].Value);
                        linePosition = int.Parse(match.Groups[2].Value);
                        lineInfo = $"错误位置：第 {lineNumber} 行，第 {linePosition} 列，也可能在该行的上一行。";
                    }
                }

                string errorMsg = string.IsNullOrEmpty(lineInfo)
                    ? "JSON 格式错误，请检查语法。"
                    : lineInfo;
                MessageBox.Show(errorMsg, "JSON 压缩失败", MessageBoxButton.OK, MessageBoxImage.Error);
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = "JSON 格式错误";
                }
                else
                {
                    RightStatusText.Text = "JSON 格式错误";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"压缩失败：{ex.Message}", "JSON 压缩失败", MessageBoxButton.OK, MessageBoxImage.Error);
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = "JSON 压缩失败";
                }
                else
                {
                    RightStatusText.Text = "JSON 压缩失败";
                }
            }
        }
    }

    private void JsonValidate_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetCurrentTab();
        if (tab != null)
        {
            try
            {
                string json = tab.TextEditor.Text;
                var parsed = JsonConvert.DeserializeObject(json);
                if (parsed != null)
                {
                    MessageBox.Show("JSON 格式正确！", "JSON 语法检查", MessageBoxButton.OK, MessageBoxImage.Information);
                    var currentTab = GetCurrentTab();
                    if (currentTab != null && _tabs.Contains(currentTab))
                    {
                        LeftStatusText.Text = "JSON 语法检查通过";
                    }
                    else
                    {
                        RightStatusText.Text = "JSON 语法检查通过";
                    }
                }
            }
            catch (JsonException ex)
            {
                // 尝试从错误消息中提取行号和列号
                string lineInfo = "";
                int lineNumber = -1;
                int linePosition = -1;

                // Newtonsoft.Json 的 JsonException 可能包含行号信息
                if (ex.Message.Contains("line"))
                {
                    // 尝试解析 "line X, position Y" 格式
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"line\s*(\d+),\s*position\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        lineNumber = int.Parse(match.Groups[1].Value);
                        linePosition = int.Parse(match.Groups[2].Value);
                        lineInfo = $"错误位置：第 {lineNumber} 行，第 {linePosition} 列，也可能在该行的上一行。";
                    }
                }

                string errorMsg = string.IsNullOrEmpty(lineInfo)
                    ? "JSON 格式错误，请检查语法。"
                    : lineInfo;
                MessageBox.Show(errorMsg, "JSON 语法检查失败", MessageBoxButton.OK, MessageBoxImage.Error);
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = "JSON 语法错误";
                }
                else
                {
                    RightStatusText.Text = "JSON 语法错误";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查失败：{ex.Message}", "JSON 语法检查失败", MessageBoxButton.OK, MessageBoxImage.Error);
                var currentTab = GetCurrentTab();
                if (currentTab != null && _tabs.Contains(currentTab))
                {
                    LeftStatusText.Text = "JSON 检查失败";
                }
                else
                {
                    RightStatusText.Text = "JSON 检查失败";
                }
            }
        }
    }

    #endregion

    #region 拖拽支持

    /// <summary>
    /// 初始化拖拽支持
    /// </summary>
    private void InitDragDrop()
    {
        // 允许拖拽文件到窗口
        this.AllowDrop = true;

        // 处理拖拽进入
        this.DragEnter += MainWindow_DragEnter;

        // 处理拖拽放下
        this.Drop += MainWindow_DragDrop;

        // 也为 TextEditor 添加拖拽支持
        AddHandler(TextEditor.PreviewDragEnterEvent, new DragEventHandler(TextEditor_PreviewDragEnter), true);
        AddHandler(TextEditor.PreviewDragOverEvent, new DragEventHandler(TextEditor_PreviewDragOver), true);
        AddHandler(TextEditor.PreviewDropEvent, new DragEventHandler(TextEditor_PreviewDrop), true);
    }

    private void MainWindow_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void MainWindow_DragDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string file in files)
            {
                if (File.Exists(file))
                {
                    OpenFile(file);
                }
            }
        }
    }

    private void TextEditor_PreviewDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void TextEditor_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void TextEditor_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string file in files)
            {
                if (File.Exists(file))
                {
                    OpenFile(file);
                }
            }
            e.Handled = true;
        }
    }

    #endregion

    #region 辅助方法

    private void LoadCustomJsonHighlighting()
    {
        try
        {
            // 检查是否已经有 JSON 高亮定义
            if (HighlightingManager.Instance.GetDefinition("JSON") == null)
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "TextFlow.Highlighting.JSON-Mode.xshd";
                using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        throw new InvalidOperationException($"找不到资源: {resourceName}");

                    using (var reader = new XmlTextReader(stream))
                    {
                        // 加载高亮定义
                        var highlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);

                        // 注册到管理器 - 使用 "JSON" 作为名称，以便与 SetLanguage 中的查找匹配
                        HighlightingManager.Instance.RegisterHighlighting(
                            "JSON",                // 名称
                            new[] { ".json" },     // 文件扩展名
                            highlighting
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 JSON 高亮定义失败：{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"堆栈跟踪：{ex.StackTrace}");
        }
    }

    private void LoadCustomXAMLHighlighting()
    {
        try
        {
            // 检查是否已经有 XAML 高亮定义
            if (HighlightingManager.Instance.GetDefinition("XAML") == null)
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "TextFlow.Highlighting.XAML-Mode.xshd";
                using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        throw new InvalidOperationException($"找不到资源: {resourceName}");

                    using (var reader = new XmlTextReader(stream))
                    {
                        // 加载高亮定义
                        var highlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);

                        // 注册到管理器 - 使用 "XAML" 作为名称，以便与 SetLanguage 中的查找匹配
                        HighlightingManager.Instance.RegisterHighlighting(
                            "XAML",                // 名称
                            new[] { ".xaml" },     // 文件扩展名
                            highlighting
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 XAML 高亮定义失败：{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"堆栈跟踪：{ex.StackTrace}");
        }
    }

    private void LoadCustomMarkdownHighlighting()
    {
        try
        {
            // 检查是否已经有 Markdown 高亮定义
            var existing = HighlightingManager.Instance.GetDefinition("Markdown");
            if (existing != null)
            {
                System.Diagnostics.Debug.WriteLine("Markdown highlighting already exists");
                return;
            }

            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "TextFlow.Highlighting.Markdown-Mode.xshd";

            Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                var allResources = assembly.GetManifestResourceNames();
                System.Diagnostics.Debug.WriteLine("All resources:");
                foreach (var r in allResources)
                {
                    System.Diagnostics.Debug.WriteLine($"  - {r}");
                }
                throw new InvalidOperationException($"找不到资源：{resourceName}");
            }

            System.Diagnostics.Debug.WriteLine($"Loading Markdown highlighting from {resourceName}, length: {stream.Length}");

            using (var reader = new XmlTextReader(stream))
            {
                // 加载高亮定义
                var highlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                System.Diagnostics.Debug.WriteLine($"Loaded highlighting: {highlighting.Name}");

                // 注册到管理器
                HighlightingManager.Instance.RegisterHighlighting(
                    "Markdown",
                    new[] { ".md", ".markdown" },
                    highlighting
                );
                System.Diagnostics.Debug.WriteLine("Markdown highlighting registered");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 Markdown 高亮定义失败：{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"堆栈跟踪：{ex.StackTrace}");
            WpfMessageBox.Show($"加载 Markdown 高亮失败：{ex.Message}", "错误", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    private void LoadCustomYamlHighlighting()
    {
        try
        {
            // 检查是否已经有 YAML 高亮定义
            var existing = HighlightingManager.Instance.GetDefinition("YAML");
            if (existing != null)
            {
                System.Diagnostics.Debug.WriteLine("YAML highlighting already exists");
                return;
            }

            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "TextFlow.Highlighting.YAML-Mode.xshd";

            Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                var allResources = assembly.GetManifestResourceNames();
                System.Diagnostics.Debug.WriteLine("All resources:");
                foreach (var r in allResources)
                {
                    System.Diagnostics.Debug.WriteLine($"  - {r}");
                }
                throw new InvalidOperationException($"找不到资源：{resourceName}");
            }

            System.Diagnostics.Debug.WriteLine($"Loading YAML highlighting from {resourceName}, length: {stream.Length}");

            using (var reader = new XmlTextReader(stream))
            {
                // 加载高亮定义
                var highlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                System.Diagnostics.Debug.WriteLine($"Loaded highlighting: {highlighting.Name}");

                // 注册到管理器
                HighlightingManager.Instance.RegisterHighlighting(
                    "YAML",
                    new[] { ".yaml", ".yml" },
                    highlighting
                );
                System.Diagnostics.Debug.WriteLine("YAML highlighting registered");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 YAML 高亮定义失败：{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"堆栈跟踪：{ex.StackTrace}");
            WpfMessageBox.Show($"加载 YAML 高亮失败：{ex.Message}", "错误", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ZoomReset_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.OemPlus && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ZoomIn_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.OemMinus && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ZoomOut_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Find_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CloseTab_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NewFile_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenFile_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }

    public void UpdateTitle()
    {
        var tab = GetCurrentTab();
        string fileName = tab?.FileName ?? "未命名";
        string modified = tab?.IsModified == true ? " *" : "";
        int totalTabs = _tabs.Count + _rightTabs.Count;
        string splitMode = _isSplitView ? " [分栏]" : "";
        Title = $"TextFlow{splitMode} - {fileName}{modified} ({totalTabs} 个标签)";
    }

    #endregion
}
