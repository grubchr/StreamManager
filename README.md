# Stream Manager

A self-service stream processing platform that decouples stream processing management from execution, allowing users to define, manage, and visualize streaming queries via a web interface.

**Now supports both ksqlDB and Apache Flink!** Choose your engine via configuration.

## Architecture

- **Frontend:** ASP.NET Core MVC (Razor Views) — UI dashboard
- **Backend:** ASP.NET Core Web API — Control plane and data plane
- **Database:** PostgreSQL — Stores metadata about queries and status
- **Stream Engine:** Configurable — Choose between ksqlDB or Apache Flink
- **Communication:** REST APIs + SignalR for real-time streaming

## Supported Stream Processing Engines

### ksqlDB (Confluent)

- **Best for:** Simple setup, smaller scale (fewer than 50 queries)
- **Pros:** Easy to configure, proven stability
- **Query Syntax:** ksqlDB SQL with `EMIT CHANGES`

### Apache Flink

- **Best for:** Large scale (more than 100 queries), advanced features
- **Pros:** Highly scalable, complex event processing, mature ecosystem
- **Query Syntax:** Standard Flink SQL (no `EMIT CHANGES`)

## Projects

### StreamManager.Api

The backend Web API that provides:

- **Stream Query Engine:** Abstracted interface supporting multiple engines (ksqlDB, Flink)
- **Ad-hoc Query Service:** Streams SQL query results via SignalR
- **Stream Management:** CRUD operations for persistent stream definitions
- **Query Validation:** Engine-specific SQL syntax validation
- **Topic Proxy Service:** Background service that consumes Kafka topics and broadcasts to SignalR groups
- **SignalR Hub:** Real-time communication for query streaming and topic preview

### StreamManager.Web

The frontend MVC web application that provides:

- **Query Tool:** Execute ad-hoc SQL queries with real-time streaming results
- **Stream Management:** Create, deploy, stop, and delete persistent streams
- **Live Preview:** View real-time data from deployed streams
- **Engine-Agnostic UI:** Works seamlessly with both ksqlDB and Flink

## Features

### Ad-Hoc Queries (/Query)

- CodeMirror-based SQL editor with syntax highlighting
- Real-time streaming of query results via SignalR
- Dynamic table generation based on a query schema
- Start/stop a query execution with cancellation support

### Stream Management (/Streams)

- Create persistent stream definitions
- Deploy streams to ksqlDB (CREATE STREAM AS SELECT)
- Stop running streams (TERMINATE a query)
- Delete stream definitions
- View stream status and metadata

### Live Preview (/Stream/{id})

- Real-time preview of data from deployed streams
- SignalR-based consumption of Kafka topics
- Connection status monitoring
- Message history with a scrollable view

## Database Schema

**StreamDefinitions Table:**

- `Id` (Guid) — Primary key
- `Name` (string) — Friendly stream name
- `SqlScript` (text) — The SQL SELECT statement (engine-agnostic)
- `JobId` (string) — Stream engine job identifier (ksqlDB query ID or Flink job ID)
- `StreamName` (string) — Generated stream/table name in the engine
- `OutputTopic` (string) — Kafka output topic name
- `IsActive` (bool) — Stream deployment status
- `CreatedAt` (DateTime) — Creation timestamp

## Getting Started

### Option 1: Run with ksqlDB

1. **Start Infrastructure:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
   ```

2. **Configure Engine (if needed):**
   
   Edit the `StreamManager.Api/appsettings.json` file:
   ```json
   {
     "StreamEngine": {
       "Provider": "KsqlDb",
       "KsqlDb": {
         "Url": "http://localhost:8088"
       }
     }
   }
   ```

3. **Run Database Migrations:**
   ```bash
   cd StreamManager.Api
   dotnet ef database update
   ```

4. **Start API Server:**
   ```bash
   cd StreamManager.Api
   dotnet run
   ```
   API will be available at: https://localhost:7068

5. **Start Web Application:**
   ```bash
   cd StreamManager.Web  
   dotnet run
   ```
   Web UI will be available at: https://localhost:7122

---

### Option 2: Run with Apache Flink

1. **Start Infrastructure:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
   ```
   
   **⏳ Wait 60 seconds** for Flink to fully initialize before proceeding.

2. **Configure Engine:**
   
   Edit `StreamManager.Api/appsettings.json` file:
   ```json
   {
     "StreamEngine": {
       "Provider": "Flink",
       "Flink": {
         "SqlGatewayUrl": "http://localhost:8082",
         "RestApiUrl": "http://localhost:8088",
         "KafkaBootstrapServers": "localhost:9092",
         "KafkaFormat": "json"
       }
     }
   }
   ```

3. **Run Database Migrations:**
   ```bash
   cd StreamManager.Api
   dotnet ef database update
   ```

4. **Start API Server:**
   ```bash
   cd StreamManager.Api
   dotnet run
   ```
   API will be available at: https://localhost:7068

5. **Start Web Application:**
   ```bash
   cd StreamManager.Web  
   dotnet run
   ```
   Web UI will be available at: https://localhost:7122

