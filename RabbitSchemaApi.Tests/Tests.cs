using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitSchemaApi.Controllers;
using RabbitSchemaApi.Models;
using RabbitSchemaApi.Services;
using RabbitSchemaApi.Repositories;
using Xunit;

namespace RabbitSchemaApi.Tests;

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

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
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

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("currency", StringComparison.OrdinalIgnoreCase)
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

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_UnknownSchema_ReturnsClearError()
    {
        var sut = BuildSut();
        var result = await sut.ValidateAsync("does-not-exist", JsonNode.Parse("{}")!);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("does-not-exist"));
    }

    [Fact]
    public async Task ValidateAsync_InvalidCurrencyEnum_ReturnsInvalid()
    {
        var sut = BuildSut();
        var json = ValidOrderJson().Replace("\"USD\"", "\"XYZ\"");
        var payload = JsonNode.Parse(json)!;

        var result = await sut.ValidateAsync("order", payload);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void RegisteredSchemas_ContainsOrder()
    {
        var sut = BuildSut();
        sut.RegisteredSchemas.Should().Contain("order", because: "it is defined in SchemaRegistry config");
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
    private readonly IFinalizedBillRepository _repository = Substitute.For<IFinalizedBillRepository>();

    private MessagesController CreateController(string bodyJson)
    {
        var logger = NullLogger<MessagesController>.Instance;
        var controller = new MessagesController(_validator, _publisher, _repository, logger);

        // Set up a fake HttpContext with the provided JSON body
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body        = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bodyJson));
        httpContext.Request.ContentType = "application/json";
        controller.ControllerContext    = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task PublishMessage_UnknownSchema_Returns404()
    {
        _validator.RegisteredSchemas.Returns(new List<string> { "order" }.AsReadOnly());

        var controller = CreateController("{}");
        var result = await controller.PublishMessage("invoice", null, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task PublishMessage_InvalidPayload_Returns422()
    {
        _validator.RegisteredSchemas.Returns(new List<string> { "order" }.AsReadOnly());
        _validator
            .ValidateAsync("order", Arg.Any<JsonNode>(), Arg.Any<CancellationToken>())
            .Returns(SchemaValidationResult.Invalid(
                [new ValidationError("$.currency", "Required property 'currency' is missing.")]));

        var controller = CreateController("""{"orderId":"abc"}""");
        var result = await controller.PublishMessage("order", null, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
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
            .PublishAsync("order", Arg.Any<object>(), Arg.Any<IDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(receipt);

        var controller = CreateController("""{"orderId":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}""");
        var result = await controller.PublishMessage("order", null, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        await _publisher.Received(1).PublishAsync(
            "order",
            Arg.Any<object>(),
            Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemas_ReturnsAllRegisteredSchemas()
    {
        var schemas = new List<string> { "order", "shipment" }.AsReadOnly();
        _validator.RegisteredSchemas.Returns(schemas);

        var controller = CreateController("{}");
        var result = controller.GetSchemas();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Should().NotBeNull();
    }
}
