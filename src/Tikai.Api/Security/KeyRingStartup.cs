using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace Tikai.Api.Security;

/// <summary>Runs before the hub serves anything: brings the key ring under TIKAI_SECRET and proves the secret is the right one.</summary>
internal static partial class KeyRingStartup
{
    private static readonly XNamespace _dataProtection = "http://schemas.asp.net/2015/03/dataProtection";

    public static void Prepare(IServiceProvider services, string keysDirectory, ILogger logger)
    {
        // Reading Value validates the options, so a missing or short secret fails here with its own message.
        var key = KeyRingKey.FromOptions(services.GetRequiredService<IOptions<KeyRingOptions>>());

        var encrypted = EncryptPlainKeys(keysDirectory, new SecretXmlEncryptor(key));
        if (encrypted > 0)
        {
            LogEncryptedOldKeys(logger, encrypted);
        }

        // Data Protection would only log an unreadable key and quietly create a new one: sessions would end and
        // stored credentials could no longer be decrypted. Fail instead, and say why.
        foreach (var dataProtectionKey in services.GetRequiredService<IKeyManager>().GetAllKeys())
        {
            try
            {
                _ = dataProtectionKey.Descriptor;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Data Protection key {dataProtectionKey.KeyId} in {keysDirectory} cannot be decrypted. " +
                    "TIKAI_SECRET (Tikai:EncryptionSecret) must be the secret this installation was set up with. " +
                    "If that secret is lost, the keys are too: delete the keys directory, then everyone logs in again and stored credentials must be entered again.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Encrypts the secret parts of keys that an older Tikai (before key ring encryption) wrote in plain text,
    /// the same way Data Protection encrypts new keys.
    /// </summary>
    /// <returns>The number of key files changed.</returns>
    internal static int EncryptPlainKeys(string keysDirectory, IXmlEncryptor encryptor)
    {
        if (!Directory.Exists(keysDirectory))
        {
            return 0;
        }

        var changed = 0;
        foreach (var file in Directory.EnumerateFiles(keysDirectory, "key-*.xml"))
        {
            var document = XDocument.Load(file);
            var plain = document.Descendants()
                .Where(e => (bool?)e.Attribute(_dataProtection + "requiresEncryption") == true)
                .ToList();
            if (plain.Count == 0)
            {
                continue;
            }

            foreach (var element in plain)
            {
                var info = encryptor.Encrypt(new XElement(element));
                element.ReplaceWith(new XElement(_dataProtection + "encryptedSecret",
                    new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!),
                    info.EncryptedElement));
            }

            // Write next to it and move over, so a crash never leaves a half-written key.
            var temp = file + ".tmp";
            document.Save(temp);
            File.Move(temp, file, overwrite: true);
            changed++;
        }
        return changed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encrypted {Count} Data Protection key(s) that were stored unencrypted, using TIKAI_SECRET")]
    private static partial void LogEncryptedOldKeys(ILogger logger, int count);
}
