# Stream Manager Prototype - Project Plan

## 1. Project Overview

This project is a prototype for **Stream Manager**, a self-service ksqlDB platform. It decouples the management of stream processing from the execution, allowing users to define, manage, and visualize ksqlDB queries via a web interface.

### Architecture Overview

- **Frontend:** ASP.NET Core MVC (Razor Views) — UI dashboard
- **Backend:** ASP.NET Core Web API — Control plane and data plane
- **Communication:**
  - **REST (Frontend → Backend):** CRUD operations on query definitions (persistent queries)
  - **SignalR (Browser → Backend):** Ad-hoc query streaming and previewing persistent topics
- **Data store:** PostgreSQL (stores metadata about queries, owners, and status)
- **Engine:** ksqlDB (Interactive Mode)

---

## 2. Infrastructure (Docker)

We will use Docker Compose to spin up the entire ecosystem.

### Critical configuration: Interactive Mode

To support ad-hoc queries via the REST API, the ksqlDB engine must be deployed in **Interactive Mode** (not Headless).

- **Image:** `confluentinc/cp-ksqldb-server:7.6.0`
- **Port:** expose `8088` for the Web API to connect
- **Service ID:** must be unique (e.g., `stream_manager_`) to prevent conflicts with other ksqlDB clusters
- **Listeners:** `KSQL_LISTENERS` must bind to `0.0.0.0` (HTTP or HTTPS) so the Web API container can reach it
- **Kafka:** single node running in KRaft mode (no ZooKeeper)
- **Schema Registry:** for Avro/Protobuf support
- **PostgreSQL:** version 15+ for storing application state

---

## 3. Application 1: The Web API (Backend)

Core logic engine built on .NET 10.3.1.

### 3.1 Ad-Hoc Query Service (`AdHocKsqlService`)

For the Query view, columns are unknown until runtime, so use a pass-through architecture: use `HttpClient` to stream raw JSON from ksqlDB to SignalR.

**Implementation strategy**

- **Protocol:** use HTTP/2 for streaming performance
- **Endpoint:** POST to `/query` (for `SELECT` statements)
- **Payload example:**

```json
{
  "ksql": "SELECT * FROM MOVIES EMIT CHANGES;",
  "streamsProperties": { "ksql.streams.auto.offset.reset": "earliest" }
}
```

