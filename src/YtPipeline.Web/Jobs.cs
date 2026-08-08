using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace YtPipeline.Web;

public enum JobStatus { Running, Succeeded, Failed }

/// <summary>A background run of one pipeline stage, with captured log lines for the UI.</summary>
public sealed class Job
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public required string Stage { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Running;
    public string? Error { get; set; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }

    private readonly List<string> _logs = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>CancellationToken to pass to the pipeline work. Fires when <see cref="Cancel"/> is called.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Request cancellation. Marks the job failed immediately so the UI updates right away.</summary>
    public void Cancel()
    {
        if (Status != JobStatus.Running) return;
        Status = JobStatus.Failed;
        Error = "Stopped by user.";
        FinishedUtc = DateTime.UtcNow;
        Append("--- stopped by user ---");
        try { _cts.Cancel(); } catch { /* already disposed */ }
    }

    public void Append(string line) { lock (_logs) _logs.Add(line); }
    public IReadOnlyList<string> Snapshot(int skip)
    {
        lock (_logs) return _logs.Skip(skip).ToList();
    }
    public int LogCount { get { lock (_logs) return _logs.Count; } }
}

/// <summary>Flows the active <see cref="Job"/> down the async call chain so logs can be captured.</summary>
public static class JobContext
{
    private static readonly AsyncLocal<Job?> _current = new();
    public static Job? Current { get => _current.Value; set => _current.Value = value; }
}

/// <summary>Starts and tracks background stage jobs.</summary>
public sealed class JobService
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public Job Start(string runId, string stage, Func<CancellationToken, Task> work)
    {
        var job = new Job { Id = Guid.NewGuid().ToString("N"), RunId = runId, Stage = stage };
        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            JobContext.Current = job;
            try
            {
                await work(job.Token);          // pass the job's real CT so cancel actually stops the work
                if (job.Status == JobStatus.Running)
                    job.Status = JobStatus.Succeeded;
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
            {
                // Cancel() already set Status = Failed + wrote the stop message.
            }
            catch (Exception ex)
            {
                if (job.Status == JobStatus.Running)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = ex.Message;
                    job.Append($"ERROR: {ex.Message}");
                }
            }
            finally
            {
                if (job.FinishedUtc is null) job.FinishedUtc = DateTime.UtcNow;
                JobContext.Current = null;
            }
        });

        return job;
    }

    public Job? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>
    /// True while any job for this run is still running. Used to refuse destructive actions —
    /// deleting a run's folder mid-stage would leave a half-written run behind.
    /// </summary>
    public bool HasActiveJobFor(string runId) =>
        _jobs.Values.Any(j => j.Status == JobStatus.Running
                              && string.Equals(j.RunId, runId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cancel a running job. Returns false if the job doesn't exist or is already finished.</summary>
    public bool Cancel(string id)
    {
        var job = Get(id);
        if (job is null || job.Status != JobStatus.Running) return false;
        job.Cancel();
        return true;
    }
}

/// <summary>Logger provider that appends Information+ logs to the job active on the current async flow.</summary>
public sealed class JobLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new JobLogger();
    public void Dispose() { }

    private sealed class JobLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            var job = JobContext.Current;
            if (job is null || !IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:HH:mm:ss} [{logLevel.ToString()[..3].ToUpperInvariant()}] {formatter(state, ex)}";
            if (ex is not null) line += $" — {ex.Message}";
            job.Append(line);
        }
    }
}
