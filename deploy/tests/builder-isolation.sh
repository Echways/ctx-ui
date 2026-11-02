#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="ctxb-isolation-test"
version="0.0.0-isolation-test"
token="isolation-test-token"
secret="isolation-test-route-secret"
control="http://127.0.0.1:8091"
ingress="http://127.0.0.1:8090"
runner_ip="10.212.0.2"
runner="ctxb-runner"
egress="ctxb-egress"
control_network="ctxb-control"
host_port=18898
a_id="0b1d6f4e-6a3c-4f55-9d0e-1a2b3c4d5e61"
b_id="0b1d6f4e-6a3c-4f55-9d0e-1a2b3c4d5e62"
a="ctxb-${a_id//-/}"
b="ctxb-${b_id//-/}"
runner_image="ghcr.io/echways/ctx-ui-builder-runner:$version"
runtime_image="ghcr.io/echways/ctx-ui-builder-runtime:$version"
egress_image="ghcr.io/echways/ctx-ui-builder-egress:$version"
fastmcp_version="4.1.4"
built=1
failed=2
candidate=3

for port in 8090 8091 "$host_port"; do
    if (exec 3<> "/dev/tcp/127.0.0.1/$port") 2> /dev/null; then
        echo "port $port on 127.0.0.1 is already in use, nothing was changed"
        exit 1
    fi
done
for name in "$runner" "$egress"; do
    if docker inspect "$name" > /dev/null 2>&1; then
        echo "a container named $name already exists, nothing was changed"
        exit 1
    fi
done
if docker network inspect "$control_network" > /dev/null 2>&1; then
    echo "a network named $control_network already exists, nothing was changed"
    exit 1
fi

work="$(mktemp -d)"
failures=0
listener_pid=""
load_pid=""
events_pid=""
slow_pid=""

compose() {
    CTX_UI_VERSION="$version" CTXB_RUNNER_TOKEN="$token" docker compose -p "$project" -f "$root/deploy/builder/compose.yaml" "$@"
}

api() {
    curl -sS -H "Authorization: Bearer $token" "$@"
}

cleanup() {
    for pid in "$listener_pid" "$load_pid" "$events_pid" "$slow_pid"; do
        if [ -n "$pid" ]; then
            kill "$pid" 2> /dev/null || true
        fi
    done
    for id in "$a_id" "$b_id"; do
        api -m 60 -X DELETE "$control/v1/servers/$id" > /dev/null 2>&1 || true
        docker ps -aq --filter "label=ctx-ui.builder=$id" | xargs -r docker rm -f > /dev/null 2>&1 || true
    done
    compose down > /dev/null 2>&1 || true
    for id in "$a_id" "$b_id"; do
        docker network ls -q --filter "label=ctx-ui.builder=$id" | xargs -r docker network rm > /dev/null 2>&1 || true
        docker volume ls -q --filter "label=ctx-ui.builder=$id" | xargs -r docker volume rm -f > /dev/null 2>&1 || true
    done
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

equals() {
    [ "$1" = "$2" ] || { echo "      expected: $1"; echo "      actual:   $2"; return 1; }
}

contains() {
    [[ "$2" == *"$1"* ]] || { echo "      expected to contain: $1"; echo "      actual: $2"; return 1; }
}

lacks() {
    [[ "$2" != *"$1"* ]] || { echo "      expected not to contain: $1"; echo "      actual: $2"; return 1; }
}

matches() {
    [[ "$2" =~ $1 ]] || { echo "      expected to match: $1"; echo "      actual: $2"; return 1; }
}

at_least() {
    [ "$2" -ge "$1" ] 2> /dev/null || { echo "      expected at least: $1"; echo "      actual:   $2"; return 1; }
}

at_most() {
    [ "$2" -le "$1" ] 2> /dev/null || { echo "      expected at most: $1"; echo "      actual:   $2"; return 1; }
}

inspect() {
    docker inspect -f "$2" "$1"
}

release_state() {
    api "$control/v1/servers/$1/releases/$2" | jq -r '.state'
}

wait_for_state() {
    local id="$1" number="$2" wanted="$3" seconds="$4" state=""
    for _ in $(seq 1 "$seconds"); do
        state="$(release_state "$id" "$number" 2> /dev/null || true)"
        if [ "$state" = "$wanted" ]; then
            return 0
        fi
        if [ "$state" = "$failed" ]; then
            break
        fi
        sleep 1
    done
    echo "      release $number of $id is in state '$state', wanted $wanted"
    api "$control/v1/servers/$id/releases/$number" | jq -r '.error, .log' | tail -n 20
    return 1
}

put_spec() {
    local id="$1" network="$2" memory="$3"
    api -o /dev/null -w '%{http_code}' -X PUT -H 'Content-Type: application/json' \
        -d "{\"network\":$network,\"limits\":{\"memoryMb\":$memory,\"cpus\":1,\"pids\":256},\"routeSecret\":\"$secret\"}" \
        "$control/v1/servers/$id"
}

publish() {
    api -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/gzip' --data-binary "@$3" "$control/v1/servers/$1/releases/$2"
}

start_candidate() {
    api -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"environment":{}}' "$control/v1/servers/$1/releases/$2/start"
}

