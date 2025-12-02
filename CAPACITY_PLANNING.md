# Capacity Planning for ksqlDB at Scale

## 🎯 Goal: Run Hundreds of Concurrent Queries

You want to run **hundreds of ksqlDB queries** with **2 threads per query**. Let's calculate the resources needed.

---

## 📊 Resource Calculation Formula

### **CPU Requirements**

```
Total Threads = Queries × Threads_Per_Query
CPU Cores Needed = Total_Threads × CPU_per_Thread + Overhead
```

**Rule of Thumb:**
- **1 thread ≈ 0.5-1 CPU core** (under load)
- Add **20-30% overhead** for JVM, GC, Kafka consumer threads, network I/O

### **Memory Requirements**

```
JVM Heap = Base_Heap + (Queries × Memory_Per_Query)
Container Memory = JVM_Heap × 1.5-2.0  (for off-heap + OS)
```

**Rule of Thumb:**
- **Base heap**: 512 MB - 1 GB
- **Per query**: 10-50 MB (depends on complexity, buffer size, state stores)
- **Off-heap overhead**: 50-100% of heap (native memory, direct buffers, metaspace)

---

## 🧮 Scenarios & Resource Estimates

### **Scenario 1: 100 Concurrent Queries**

| Metric | Calculation | Result |
|--------|-------------|--------|
| Total threads | 100 × 2 = 200 | **200 threads** |
| CPU (conservative) | 200 × 0.75 + 20% overhead | **180 cores** |
| CPU (optimistic) | 200 × 0.5 + 20% overhead | **120 cores** |
| JVM heap | 1 GB + (100 × 30 MB) | **4 GB** |
| Container memory | 4 GB × 1.75 | **7 GB** |

**Recommended Docker Limits:**
```yaml
deploy:
  resources:
    limits:
      cpus: '120-180'
      memory: 8G
    reservations:
      cpus: '60'
      memory: 4G
```

---

### **Scenario 2: 200 Concurrent Queries**

| Metric | Calculation | Result |
|--------|-------------|--------|
| Total threads | 200 × 2 = 400 | **400 threads** |
| CPU (conservative) | 400 × 0.75 + 20% overhead | **360 cores** |
| CPU (optimistic) | 400 × 0.5 + 20% overhead | **240 cores** |
| JVM heap | 1 GB + (200 × 30 MB) | **7 GB** |
| Container memory | 7 GB × 1.75 | **12 GB** |

**Recommended Docker Limits:**
```yaml
deploy:
  resources:
    limits:
      cpus: '240-360'
      memory: 16G
    reservations:
      cpus: '120'
      memory: 8G
```

---

### **Scenario 3: 500 Concurrent Queries** 🚀

| Metric | Calculation | Result |
|--------|-------------|--------|
| Total threads | 500 × 2 = 1000 | **1000 threads** |
| CPU (conservative) | 1000 × 0.75 + 20% overhead | **900 cores** |
| CPU (optimistic) | 1000 × 0.5 + 20% overhead | **600 cores** |
| JVM heap | 1 GB + (500 × 30 MB) | **16 GB** |
| Container memory | 16 GB × 1.75 | **28 GB** |

**Recommended Docker Limits:**
```yaml
deploy:
  resources:
    limits:
      cpus: '600-900'
      memory: 32G
    reservations:
      cpus: '300'
      memory: 16G
```

---

## 🏗️ Architecture Patterns for Scale

### **Option 1: Single Large Instance** ❌ Not Recommended

Running 500 queries on one ksqlDB instance:
- ❌ Single point of failure
- ❌ Difficult to manage
- ❌ Resource contention
- ❌ Blast radius = entire system

---

### **Option 2: Horizontal Scaling (Multiple ksqlDB Instances)** ✅ Recommended

**Pattern**: Multiple smaller ksqlDB instances behind a load balancer

#### **Example: 500 Total Queries**

**Configuration per instance:**
```yaml
# Each of 10 instances handles 50 queries
ksqldb-server-1 through ksqldb-server-10:
  deploy:
    resources:
      limits:
        cpus: '60'     # 50 queries × 2 threads × 0.5 CPU + overhead
        memory: 6G     # 1GB + (50 × 30MB) × 1.75
```

**Benefits:**
- ✅ Fault isolation (1 instance fails, 90% still running)
- ✅ Easier to manage per-instance resources
- ✅ Can scale up/down by adding/removing instances
- ✅ Better resource utilization
- ✅ Easier to debug and monitor

**Total Resources:**
- **10 instances × 60 cores = 600 cores**
- **10 instances × 6 GB = 60 GB**

---

### **Option 3: Multi-Tenancy with Query Routing** ✅ Best for Your Use Case

**Pattern**: Route users/teams to dedicated ksqlDB instances

```
Load Balancer (with sticky sessions)
    ↓
    ├─→ ksqldb-team-a (50 queries)  ← Team A users
    ├─→ ksqldb-team-b (75 queries)  ← Team B users
    ├─→ ksqldb-team-c (30 queries)  ← Team C users
    └─→ ksqldb-shared (100 queries) ← Everyone else
```

**Benefits:**
- ✅ Resource isolation per tenant
- ✅ Fair resource allocation
- ✅ Easier cost attribution
- ✅ Independent scaling per team

---

## 💰 Real-World Resource Considerations

### **Factors that Affect Resource Usage**

