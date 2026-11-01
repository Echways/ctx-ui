#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"
suffix="$$-$(date +%s)"
label="ctx-ui.builder-test=1"
runtime_image="ctx-ui-builder-test-runtime:$suffix"
egress_image="ctx-ui-builder-test-egress:$suffix"
network="ctx-ui-builder-test-net-$suffix"
egress="ctx-ui-builder-test-egress-$suffix"
server="ctx-ui-builder-test-server-$suffix"
proxy="http://$egress:3128"
fastmcp_version="4.1.4"
listener_pid=""
failures=0

cleanup() {
    if [ -n "$listener_pid" ]; then
        kill "$listener_pid" 2> /dev/null || true
    fi
    docker ps -aq --filter "label=$label" | xargs -r docker rm -f > /dev/null 2>&1 || true
    docker network ls -q --filter "label=$label" | xargs -r docker network rm > /dev/null 2>&1 || true
    docker volume ls -q --filter "label=$label" | xargs -r docker volume rm -f > /dev/null 2>&1 || true
    docker rmi -f "$runtime_image" "$egress_image" > /dev/null 2>&1 || true
    rm -rf "$work"
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

contains() {
    [[ "$2" == *"$1"* ]] || { echo "      expected to contain: $1"; echo "      actual: $2"; return 1; }
}

lacks() {
    [[ "$2" != *"$1"* ]] || { echo "      expected not to contain: $1"; echo "      actual: $2"; return 1; }
}

equals() {
    [ "$1" = "$2" ] || { echo "      expected: $1"; echo "      actual: $2"; return 1; }
}

fails() {
    ! "$@" > /dev/null 2>&1
}

volume() {
    docker volume create --label "$label" "$1" > /dev/null
}

hardened=(--read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp --memory 512m --cpus 1 --pids-limit 256)
proxied=(--env "HTTP_PROXY=$proxy" --env "HTTPS_PROXY=$proxy" --env "NO_PROXY=127.0.0.1,localhost" --env UV_CACHE_DIR=/uv-cache)

run_runtime() {
    docker run --rm --label "$label" --network "$network" "${hardened[@]}" "${proxied[@]}" "$@"
}

prepare() {
    run_runtime -v "$1-code:/code:ro" -v "$1-env:/env" -v "$1-cache:/uv-cache" "$runtime_image" python /opt/builder/prepare.py
}

start_server() {
    docker run -d --name "$server" --label "$label" --network "$network" "${hardened[@]}" "${proxied[@]}" \
        --env BUILDER_BASE_URL=https://pypi.org --env BUILDER_API_HEADERS='{"X-Builder-Test":"1"}' \
        -v "$1-code:/code:ro" -v "$1-env:/env:ro" -v "$1-data:/data" \
        "$runtime_image" /env/.venv/bin/fastmcp run /code/fastmcp.json --skip-env --no-banner > /dev/null
}

seed_project() {
    local name="$1" source="$2"
    volume "$name-code"
    volume "$name-env"
    volume "$name-cache"
    volume "$name-data"
    docker run --rm --label "$label" --user 0 -v "$name-code:/code" -v "$source:/source:ro" "$runtime_image" \
        sh -c 'cp -a /source/. /code/ && cp /opt/builder/main.py /code/main.py && chown -R 10001:10001 /code'
}

healthy() {
    for _ in $(seq 1 60); do
        if [ "$(docker exec "$server" python -c "import urllib.request;print(urllib.request.urlopen('http://127.0.0.1:8000/healthz').read().decode())" 2> /dev/null || true)" = "ok" ]; then
            return 0
        fi
        sleep 1
    done
    docker logs "$server" 2>&1 | tail -n 20
    return 1
}

read -r -d '' client_script <<'PY' || true
import asyncio
import json
import sys

from fastmcp import Client


async def main(action, argument):
    async with Client("http://127.0.0.1:8000/mcp") as client:
        if action == "tools":
            print(" ".join(sorted(tool.name for tool in await client.list_tools())))
        elif action == "resources":
            print(" ".join(str(resource.uri) for resource in await client.list_resources()))
        elif action == "prompts":
            print(" ".join(prompt.name for prompt in await client.list_prompts()))
        elif action == "call":
            name, arguments = argument.split(" ", 1)
            result = await client.call_tool(name, json.loads(arguments))
            print(json.dumps(result.data if result.data is not None else result.content[0].text, default=str))
        elif action == "read":
            print((await client.read_resource(argument))[0].text)
        elif action == "render":
            name, arguments = argument.split(" ", 1)
            result = await client.get_prompt(name, json.loads(arguments))
            print(result.messages[0].content.text)


asyncio.run(main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else ""))
PY

