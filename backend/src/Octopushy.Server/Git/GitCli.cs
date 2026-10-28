using System.Diagnostics;
using System.Text;

namespace Octopushy.Server.Git;

public class GitException(string message) : Exception(message);

/// <summary>Runs the Git CLI without a shell, without prompts, and only over allowed transports.</summary>
public sealed class GitCli(ILogger<GitCli> logger)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? secrets = null,
        CancellationToken cancellationToken = default,
        string? input = null)
    {
        var output = new MemoryStream();
        var (exitCode, error) = await RunCoreAsync(arguments, output, workingDirectory, environment, secrets, input, cancellationToken);
        if (exitCode != 0)
        {
            throw new GitException(error);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Runs Git and copies its standard output to <paramref name="output"/>; returns an error message on failure.</summary>
    public async Task<string?> RunToStreamAsync(
        IReadOnlyList<string> arguments,
        Stream output,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        var (exitCode, error) = await RunCoreAsync(arguments, output, workingDirectory, environment, secrets, null, cancellationToken);
        return exitCode == 0 ? null : error;
    }

    /// <summary>Runs Git and returns its exit code and output when the code is one of <paramref name="accepted"/>,
    /// such as 1 for a merge with conflicts.</summary>
    public async Task<(int ExitCode, string Output)> RunAcceptingAsync(
        IReadOnlyList<string> arguments,
        IReadOnlySet<int> accepted,
        IReadOnlyDictionary<string, string>? environment = null,
        string? input = null,
        CancellationToken cancellationToken = default)
    {
        var output = new MemoryStream();
        var (exitCode, error) = await RunCoreAsync(arguments, output, null, environment, null, input, cancellationToken);
        if (exitCode != 0 && !accepted.Contains(exitCode))
        {
            throw new GitException(error);
        }

        return (exitCode, Encoding.UTF8.GetString(output.ToArray()));
    }

    private async Task<(int ExitCode, string Error)> RunCoreAsync(
        IReadOnlyList<string> arguments,
        Stream output,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        IReadOnlyCollection<string>? secrets,
        string? input,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file:git:http:https:ssh";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["LC_ALL"] = "C";
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        using var process = Process.Start(start) ?? throw new GitException("Git could not be started.");
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            // Input is written while output is read, so neither pipe can fill and stall Git.
            var inputTask = WriteInputAsync(process.StandardInput, input, timeout.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            await inputTask;
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            // Observe the stderr task even when a bounded output stream refuses more data.
            try { await stderrTask; } catch (OperationCanceledException) { }
            throw;
        }

        var stderr = await stderrTask;
        if (process.ExitCode == 0)
        {
            return (0, "");
        }

        var message = Mask(stderr.Trim(), secrets);
        logger.LogDebug("git {Command} failed: {Error}", arguments.FirstOrDefault(), message);
        return (process.ExitCode, string.IsNullOrEmpty(message) ? $"git {arguments.FirstOrDefault()} exited with {process.ExitCode}" : message);
    }

    private static async Task WriteInputAsync(StreamWriter writer, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
            {
                await writer.WriteAsync(input.AsMemory(), cancellationToken);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // Git exited without reading everything; its exit code reports why.
        }
        finally
        {
            try { writer.Close(); } catch (IOException) { }
        }
    }

    private static string Mask(string text, IReadOnlyCollection<string>? secrets)
    {
        if (secrets is null)
        {
            return text;
        }

        foreach (var secret in secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
