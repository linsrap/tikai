using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Tikai.Git.Processes;

/// <summary>Runs a program to completion. Arguments are passed as a list, never as one command string.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Environment">Overrides for the child's environment; a null value removes the variable.</param>
/// <param name="MaxOutputBytes">Standard output beyond this size kills the process and fails the call.</param>
public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? WorkingDirectory = null,
    byte[]? StandardInput = null,
    long MaxOutputBytes = 64 * 1024 * 1024);

public sealed record ProcessResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public string OutputText => Encoding.UTF8.GetString(StandardOutput);
}

public sealed class ProcessTimeoutException(string message) : Exception(message);

/// <summary>The program wrote more than <see cref="ProcessRequest.MaxOutputBytes"/>; it was killed.</summary>
public sealed class ProcessOutputLimitException(string message) : Exception(message);

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in request.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{request.FileName}': {ex.Message}", ex);
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var output = new MemoryStream();
        try
        {
            var readOutput = CopyLimitedAsync(process.StandardOutput.BaseStream, output, request.MaxOutputBytes, linked.Token);
            var readError = process.StandardError.ReadToEndAsync(linked.Token);
            await WriteInputAsync(process, request.StandardInput, linked.Token);
            await readOutput;
            var error = await readError;
            await process.WaitForExitAsync(linked.Token);
            return new ProcessResult(process.ExitCode, output.ToArray(), error);
        }
        catch (Exception) when (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ProcessTimeoutException($"'{request.FileName} {(request.Arguments.Count > 0 ? request.Arguments[0] : "")}' did not finish within {request.Timeout}.");
            }
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[]? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is { Length: > 0 })
            {
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program exited without reading all of its input; its exit code tells what happened.
        }
    }

    private static async Task CopyLimitedAsync(Stream source, MemoryStream target, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (target.Length + read > maxBytes)
            {
                throw new ProcessOutputLimitException($"The program wrote more than {maxBytes} bytes of output.");
            }
            target.Write(buffer, 0, read);
        }
    }
}
