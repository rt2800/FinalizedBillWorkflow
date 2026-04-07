using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mbr.Api.Workflow.FinalizedBill.BackgroundServices;

public enum BackgroundTaskType
{
    SftpUpload,
    AuditLog
}

public sealed class BackgroundTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BackgroundTaskType Type { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string? Target { get; set; } // e.g., QueueName or RemotePath
    public string? MicBillId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public interface IBackgroundTaskQueue
{
    ValueTask EnqueueAsync(BackgroundTask task);
    ValueTask<BackgroundTask> DequeueAsync(CancellationToken ct);
}

public sealed class PersistentBackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly System.Threading.Channels.Channel<BackgroundTask> _queue;
    private readonly string _persistenceDir;
    private readonly ILogger<PersistentBackgroundTaskQueue> _logger;

    public PersistentBackgroundTaskQueue(ILogger<PersistentBackgroundTaskQueue> logger)
    {
        _logger = logger;
        _persistenceDir = Path.Combine(AppContext.BaseDirectory, "PendingTasks");
        Directory.CreateDirectory(_persistenceDir);

        // Bounded channel to prevent memory issues under extreme load
        var options = new System.Threading.Channels.BoundedChannelOptions(1000)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
        };
        _queue = System.Threading.Channels.Channel.CreateBounded<BackgroundTask>(options);

        // Recover existing tasks from disk
        RecoverTasks();
    }

    public async ValueTask EnqueueAsync(BackgroundTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 1. Persist to disk first
        var filePath = Path.Combine(_persistenceDir, $"{task.Id}.json");
        var json = JsonSerializer.Serialize(task);
        await File.WriteAllTextAsync(filePath, json);

        // 2. Add to in-memory queue
        await _queue.Writer.WriteAsync(task);
        _logger.LogDebug("Task {TaskId} ({Type}) enqueued and persisted.", task.Id, task.Type);
    }

    public async ValueTask<BackgroundTask> DequeueAsync(CancellationToken ct)
    {
        try
        {
            // Prefer ReadAsync for cleaner awaitable task
            return await _queue.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Re-throw or handle as per caller's expectation.
            // BackgroundTaskProcessor handles this.
            throw;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            _logger.LogWarning("BackgroundTaskQueue channel was closed while dequeuing.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error dequeuing background task.");
            throw;
        }
    }

    public void MarkComplete(string taskId)
    {
        var filePath = Path.Combine(_persistenceDir, $"{taskId}.json");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogDebug("Task {TaskId} completed and removed from disk.", taskId);
        }
    }

    private void RecoverTasks()
    {
        var files = Directory.GetFiles(_persistenceDir, "*.json");
        _logger.LogInformation("Recovering {Count} pending tasks from disk...", files.Length);

        foreach (var file in files.OrderBy(f => File.GetCreationTimeUtc(f)))
        {
            try
            {
                var json = File.ReadAllText(file);
                var task = JsonSerializer.Deserialize<BackgroundTask>(json);
                if (task != null)
                {
                    _queue.Writer.TryWrite(task);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover task from {File}", file);
            }
        }
    }
}