| Factor | Impact on Resources | Mitigation |
|--------|---------------------|------------|
| **Query complexity** | Complex JOINs use 2-5x more CPU/memory | Limit JOINs via validation |
| **Data volume** | High throughput = more CPU for deserialization | Limit buffer sizes |
| **State stores** | Windowed aggregations = more memory | Limit window sizes |
| **Serdes** | Avro/Protobuf = more CPU than JSON | Use efficient formats |
| **Number of partitions** | More partitions = more threads utilized | Balance partitions |

### **Typical Query Resource Usage**

| Query Type | CPU (per query) | Memory (per query) | Example |
|------------|-----------------|--------------------|---------| 
| **Simple filter** | 0.5-1 core | 20-30 MB | `SELECT * FROM orders WHERE amount > 100` |
| **Single JOIN** | 1-2 cores | 50-100 MB | `SELECT * FROM orders JOIN customers` |
| **Multiple JOINs** | 2-4 cores | 100-200 MB | `SELECT * FROM a JOIN b JOIN c` |
| **Windowed aggregation** | 1-3 cores | 100-500 MB | `SELECT WINDOWSTART, COUNT(*) FROM ...` |

---

## 🎛️ Optimal Settings for High Concurrency

### **Recommended ksqlDB Environment Variables**

```yaml
environment:
  # Thread configuration (2 threads per query)
  KSQL_KSQL_STREAMS_NUM_STREAM_THREADS: 2
  
  # Memory limits per query
  KSQL_CACHE_MAX_BYTES_BUFFERING: 5242880  # 5MB (reduced from 10MB)
  KSQL_KSQL_STREAMS_BUFFER_MEMORY: 16777216  # 16MB total (reduced from 32MB)
  
  # Commit frequently to reduce memory usage
  KSQL_KSQL_STREAMS_COMMIT_INTERVAL_MS: 1000  # 1 second (reduced from 2s)
  
  # Reduce state store cache
  KSQL_KSQL_STREAMS_STATE_DIR: /tmp/kafka-streams
  
  # JVM tuning for many threads
  KSQL_HEAP_OPTS: "-Xms8G -Xmx8G -XX:+UseG1GC -XX:MaxGCPauseMillis=100 -XX:+ParallelRefProcEnabled"
  
  # Network and consumer tuning
  KSQL_CONSUMER_MAX_POLL_RECORDS: 100
  KSQL_KSQL_STREAMS_MAX_TASK_IDLE_MS: 100
```

### **Why These Settings?**

1. **Smaller buffers**: Reduces memory per query (5MB vs 10MB) = 2x more queries
2. **Frequent commits**: Reduces in-memory state, prevents memory bloat
3. **G1GC**: Better garbage collection for applications with many threads
4. **Lower poll records**: Reduces memory spikes from batching

---

## 📈 Scaling Strategy: Start Small, Scale Up

### **Phase 1: Initial Deployment (Current)**
- **Target**: 50 queries
- **Resources**: 2 cores, 2GB
- **Purpose**: Proof of concept, testing

### **Phase 2: Team Rollout**
- **Target**: 200 queries
- **Resources**: 4 instances × (60 cores, 6GB) = 240 cores, 24GB
- **Purpose**: Production for multiple teams

### **Phase 3: Organization-Wide**
- **Target**: 500+ queries
- **Resources**: 10+ instances (scale horizontally)
- **Purpose**: Full organizational deployment

---

## 🔍 Monitoring & Right-Sizing

### **Key Metrics to Monitor**

1. **CPU Usage**
   - Target: 60-70% average (leaves headroom for spikes)
   - Alert: > 85% sustained

2. **Memory Usage**
   - JVM heap: 70-80% utilized
   - Alert: > 90% or frequent full GCs

3. **Thread Count**
   - Monitor actual thread count vs expected
   - Alert: Unexpected thread growth

4. **GC Pauses**
   - Target: < 100ms
   - Alert: > 500ms pauses

5. **Query Latency**
   - Track end-to-end latency per query
   - Alert: Degradation over time

### **Tools**

- **JMX Metrics**: Expose ksqlDB JMX for Prometheus
- **Grafana Dashboards**: Visualize resource usage
- **Alerting**: Set up alerts for resource exhaustion

---

## 🎯 Final Recommendations

### **For 100 Queries:**
```yaml
# Single instance is fine
cpus: '120'
memory: 8G
KSQL_HEAP_OPTS: "-Xms4G -Xmx4G"
```

### **For 200-500 Queries:**
```yaml
# Use 4-10 instances, each handling 50 queries
# Example: 10 instances
instances: 10
per_instance:
  cpus: '60'
  memory: 6G
  KSQL_HEAP_OPTS: "-Xms3G -Xmx3G"
  max_queries: 50
```

### **For 500+ Queries:**
```yaml
# Horizontal scaling + load balancing + auto-scaling
# Consider Kubernetes for orchestration
instances: 10-20 (auto-scale)
per_instance:
  cpus: '60-120'
  memory: 6-8G
  max_queries: 50-100
```

---

## 📚 Summary

| Queries | Threads | CPU Needed | Memory Needed | Recommended Architecture |
|---------|---------|------------|---------------|--------------------------|
| 50 | 100 | 60-90 cores | 4-6 GB | Single instance |
| 100 | 200 | 120-180 cores | 7-8 GB | Single instance or 2×50 |
| 200 | 400 | 240-360 cores | 14-16 GB | 4 instances × 50 queries |
| 500 | 1000 | 600-900 cores | 28-32 GB | 10 instances × 50 queries |

**Key Takeaway**: Beyond 100 queries, **horizontal scaling** (multiple instances) is more efficient and resilient than vertical scaling (one huge instance).
