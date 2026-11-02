#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="0.0.0-install-test"
base_image="ctx-ui-systemd-test"
box_image="ctx-ui-builder-install-image"
box="ctx-ui-builder-install-box"
stack_project="ctxb-install-stack"
runner_image="ghcr.io/echways/ctx-ui-builder-runner:$version"
runtime_image="ghcr.io/echways/ctx-ui-builder-runtime:$version"
egress_image="ghcr.io/echways/ctx-ui-builder-egress:$version"
panel_image="ghcr.io/echways/ctx-ui:$version"
server_id="0b1d6f4e-6a3c-4f55-9d0e-1a2b3c4d5e71"
server_container="ctxb-${server_id//-/}-r1"
route_secret="install-test-route-secret"
fastmcp_version="4.1.4"
settings_prefix="CtxUi__Extensions__builder__"
runner="ctxb-runner"
egress="ctxb-egress"
label="ctx-ui.builder"
missing_version="0.0.0-install-missing"
clash_network="ctxb-install-clash"
decoy="ctxb-install-decoy"
stopped_decoy="ctxb-install-stopped-decoy"
control_subnet="10.212.0.0/29"
built=1
failed=2
candidate=3

case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) echo "unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac

for port in 8090 8091 18080 18443; do
    if (exec 3<> "/dev/tcp/127.0.0.1/$port") 2> /dev/null; then
        echo "port $port on 127.0.0.1 is already in use, nothing was changed"
        exit 1
    fi
done
for name in "$runner" "$egress" "$box"; do
    if docker inspect "$name" > /dev/null 2>&1; then
        echo "a container named $name already exists, nothing was changed"
        exit 1
    fi
done
if [ -n "$(docker ps -a -q --filter label=com.docker.compose.service=ctx-ui)" ]; then
    echo "a compose project with a ctx-ui service already exists on this host, nothing was changed"
    exit 1
fi
for name in ctxb-control ctx-ui-builder_outbound; do
    if docker network inspect "$name" > /dev/null 2>&1; then
        echo "a network named $name already exists, nothing was changed"
        exit 1
    fi
done
if [ -n "$(docker ps -a -q --filter "label=$label")$(docker volume ls -q --filter "label=$label")$(docker network ls -q --filter "label=$label")" ]; then
    echo "containers, volumes or networks labelled $label already exist on this host, they may belong to real builder servers, nothing was changed"
    exit 1
fi
if docker network create --subnet "$control_subnet" "$clash_network" > /dev/null 2>&1; then
    docker network rm "$clash_network" > /dev/null
else
    echo "the subnet $control_subnet is already in use on this host, nothing was changed"
    exit 1
fi

dist="$(mktemp -d)"
stack="$(mktemp -d)"
failures=0
listener_pid=""

stack_compose() {
    docker compose --project-directory "$stack" -p "$stack_project" "$@"
}

remove_builder_objects() {
    docker rm -f "$runner" "$egress" > /dev/null 2>&1 || true
    docker ps -a -q --filter "label=$label=$server_id" | xargs -r docker rm -f > /dev/null 2>&1 || true
    docker volume ls -q --filter "label=$label=$server_id" | xargs -r docker volume rm -f > /dev/null 2>&1 || true
    docker network ls -q --filter "label=$label=$server_id" | xargs -r docker network rm > /dev/null 2>&1 || true
    docker network rm ctxb-control ctx-ui-builder_outbound > /dev/null 2>&1 || true
}

cleanup() {
    stack_compose down -v > /dev/null 2>&1 || true
    docker ps -a -q --filter "label=com.docker.compose.project=$stack_project" | xargs -r docker rm -f > /dev/null 2>&1 || true
    docker network ls -q --filter "label=com.docker.compose.project=$stack_project" | xargs -r docker network rm > /dev/null 2>&1 || true
    docker volume ls -q --filter "label=com.docker.compose.project=$stack_project" | xargs -r docker volume rm -f > /dev/null 2>&1 || true
    docker rm -f "$box" "$decoy" "$stopped_decoy" > /dev/null 2>&1 || true
    docker network rm "$clash_network" > /dev/null 2>&1 || true
    if [ -n "$listener_pid" ]; then
        kill "$listener_pid" 2> /dev/null || true
    fi
    remove_builder_objects
    docker rmi -f "$runner_image" "$runtime_image" "$egress_image" "$panel_image" "$box_image" > /dev/null 2>&1 || true
    rm -rf "$dist" "$stack"
}
trap cleanup EXIT

