#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="ctx-ui-docker-test"
version="0.0.0-test"
stack="$(mktemp -d)"
failures=0

compose() {
    docker compose --project-directory "$stack" -p "$project" "$@"
}

cleanup() {
    compose down -v > /dev/null 2>&1 || true
    rm -rf "$stack"
}
trap cleanup EXIT

check() {
    local name="$1"
    shift
    if "$@"; then
        echo "ok    $name"
    else
        echo "FAIL  $name"
        failures=$((failures + 1))
    fi
}

equals() {
    [ "$1" = "$2" ] || { echo "      expected: $1"; echo "      actual:   $2"; return 1; }
}

contains() {
    [[ "$2" == *"$1"* ]] || { echo "      expected to contain: $1"; echo "      actual: $2"; return 1; }
}

response_becomes() {
    local expected="$1" actual=""
    shift
    for _ in $(seq 1 30); do
        actual="$(curl -sk -w ' %{http_code}' "$@" https://localhost:18443/mcp/echo || true)"
        if [[ "$actual" == *"$expected" ]]; then
            return 0
        fi
        sleep 1
    done
    echo "      expected to end with: $expected"
    echo "      actual: $actual"
    return 1
}

docker build -q -f "$root/deploy/docker/Dockerfile" --build-arg "VERSION=$version" \
    -t "ghcr.io/echways/ctx-ui:$version" "$root" > /dev/null

cp "$root/deploy/docker/compose.yaml" "$root/deploy/docker/Caddyfile" "$stack/"
sed -e "s/^CTX_UI_VERSION=.*/CTX_UI_VERSION=$version/" -e "s/^CTX_UI_DOMAIN=.*/CTX_UI_DOMAIN=localhost/" \
    "$root/deploy/docker/.env.example" > "$stack/.env"
cat > "$stack/compose.override.yaml" <<'YAML'
services:
  caddy:
    ports: !override
      - "18080:80"
      - "18443:443"
  upstream:
    image: caddy:2
    command: caddy respond --listen :9000 "pong"
YAML

echo "== settings"
mv "$stack/.env" "$stack/.env.saved"
output="$(compose config 2>&1)" && code=0 || code=$?
check "compose refuses to start without a domain" equals 1 "$code"
check "refusal names the variable" contains "CTX_UI_" "$output"
mv "$stack/.env.saved" "$stack/.env"

compose up -d > /dev/null 2>&1

echo "== container"
check "runs without root" equals 1654 "$(compose exec -T ctx-ui id -u)"
check "data directory is private" equals "700 1654" "$(compose exec -T ctx-ui stat -c '%a %u' /data)"

echo "== command line"
output="$(compose exec -T ctx-ui ctx-ui inbound add echo --url http://upstream:9000/mcp 2>&1)" || true
check "inbound address uses the domain" contains "https://localhost/mcp/echo" "$output"
output="$(compose exec -T ctx-ui ctx-ui client add probe --inbound echo 2>&1)" || true
token="$(echo "$output" | grep -o -E 'tnk_[A-Za-z0-9]{32}' | head -n 1 || true)"
check "client add prints a token" [ -n "$token" ]

echo "== through caddy"
check "request without a token is rejected" response_becomes " 401"
check "request with the token reaches the upstream" response_becomes "pong 200" -H "Authorization: Bearer $token"

echo "== restart"
compose restart ctx-ui > /dev/null 2>&1
check "client survives a restart" response_becomes "pong 200" -H "Authorization: Bearer $token"

if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all checks passed"
