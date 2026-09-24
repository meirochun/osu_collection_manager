using System.Collections.Concurrent;

namespace OsuCollectionManager.Services;

public enum JobState { Running, Completed, Failed, Cancelled }

public sealed class Job
{
    private readonly ConcurrentQueue<string> _log = new();

    public required string Id { get; init; }
    public required string Kind { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public JobState State { get; set; } = JobState.Running;
    public string Status { get; set; } = "Starting…";
    public int Done { get; set; }
    public int Total { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public IEnumerable<string> Log => _log.TakeLast(300);
    internal CancellationTokenSource Cts { get; } = new();

    public void Report(string line)
    {
        _log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (_log.Count > 1000) _log.TryDequeue(out _);
    }
}

public sealed class JobManager(ILogger<JobManager> logger)
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public Job Start(string kind, Func<Job, CancellationToken, Task<object?>> work)
    {
        var job = new Job { Id = Guid.NewGuid().ToString("n")[..8], Kind = kind };
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                job.Result = await work(job, job.Cts.Token);
                job.State = JobState.Completed;
                job.Status = "Done";
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
                job.Status = "Cancelled";
            }
            catch (Exception e)
            {
                logger.LogError(e, "Job {Kind} {Id} failed", kind, job.Id);
                job.State = JobState.Failed;
                job.Error = e.Message;
                job.Status = "Failed: " + e.Message;
                job.Report("ERROR: " + e.Message);
            }
        });
        return job;
    }

    public Job? Get(string id) => _jobs.GetValueOrDefault(id);
    public IEnumerable<Job> All => _jobs.Values.OrderByDescending(j => j.Started);
    public void Cancel(string id) { if (_jobs.TryGetValue(id, out var j)) j.Cts.Cancel(); }
}
