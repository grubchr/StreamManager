#!/bin/bash

# Cleanup orphaned ksqlDB streams and topics
# Usage: ./cleanup-ksqldb.sh

echo "🧹 Cleaning up ksqlDB streams and topics..."
echo ""

# Show current state
echo "📊 Current Queries:"
docker exec ksqldb-cli ksql http://ksqldb-server:8088 --execute "SHOW QUERIES;" 2>/dev/null
echo ""

echo "📊 Current Streams:"
docker exec ksqldb-cli ksql http://ksqldb-server:8088 --execute "SHOW STREAMS;" 2>/dev/null
echo ""

echo "📊 Current Topics:"
docker exec ksqldb-cli ksql http://ksqldb-server:8088 --execute "SHOW TOPICS;" 2>/dev/null
echo ""

# Drop orphaned stream (has no running query)
echo "🗑️  Dropping VIEW_CUSTOMER525ORDERS_E0E2AE6D7AA4448ABB50341E39E2E369..."
docker exec ksqldb-cli ksql http://ksqldb-server:8088 --execute "DROP STREAM IF EXISTS VIEW_CUSTOMER525ORDERS_E0E2AE6D7AA4448ABB50341E39E2E369 DELETE TOPIC;" 2>/dev/null
echo ""

# Wait a moment for cleanup
sleep 2

echo "✅ Cleanup complete!"
echo ""
echo "📊 Final State:"
docker exec ksqldb-cli ksql http://ksqldb-server:8088 --execute "SHOW STREAMS;" 2>/dev/null
echo ""
docker exec ksqldb-cli ksql http://ksqldb-server:8088 --execute "SHOW TOPICS;" 2>/dev/null