in_box() {
    docker exec "$box" "$@"
}

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

becomes() {
    local expected="$1" actual="" seconds="$2"
    shift 2
    for _ in $(seq 1 "$seconds"); do
        actual="$("$@" 2> /dev/null || true)"
        if [ "$actual" = "$expected" ]; then
            return 0
        fi
        sleep 1
    done
    echo "      expected: $expected"
    echo "      actual:   $actual"
    return 1
}

api() {
    local token="$1"
    shift
    curl -sS -H "Authorization: Bearer $token" "$@"
}

release_state() {
    api "$2" "$1/v1/servers/$server_id/releases/1" | jq -r '.state'
}

wait_for_state() {
    local base="$1" token="$2" wanted="$3" seconds="$4" state=""
    for _ in $(seq 1 "$seconds"); do
        state="$(release_state "$base" "$token" 2> /dev/null || true)"
        if [ "$state" = "$wanted" ]; then
            return 0
        fi
        if [ "$state" = "$failed" ]; then
            break
        fi
        sleep 1
    done
    echo "      release 1 is in state '$state', wanted $wanted"
    api "$token" "$base/v1/servers/$server_id/releases/1" | jq -r '.error, .log' | tail -n 20
    return 1
}

status_of() {
    api "$2" -o /dev/null -w '%{http_code}' -m 120 "${@:3}"
}

