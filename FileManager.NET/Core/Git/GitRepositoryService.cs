using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace FileManager.NET.Core.Git;

internal sealed class GitRepositoryService : IGitRepositoryService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int AvailabilityTimeoutMilliseconds = 2_000;

    private readonly string _gitExecutable;
    private volatile bool _gitAvailable;

    public GitRepositoryService()
        : this("git.exe")
    {
    }

    internal GitRepositoryService(string gitExecutable)
    {
        _gitExecutable = gitExecutable;
        _gitAvailable = CheckGitAvailability();
    }

    public bool IsAvailable => _gitAvailable;

    public async Task<GitRepositoryInfo?> DetectAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!_gitAvailable || !Directory.Exists(directory))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var repository = await RunGitAsync(
                directory,
                ["rev-parse", "--is-inside-work-tree", "--show-toplevel"],
                timeout.Token).ConfigureAwait(false);
            if (repository.ExitCode != 0)
            {
                return null;
            }

            var lines = repository.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2
                || !string.Equals(lines[0], "true", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var worktreeRoot = lines[1];
            var symbolicBranch = await RunGitAsync(
                directory,
                ["symbolic-ref", "--quiet", "--short", "HEAD"],
                timeout.Token).ConfigureAwait(false);
            var detached = symbolicBranch.ExitCode != 0 || symbolicBranch.Output.Trim().Length == 0;
            string branch;
            if (detached)
            {
                var commit = await RunGitAsync(
                    directory,
                    ["rev-parse", "--short", "HEAD"],
                    timeout.Token).ConfigureAwait(false);
                branch = commit.ExitCode == 0 && commit.Output.Length > 0
                    ? $"detached@{commit.Output.Trim()}"
                    : "detached";
            }
            else
            {
                branch = symbolicBranch.Output.Trim();
            }

            var remoteName = await GetTrackedRemoteNameAsync(
                directory,
                branch,
                detached,
                timeout.Token).ConfigureAwait(false);
            var remoteUrl = await GetRemoteUrlAsync(
                directory,
                remoteName,
                timeout.Token).ConfigureAwait(false);

            return new GitRepositoryInfo(
                worktreeRoot,
                branch,
                remoteUrl is null ? null : NormalizeRemote(remoteUrl));
        }
        catch (Win32Exception ex)
        {
            _gitAvailable = false;
            Log.Information(
                "Git executable became unavailable; repository detection is disabled: {Message}",
                ex.Message);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Git repository detection timed out for {Directory}", directory);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure detecting Git repository for {Directory}", directory);
            return null;
        }
    }

    private async Task<string> GetTrackedRemoteNameAsync(
        string directory,
        string branch,
        bool detached,
        CancellationToken cancellationToken)
    {
        if (!detached)
        {
            var upstream = await RunGitAsync(
                directory,
                [
                    "for-each-ref",
                    "--format=%(upstream:remotename)",
                    $"refs/heads/{branch}",
                ],
                cancellationToken).ConfigureAwait(false);
            var remoteName = upstream.Output.Trim();
            if (upstream.ExitCode == 0
                && remoteName.Length > 0
                && !string.Equals(remoteName, ".", StringComparison.Ordinal))
            {
                return remoteName;
            }
        }

        return "origin";
    }

    private async Task<string?> GetRemoteUrlAsync(
        string directory,
        string remoteName,
        CancellationToken cancellationToken)
    {
        var remote = await RunGitAsync(
            directory,
            ["remote", "get-url", remoteName],
            cancellationToken).ConfigureAwait(false);
        if (remote.ExitCode == 0 && remote.Output.Trim().Length > 0)
        {
            return remote.Output.Trim();
        }

        if (string.Equals(remoteName, "origin", StringComparison.Ordinal))
        {
            return null;
        }

        var origin = await RunGitAsync(
            directory,
            ["remote", "get-url", "origin"],
            cancellationToken).ConfigureAwait(false);
        return origin.ExitCode == 0 && origin.Output.Trim().Length > 0
            ? origin.Output.Trim()
            : null;
    }

    private static string? NormalizeRemote(string remote)
    {
        remote = remote.Trim();
        var helperSeparator = remote.IndexOf("::", StringComparison.Ordinal);
        if (helperSeparator > 0 && IsRemoteHelperPrefix(remote.AsSpan(0, helperSeparator)))
        {
            var payload = remote[(helperSeparator + 2)..];
            if (payload.Length == 0)
            {
                return null;
            }

            var payloadColon = FindScpSeparator(payload);
            var payloadAt = payload.IndexOf('@');
            var isUri = Uri.TryCreate(payload, UriKind.Absolute, out var payloadUri)
                        && (payloadUri.IsFile || payloadUri.Host.Length > 0);
            var isScp = payloadAt >= 0 && payloadColon > payloadAt;
            return isUri || isScp ? NormalizeRemote(payload) : null;
        }

        var bracketedScpSeparator = FindScpSeparator(remote);
        var closingBracket = remote.IndexOf(']');
        if (closingBracket >= 0 && bracketedScpSeparator > closingBracket)
        {
            return NormalizeScpRemote(remote, bracketedScpSeparator);
        }

        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            && (uri.IsFile || uri.Host.Length > 0))
        {
            if (uri.IsFile)
            {
                return TrimRepositorySuffix(uri.LocalPath.Replace('\\', '/'));
            }

            var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            var path = TrimRepositorySuffix(uri.AbsolutePath);
            return path.Length == 0 ? host : $"{host}/{path.TrimStart('/')}";
        }

        var colon = FindScpSeparator(remote);
        if (colon == 1 && char.IsAsciiLetter(remote[0]))
        {
            return remote.Contains('@')
                ? null
                : TrimRepositorySuffix(remote.Replace('\\', '/'));
        }

        if (remote.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var slash = remote.IndexOfAny(['/', '\\']);
        if (colon > 0 && (slash < 0 || colon < slash))
        {
            return NormalizeScpRemote(remote, colon);
        }

        return remote.Contains('@')
            ? null
            : TrimRepositorySuffix(remote.Replace('\\', '/'));
    }

    private static string NormalizeScpRemote(string remote, int separator)
    {
        var host = remote[..separator];
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }

        var path = TrimRepositorySuffix(remote[(separator + 1)..]);
        return path.Length == 0 ? host : $"{host}/{path.TrimStart('/')}";
    }

    private static string TrimRepositorySuffix(string value)
    {
        value = value.Trim().TrimEnd('/');
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    private static bool IsRemoteHelperPrefix(ReadOnlySpan<char> prefix)
    {
        foreach (var character in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static int FindScpSeparator(string remote)
    {
        var openingBracket = remote.IndexOf('[');
        if (openingBracket >= 0)
        {
            var closingBracket = remote.IndexOf(']', openingBracket + 1);
            if (closingBracket >= 0)
            {
                return remote.IndexOf(':', closingBracket + 1);
            }
        }

        return remote.IndexOf(':');
    }

    private bool CheckGitAvailability()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _gitExecutable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--version");

            if (!process.Start())
            {
                Log.Information("Git could not be started; repository detection is disabled");
                return false;
            }

            if (!process.WaitForExit(AvailabilityTimeoutMilliseconds))
            {
                TryKillProcess(process);
                Log.Warning("Git availability check timed out; repository detection is disabled");
                return false;
            }

            if (process.ExitCode != 0)
            {
                Log.Information(
                    "Git availability check returned exit code {ExitCode}; repository detection is disabled",
                    process.ExitCode);
                return false;
            }

            Log.Debug("Git repository detection is available");
            return true;
        }
        catch (Win32Exception)
        {
            Log.Information("Git is not installed or not on PATH; repository detection is disabled");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Git availability check failed; repository detection is disabled");
            return false;
        }
    }

    private async Task<GitCommandResult> RunGitAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(directory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
            Log.Debug(ex, "Git process exited before cancellation cleanup");
        }
    }

    private sealed record GitCommandResult(int ExitCode, string Output);
}