activate() {
    api -o /dev/null -w '%{http_code}' -m 120 -X POST "$control/v1/servers/$1/releases/$2/activate"
}

roll_out() {
    local id="$1" number="$2" status
    status="$(start_candidate "$id" "$number")"
    equals 202 "$status" || return 1
    wait_for_state "$id" "$number" "$candidate" 120 || return 1
    status="$(activate "$id" "$number")"
    equals 200 "$status"
}

ingress_call() {
    local id="$1" supplied="$2" tool="$3" arguments="$4" limit="${5:-30}" secret_header=()
    if [ -n "$supplied" ]; then
        secret_header=(-H "X-Builder-Route-Secret: $supplied")
    fi
    curl -sS -m "$limit" -w '\n%{http_code}' -X POST "${secret_header[@]}" \
        -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"$tool\",\"arguments\":$arguments}}" \
        "$ingress/s/$id/mcp" 2> /dev/null || true
}

ingress_status() {
    ingress_call "$@" | tail -n 1
}

ingress_becomes_ok() {
    local id="$1" answer=""
    for _ in $(seq 1 90); do
        answer="$(ingress_status "$id" "$secret" version '{}')"
        if [ "$answer" = 200 ]; then
            return 0
        fi
        sleep 1
    done
    echo "      the ingress for $id answered '$answer'"
    return 1
}

load_start() {
    : > "$2"
    (
        while [ ! -f "$work/stop-load" ]; do
            curl -sS -m 20 -o /dev/null -w '%{http_code}\n' -X POST -H "X-Builder-Route-Secret: $secret" \
                -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
                -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"version","arguments":{}}}' \
                "$ingress/s/$1/mcp" >> "$2" 2> /dev/null || echo 000 >> "$2"
            sleep 0.05
        done
    ) &
    load_pid=$!
}

load_stop() {
    touch "$work/stop-load"
    wait "$load_pid" || true
    rm -f "$work/stop-load"
    load_pid=""
}

load_was_clean() {
    local calls other
    calls="$(wc -l < "$1")"
    other="$(grep -vc '^200$' "$1" || true)"
    [ "$calls" -ge 20 ] && [ "$other" = 0 ] || { echo "      calls: $calls, not 200: $other"; sort "$1" | uniq -c; return 1; }
}

read -r -d '' outcome_script <<'PY' || true
import errno
import socket
import sys

try:
    socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=4).close()
    print("connected")
except TimeoutError:
    print("timeout")
except ConnectionRefusedError:
    print("refused")
except socket.gaierror:
    print("gaierror")
except OSError as failure:
    print(errno.errorcode.get(failure.errno, type(failure).__name__))
