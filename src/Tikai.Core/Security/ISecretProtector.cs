namespace Tikai.Core.Security;

/// <summary>Encrypts secrets (git credentials, later LLM keys) for storage in the database.</summary>
public interface ISecretProtector
{
    /// <param name="purpose">Separates kinds of secrets: a value protected for one purpose cannot be read for another.</param>
    string Protect(string purpose, string plaintext);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the value cannot be decrypted (wrong or lost keys).</summary>
    string Unprotect(string purpose, string protectedValue);
}
