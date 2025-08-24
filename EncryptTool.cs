using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public static class EncryptTool
{
    public const string validationKey = "2a;ad.,&fSf^SX.,:12@D";
    // GameConf.modEncryPassword
    // Used for mod files
    public const string modEncryPassword = ",.?<aH.5:.L;_=-A%K/DF4s";
    // GameConf.cacheEncryPassword
    // Used for save files
    public const string cacheEncryPassword = "5:.A%KL;,.?<aH._=-/DF4s";

    // This is the header that's prepended to an encrypted file
    private static byte[] validationKeyXor3 => Encoding.UTF8.GetBytes(validationKey).Select(b => (byte)(b ^ 3)).ToArray();

    public static bool LooksEncrypted(ReadOnlySpan<byte> bytes)
    {
        var header = validationKeyXor3;
        if (bytes.Length < header.Length) return false;
        for (int i = 0; i < header.Length; i++)
            if (bytes[i] != header[i]) return false;
        return true;
    }

    public static byte[] EncryptMult(byte[] input, string key)
    {
        // If it already looks encrypted (has the header), return as-is
        if (LooksEncrypted(input)) return input;

        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(key));

        // Per-byte add with rolling key
        var outData = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            byte k = keyBytes[i % keyBytes.Length];
            outData[i] = unchecked((byte)(input[i] + k));
        }

        // Prepend header bytes (each marker char XOR 3)
        var marker = validationKeyXor3;
        var result = new byte[marker.Length + outData.Length];
        Buffer.BlockCopy(marker, 0, result, 0, marker.Length);
        Buffer.BlockCopy(outData, 0, result, marker.Length, outData.Length);
        return result;
    }

    public static byte[] DecryptMult(byte[] input, string key)
    {
        var header = validationKeyXor3;

        if (!LooksEncrypted(input))
        {
            return input;
        }

        // Skip header
        int offset = header.Length;

        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(key));

        var len = input.Length - offset;
        if (len < 0) len = 0;

        var outData = new byte[len];
        for (int i = 0; i < len; i++)
        {
            byte k = keyBytes[i % keyBytes.Length];
            outData[i] = unchecked((byte)(input[offset + i] - k));
        }
        return outData;
    }

    public static string EncryptDES(string plainText, string key)
    {
        var data = Encoding.UTF8.GetBytes(plainText);
        var enc = ProcessDES(data, key, encrypt: true);
        // Base64 + replace '/' => '@'
        return Convert.ToBase64String(enc).Replace('/', '@');
    }

    public static string DecryptDES(string encoded, string key)
    {
        // reverse replace '@' => '/'
        var data = Convert.FromBase64String(encoded.Replace('@', '/'));
        var dec = ProcessDES(data, key, encrypt: false);
        return Encoding.UTF8.GetString(dec);
    }

    private static byte[] ProcessDES(byte[] data, string key, bool encrypt)
    {
        using var des = System.Security.Cryptography.DES.Create();
        // Key derivation exactly as in IL: MD5(key), first 8 -> Key, next 8 -> IV
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        des.Key = hash.Take(8).ToArray();
        des.IV = hash.Skip(8).Take(8).ToArray();

        using var ms = new MemoryStream();
        using (var cs = new System.Security.Cryptography.CryptoStream(
            ms,
            encrypt ? des.CreateEncryptor() : des.CreateDecryptor(),
            System.Security.Cryptography.CryptoStreamMode.Write))
        {
            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();
        }
        return ms.ToArray();
    }
}