build_server() {
    local base="$1" token="$2"
    [ "$(status_of "$base" "$token" -X PUT -H 'Content-Type: application/json' \
        -d "{\"network\":0,\"limits\":{\"memoryMb\":512,\"cpus\":1,\"pids\":256},\"routeSecret\":\"$route_secret\"}" \
        "$base/v1/servers/$server_id")" = 204 ] || return 1
    [ "$(status_of "$base" "$token" -X POST -H 'Content-Type: application/gzip' --data-binary "@$dist/project.tar.gz" \
        "$base/v1/servers/$server_id/releases/1")" = 202 ] || return 1
    wait_for_state "$base" "$token" "$built" 600 || return 1
    [ "$(status_of "$base" "$token" -X POST -H 'Content-Type: application/json' -d '{"environment":{}}' \
        "$base/v1/servers/$server_id/releases/1/start")" = 202 ] || return 1
    wait_for_state "$base" "$token" "$candidate" 120 || return 1
    [ "$(status_of "$base" "$token" -X POST "$base/v1/servers/$server_id/releases/1/activate")" = 200 ]
}

call_server() {
    curl -sS -m 30 -X POST -H "X-Builder-Route-Secret: $route_secret" \
        -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greeting","arguments":{}}}' \
        "$1/s/$server_id/mcp"
}

server_answers() {
    for _ in $(seq 1 60); do
        if [[ "$(call_server "$1" 2> /dev/null || true)" == *installed-ok* ]]; then
            return 0
        fi
        sleep 1
    done
    echo "      no answer from the built server through $1"
    return 1
}

write_project() {
    local dir="$1"
    mkdir -p "$dir/mcp"
    cp "$root/extensions/builder/runtime/main.py" "$dir/main.py"
    cat > "$dir/fastmcp.json" <<JSON
{
  "source": {"path": "main.py", "entrypoint": "mcp"},
  "environment": {"type": "uv", "python": "3.13", "dependencies": ["fastmcp==$fastmcp_version"]},
  "deployment": {"transport": "http", "host": "0.0.0.0", "port": 8000, "path": "/mcp"}
}
JSON
    cat > "$dir/mcp/greeting.py" <<'PY'
from fastmcp.tools import tool


@tool
def greeting() -> str:
    return "installed-ok"
PY
    tar --owner=10001 --group=10001 --numeric-owner -C "$dir" -czf "$dir.tar.gz" fastmcp.json main.py mcp
}

builder_containers() {
    {
        docker ps -a --filter "name=^$runner\$" --filter "name=^$egress\$" --format '{{.Names}}'
        docker ps -a --filter "label=$label=$server_id" --format '{{.Names}}'
    } | sort | paste -sd ' '
}

running_containers() {
    docker ps --filter "name=^$runner\$" --filter "name=^$egress\$" --format '{{.Names}}' | sort | paste -sd ' '
}

extension_line() {
    in_box sh -c 'cd / && ctx-ui extension list' 2>&1 | grep -E '^builder[[:space:]]' || true
}

box_snapshot() {
    in_box sh -c 'cd / && sha256sum /etc/ctx-ui/ctx-ui.env; ls /opt 2>/dev/null; ctx-ui extension list 2>&1' 2> /dev/null | sha256sum
    builder_containers
}

stack_snapshot() {
    sha256sum "$stack/.env"
    ls "$stack"
    builder_containers
}

installer() {
    local release="$1"
    shift
    in_box env CTX_UI_DOWNLOAD_URL="file:///dist/$release" "$@"
}

echo "== build"
"$root/deploy/builder/package.sh" "$version" "$dist/release" > /dev/null
mkdir "$dist/tampered"
cp "$dist/release/"* "$dist/tampered/"
echo tampered >> "$dist/tampered/builder-$version.zip"
mkdir "$dist/missing"
cp "$dist/release/"* "$dist/missing/"
mv "$dist/missing/builder-$version.zip" "$dist/missing/builder-$missing_version.zip"
sed -i "s/builder-$version.zip/builder-$missing_version.zip/" "$dist/missing/SHA256SUMS"
mkdir "$dist/incompatible" "$dist/unpacked"
cp "$dist/release/"* "$dist/incompatible/"
unzip -q "$dist/release/builder-$version.zip" -d "$dist/unpacked"
jq '.api = "9.0"' "$dist/unpacked/extension.json" > "$dist/extension.json"
mv "$dist/extension.json" "$dist/unpacked/extension.json"
rm "$dist/incompatible/builder-$version.zip"
(cd "$dist/unpacked" && zip -q -r "$dist/incompatible/builder-$version.zip" .)
(cd "$dist/incompatible" && sha256sum "builder-$version.zip" builder-compose.yaml builder-compose.docker.yaml builder-install.sh > SHA256SUMS)
mkdir "$dist/unstamped"
cp "$root/deploy/builder/builder-install.sh" "$dist/unstamped/"
cp "$root/deploy/linux/install.sh" "$dist/install.sh"
write_project "$dist/project"

dotnet publish "$root/src/CtxUi.Server" -c Release -r "linux-$arch" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=0.0.1 -o "$dist/ctx-ui/ctx-ui" > /dev/null
cp "$root/LICENSE" "$root/deploy/linux/ctx-ui.sh" "$root/deploy/linux/ctx-ui.service" "$dist/ctx-ui/ctx-ui/"
echo 0.0.1 > "$dist/ctx-ui/ctx-ui/VERSION"
chmod 755 "$dist/ctx-ui/ctx-ui/ctx-ui" "$dist/ctx-ui/ctx-ui/ctx-ui.sh"
tar -C "$dist/ctx-ui" --owner=0 --group=0 --numeric-owner -czf "$dist/ctx-ui-0.0.1.tar.gz" ctx-ui
cat > "$dist/docker" <<'SH'
#!/bin/sh
if [ "$1" = version ]; then
    echo 28.5.0
    exit 0
fi
exec /usr/local/bin/docker "$@"
SH
chmod -R a+rX "$dist"
chmod 755 "$dist/docker"

docker build -q -t "$runtime_image" "$root/extensions/builder/runtime" > /dev/null || { echo "FAIL  the runtime image builds"; exit 1; }
docker build -q -t "$egress_image" "$root/extensions/builder/egress" > /dev/null || { echo "FAIL  the egress image builds"; exit 1; }
docker build -q -t "$runner_image" --build-arg "VERSION=$version" -f "$root/extensions/builder/CtxUi.Builder.Runner/Dockerfile" "$root" > /dev/null || { echo "FAIL  the runner image builds"; exit 1; }
docker build -q -t "$panel_image" -f "$root/deploy/docker/Dockerfile" --build-arg "VERSION=$version" "$root" > /dev/null || { echo "FAIL  the ctx-ui image builds"; exit 1; }
docker build -q -t "$base_image" -f "$root/deploy/tests/systemd.Dockerfile" "$root/deploy/tests" > /dev/null
docker build -q -t "$box_image" - > /dev/null <<EOF
FROM $base_image
COPY --from=docker:cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
EOF
echo "ok    the release assets, the images and the test machine are built"

docker run -d --name "$box" --privileged --cgroupns private --network host \
    --tmpfs /run --tmpfs /run/lock --tmpfs /tmp \
    -v /var/run/docker.sock:/var/run/docker.sock -v "$dist:/dist:ro" -v "$stack:$stack" "$box_image" > /dev/null
for _ in $(seq 1 30); do
    state="$(in_box systemctl is-system-running 2> /dev/null || true)"
    if [ "$state" = running ] || [ "$state" = degraded ]; then
        break
    fi
    sleep 1
done

echo "== release assets"
sums="$(cat "$dist/release/SHA256SUMS")"
check "the archive is checksummed" contains "builder-$version.zip" "$sums"
check "the installer is checksummed" contains "builder-install.sh" "$sums"
check "the compose file is checksummed" contains "builder-compose.yaml" "$sums"
check "the docker overlay is checksummed" contains "builder-compose.docker.yaml" "$sums"
check "the checksums hold" bash -c "cd '$dist/release' && sha256sum -c --status SHA256SUMS"
check "the manifest carries the release version" contains "\"version\": \"$version\"" "$(unzip -p "$dist/release/builder-$version.zip" extension.json)"
check "the installer is stamped with the version" contains "default_version=\"$version\"" "$(cat "$dist/release/builder-install.sh")"

echo "== no ctx-ui"
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "installing without ctx-ui is refused" equals 1 "$code"
check "the refusal says ctx-ui is missing" contains "ctx-ui is not installed" "$output"
output="$(installer release bash /dist/release/builder-install.sh --unknown 2>&1)" && code=0 || code=$?
check "an unknown option is refused" equals 1 "$code"
output="$(in_box bash /dist/release/builder-install.sh --help 2>&1)" && code=0 || code=$?
check "help succeeds" equals 0 "$code"
check "help names the flags" contains "--remove" "$output"

echo "== systemd: refusals"
in_box bash /dist/install.sh --archive /dist/ctx-ui-0.0.1.tar.gz --public-url http://127.0.0.1:18080 --listen http://127.0.0.1:18080 > /dev/null 2>&1 || true
check "ctx-ui is running" equals active "$(in_box systemctl is-active ctx-ui)"
before="$(box_snapshot)"
output="$(installer release env PATH=/dist:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "Docker below 29 is refused" equals 1 "$code"
check "the refusal names the version" contains "28.5.0" "$output"
check "the refused Docker changed nothing" equals "$before" "$(box_snapshot)"
output="$(installer tampered bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "a tampered archive is refused" equals 1 "$code"
check "the refusal mentions the checksum" contains "checksum" "$output"
check "the tampered archive changed nothing" equals "$before" "$(box_snapshot)"
output="$(installer release bash /dist/unstamped/builder-install.sh 2>&1)" && code=0 || code=$?
check "an installer without a version is refused" equals 1 "$code"
check "the refusal asks for a version" contains "no version given" "$output"
output="$(installer missing bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "an unreachable release is refused" equals 1 "$code"
check "the unreachable release changed nothing" equals "$before" "$(box_snapshot)"
output="$(installer release bash /dist/release/builder-install.sh --version 'x; touch /tmp/not-allowed' 2>&1)" && code=0 || code=$?
check "a malformed version is refused" equals 1 "$code"
check "the refusal explains the version" contains "is not a version" "$output"
check "nothing ran from the version" equals "" "$(in_box sh -c 'ls /tmp/not-allowed 2>/dev/null' || true)"
check "the malformed version changed nothing" equals "$before" "$(box_snapshot)"
output="$(installer missing bash /dist/release/builder-install.sh --version "$missing_version" 2>&1)" && code=0 || code=$?
check "images that cannot be pulled are refused" equals 1 "$code"
check "the refusal names the pull" contains "could not be pulled" "$output"
check "the failed pull changed nothing" equals "$before" "$(box_snapshot)"
output="$(installer incompatible bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "an extension newer than ctx-ui is refused" equals 1 "$code"
check "the refusal comes from ctx-ui" contains "did not accept the extension" "$output"
check "the incompatible extension changed nothing" equals "$before" "$(box_snapshot)"
python3 -m http.server 8091 --bind 127.0.0.1 --directory "$dist" > /dev/null 2>&1 &
listener_pid=$!
becomes up 10 bash -c "(exec 3<> /dev/tcp/127.0.0.1/8091) 2> /dev/null && echo up" > /dev/null
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "a busy port is refused" equals 1 "$code"
check "the refusal names the port" contains "8091" "$output"
check "the busy port changed nothing" equals "$before" "$(box_snapshot)"
kill "$listener_pid" 2> /dev/null || true
listener_pid=""
docker network create --subnet 10.212.0.0/28 "$clash_network" > /dev/null
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "a clashing subnet is refused" equals 1 "$code"
check "the refusal names the network" contains "$clash_network" "$output"
check "the clashing subnet changed nothing" equals "$before" "$(box_snapshot)"
docker network rm "$clash_network" > /dev/null
check "the installer sends the token header through stdin" contains "-H @-" "$(cat "$dist/release/builder-install.sh")"

echo "== systemd: install"
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "the installer succeeds" equals 0 "$code"
check "the installer reports the version" contains "Server builder $version is installed." "$output"
check "ctx-ui is active" equals active "$(in_box systemctl is-active ctx-ui)"
check "the extension is working" equals "builder  $version  ok" "$(extension_line)"
token="$(in_box cat /opt/ctx-ui-builder/.env | sed -n 's/^CTXB_RUNNER_TOKEN=//p')"
check "the token is 64 hex digits" matches '^[0-9a-f]{64}$' "$token"
env_content="$(in_box cat /etc/ctx-ui/ctx-ui.env)"
check "the Runner address is written" contains "${settings_prefix}RunnerUrl=http://127.0.0.1:8091" "$env_content"
check "the ingress address is written" contains "${settings_prefix}RunnerIngressUrl=http://127.0.0.1:8090" "$env_content"
check "the token is written" contains "${settings_prefix}RunnerToken=$token" "$env_content"
check "the settings file keeps its mode" equals "640 root:ctx-ui" "$(in_box stat -c '%a %U:%G' /etc/ctx-ui/ctx-ui.env)"
check "the builder token file is private" equals "600 root:root" "$(in_box stat -c '%a %U:%G' /opt/ctx-ui-builder/.env)"
check "the Runner and the proxy run" equals "$egress $runner" "$(running_containers)"
check "the Runner reports the release version" equals "$version" "$(api "$token" http://127.0.0.1:8091/v1/health | jq -r '.version')"
check "the Runner refuses a wrong token" equals 401 "$(curl -s -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer wrong' http://127.0.0.1:8091/v1/health)"
check "the ports are on loopback only" equals "127.0.0.1:8090 127.0.0.1:8091" "$(docker port "$runner" | sed -E 's/^[0-9]+\/tcp -> //' | sort | paste -sd ' ')"
check "the extension has no errors in the journal" lacks "fail:" "$(in_box journalctl -u ctx-ui --no-pager 2>&1)"
panel_path="$(in_box sh -c 'cd / && ctx-ui admin add root' 2>&1 | grep -o -E 'http://127\.0\.0\.1:18080/[a-z0-9]{16}/' | head -n 1 || true)"
key="$(in_box sh -c 'cd / && ctx-ui apikey add probe' 2>&1 | grep -o -E 'ctxk_[A-Za-z0-9]{40}' | head -n 1 || true)"
check "the extension endpoint answers" equals 200 "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $key" "${panel_path}extensions/builder/servers")"
check "a server builds and activates" build_server http://127.0.0.1:8091 "$token"
check "the built server answers through the ingress" server_answers http://127.0.0.1:8090

