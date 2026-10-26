using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace Tikai.Api.Security;

/// <summary>Tikai:EncryptionSecret (TIKAI_SECRET in compose): encrypts the Data Protection key ring.</summary>
public sealed class KeyRingOptions
{
    public const int MinSecretLength = 32;

    public string? EncryptionSecret { get; set; }
}

/// <summary>The AES key for the key ring, derived from the secret with HKDF. Wrong secret = different key = decryption fails.</summary>
internal static class KeyRingKey
{
    internal static readonly byte[] Context = "Tikai.KeyRing.v1"u8.ToArray();

    public static byte[] Derive(string secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, salt: "Tikai.KeyRing.salt"u8.ToArray(), info: Context);

    public static byte[] FromOptions(IOptions<KeyRingOptions> options) =>
        Derive(options.Value.EncryptionSecret ?? throw new InvalidOperationException("Tikai:EncryptionSecret is not set."));
}

/// <summary>
/// Encrypts key ring elements with AES-256-GCM: &lt;encryptedKey version="1"&gt;&lt;value&gt;base64(nonce | tag | ciphertext)&lt;/value&gt;&lt;/encryptedKey&gt;.
/// </summary>
internal sealed class SecretXmlEncryptor(byte[] key) : IXmlEncryptor
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), KeyRingKey.Context);
        }
        CryptographicOperations.ZeroMemory(plaintext);

        var element = new XElement("encryptedKey",
            new XAttribute("version", "1"),
            new XComment(" This key is encrypted with TIKAI_SECRET (Tikai:EncryptionSecret). "),
            new XElement("value", Convert.ToBase64String(output)));
        return new EncryptedXmlInfo(element, typeof(SecretXmlDecryptor));
    }
}

/// <summary>Created by Data Protection for every encrypted key it reads (needs the IServiceProvider constructor).</summary>
internal sealed class SecretXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var data = Convert.FromBase64String((string?)encryptedElement.Element("value")
            ?? throw new CryptographicException("The encrypted key has no value."));
        if ((string?)encryptedElement.Attribute("version") != "1" || data.Length < SecretXmlEncryptor.NonceSize + SecretXmlEncryptor.TagSize)
        {
            throw new CryptographicException("The encrypted key has an unknown format.");
        }

        var key = KeyRingKey.FromOptions(services.GetRequiredService<IOptions<KeyRingOptions>>());
        var plaintext = new byte[data.Length - SecretXmlEncryptor.NonceSize - SecretXmlEncryptor.TagSize];
        try
        {
            using var aes = new AesGcm(key, SecretXmlEncryptor.TagSize);
            aes.Decrypt(
                data.AsSpan(0, SecretXmlEncryptor.NonceSize),
                data.AsSpan(SecretXmlEncryptor.NonceSize + SecretXmlEncryptor.TagSize),
                data.AsSpan(SecretXmlEncryptor.NonceSize, SecretXmlEncryptor.TagSize),
                plaintext,
                KeyRingKey.Context);
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptographicException(
                "A Data Protection key cannot be decrypted: TIKAI_SECRET (Tikai:EncryptionSecret) is not the secret it was encrypted with.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
