# Resource Limits & Protection Strategy

This document describes the multi-layer resource protection implemented in Stream Manager to prevent any single user or query from consuming excessive resources on the shared ksqlDB instance.

## 🛡️ Protection Layers

### **1. Infrastructure Level (Docker)**

Docker resource limits constrain the ksqlDB container itself:

```yaml
deploy:
  resources:
    limits:
      cpus: '2.0'      # Maximum 2 CPU cores
      memory: 2G       # Maximum 2GB RAM
    reservations:
      cpus: '0.5'      # Minimum 0.5 CPU cores
      memory: 512M     # Minimum 512MB RAM
```

**Location**: `docker-compose.yml` → `ksqldb-server` service

**Benefits**:
- Hard limit on total resources available to ksqlDB
- Prevents runaway queries from consuming entire host
- Protects other services on the same host

---

### **2. ksqlDB Configuration**

Environment variables limit query behavior within ksqlDB:

| Setting | Value | Purpose |
|---------|-------|---------|
| `KSQL_CACHE_MAX_BYTES_BUFFERING` | 10 MB | Buffer size per query |
| `KSQL_KSQL_STREAMS_BUFFER_MEMORY` | 32 MB | Total buffer memory |
| `KSQL_KSQL_STREAMS_NUM_STREAM_THREADS` | 2 | Threads **per query** (not total queries!) |
| `KSQL_HEAP_OPTS` | `-Xms512M -Xmx1536M` | JVM heap limits |

**⚠️ Important**: `NUM_STREAM_THREADS` is **per-query**, not total. With 50 queries × 2 threads = 100 total threads.

**Location**: `docker-compose.yml` → `ksqldb-server.environment`

**Benefits**:
- Limits memory per query
- Controls parallelism
- Prevents memory exhaustion

---

### **3. Query Validation (`KsqlQueryValidator`)**

Pre-execution validation checks before queries reach ksqlDB:

#### **Ad-Hoc Query Rules**
✅ **Allowed**: SELECT statements only
❌ **Blocked**: CREATE CONNECTOR, DROP CONNECTOR, PRINT
⚠️ **Limited**: 
- Max 3 JOINs
- Max 2 WINDOWs
- Max 5000 characters

#### **Persistent Query Rules**
✅ **Allowed**: SELECT statements (wrapped in CREATE STREAM)
❌ **Blocked**: LIMIT clause (incompatible with persistent queries)
⚠️ **Warned**: Queries with multiple JOINs

**Location**: `StreamManager.Api/Services/KsqlQueryValidator.cs`

**Configuration**: `appsettings.ResourceLimits.json` → `ResourceLimits.Query`

---

### **4. Rate Limiting (`QueryRateLimiter`)**

Controls concurrent queries and request rates:

#### **Per-User Limits**
| Limit Type | Default | Description |
|------------|---------|-------------|
| Concurrent Ad-Hoc Queries | 2 | Max simultaneous SELECT queries |
| Concurrent Persistent Queries | 5 | Max active CREATE STREAM AS queries |
| Queries Per Minute | 30 | Max query submissions per minute |

#### **Global Limits**
| Limit Type | Default | Description |
|------------|---------|-------------|
| Total Ad-Hoc Queries | 20 | System-wide concurrent ad-hoc queries |
| Total Persistent Queries | 50 | System-wide active persistent queries |

**Location**: `StreamManager.Api/Services/QueryRateLimiter.cs`

**Configuration**: `appsettings.ResourceLimits.json` → `ResourceLimits.RateLimits`

---

## 🧮 Understanding Thread Limits vs Query Limits

### **Common Confusion: Threads ≠ Queries**

| Setting | What It Limits | Example |
|---------|----------------|---------|
| `KSQL_STREAMS_NUM_STREAM_THREADS` | Threads **per query** | Each query can use 2 threads |
| `MaxTotalPersistentQueries` (app) | **Total queries** system-wide | Max 50 queries running |
| **Total threads** | `threads × queries` | 2 × 50 = **100 total threads** |

### **Why Limit Threads Per Query?**

**Lower threads per query** = **More total queries can run**

- 4 threads/query × 50 queries = 200 threads ❌ (too much!)
- 2 threads/query × 50 queries = 100 threads ✅ (better)
- 1 thread/query × 50 queries = 50 threads ✅✅ (most efficient for multi-tenancy)

**Trade-off**: 
- ⬇️ Fewer threads = Slower processing per query
- ⬆️ More threads = Faster per query, but fewer total queries

### **Recommended Settings by Use Case**

| Use Case | Threads/Query | Max Queries | Total Threads | Rationale |
|----------|---------------|-------------|---------------|-----------|
| **Multi-tenant** (current) | 1-2 | 50 | 50-100 | Many users, fair sharing |
| **Power users** | 4 | 20 | 80 | Fewer complex queries |
| **Development** | 1 | 10 | 10 | Save resources |