echo "== systemd: second run"
runner_id="$(docker inspect -f '{{.Id}}' "$runner")"
server_id_before="$(docker inspect -f '{{.Id}}' "$server_container")"
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "the second run succeeds" equals 0 "$code"
check "the token is kept" equals "$token" "$(in_box cat /opt/ctx-ui-builder/.env | sed -n 's/^CTXB_RUNNER_TOKEN=//p')"
env_content="$(in_box cat /etc/ctx-ui/ctx-ui.env)"
check "the token setting is not duplicated" equals 1 "$(grep -c "^${settings_prefix}RunnerToken=" <<< "$env_content")"
check "the address setting is not duplicated" equals 1 "$(grep -c "^${settings_prefix}RunnerUrl=" <<< "$env_content")"
check "the ctx-ui settings are kept" contains "CtxUi__PublicUrl=http://127.0.0.1:18080" "$env_content"
check "the Runner container is the same" equals "$runner_id" "$(docker inspect -f '{{.Id}}' "$runner")"
check "the server container is the same" equals "$server_id_before" "$(docker inspect -f '{{.Id}}' "$server_container")"
check "the server still answers" server_answers http://127.0.0.1:8090
check "the extension is still working" equals "builder  $version  ok" "$(extension_line)"
check "ctx-ui is active after the second run" equals active "$(in_box systemctl is-active ctx-ui)"