PY

outcome() {
    docker exec "$1" python -c "$outcome_script" "$2" "$3"
}

connects_within() {
    local attempts="$1" answer=""
    shift
    for _ in $(seq 1 "$attempts"); do
        answer="$(outcome "$@")"
        if [ "$answer" = connected ]; then
            return 0
        fi
    done
    echo "      the last answer was '$answer'"
    return 1
}

read -r -d '' http_script <<'PY' || true
import sys
import urllib.error
import urllib.request

try:
    print("http-%d" % urllib.request.urlopen(f"http://{sys.argv[1]}:{sys.argv[2]}/", timeout=6).status)
except urllib.error.HTTPError as failure:
    print("http-%d" % failure.code)
except Exception as failure:
    print(type(failure.reason if isinstance(failure, urllib.error.URLError) else failure).__name__)
PY

http_outcome() {
    docker exec --env NO_PROXY='*' --env no_proxy='*' "$1" python -c "$http_script" "$2" "$3"
}

read -r -d '' fork_script <<'PY' || true
import errno
import os
import signal
import time

children = []
failure = "none"
try:
    while len(children) < 1000:
        pid = os.fork()
        if pid == 0:
            time.sleep(60)
            os._exit(0)
        children.append(pid)
except OSError as error:
    failure = errno.errorcode[error.errno]
print(failure, len(children))
for pid in children:
    os.kill(pid, signal.SIGKILL)
    os.waitpid(pid, 0)
PY

via_proxy_status() {
    docker exec --env NO_PROXY= --env no_proxy= "$1" python -c "
import sys, urllib.request, urllib.error
proxy = 'http://$egress:3128'
opener = urllib.request.build_opener(urllib.request.ProxyHandler({'http': proxy, 'https': proxy}))
try:
    print(opener.open(sys.argv[1], timeout=20).status)
except urllib.error.HTTPError as error:
    print(error.code)
except Exception as error:
    print(type(error).__name__)
" "$2"
}

address_on() {
    inspect "$1" "{{(index .NetworkSettings.Networks \"$2\").IPAddress}}"
}

networks_of() {
    inspect "$1" '{{json .NetworkSettings.Networks}}' | jq -r 'keys | join(" ")'
}

gateway_of() {
    docker network inspect -f '{{(index .IPAM.Config 0).Gateway}}' "$1"
}

first_address_of() {
    python3 -c "import ipaddress, sys; print(next(ipaddress.ip_network(sys.argv[1]).hosts()))" \
        "$(docker network inspect -f '{{(index .IPAM.Config 0).Subnet}}' "$1")"
}

write_project() {
    local dir="$1" label="$2"
    mkdir -p "$dir/mcp"
    cp "$root/extensions/builder/runtime/main.py" "$dir/main.py"
    cat > "$dir/fastmcp.json" <<JSON
{
  "source": {"path": "main.py", "entrypoint": "mcp"},
  "environment": {"type": "uv", "python": "3.13", "dependencies": ["fastmcp==$fastmcp_version"]},
  "deployment": {"transport": "http", "host": "0.0.0.0", "port": 8000, "path": "/mcp"}
}
JSON
    cat > "$dir/mcp/probe.py" <<PY
import asyncio

from fastmcp.server.dependencies import get_http_headers
from fastmcp.tools import tool


@tool
def version() -> str:
    return "$label"


@tool
def seen_headers() -> list[str]:
    return sorted(get_http_headers(include_all=True))


@tool
def eat(megabytes: int) -> int:
    block = bytearray(b"\x01") * (megabytes * 1024 * 1024)
    return len(block)


@tool
async def slow(seconds: int) -> str:
    await asyncio.sleep(seconds)
    return "done"
PY
    tar --owner=10001 --group=10001 --numeric-owner -C "$dir" -czf "$dir.tar.gz" fastmcp.json main.py mcp
}

