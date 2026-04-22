using System.Windows;
using System.Windows.Input;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TextFlow;

public partial class ReplaceDialog : Window
{
    private readonly MainWindow _mainWindow;
    private int _lastIndex = 0;

    public ReplaceDialog(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        FindTextBox.Focus();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        FindText();
    }

    private void FindText()
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

        editor.Select(index, searchText.Length);
        editor.Focus();
        _lastIndex = index + searchText.Length;

        _mainWindow.LeftStatusText.Text = $"已找到\"{searchText}\"在位置 {index}";
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        string searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            MessageBox.Show("请输入查找内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor == null) return;

        string selectedText = editor.SelectedText;
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (selectedText.Equals(searchText, comparison))
        {
            editor.SelectedText = ReplaceTextBox.Text;
            var tab = _mainWindow.GetCurrentTab();
            if (tab != null)
            {
                tab.IsModified = true;
                _mainWindow.UpdateTabHeaderPublic(tab);
                _mainWindow.UpdateTitle();
            }
            _mainWindow.LeftStatusText.Text = "已替换";
        }

        FindText();
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
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
        string replaceText = ReplaceTextBox.Text;
        StringComparison comparison = MatchCaseCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int count = 0;
        int index = content.IndexOf(searchText, 0, comparison);

        while (index != -1)
        {
            content = content.Remove(index, searchText.Length).Insert(index, replaceText);
            count++;
            index = content.IndexOf(searchText, index + replaceText.Length, comparison);
        }

        if (count > 0)
        {
            editor.Text = content;
            var tab = _mainWindow.GetCurrentTab();
            if (tab != null)
            {
                tab.IsModified = true;
                _mainWindow.UpdateTabHeaderPublic(tab);
                _mainWindow.UpdateTitle();
            }
            _mainWindow.LeftStatusText.Text = $"全部替换完成，共替换 {count} 处";
            MessageBox.Show($"全部替换完成，共替换 {count} 处", "替换", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"未找到\"{searchText}\"", "查找", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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

    private void ReplaceTextBox_KeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Replace_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