6. **Verify Flink is Running:**
   ```bash
   # Check Flink JobManager
   curl http://localhost:8088/overview
   
   # Check Flink SQL Gateway
   curl http://localhost:8082/v1/info
   
   # Access Flink Web UI
   open http://localhost:8088
   ```

---

## Infrastructure Requirements

### Shared Services (Both Engines)

- **Kafka:** localhost:9092
- **Schema Registry:** http://localhost:8081
- **PostgreSQL:** localhost:5432
  - Database: `stream_manager_db`
  - User: `admin`
  - Password: `password`

### Engine-Specific Services

**ksqlDB:**
- **ksqlDB Server:** http://localhost:8088

**Flink:**
- **Flink JobManager (REST API):** http://localhost:8088
- **Flink SQL Gateway:** http://localhost:8082
- **Flink Web UI:** http://localhost:8088

---

## Switching Between Engines

To switch from ksqlDB to Flink (or vice versa):

1. **Stop all services:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml down
   # or
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml down
   ```

2. **Update Configuration:**
   
   Edit the `StreamManager.Api/appsettings.json` file and change the `Provider`:
   - For ksqlDB: `"Provider": "KsqlDb"`
   - For Flink: `"Provider": "Flink"`

3. **Start New Infrastructure:**
   ```bash
   # For ksqlDB
   docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
   
   # OR for Flink
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
   ```

4. **Restart API and Web apps**

**Note:** Existing stream definitions in the database will need their SQL syntax updated to match the new engine.

## SQL Syntax Differences

Understanding the syntax differences between engines is crucial:

| Feature                 | ksqlDB                               | Flink                                   |
|-------------------------|--------------------------------------|-----------------------------------------|
| **Streaming SELECT**    | `SELECT * FROM orders EMIT CHANGES;` | `SELECT * FROM orders;`                 |
| **Persistent Queries**  | `CREATE STREAM AS SELECT...`         | `INSERT INTO table SELECT...`           |
| **Window (Tumbling)**   | `WINDOW TUMBLING (SIZE 5 MINUTES)`   | `TUMBLE(time_col, INTERVAL '5' MINUTE)` |
| **LIMIT in Persistent** | ❌ Not allowed                        | ✅ Allowed (bounded queries)             |

**📚 See [FLINK_MIGRATION.md](docs/FLINK_MIGRATION.md) for comprehensive SQL migration guide.**

---

## API Endpoints

### Stream Management

- `GET /api/streams` — List all streams
- `POST /api/streams` — Create new stream
- `GET /api/streams/{id}` — Get stream details
- `PUT /api/streams/{id}` — Update stream definition
- `POST /api/streams/{id}/deploy` — Deploy stream to active engine
- `POST /api/streams/{id}/stop` — Stop running stream
- `DELETE /api/streams/{id}` — Delete stream

### Testing

- `POST /api/test/query` — Test ad-hoc query execution
- `POST /api/test/direct` — Direct engine connection test

### SignalR Hub (/hub/stream)

- `ExecuteAdHoc(string query)` — Stream ad-hoc query results
- `ViewStream(string streamId)` — Stream results from a deployed query
- `JoinStreamGroup(string topicName)` — Subscribe to topic updates
- `LeaveStreamGroup(string topicName)` — Unsubscribe from a topic
- `GetQueryLimits()` — Get rate limit information

## Technology Stack

### Backend

- **.NET 10** — Application framework
- **ASP.NET Core Web API** — RESTful API
- **ASP.NET Core MVC** — Web framework
- **SignalR** — Real-time communication
- **Entity Framework Core** — ORM with PostgreSQL
- **Confluent.Kafka** — Kafka client library

### Stream Processing Engines

- **ksqlDB 7.6.0** — Confluent's streaming database
- **Apache Flink 1.18** — Distributed stream processing framework

### Frontend

- **Bootstrap 5** — UI framework
- **CodeMirror** — Code editor component
- **SignalR Client** — Real-time updates

### Infrastructure

- **Docker & Docker Compose** — Containerization
- **PostgreSQL 15** — Metadata database
- **Apache Kafka (KRaft)** — Event streaming platform
- **Confluent Schema Registry** — Schema management

---

## Project Structure

```
StreamManager/
├── StreamManager.Api/              # Backend API
│   ├── Controllers/                # REST API controllers
│   ├── Services/                   # Business logic
│   │   ├── Engines/               # Stream engine implementations
│   │   │   ├── KsqlDbEngine.cs    # ksqlDB implementation
│   │   │   └── FlinkEngine.cs     # Flink implementation
│   │   └── Validators/            # Query validators
│   │       ├── KsqlDbQueryValidator.cs
│   │       └── FlinkQueryValidator.cs
│   ├── Hubs/                      # SignalR hubs
│   └── Configuration/             # Config models
├── StreamManager.Web/             # Frontend MVC
│   ├── Controllers/               # MVC controllers
│   ├── Views/                     # Razor views
│   └── wwwroot/                   # Static assets
├── docker-compose.base.yml        # Shared services (Kafka, Postgres)
├── docker-compose.ksqldb.yml      # ksqlDB service
├── docker-compose.flink.yml       # Flink services
└── docs/                          # Documentation
    ├── ENGINE_ABSTRACTION_PLAN.md
    ├── QUICK_START.md
    ├── FLINK_MIGRATION.md
    └── ... (8 comprehensive guides)