echo "== images"
docker build -q -t "$runtime_image" "$root/extensions/builder/runtime" > /dev/null || { echo "FAIL  the runtime image builds"; exit 1; }
docker build -q -t "$egress_image" "$root/extensions/builder/egress" > /dev/null || { echo "FAIL  the egress image builds"; exit 1; }
docker build -q -t "$runner_image" -f "$root/extensions/builder/CtxUi.Builder.Runner/Dockerfile" "$root" > /dev/null || { echo "FAIL  the runner image builds"; exit 1; }
echo "ok    the runner, runtime and egress images build"

python3 -m http.server "$host_port" --bind 0.0.0.0 --directory "$work" > /dev/null 2>&1 &
listener_pid=$!
write_project "$work/v1" v1
write_project "$work/v2" v2

echo "== compose"
compose up -d > /dev/null
runner_ready() {
    for _ in $(seq 1 60); do
        if [ "$(api -m 5 -o /dev/null -w '%{http_code}' "$control/v1/health" 2> /dev/null || true)" = 200 ]; then
            return 0
        fi
        sleep 1
    done
    docker logs "$runner" 2>&1 | tail -n 20
    return 1
}
check "runner answers its health endpoint" runner_ready
check "runner reports the egress container" equals true "$(api "$control/v1/health" | jq -r '.egressReachable')"
check "control api refuses a call without the token" equals 401 "$(curl -s -o /dev/null -w '%{http_code}' "$control/v1/health")"
check "control paths are not served on the ingress port" equals 401 "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $token" "$ingress/v1/health")"
runner_logs="$(docker logs "$runner" 2>&1)"
check "runner start logged something" contains "Application started" "$runner_logs"
check "runner start has no data protection warnings" lacks "DataProtection" "$runner_logs"
check "runner start does not mention the key folder" lacks ".aspnet" "$runner_logs"
check "runner start has no warnings at all" lacks "warn:" "$runner_logs"
check "runner root is read-only" equals true "$(inspect "$runner" '{{.HostConfig.ReadonlyRootfs}}')"
check "runner cannot write to its root" contains "Read-only file system" "$(docker exec "$runner" touch /root/.aspnet 2>&1 || true)"
check "runner drops all capabilities" equals '["ALL"]' "$(inspect "$runner" '{{json .HostConfig.CapDrop}}')"
check "runner forbids new privileges" contains no-new-privileges "$(inspect "$runner" '{{json .HostConfig.SecurityOpt}}')"
check "runner carries the system label" equals system "$(inspect "$runner" '{{index .Config.Labels "ctx-ui.builder"}}')"
check "runner sits on the fixed address" equals "$runner_ip" "$(address_on "$runner" "$control_network")"
check "egress root is read-only" equals true "$(inspect "$egress" '{{.HostConfig.ReadonlyRootfs}}')"
check "egress drops all capabilities" equals '["ALL"]' "$(inspect "$egress" '{{json .HostConfig.CapDrop}}')"
check "egress forbids new privileges" contains no-new-privileges "$(inspect "$egress" '{{json .HostConfig.SecurityOpt}}')"
check "egress carries the system label" equals system "$(inspect "$egress" '{{index .Config.Labels "ctx-ui.builder"}}')"
check "the control network has the planned subnet" equals 10.212.0.0/29 "$(docker network inspect -f '{{(index .IPAM.Config 0).Subnet}}' "$control_network")"
check "ports are published on loopback only" equals "127.0.0.1:8090 127.0.0.1:8091" \
    "$(docker port "$runner" | sed -E 's/^[0-9]+\/tcp -> //' | sort | paste -sd ' ')"