echo "== systemd: remove"
output="$(installer release env PATH=/dist:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin bash /dist/release/builder-install.sh --remove 2>&1)" && code=0 || code=$?
check "remove works on Docker below 29" equals 0 "$code"
check "remove says ctx-ui keeps running" contains "ctx-ui keeps running" "$output"
check "no container with the label is left" equals "" "$(builder_containers)"
check "the extension is gone" equals "" "$(extension_line)"
env_content="$(in_box cat /etc/ctx-ui/ctx-ui.env)"
check "the builder settings are gone" lacks "$settings_prefix" "$env_content"
check "the ctx-ui settings are kept" contains "CtxUi__PublicUrl=http://127.0.0.1:18080" "$env_content"
check "the builder directory is gone" equals "" "$(in_box sh -c 'ls -d /opt/ctx-ui-builder 2>/dev/null' || true)"
check "ctx-ui is active" equals active "$(in_box systemctl is-active ctx-ui)"
check "ctx-ui is healthy" in_box /opt/ctx-ui/ctx-ui health
check "the extension endpoint is gone" equals 302 "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $key" "${panel_path}extensions/builder/servers")"
check "a second remove succeeds" in_box bash /dist/release/builder-install.sh --remove
in_box bash /dist/install.sh --uninstall --purge > /dev/null 2>&1 || true
remove_builder_objects