mcp() {
    docker exec -i "$server" /env/.venv/bin/python -c "$client_script" "$@"
}

via_proxy_status() {
    docker exec --env NO_PROXY= --env no_proxy= "$server" python -c "
import sys, urllib.request, urllib.error
opener = urllib.request.build_opener(urllib.request.ProxyHandler({'http': '$proxy', 'https': '$proxy'}))
try:
    print(opener.open(sys.argv[1], timeout=15).status)
except urllib.error.HTTPError as error:
    print(error.code)
except Exception as error:
    print(type(error).__name__)
" "$1"
}

reaches() {
    docker exec "$server" python -c "
import socket, sys
try:
    socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=4).close()
except OSError:
    sys.exit(1)
" "$1" "$2"
}

write_hello_project() {
    local dir="$1"
    mkdir -p "$dir/mcp"
    cat > "$dir/fastmcp.json" <<JSON
{
  "source": {"path": "main.py", "entrypoint": "mcp"},
  "environment": {"type": "uv", "python": "3.13", "dependencies": ["fastmcp==$fastmcp_version"]},
  "deployment": {"transport": "http", "host": "0.0.0.0", "port": 8000, "path": "/mcp"}
}
JSON
    cat > "$dir/mcp/hello.py" <<'PY'
from fastmcp.tools import tool


@tool
def hello(name: str) -> str:
    return f"Hello, {name}"
PY
    cat > "$dir/openapi.json" <<'JSON'
{
  "openapi": "3.1.0",
  "info": {"title": "PyPI", "version": "1"},
  "servers": [{"url": "https://pypi.org"}],
  "paths": {
    "/pypi/{package}/json": {
      "get": {
        "operationId": "get_package",
        "parameters": [{"name": "package", "in": "path", "required": true, "schema": {"type": "string"}}],
        "responses": {
          "200": {
            "description": "ok",
            "content": {"application/json": {"schema": {"type": "object", "properties": {"info": {"type": "object"}}}}}
          }
        }
      }
    },
    "/pypi/{package}/{version}/json": {
      "get": {
        "operationId": "get_package_mismatched",
        "parameters": [
          {"name": "package", "in": "path", "required": true, "schema": {"type": "string"}},
          {"name": "version", "in": "path", "required": true, "schema": {"type": "string"}}
        ],
        "responses": {
          "200": {
            "description": "ok",
            "content": {"application/json": {"schema": {"type": "object", "properties": {"info": {"type": "string"}}, "required": ["info", "absent"]}}}
          }
        }
      }
    }
  }
}
JSON
    cat > "$dir/builder.json" <<'JSON'
{
  "resources": [{"uri": "data://cities", "name": "cities", "mimeType": "application/json", "text": "[\"Moscow\"]"}],
  "prompts": [{"name": "daily_report", "description": "Daily report", "template": "Report for {day} by {author}"}]
}
JSON
}

echo "== images"
docker build -q -t "$runtime_image" "$root/extensions/builder/runtime" > /dev/null || { echo "FAIL  the runtime image builds"; exit 1; }
docker build -q -t "$egress_image" "$root/extensions/builder/egress" > /dev/null || { echo "FAIL  the egress image builds"; exit 1; }
echo "ok    both images build"

check "the code directories belong to 10001" equals "10001:10001 10001:10001 10001:10001 10001:10001" \
    "$(docker run --rm "$runtime_image" stat -c '%u:%g' /code /env /data /uv-cache | paste -sd ' ')"
check "prepare is in the image" docker run --rm "$runtime_image" test -f /opt/builder/prepare.py

