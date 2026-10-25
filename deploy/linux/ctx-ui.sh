#!/usr/bin/env bash
set -euo pipefail

home=/opt/ctx-ui
config_dir=/etc/ctx-ui
env_file="$config_dir/ctx-ui.env"
data_dir=/var/lib/ctx-ui
wrapper=/usr/local/bin/ctx-ui
unit=/etc/systemd/system/ctx-ui.service
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

uninstall() {
    local keep_data=0 confirmed=0 answer=""

    require_root
    while [ $# -gt 0 ]; do
        case "$1" in
            --keep-data) keep_data=1 ;;
            --yes) confirmed=1 ;;
            *) fail "usage: ctx-ui uninstall [--keep-data] [--yes]" ;;
        esac
        shift
    done

    if [ "$confirmed" -eq 0 ]; then
        if ! (: < /dev/tty) 2> /dev/null; then
            fail "no terminal to confirm removal, add --yes"
        fi
        if [ "$keep_data" -eq 1 ]; then
            printf 'Remove ctx-ui and keep settings in %s and data in %s? Type yes: ' "$config_dir" "$data_dir" > /dev/tty
        else
            printf 'Remove ctx-ui with all settings, data and keys in %s and %s? This cannot be undone. Type yes: ' "$config_dir" "$data_dir" > /dev/tty
        fi
        read -r answer < /dev/tty || answer=""
        [ "$answer" = yes ] || fail "removal cancelled"
    fi

    systemctl disable --now --quiet ctx-ui > /dev/null 2>&1 || true
    rm -f "$unit" "$wrapper"
    rm -rf "$home"
    systemctl daemon-reload

    if [ "$keep_data" -eq 1 ]; then
        echo "ctx-ui removed. Settings in $config_dir and data in $data_dir were kept."
        return 0
    fi

    rm -rf "$config_dir" "$data_dir"
    if id -u "$user" > /dev/null 2>&1; then
        userdel "$user"
    fi
    echo "ctx-ui removed together with settings and data."
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
    uninstall)
        shift
        uninstall "$@"
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
