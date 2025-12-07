# Stream Manager - Quick Start Guide

## Choosing Your Stream Processing Engine

Stream Manager supports two stream processing engines:
- **ksqlDB** (Confluent) - Default, simpler setup
- **Apache Flink** - More scalable, advanced features

The engine is configured once at deployment time via `appsettings.json`.

---

## Option 1: Run with ksqlDB (Default)

### 1. Start Infrastructure

```bash
# Start Kafka, Schema Registry, PostgreSQL, and ksqlDB
docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
```

### 2. Configure Application

Edit `StreamManager.Api/appsettings.json`:

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

### 3. Run Database Migrations

```bash
cd StreamManager.Api
dotnet ef database update
```

### 4. Start API Server

```bash
cd StreamManager.Api
dotnet run
```

API available at: **https://localhost:7068**

### 5. Start Web Application

```bash
cd StreamManager.Web
dotnet run
```

Web UI available at: **https://localhost:7122**

### 6. Verify ksqlDB

Check ksqlDB is running:
```bash
curl http://localhost:8088/info
```

---

## Option 2: Run with Apache Flink

### 1. Start Infrastructure

```bash
# Start Kafka, Schema Registry, PostgreSQL, and Flink
docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
```

**Wait 30-60 seconds** for Flink to fully start up.

### 2. Configure Application

Edit `StreamManager.Api/appsettings.json`:

```json
{
  "StreamEngine": {
    "Provider": "Flink",
    "Flink": {
      "SqlGatewayUrl": "http://localhost:8082",
      "RestApiUrl": "http://localhost:8088"
    }
  }
}
```

### 3. Run Database Migrations

```bash
cd StreamManager.Api
dotnet ef database update
```

### 4. Start API Server

```bash
cd StreamManager.Api
dotnet run
```

API available at: **https://localhost:7068**

### 5. Start Web Application

```bash
cd StreamManager.Web
dotnet run
```

Web UI available at: **https://localhost:7122**

### 6. Verify Flink

Check Flink JobManager is running:
```bash
curl http://localhost:8088/overview
```

Check Flink SQL Gateway is running:
```bash
curl http://localhost:8082/v1/info
```

Access Flink Web UI: **http://localhost:8088**

---

## Creating the Orders Stream for Development

Before creating stream definitions, you need to create the base `orders` stream/table in your streaming engine. This is required for the sample order generator script and examples.

### For ksqlDB

Connect to ksqlDB CLI:
```bash
docker exec -it ksqldb-cli ksql http://ksqldb-server:8088
```

Create the orders stream:
```sql
CREATE STREAM orders (
    ORDER_ID INT,
    CUSTOMER_ID INT,
    PRODUCT VARCHAR,
    AMOUNT DOUBLE,
    PURCHASE_DATE VARCHAR
) WITH (
    KAFKA_TOPIC='orders',
    VALUE_FORMAT='JSON',
    PARTITIONS=3
);
```

Exit ksqlDB CLI:
```sql
EXIT;
```

### For Apache Flink

Create the orders table using the API. First, create a stream definition via the API:

```bash
curl -X POST http://localhost:7068/api/streams \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Base Orders Table",
    "sqlScript": "CREATE TABLE orders (ORDER_ID INT, CUSTOMER_ID INT, PRODUCT STRING, AMOUNT DOUBLE, PURCHASE_DATE STRING) WITH ('\''connector'\'' = '\''kafka'\'', '\''topic'\'' = '\''orders'\'', '\''properties.bootstrap.servers'\'' = '\''localhost:9092'\'', '\''format'\'' = '\''json'\'', '\''scan.startup.mode'\'' = '\''earliest-offset'\'')"
  }'
```

Or use the Flink SQL Gateway directly:

```bash
# Open a Flink SQL session
SESSION_ID=$(curl -X POST http://localhost:8082/v1/sessions \
  -H "Content-Type: application/json" \
  -d '{"properties": {}}' | jq -r '.sessionHandle')

# Create the orders table
curl -X POST http://localhost:8082/v1/sessions/$SESSION_ID/statements \
  -H "Content-Type: application/json" \
  -d '{
    "statement": "CREATE TABLE IF NOT EXISTS orders (ORDER_ID INT, CUSTOMER_ID INT, PRODUCT STRING, AMOUNT DOUBLE, PURCHASE_DATE STRING) WITH ('\''connector'\'' = '\''kafka'\'', '\''topic'\'' = '\''orders'\'', '\''properties.bootstrap.servers'\'' = '\''kafka:9092'\'', '\''format'\'' = '\''json'\'', '\''scan.startup.mode'\'' = '\''earliest-offset'\'')"
  }'
```

### Generate Sample Orders

Once the orders stream/table is created, use the provided script to generate sample data:

```bash
cd scripts
chmod +x generate_orders.sh
./generate_orders.sh
```

The script will continuously insert orders every 30 seconds with:
- **ORDER_ID**: Sequential starting at 1011
- **CUSTOMER_ID**: Random between 500-599
- **PRODUCT**: Random from 20 product names (Laptop, Mouse, Keyboard, etc.)
- **AMOUNT**: Random between $10-$1500
- **PURCHASE_DATE**: Current timestamp in ISO 8601 format

**Example Output:**
```
[2025-12-07T01:45:32] Inserting Order #1011: Laptop - $899.99 for Customer 542
  ✓ Successfully inserted

[2025-12-07T01:46:02] Inserting Order #1012: Wireless Mouse - $29.99 for Customer 513
  ✓ Successfully inserted
```

Press `Ctrl+C` to stop the generator.

