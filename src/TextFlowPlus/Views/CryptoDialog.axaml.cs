using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TextFlowPlus.Views
{
    public partial class CryptoDialog : Window
    {
        // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
        private Task? _dialogTask;

        public CryptoDialog()
        {
            InitializeComponent();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
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

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string filePath = FilePathTextBox.Text.Trim();
                string key = KeyTextBox.Text.Trim();
                string algorithm = (AlgorithmComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "AES";
                bool isEncrypt = EncryptRadio.IsChecked == true;

                if (string.IsNullOrEmpty(filePath))
                {
                    StatusTextBlock.Text = "请选择文件";
                    StatusTextBlock.Foreground = Avalonia.Media.Brushes.Red;
                    return;
                }

                if (string.IsNullOrEmpty(key))
                {
                    StatusTextBlock.Text = "请输入密钥";
                    StatusTextBlock.Foreground = Avalonia.Media.Brushes.Red;
                    return;
                }

                if (!File.Exists(filePath))
                {
                    StatusTextBlock.Text = "文件不存在";
                    StatusTextBlock.Foreground = Avalonia.Media.Brushes.Red;
                    return;
                }

                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                
                // 根据算法调整密钥长度（自动补齐或截断）
                keyBytes = AdjustKeyLength(keyBytes, algorithm);

                // 读取文件内容
                byte[] inputBytes = File.ReadAllBytes(filePath);
                byte[] outputBytes;

                if (isEncrypt)
                {
                    outputBytes = Encrypt(inputBytes, keyBytes, algorithm);
                }
                else
                {
                    outputBytes = Decrypt(inputBytes, keyBytes, algorithm);
                }

                // 写入原文件
                File.WriteAllBytes(filePath, outputBytes);

                StatusTextBlock.Text = isEncrypt ? "加密完成" : "解密完成";
                StatusTextBlock.Foreground = Avalonia.Media.Brushes.Green;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"操作失败: {ex.Message}";
                StatusTextBlock.Foreground = Avalonia.Media.Brushes.Red;
            }
        }

        private byte[] AdjustKeyLength(byte[] key, string algorithm)
        {
            int targetLength = GetTargetKeyLength(algorithm);
            byte[] adjustedKey = new byte[targetLength];
            
            // 确保密钥长度至少为1，避免除以零
            if (key.Length == 0)
            {
                key = new byte[] { 0x00 };
            }
            
            for (int i = 0; i < targetLength; i++)
            {
                adjustedKey[i] = key[i % key.Length];
            }
            
            return adjustedKey;
        }

        private int GetTargetKeyLength(string algorithm)
        {
            switch (algorithm.ToUpper())
            {
                case "AES":
                    return 32; // 使用256位密钥
                case "CHACHA20":
                    return 32; // 256位密钥
                case "DES":
                    return 8;  // 64位密钥
                case "3DES":
                    return 24; // 192位密钥
                default:
                    return 16; // 默认128位
            }
        }

        private byte[] Encrypt(byte[] data, byte[] key, string algorithm)
        {
            switch (algorithm.ToUpper())
            {
                case "AES":
                    return AesEncrypt(data, key);
                case "CHACHA20":
                    return ChaCha20Encrypt(data, key);
                case "DES":
                    return DesEncrypt(data, key);
                case "3DES":
                    return TripleDesEncrypt(data, key);
                default:
                    throw new NotSupportedException($"不支持的算法: {algorithm}");
            }
        }

        private byte[] Decrypt(byte[] data, byte[] key, string algorithm)
        {
            switch (algorithm.ToUpper())
            {
                case "AES":
                    return AesDecrypt(data, key);
                case "CHACHA20":
                    return ChaCha20Decrypt(data, key);
                case "DES":
                    return DesDecrypt(data, key);
                case "3DES":
                    return TripleDesDecrypt(data, key);
                default:
                    throw new NotSupportedException($"不支持的算法: {algorithm}");
            }
        }

        #region AES
        private byte[] AesEncrypt(byte[] data, byte[] key)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (MemoryStream ms = new MemoryStream())
                {
                    ms.Write(aes.IV, 0, aes.IV.Length);
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, 0, data.Length);
                        cs.FlushFinalBlock();
                    }
                    return ms.ToArray();
                }
            }
        }

        private byte[] AesDecrypt(byte[] data, byte[] key)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                byte[] iv = new byte[16];
                Array.Copy(data, 0, iv, 0, iv.Length);
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, iv.Length, data.Length - iv.Length);
                        cs.FlushFinalBlock();
                    }
                    return ms.ToArray();
                }
            }
        }
        #endregion

        #region DES
        private byte[] DesEncrypt(byte[] data, byte[] key)
        {
            using (DES des = DES.Create())
            {
                des.Key = key;
                des.GenerateIV();
                des.Mode = CipherMode.CBC;
                des.Padding = PaddingMode.None;

                // 使用空格补齐数据到8字节的倍数
                int blockSize = 8;
                int paddingLength = blockSize - (data.Length % blockSize);
                if (paddingLength != blockSize)
                {
                    byte[] paddedData = new byte[data.Length + paddingLength];
                    Buffer.BlockCopy(data, 0, paddedData, 0, data.Length);
                    for (int i = data.Length; i < paddedData.Length; i++)
                    {
                        paddedData[i] = (byte)' ';
                    }
                    data = paddedData;
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    ms.Write(des.IV, 0, des.IV.Length);
                    using (CryptoStream cs = new CryptoStream(ms, des.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, 0, data.Length);
                        cs.FlushFinalBlock();
                    }
                    return ms.ToArray();
                }
            }
        }

        private byte[] DesDecrypt(byte[] data, byte[] key)
        {
            using (DES des = DES.Create())
            {
                des.Key = key;
                byte[] iv = new byte[8];
                Array.Copy(data, 0, iv, 0, iv.Length);
                des.IV = iv;
                des.Mode = CipherMode.CBC;
                des.Padding = PaddingMode.None;

                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, des.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, iv.Length, data.Length - iv.Length);
                        cs.FlushFinalBlock();
                    }
                    // 移除空格填充
                    byte[] result = ms.ToArray();
                    return TrimTrailingSpaces(result);
                }
            }
        }
        #endregion

        #region 3DES
        private byte[] TripleDesEncrypt(byte[] data, byte[] key)
        {
            // 使用空格补齐数据到8字节的倍数（在创建加密器之前执行）
            int blockSize = 8;
            int paddingLength = blockSize - (data.Length % blockSize);
            if (paddingLength != blockSize)
            {
                byte[] paddedData = new byte[data.Length + paddingLength];
                Buffer.BlockCopy(data, 0, paddedData, 0, data.Length);
                for (int i = data.Length; i < paddedData.Length; i++)
                {
                    paddedData[i] = (byte)' ';
                }
                data = paddedData;
            }

            using (TripleDES des = TripleDES.Create())
            {
                des.Key = key;
                des.GenerateIV();
                des.Mode = CipherMode.CBC;
                des.Padding = PaddingMode.None;

                using (MemoryStream ms = new MemoryStream())
                {
                    ms.Write(des.IV, 0, des.IV.Length);
                    using (CryptoStream cs = new CryptoStream(ms, des.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, 0, data.Length);
                        cs.FlushFinalBlock();
                    }
                    return ms.ToArray();
                }
            }
        }

        private byte[] TripleDesDecrypt(byte[] data, byte[] key)
        {
            using (TripleDES des = TripleDES.Create())
            {
                des.Key = key;
                byte[] iv = new byte[8];
                Array.Copy(data, 0, iv, 0, iv.Length);
                des.IV = iv;
                des.Mode = CipherMode.CBC;
                des.Padding = PaddingMode.None;

                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, des.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, iv.Length, data.Length - iv.Length);
                        cs.FlushFinalBlock();
                    }
                    // 移除空格填充
                    byte[] result = ms.ToArray();
                    return TrimTrailingSpaces(result);
                }
            }
        }
        #endregion

        /// <summary>
        /// 移除尾部空格
        /// </summary>
        private byte[] TrimTrailingSpaces(byte[] data)
        {
            int lastNonSpace = data.Length - 1;
            while (lastNonSpace >= 0 && data[lastNonSpace] == ' ')
            {
                lastNonSpace--;
            }
            if (lastNonSpace < 0)
            {
                return new byte[0];
            }
            byte[] result = new byte[lastNonSpace + 1];
            Buffer.BlockCopy(data, 0, result, 0, result.Length);
            return result;
        }

        #region ChaCha20
        private byte[] ChaCha20Encrypt(byte[] data, byte[] key)
        {
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            using (var chaCha20 = new ChaCha20Poly1305(key))
            {
                byte[] tag = new byte[16];
                byte[] encrypted = new byte[data.Length];
                chaCha20.Encrypt(nonce.AsSpan(), data.AsSpan(), encrypted.AsSpan(), tag.AsSpan(), null);
                
                byte[] result = new byte[nonce.Length + encrypted.Length + tag.Length];
                nonce.CopyTo(result, 0);
                encrypted.CopyTo(result, nonce.Length);
                tag.CopyTo(result, nonce.Length + encrypted.Length);
                return result;
            }
        }

        private byte[] ChaCha20Decrypt(byte[] data, byte[] key)
        {
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            Array.Copy(data, 0, nonce, 0, nonce.Length);
            Array.Copy(data, data.Length - tag.Length, tag, 0, tag.Length);
            
            byte[] encryptedData = new byte[data.Length - nonce.Length - tag.Length];
            Array.Copy(data, nonce.Length, encryptedData, 0, encryptedData.Length);

            using (var chaCha20 = new ChaCha20Poly1305(key))
            {
                byte[] decrypted = new byte[encryptedData.Length];
                chaCha20.Decrypt(nonce.AsSpan(), encryptedData.AsSpan(), tag.AsSpan(), decrypted.AsSpan(), null);
                return decrypted;
            }
        }
        #endregion

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}