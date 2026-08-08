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
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken ct = default)
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

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

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
            logger.LogWarning("{File} exited {Code}", fileName, result.ExitCode);
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
}
