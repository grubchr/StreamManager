#!/bin/bash

# Array of product names
PRODUCTS=("Laptop" "Wireless Mouse" "Mechanical Keyboard" "USB-C Hub" "Monitor 27inch" "Webcam HD" "Desk Lamp LED" "Office Chair" "Headphones Wireless" "External SSD 1TB" "Gaming Mouse" "Laptop Stand" "USB Cable" "Power Bank" "Bluetooth Speaker" "Phone Case" "Screen Protector" "Wireless Charger" "Memory Card" "HDMI Cable")

# Counter for order IDs
ORDER_ID=1011

echo "Starting order generator - creating 1 order every 30 seconds"
echo "Press Ctrl+C to stop"
echo ""

while true; do
    # Generate random values
    CUSTOMER_ID=$((500 + RANDOM % 100))
    PRODUCT_INDEX=$((RANDOM % ${#PRODUCTS[@]}))
    PRODUCT="${PRODUCTS[$PRODUCT_INDEX]}"
    AMOUNT=$(awk -v min=10 -v max=1500 'BEGIN{srand(); printf "%.2f\n", min+rand()*(max-min)}')
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S")
    
    # Create INSERT statement
    INSERT_SQL="INSERT INTO orders (ORDER_ID, CUSTOMER_ID, PRODUCT, AMOUNT, PURCHASE_DATE) VALUES ($ORDER_ID, $CUSTOMER_ID, '$PRODUCT', $AMOUNT, '$TIMESTAMP');"
    
    echo "[$TIMESTAMP] Inserting Order #$ORDER_ID: $PRODUCT - \$$AMOUNT for Customer $CUSTOMER_ID"
    
    # Execute via ksqlDB REST API
    curl -s -X POST http://localhost:8088/ksql \
      -H "Content-Type: application/vnd.ksql.v1+json; charset=utf-8" \
      -d "{\"ksql\":\"$INSERT_SQL\",\"streamsProperties\":{}}" > /dev/null
    
    if [ $? -eq 0 ]; then
        echo "  ✓ Successfully inserted"
    else
        echo "  ✗ Failed to insert"
    fi
    
    echo ""
    
    # Increment order ID
    ORDER_ID=$((ORDER_ID + 1))
    
    # Wait 30 seconds
    sleep 30
done