echo "== network"
docker network create --internal --label "$label" --opt com.docker.network.bridge.gateway_mode_ipv4=isolated "$network" > /dev/null
docker run -d --name "$egress" --label "$label" --network bridge "${hardened[@]}" "$egress_image" > /dev/null
docker network connect --alias egress "$network" "$egress"
egress_ip="$(docker inspect -f "{{(index .NetworkSettings.Networks \"$network\").IPAddress}}" "$egress")"
gateway="$(docker network inspect -f '{{(index .IPAM.Config 0).Gateway}}' "$network")"
python3 -m http.server 18899 --bind 0.0.0.0 --directory "$work" > /dev/null 2>&1 &
listener_pid=$!
sleep 1

echo "== project"
project="$work/project"
write_hello_project "$project"
seed_project hello "$project"
check "prepare works on a read-only root" prepare hello
check "prepare writes the lock" contains "fastmcp==$fastmcp_version" \
    "$(docker run --rm -v hello-env:/env:ro "$runtime_image" cat /env/requirements.lock 2> /dev/null || true)"

echo "== server"
start_server hello
check "the server answers /healthz with ok" healthy
check "the server runs as 10001" equals "10001" "$(docker exec "$server" id -u)"
check "writing to /code is refused" fails docker exec "$server" touch /code/x
check "writing to / is refused" fails docker exec "$server" touch /x
check "writing to /data works" docker exec "$server" touch /data/x
check "tools/list has hello and the operation" equals "get_package get_package_mismatched hello" "$(mcp tools)"
check "resources/list has one item" equals "data://cities" "$(mcp resources)"
check "prompts/list has one item" equals "daily_report" "$(mcp prompts)"
check "the resource is readable" equals '["Moscow"]' "$(mcp read data://cities)"
check "the prompt renders its arguments" equals "Report for monday by me" "$(mcp render 'daily_report {"day":"monday","author":"me"}')"
check "a local tool is callable" contains "Hello, world" "$(mcp call 'hello {"name":"world"}')"
check "the operation returns data through the proxy" contains '"name": "fastmcp"' "$(mcp call 'get_package {"package":"fastmcp"}')"
check "a response that breaks the schema does not break the call" contains '"name": "fastmcp"' "$(mcp call 'get_package_mismatched {"package":"fastmcp","version":"4.1.4"}')"

echo "== egress"
check "public HTTPS passes the proxy" equals "200" "$(via_proxy_status https://pypi.org/simple/)"
check "the cloud metadata address is refused" equals "403" "$(via_proxy_status http://169.254.169.254/)"
check "localhost is refused" equals "403" "$(via_proxy_status http://localhost:8000/)"
check "the loopback address is refused" equals "403" "$(via_proxy_status http://127.0.0.1:8000/)"
check "the proxy itself is refused" equals "403" "$(via_proxy_status "http://$egress_ip:3128/")"
check "a private address is refused" equals "403" "$(via_proxy_status http://10.0.0.1/)"
check "a mail port is refused" equals "403" "$(via_proxy_status http://pypi.org:25/)"
check "a direct connection to the internet fails" fails reaches 1.1.1.1 443
check "a host service through the gateway fails" fails reaches "$gateway" 18899
check "the proxy runs on a read-only root" equals "true" "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$egress")"
check "the proxy runs as non-root" fails docker exec --user squid "$egress" sh -c 'test "$(id -u)" = 0'
docker rm -f "$server" > /dev/null

echo "== name conflict"
conflict="$work/conflict"
write_hello_project "$conflict"
cat > "$conflict/mcp/clash.py" <<'PY'
from fastmcp.tools import tool


@tool
def get_package(package: str) -> str:
    return package
PY
seed_project conflict "$conflict"
prepare conflict
code=0
timeout 60 docker run --rm --label "$label" --network "$network" "${hardened[@]}" "${proxied[@]}" --env BUILDER_BASE_URL=https://pypi.org \
    -v conflict-code:/code:ro -v conflict-env:/env:ro "$runtime_image" \
    /env/.venv/bin/fastmcp run /code/fastmcp.json --skip-env --no-banner > "$work/conflict.log" 2>&1 || code=$?
check "a tool name from two providers stops the server" [ "$code" -ne 0 ]
check "the failure names the tool" contains "get_package" "$(cat "$work/conflict.log")"

echo "== openapi without a base url"
docker run --rm --label "$label" --network "$network" "${hardened[@]}" -v hello-code:/code:ro -v hello-env:/env:ro \
    "$runtime_image" /env/.venv/bin/fastmcp run /code/fastmcp.json --skip-env --no-banner > "$work/nobase.log" 2>&1 || true
check "a missing base url fails clearly" contains "BUILDER_BASE_URL" "$(cat "$work/nobase.log")"

echo "== plain project"
plain="$work/plain"
mkdir -p "$plain/mcp"
cp "$project/fastmcp.json" "$plain/fastmcp.json"
cp "$project/mcp/hello.py" "$plain/mcp/hello.py"
seed_project plain "$plain"
prepare plain
start_server plain
check "a project with only code starts" healthy
check "a project with only code lists its tool" equals "hello" "$(mcp tools)"

if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all checks passed"