### Example Stream Definitions

Once the base `orders` stream/table exists, you can create stream definitions to process the data:

#### High-Value Orders (ksqlDB)

```sql
SELECT 
    ORDER_ID,
    CUSTOMER_ID,
    PRODUCT,
    AMOUNT,
    PURCHASE_DATE
FROM orders 
WHERE AMOUNT > 500
EMIT CHANGES;
```

#### High-Value Orders (Flink)

```sql
SELECT 
    ORDER_ID,
    CUSTOMER_ID,
    PRODUCT,
    AMOUNT,
    PURCHASE_DATE
FROM orders 
WHERE AMOUNT > 500;
```

#### Customer Order Summary (ksqlDB)

```sql
SELECT 
    CUSTOMER_ID,
    COUNT(*) AS ORDER_COUNT,
    SUM(AMOUNT) AS TOTAL_SPENT,
    AVG(AMOUNT) AS AVG_ORDER_VALUE
FROM orders
GROUP BY CUSTOMER_ID
EMIT CHANGES;
```

#### Customer Order Summary (Flink)

```sql
SELECT 
    CUSTOMER_ID,
    COUNT(*) AS ORDER_COUNT,
    SUM(AMOUNT) AS TOTAL_SPENT,
    AVG(AMOUNT) AS AVG_ORDER_VALUE
FROM orders
GROUP BY CUSTOMER_ID;
```

#### Product Sales Analytics (ksqlDB)

```sql
SELECT 
    PRODUCT,
    COUNT(*) AS UNITS_SOLD,
    SUM(AMOUNT) AS REVENUE,
    AVG(AMOUNT) AS AVG_PRICE
FROM orders
WINDOW TUMBLING (SIZE 1 HOUR)
GROUP BY PRODUCT
EMIT CHANGES;
```

#### Product Sales Analytics (Flink)

```sql
SELECT 
    PRODUCT,
    COUNT(*) AS UNITS_SOLD,
    SUM(AMOUNT) AS REVENUE,
    AVG(AMOUNT) AS AVG_PRICE,
    TUMBLE_START(TO_TIMESTAMP(PURCHASE_DATE), INTERVAL '1' HOUR) AS WINDOW_START
FROM orders
GROUP BY PRODUCT, TUMBLE(TO_TIMESTAMP(PURCHASE_DATE), INTERVAL '1' HOUR);
```

---

## SQL Syntax Differences

### ksqlDB Queries

```sql
-- Ad-hoc query (EMIT CHANGES is optional, added automatically)
SELECT * FROM orders WHERE amount > 100 EMIT CHANGES;

-- Persistent query (system creates stream)
SELECT customer_id, SUM(amount) as total
FROM orders
GROUP BY customer_id
EMIT CHANGES;
```

### Flink SQL Queries

```sql
-- Ad-hoc query (NO EMIT CHANGES)
SELECT * FROM orders WHERE amount > 100;

-- Persistent query (similar syntax)
SELECT customer_id, SUM(amount) as total
FROM orders
GROUP BY customer_id;
```

**Key Difference:** Remove `EMIT CHANGES` when using Flink!

---

## Ports Reference

| Service | Port | Purpose |
|---------|------|---------|
| **Kafka** | 9092 | Kafka broker (external) |
| **Schema Registry** | 8081 | Avro schema management |
| **PostgreSQL** | 5432 | Application database |
| **ksqlDB Server** | 8088 | ksqlDB REST API (ksqlDB only) |
| **Flink JobManager** | 8088 | Flink Web UI & REST API (Flink only) |
| **Flink SQL Gateway** | 8082 | SQL submission API (Flink only) |
| **API Server** | 7068 | Stream Manager API |
| **Web UI** | 7122 | Stream Manager Web Interface |

---

## Switching Engines

To switch from ksqlDB to Flink (or vice versa):

1. **Stop all services:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml down
   # or
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml down
   ```

2. **Update `appsettings.json`** - Change `Provider` to `"KsqlDb"` or `"Flink"`

3. **Start new infrastructure:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
   ```

4. **Restart API and Web apps**

**Note:** Existing stream definitions in the database will need their SQL updated to match the new engine's syntax.

---

## Troubleshooting

### ksqlDB: "Connection refused" error
- Wait 15-30 seconds for ksqlDB to start
- Check logs: `docker logs ksqldb-server`

### Flink: "Session creation failed"
- Wait 30-60 seconds for Flink to fully initialize
- Verify all Flink containers are running: `docker ps | grep flink`
- Check SQL Gateway logs: `docker logs flink-sql-gateway`
- Check JobManager logs: `docker logs flink-jobmanager`

### Database: "Connection refused"
- Ensure PostgreSQL is running: `docker ps | grep postgres`
- Check connection string in `appsettings.json`

### "Unknown stream engine provider"
- Verify `StreamEngine.Provider` is either `"KsqlDb"` or `"Flink"` (case-sensitive)

---

## Next Steps

- See `docs/ENGINE_ABSTRACTION_PLAN.md` for architecture details
- See `docs/FLINK_MIGRATION.md` for SQL syntax migration guide (coming soon)
- See `docs/BEST_PRACTICES.md` for capacity planning

---

## Development vs Production

### Development (Current Setup)
- Single ksqlDB or Flink instance
- No authentication
- Lightweight resource limits

### Production Recommendations
- Multi-instance deployment (see `CAPACITY_PLANNING.md`)
- Add authentication/authorization
- Configure TLS/SSL
- Set up monitoring (Prometheus, Grafana)
- Implement backup strategy for PostgreSQL
