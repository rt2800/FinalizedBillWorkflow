using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Serilog.Context;
using Mbr.Api.Workflow.FinalizedBill.Resilience;
using Polly;

namespace Mbr.Api.Workflow.FinalizedBill.Repositories;

public interface IFinalizedBillRepository
{
    Task AddAuditLogAsync(string messageId, string payload, string queueName);
    Task AddExceptionLogAsync(Exception ex, string? messageId = null, string? context = null);
    Task AddFailedMessageAsync(string messageId, string payload, string queueName, string errorMessage);
}

public class FinalizedBillRepository : IFinalizedBillRepository
{
    private readonly string _connectionString;
    private readonly ILogger<FinalizedBillRepository> _logger;
    private readonly string _fallbackLogDir;

    public FinalizedBillRepository(IConfiguration configuration, ILogger<FinalizedBillRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("OracleDbConnection")
            ?? throw new ArgumentNullException("OracleDbConnection connection string is missing.");
        _logger = logger;

        _fallbackLogDir = Path.Combine(AppContext.BaseDirectory, "DbFallbackLogs");
        Directory.CreateDirectory(_fallbackLogDir);
    }

    public async Task AddAuditLogAsync(string messageId, string payload, string queueName)
    {
        using (LogContext.PushProperty("MessageId", messageId))
        using (LogContext.PushProperty("QueueName", queueName))
        {
            _logger.LogInformation("Attempting to insert audit log for message {MessageId} to queue {QueueName}", messageId, queueName);

            var policy = ResiliencePolicies.CreateRetryPolicy(_logger, $"AddAuditLog to DB for {messageId}");

            try
            {
                await policy.ExecuteAsync(async () =>
                {
                    using var connection = new OracleConnection(_connectionString);
                    await connection.OpenAsync();

                    using var command = connection.CreateCommand();
                    command.BindByName = true;
                    command.CommandText = "INSERT INTO finalizedbill_logs (message_id, payload, queue_name, created_at) VALUES (:messageId, :payload, :queueName, :createdAt)";

                    command.Parameters.Add("messageId", OracleDbType.Varchar2).Value = messageId;
                    command.Parameters.Add("payload", OracleDbType.Clob).Value = payload;
                    command.Parameters.Add("queueName", OracleDbType.Varchar2).Value = queueName;
                    command.Parameters.Add("createdAt", OracleDbType.TimeStamp).Value = DateTime.Now;

                    await command.ExecuteNonQueryAsync();
                });
                _logger.LogInformation("Successfully inserted audit log for message {MessageId}", messageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert audit log for message {MessageId} after retries. Falling back to local file.", messageId);

                // Fallback: Local log file
                try
                {
                    var fallbackPath = Path.Combine(_fallbackLogDir, $"audit_log_{messageId}.json");
                    var logEntry = new { MessageId = messageId, QueueName = queueName, Payload = payload, Timestamp = DateTime.UtcNow };
                    await File.WriteAllTextAsync(fallbackPath, System.Text.Json.JsonSerializer.Serialize(logEntry));
                    _logger.LogWarning("Audit log saved to fallback: {FilePath}", fallbackPath);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogCritical(fallbackEx, "Failed to save audit log to DB fallback.");
                }
            }
        }
    }

    public async Task AddExceptionLogAsync(Exception ex, string? messageId = null, string? context = null)
    {
        using (LogContext.PushProperty("MessageId", messageId))
        using (LogContext.PushProperty("ExecutionContext", context))
        {
            _logger.LogWarning("Attempting to insert exception log for context {ExecutionContext}", context ?? "N/A");

            var policy = ResiliencePolicies.CreateRetryPolicy(_logger, $"AddExceptionLog to DB for {context}");

            try
            {
                await policy.ExecuteAsync(async () =>
                {
                    using var connection = new OracleConnection(_connectionString);
                    await connection.OpenAsync();

                    using var command = connection.CreateCommand();
                    command.BindByName = true;
                    command.CommandText = "INSERT INTO finalizedbill_exceptions (message_id, exception_message, stack_trace, context, created_at) VALUES (:messageId, :exceptionMessage, :stackTrace, :context, :createdAt)";

                    command.Parameters.Add("messageId", OracleDbType.Varchar2).Value = (object?)messageId ?? DBNull.Value;
                    command.Parameters.Add("exceptionMessage", OracleDbType.Varchar2).Value = ex.Message;
                    command.Parameters.Add("stackTrace", OracleDbType.Clob).Value = (object?)ex.StackTrace ?? DBNull.Value;
                    command.Parameters.Add("context", OracleDbType.Varchar2).Value = (object?)context ?? DBNull.Value;
                    command.Parameters.Add("createdAt", OracleDbType.TimeStamp).Value = DateTime.Now;

                    await command.ExecuteNonQueryAsync();
                });
                _logger.LogInformation("Successfully inserted exception log");
            }
            catch (Exception dbEx)
            {
                // Fallback to only file/console logging if DB logging fails to avoid infinite loops
                _logger.LogCritical(dbEx, "CRITICAL: Failed to log exception to Oracle database. Original exception: {OriginalMessage}", ex.Message);
            }
        }
    }

    public async Task AddFailedMessageAsync(string messageId, string payload, string queueName, string errorMessage)
    {
        using (LogContext.PushProperty("MessageId", messageId))
        using (LogContext.PushProperty("QueueName", queueName))
        {
            _logger.LogInformation("Attempting to insert failed message for message {MessageId} to queue {QueueName}", messageId, queueName);

            var policy = ResiliencePolicies.CreateRetryPolicy(_logger, $"AddFailedMessage to DB for {messageId}");

            try
            {
                await policy.ExecuteAsync(async () =>
                {
                    using var connection = new OracleConnection(_connectionString);
                    await connection.OpenAsync();

                    using var command = connection.CreateCommand();
                    command.BindByName = true;
                    command.CommandText = "INSERT INTO failed_messages (message_id, payload, queue_name, error_message, created_at) VALUES (:messageId, :payload, :queueName, :errorMessage, :createdAt)";

                    command.Parameters.Add("messageId", OracleDbType.Varchar2).Value = messageId;
                    command.Parameters.Add("payload", OracleDbType.Clob).Value = payload;
                    command.Parameters.Add("queueName", OracleDbType.Varchar2).Value = queueName;
                    command.Parameters.Add("errorMessage", OracleDbType.Varchar2).Value = errorMessage;
                    command.Parameters.Add("createdAt", OracleDbType.TimeStamp).Value = DateTime.Now;

                    await command.ExecuteNonQueryAsync();
                });
                _logger.LogInformation("Successfully inserted failed message for message {MessageId}", messageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert failed message for message {MessageId} after retries.", messageId);
                // No further fallback for DB failure within repository, handled in publisher or caller
                throw;
            }
        }
    }
}