- **Streaming logic (C#):**
  - Use `HttpCompletionOption.ResponseHeadersRead`
  - Do not await the entire response
  - Read the response stream line-by-line using `StreamReader`
  - `yield return` each JSON line immediately to the SignalR hub (use `IAsyncEnumerable<string>`)

### 3.2 SignalR Hub (`KsqlHub`)

The Hub bridges the Razor frontend, external clients, and backend services.

- **Method:** `ExecuteAdHoc(string ksql, CancellationToken ct)`
  - Validate the string starts with `SELECT`
  - Append `EMIT CHANGES` if missing
  - Call `AdHocKsqlService.ExecuteQueryStreamAsync` and stream results to the caller using `IAsyncEnumerable<string>`
  - Support cancellation: when the user clicks Stop or closes the tab, the `CancellationToken` triggers and the HTTP stream to ksqlDB is closed

- **Method:** `JoinStreamGroup(string topicName)`
  - Add `Context.ConnectionId` to a SignalR group named after the topic
  - Used by the Razor UI (previewing) and external client apps (consuming live data)

### 3.3 Persistent Query Management (`StreamController`)

Manages lifecycle of long-running queries. Prefer using `ksqlDB.RestApi.Client` for DDL operations.

- **Deploy logic**
  - Input: `SELECT ...`
  - Action: wrap SQL in `CREATE STREAM [SafeName] AS SELECT ...`
  - Storage: save returned `QueryID` and `OutputTopicName` to Postgres

- **Stop logic**
  - Input: `ViewName`
  - Action: look up `QueryID` in Postgres → execute `TERMINATE [QueryID]`

- **Update logic**
  - ksqlDB does not support `ALTER`
  - Action: `TERMINATE` → `DROP STREAM` → `CREATE STREAM` (new SQL)

### 3.4 Background Consumer (`TopicProxyService`)

Allows users to preview results of persistent queries.

- Runs as a `BackgroundService`
- Periodically scans Postgres for active streams
- Dynamically creates `Confluent.Kafka.IConsumer` instances for output topics
- Consumes messages and uses `IHubContext` to broadcast them to the SignalR group for the topic

---

## 4. Application 2: The Razor Web App (Frontend)

Built on .NET 10 MVC — the UI layer.

### 4.1 Query View (`/Query`)

Designed for rapid prototyping and debugging ad-hoc queries.

**User workflow:**

- **Input:** user types a SQL query (e.g., `SELECT * FROM PAYMENTS WHERE AMOUNT > 500`) into the code editor
- **Start:** user clicks *Run Query*
- **Streaming:** UI clears the previous results table. The SignalR stream opens and rows appear in the table in real time as they arrive from ksqlDB
- **Parsing:**
  - The first message received will be the header: `{"header": {"schema": "..."}}`
  - Parse the schema string (comma-separated) to generate the HTML table headers (use `<thead>`)
  - Subsequent messages are data: `{"row": {"columns": [...]}}`
  - Append these to the HTML table body (use `<tbody>`)
- **Stop:** user clicks *Stop Stream* or navigates away — this sends a cancellation signal to SignalR, which terminates the backend ksqlDB request


### 4.2 Streams View (`/Streams`)

Control plane for long-running ETL jobs.

**User workflow:**

- **Create:** user clicks *New Stream*, provides a Name (e.g., `HighValueOrders`) and the KSQL `SELECT` statement
- **Save & Deploy:** clicking *Save* stores the definition in Postgres. Clicking *Deploy* sends the command to the Web API to start the persistent query on the ksqlDB engine
- **Status:** dashboard shows the status (`Running` / `Stopped`) based on the ksqlDB engine state

**Edit / Update lifecycle:**

- User edits a running stream and clicks *Update*
- UI warns the user this will restart the stream
- Backend terminates the old query, drops the old stream, and creates a new one with the updated logic

**Consumption (external apps):**

- Dashboard displays the topic name (e.g., `view_HighValueOrders_GUID`)
- External .NET apps connect to `KsqlHub` via SignalR and call `JoinStreamGroup("view_HighValueOrders_GUID")` to receive the live data stream


### 4.3 Live Preview View (`/Stream/{id}`)

- Looks up the topic name
- Connects to SignalR and invokes `JoinStreamGroup(topicName)`
- Backend `TopicProxyService` pushes data to this view

---

## 5. Database Schema (Postgres)

**Table:** `StreamDefinitions`

- `Id` (PK, Guid)
- `Name` (string) — friendly name (e.g., `FraudFilter`)
- `KsqlScript` (text) — the raw `SELECT` statement
- `KsqlQueryId` (string) — the ID returned by ksqlDB (e.g., `CSAS_FRAUD_01`)
- `OutputTopic` (string) — the Kafka topic created by this stream
- `IsActive` (bool)
- `CreatedAt` (DateTime)

---

## 6. Implementation Steps

### Setup Docker Compose

- Ensure ksqldb-server environment variables are set for Interactive Mode
- Ensure Kafka is in KRaft mode

### Build API core

Run:
```bash
dotnet new webapi -n StreamManager.Api
```

- Setup EF Core 10.0 with Postgres
- Implement `AdHocKsqlService` using `HttpClient`
- Implement SignalR: create `KsqlHub` and wire up ad-hoc streaming
- Implement persistent logic: create `StreamController`
- Implement `TopicProxyService` for consuming persistent topics

### Build Razor UI

Run:
```bash
dotnet new mvc -n StreamManager.Web
```

- Create the Query View (`/Query`) with header/row parsing logic
- Create the Streams View (`/Streams`) for managing persistent queries

---

### Notes & next steps

- Consider adding a short `docker-compose.yml` example and minimal SQL migration for `StreamDefinitions` as a follow-up.
- If you want, I can also add a simple `docker-compose.yml` and example `.env` showing the ksqlDB/Kafka/Postgres configuration for Interactive Mode.

