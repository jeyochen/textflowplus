using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TextFlow;

public partial class FindDialog : Window
{
    private readonly MainWindow _mainWindow;
    private int _lastIndex = 0;

    public FindDialog(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        FindTextBox.Focus();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        string searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            MessageBox.Show("请输入查找内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor == null) return;

        string content = editor.Text;
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int index = content.IndexOf(searchText, _lastIndex, comparison);

        if (index == -1)
        {
            _lastIndex = 0;
            index = content.IndexOf(searchText, _lastIndex, comparison);

            if (index == -1)
            {
                MessageBox.Show($"未找到\"{searchText}\"", "查找", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        // 设置选中区域
        editor.Select(index, searchText.Length);

        // 将光标移动到选中区域的开始位置
        editor.CaretOffset = index;

        // 确保选中区域滚动到可视范围
        editor.ScrollToLine(editor.Document.GetLineByOffset(index).LineNumber);

        // 聚焦编辑器
        editor.Focus();

        _lastIndex = index + searchText.Length;

        _mainWindow.LeftStatusText.Text = $"已找到\"{searchText}\"在位置 {index}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FindTextBox_KeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
