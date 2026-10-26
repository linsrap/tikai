using System.Text.Json;
using Tikai.Core.Git;
using Tikai.Core.Projects;
using Tikai.Core.Security;

namespace Tikai.Api.Projects;

/// <summary>Checks entered credentials and moves them between <see cref="CredentialInput"/>, the encrypted column, and <see cref="GitCredential"/>.</summary>
internal sealed class RepositoryCredentials(ISecretProtector protector)
{
    private const string Purpose = "Tikai.GitCredentials.v1";
    private const string DefaultUsername = "x-access-token";
    private const int MaxTokenLength = 4096;
    private const int MaxKeyLength = 16 * 1024;

    /// <returns>Null when the credential fits the remote, otherwise a message for people.</returns>
    public static string? ValidationError(CredentialInput? input, GitRemoteUrl? remote)
    {
        var kind = input?.Kind ?? CredentialKind.None;
        if (remote is null)
        {
            return kind == CredentialKind.None ? null : "A local repository has no remote, so it needs no credential.";
        }

        return (kind, remote.Transport) switch
        {
            (CredentialKind.None, GitTransport.Ssh) => "SSH remotes need an SSH key.",
            (CredentialKind.None, _) => null,
            (CredentialKind.HttpsToken, GitTransport.Https or GitTransport.Http) => TokenError(input!),
            (CredentialKind.HttpsToken, _) => "A token works only with https:// remotes.",
            (CredentialKind.SshKey, GitTransport.Ssh) => KeyError(input!.PrivateKey),
            (CredentialKind.SshKey, _) => "An SSH key works only with SSH remotes (ssh:// or user@host:path).",
            _ => "Unknown credential kind.",
        };
    }

    /// <summary>The values for <see cref="Repository.CredentialKind"/> and <see cref="Repository.EncryptedCredential"/>. Validate first.</summary>
    public (CredentialKind Kind, string? Encrypted) Protect(CredentialInput? input)
    {
        Stored? stored = input?.Kind switch
        {
            CredentialKind.HttpsToken => new Stored(
                string.IsNullOrWhiteSpace(input.Username) ? DefaultUsername : input.Username.Trim(), input.Token!.Trim(), null),
            CredentialKind.SshKey => new Stored(null, null, input.PrivateKey!.Trim()),
            _ => null,
        };
        return stored is null
            ? (CredentialKind.None, null)
            : (input!.Kind, protector.Protect(Purpose, JsonSerializer.Serialize(stored)));
    }

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the hub's keys changed.</summary>
    public GitCredential? Unprotect(Repository repository)
    {
        if (repository.EncryptedCredential is null)
        {
            return null;
        }
        var stored = JsonSerializer.Deserialize<Stored>(protector.Unprotect(Purpose, repository.EncryptedCredential))!;
        return repository.CredentialKind switch
        {
            CredentialKind.HttpsToken => new GitCredential.HttpsToken(stored.Username!, stored.Token!),
            CredentialKind.SshKey => new GitCredential.SshKey(stored.PrivateKey!),
            _ => null,
        };
    }

    private static string? TokenError(CredentialInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Token) || input.Token.Trim().Length > MaxTokenLength)
        {
            return "Enter the access token.";
        }
        if (input.Username?.Contains(':', StringComparison.Ordinal) == true)
        {
            return "The user name must not contain \":\".";
        }
        return input.Token.Any(char.IsControl) ? "The token must not contain line breaks or control characters." : null;
    }

    private static string? KeyError(string? privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey) || privateKey.Length > MaxKeyLength
            || !privateKey.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return "Paste a private key in OpenSSH format (-----BEGIN OPENSSH PRIVATE KEY-----).";
        }
        return privateKey.Contains("ENCRYPTED", StringComparison.Ordinal) ? "Keys with a passphrase are not supported." : null;
    }

    private sealed record Stored(string? Username, string? Token, string? PrivateKey);
}
