#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
image="ctx-ui-systemd-test"
box="ctx-ui-install-test"
dist="$(mktemp -d)"
failures=0

case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) echo "unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac

cleanup() {
    docker rm -f "$box" > /dev/null 2>&1 || true
    rm -rf "$dist"
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

status() {
    in_box curl -s -o /dev/null -w '%{http_code}' "$@" http://127.0.0.1:8080/mcp/smoke
}

panel_status() {
    in_box curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8080$1"
}

status_becomes() {
    local expected="$1" actual=""
    shift
    for _ in $(seq 1 20); do
        actual="$(status "$@")"
        if [ "$actual" = "$expected" ]; then
            return 0
        fi
        sleep 0.5
    done
    echo "      expected: $expected"
    echo "      actual:   $actual"
    return 1
}

"$root/deploy/package.sh" 0.0.1 "$dist" > /dev/null
first="ctx-ui-0.0.1-linux-$arch.tar.gz"
second="ctx-ui-0.0.2-linux-$arch.tar.gz"
mkdir "$dist/next"
tar -xzf "$dist/$first" -C "$dist/next"
echo 0.0.2 > "$dist/next/ctx-ui/VERSION"
tar -C "$dist/next" -czf "$dist/$second" ctx-ui
mkdir "$dist/v0.0.2"
cp "$dist/$second" "$dist/v0.0.2/"
sed 's/^default_version=.*/default_version="0.0.2"/' "$root/deploy/linux/install.sh" > "$dist/v0.0.2/install.sh"
(cd "$dist/v0.0.2" && sha256sum "$second" install.sh > SHA256SUMS)
cp "$root/deploy/linux/install.sh" "$dist/install-unstamped.sh"
mkdir "$dist/tampered"
cp "$dist/SHA256SUMS" "$dist/$first" "$dist/tampered/"
echo tampered >> "$dist/tampered/$first"
chmod -R a+rX "$dist"

docker build -q -t "$image" -f "$root/deploy/tests/systemd.Dockerfile" "$root/deploy/tests" > /dev/null
docker rm -f "$box" > /dev/null 2>&1 || true
docker run -d --name "$box" --privileged --cgroupns private \
    --tmpfs /run --tmpfs /run/lock --tmpfs /tmp -v "$dist:/dist:ro" "$image" > /dev/null
for _ in $(seq 1 30); do
    state="$(in_box systemctl is-system-running 2> /dev/null || true)"
    if [ "$state" = running ] || [ "$state" = degraded ]; then
        break
    fi
    sleep 1
done

echo "== refusals"
output="$(in_box bash /dist/install-unstamped.sh 2>&1)" && code=0 || code=$?
check "unstamped installer needs a version" equals 1 "$code"
check "unstamped installer explains" contains "no version given" "$output"
output="$(in_box bash /dist/install.sh --purge 2>&1)" && code=0 || code=$?
check "purge alone is refused" equals 1 "$code"
output="$(in_box bash /dist/install.sh --archive /dist/SHA256SUMS 2>&1)" && code=0 || code=$?
check "foreign archive is refused" equals 1 "$code"
output="$(in_box env CTX_UI_DOWNLOAD_URL=file:///dist/tampered bash /dist/install.sh 2>&1)" && code=0 || code=$?
check "tampered download is refused" equals 1 "$code"
check "tampered download explains" contains "checksum" "$output"
output="$(in_box bash /dist/install.sh --archive "/dist/$first" --listen "http://127.0.0.1:8080; reboot" 2>&1)" && code=0 || code=$?
check "address with shell syntax is refused" equals 1 "$code"
check "refused address leaves no settings" equals "" "$(in_box sh -c 'ls /etc/ctx-ui/ctx-ui.env 2>/dev/null' || true)"
check "nothing is installed after refusals" equals "" "$(in_box sh -c 'ls -d /opt/ctx-ui 2>/dev/null' || true)"

echo "== install"
output="$(in_box env CTX_UI_DOWNLOAD_URL=file:///dist bash /dist/install.sh \
    --public-url https://mcp.example.com/ --listen http://127.0.0.1:8080 2>&1)" || true
check "installer reports the version" contains "ctx-ui 0.0.1 is installed and running." "$output"
check "service is active" equals active "$(in_box systemctl is-active ctx-ui)"
check "service is enabled" equals enabled "$(in_box systemctl is-enabled ctx-ui)"
check "service runs as ctx-ui" equals ctx-ui "$(in_box ps -o user= -C ctx-ui | sort -u)"
check "program directory" equals "755 root:root" "$(in_box stat -c '%a %U:%G' /opt/ctx-ui)"
check "settings file" equals "640 root:ctx-ui" "$(in_box stat -c '%a %U:%G' /etc/ctx-ui/ctx-ui.env)"
check "data directory" equals "700 ctx-ui:ctx-ui" "$(in_box stat -c '%a %U:%G' /var/lib/ctx-ui)"
check "settings content" equals \
    "Urls=http://127.0.0.1:8080 CtxUi__PublicUrl=https://mcp.example.com CtxUi__DataDir=/var/lib/ctx-ui" \
    "$(in_box cat /etc/ctx-ui/ctx-ui.env | tr '\n' ' ' | sed 's/ $//')"

echo "== command line"
output="$(in_box sh -c 'cd / && ctx-ui inbound add smoke --url http://127.0.0.1:9/mcp' 2>&1)" || true
check "inbound address starts with the public url" contains "https://mcp.example.com/mcp/smoke" "$output"
output="$(in_box sh -c 'cd / && ctx-ui client add smoke --inbound smoke' 2>&1)" || true
token="$(echo "$output" | grep -o -E 'tnk_[A-Za-z0-9]{32}' | head -n 1 || true)"
check "client add prints a token" [ -n "$token" ]
check "data files belong to ctx-ui" equals "" "$(in_box find /var/lib/ctx-ui ! -user ctx-ui)"
check "request without a token is rejected" status_becomes 401
check "request with the token reaches forwarding" status_becomes 502 -H "Authorization: Bearer $token"
output="$(in_box runuser -u nobody -- ctx-ui inbound list 2>&1)" && code=0 || code=$?
check "other users are refused" equals 1 "$code"
check "refusal mentions sudo" contains "sudo" "$output"

echo "== panel"
output="$(in_box sh -c 'cd / && ctx-ui admin add root' 2>&1)" || true
panel="$(echo "$output" | grep -o -E 'https://mcp\.example\.com/[a-z0-9]{16}/' | head -n 1 || true)"
check "admin add prints the panel address" [ -n "$panel" ]
check "admin add prints a password" contains "Password:  " "$output"
path="${panel#https://mcp.example.com}"
check "login form is served under the secret path" equals 200 "$(panel_status "${path}login")"
check "panel styles are in the archive" equals 200 "$(panel_status "${path}_content/MudBlazor/MudBlazor.min.css")"
check "panel script is in the archive" equals 200 "$(panel_status "${path}_framework/blazor.web.js")"
check "swagger ui is in the archive" equals 200 "$(panel_status "${path}swagger/index.html")"
check "api asks for a key" equals 401 "$(panel_status "${path}api/v1/inbounds")"
check "panel does not exist without the secret path" equals 404 "$(panel_status /login)"

echo "== update"
check "version command" equals 0.0.1 "$(in_box ctx-ui version)"
pid="$(in_box systemctl show -p MainPID --value ctx-ui)"
output="$(in_box sh -c 'cd / && CTX_UI_DOWNLOAD_URL=file:///dist ctx-ui update' 2>&1)" || true
check "update to the installed version says so" contains "ctx-ui 0.0.1 is already installed" "$output"
check "update to the installed version keeps the service" equals "$pid" "$(in_box systemctl show -p MainPID --value ctx-ui)"
output="$(in_box runuser -u ctx-ui -- ctx-ui update 2>&1)" && code=0 || code=$?
check "update without root is refused" equals 1 "$code"
output="$(in_box sh -c 'cd / && CTX_UI_DOWNLOAD_URL=file:///dist/missing ctx-ui update' 2>&1)" && code=0 || code=$?
check "update without a reachable release fails" equals "1 active" "$code $(in_box systemctl is-active ctx-ui)"
output="$(in_box sh -c 'cd / && CTX_UI_DOWNLOAD_URL=file:///dist/v0.0.2 ctx-ui update 0.0.2' 2>&1)" || true
check "update reports the upgrade" contains "ctx-ui updated from 0.0.1 to 0.0.2" "$output"
check "settings are untouched" contains "CtxUi__PublicUrl=https://mcp.example.com" "$(in_box cat /etc/ctx-ui/ctx-ui.env)"
check "new version is in place" equals 0.0.2 "$(in_box ctx-ui version)"
check "no installer is left behind" equals "" "$(in_box find /tmp -maxdepth 1 -name 'tmp.*' -type f)"
check "service is active after upgrade" equals active "$(in_box systemctl is-active ctx-ui)"
check "client survives the upgrade" status_becomes 502 -H "Authorization: Bearer $token"
check "panel address survives the upgrade" contains "$panel" "$(in_box sh -c 'cd / && ctx-ui admin path' 2>&1)"
check "login form is served after the upgrade" equals 200 "$(panel_status "${path}login")"

echo "== reinstall"
output="$(in_box bash /dist/install.sh --archive "/dist/$second" --public-url https://other.example.com 2>&1)" || true
check "archive is installed over the same version" contains "ctx-ui 0.0.2 is installed and running." "$output"
check "installer says settings were kept" contains "new values were not applied" "$output"
check "settings stay untouched" contains "CtxUi__PublicUrl=https://mcp.example.com" "$(in_box cat /etc/ctx-ui/ctx-ui.env)"

output="$(in_box bash /dist/install.sh --archive /dist/SHA256SUMS 2>&1)" && code=0 || code=$?
check "refused archive leaves the service running" equals "1 active" "$code $(in_box systemctl is-active ctx-ui)"

echo "== uninstall"
in_box bash /dist/install.sh --uninstall > /dev/null 2>&1 || true
check "program is removed" equals "" "$(in_box sh -c 'ls -d /opt/ctx-ui /usr/local/bin/ctx-ui /etc/systemd/system/ctx-ui.service 2>/dev/null' || true)"
check "data and settings stay" equals "/etc/ctx-ui/ctx-ui.env /var/lib/ctx-ui/ctx-ui.db" \
    "$(in_box sh -c 'ls /etc/ctx-ui/ctx-ui.env /var/lib/ctx-ui/ctx-ui.db' | tr '\n' ' ' | sed 's/ $//')"
in_box bash /dist/install.sh --uninstall --purge > /dev/null 2>&1 || true
check "purge removes data and settings" equals "" "$(in_box sh -c 'ls -d /etc/ctx-ui /var/lib/ctx-ui 2>/dev/null' || true)"
check "purge removes the user" equals "" "$(in_box sh -c 'getent passwd ctx-ui' || true)"

if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all checks passed"
