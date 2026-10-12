#!/usr/bin/env bash
set -euo pipefail

home=/opt/ctx-ui
env_file=/etc/ctx-ui/ctx-ui.env
user=ctx-ui

if [ "$(id -u)" -ne 0 ] && [ "$(id -un)" != "$user" ]; then
    echo "ctx-ui: нужны права root, запустите через sudo" >&2
    exit 1
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
