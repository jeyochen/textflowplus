using System;
using System.Security.Cryptography;
using System.Text;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// SSH 保存密码的加密保护：磁盘上以密文存储，读取时解密。
/// 使用 AES-256（密钥/IV 由应用常量派生），兼容历史明文记录（无 enc: 前缀视为明文）。
/// </summary>
public static class PasswordProtector
{
    private const string Prefix = "enc:";
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("AIEditor::SSH::Password::Key::v1"));
    private static readonly byte[] IV = MD5.HashData(Encoding.UTF8.GetBytes("AIEditor::SSH::IV::v1"));

    public static string? Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;
            using var enc = aes.CreateEncryptor();
            var plain = Encoding.UTF8.GetBytes(plainText);
            var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            return plainText;
        }
    }

    public static string Decrypt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText ?? "";
        if (!cipherText.StartsWith(Prefix, StringComparison.Ordinal)) return cipherText; // 历史明文
        try
        {
            var data = Convert.FromBase64String(cipherText.Substring(Prefix.Length));
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(data, 0, data.Length);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return cipherText; // 无法解密时原样返回
        }
    }
}