echo "== docker: stack"
cp "$root/deploy/docker/compose.yaml" "$root/deploy/docker/Caddyfile" "$stack/"
sed -e "s/^CTX_UI_VERSION=.*/CTX_UI_VERSION=$version/" -e "s/^CTX_UI_DOMAIN=.*/CTX_UI_DOMAIN=localhost/" \
    "$root/deploy/docker/.env.example" > "$stack/.env"
cat > "$stack/test.yaml" <<'YAML'
services:
  caddy:
    ports: !override
      - "18080:80"
      - "18443:443"
YAML
chmod -R a+rX "$stack"
stack_compose -f "$stack/compose.yaml" -f "$stack/test.yaml" up -d > /dev/null 2>&1
panel_container="$(stack_compose -f "$stack/compose.yaml" -f "$stack/test.yaml" ps -q ctx-ui)"
panel_healthy() {
    docker inspect -f '{{.State.Health.Status}}' "$panel_container"
}
check "ctx-ui runs in Docker" becomes healthy 90 panel_healthy
docker run -d --name "$decoy" --entrypoint sleep --label com.docker.compose.service=ctx-ui --label com.docker.compose.project=ctxb-install-decoy "$panel_image" 600 > /dev/null
docker create --name "$stopped_decoy" --entrypoint sleep --label com.docker.compose.service=ctx-ui --label com.docker.compose.project=ctxb-install-stopped "$panel_image" 600 > /dev/null

