#!/usr/bin/env bash
set -euo pipefail

home=/opt/ctx-ui
env_file=/etc/ctx-ui/ctx-ui.env
user=ctx-ui
repo=Echways/ctx-ui
installer=""

fail() {
    echo "ctx-ui: $*" >&2
    exit 1
}

require_root() {
    if [ "$(id -u)" -ne 0 ]; then
        fail "root privileges required, run with sudo"
    fi
}

update() {
    local base

    require_root
    case $# in
        0) base="https://github.com/$repo/releases/latest/download" ;;
        1)
            base="https://github.com/$repo/releases/download/v${1#v}"
            export CTX_UI_VERSION="${1#v}"
            ;;
        *) fail "usage: ctx-ui update [version]" ;;
    esac
    base="${CTX_UI_DOWNLOAD_URL:-$base}"

    installer="$(mktemp)"
    trap 'rm -f "$installer"' EXIT
    curl -fsSL -o "$installer" "$base/install.sh" || fail "failed to download $base/install.sh"
    bash "$installer"
}

down() {
    require_root
    [ $# -eq 0 ] || fail "usage: ctx-ui down"
    systemctl disable --now --quiet ctx-ui
    echo "ctx-ui is stopped and will not start at boot. Start it again with: sudo ctx-ui up"
}

up() {
    require_root
    [ $# -eq 0 ] || fail "usage: ctx-ui up"
    systemctl enable --now --quiet ctx-ui
    echo "ctx-ui is running."
}

case "${1:-}" in
    update)
        shift
        update "$@"
        exit 0
        ;;
    down)
        shift
        down "$@"
        exit 0
        ;;
    up)
        shift
        up "$@"
        exit 0
        ;;
    version)
        cat "$home/VERSION"
        exit 0
        ;;
esac

if [ "$(id -u)" -ne 0 ] && [ "$(id -un)" != "$user" ]; then
    fail "root privileges required, run with sudo"
fi

while IFS='=' read -r key value; do
    case "$key" in
        '' | \#*) continue ;;
    esac
    export "$key=$value"
done < "$env_file"

cd "$home"

if [ "$(id -u)" -eq 0 ]; then
    exec runuser -u "$user" -- "$home/ctx-ui" "$@"
fi

exec "$home/ctx-ui" "$@"
