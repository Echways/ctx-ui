#!/usr/bin/env bash
set -euo pipefail

default_version=""
repo="Echways/ctx-ui"
user="ctx-ui"
home="/opt/ctx-ui"
config_dir="/etc/ctx-ui"
env_file="$config_dir/ctx-ui.env"
data_dir="/var/lib/ctx-ui"
wrapper="/usr/local/bin/ctx-ui"
unit="/etc/systemd/system/ctx-ui.service"
url_pattern='^https?://[][A-Za-z0-9._~:/@%-]+$'
work=""

fail() {
    echo "ctx-ui: $*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Installs ctx-ui.

  install.sh [--public-url <address>] [--listen <address>] [--version <version>] [--archive <file>]
  install.sh --uninstall [--purge]

  --public-url  external gateway address, the start of every client address
  --listen      address the service listens on
  --version     release version without the leading v
  --archive     local archive instead of a download, installed even over the same version
  --uninstall   remove the program, the service and the ctx-ui command
  --purge       with --uninstall, also remove settings, data and the user

The same values are read from CTX_UI_PUBLIC_URL, CTX_UI_LISTEN and CTX_UI_VERSION.
CTX_UI_DOWNLOAD_URL sets where the archive and SHA256SUMS are, instead of the release page.
EOF
}

cleanup() {
    if [ -n "$work" ]; then
        rm -rf "$work"
    fi
}

ask() {
    local question="$1" fallback="$2" answer=""
    if (: < /dev/tty) 2> /dev/null; then
        printf '%s [%s]: ' "$question" "$fallback" > /dev/tty
        read -r answer < /dev/tty || answer=""
    fi
    echo "${answer:-$fallback}"
}

detect_arch() {
    case "$(uname -m)" in
        x86_64 | amd64) echo x64 ;;
        aarch64 | arm64) echo arm64 ;;
        *) fail "architecture $(uname -m) is not supported, builds exist for x86_64 and aarch64" ;;
    esac
}

nologin_shell() {
    if [ -x /usr/sbin/nologin ]; then
        echo /usr/sbin/nologin
    else
        echo /sbin/nologin
    fi
}

download() {
    local version="$1" arch="$2"
    local name="ctx-ui-$version-linux-$arch.tar.gz"
    local base="${CTX_UI_DOWNLOAD_URL:-https://github.com/$repo/releases/download/v$version}"

    echo "Downloading $name" >&2
    curl -fsSL -o "$work/$name" "$base/$name" || fail "failed to download $base/$name"
    curl -fsSL -o "$work/SHA256SUMS" "$base/SHA256SUMS" || fail "failed to download $base/SHA256SUMS"
    if ! (cd "$work" && grep -F "  $name" SHA256SUMS | sha256sum -c --status -); then
        fail "checksum of $name does not match, installation aborted"
    fi
    echo "$work/$name"
}

configure() {
    local public_url="$1" listen="$2"

    if [ -z "$public_url" ]; then
        public_url="$(ask "External address clients use to reach the gateway" "http://localhost:8080")"
    fi
    if [ -z "$listen" ]; then
        listen="$(ask "Address the service listens on" "http://127.0.0.1:8080")"
    fi
    public_url="${public_url%/}"
    listen="${listen%/}"

    if ! [[ "$public_url" =~ $url_pattern ]]; then
        fail "external address must look like https://mcp.example.com, got: $public_url"
    fi
    if ! [[ "$listen" =~ $url_pattern ]]; then
        fail "listen address must look like http://127.0.0.1:8080, got: $listen"
    fi

    install -d -m 750 -o root -g "$user" "$config_dir"
    printf 'Urls=%s\nCtxUi__PublicUrl=%s\nCtxUi__DataDir=%s\n' "$listen" "$public_url" "$data_dir" > "$env_file"
    chown "root:$user" "$env_file"
    chmod 640 "$env_file"
}

setting() {
    sed -n "s/^$1=//p" "$env_file" | head -n 1
}

probe_url() {
    local listen
    listen="$(setting Urls)"
    listen="${listen%%;*}"
    listen="${listen:-http://127.0.0.1:8080}"
    echo "$listen" | sed -E 's#//(0\.0\.0\.0|\*|\+|\[::\])#//127.0.0.1#'
}

wait_ready() {
    local url="$1"
    for _ in $(seq 1 30); do
        if curl -s -o /dev/null --max-time 2 "$url/"; then
            return 0
        fi
        sleep 1
    done
    return 1
}

remove() {
    local purge="$1"

    systemctl disable --now ctx-ui > /dev/null 2>&1 || true
    rm -f "$unit" "$wrapper"
    rm -rf "$home"
    systemctl daemon-reload

    if [ "$purge" -eq 1 ]; then
        rm -rf "$config_dir" "$data_dir"
        if id -u "$user" > /dev/null 2>&1; then
            userdel "$user"
        fi
        echo "ctx-ui removed together with settings and data."
    else
        echo "ctx-ui removed. Settings in $config_dir and data in $data_dir were kept."
    fi
}