echo "== servers"
check "server A accepts its spec" equals 204 "$(put_spec "$a_id" 0 512)"
check "server B accepts its spec with a low memory limit" equals 204 "$(put_spec "$b_id" 0 160)"
check "server A accepts the first release" equals 202 "$(publish "$a_id" 1 "$work/v1.tar.gz")"
check "server B accepts the first release" equals 202 "$(publish "$b_id" 1 "$work/v1.tar.gz")"
check "server A builds" wait_for_state "$a_id" 1 "$built" 600
check "server B builds" wait_for_state "$b_id" 1 "$built" 600
check "server A rolls out" roll_out "$a_id" 1
check "server B rolls out" roll_out "$b_id" 1
a_net="${a}-internet"
b_net="${b}-internet"
a_container="$a-r1"
b_container="$b-r1"

echo "== ingress"
check "server A answers through the ingress" ingress_becomes_ok "$a_id"
check "server B answers through the ingress" ingress_becomes_ok "$b_id"
check "a call without the secret gets 401" equals 401 "$(ingress_status "$a_id" "" version '{}')"
check "a call with a wrong secret gets 401" equals 401 "$(ingress_status "$a_id" wrong version '{}')"
check "a call with the secret gets 200" equals 200 "$(ingress_status "$a_id" "$secret" version '{}')"
check "an unknown server gets 401" equals 401 "$(ingress_status 11111111-2222-4333-8444-555555555555 "$secret" version '{}')"
headers="$(ingress_call "$a_id" "$secret" seen_headers '{}')"
check "the server sees the request headers" contains content-type "$headers"
check "the server does not see the secret header" lacks x-builder-route-secret "$headers"

echo "== long reply"
long_started="$(date +%s)"
ingress_call "$a_id" "$secret" slow '{"seconds":110}' 170 > "$work/slow.out" &
slow_pid=$!

echo "== isolation of server A"
a_gateway="$(first_address_of "$a_net")"
b_address="$(address_on "$b_container" "$b_net")"
check "the probe reaches the proxy from A" equals connected "$(outcome "$a_container" "$egress" 3128)"
check "the probe reaches A itself" equals connected "$(outcome "$a_container" 127.0.0.1 8000)"
check "a direct name lookup of the internet fails" equals gaierror "$(outcome "$a_container" pypi.org 443)"
check "a direct connection to the internet has no route" equals ENETUNREACH "$(outcome "$a_container" 1.1.1.1 443)"
check "server B has no route from A" equals ENETUNREACH "$(outcome "$a_container" "$b_address" 8000)"
check "the internal network is created with an isolated gateway" equals isolated "$(docker network inspect -f '{{index .Options "com.docker.network.bridge.gateway_mode_ipv4"}}' "$a_net")"
check "the internal network is internal" equals true "$(docker network inspect -f '{{.Internal}}' "$a_net")"
check "the host service through the gateway is not reachable" matches '^(EHOSTUNREACH|ENETUNREACH|refused|timeout)$' "$(outcome "$a_container" "$a_gateway" "$host_port")"
check "runner ingress port has no route" equals ENETUNREACH "$(outcome "$a_container" "$runner_ip" 8090)"
check "runner control port has no route" equals ENETUNREACH "$(outcome "$a_container" "$runner_ip" 8091)"
check "A is attached to its own network only" equals "$a_net" "$(networks_of "$a_container")"
egress_on_a="$(address_on "$egress" "$a_net")"
check "the proxy refuses server B" equals 403 "$(via_proxy_status "$a_container" "http://$b_address:8000/")"
check "the proxy refuses 10.0.0.1" equals 403 "$(via_proxy_status "$a_container" http://10.0.0.1/)"
check "the proxy refuses 172.16.0.1" equals 403 "$(via_proxy_status "$a_container" http://172.16.0.1/)"
check "the proxy refuses 192.168.0.1" equals 403 "$(via_proxy_status "$a_container" http://192.168.0.1/)"
check "the proxy refuses the metadata address" equals 403 "$(via_proxy_status "$a_container" http://169.254.169.254/)"
check "the proxy refuses localhost" equals 403 "$(via_proxy_status "$a_container" http://localhost:8000/)"
check "the proxy refuses itself" equals 403 "$(via_proxy_status "$a_container" "http://$egress_on_a:3128/")"
check "the proxy refuses the runner address" equals 403 "$(via_proxy_status "$a_container" "http://$runner_ip:8091/")"
check "the proxy refuses a public mail port" equals 403 "$(via_proxy_status "$a_container" http://pypi.org:25/)"
check "the proxy passes https://pypi.org" equals 200 "$(via_proxy_status "$a_container" https://pypi.org/simple/)"
check "the proxy passes http://example.com" equals 200 "$(via_proxy_status "$a_container" http://example.com/)"
check "A cannot write to /" contains "Read-only file system" "$(docker exec "$a_container" touch /x 2>&1 || true)"
check "A cannot write to /code" contains "Read-only file system" "$(docker exec "$a_container" touch /code/x 2>&1 || true)"
check "A can write to /data" docker exec "$a_container" touch /data/x
check "A runs as 10001" equals 10001 "$(docker exec "$a_container" id -u)"
check "A drops all capabilities" equals '["ALL"]' "$(inspect "$a_container" '{{json .HostConfig.CapDrop}}')"
check "A forbids new privileges" contains no-new-privileges "$(inspect "$a_container" '{{json .HostConfig.SecurityOpt}}')"
check "A has the memory limit and no swap" equals "536870912 536870912" "$(inspect "$a_container" '{{.HostConfig.Memory}} {{.HostConfig.MemorySwap}}')"
check "A has the cpu and process limits" equals "1000000000 256" "$(inspect "$a_container" '{{.HostConfig.NanoCpus}} {{.HostConfig.PidsLimit}}')"
check "A does not see the docker socket" contains "No such file" "$(docker exec "$a_container" ls /var/run/docker.sock 2>&1 || true)"

