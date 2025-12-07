# Stream Manager - Operations Guide

**Last Updated:** December 7, 2025

## Table of Contents
1. [Capacity Planning](#capacity-planning)
2. [Resource Limits & Protection](#resource-limits--protection)
3. [Best Practices](#best-practices)
4. [Ad-Hoc Query Costs](#ad-hoc-query-costs)
5. [Monitoring & Troubleshooting](#monitoring--troubleshooting)

---

## Capacity Planning

### Goal: Running Hundreds of Concurrent Queries

When planning to run **hundreds of stream processing queries** with Stream Manager, proper capacity planning is essential.

### Resource Estimation

#### **Assumptions**
- **2 threads per query** (typical for stream processing)
- **Target: 500 concurrent queries**
- **Resource overhead:** 20% for system operations

#### **CPU Calculation**
```
Queries: 500
Threads per query: 2
Total threads: 1,000
Recommended CPU cores: 1,200 (with 20% overhead)
```

#### **Memory Calculation**
```
Per-query memory: 512 MB (typical)
Total queries: 500
Base memory: 256 GB
Recommended memory: 320 GB (with 20% overhead)
```

### Scaling Strategy

#### **Option 1: Horizontal Scaling (Recommended)**
Deploy multiple Stream Manager instances, each managing a subset of queries:

```
┌─────────────────────────────────────────┐
│         Load Balancer / Gateway         │
└──────────┬──────────────────────────────┘
           │
    ┌──────┴──────┬──────────┬──────────┐
    │             │          │          │
┌───▼───┐    ┌───▼───┐  ┌───▼───┐  ┌───▼───┐
│ SM #1 │    │ SM #2 │  │ SM #3 │  │ SM #4 │
│ 125   │    │ 125   │  │ 125   │  │ 125   │
│queries│    │queries│  │queries│  │queries│
└───┬───┘    └───┬───┘  └───┬───┘  └───┬───┘
    │            │          │          │
    └────────────┴──────────┴──────────┘
                 │
          ┌──────▼──────┐
          │    Kafka    │
          │   Cluster   │
          └─────────────┘
```

**Benefits:**
- ✅ Fault isolation
- ✅ Easier maintenance
- ✅ Better resource utilization
- ✅ Gradual scaling

**Per-Instance Sizing:**
- **50-100 queries per instance** (sweet spot)
- **32 GB RAM** per instance
- **16 CPU cores** per instance

#### **Option 2: Vertical Scaling**
Single large instance with massive resources:

**Pros:**
- Simpler deployment
- Lower network latency

**Cons:**
- ❌ Single point of failure
- ❌ Harder to maintain
- ❌ Resource contention
- ❌ Expensive hardware

### Engine-Specific Considerations

#### **ksqlDB Scaling**
- Deploy multiple ksqlDB servers
- Use shared Kafka cluster
- Each Stream Manager instance connects to dedicated ksqlDB instance
- Scale ksqlDB horizontally (no query distribution across servers)

#### **Apache Flink Scaling**
- Deploy Flink cluster with JobManager + TaskManagers
- Scale TaskManagers horizontally (add more workers)
- Flink distributes jobs across TaskManagers automatically
- **Recommended:** 1 TaskManager per 50-100 jobs

---

## Resource Limits & Protection

### Multi-Layer Protection Strategy

Stream Manager implements multiple protection layers to prevent resource exhaustion:

```
┌──────────────────────────────────────────────┐
│  Layer 1: Application-Level Limits          │
│  - Query timeout limits                      │
│  - Result size limits                        │
│  - Concurrent query limits per user          │
└──────────────────────────────────────────────┘
                    │
┌──────────────────────────────────────────────┐
│  Layer 2: Engine Configuration               │
│  - ksqlDB: ksql.queries.max-concurrent       │
│  - Flink: taskmanager.numberOfTaskSlots      │
└──────────────────────────────────────────────┘
                    │
┌──────────────────────────────────────────────┐
│  Layer 3: Infrastructure Limits              │
│  - Docker memory/CPU limits                  │
│  - Kubernetes resource quotas                │
└──────────────────────────────────────────────┘
```

### Layer 1: Application-Level Limits

Implemented in `StreamHub.cs` and controllers:

```csharp
// Query timeout (default: 5 minutes)
private readonly TimeSpan _queryTimeout = TimeSpan.FromMinutes(5);

// Maximum results per query (default: 1000 rows)
private const int MaxResults = 1000;

// Concurrent queries per user (default: 3)
private const int MaxConcurrentQueriesPerUser = 3;
```

**Configuration in appsettings.json:**
```json
{
  "StreamProcessing": {
    "Limits": {
      "QueryTimeoutSeconds": 300,
      "MaxResultsPerQuery": 1000,
      "MaxConcurrentQueriesPerUser": 3,
      "MaxQueryLengthChars": 10000
    }
  }
}
```

### Layer 2: Engine Configuration

#### **ksqlDB Limits**
Edit `ksqldb-server.properties`:

```properties
# Maximum concurrent queries (default: 100)
ksql.queries.max-concurrent=100

# Query timeout (milliseconds)
ksql.query.timeout.ms=300000

# Maximum memory per query
ksql.streams.buffer.memory=128000000

# Maximum processing threads
ksql.streams.num.stream.threads=2
```

#### **Flink Limits**
Edit `flink-conf.yaml`:

```yaml
# Task slots per TaskManager (concurrent tasks)
taskmanager.numberOfTaskSlots: 4

# Memory per TaskManager
taskmanager.memory.process.size: 2048m

# Maximum parallelism
parallelism.default: 2

# Job timeout
execution.timeout: 5 min
```

### Layer 3: Infrastructure Limits

#### **Docker Compose**
```yaml
services:
  flink-taskmanager:
    deploy:
      resources:
        limits:
          cpus: '4.0'
          memory: 4G
        reservations:
          cpus: '2.0'
          memory: 2G
```

#### **Kubernetes**
```yaml
apiVersion: v1
kind: ResourceQuota
metadata:
  name: stream-manager-quota
spec:
  hard:
    requests.cpu: "100"
    requests.memory: 200Gi
    limits.cpu: "200"
    limits.memory: 400Gi
    persistentvolumeclaims: "10"
```

---

## Best Practices

### ✅ Recommended: 50 Concurrent Queries per Instance

**Why 50 queries?**
This is the **sweet spot** balancing:
- Performance stability
- Resource predictability
- Operational simplicity
- Cost efficiency

### Instance Sizing Guidelines

#### **Small Instance (10-25 queries)**
```yaml
Resources:
  CPU: 4-8 cores
  Memory: 8-16 GB
  
Use Case:
  - Development/testing
  - Small teams
  - Low-volume processing
```

#### **Medium Instance (25-50 queries)** ⭐ **RECOMMENDED**
```yaml
Resources:
  CPU: 8-16 cores
  Memory: 16-32 GB
  
Use Case:
  - Production workloads
  - Mid-sized teams
  - Standard processing volume
```

#### **Large Instance (50-100 queries)**
```yaml
Resources:
  CPU: 16-32 cores
  Memory: 32-64 GB
  
Use Case:
  - High-volume processing
  - Large teams
  - Complex transformations
```

### Query Design Best Practices

#### **1. Use Windowing for Aggregations**
```sql
-- ✅ Good: Bounded window
SELECT customer_id, COUNT(*) 
FROM orders 
WINDOW TUMBLING (SIZE 1 HOUR)
GROUP BY customer_id;

-- ❌ Bad: Unbounded aggregation
SELECT customer_id, COUNT(*) 
FROM orders 
GROUP BY customer_id;
```

#### **2. Filter Early, Filter Often**
```sql
-- ✅ Good: Filter before joins
SELECT o.*, c.name
FROM orders o
WHERE o.amount > 1000
JOIN customers c ON o.customer_id = c.id;

-- ❌ Bad: Filter after joins
SELECT o.*, c.name
FROM orders o
JOIN customers c ON o.customer_id = c.id
WHERE o.amount > 1000;
```

#### **3. Limit Result Sets**
```sql
-- ✅ Good: Limited results
SELECT * FROM orders 
WHERE order_date = CURRENT_DATE
LIMIT 1000;

-- ❌ Bad: Unbounded results
SELECT * FROM orders;
```

#### **4. Use Appropriate Emit Strategies**
```sql
-- ksqlDB: Control emission frequency
SELECT * FROM orders 
EMIT CHANGES EVERY 5 SECONDS;

-- Flink: Use watermarks appropriately
CREATE TABLE orders (
    order_time TIMESTAMP(3),
    WATERMARK FOR order_time AS order_time - INTERVAL '5' SECOND
) WITH (...);
```

### Monitoring Best Practices

#### **Key Metrics to Track**

1. **Query Performance**
   - Execution time
   - Result count
   - Error rate

2. **Resource Usage**
   - CPU utilization
   - Memory consumption
   - Network I/O

3. **Engine Health**
   - Active queries count
   - Query backlog
   - Lag metrics

#### **Alerting Thresholds**

```yaml
Alerts:
  high_cpu:
    threshold: 80%
    duration: 5m
    
  high_memory:
    threshold: 85%
    duration: 5m
    
  query_failures:
    threshold: 5 failures in 10m
    
  slow_queries:
    threshold: >30s execution time
```

---

## Ad-Hoc Query Costs

### Cost Analysis

Ad-hoc queries (one-time SELECT statements) have different resource profiles than persistent streams:

#### **Query Type Comparison**

| Aspect | Persistent Stream | Ad-Hoc Query |
|--------|------------------|--------------|
| **Duration** | Continuous (hours/days) | One-time (seconds/minutes) |
| **Resources** | Sustained allocation | Burst allocation |
| **Impact** | Long-term capacity planning | Short-term resource spike |
| **Cost** | Predictable | Variable |

#### **Resource Usage Patterns**

**Persistent Stream:**
```
CPU │ ████████████████████████████████ (steady)
Mem │ ████████████████████████████████ (steady)
    └─────────────────────────────────► Time
```

**Ad-Hoc Query:**
```
CPU │     ████                          (spike)
Mem │     ████                          (spike)
    └─────────────────────────────────► Time
```

### Cost-Benefit Analysis

#### **When to Use Ad-Hoc Queries**
✅ Data exploration  
✅ One-time reports  
✅ Debugging/troubleshooting  
✅ Schema validation  

#### **When to Use Persistent Streams**
✅ Continuous monitoring  
✅ Real-time alerting  
✅ Ongoing transformations  
✅ Analytics pipelines  

### Optimization Strategies

#### **1. Cache Common Queries**
```csharp
// Cache frequently-used query results
private readonly IMemoryCache _cache;

public async Task<List<Order>> GetTodaysOrders()
{
    var cacheKey = $"orders_{DateTime.Today:yyyyMMdd}";
    
    if (!_cache.TryGetValue(cacheKey, out List<Order> orders))
    {
        orders = await ExecuteQuery("SELECT * FROM orders WHERE date = CURRENT_DATE");
        _cache.Set(cacheKey, orders, TimeSpan.FromMinutes(5));
    }
    
    return orders;
}
```

#### **2. Materialize Common Views**
Instead of ad-hoc queries, create materialized tables:

```sql
-- Create persistent materialized view
CREATE TABLE daily_summary AS
SELECT 
    order_date,
    COUNT(*) as order_count,
    SUM(amount) as total_amount
FROM orders
WINDOW TUMBLING (SIZE 1 DAY)
GROUP BY order_date;

-- Query the materialized view (much faster)
SELECT * FROM daily_summary WHERE order_date = CURRENT_DATE;
```

#### **3. Use Query Result Pagination**
```csharp
// Limit result sets
var query = "SELECT * FROM orders ORDER BY order_date DESC LIMIT 100";
```

#### **4. Implement Query Queuing**
For high-concurrency scenarios:

```csharp
public class QueryQueueService
{
    private readonly SemaphoreSlim _semaphore;
    
    public QueryQueueService(int maxConcurrent = 10)
    {
        _semaphore = new SemaphoreSlim(maxConcurrent);
    }
    
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> queryFunc)
    {
        await _semaphore.WaitAsync();
        try
        {
            return await queryFunc();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
```

---

## Monitoring & Troubleshooting

### Health Checks

#### **Stream Manager Health Endpoint**
```bash
# Check API health
curl http://localhost:5000/health

# Expected response
{
  "status": "Healthy",
  "components": {
    "database": "Healthy",
    "streamEngine": "Healthy",
    "kafka": "Healthy"
  }
}
```

#### **ksqlDB Health**
```bash
# Check ksqlDB server status
curl http://localhost:8088/info

# List active queries
curl http://localhost:8088/ksql -d '{"ksql": "SHOW QUERIES;"}'
```

#### **Flink Health**
```bash
# Check JobManager
curl http://localhost:8088/overview

# List running jobs
curl http://localhost:8088/jobs
```

### Common Issues & Solutions

#### **Issue: High CPU Usage**

**Symptoms:**
- Slow query responses
- API timeouts
- UI lag

**Solutions:**
1. Check active query count: `GET /api/stream/active`
2. Identify expensive queries in engine logs
3. Add indexes to source topics
4. Scale horizontally (add more instances)
5. Optimize query logic (add filters, reduce joins)

#### **Issue: Memory Pressure**

**Symptoms:**
- Out of memory errors
- Process crashes
- Slow garbage collection

**Solutions:**
1. Increase memory limits in docker-compose
2. Reduce concurrent query limit
3. Add result set limits to queries
4. Enable query result pagination
5. Review query windows (reduce state size)

#### **Issue: Query Failures**

**Symptoms:**
- Persistent query stops unexpectedly
- Ad-hoc queries return errors
- SignalR disconnections

**Solutions:**
1. Check engine logs: `docker logs flink-jobmanager`
2. Validate query syntax with validator
3. Check Kafka topic availability
4. Verify schema compatibility
5. Review resource limits

#### **Issue: Slow Query Performance**

**Symptoms:**
- Long execution times
- Delayed results
- UI timeouts

**Solutions:**
1. Add WHERE filters to reduce data volume
2. Use indexed fields in JOIN conditions
3. Optimize window sizes
4. Partition source topics appropriately
5. Scale TaskManagers (Flink) or add ksqlDB instances

### Log Locations

```bash
# Stream Manager API logs
docker logs streammanager-api

# Stream Manager Web logs
docker logs streammanager-web

# ksqlDB logs
docker logs ksqldb-server

# Flink JobManager logs
docker logs flink-jobmanager

# Flink TaskManager logs
docker logs flink-taskmanager

# Kafka logs
docker logs kafka
```

### Debugging Commands

```bash
# View active Docker containers
docker ps

# Check resource usage
docker stats

# Tail logs in real-time
docker logs -f flink-jobmanager

# Execute commands inside containers
docker exec -it flink-jobmanager bash

# Restart a service
docker-compose restart flink-taskmanager

# Full system restart
docker-compose down && docker-compose up -d
```

---

## Summary

### Key Takeaways

1. **Plan for Scale** — Use horizontal scaling for 100+ queries
2. **Set Limits** — Implement multi-layer resource protection
3. **Monitor Actively** — Track metrics and set alerts
4. **Optimize Queries** — Filter early, use windows, limit results
5. **Cache When Possible** — Reduce ad-hoc query load
6. **Size Appropriately** — 50 queries per instance is the sweet spot

### Recommended Instance Configuration

```yaml
Production Instance (50 queries):
  CPU: 16 cores
  Memory: 32 GB
  Storage: 100 GB SSD
  Network: 10 Gbps
  
  Limits:
    Max Concurrent Queries: 50
    Max Ad-Hoc Queries: 10
    Query Timeout: 5 minutes
    Max Result Size: 1000 rows
```

This configuration provides a solid foundation for running Stream Manager in production with predictable performance and cost characteristics.
