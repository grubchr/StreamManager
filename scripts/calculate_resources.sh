#!/bin/bash

# ksqlDB Resource Calculator
# Usage: ./calculate_resources.sh <num_queries> <threads_per_query>

QUERIES=${1:-100}
THREADS_PER_QUERY=${2:-2}

echo "╔════════════════════════════════════════════════════════════════╗"
echo "║         ksqlDB Resource Calculator                             ║"
echo "╚════════════════════════════════════════════════════════════════╝"
echo ""
echo "Input Parameters:"
echo "  • Target Queries: $QUERIES"
echo "  • Threads per Query: $THREADS_PER_QUERY"
echo ""

# Calculate total threads
TOTAL_THREADS=$((QUERIES * THREADS_PER_QUERY))
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "📊 Thread Calculation"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Total Threads = $QUERIES × $THREADS_PER_QUERY = $TOTAL_THREADS threads"
echo ""

# Calculate CPU (optimistic and conservative)
CPU_OPTIMISTIC=$(echo "$TOTAL_THREADS * 0.5 * 1.2" | bc)
CPU_CONSERVATIVE=$(echo "$TOTAL_THREADS * 0.75 * 1.2" | bc)

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "⚙️  CPU Requirements"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
printf "  Optimistic:    %.0f cores (0.5 CPU/thread + 20%% overhead)\n" $CPU_OPTIMISTIC
printf "  Conservative:  %.0f cores (0.75 CPU/thread + 20%% overhead)\n" $CPU_CONSERVATIVE
echo ""

# Calculate Memory
MEMORY_PER_QUERY_MB=30
BASE_HEAP_GB=1
QUERY_HEAP_GB=$(echo "$QUERIES * $MEMORY_PER_QUERY_MB / 1024" | bc -l)
TOTAL_HEAP_GB=$(echo "$BASE_HEAP_GB + $QUERY_HEAP_GB" | bc -l)
CONTAINER_MEMORY_GB=$(echo "$TOTAL_HEAP_GB * 1.75" | bc -l)

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "💾 Memory Requirements"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
printf "  JVM Heap:      %.1f GB (1 GB base + %.1f GB for queries)\n" $TOTAL_HEAP_GB $QUERY_HEAP_GB
printf "  Container:     %.1f GB (heap × 1.75 for off-heap)\n" $CONTAINER_MEMORY_GB
echo ""

# Recommended architecture
if [ $QUERIES -le 50 ]; then
    ARCHITECTURE="Single instance"
    INSTANCES=1
    QUERIES_PER_INSTANCE=$QUERIES
elif [ $QUERIES -le 100 ]; then
    ARCHITECTURE="1-2 instances"
    INSTANCES=2
    QUERIES_PER_INSTANCE=$((QUERIES / INSTANCES))
else
    INSTANCES=$(((QUERIES + 49) / 50))  # Round up to nearest 50
    QUERIES_PER_INSTANCE=50
    ARCHITECTURE="$INSTANCES instances (horizontal scaling)"
fi

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "🏗️  Recommended Architecture"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Pattern:       $ARCHITECTURE"
echo "  Instances:     $INSTANCES"
echo "  Queries/inst:  $QUERIES_PER_INSTANCE"
echo ""

# Per-instance resources (if multiple)
if [ $INSTANCES -gt 1 ]; then
    PER_INST_CPU_OPT=$(echo "$CPU_OPTIMISTIC / $INSTANCES" | bc)
    PER_INST_CPU_CONS=$(echo "$CPU_CONSERVATIVE / $INSTANCES" | bc)
    PER_INST_MEM=$(echo "$CONTAINER_MEMORY_GB / $INSTANCES" | bc -l)
    
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "📦 Per-Instance Resources"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    printf "  CPU:           %d-%d cores per instance\n" $PER_INST_CPU_OPT $PER_INST_CPU_CONS
    printf "  Memory:        %.1f GB per instance\n" $PER_INST_MEM
    echo ""
fi

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "🐳 Docker Compose Configuration"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if [ $INSTANCES -eq 1 ]; then
    # Single instance config
    HEAP_GB=$(printf "%.0f" $TOTAL_HEAP_GB)
    MEM_GB=$(printf "%.0f" $(echo "$CONTAINER_MEMORY_GB + 0.5" | bc))
    CPU=$(printf "%.0f" $CPU_OPTIMISTIC)
    
    cat <<YAML
  ksqldb-server:
    deploy:
      resources:
        limits:
          cpus: '$CPU'
          memory: ${MEM_GB}G
    environment:
      KSQL_KSQL_STREAMS_NUM_STREAM_THREADS: $THREADS_PER_QUERY
      KSQL_HEAP_OPTS: "-Xms${HEAP_GB}G -Xmx${HEAP_GB}G"
YAML
else
    # Multiple instance config
    PER_CPU=$(printf "%.0f" $PER_INST_CPU_OPT)
    PER_MEM=$(printf "%.0f" $(echo "$PER_INST_MEM + 0.5" | bc))
    PER_HEAP=$(printf "%.0f" $(echo "$PER_INST_MEM / 1.75" | bc))
    
    for i in $(seq 1 $INSTANCES); do
        cat <<YAML
  ksqldb-server-$i:
    deploy:
      resources:
        limits:
          cpus: '$PER_CPU'
          memory: ${PER_MEM}G
    environment:
      KSQL_KSQL_SERVICE_ID: "stream_manager_${i}_"
      KSQL_KSQL_STREAMS_NUM_STREAM_THREADS: $THREADS_PER_QUERY
      KSQL_HEAP_OPTS: "-Xms${PER_HEAP}G -Xmx${PER_HEAP}G"

YAML
    done
fi

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "💡 Notes:"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  • These are estimates based on simple queries"
echo "  • Complex queries (JOINs, windows) need 2-5x more resources"
echo "  • Monitor actual usage and adjust accordingly"
echo "  • See CAPACITY_PLANNING.md for detailed guidelines"
echo ""
