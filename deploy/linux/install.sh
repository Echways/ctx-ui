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
Установка ctx-ui.

  install.sh [--public-url <адрес>] [--listen <адрес>] [--version <версия>] [--archive <файл>]
  install.sh --uninstall [--purge]

  --public-url  внешний адрес шлюза, с него начинаются адреса для клиентов
  --listen      адрес, на котором слушает сервис
  --version     версия релиза без буквы v
  --archive     готовый архив вместо скачивания, ставится даже поверх той же версии
  --uninstall   удалить программу, сервис и команду ctx-ui
  --purge       вместе с --uninstall удалить настройки, данные и пользователя

Те же значения читаются из CTX_UI_PUBLIC_URL, CTX_UI_LISTEN и CTX_UI_VERSION.
CTX_UI_DOWNLOAD_URL задаёт адрес, где лежат архив и SHA256SUMS, вместо страницы релиза.
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
        *) fail "архитектура $(uname -m) не поддерживается, есть сборки под x86_64 и aarch64" ;;
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

    echo "Скачиваю $name" >&2
    curl -fsSL -o "$work/$name" "$base/$name" || fail "не удалось скачать $base/$name"
    curl -fsSL -o "$work/SHA256SUMS" "$base/SHA256SUMS" || fail "не удалось скачать $base/SHA256SUMS"
    if ! (cd "$work" && grep -F "  $name" SHA256SUMS | sha256sum -c --status -); then
        fail "контрольная сумма $name не совпала, установка прервана"
    fi
    echo "$work/$name"
}

configure() {
    local public_url="$1" listen="$2"

    if [ -z "$public_url" ]; then
        public_url="$(ask "Внешний адрес, по которому клиенты видят шлюз" "http://localhost:8080")"
    fi
    if [ -z "$listen" ]; then
        listen="$(ask "Адрес, на котором слушает сервис" "http://127.0.0.1:8080")"
    fi
    public_url="${public_url%/}"
    listen="${listen%/}"

    if ! [[ "$public_url" =~ $url_pattern ]]; then
        fail "внешний адрес должен выглядеть как https://mcp.example.com, получено: $public_url"
    fi
    if ! [[ "$listen" =~ $url_pattern ]]; then
        fail "адрес прослушивания должен выглядеть как http://127.0.0.1:8080, получено: $listen"
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
        echo "ctx-ui удалён вместе с настройками и данными."
    else
        echo "ctx-ui удалён. Настройки в $config_dir и данные в $data_dir оставлены."
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
                    fail "после $1 нужно значение"
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
                fail "неизвестный параметр $1, список параметров: --help"
                ;;
        esac
    done

    if [ "$(id -u)" -ne 0 ]; then
        fail "нужны права root, запустите через sudo"
    fi
    if ! command -v systemctl > /dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
        fail "нужен systemd; без него ставьте через Docker, см. docs/install.md"
    fi

    if [ "$uninstall" -eq 1 ]; then
        remove "$purge"
        return 0
    fi
    if [ "$purge" -eq 1 ]; then
        fail "--purge работает только вместе с --uninstall"
    fi

    for tool in curl tar sha256sum runuser useradd; do
        if ! command -v "$tool" > /dev/null 2>&1; then
            fail "не найдена команда $tool"
        fi
    done

    arch="$(detect_arch)"
    work="$(mktemp -d)"
    trap cleanup EXIT

    if [ -z "$archive" ]; then
        if [ -z "$version" ]; then
            fail "не указана версия: добавьте --version или возьмите install.sh со страницы релиза"
        fi
        if [ -f "$home/VERSION" ] && [ "$(cat "$home/VERSION")" = "$version" ] && systemctl is-active --quiet ctx-ui; then
            echo "ctx-ui $version уже установлен и запущен, обновлять нечего."
            return 0
        fi
        archive="$(download "$version" "$arch")"
    elif [ ! -f "$archive" ]; then
        fail "файл $archive не найден"
    fi

    if ! tar --no-same-owner -xzf "$archive" -C "$work" 2> /dev/null; then
        fail "архив $archive не распаковывается"
    fi
    if [ ! -f "$work/ctx-ui/ctx-ui" ] || [ ! -f "$work/ctx-ui/VERSION" ]; then
        fail "архив $archive не похож на релиз ctx-ui"
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
            echo "Настройки уже есть в $env_file, новые значения не применены." >&2
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
        fail "сервис не ответил на $(probe_url) за 30 секунд, журнал выше"
    fi

    if [ -n "$previous" ] && [ "$previous" != "$version" ]; then
        echo "ctx-ui обновлён с $previous до $version и запущен."
    else
        echo "ctx-ui $version установлен и запущен."
    fi
    cat <<EOF

  Слушает:        $(setting Urls)
  Внешний адрес:  $(setting CtxUi__PublicUrl)
  Настройки:      $env_file, после правки: systemctl restart ctx-ui
  Данные:         $data_dir
  Журнал:         journalctl -u ctx-ui -f
  Обновление:     ctx-ui update

TLS сервис не поднимает: перед ним ставится Caddy или nginx.

Дальше:
  ctx-ui admin add admin
      печатает адрес панели и пароль; inbound'ы и клиентов можно заводить в ней
  ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
  ctx-ui client add "Иванов" --inbound learn
EOF
}

main "$@"