echo "== docker: refusals"
before="$(stack_snapshot)"
output="$(installer release env PATH=/dist:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "Docker below 29 is refused" equals 1 "$code"
check "the refused Docker changed nothing" equals "$before" "$(stack_snapshot)"
output="$(installer tampered bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "a tampered archive is refused" equals 1 "$code"
check "the tampered archive changed nothing" equals "$before" "$(stack_snapshot)"
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "two running ctx-ui containers are refused" equals 1 "$code"
check "the refusal lists the candidates" contains "more than one running ctx-ui container" "$output"
check "the two containers changed nothing" equals "$before" "$(stack_snapshot)"
docker rm -f "$decoy" > /dev/null
output="$(installer missing bash /dist/release/builder-install.sh --version "$missing_version" 2>&1)" && code=0 || code=$?
check "images that cannot be pulled are refused" equals 1 "$code"
check "the failed pull changed nothing" equals "$before" "$(stack_snapshot)"
output="$(installer incompatible bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "an extension newer than ctx-ui is refused" equals 1 "$code"
check "the incompatible extension changed nothing" equals "$before" "$(stack_snapshot)"
check "the incompatible extension was not installed" lacks builder "$(docker exec "$(stack_compose ps -q ctx-ui)" ctx-ui extension list 2>&1)"
output="$(installer release bash /dist/release/builder-install.sh --version 'x; touch /tmp/not-allowed' 2>&1)" && code=0 || code=$?
check "a malformed version is refused" equals 1 "$code"
check "the malformed version changed nothing" equals "$before" "$(stack_snapshot)"

echo "== docker: install"
chmod 644 "$stack/.env"
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "the installer succeeds" equals 0 "$code"
check "the installer reports the version" contains "Server builder $version is installed." "$output"
panel_container="$(stack_compose ps -q ctx-ui)"
check "ctx-ui is healthy" becomes healthy 90 panel_healthy
token="$(sed -n 's/^CTXB_RUNNER_TOKEN=//p' "$stack/.env")"
check "the token is 64 hex digits" matches '^[0-9a-f]{64}$' "$token"
check "the version is written" equals "$version" "$(sed -n 's/^CTXB_VERSION=//p' "$stack/.env")"
check "the ctx-ui version is untouched" equals "$version" "$(sed -n 's/^CTX_UI_VERSION=//p' "$stack/.env")"
check "the token file is private and keeps its owner" equals "600 $(id -un)" "$(stat -c '%a %U' "$stack/.env")"
check "the project name is pinned in .env" equals "$stack_project" "$(sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$stack/.env")"
expected_files="$stack/compose.yaml:$stack/test.yaml:$stack/builder-compose.yaml:$stack/builder-compose.docker.yaml"
check "the compose files are listed in .env" equals "$expected_files" "$(sed -n 's/^COMPOSE_FILE=//p' "$stack/.env")"
check "the compose files are next to the stack" equals "builder-compose.docker.yaml builder-compose.yaml" "$(find "$stack" -name 'builder-*' -printf '%f\n' | sort | paste -sd ' ')"
check "the extension is working" contains "builder  $version  ok" "$(docker exec "$panel_container" ctx-ui extension list 2>&1)"
panel_env="$(docker exec "$panel_container" env)"
check "ctx-ui gets the Runner address" contains "${settings_prefix}RunnerUrl=http://10.212.0.2:8091" "$panel_env"
check "ctx-ui gets the ingress address" contains "${settings_prefix}RunnerIngressUrl=http://10.212.0.2:8090" "$panel_env"
check "ctx-ui gets the token" contains "${settings_prefix}RunnerToken=$token" "$panel_env"
check "ctx-ui is on the control network" contains ctxb-control "$(docker inspect -f '{{json .NetworkSettings.Networks}}' "$panel_container")"
check "the Runner and the proxy run" equals "$egress $runner" "$(running_containers)"
check "the Runner publishes no host ports" equals "" "$(docker port "$runner")"
check "the Runner sits on the planned address" equals 10.212.0.2 "$(docker inspect -f '{{(index .NetworkSettings.Networks "ctxb-control").IPAddress}}' "$runner")"
check "the Runner reports the release version" equals "$version" "$(api "$token" http://10.212.0.2:8091/v1/health | jq -r '.version')"
check "the extension has no errors in the log" lacks "fail:" "$(docker logs "$panel_container" 2>&1)"
panel_path="$(docker exec "$panel_container" ctx-ui admin add root 2>&1 | grep -o -E 'https://localhost/[a-z0-9]{16}/' | head -n 1 || true)"
key="$(docker exec "$panel_container" ctx-ui apikey add probe 2>&1 | grep -o -E 'ctxk_[A-Za-z0-9]{40}' | head -n 1 || true)"
panel_endpoint="https://localhost:18443${panel_path#https://localhost}extensions/builder/servers"
check "the extension endpoint answers" equals 200 "$(curl -sk -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $key" "$panel_endpoint")"
check "a server builds and activates" build_server http://10.212.0.2:8091 "$token"
check "the built server answers through the ingress" server_answers http://10.212.0.2:8090
runner_id="$(docker inspect -f '{{.Id}}' "$runner")"
check "a plain compose up keeps the builder" bash -c "cd '$stack' && docker compose up -d > /dev/null 2>&1"
check "the plain compose up keeps ctx-ui" equals "$panel_container" "$(stack_compose ps -q ctx-ui)"
check "the plain compose up keeps the Runner" equals "$runner_id" "$(docker inspect -f '{{.Id}}' "$runner")"