echo "== memory limit"
check "B has a memory limit and no swap" equals "167772160 167772160" "$(inspect "$b_container" '{{.HostConfig.Memory}} {{.HostConfig.MemorySwap}}')"
check "B restarts through unless-stopped" equals unless-stopped "$(inspect "$b_container" '{{.HostConfig.RestartPolicy.Name}}')"
docker events --filter "container=$b_container" --filter event=oom --format '{{.Action}}' > "$work/oom.events" 2> /dev/null &
events_pid=$!
sleep 1
ingress_call "$b_id" "$secret" eat '{"megabytes":1024}' 60 > /dev/null &
eat_pid=$!
oom_killed=false
for _ in $(seq 1 600); do
    snapshot="$(inspect "$b_container" '{{.State.OOMKilled}} {{.RestartCount}} {{.State.Running}}' 2> /dev/null || true)"
    if [[ "$snapshot" == true* ]]; then
        oom_killed=true
    fi
    read -r _ restarts running <<< "$snapshot"
    if [ "${restarts:-0}" -ge 1 ] && [ "$running" = true ]; then
        break
    fi
    sleep 0.05
done
wait "$eat_pid" || true
kill "$events_pid" 2> /dev/null || true
events_pid=""
check "the memory event of B is an oom kill" contains oom "$(cat "$work/oom.events")"
check "docker marked B as OOMKilled" equals true "$oom_killed"
check "B was restarted once" at_least 1 "$(inspect "$b_container" '{{.RestartCount}}')"
check "B is running again" equals true "$(inspect "$b_container" '{{.State.Running}}')"
check "B serves again after the restart" ingress_becomes_ok "$b_id"

echo "== process limit"
ready_to_fork() {
    for _ in $(seq 1 200); do
        if ! kill -0 "$slow_pid" 2> /dev/null; then
            return 0
        fi
        sleep 1
    done
    return 1
}
check "the long reply finishes before the fork test" ready_to_fork
wait "$slow_pid" || true
slow_pid=""
long_elapsed=$(($(date +%s) - long_started))
check "a reply idle for over 100 seconds stays open through the ingress" contains '"done"' "$(cat "$work/slow.out")"
check "the long reply ended with 200" equals 200 "$(tail -n 1 "$work/slow.out")"
check "the long reply really waited" at_least 108 "$long_elapsed"
fork_result="$(docker exec "$a_container" python -c "$fork_script")"
read -r fork_error fork_count <<< "$fork_result"
check "fork fails with EAGAIN" equals EAGAIN "$fork_error"
check "fork stops below the limit of 256" at_most 255 "$fork_count"
check "fork got most of the way to the limit" at_least 150 "$fork_count"
check "A still answers after the fork test" ingress_becomes_ok "$a_id"

