using System.Security.Cryptography;
using System.Text;

namespace Forgekeeper.Infrastructure.Services;

public static class SecretEncryption
{
    // Key derived from machine-specific data or environment variable
    private static byte[] GetKey()
    {
        var keySource = Environment.GetEnvironmentVariable("FORGEKEEPER_ENCRYPTION_KEY") 
            ?? "forgekeeper-default-key-change-me";
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(keySource));
    }

    public static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = GetKey();
        aes.GenerateIV();
        
        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
        
        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(result, 0);
        cipherBytes.CopyTo(result, aes.IV.Length);
        
        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string ciphertext)
    {
        var fullCipher = Convert.FromBase64String(ciphertext);
        
        using var aes = Aes.Create();
        aes.Key = GetKey();
        
        var iv = new byte[aes.BlockSize / 8];
        var cipher = new byte[fullCipher.Length - iv.Length];
        
        Array.Copy(fullCipher, iv, iv.Length);
        Array.Copy(fullCipher, iv.Length, cipher, 0, cipher.Length);
        
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        
        return Encoding.UTF8.GetString(plainBytes);
    }
}