main() {
    local public_url="${CTX_UI_PUBLIC_URL:-}"
    local listen="${CTX_UI_LISTEN:-}"
    local version="${CTX_UI_VERSION:-$default_version}"
    local archive="" uninstall=0 purge=0 previous="" arch

    while [ $# -gt 0 ]; do
        case "$1" in
            --public-url | --listen | --version | --archive)
                if [ $# -lt 2 ]; then
                    fail "$1 needs a value"
                fi
                case "$1" in
                    --public-url) public_url="$2" ;;
                    --listen) listen="$2" ;;
                    --version) version="${2#v}" ;;
                    --archive) archive="$2" ;;
                esac
                shift 2
                ;;
            --uninstall)
                uninstall=1
                shift
                ;;
            --purge)
                purge=1
                shift
                ;;
            -h | --help)
                usage
                return 0
                ;;
            *)
                fail "unknown option $1, see --help for the list"
                ;;
        esac
    done

    if [ "$(id -u)" -ne 0 ]; then
        fail "root privileges required, run with sudo"
    fi
    if ! command -v systemctl > /dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
        fail "systemd is required; without it install with Docker, see docs/install.md"
    fi

    if [ "$uninstall" -eq 1 ]; then
        remove "$purge"
        return 0
    fi
    if [ "$purge" -eq 1 ]; then
        fail "--purge works only together with --uninstall"
    fi

    for tool in curl tar sha256sum runuser useradd; do
        if ! command -v "$tool" > /dev/null 2>&1; then
            fail "command not found: $tool"
        fi
    done

    arch="$(detect_arch)"
    work="$(mktemp -d)"
    trap cleanup EXIT

    if [ -z "$archive" ]; then
        if [ -z "$version" ]; then
            fail "no version given: add --version or take install.sh from the release page"
        fi
        if [ -f "$home/VERSION" ] && [ "$(cat "$home/VERSION")" = "$version" ] && systemctl is-active --quiet ctx-ui; then
            echo "ctx-ui $version is already installed and running, nothing to update."
            return 0
        fi
        archive="$(download "$version" "$arch")"
    elif [ ! -f "$archive" ]; then
        fail "file $archive not found"
    fi

    if ! tar --no-same-owner -xzf "$archive" -C "$work" 2> /dev/null; then
        fail "archive $archive cannot be unpacked"
    fi
    if [ ! -f "$work/ctx-ui/ctx-ui" ] || [ ! -f "$work/ctx-ui/VERSION" ]; then
        fail "archive $archive does not look like a ctx-ui release"
    fi
    version="$(cat "$work/ctx-ui/VERSION")"

    if [ -f "$home/VERSION" ]; then
        previous="$(cat "$home/VERSION")"
    fi

    if ! id -u "$user" > /dev/null 2>&1; then
        useradd --system --user-group --no-create-home --home-dir "$data_dir" --shell "$(nologin_shell)" "$user"
    fi
    install -d -m 700 -o "$user" -g "$user" "$data_dir"

    if [ -f "$env_file" ]; then
        if [ -n "$public_url$listen" ]; then
            echo "Settings already exist in $env_file, new values were not applied." >&2
        fi
    else
        configure "$public_url" "$listen"
    fi

    rm -rf "$home"
    mv "$work/ctx-ui" "$home"
    chown -R root:root "$home"
    chmod 755 "$home" "$home/ctx-ui"
    install -m 755 "$home/ctx-ui.sh" "$wrapper"
    install -m 644 "$home/ctx-ui.service" "$unit"

    systemctl daemon-reload
    systemctl enable ctx-ui > /dev/null 2>&1
    systemctl restart ctx-ui

    if ! wait_ready "$(probe_url)"; then
        journalctl -u ctx-ui -n 30 --no-pager >&2 || true
        fail "the service did not answer at $(probe_url) within 30 seconds, see the log above"
    fi

    if [ -n "$previous" ] && [ "$previous" != "$version" ]; then
        echo "ctx-ui updated from $previous to $version and running."
    else
        echo "ctx-ui $version is installed and running."
    fi
    cat <<EOF

  Listens on:        $(setting Urls)
  External address:  $(setting CtxUi__PublicUrl)
  Settings:          $env_file, after editing: systemctl restart ctx-ui
  Data:              $data_dir
  Log:               journalctl -u ctx-ui -f
  Upgrade:           ctx-ui update
  Stop, start:       ctx-ui down, ctx-ui up

The service does not terminate TLS: put Caddy or nginx in front of it.

Next:
  ctx-ui admin add admin
      prints the panel address and a password; inbounds and clients can be added there
  ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
  ctx-ui client add "Ivanov" --inbound learn
EOF
}

main "$@"
