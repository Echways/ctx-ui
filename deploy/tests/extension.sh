#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"
version="0.0.$(($(date +%s) % 65000))"
packages="$work/packages"
hive="$work/hive"
preview_pid=""
server_pid=""
failures=0

stop() {
    if [ -n "$1" ]; then
        kill -- "-$1" 2> /dev/null || true
        wait "$1" 2> /dev/null || true
    fi
}

cleanup() {
    stop "$preview_pid"
    stop "$server_pid"
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
    [[ "$2" != *"$1"* ]] || { echo "      expected not to contain: $1"; return 1; }
}

answers() {
    for _ in $(seq 1 120); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' "$1" || true)" != "000" ]; then
            return 0
        fi
        sleep 1
    done
    echo "      no answer from $1"
    return 1
}

serves() {
    local actual
    actual="$(curl -s -o /dev/null -w '%{http_code}' "$1" || true)"
    [ "$actual" = 200 ] || { echo "      $1 answered $actual"; return 1; }
}

log_shows() {
    for _ in $(seq 1 180); do
        if grep -q "$1" "$2"; then
            return 0
        fi
        sleep 1
    done
    echo "      $2 never showed: $1"
    tail -n 20 "$2"
    return 1
}

server_env() {
    env "CtxUi__DataDir=$work/data" "Urls=http://127.0.0.1:18790" "CtxUi__PublicUrl=http://127.0.0.1:18790" CtxUi__Language=en "$@"
}

echo "== packages"
for project in src/CtxUi.Extensions src/CtxUi.Extensions.Testing templates/CtxUi.Extensions.Templates; do
    dotnet pack "$root/$project" -c Release -p:Version="$version" -o "$packages" > "$work/pack.log" || { cat "$work/pack.log"; exit 1; }
done
check "three packages are built" contains "CtxUi.Extensions.Templates.$version.nupkg" "$(ls "$packages")"

echo "== template"
dotnet new install "$packages/CtxUi.Extensions.Templates.$version.nupkg" --debug:custom-hive "$hive" > /dev/null
mkdir "$work/foo"
cat > "$work/foo/nuget.config" <<XML
<configuration>
  <packageSources>
    <add key="local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
(cd "$work/foo" && dotnet new ctxui-extension -n Foo --id foo --debug:custom-hive "$hive" > /dev/null)
export NUGET_PACKAGES="$work/nuget"
dotnet publish "$work/foo/Foo" -c Release > "$work/publish.log" || { cat "$work/publish.log"; exit 1; }
check "the license stands at the project root" [ -f "$work/foo/Foo/LICENSE" ]
check "the project ignores build output" [ -f "$work/foo/Foo/.gitignore" ]
archive="$(find "$work/foo/Foo/bin/Release" -name 'foo-1.0.0.zip')"
check "the template builds into an archive" [ -n "$archive" ]
check "the manifest names the id" contains '"id": "foo"' "$(unzip -p "$archive" extension.json)"
output="$(dotnet publish "$work/foo/Foo" -c Release -p:CtxUiExtensionId=Bad_Id 2>&1)" || true
check "the build refuses a bad id" contains "CtxUiExtensionId 'Bad_Id'" "$output"
unset NUGET_PACKAGES

echo "== installed server"
dotnet publish "$root/src/CtxUi.Server" -c Release -r "linux-$(uname -m | sed -e s/x86_64/x64/ -e s/aarch64/arm64/)" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=0.0.1 -o "$work/server" > /dev/null
binary="$work/server/ctx-ui"
output="$(server_env "$binary" extension install "$archive" 2>&1)" || true
check "the archive installs" contains "foo 1.0.0" "$output"
(cd "$work/server" && exec setsid env "CtxUi__DataDir=$work/data" "Urls=http://127.0.0.1:18790" "CtxUi__PublicUrl=http://127.0.0.1:18790" CtxUi__Language=en "$binary") > "$work/server.log" 2>&1 &
server_pid=$!
check "the server starts" answers http://127.0.0.1:18790/healthz
check "health is fine" server_env "$binary" health
check "the extension is working" contains "foo  1.0.0  ok" "$(server_env "$binary" extension list 2>&1)"
check "the log has no errors" lacks "fail:" "$(cat "$work/server.log")"
stop "$server_pid"
server_pid=""

echo "== test package"
if CTXUI_BINARY="$binary" dotnet test --project "$root/tests/fixtures/SampleExtension.BlackBoxTests" -c Release > "$work/blackbox.log" 2>&1; then
    echo "ok    the test package opens a section in a browser"
else
    tail -n 40 "$work/blackbox.log"
    echo "FAIL  the test package opens a section in a browser"
    failures=$((failures + 1))
fi

echo "== preview"
export NUGET_PACKAGES="$work/nuget"
preview() {
    setsid env CTXUI_BINARY="$binary" dotnet build "$work/foo/Foo" -t:CtxUiPreview -nodeReuse:false > "$1" 2>&1 &
    preview_pid=$!
}
preview "$work/preview1.log"
check "the first preview shows the panel" log_shows "Panel:" "$work/preview1.log"
check "the first preview shows the password" log_shows "Password:" "$work/preview1.log"
check "the first preview answers" answers http://127.0.0.1:5199/healthz
panel="$(grep -o -E 'http://127\.0\.0\.1:5199/[a-z0-9]{16}/' "$work/preview1.log" | head -n 1 || true)"
check "the first preview serves the panel script" serves "${panel}_framework/blazor.web.js"
stop "$preview_pid"
preview_pid=""
preview "$work/preview2.log"
check "the second preview shows the panel" log_shows "Panel:" "$work/preview2.log"
check "the second preview answers" answers http://127.0.0.1:5199/healthz
check "the second preview serves the panel script" serves "${panel}_framework/blazor.web.js"
check "the second preview has no errors" lacks "error" "$(cat "$work/preview2.log")"
stop "$preview_pid"
preview_pid=""

if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all checks passed"
