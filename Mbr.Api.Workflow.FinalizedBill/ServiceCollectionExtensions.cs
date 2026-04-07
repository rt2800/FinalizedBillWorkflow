using System.Text;
using EasyNetQ;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Mbr.Api.Workflow.FinalizedBill.BackgroundServices;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Repositories;
using Mbr.Api.Workflow.FinalizedBill.Services;

namespace Mbr.Api.Workflow.FinalizedBill;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqSettings>(configuration.GetSection("RabbitMQ"));
        services.Configure<SftpSettings>(configuration.GetSection("Sftp"));

        // JWT Authentication setup
        var jwtKey = configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing from configuration.");
        var jwtIssuer = configuration["Jwt:Issuer"] ?? "Mbr.Api.Workflow.FinalizedBill";
        var jwtAudience = configuration["Jwt:Audience"] ?? "Mbr.Api.Workflow.FinalizedBillUsers";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
            };
        });

        services.AddAuthorization();

        services.AddSingleton<ISchemaRepository, SchemaRepository>();

        var rabbitSettings = configuration.GetSection("RabbitMQ").Get<RabbitMqSettings>();
        if (rabbitSettings != null)
        {
            var connectionString = $"host={rabbitSettings.HostName};port={rabbitSettings.Port};username={rabbitSettings.UserName};password={rabbitSettings.Password};virtualHost={rabbitSettings.VirtualHost}";
            RabbitHutch.AddEasyNetQ(services, connectionString);
        }

        services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

        services.AddScoped<IFinalizedBillRepository, FinalizedBillRepository>();

        return services;
    }

    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        services.AddSingleton<ISchemaValidationService, SchemaValidationService>();
        services.AddSingleton<ISftpService, SftpService>();

        // Background Processing
        services.AddSingleton<IBackgroundTaskQueue, PersistentBackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskProcessor>();

        return services;
    }

    public static IServiceCollection AddSwaggerSupport(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Components ??= new OpenApiComponents();
                if (doc.Components.SecuritySchemes != null)
                {
                    doc.Components.SecuritySchemes.Add("Bearer", new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        In = ParameterLocation.Header,
                        Description = "JWT Authorization header using the Bearer scheme."
                    });
                }

                if (doc.SecurityRequirements != null)
                {
                    doc.SecurityRequirements.Add(new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
                                }
                            },
                            Array.Empty<string>()
                        }
                    });
                }

                if (doc.Info != null)
                {
                    doc.Info.Title = "Mbr.Api.Workflow.FinalizedBill API";
                    doc.Info.Version = "v1";
                    doc.Info.Description =
                        "Accepts JSON payloads, validates them against registered OpenAPI/JSON Schema " +
                        "definitions, and publishes conforming messages to RabbitMQ.";
                    doc.Info.Contact = new() { Name = "Platform Team", Email = "platform@example.com" };
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }
}
