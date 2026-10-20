using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

/// <summary>
/// 信息摘要计算工具（UserControl，可嵌入主窗口标签页）。
/// 由 HashCalcDialog 改造而来：不再以独立窗口弹出，而是作为标签页内容承载。
/// </summary>
public partial class HashCalcView : UserControl, ICloseRequested
{
    private bool _isFileMode = true;
    private string _selectedAlgorithm = "MD5";

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;

    /// <summary>视图内“关闭”按钮触发：请求主窗口关闭本标签页。</summary>
    public event Action? CloseRequested;

    public HashCalcView()
    {
        InitializeComponent();

        // 绑定事件
        FileModeRadio.IsCheckedChanged += FileModeRadio_CheckedChanged;
        TextModeRadio.IsCheckedChanged += TextModeRadio_CheckedChanged;
        BrowseButton.Click += BrowseButton_Click;
        CalculateButton.Click += CalculateButton_Click;
        CloseButton.Click += (s, e) => CloseRequested?.Invoke();
        AlgorithmComboBox.SelectionChanged += AlgorithmComboBox_SelectionChanged;

        UpdatePanelVisibility();
        LangService.LanguageChanged += ApplyTexts;
        ApplyTexts();
    }

    /// <summary>按当前界面语言刷新本面板文本。</summary>
    public void ApplyTexts()
    {
        FileModeRadio.Content = LangService.T("文件模式");
        TextModeRadio.Content = LangService.T("文本模式");
        AlgorithmLabelText.Text = LangService.T("选择算法：");
        FilePathTextBox.PlaceholderText = LangService.T("选择要计算信息摘要的文件...");
        BrowseButton.Content = LangService.T("浏览...");
        TextInputBox.PlaceholderText = LangService.T("输入要计算信息摘要的文本...");
        CalculateButton.Content = LangService.T("计算");
        ResultLabelText.Text = LangService.T("计算结果：");
        ResultTextBox.PlaceholderText = LangService.T("计算结果将显示在这里...");
        CloseButton.Content = LangService.T("关闭");
    }
    private void AlgorithmComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AlgorithmComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            _selectedAlgorithm = item.Content.ToString()!;
        }
    }

    private void FileModeRadio_CheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (FileModeRadio.IsChecked == true)
        {
            _isFileMode = true;
            UpdatePanelVisibility();
        }
    }

    private void TextModeRadio_CheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (TextModeRadio.IsChecked == true)
        {
            _isFileMode = false;
            UpdatePanelVisibility();
        }
    }

    private void UpdatePanelVisibility()
    {
        FileSelectPanel.IsVisible = _isFileMode;
        TextInputBox.IsVisible = !_isFileMode;
        // 标签页高度由主窗口布局决定，不再调整窗口高度
    }

    private void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        _dialogTask = BrowseFileAsync();
    }

    private async Task BrowseFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangService.T("选择文件"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(LangService.T("所有文件")) { Patterns = new[] { "*.*" } }
            }
        });

        if (files != null && files.Count > 0)
        {
            FilePathTextBox.Text = files[0].Path.LocalPath;
        }
    }

    private void CalculateButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_isFileMode)
            {
                CalculateFileHash();
            }
            else
            {
                CalculateTextHash();
            }
        }
        catch (Exception ex)
        {
            ResultTextBox.Text = LangService.TFormat("错误：{0}", ex.Message);
        }
    }

    private void CalculateFileHash()
    {
        var filePath = FilePathTextBox.Text?.Trim();

        if (string.IsNullOrEmpty(filePath))
        {
            ResultTextBox.Text = LangService.T("请先选择要计算的文件");
            return;
        }

        if (!File.Exists(filePath))
        {
            ResultTextBox.Text = LangService.T("文件不存在");
            return;
        }

        using var stream = File.OpenRead(filePath);
        var hashString = ComputeHash(stream, _selectedAlgorithm);
        ResultTextBox.Text = hashString;
    }

    private void CalculateTextHash()
    {
        var text = TextInputBox.Text ?? "";
        var bytes = Encoding.UTF8.GetBytes(text);

        using var stream = new MemoryStream(bytes);
        var hashString = ComputeHash(stream, _selectedAlgorithm);
        ResultTextBox.Text = hashString;
    }

    private static string ComputeHash(Stream stream, string algorithm)
    {
        byte[] hashBytes = algorithm switch
        {
            "MD5" => MD5.Create().ComputeHash(stream),
            "SHA-1" => SHA1.Create().ComputeHash(stream),
            "SHA-2" => SHA256.Create().ComputeHash(stream),
            "SHA-256" => SHA256.Create().ComputeHash(stream),
            "SHA-512" => SHA512.Create().ComputeHash(stream),
            "SHA3-256" => System.Security.Cryptography.SHA3_256.Create().ComputeHash(stream),
            "SHA3-512" => System.Security.Cryptography.SHA3_512.Create().ComputeHash(stream),
            "BLAKE2s" => ComputeBlake2s(stream),
            "BLAKE2b" => ComputeBlake2b(stream),
            "SM3" => ComputeSm3(stream),
            "CRC" => Crc32.Compute(stream),
            "PBKDF2" => ComputePbkdf2(stream),
            "bcrypt" => ComputeBcrypt(stream),
            "Scrypt" => ComputeScrypt(stream),
            "Argon2" => ComputeArgon2(stream),
            _ => throw new ArgumentException(LangService.TFormat("不支持的算法：{0}", algorithm))
        };

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
    }

    private static byte[] ComputeBlake2s(Stream stream)
    {
        var digest = new Blake2sDigest(256);
        return ComputeBouncyCastleHash(digest, stream);
    }

    private static byte[] ComputeBlake2b(Stream stream)
    {
        var digest = new Blake2bDigest(512);
        return ComputeBouncyCastleHash(digest, stream);
    }

    private static byte[] ComputeSm3(Stream stream)
    {
        var digest = new SM3Digest();
        return ComputeBouncyCastleHash(digest, stream);
    }

    private static byte[] ComputeBouncyCastleHash(IDigest digest, Stream stream)
    {
        byte[] buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        byte[] result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return result;
    }

    private static byte[] ComputePbkdf2(Stream stream)
    {
        // PBKDF2 需要密码和盐，这里使用默认值进行演示
        byte[] inputData = ReadStreamToBytes(stream);
        byte[] salt = Encoding.UTF8.GetBytes("AIEditorSalt");
        byte[] password = Encoding.UTF8.GetBytes("AIEditorPassword");

        // 使用 Rfc2898DeriveBytes (.NET 内置 PBKDF2)
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static byte[] ComputeBcrypt(Stream stream)
    {
        // bcrypt 主要用于密码哈希，这里返回输入数据的哈希
        byte[] inputData = ReadStreamToBytes(stream);
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(inputData);
    }

    private static byte[] ComputeScrypt(Stream stream)
    {
        byte[] inputData = ReadStreamToBytes(stream);
        byte[] salt = Encoding.UTF8.GetBytes("AIEditorSalt");
        byte[] password = Encoding.UTF8.GetBytes("AIEditorPassword");

        byte[] key = Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(password, salt, 16384, 8, 1, 32);
        return key;
    }

    private static byte[] ComputeArgon2(Stream stream)
    {
        // Argon2 需要专用库，这里使用 SHA256 作为占位符
        byte[] inputData = ReadStreamToBytes(stream);
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(inputData);
    }

    private static byte[] ReadStreamToBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
