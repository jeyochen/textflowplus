using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TextFlowPlus.Views;

public partial class ReplaceDialog : Window
{
    private readonly MainWindow _mainWindow;
    private int _lastIndex;

    public ReplaceDialog() { }

    public ReplaceDialog(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        FindTextBox.KeyDown += FindTextBox_KeyDown;
        ReplaceTextBox.KeyDown += ReplaceTextBox_KeyDown;
    }

    /// <summary>弹出后把输入光标放到第一个输入框（查找内容）：构造期间控件还未接入可视树，那时 Focus() 不生效。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FindTextBox?.Focus();
    }

    private void FindTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void ReplaceTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Replace_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e)
    {
        FindText();
    }

    private void FindText()
    {
        string searchText = FindTextBox.Text ?? "";
        if (string.IsNullOrEmpty(searchText))
        {
            _mainWindow.StatusText.Text = "请输入查找内容";
            return;
        }

        var comparison = (MatchCaseCheckBox.IsChecked == true)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // 富文本模式：全文快照查找后选中
        if (_mainWindow.IsCurrentRichTextTab)
        {
            string content = _mainWindow.GetCurrentRichTextContent() ?? "";
            int index = content.IndexOf(searchText, _lastIndex, comparison);

            if (index == -1)
            {
                _lastIndex = 0;
                index = content.IndexOf(searchText, _lastIndex, comparison);

                if (index == -1)
                {
                    _mainWindow.StatusText.Text = $"未找到\"{searchText}\"";
                    return;
                }
            }

            _mainWindow.SelectRichTextRange(index, searchText.Length);
            _lastIndex = index + searchText.Length;
            _mainWindow.StatusText.Text = $"已找到\"{searchText}\"在位置 {index}";
            return;
        }

        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor == null) return;

        string contentText = editor.Text ?? "";
        int textIndex = contentText.IndexOf(searchText, _lastIndex, comparison);

        if (textIndex == -1)
        {
            _lastIndex = 0;
            textIndex = contentText.IndexOf(searchText, _lastIndex, comparison);

            if (textIndex == -1)
            {
                _mainWindow.StatusText.Text = $"未找到\"{searchText}\"";
                return;
            }
        }

        editor.Select(textIndex, searchText.Length);
        editor.Focus();
        _lastIndex = textIndex + searchText.Length;

        _mainWindow.StatusText.Text = $"已找到\"{searchText}\"在位置 {textIndex}";
    }

    private void Replace_Click(object? sender, RoutedEventArgs e)
    {
        string searchText = FindTextBox.Text ?? "";
        if (string.IsNullOrEmpty(searchText))
        {
            _mainWindow.StatusText.Text = "请输入查找内容";
            return;
        }

        var comparison = (MatchCaseCheckBox.IsChecked == true)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // 富文本模式：若当前选中区就是查找词则替换，然后继续查找
        if (_mainWindow.IsCurrentRichTextTab)
        {
            string selectedText = _mainWindow.GetCurrentRichTextSelectionText();
            if (selectedText.Equals(searchText, comparison))
            {
                if (_mainWindow.ReplaceCurrentRichTextSelection(ReplaceTextBox.Text ?? ""))
                    _mainWindow.StatusText.Text = "已替换";
            }

            // 替换会改变文档偏移，重置游标避免跳过内容
            _lastIndex = 0;
            FindText();
            return;
        }

        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor == null) return;

        string editorSelectedText = editor.SelectedText ?? "";
        if (editorSelectedText.Equals(searchText, comparison))
        {
            editor.SelectedText = ReplaceTextBox.Text ?? "";
            var tab = _mainWindow.GetCurrentTab();
            if (tab != null)
            {
                tab.IsModified = true;
                _mainWindow.UpdateTabHeaderPublic(tab);
                _mainWindow.UpdateTitle();
            }
            _mainWindow.StatusText.Text = "已替换";
        }

        FindText();
    }

    private void ReplaceAll_Click(object? sender, RoutedEventArgs e)
    {
        string searchText = FindTextBox.Text ?? "";
        if (string.IsNullOrEmpty(searchText))
        {
            _mainWindow.StatusText.Text = "请输入查找内容";
            return;
        }

        string replaceText = ReplaceTextBox.Text ?? "";
        var comparison = (MatchCaseCheckBox.IsChecked == true)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // 富文本模式：循环"定位→替换"，每次替换后刷新全文快照
        if (_mainWindow.IsCurrentRichTextTab)
        {
            int count = 0;
            int searchFrom = 0;
            while (true)
            {
                string richContent = _mainWindow.GetCurrentRichTextContent() ?? "";
                int foundIndex = richContent.IndexOf(searchText, searchFrom, comparison);
                if (foundIndex == -1) break;
                if (!_mainWindow.ReplaceRichTextRange(foundIndex, searchText.Length, replaceText)) break;  // 替换失败（文档异常）时停止，避免死循环
                count++;
                searchFrom = foundIndex + replaceText.Length;   // 跳过刚替换的区域（与文本模式一致）
            }

            _mainWindow.StatusText.Text = count > 0
                ? $"全部替换完成，共替换 {count} 处"
                : $"未找到\"{searchText}\"";
            _lastIndex = 0;
            return;
        }

        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor == null) return;

        string content = editor.Text ?? "";
        int replaceCount = 0;
        int index = content.IndexOf(searchText, 0, comparison);

        while (index != -1)
        {
            content = content.Remove(index, searchText.Length).Insert(index, replaceText);
            replaceCount++;
            index = content.IndexOf(searchText, index + replaceText.Length, comparison);
        }

        if (replaceCount > 0)
        {
            editor.Text = content;
            var tab = _mainWindow.GetCurrentTab();
            if (tab != null)
            {
                tab.IsModified = true;
                _mainWindow.UpdateTabHeaderPublic(tab);
                _mainWindow.UpdateTitle();
            }
            _mainWindow.StatusText.Text = $"全部替换完成，共替换 {replaceCount} 处";
        }
        else
        {
            _mainWindow.StatusText.Text = $"未找到\"{searchText}\"";
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
