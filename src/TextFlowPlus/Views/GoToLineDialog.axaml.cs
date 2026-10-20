using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TextFlowPlus.Views;

public partial class GoToLineDialog : Window
{
    private readonly MainWindow _mainWindow;

    public GoToLineDialog() { }

    public GoToLineDialog(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        LineNumberTextBox.KeyDown += LineNumberTextBox_KeyDown;
        LineNumberTextBox.TextInput += LineNumberTextBox_TextInput;
        
        // 显示当前行数信息
        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor != null)
        {
            Title = $"跳转到行 (共 {editor.Document.LineCount} 行)";
        }
        
        // 在窗口加载完成后设置焦点
        Loaded += (_, __) => LineNumberTextBox.Focus();
    }

    private void LineNumberTextBox_TextInput(object? sender, TextInputEventArgs e)
    {
        // 只允许输入数字
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void LineNumberTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoTo_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void GoTo_Click(object? sender, RoutedEventArgs e)
    {
        string input = LineNumberTextBox.Text ?? "";
        if (!int.TryParse(input, out int lineNumber))
        {
            _mainWindow.StatusText.Text = "请输入有效的行号";
            return;
        }

        var editor = _mainWindow.GetCurrentTextEditor();
        if (editor == null)
        {
            Close();
            return;
        }

        int totalLines = editor.Document.LineCount;
        
        // 确保行号在有效范围内
        if (lineNumber < 1)
            lineNumber = 1;
        if (lineNumber > totalLines)
            lineNumber = totalLines;

        // 跳转到指定行
        editor.CaretOffset = editor.Document.GetLineByNumber(lineNumber).Offset;
        editor.ScrollToLine(lineNumber);
        editor.Focus();
        
        _mainWindow.StatusText.Text = $"已跳转到第 {lineNumber} 行";
        
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}