echo "== docker: second run"
server_id_before="$(docker inspect -f '{{.Id}}' "$server_container")"
output="$(installer release bash /dist/release/builder-install.sh 2>&1)" && code=0 || code=$?
check "the second run succeeds" equals 0 "$code"
check "the token is kept" equals "$token" "$(sed -n 's/^CTXB_RUNNER_TOKEN=//p' "$stack/.env")"
check "the compose list is not duplicated" equals "$expected_files" "$(sed -n 's/^COMPOSE_FILE=//p' "$stack/.env")"
check "the project name is not duplicated" equals 1 "$(grep -c '^COMPOSE_PROJECT_NAME=' "$stack/.env")"
check "the token file stays private" equals "600 $(id -un)" "$(stat -c '%a %U' "$stack/.env")"
check "the Runner container is the same" equals "$runner_id" "$(docker inspect -f '{{.Id}}' "$runner")"
check "the server container is the same" equals "$server_id_before" "$(docker inspect -f '{{.Id}}' "$server_container")"
panel_container="$(stack_compose ps -q ctx-ui)"
check "ctx-ui is healthy after the second run" becomes healthy 90 panel_healthy
check "the server still answers" server_answers http://10.212.0.2:8090
check "the extension is still working" contains "builder  $version  ok" "$(docker exec "$panel_container" ctx-ui extension list 2>&1)"

echo "== docker: remove"
output="$(in_box bash /dist/release/builder-install.sh --remove 2>&1)" && code=0 || code=$?
check "remove succeeds" equals 0 "$code"
check "remove says ctx-ui keeps running" contains "ctx-ui keeps running" "$output"
check "no container with the label is left" equals "" "$(builder_containers)"
panel_container="$(stack_compose ps -q ctx-ui)"
check "ctx-ui is healthy" becomes healthy 90 panel_healthy
check "the extension is gone" lacks builder "$(docker exec "$panel_container" ctx-ui extension list 2>&1)"
check "ctx-ui lost the builder settings" lacks "$settings_prefix" "$(docker exec "$panel_container" env)"
check "ctx-ui left the control network" lacks ctxb-control "$(docker inspect -f '{{json .NetworkSettings.Networks}}' "$panel_container")"
check "the builder files are gone" equals "" "$(find "$stack" -name 'builder-*' -printf '%f\n')"
check "the token and the version are gone from .env" equals "" "$(grep -E '^(CTXB_|CtxUi__)' "$stack/.env" || true)"
check "the compose list is back to the stack" equals "$stack/compose.yaml:$stack/test.yaml" "$(sed -n 's/^COMPOSE_FILE=//p' "$stack/.env")"
check "the ctx-ui settings are kept" equals "localhost" "$(sed -n 's/^CTX_UI_DOMAIN=//p' "$stack/.env")"
check "the project name stays pinned" equals "$stack_project" "$(sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$stack/.env")"
check "the extension endpoint is gone" equals 302 "$(curl -sk -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $key" "$panel_endpoint")"
check "a second remove succeeds" in_box bash /dist/release/builder-install.sh --remove

if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all checks passed"
