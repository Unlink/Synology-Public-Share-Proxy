using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareProxy;

// DSM's captured wire protocol: RSA PKCS#1 v1.5 + CryptoJS/OpenSSL salted AES passphrase format.
// This is only envelope compatibility. HTTPS with full certificate validation is mandatory.
public static class DsmEncryption
{
    public static string Encrypt(JsonElement info, string token, string password)
    {
        if (info.GetProperty("cipherkey").GetString() != "__cIpHeRtExT") throw new ProxyException(502, "protocol_error");
        var modulus = info.GetProperty("public_key").GetString() ?? "";
        var timeKey = info.GetProperty("ciphertoken").GetString() ?? "";
        if (modulus.Length != 1024 || timeKey.Length is < 1 or > 64 || !timeKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ProxyException(502, "protocol_error");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(251))[..501];
        var plaintext = string.Join('&', new Dictionary<string, string>
        {
            [timeKey] = info.GetProperty("server_time").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["password"] = JsonSerializer.Serialize(password),
            ["sharing_id"] = JsonSerializer.Serialize(token)
        }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromHexString(modulus), Exponent = [1, 0, 1] });
        var rsaEnvelope = rsa.Encrypt(Encoding.ASCII.GetBytes(secret), RSAEncryptionPadding.Pkcs1);
        return JsonSerializer.Serialize(new { rsa = Convert.ToBase64String(rsaEnvelope), aes = EncryptAes(plaintext, secret) });
    }

    internal static string EncryptAes(string plaintext, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        var (key, iv) = Derive(Encoding.UTF8.GetBytes(passphrase), salt);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv;
            var data = aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), iv, PaddingMode.PKCS7);
            return Convert.ToBase64String([.. Encoding.ASCII.GetBytes("Salted__"), .. salt, .. data]);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv); }
    }

    internal static (byte[] Key, byte[] Iv) Derive(byte[] password, byte[] salt)
    {
        byte[] previous = [], material = [];
        // MD5 is required by the DSM envelope; it is not used to hash or store user passwords.
        while (material.Length < 48)
        {
            previous = MD5.HashData([.. previous, .. password, .. salt]);
            material = [.. material, .. previous];
        }
        var result = (material[..32], material[32..48]);
        CryptographicOperations.ZeroMemory(material);
        CryptographicOperations.ZeroMemory(password);
        return result;
    }
}
