using System.Text.Json.Nodes;
using RabbitSchemaApi.Repositories;
using RabbitSchemaApi.Services;

namespace RabbitSchemaApi.BackgroundServices;

public sealed class BackgroundTaskProcessor : BackgroundService
{
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundTaskProcessor> _logger;

    public BackgroundTaskProcessor(
        IBackgroundTaskQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<BackgroundTaskProcessor> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Task Processor started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _queue.DequeueAsync(stoppingToken);
                await ProcessTaskAsync(task, stoppingToken);

                // If processing succeeds, remove from persistent storage
                if (_queue is PersistentBackgroundTaskQueue persistentQueue)
                {
                    persistentQueue.MarkComplete(task.Id);
                }
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
                    messageId: task.Id, // Note: Use the original message ID if possible
                    payload: task.Payload,
                    queueName: task.Target ?? "unknown");
                break;

            default:
                _logger.LogWarning("Unknown background task type: {Type}", task.Type);
                break;
        }
    }
}
