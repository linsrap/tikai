using System.Diagnostics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Tikai.Core.Git;
using Tikai.Git;

namespace Tikai.IntegrationTests.Git;

/// <summary>Credentials against real servers: HTTPS-style token auth (git http-backend) and SSH keys (sshd in a container).</summary>
public sealed class GitCredentialTests : IDisposable
{
    private const string Token = "tok_9f8e7d6c5b4a";
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task The_token_reaches_the_server_but_never_the_arguments_or_the_repo_config()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = _temp.Combine("served");
        var remote = await TestGit.InitBareAsync(Path.Combine(root, "remote.git"));
        var head = await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));
        await using var server = await GitHttpServer.StartAsync(root, expected);
        var recorder = new RecordingRunner(new Tikai.Git.Processes.ProcessRunner());
        var git = TestGit.CreateService(runner: recorder);
        var url = GitRemoteUrl.Parse($"{server.BaseUrl}/remote.git");
        var credential = new GitCredential.HttpsToken("x-access-token", Token);
        var mirror = _temp.Combine("mirror.git");

        await git.CreateMirrorAsync(mirror, url, credential, null, ct);
        await TestGit.CommitAsync(remote, "main", "Second", ("a.txt", "2\n"));
        var fetched = await git.FetchAsync(mirror, url, credential, "main", ct);

        Assert.Equal(DefaultBranchSync.FastForwarded, fetched.DefaultBranch);
        Assert.NotEqual(head, await git.ResolveCommitAsync(mirror, "main", ct));
        Assert.Contains(expected, server.AuthorizationHeaders);
        Assert.DoesNotContain(server.AuthorizationHeaders, h => h != expected); // sent up front, no 401 round trip
        Assert.All(recorder.Requests, r => Assert.DoesNotContain(r.Arguments, a => a.Contains(Token, StringComparison.Ordinal) || a.Contains(expected, StringComparison.Ordinal)));
        Assert.All(Directory.EnumerateFiles(mirror, "*", SearchOption.AllDirectories), file =>
            Assert.DoesNotContain(expected[6..], File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_remote_that_wants_a_password_fails_at_once_instead_of_prompting()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = _temp.Combine("served");
        await TestGit.InitBareAsync(Path.Combine(root, "remote.git"));
        await using var server = await GitHttpServer.StartAsync(root, "Basic something-else");
        var git = TestGit.CreateService();
        var url = GitRemoteUrl.Parse($"{server.BaseUrl}/remote.git");

        var stopwatch = Stopwatch.StartNew();
        var withoutToken = await Assert.ThrowsAsync<GitException>(() => git.CreateMirrorAsync(_temp.Combine("a.git"), url, null, null, ct));
        var wrongToken = await Assert.ThrowsAsync<GitException>(() =>
            git.CreateMirrorAsync(_temp.Combine("b.git"), url, new GitCredential.HttpsToken("x", "wrong"), null, ct));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"took {stopwatch.Elapsed}");
        Assert.Contains("The remote asked for a login", withoutToken.Message, StringComparison.Ordinal);
        Assert.Contains("The remote asked for a login", wrongToken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ssh_key_works_the_host_key_is_trusted_on_first_use_and_a_changed_host_key_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sshd = await StartSshServerAsync(ct);
        var privateKey = Encoding.ASCII.GetString(await sshd.ReadFileAsync("/keys/id", ct));
        var keyDir = _temp.Combine("ssh-temp");
        var git = TestGit.CreateService(tempPath: keyDir);
        var url = GitRemoteUrl.Parse($"ssh://git@{sshd.Hostname}:{sshd.GetMappedPublicPort(22)}/home/git/repo.git");
        var credential = new GitCredential.SshKey(privateKey.Replace("\n", "\r\n", StringComparison.Ordinal)); // as pasted on Windows
        var mirror = _temp.Combine("mirror.git");

        var result = await git.CreateMirrorAsync(mirror, url, credential, null, ct);

        Assert.Equal(new MirrorResult("main", IsEmpty: false), result);
        var head = await git.ResolveCommitAsync(mirror, "main", ct);
        Assert.Equal("hello\n", Encoding.UTF8.GetString((await git.ReadFileAsync(mirror, head!, "hello.txt", ct))!));
        var knownHosts = Path.Combine(mirror, GitService.KnownHostsFile);
        Assert.Contains($"[{sshd.Hostname}]:{sshd.GetMappedPublicPort(22)}", await File.ReadAllTextAsync(knownHosts, ct), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(keyDir, "tikai-ssh"))); // the key file is gone after the call
        Assert.Equal(DefaultBranchSync.UpToDate, (await git.FetchAsync(mirror, url, credential, "main", ct)).DefaultBranch);

        // Someone swaps the server (or sits in between): the stored host key no longer matches.
        var otherKey = Encoding.ASCII.GetString(await sshd.ReadFileAsync("/keys/id.pub", ct)).Split(' ');
        await File.WriteAllTextAsync(knownHosts, $"[{sshd.Hostname}]:{sshd.GetMappedPublicPort(22)} {otherKey[0]} {otherKey[1]}\n", ct);
        var changed = await Assert.ThrowsAsync<GitException>(() => git.FetchAsync(mirror, url, credential, "main", ct));
        Assert.Contains("Host key verification failed", changed.Message, StringComparison.Ordinal);
    }

    // alpine + sshd + git; the container makes the client key pair and a repository with one commit.
    private static async Task<IContainer> StartSshServerAsync(CancellationToken cancellationToken)
    {
        const string script = """
            set -e
            apk add --no-cache openssh-server git >/dev/null
            ssh-keygen -A >/dev/null
            adduser -D -s /bin/sh git
            sed -i 's/^git:!/git:*/' /etc/shadow
            mkdir -p /keys /home/git/.ssh
            ssh-keygen -q -t ed25519 -N '' -f /keys/id
            cp /keys/id.pub /home/git/.ssh/authorized_keys
            git init -q --bare -b main /home/git/repo.git
            git init -q -b main /tmp/w && cd /tmp/w && echo hello > hello.txt && git add hello.txt
            git -c user.name=t -c user.email=t@example.org commit -qm First && git push -q /home/git/repo.git main
            chown -R git:git /home/git && chmod 700 /home/git/.ssh && chmod 600 /home/git/.ssh/authorized_keys
            exec /usr/sbin/sshd -D -e
            """;
        var container = new ContainerBuilder("alpine:3.22")
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand(script.Replace("\r\n", "\n", StringComparison.Ordinal))
            .WithPortBinding(22, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening"))
            .Build();
        await container.StartAsync(cancellationToken);
        return container;
    }
}
