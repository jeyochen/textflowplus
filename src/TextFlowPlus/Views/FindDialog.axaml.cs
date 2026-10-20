using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TextFlowPlus.Views;

public partial class FindDialog : Window
{
    private readonly MainWindow _mainWindow;
    private int _lastIndex;

    public FindDialog() { }

    public FindDialog(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        FindTextBox.KeyDown += FindTextBox_KeyDown;
    }

    /// <summary>弹出后把输入光标放到查找框：构造期间控件还未接入可视树，那时 Focus() 不生效（焦点为空）。</summary>
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

    private void FindNext_Click(object? sender, RoutedEventArgs e)
    {
        FindNext();
    }

    /// <summary>查找下一个。富文本模式下基于纯文本快照定位，再以选区形式高亮（F3 也可触达）。</summary>
    public void FindNext()
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

        // 富文本模式：文档在 RichTextEditor 中，用全文快照查找后选中
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
        editor.CaretOffset = textIndex;
        editor.ScrollToLine(editor.Document.GetLineByOffset(textIndex).LineNumber);
        editor.Focus();
        _lastIndex = textIndex + searchText.Length;

        _mainWindow.StatusText.Text = $"已找到\"{searchText}\"在位置 {textIndex}";
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
