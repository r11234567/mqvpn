#!/usr/bin/env bash
# Test H2 proxy with Proxy Protocol v2 support
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
BUILD_DIR="${BUILD_DIR:-$PROJECT_ROOT/build}"

echo "=== H2 Proxy Protocol Test ==="
echo "Build dir: $BUILD_DIR"

# Check if binaries exist
if [ ! -f "$BUILD_DIR/mqvpn" ]; then
    echo "ERROR: mqvpn binary not found at $BUILD_DIR/mqvpn"
    exit 1
fi

# Create test directory
TEST_DIR=$(mktemp -d -t mqvpn-h2-proxy-test.XXXXXX)
trap "rm -rf $TEST_DIR" EXIT

echo "Test dir: $TEST_DIR"

# Generate certificates
echo "[1/6] Generating test certificates..."
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 1 \
    -keyout "$TEST_DIR/server.key" \
    -out "$TEST_DIR/server.crt" \
    -subj "/CN=localhost" 2>/dev/null

openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 1 \
    -keyout "$TEST_DIR/client.key" \
    -out "$TEST_DIR/client.crt" \
    -subj "/CN=client" 2>/dev/null

# Create Nginx config with Proxy Protocol support
echo "[2/6] Creating Nginx config..."
cat > "$TEST_DIR/nginx.conf" <<'EOF'
worker_processes 1;
error_log /dev/stderr info;
pid /tmp/nginx-h2-proxy-test.pid;
daemon off;

events {
    worker_connections 64;
}

http {
    access_log /dev/stdout;

    # Upstream with Proxy Protocol
    server {
        listen 127.0.0.1:8444 http2 proxy_protocol;
        listen [::1]:8444 http2 proxy_protocol;
        server_name _;

        location / {
            # Log real client IP from Proxy Protocol
            add_header X-Real-IP $proxy_protocol_addr always;
            add_header X-Real-Port $proxy_protocol_port always;
            return 200 "Hello from Nginx via Proxy Protocol\nClient: $proxy_protocol_addr:$proxy_protocol_port\n";
        }
    }

    # Direct access (for comparison)
    server {
        listen 127.0.0.1:8445;
        listen [::1]:8445;
        server_name _;

        location / {
            return 200 "Hello from Nginx direct access\n";
        }
    }
}
EOF

# Start Nginx
echo "[3/6] Starting Nginx..."
nginx -c "$TEST_DIR/nginx.conf" -p "$TEST_DIR" &
NGINX_PID=$!
sleep 1

if ! kill -0 $NGINX_PID 2>/dev/null; then
    echo "ERROR: Nginx failed to start"
    exit 1
fi

trap "kill $NGINX_PID 2>/dev/null || true; rm -rf $TEST_DIR" EXIT

# Test direct Nginx access
echo "[4/6] Testing direct Nginx access..."
DIRECT_RESPONSE=$(curl -s http://127.0.0.1:8445/)
echo "Direct response: $DIRECT_RESPONSE"

if [[ ! "$DIRECT_RESPONSE" =~ "Hello from Nginx direct access" ]]; then
    echo "ERROR: Direct Nginx access failed"
    exit 1
fi

# Create mqvpn server config with H2 proxy
echo "[5/6] Creating mqvpn server config..."
cat > "$TEST_DIR/server.json" <<EOF
{
    "role": "server",
    "listen": "[::]:4433",
    "cert": "$TEST_DIR/server.crt",
    "key": "$TEST_DIR/server.key",
    "auth_key": "h2-proxy-protocol-test-key",
    "proxy": {
        "enabled": true,
        "sni": "test.example.com",
        "http2_backend": "127.0.0.1:8444",
        "http2_backend_proxy_protocol": true
    }
}
EOF

# Start mqvpn server
echo "[6/6] Starting mqvpn server with H2 proxy..."
"$BUILD_DIR/mqvpn" --config "$TEST_DIR/server.json" &
MQVPN_PID=$!
sleep 2

if ! kill -0 $MQVPN_PID 2>/dev/null; then
    echo "ERROR: mqvpn server failed to start"
    exit 1
fi

trap "kill $MQVPN_PID $NGINX_PID 2>/dev/null || true; rm -rf $TEST_DIR" EXIT

# Test H2 proxy with Proxy Protocol
echo ""
echo "=== Testing H2 Proxy with Proxy Protocol ==="

# Use curl to send HTTP/3 request through mqvpn
PROXY_RESPONSE=$(curl -k --http3-only \
    --resolve test.example.com:4433:127.0.0.1 \
    https://test.example.com:4433/ 2>&1 || echo "CURL_FAILED")

echo "Proxy response: $PROXY_RESPONSE"

# Check if response contains expected content
if [[ "$PROXY_RESPONSE" =~ "Hello from Nginx via Proxy Protocol" ]]; then
    echo "✓ Proxy Protocol test PASSED"

    # Extract and display client IP from Proxy Protocol
    if [[ "$PROXY_RESPONSE" =~ Client:\ ([0-9a-f:.]+):([0-9]+) ]]; then
        CLIENT_IP="${BASH_REMATCH[1]}"
        CLIENT_PORT="${BASH_REMATCH[2]}"
        echo "  Real client IP: $CLIENT_IP:$CLIENT_PORT"
    fi

    exit 0
else
    echo "✗ Proxy Protocol test FAILED"
    echo "  Expected: 'Hello from Nginx via Proxy Protocol'"
    echo "  Got: $PROXY_RESPONSE"
    exit 1
fi
