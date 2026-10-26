namespace Tikai.Core.Git;

/// <summary>How the hub authenticates to a remote. ToString never shows the secret, so logging one is safe.</summary>
public abstract record GitCredential
{
    private GitCredential()
    {
    }

    /// <summary>HTTPS with a token (GitHub, GitLab, Gitea/Forgejo personal or deploy tokens), sent as basic auth.</summary>
    public sealed record HttpsToken(string Username, string Token) : GitCredential
    {
        public override string ToString() => $"HttpsToken {{ Username = {Username}, Token = *** }}";
    }

    /// <summary>SSH with a private key in OpenSSH format, without a passphrase.</summary>
    public sealed record SshKey(string PrivateKey) : GitCredential
    {
        public override string ToString() => "SshKey { PrivateKey = *** }";
    }
}