echo "== publishing a new version"
check "server A accepts the second release" equals 202 "$(publish "$a_id" 2 "$work/v2.tar.gz")"
check "the second release builds" wait_for_state "$a_id" 2 "$built" 600
load_start "$a_id" "$work/load-release"
check "the second release rolls out" roll_out "$a_id" 2
sleep 3
load_stop
check "calls during the publication all returned 200" load_was_clean "$work/load-release"
check "the new version answers" contains v2 "$(ingress_call "$a_id" "$secret" version '{}')"
check "the new version is the active release" equals 2 "$(api "$control/v1/servers/$a_id" | jq -r '.activeRelease')"

echo "== runner restart"
docker restart "$runner" > /dev/null
check "runner is back after a restart" runner_ready
check "the active release of A survived the restart" equals 2 "$(api "$control/v1/servers/$a_id" | jq -r '.activeRelease')"
check "the active release of B survived the restart" equals 1 "$(api "$control/v1/servers/$b_id" | jq -r '.activeRelease')"
check "A still answers through the ingress" ingress_becomes_ok "$a_id"
check "B still answers through the ingress" ingress_becomes_ok "$b_id"
check "A still answers with the new version" contains v2 "$(ingress_call "$a_id" "$secret" version '{}')"
check "a call without the secret is still refused after the restart" equals 401 "$(ingress_status "$a_id" "" version '{}')"

echo "== full network"
check "A accepts the full network spec" equals 204 "$(put_spec "$a_id" 1 512)"
check "the full network spec changes nothing that runs" equals InternetOnly "$(inspect "$a-r2" '{{index .Config.Labels "ctx-ui.builder.network"}}')"
load_start "$a_id" "$work/load-network"
check "the full network candidate rolls out" roll_out "$a_id" 2
sleep 3
load_stop
check "calls during the network change all returned 200" load_was_clean "$work/load-network"
full_container="$(docker ps --filter "label=ctx-ui.builder=$a_id" --filter "label=ctx-ui.builder.network=FullNetwork" --format '{{.Names}}' | head -n 1)"
full_network="${a}-full"
check "A runs on the full network" equals "$full_network" "$(networks_of "$full_container")"
full_gateway="$(gateway_of "$full_network")"
b_address="$(address_on "$b_container" "$b_net")"
check "the host service is reachable on the full network" equals connected "$(outcome "$full_container" "$full_gateway" "$host_port")"
check "the http probe reaches the host service on the full network" equals http-200 "$(http_outcome "$full_container" "$full_gateway" "$host_port")"
check "server B serves nothing to the full network" matches '^[A-Za-z]+$' "$(http_outcome "$full_container" "$b_address" 8000)"
check "runner ingress port serves nothing to the full network" matches '^[A-Za-z]+$' "$(http_outcome "$full_container" "$runner_ip" 8090)"
check "runner control port serves nothing to the full network" matches '^[A-Za-z]+$' "$(http_outcome "$full_container" "$runner_ip" 8091)"
check "published runner ports serve nothing through the gateway" matches '^[A-Za-z]+$' "$(http_outcome "$full_container" "$full_gateway" 8091)"
check "the internet is reachable directly on the full network" connects_within 3 "$full_container" 1.1.1.1 443
check "the full network container is still read-only" contains "Read-only file system" "$(docker exec "$full_container" touch /x 2>&1 || true)"
check "the full network container still answers" ingress_becomes_ok "$a_id"

if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all checks passed"
