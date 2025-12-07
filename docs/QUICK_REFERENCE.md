# Stream Manager - Quick Reference Card

**Version:** 1.0.0  
**Updated:** December 7, 2025

---

## 🚀 Quick Start

### Start with ksqlDB
```bash
docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
cd StreamManager.Api && dotnet run
```

### Start with Flink
```bash
docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
# Wait 60 seconds
cd StreamManager.Api && dotnet run
```

---

## ⚙️ Configuration

### appsettings.json
```json
{
  "StreamEngine": {
    "Provider": "KsqlDb"  // or "Flink"
  }
}
```

### Switch Engines
1. Stop API
2. Change `Provider` in `appsettings.json`
3. Stop old docker services
4. Start new docker services
5. Start API

---

## 📝 SQL Syntax Quick Reference

| Feature | ksqlDB | Flink |
|---------|--------|-------|
| **Streaming** | `EMIT CHANGES` | *(none)* |
| **Persistent** | `CREATE STREAM AS` | `INSERT INTO` |
| **Window** | `WINDOW TUMBLING (SIZE 5 MINUTES)` | `TUMBLE(col, INTERVAL '5' MINUTE)` |
| **Limit** | Ad-hoc only | Anywhere |

---

## 🔍 Common Queries

### Ad-Hoc Filter (Both Engines)

**ksqlDB:**
```sql
SELECT * FROM orders WHERE amount > 100 EMIT CHANGES LIMIT 10;
```

**Flink:**
```sql
SELECT * FROM orders WHERE amount > 100 LIMIT 10;
```

---

### Aggregation

**ksqlDB:**
```sql
SELECT customer_id, COUNT(*) as count
FROM orders
GROUP BY customer_id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT customer_id, COUNT(*) as count
FROM orders
GROUP BY customer_id;
```

---

## 🌐 Endpoints

| Service | Port | URL |
|---------|------|-----|
| **API** | 7068 | https://localhost:7068 |
| **Web UI** | 7122 | https://localhost:7122 |
| **ksqlDB** | 8088 | http://localhost:8088 |
| **Flink REST** | 8088 | http://localhost:8088 |
| **Flink SQL** | 8082 | http://localhost:8082 |
| **Kafka** | 9092 | localhost:9092 |
| **Postgres** | 5432 | localhost:5432 |

---

## 🔧 API Endpoints

### Streams
```bash
GET    /api/streams           # List all streams
POST   /api/streams           # Create stream
GET    /api/streams/{id}      # Get stream details
PUT    /api/streams/{id}      # Update stream
DELETE /api/streams/{id}      # Delete stream
POST   /api/streams/{id}/deploy  # Deploy stream
POST   /api/streams/{id}/stop    # Stop stream
```

### Testing
```bash
POST   /api/test/query        # Test ad-hoc query
POST   /api/test/direct       # Direct engine test
```

---

## 🐛 Troubleshooting

### ksqlDB: Connection Refused
```bash
# Wait 15-30 seconds after docker-compose up
docker logs ksqldb-server
```

### Flink: Session Creation Failed
```bash
# Wait 60 seconds after docker-compose up
docker logs flink-sql-gateway
curl http://localhost:8082/v1/info
```

### Build Errors
```bash
cd StreamManager.Api
dotnet clean
dotnet build
```

### Database Issues
```bash
cd StreamManager.Api
dotnet ef database update
```

---

## 📊 Health Checks

### ksqlDB
```bash
curl http://localhost:8088/info
```

### Flink
```bash
# SQL Gateway
curl http://localhost:8082/v1/info

# REST API
curl http://localhost:8088/overview
```

### Kafka
```bash
docker exec -it broker kafka-topics --list --bootstrap-server localhost:9092
```

### API
```bash
curl https://localhost:7068/api/streams -k
```

---

## 🚨 Common Errors

### "EMIT CHANGES is not valid Flink SQL syntax"
**Solution:** Remove `EMIT CHANGES` from your query (Flink mode)

### "Persistent queries cannot have LIMIT"
**Solution:** Remove `LIMIT` from persistent queries (ksqlDB mode)

### "Failed to create Flink session"
**Solution:** 
1. Check Flink services are running
2. Wait 60 seconds after startup
3. Check logs: `docker logs flink-sql-gateway`

### "Stream has no job ID to terminate"
**Solution:** Stream was never deployed or already stopped

---

## 📚 Documentation

- **Quick Start:** `QUICK_START.md`
- **Architecture:** `ENGINE_ABSTRACTION_PLAN.md`
- **Flink Guide:** `PHASE4_IMPLEMENTATION.md`
- **SQL Migration:** `FLINK_MIGRATION.md`
- **Full Summary:** `COMPLETE_IMPLEMENTATION_SUMMARY.md`

---

## 💡 Tips

1. **ksqlDB** is simpler for getting started
2. **Flink** is better for large scale (100+ queries)
3. Use `LIMIT` in ad-hoc queries to avoid overwhelming the system
4. Monitor Flink Web UI at http://localhost:8088
5. Check logs when things go wrong
6. Test queries with `/api/test/query` endpoint first

---

## 🎯 Best Practices

### Query Writing
- ✅ Always use WHERE clauses
- ✅ Add LIMIT to ad-hoc queries
- ✅ Test queries before deploying
- ❌ Don't use too many JOINs (max 3)
- ❌ Don't use too many WINDOWs (max 2)

### Operations
- ✅ Monitor resource usage
- ✅ Stop streams when not needed
- ✅ Clean up old streams
- ✅ Regular database backups
- ❌ Don't deploy untested queries

---

## 🔐 Security Checklist

- [ ] Change default passwords
- [ ] Enable TLS/SSL
- [ ] Add authentication
- [ ] Restrict network access
- [ ] Enable audit logging
- [ ] Regular security updates

---

## 📞 Support

**Issues?** Check documentation in `/docs` folder  
**Questions?** Review `QUICK_START.md`  
**Bugs?** Check build status: `dotnet build`

---

**Quick Reference Version 1.0.0**  
*Keep this handy for daily operations!*