---

## 📊 How It Works

### **Ad-Hoc Query Flow**

```
User submits query
    ↓
Rate Limiter checks: Can user run another query?
    ↓ YES
Query Validator checks: Is query safe?
    ↓ VALID
Add stream properties (buffer limits, thread count)
    ↓
Execute query via ksqlDB REST API
    ↓
Stream results to user via SignalR
    ↓
On completion/cancellation: Release rate limit slot
```

### **Persistent Query Flow**

```
User creates stream definition
    ↓
Query Validator checks: Is SELECT valid?
    ↓ VALID
Save to database (not deployed yet)
    ↓
User clicks "Deploy"
    ↓
Rate Limiter checks: Can user deploy another stream?
    ↓ YES
Wrap in CREATE STREAM AS statement
    ↓
Execute via ksqlDB REST API
    ↓
Store query ID and topic name
    ↓
Background service consumes output topic
```

---

## ⚙️ Configuration

### **Adjusting Limits**

Edit `appsettings.ResourceLimits.json`:

```json
{
  "ResourceLimits": {
    "Query": {
      "MaxQueryLength": 5000,
      "MaxJoins": 3,
      "MaxWindows": 2
    },
    "RateLimits": {
      "PerUser": {
        "MaxAdHocQueries": 2,
        "MaxPersistentQueries": 5,
        "MaxQueriesPerMinute": 30
      },
      "Global": {
        "MaxTotalAdHocQueries": 20,
        "MaxTotalPersistentQueries": 50
      }
    }
  }
}
```

### **Docker Resource Limits**

Edit `docker-compose.yml`:

```yaml
ksqldb-server:
  deploy:
    resources:
      limits:
        cpus: '4.0'    # Increase to 4 cores
        memory: 4G     # Increase to 4GB
```

---

## 🚨 Monitoring & Alerts

### **Metrics to Track**

1. **Active Queries**
   - Per-user ad-hoc query count
   - Global ad-hoc query count
   - Per-user persistent query count
   - Global persistent query count

2. **Resource Usage**
   - ksqlDB container CPU usage
   - ksqlDB container memory usage
   - JVM heap usage

3. **Rate Limit Hits**
   - Users hitting concurrent query limits
   - Users hitting rate limits
   - Global capacity reached events

### **Getting Limits Info**

SignalR method available to clients:

```javascript
const limits = await connection.invoke('GetQueryLimits');
console.log(`Active: ${limits.userActiveAdHocQueries}/${limits.userMaxAdHocQueries}`);
```

---

## 🔐 Best Practices

### **For Users**

1. **Use LIMIT clauses** for ad-hoc queries to restrict result sets
2. **Add WHERE filters** to reduce data scanned
3. **Avoid SELECT *** when possible - specify only needed columns
4. **Test with LIMIT first** before running unbounded queries
5. **Stop queries** when you're done viewing results

### **For Administrators**

1. **Monitor ksqlDB logs** for slow queries
2. **Review rate limit hits** to identify heavy users
3. **Adjust limits** based on actual usage patterns
4. **Scale ksqlDB horizontally** if global limits are frequently hit
5. **Implement authentication** and use real user IDs (not connection IDs)

---

## 🛠️ Troubleshooting

### **"System is at capacity" Error**

**Cause**: Global ad-hoc query limit reached (20 concurrent queries by default)

**Solutions**:
1. Wait for other queries to complete
2. Increase `MaxTotalAdHocQueries` in config
3. Add more ksqlDB instances (load balancing)

### **"You have reached the maximum of N concurrent queries" Error**

**Cause**: User has too many active queries

**Solutions**:
1. Stop existing queries before starting new ones
2. Use persistent queries instead of long-running ad-hoc queries
3. Increase `MaxAdHocQueriesPerUser` in config

### **"Rate limit exceeded" Error**

**Cause**: User submitted too many queries in one minute

**Solutions**:
1. Wait 60 seconds before retrying
2. Increase `MaxQueriesPerMinute` in config

### **Query Validation Errors**

**Cause**: Query contains blocked operations or exceeds limits

**Solutions**:
1. Simplify query (reduce JOINs/WINDOWs)
2. Remove blocked keywords (PRINT, CREATE CONNECTOR, etc.)
3. Shorten query if over 5000 characters

---

## 📝 Future Enhancements

1. **Query Cost Estimation**: Analyze query before execution
2. **Query Timeout**: Auto-terminate long-running queries
3. **User Quotas**: CPU/memory quotas per user
4. **Query Queue**: Queue queries when at capacity instead of rejecting
5. **Audit Logging**: Track all query executions
6. **Metrics Dashboard**: Real-time resource usage visualization
