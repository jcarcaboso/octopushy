using System.Diagnostics;
using System.Text;

namespace Octopushy.Server.Git;

public sealed class GitException(string message) : Exception(message);

/// <summary>Runs the Git CLI without a shell, without prompts, and only over allowed transports.</summary>
public sealed class GitCli(ILogger<GitCli> logger)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        var output = new MemoryStream();
        var error = await RunToStreamAsync(arguments, output, workingDirectory, environment, secrets, cancellationToken);
        if (error is not null)
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
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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
        process.StandardInput.Close();
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stderr = await stderrTask;
        if (process.ExitCode == 0)
        {
            return null;
        }

        var message = Mask(stderr.Trim(), secrets);
        logger.LogDebug("git {Command} failed: {Error}", arguments.FirstOrDefault(), message);
        return string.IsNullOrEmpty(message) ? $"git {arguments.FirstOrDefault()} exited with {process.ExitCode}" : message;
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
