# RabbitSchemaApi

A .NET 10 Web API that **validates JSON payloads against OpenAPI/JSON Schema definitions** and
**publishes conforming messages to RabbitMQ**. Non-conforming payloads are rejected with detailed
error paths before they ever touch the broker.

```
HTTP POST /api/messages/{schemaName}
         │
         ▼
  ┌──────────────┐    invalid    ┌─────────────────────────┐
  │ JSON Schema  │ ────────────▶ │ 422 + error list        │
  │ Validation   │               │ (path + keyword + msg)  │
  └──────┬───────┘               └─────────────────────────┘
         │ valid
         ▼
  ┌──────────────┐               ┌─────────────────────────┐
  │  RabbitMQ    │ ────────────▶ │ 202 + PublishReceipt    │
  │  Publisher   │               │ (messageId, queue, ts)  │
  └──────────────┘               └─────────────────────────┘
```

---

## Tech Stack

| Concern | Library |
|---|---|
| Web framework | ASP.NET Core 10 (controllers) |
| JSON Schema validation | `JsonSchema.Net` (Draft 2020-12) |
| OpenAPI document | `Microsoft.AspNetCore.OpenApi` (built-in, .NET 10) |
| OpenAPI UI | `Scalar.AspNetCore` |
| Message broker client | `RabbitMQ.Client` v7 (async-first) |
| Logging | `Serilog.AspNetCore` |
| Tests | xUnit + NSubstitute + FluentAssertions |

---

## Project Structure

```
RabbitSchemaApi/
├── Controllers/
│   └── MessagesController.cs      # 3 endpoints: publish, validate-only, list schemas
├── Middleware/
│   └── GlobalExceptionMiddleware.cs
├── Models/
│   └── Models.cs                  # ApiResponse<T>, ValidationError, PublishReceipt, …
├── Schemas/
│   └── order.schema.json          # Example JSON Schema (Draft 2020-12)
├── Services/
│   ├── SchemaValidationService.cs # Loads + caches schemas, validates JsonNode
│   └── RabbitMqPublisher.cs       # Singleton connection, async publish, DLX support
├── Program.cs                     # Composition root — DI, middleware, OpenAPI, Serilog
├── appsettings.json
├── appsettings.Development.json
├── docker-compose.yml             # RabbitMQ + Management UI
└── rabbitmq-definitions.json      # Pre-configures DLX and dead-letter queue

RabbitSchemaApi.Tests/
└── Tests.cs                       # Unit tests for validator + controller
```

---

## Quick Start

### 1. Start RabbitMQ

```bash
docker compose up -d
# Management UI → http://localhost:15672  (guest / guest)
```

### 2. Run the API

```bash
cd RabbitSchemaApi
dotnet run
# API         → https://localhost:7xxx
# Scalar UI   → https://localhost:7xxx/scalar/v1
# OpenAPI doc → https://localhost:7xxx/openapi/v1.json
# Health      → https://localhost:7xxx/health
```

### 3. Publish a valid order

```bash
curl -X POST https://localhost:7xxx/api/messages/order \
  -H "Content-Type: application/json" \
  -d '{
    "orderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "customerId": "cust-001",
    "orderDate": "2024-06-15T10:30:00Z",
    "items": [
      { "productId": "WIDGET-42", "quantity": 3, "unitPrice": 19.99 }
    ],
    "totalAmount": 59.97,
    "currency": "USD"
  }'
```

**202 Response:**
```json
{
  "success": true,
  "message": "Payload validated and published successfully.",
  "data": {
    "messageId": "d290f1ee-6c54-4b01-90e6-d701748f0851",
    "queue": "order",
    "exchange": "",
    "publishedAt": "2024-06-15T10:31:00+00:00"
  }
}
```

### 4. Submit an invalid payload

```bash
curl -X POST https://localhost:7xxx/api/messages/order \
  -H "Content-Type: application/json" \
  -d '{ "orderId": "not-a-uuid", "totalAmount": -1 }'
```

**422 Response:**
```json
{
  "success": false,
  "message": "Payload does not conform to schema 'order'.",
  "errors": [
    "[$] Required property 'customerId' is missing.",
    "[$] Required property 'items' is missing.",
    "[$] Required property 'currency' is missing.",
    "[$.totalAmount] Value is less than or equal to 0."
  ]
}
```

### 5. Dry-run validation (no publish)

```bash
curl -X POST https://localhost:7xxx/api/messages/order/validate \
  -H "Content-Type: application/json" \
  -d '{ ... }'
```

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/messages/{schemaName}` | Validate + publish to RabbitMQ |
| `POST` | `/api/messages/{schemaName}/validate` | Validate only (dry-run, no publish) |
| `GET`  | `/api/messages/schemas` | List all registered schema names |
| `GET`  | `/health` | Health check |
| `GET`  | `/scalar/v1` | Interactive API documentation (dev only) |
| `GET`  | `/openapi/v1.json` | Raw OpenAPI document (dev only) |

---

## Adding a New Schema

1. Create the JSON Schema file in `Schemas/`, e.g. `Schemas/shipment.schema.json`
2. Register it in `appsettings.json`:

```json
"SchemaRegistry": [
  { "Name": "order",    "FilePath": "Schemas/order.schema.json",    "Version": "1.0.0" },
  { "Name": "shipment", "FilePath": "Schemas/shipment.schema.json", "Version": "1.0.0" }
]
```

3. Restart the API. The new schema is available immediately at `POST /api/messages/shipment`.

No code changes required.

---

## Publishing to a Custom Queue

Use the optional `?queueName=` query parameter to override the default routing
(which uses the schema name as the queue name):

```bash
POST /api/messages/order?queueName=orders.priority
```

---

## Configuration Reference

```json
{
  "RabbitMQ": {
    "HostName": "localhost",       // broker hostname or IP
    "Port": 5672,                  // AMQP port
    "UserName": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "ExchangeName": "",            // "" = default AMQP exchange
    "DeadLetterExchange": "dlx"    // unprocessable messages land here
  }
}
```

For production, override `Password` with a secret manager or environment variable:

```bash
export RabbitMQ__Password="s3cr3t"
dotnet run
```

---

## Running Tests

```bash
dotnet test RabbitSchemaApi.Tests
```

Tests use NSubstitute to mock `IRabbitMqPublisher` and `ISchemaValidationService`,
and test against the real `order.schema.json` file.

---

## Design Decisions

**Why `JsonSchema.Net` instead of `NJsonSchema`?**  
`JsonSchema.Net` by Greg Dennis is the most actively maintained .NET implementation and
supports JSON Schema Draft 2020-12 natively. It works directly with `System.Text.Json`'s
`JsonNode` — no Newtonsoft.Json dependency.

**Why a Singleton RabbitMQ connection?**  
The RabbitMQ .NET client documentation explicitly recommends one long-lived connection
per process. Channels are lightweight and can be created per-operation. The Singleton
registration ensures the TCP connection is reused and `AutomaticRecovery` handles
transient network failures without restarting the app.

**Why 422 for schema failures instead of 400?**  
HTTP 400 (Bad Request) means the server cannot understand the request syntax.
HTTP 422 (Unprocessable Entity) means the syntax is valid but the content has semantic
errors — which is exactly what a schema violation is. This distinction makes client-side
error handling cleaner.
