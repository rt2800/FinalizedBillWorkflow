using System.Text.Json.Nodes;
using Mbr.Api.Workflow.FinalizedBill.Repositories;
using Mbr.Api.Workflow.FinalizedBill.Services;

namespace Mbr.Api.Workflow.FinalizedBill.BackgroundServices;

public sealed class BackgroundTaskProcessor : BackgroundService, IDisposable
{
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundTaskProcessor> _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;

    public BackgroundTaskProcessor(
        IBackgroundTaskQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<BackgroundTaskProcessor> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Bounded concurrency to protect resources (e.g., DB connections, SFTP throughput)
        _concurrencySemaphore = new SemaphoreSlim(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Task Processor started with bounded concurrency.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _queue.DequeueAsync(stoppingToken);

                await _concurrencySemaphore.WaitAsync(stoppingToken);

                // Process each task in its own Task to allow concurrent processing
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTaskAsync(task, stoppingToken);

                        // If processing succeeds, remove from persistent storage
                        if (_queue is PersistentBackgroundTaskQueue persistentQueue)
                        {
                            persistentQueue.MarkComplete(task.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing background task {TaskId}.", task.Id);
                        await NotifyFailureAsync(task, ex);
                    }
                    finally
                    {
                        _concurrencySemaphore.Release();
                    }
                }, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing background task.");
            }
        }

        _logger.LogInformation("Background Task Processor stopping.");
    }

    private async Task NotifyFailureAsync(BackgroundTask task, Exception ex)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IFinalizedBillRepository>();
            var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

            await repository.AddExceptionLogAsync(ex, task.MicBillId, task.CorrelationId, $"Background Task Failure: {task.Type}");

            var subject = $"Background Task Failure ({task.Type}): {ex.Message}";
            var body = $"A background task failed to complete.\n\n" +
                       $"Task ID: {task.Id}\n" +
                       $"Task Type: {task.Type}\n" +
                       $"MicBillId: {task.MicBillId}\n" +
                       $"CorrelationId: {task.CorrelationId}\n" +
                       $"Exception: {ex.Message}\n\n" +
                       $"Stack Trace:\n{ex.StackTrace}";

            await emailSender.SendEmailAsync(subject, body);
        }
        catch (Exception notifyEx)
        {
            _logger.LogCritical(notifyEx, "Failed to send failure notification for background task {TaskId}", task.Id);
        }
    }

    private async Task ProcessTaskAsync(BackgroundTask task, CancellationToken ct)
    {
        _logger.LogInformation("Processing background task {TaskId} of type {Type}.", task.Id, task.Type);

        using var scope = _scopeFactory.CreateScope();

        switch (task.Type)
        {
            case BackgroundTaskType.SftpUpload:
                var sftpService = scope.ServiceProvider.GetRequiredService<ISftpService>();
                var payloadNode = JsonNode.Parse(task.Payload);
                if (payloadNode != null)
                {
                    await sftpService.UploadAsync(payloadNode, ct);
                }
                break;

            case BackgroundTaskType.AuditLog:
                var repository = scope.ServiceProvider.GetRequiredService<IFinalizedBillRepository>();
                await repository.AddAuditLogAsync(
                    micBillId: task.MicBillId ?? "N/A",
                    correlationId: task.CorrelationId ?? "N/A",
                    payload: task.Payload);
                break;

            default:
                _logger.LogWarning("Unknown background task type: {Type}", task.Type);
                break;
        }
    }
}
