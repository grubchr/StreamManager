# Stream Manager - Architecture & Implementation

**Last Updated:** December 7, 2025  
**Status:** ✅ Production Ready

## Table of Contents
1. [Overview](#overview)
2. [Engine Abstraction Design](#engine-abstraction-design)
3. [Implementation Details](#implementation-details)
4. [Component Breakdown](#component-breakdown)
5. [Database Schema](#database-schema)
6. [API Endpoints](#api-endpoints)
7. [SignalR Integration](#signalr-integration)

---

## Overview

Stream Manager is a self-service stream processing platform that supports **both ksqlDB and Apache Flink** as interchangeable stream processing engines. The application provides a unified interface for creating, managing, and monitoring stream processing queries regardless of the underlying engine.

### Key Features
- **Dual Engine Support** — Switch between ksqlDB and Flink via configuration
- **Engine Abstraction** — Single API works with both engines seamlessly
- **Real-time Streaming** — SignalR-based live query results
- **Query Validation** — Engine-specific SQL validation
- **Resource Management** — Automatic cleanup and lifecycle management

---

## Engine Abstraction Design

### Architectural Pattern
The implementation uses the **Strategy Pattern** with dependency injection to provide engine-specific behavior behind common interfaces.

```
┌─────────────────────────────────────────────┐
│          Stream Manager API Layer            │
│  (Controllers, SignalR Hubs, ViewModels)    │
└──────────────────┬──────────────────────────┘
                   │
         ┌─────────▼──────────┐
         │  IStreamEngine     │ ◄─── Abstraction Layer
         │  IQueryValidator   │
         └─────────┬──────────┘
                   │
       ┌───────────┴───────────┐
       │                       │
┌──────▼──────┐        ┌──────▼──────┐
│ KsqlEngine  │        │ FlinkEngine │
│ KsqlValidator│       │FlinkValidator│
└─────────────┘        └─────────────┘
```

### Configuration-Based Selection

**appsettings.json**
```json
{
  "StreamProcessing": {
    "Engine": "Flink",  // or "KsqlDb"
    "KsqlDb": {
      "Url": "http://ksqldb-server:8088"
    },
    "Flink": {
      "JobManagerUrl": "http://flink-jobmanager:8088",
      "KafkaBrokers": "kafka:9092"
    }
  }
}
```

**Service Registration** (`Program.cs`)
```csharp
var engineType = builder.Configuration["StreamProcessing:Engine"];

if (engineType == "Flink")
{
    builder.Services.AddScoped<IStreamEngine, FlinkEngine>();
    builder.Services.AddScoped<IQueryValidator, FlinkQueryValidator>();
}
else
{
    builder.Services.AddScoped<IStreamEngine, KsqlEngine>();
    builder.Services.AddScoped<IQueryValidator, KsqlQueryValidator>();
}
```

---

## Implementation Details

### Phase 1: Abstraction Layer ✅

Created core interfaces and DTOs:

**IStreamEngine.cs**
```csharp
public interface IStreamEngine
{
    Task<StreamDeploymentResult> DeployStreamAsync(string streamName, string sqlScript, string outputTopic);
    Task<bool> StopStreamAsync(string streamName, string queryId);
    Task<List<string>> GetActiveStreamsAsync();
    Task<IAsyncEnumerable<string>> ExecuteQueryAsync(string query, CancellationToken cancellationToken);
}
```

**IQueryValidator.cs**
```csharp
public interface IQueryValidator
{
    Task<QueryValidationResult> ValidateQueryAsync(string query);
}
```

**StreamDeploymentResult.cs**
```csharp
public class StreamDeploymentResult
{
    public bool Success { get; set; }
    public string? StreamName { get; set; }
    public string? QueryId { get; set; }
    public string? Message { get; set; }
}
```

### Phase 2: ksqlDB Implementation ✅

Migrated existing ksqlDB logic into `KsqlEngine` and `KsqlQueryValidator`:

**Key Features:**
- Pull queries for ad-hoc data retrieval
- Push queries for continuous streaming
- Query termination via `TERMINATE {queryId}`
- Stream/table introspection

**Query Types:**
- `CREATE STREAM` — Persistent queries
- `SELECT * FROM` — Push queries (continuous)
- `SELECT * FROM LIMIT` — Pull queries (one-time)

### Phase 3: Engine-Agnostic Components ✅

Updated all components to use interfaces:

**Updated Components:**
- ✅ `StreamController.cs` — Uses `IStreamEngine`
- ✅ `StreamHub.cs` (SignalR) — Real-time query execution
- ✅ `StreamsController.cs` — Web UI controller
- ✅ `StreamDefinition` model — Renamed fields

**Field Name Changes:**
- `KsqlScript` → `SqlScript`
- `KsqlQueryId` → `QueryId`
- `KsqlStreamName` → `StreamName`

**SignalR Hub Renamed:**
- `KsqlHub` → `StreamHub`

### Phase 4: Flink Implementation ✅

Implemented Apache Flink support with automatic table creation:

**FlinkEngine Key Features:**
- Automatic Kafka connector table creation
- Flink SQL Gateway API integration
- Job lifecycle management via JobManager REST API
- Streaming and batch query support

**Automatic Table Creation:**
When deploying a stream, FlinkEngine automatically creates the Kafka source tables:

```csharp
public async Task<StreamDeploymentResult> DeployStreamAsync(
    string streamName, 
    string sqlScript, 
    string outputTopic)
{
    // 1. Extract source tables from SQL
    var sourceTables = ExtractSourceTables(sqlScript);
    
    // 2. Create Kafka connector tables for each source
    foreach (var table in sourceTables)
    {
        await CreateKafkaTableIfNotExists(table);
    }
    
    // 3. Submit the stream query as INSERT INTO
    var jobId = await SubmitFlinkJob(streamName, sqlScript, outputTopic);
    
    return new StreamDeploymentResult
    {
        Success = true,
        StreamName = streamName,
        QueryId = jobId
    };
}
```

**Generated Flink SQL:**
```sql
-- Auto-created source table
CREATE TABLE IF NOT EXISTS orders (
    order_id STRING,
    customer_id STRING,
    product_name STRING,
    amount DECIMAL(10, 2),
    order_time TIMESTAMP(3),
    WATERMARK FOR order_time AS order_time - INTERVAL '5' SECOND
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'kafka:9092',
    'properties.group.id' = 'streammanager-consumer',
    'scan.startup.mode' = 'earliest-offset',
    'format' = 'json'
);

-- User's query wrapped in INSERT INTO
CREATE TABLE large_orders WITH ('connector' = 'kafka', 'topic' = 'large_orders') 
AS SELECT * FROM orders WHERE amount > 100;
```

**FlinkQueryValidator:**
- Validates Flink SQL syntax via SQL Gateway
- Checks for unsupported operations
- Returns detailed error messages

---

## Component Breakdown

### Stream Processing Engines

#### **KsqlEngine.cs**
- Connects to ksqlDB server REST API
- Executes ksql commands via `/ksql` endpoint
- Streams query results via `/query-stream` endpoint
- Terminates queries via `TERMINATE {queryId}`

**Key Methods:**
```csharp
DeployStreamAsync()     // CREATE STREAM ... AS SELECT
StopStreamAsync()       // TERMINATE {queryId}
ExecuteQueryAsync()     // SELECT queries (push/pull)
GetActiveStreamsAsync() // SHOW STREAMS; SHOW TABLES;
```

#### **FlinkEngine.cs**
- Connects to Flink SQL Gateway and JobManager
- Creates Kafka connector tables automatically
- Submits Flink SQL jobs via `/sessions/{sessionId}/statements`
- Manages job lifecycle via JobManager REST API

**Key Methods:**
```csharp
DeployStreamAsync()           // INSERT INTO ... SELECT
StopStreamAsync()             // Cancel job via JobManager
ExecuteQueryAsync()           // SELECT queries
CreateKafkaTableIfNotExists() // Auto-create source tables
```

### Query Validators

#### **KsqlQueryValidator.cs**
Validates ksqlDB syntax and semantics:
- Checks for required CREATE STREAM wrapper
- Validates EMIT CHANGES for continuous queries
- Ensures proper aggregation usage
- Validates window functions

#### **FlinkQueryValidator.cs**
Validates Flink SQL syntax:
- Uses Flink SQL Gateway validation endpoint
- Checks for unsupported keywords (`EMIT CHANGES`)
- Validates table references
- Returns descriptive error messages

### Controllers

#### **StreamController.cs** (API)
RESTful API for stream management:
- `POST /api/stream` — Create and deploy a stream
- `DELETE /api/stream/{name}` — Stop and delete a stream
- `GET /api/stream` — List all streams
- `GET /api/stream/active` — Get active streams from engine

#### **StreamsController.cs** (Web UI)
MVC controller for browser interface:
- `GET /streams` — View all streams
- `GET /streams/create` — Create stream form
- `POST /streams/create` — Submit new stream

### SignalR Hub

#### **StreamHub.cs**
Real-time streaming query results:
- `ExecuteQuery(string query)` — Start streaming results
- `StopQuery()` — Cancel active query
- Broadcasts results via `ReceiveQueryResult` event

**Client Usage:**
```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/streamHub")
    .build();

connection.on("ReceiveQueryResult", (data) => {
    console.log("Result:", data);
});

await connection.start();
await connection.invoke("ExecuteQuery", "SELECT * FROM orders EMIT CHANGES");
```

---

## Database Schema

### StreamDefinition Table

```csharp
public class StreamDefinition
{
    public int Id { get; set; }
    public string Name { get; set; }           // Stream name
    public string SqlScript { get; set; }      // SELECT statement
    public string? StreamName { get; set; }    // Engine-assigned name
    public string? QueryId { get; set; }       // Engine query/job ID
    public string? OutputTopic { get; set; }   // Kafka output topic
    public bool IsActive { get; set; }         // Deployment status
    public DateTime CreatedAt { get; set; }
}
```

**Field Purpose:**
- `Name` — User-friendly identifier
- `SqlScript` — The actual SQL query (engine-agnostic)
- `StreamName` — Created stream/table name in engine
- `QueryId` — ksqlDB query ID or Flink job ID
- `OutputTopic` — Where results are written
- `IsActive` — Whether currently deployed

---

## API Endpoints

### Stream Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/stream` | Deploy a new stream |
| DELETE | `/api/stream/{name}` | Stop and delete stream |
| GET | `/api/stream` | List all streams |
| GET | `/api/stream/active` | Get active streams from engine |

### Request/Response Examples

**Create Stream:**
```json
POST /api/stream
{
  "name": "high_value_orders",
  "sqlScript": "SELECT * FROM orders WHERE amount > 1000",
  "outputTopic": "high_value_orders"
}

Response:
{
  "success": true,
  "streamName": "high_value_orders",
  "queryId": "flink-job-abc123",
  "message": "Stream deployed successfully"
}
```

**Delete Stream:**
```json
DELETE /api/stream/high_value_orders

Response: 200 OK
```

---

## SignalR Integration

### Hub Connection

**Server-Side (StreamHub.cs):**
```csharp
public async Task ExecuteQuery(string query)
{
    var cancellationToken = new CancellationTokenSource();
    Context.Items["CancellationToken"] = cancellationToken;
    
    await foreach (var result in _streamEngine.ExecuteQueryAsync(query, cancellationToken.Token))
    {
        await Clients.Caller.SendAsync("ReceiveQueryResult", result);
    }
}

public Task StopQuery()
{
    if (Context.Items["CancellationToken"] is CancellationTokenSource cts)
    {
        cts.Cancel();
    }
    return Task.CompletedTask;
}
```

**Client-Side (JavaScript):**
```javascript
// Connect
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/streamHub")
    .configureLogging(signalR.LogLevel.Information)
    .build();

// Handle incoming results
connection.on("ReceiveQueryResult", (data) => {
    const result = JSON.parse(data);
    displayResult(result);
});

// Start query
await connection.start();
await connection.invoke("ExecuteQuery", sqlQuery);

// Stop query
await connection.invoke("StopQuery");
```

---

## Deployment Models

### Development (Single Node)
```yaml
docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up
```

Components:
- Kafka
- Flink JobManager + TaskManager
- PostgreSQL
- Stream Manager API + Web UI

### Production (Multi-Node)
Separate deployment:
- **Kafka Cluster** — 3+ brokers with replication
- **Flink Cluster** — Scalable JobManager + TaskManagers
- **Stream Manager** — Load balanced API instances
- **PostgreSQL** — HA database cluster

---

## Summary

The Stream Manager architecture provides:
- ✅ **Flexibility** — Switch engines via configuration
- ✅ **Extensibility** — Easy to add new engines
- ✅ **Consistency** — Unified API regardless of engine
- ✅ **Real-time** — SignalR streaming results
- ✅ **Reliability** — Proper lifecycle management
- ✅ **Developer-Friendly** — Clear abstractions and patterns

The implementation is production-ready and supports both ksqlDB and Apache Flink with seamless switching via configuration.
