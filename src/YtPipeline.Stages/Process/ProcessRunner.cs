using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YtPipeline.Stages.Process;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public sealed class ProcessException(string fileName, ProcessResult result)
    : Exception($"Process '{fileName}' exited with code {result.ExitCode}.\nSTDERR:\n{result.StdErr}")
{
    public ProcessResult Result { get; } = result;
}

/// <summary>
/// Shared helper for shelling out to CLI tools (claude, codex, ffmpeg).
/// Captures stdout/stderr, optionally pipes stdin, and honours cancellation.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger)
{
    /// <param name="onLine">
    /// Called with each stdout/stderr line as it arrives, for tools whose progress is worth showing
    /// while they run (an upload). The full output is still returned at the end either way.
    /// </param>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken ct = default,
        Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            // Read/write the child process streams as UTF-8. Without this, the default OEM
            // code page mangles non-ASCII (e.g. an em-dash "—" becomes "ΓÇö").
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = stdin is not null ? new UTF8Encoding(false) : null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogInformation("exec {File} {Args}", fileName, string.Join(' ', psi.ArgumentList));

        using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start '{fileName}'. Is it installed and on PATH?", ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Kill the entire process tree so ffmpeg / codex / claude don't linger.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var result = new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        if (!result.Success)
        {
            // Log WHY, not just that it failed. The reason is almost always in stderr — an
            // expired token, a missing file, a rate limit — and logging the bare exit code sends
            // you hunting through processes and folders for something the tool already said.
            // Falls back to stdout because some CLIs report errors there.
            var detail = FirstNonEmpty(result.StdErr, result.StdOut);
            logger.LogWarning("{File} exited {Code}{Detail}", fileName, result.ExitCode,
                string.IsNullOrWhiteSpace(detail) ? " (no output)" : $" — {Truncate(detail, 600)}");
        }
        return result;
    }

    /// <summary>Runs and throws <see cref="ProcessException"/> on a non-zero exit code.</summary>
    public async Task<ProcessResult> RunOrThrowAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(fileName, args, workingDirectory, stdin, ct);
        if (!result.Success) throw new ProcessException(fileName, result);
        return result;
    }

    private static string FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "";

    /// <summary>Keeps a wall of CLI output from burying the rest of the log.</summary>
    private static string Truncate(string s, int max)
    {
        var flat = s.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
