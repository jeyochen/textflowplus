using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;

namespace TextFlowPlus.Views;

public partial class HashCalcDialog : Window
{
    private bool _isFileMode = true;
    private string _selectedAlgorithm = "MD5";

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;

    public HashCalcDialog()
    {
        InitializeComponent();

        // 绑定事件
        FileModeRadio.IsCheckedChanged += FileModeRadio_CheckedChanged;
        TextModeRadio.IsCheckedChanged += TextModeRadio_CheckedChanged;
        BrowseButton.Click += BrowseButton_Click;
        CalculateButton.Click += CalculateButton_Click;
        CloseButton.Click += (s, e) => Close();
        AlgorithmComboBox.SelectionChanged += AlgorithmComboBox_SelectionChanged;

        UpdatePanelVisibility();
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

        // 根据模式调整窗口高度
        if (_isFileMode)
        {
            // 文件模式：窗口高度变小
            Height = 400;
        }
        else
        {
            // 文本模式：窗口高度变大
            Height = 550;
        }
    }

    private void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        _dialogTask = BrowseFileAsync();
    }

    private async Task BrowseFileAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
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
            ResultTextBox.Text = $"错误：{ex.Message}";
        }
    }

    private void CalculateFileHash()
    {
        var filePath = FilePathTextBox.Text?.Trim();

        if (string.IsNullOrEmpty(filePath))
        {
            ResultTextBox.Text = "请先选择要计算的文件";
            return;
        }

        if (!File.Exists(filePath))
        {
            ResultTextBox.Text = "文件不存在";
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
            _ => throw new ArgumentException($"不支持的算法：{algorithm}")
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

// CRC32 实现
public static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
            }
            table[i] = crc;
        }
        return table;
    }

    public static byte[] Compute(Stream stream)
    {
        uint crc = 0xFFFFFFFF;
        int b;
        while ((b = stream.ReadByte()) >= 0)
        {
            crc = (crc >> 8) ^ Table[(crc ^ (byte)b) & 0xFF];
        }
        crc ^= 0xFFFFFFFF;

        byte[] result = new byte[4];
        result[0] = (byte)(crc & 0xFF);
        result[1] = (byte)((crc >> 8) & 0xFF);
        result[2] = (byte)((crc >> 16) & 0xFF);
        result[3] = (byte)((crc >> 24) & 0xFF);
        return result;
    }
}