```

---

## Documentation

Comprehensive documentation is available in the `/docs` folder:

| Document                                                                          | Description                               |
|-----------------------------------------------------------------------------------|-------------------------------------------|
| **[QUICK_START.md](docs/QUICK_START.md)**                                         | Fast setup guide for both engines         |
| **[QUICK_REFERENCE.md](docs/QUICK_REFERENCE.md)**                                 | Quick reference card for daily operations |
| **[ENGINE_ABSTRACTION_PLAN.md](docs/ENGINE_ABSTRACTION_PLAN.md)**                 | Architecture and design decisions         |
| **[PHASE4_IMPLEMENTATION.md](docs/PHASE4_IMPLEMENTATION.md)**                     | Flink implementation details              |
| **[FLINK_MIGRATION.md](docs/FLINK_MIGRATION.md)**                                 | SQL syntax migration guide                |
| **[COMPLETE_IMPLEMENTATION_SUMMARY.md](docs/COMPLETE_IMPLEMENTATION_SUMMARY.md)** | Full project summary                      |

---

## Key Features

### ✨ Multi-Engine Support

- **Configuration-based** engine selection (no code changes)
- **Engine-specific** SQL validation
- **Unified interface** — same API for both engines

### 🔄 Real-Time Streaming

- **SignalR-based** result streaming
- **Live previews** of deployed streams
- **Rate limiting** to prevent resource exhaustion

### 🎯 Query Management

- **Ad-hoc queries** — Execute queries on-demand
- **Persistent queries** — Deploy long-running streaming jobs
- **Query validation** — Prevent invalid SQL before execution
- **Resource limits** — Configurable query complexity limits

### 📊 Stream Management

- **CRUD operations** for stream definitions
- **Deploy/Stop** control for persistent streams
- **Job tracking** — Monitor running jobs
- **Status monitoring** — Real-time stream health

### 🛡️ Safety Features

- **Query validation** — Engine-specific syntax checking
- **Rate limiting** — Prevent a query abuse
- **Resource limits** — Max JOINs, windows, query length
- **Blocked keywords** — Prevent dangerous operations

---

## Production Considerations

### Security

- [ ] Add authentication (e.g., OAuth, JWT)
- [ ] Enable HTTPS/TLS
- [ ] Secure database credentials (use secrets management)
- [ ] Implement authorization (role-based access)
- [ ] Enable audit logging

### Monitoring

- [ ] Set up Prometheus metrics
- [ ] Configure Grafana dashboards
- [ ] Monitor Flink/ksqlDB health
- [ ] Track a query performance
- [ ] Set up alerting (PagerDuty, Slack)

### Scalability

- [ ] Load balance API servers
- [ ] Scale Flink TaskManagers (if using Flink)
- [ ] Configure proper resource limits
- [ ] Set up PostgreSQL replication
- [ ] Implement connection pooling

### High Availability

- [ ] Deploy multiple API instances
- [ ] Use managed PostgreSQL (AWS RDS, Azure Database)
- [ ] Configure Kafka replication
- [ ] Set up health checks
- [ ] Implement graceful shutdown

---

## Troubleshooting

### Common Issues

**1. "Failed to connect to ksqlDB/Flink"**
```bash
# Check if services are running
docker ps

# View logs
docker logs ksqldb-server
docker logs flink-sql-gateway

# Verify connectivity
curl http://localhost:8088/info  # ksqlDB
curl http://localhost:8082/v1/info  # Flink
```

**2. "EMIT CHANGES is not valid Flink SQL syntax"**

- You're using ksqlDB syntax with Flink configured
- Remove `EMIT CHANGES` from your query

**3. "Database connection failed"**

```bash
# Check PostgreSQL
docker logs postgres-db

# Verify credentials in the appsettings.json file
# Default: Host=localhost;Database=stream_manager_db;Username=admin;Password=password
```

**4. SignalR connection fails**

- Ensure endpoint is `/hub/stream` (not `/hub/ksql`)
- Check CORS configuration
- Verify API is running and accessible

---

## Contributing

Contributions are welcome! The architecture is designed to be extensible:

1. **Add new engines** — Implement `IStreamQueryEngine` interface
2. **Add validators** — Implement `IQueryValidator` interface
3. **Enhance UI** — Add features to StreamManager.Web
4. **Improve docs** — Update documentation in `/docs`

---

## License

[Your License Here]

---

## Support

For issues, questions, or contributions:

- **Documentation:** Check the `/docs` folder
- **Quick Start:** [QUICK_START.md](docs/QUICK_START.md)
- **Issues:** Create a GitHub issue
- **Questions:** Refer to [QUICK_REFERENCE.md](docs/QUICK_REFERENCE.md)

---

**🎉 Stream Manager – Now with dual-engine support for ksqlDB and Apache Flink!**

**Version:** 1.0.0  
**Last Updated:** December 7, 2025