using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Mbr.Api.Workflow.FinalizedBill.Controllers;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Services;
using Mbr.Api.Workflow.FinalizedBill.Repositories;
using Mbr.Api.Workflow.FinalizedBill.BackgroundServices;
using Xunit;

namespace Mbr.Api.Workflow.FinalizedBill.Tests;

// ── SchemaValidationService Tests ─────────────────────────────────────────────

public class SchemaValidationServiceTests
{
    // Builds a SchemaValidationService wired to the real order.schema.json
    private static SchemaValidationService BuildSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The key format for a list in AddInMemoryCollection
                ["SchemaRegistry:0:Name"]     = "order",
                ["SchemaRegistry:0:FilePath"] = "Schemas/order.schema.json",
                ["SchemaRegistry:0:Version"]  = "1.0.0"
            })
            .Build();

        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(AppContext.BaseDirectory);

        var repo = new SchemaRepository(config, env, NullLogger<SchemaRepository>.Instance);

        return new SchemaValidationService(repo, NullLogger<SchemaValidationService>.Instance);
    }

    [Fact]
    public async Task ValidateAsync_WithValidPayload_ReturnsValid()
    {
        var sut = BuildSut();
        var payload = JsonNode.Parse(ValidOrderJson())!;

        var result = await sut.ValidateAsync("order", payload);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_MissingRequiredField_ReturnsInvalid()
    {
        var sut = BuildSut();
        // Remove required "currency" field
        var payload = JsonNode.Parse("""
            {
              "orderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
              "customerId": "cust-001",
              "items": [{ "productId": "prod-1", "quantity": 1, "unitPrice": 10.00 }],
              "totalAmount": 10.00
            }
        """)!;

        var result = await sut.ValidateAsync("order", payload);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("currency", StringComparison.OrdinalIgnoreCase)
                                         || e.SchemaKeyword == "required");
    }

    [Fact]
    public async Task ValidateAsync_NegativeQuantity_ReturnsInvalid()
    {
        var sut = BuildSut();
        var payload = JsonNode.Parse("""
            {
              "orderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
              "customerId": "cust-001",
              "items": [{ "productId": "prod-1", "quantity": -5, "unitPrice": 10.00 }],
              "totalAmount": -50.00,
              "currency": "USD"
            }
        """)!;

        var result = await sut.ValidateAsync("order", payload);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_UnknownSchema_ReturnsClearError()
    {
        var sut = BuildSut();
        var result = await sut.ValidateAsync("does-not-exist", JsonNode.Parse("{}")!);

        Assert.False(result.IsValid);
        var singleError = Assert.Single(result.Errors);
        Assert.Contains("does-not-exist", singleError.Message);
    }

    [Fact]
    public async Task ValidateAsync_InvalidCurrencyEnum_ReturnsInvalid()
    {
        var sut = BuildSut();
        var json = ValidOrderJson().Replace("\"USD\"", "\"XYZ\"");
        var payload = JsonNode.Parse(json)!;

        var result = await sut.ValidateAsync("order", payload);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RegisteredSchemas_ContainsOrder()
    {
        var sut = BuildSut();
        Assert.Contains("order", sut.RegisteredSchemas);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ValidOrderJson() => """
        {
          "orderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
          "customerId": "cust-001",
          "orderDate": "2024-06-15T10:30:00Z",
          "items": [
            {
              "productId": "PROD-WIDGET-42",
              "productName": "Super Widget",
              "quantity": 3,
              "unitPrice": 19.99,
              "discount": 10
            }
          ],
          "totalAmount": 53.97,
          "currency": "USD",
          "shippingAddress": {
            "street": "123 Main St",
            "city": "Springfield",
            "state": "IL",
            "zip": "62701",
            "country": "US"
          }
        }
    """;
}

// ── MessagesController Tests ──────────────────────────────────────────────────

public class MessagesControllerTests
{
    private readonly ISchemaValidationService _validator = Substitute.For<ISchemaValidationService>();
    private readonly IRabbitMqPublisher _publisher       = Substitute.For<IRabbitMqPublisher>();
    private readonly IBackgroundTaskQueue _taskQueue     = Substitute.For<IBackgroundTaskQueue>();
    private readonly IExternalApiService _externalApi    = Substitute.For<IExternalApiService>();

    private MessagesController CreateController()
    {
        var logger = NullLogger<MessagesController>.Instance;
        var controller = new MessagesController(_validator, _publisher, _taskQueue, _externalApi, logger);

        // Set up a fake HttpContext
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task PublishMessage_UnknownSchema_Returns404()
    {
        _validator.RegisteredSchemas.Returns(new List<string> { "order" }.AsReadOnly());

        var controller = CreateController();
        var payload = new JsonObject();
        var result = await controller.PublishMessage("invoice", payload, null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task PublishMessage_InvalidPayload_Returns422()
    {
        _validator.RegisteredSchemas.Returns(new List<string> { "order" }.AsReadOnly());
        _validator
            .ValidateAsync("order", Arg.Any<JsonNode>(), Arg.Any<CancellationToken>())
            .Returns(SchemaValidationResult.Invalid(
                [new ValidationError("$.currency", "Required property 'currency' is missing.")]));

        var controller = CreateController();
        var payload = new JsonObject { ["orderId"] = "abc" };
        var result = await controller.PublishMessage("order", payload, null, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task PublishMessage_ValidPayload_Returns202AndPublishes()
    {
        _validator.RegisteredSchemas.Returns(new List<string> { "order" }.AsReadOnly());
        _validator
            .ValidateAsync("order", Arg.Any<JsonNode>(), Arg.Any<CancellationToken>())
            .Returns(SchemaValidationResult.Valid());

        var receipt = new PublishReceipt
        {
            MessageId   = Guid.NewGuid().ToString(),
            Queue       = "order",
            Exchange    = "",
            PublishedAt = DateTimeOffset.UtcNow
        };
        _publisher
            .PublishAsync("order", Arg.Any<JsonNode>(), Arg.Any<IDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(receipt);

        var controller = CreateController();
        var payload = new JsonObject { ["orderId"] = "3fa85f64-5717-4562-b3fc-2c963f66afa6" };
        var result = await controller.PublishMessage("order", payload, null, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        await _publisher.Received(1).PublishAsync(
            "order",
            Arg.Any<JsonNode>(),
            Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());

        await _taskQueue.Received(2).EnqueueAsync(Arg.Any<BackgroundTask>());
    }

    [Fact]
    public async Task GetSchemas_ReturnsAllRegisteredSchemas()
    {
        var schemas = new List<string> { "order", "shipment" }.AsReadOnly();
        _validator.RegisteredSchemas.Returns(schemas);

        var controller = CreateController();
        var result = controller.GetSchemas();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }
}
