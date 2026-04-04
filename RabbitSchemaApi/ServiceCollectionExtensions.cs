using Microsoft.Extensions.DependencyInjection;
using RabbitSchemaApi.Models;
using RabbitSchemaApi.Repositories;
using RabbitSchemaApi.Services;

namespace RabbitSchemaApi;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqSettings>(configuration.GetSection("RabbitMQ"));

        services.AddSingleton<ISchemaRepository, SchemaRepository>();
        services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

        services.AddScoped<IFinalizedBillRepository, FinalizedBillRepository>();

        return services;
    }

    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        services.AddSingleton<ISchemaValidationService, SchemaValidationService>();

        return services;
    }

    public static IServiceCollection AddSwaggerSupport(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info.Title       = "RabbitSchema API";
                doc.Info.Version     = "v1";
                doc.Info.Description =
                    "Accepts JSON payloads, validates them against registered OpenAPI/JSON Schema " +
                    "definitions, and publishes conforming messages to RabbitMQ.";
                doc.Info.Contact = new() { Name = "Platform Team", Email = "platform@example.com" };
                return Task.CompletedTask;
            });
        });

        return services;
    }
}
