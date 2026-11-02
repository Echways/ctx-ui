#!/usr/bin/env bash
set -euo pipefail

default_version=""
repo="Echways/ctx-ui"
label="ctx-ui.builder"
extension_id="builder"
minimum_docker=29
unit="/etc/systemd/system/ctx-ui.service"
env_file="/etc/ctx-ui/ctx-ui.env"
builder_home="/opt/ctx-ui-builder"
control_port=8091
ingress_port=8090
docker_runner_address="10.212.0.2"
compose_asset="builder-compose.yaml"
overlay_asset="builder-compose.docker.yaml"
setting_prefix="CtxUi__Extensions__${extension_id}__"
work=""
mode=""
stack_project=""
stack_dir=""
stack_files=()

fail() {
    echo "ctx-ui builder: $*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Installs the ctx-ui server builder next to an installed ctx-ui.

  builder-install.sh [--version <version>]
  builder-install.sh --remove

  --version  release version without the leading v
  --remove   remove the containers labelled ctx-ui.builder and the extension; ctx-ui keeps running

The version is also read from CTXB_VERSION.
CTX_UI_DOWNLOAD_URL sets where the archive and SHA256SUMS are, instead of the release page.
EOF
}

cleanup() {
    if [ -n "$work" ]; then
        rm -rf "$work"
    fi
}

read_value() {
    if [ -f "$1" ]; then
        sed -n "s/^$2=//p" "$1" | head -n 1
    fi
}

write_value() {
    local file="$1" key="$2" value="$3" next
    next="$(mktemp)"
    if [ -f "$file" ]; then
        grep -v "^$key=" "$file" > "$next" || true
    fi
    echo "$key=$value" >> "$next"
    cat "$next" > "$file"
    rm -f "$next"
}

delete_value() {
    local file="$1" key="$2" next
    if [ -f "$file" ]; then
        next="$(mktemp)"
        grep -v "^$key=" "$file" > "$next" || true
        cat "$next" > "$file"
        rm -f "$next"
    fi
}

new_token() {
    head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'
}

check_tools() {
    local tool
    for tool in curl sha256sum od docker; do
        if ! command -v "$tool" > /dev/null 2>&1; then
            fail "command not found: $tool"
        fi
    done
}

check_docker() {
    local version major
    version="$(docker version --format '{{.Server.Version}}' 2> /dev/null)" || fail "Docker is not running or not reachable, start it and run the command again"
    major="${version%%.*}"
    if ! [[ "$major" =~ ^[0-9]+$ ]] || [ "$major" -lt "$minimum_docker" ]; then
        fail "Docker Engine $minimum_docker or newer is required, this host runs $version"
    fi
    docker compose version > /dev/null 2>&1 || fail "the Docker Compose plugin is required"
}

detect_install() {
    local id files
    if [ -f "$unit" ]; then
        mode=systemd
        return 0
    fi
    id="$(docker ps -a -q --filter label=com.docker.compose.service=ctx-ui | head -n 1)"
    if [ -z "$id" ]; then
        mode=""
        return 0
    fi
    mode=docker
    stack_project="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$id")"
    stack_dir="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' "$id")"
    files="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.config_files"}}' "$id")"
    IFS=, read -r -a stack_files <<< "$files"
    if [ -z "$stack_project" ] || [ ! -d "$stack_dir" ] || [ "${#stack_files[@]}" -eq 0 ]; then
        fail "cannot read where the ctx-ui compose files are from the running container"
    fi
}

download() {
    local version="$1" name names
    names=("builder-$version.zip" "$compose_asset")
    local base="${CTX_UI_DOWNLOAD_URL:-https://github.com/$repo/releases/download/v$version}"

    if [ "$mode" = docker ]; then
        names+=("$overlay_asset")
    fi
    echo "Downloading builder $version" >&2
    curl -fsSL -o "$work/SHA256SUMS" "$base/SHA256SUMS" || fail "failed to download $base/SHA256SUMS"
    for name in "${names[@]}"; do
        curl -fsSL -o "$work/$name" "$base/$name" || fail "failed to download $base/$name"
        if ! (cd "$work" && grep -F "  $name" SHA256SUMS | sha256sum -c --status -); then
            fail "checksum of $name does not match, installation aborted"
        fi
    done
}

wait_for() {
    local seconds="$1"
    shift
    for _ in $(seq 1 "$seconds"); do
        if "$@"; then
            return 0
        fi
        sleep 1
    done
    return 1
}

runner_answers() {
    local body
    body="$(curl -fsS -m 3 -H "Authorization: Bearer $2" "$1/v1/health" 2> /dev/null)" || return 1
    [[ "$body" == *"\"version\":\"$3\""* ]]
}

wait_for_runner() {
    local url="$1" token="$2" version="$3"
    if ! wait_for 90 runner_answers "$url" "$token" "$version"; then
        docker logs --tail 30 ctxb-runner >&2 2>&1 || true
        fail "the Runner did not report version $version at $url within 90 seconds, see the log above"
    fi
}

panel_url() {
    local listen
    listen="$(read_value "$env_file" Urls)"
    listen="${listen%%;*}"
    listen="${listen:-http://127.0.0.1:8080}"
    echo "$listen" | sed -E 's#//(0\.0\.0\.0|\*|\+|\[::\])#//127.0.0.1#'
}

panel_answers() {
    curl -s -o /dev/null --max-time 2 "$(panel_url)/"
}

builder_compose() {
    docker compose -f "$builder_home/$compose_asset" "$@"
}

ours() {
    [ "$(basename "$1")" = "$compose_asset" ] || [ "$(basename "$1")" = "$overlay_asset" ]
}

stack_compose() {
    local with_builder="$1" file args=()
    shift
    for file in "${stack_files[@]}"; do
        if ! ours "$file"; then
            args+=(-f "$file")
        fi
    done
    if [ "$with_builder" -eq 1 ]; then
        args+=(-f "$stack_dir/$compose_asset" -f "$stack_dir/$overlay_asset")
    fi
    docker compose --project-directory "$stack_dir" -p "$stack_project" "${args[@]}" "$@"
}

stack_container() {
    stack_compose "$1" ps -q ctx-ui
}

stack_healthy() {
    docker exec "$1" ctx-ui health > /dev/null 2>&1
}

compose_file_value() {
    local file joined=""
    for file in "${stack_files[@]}"; do
        if ! ours "$file"; then
            joined="${joined:+$joined:}$file"
        fi
    done
    if [ "$1" -eq 1 ]; then
        joined="$joined:$stack_dir/$compose_asset:$stack_dir/$overlay_asset"
    fi
    echo "$joined"
}

install_systemd() {
    local version="$1" token archive existing_token existing_version
    existing_token="$(read_value "$builder_home/.env" CTXB_RUNNER_TOKEN)"
    existing_version="$(read_value "$builder_home/.env" CTXB_VERSION)"
    token="${existing_token:-$(new_token)}"

    install -d -m 755 "$builder_home"
    install -m 644 "$work/$compose_asset" "$builder_home/$compose_asset"
    (
        umask 077
        touch "$builder_home/.env"
    )
    write_value "$builder_home/.env" CTXB_VERSION "$version"
    write_value "$builder_home/.env" CTXB_RUNNER_TOKEN "$token"
    builder_compose up -d > /dev/null
    wait_for_runner "http://127.0.0.1:$control_port" "$token" "$version"

    chmod 755 "$work"
    chmod 644 "$work/builder-$version.zip"
    archive="$work/builder-$version.zip"
    (cd / && ctx-ui extension install "$archive") > /dev/null

    write_value "$env_file" "${setting_prefix}RunnerUrl" "http://127.0.0.1:$control_port"
    write_value "$env_file" "${setting_prefix}RunnerIngressUrl" "http://127.0.0.1:$ingress_port"
    write_value "$env_file" "${setting_prefix}RunnerToken" "$token"

    systemctl restart ctx-ui
    if ! wait_for 30 panel_answers; then
        journalctl -u ctx-ui -n 30 --no-pager >&2 || true
        fail "ctx-ui did not answer at $(panel_url) within 30 seconds, see the log above"
    fi

    if [ -n "$existing_version" ] && [ "$existing_version" != "$version" ]; then
        echo "Server builder updated from $existing_version to $version."
    else
        echo "Server builder $version is installed."
    fi
    cat <<EOF

  Runner:    containers ctxb-runner and ctxb-egress, project ctx-ui-builder
  Compose:   $builder_home
  Settings:  $env_file, keys ${setting_prefix}*
  Remove:    builder-install.sh --remove

Open the "Builder" section in the panel.
EOF
}

install_docker() {
    local version="$1" token archive container existing_token existing_version
    existing_token="$(read_value "$stack_dir/.env" CTXB_RUNNER_TOKEN)"
    existing_version="$(read_value "$stack_dir/.env" CTXB_VERSION)"
    token="${existing_token:-$(new_token)}"

    install -m 644 "$work/$compose_asset" "$stack_dir/$compose_asset"
    install -m 644 "$work/$overlay_asset" "$stack_dir/$overlay_asset"
    touch "$stack_dir/.env"
    write_value "$stack_dir/.env" CTXB_VERSION "$version"
    write_value "$stack_dir/.env" CTXB_RUNNER_TOKEN "$token"
    write_value "$stack_dir/.env" COMPOSE_FILE "$(compose_file_value 1)"
    stack_compose 1 up -d > /dev/null
    wait_for_runner "http://$docker_runner_address:$control_port" "$token" "$version"

    container="$(stack_container 1)"
    archive="/tmp/builder-$version.zip"
    docker exec -i "$container" sh -c "cat > $archive" < "$work/builder-$version.zip"
    docker exec "$container" ctx-ui extension install "$archive" > /dev/null
    docker exec "$container" rm -f "$archive"
    docker restart "$container" > /dev/null
    if ! wait_for 60 stack_healthy "$container"; then
        docker logs --tail 30 "$container" >&2 2>&1 || true
        fail "ctx-ui did not become healthy within 60 seconds, see the log above"
    fi

    if [ -n "$existing_version" ] && [ "$existing_version" != "$version" ]; then
        echo "Server builder updated from $existing_version to $version."
    else
        echo "Server builder $version is installed."
    fi
    cat <<EOF

  Runner:    containers ctxb-runner and ctxb-egress on the network ctxb-control, $docker_runner_address
  Compose:   $stack_dir, files $compose_asset and $overlay_asset, listed in COMPOSE_FILE of .env
  Remove:    builder-install.sh --remove

Open the "Builder" section in the panel.
EOF
}

remove_containers() {
    local ids
    ids="$(docker ps -a -q --filter "label=$label")"
    if [ -n "$ids" ]; then
        echo "$ids" | xargs docker rm -f > /dev/null
    fi
}

extension_listed() {
    local listing
    listing="$(cat)"
    grep -qE "^${extension_id}[[:space:]]" <<< "$listing"
}

remove_systemd() {
    remove_containers
    if (cd / && ctx-ui extension list 2> /dev/null) | extension_listed; then
        (cd / && ctx-ui extension remove "$extension_id") > /dev/null
    fi
    delete_value "$env_file" "${setting_prefix}RunnerUrl"
    delete_value "$env_file" "${setting_prefix}RunnerIngressUrl"
    delete_value "$env_file" "${setting_prefix}RunnerToken"
    rm -rf "$builder_home"
    systemctl restart ctx-ui
    wait_for 30 panel_answers || fail "ctx-ui did not answer at $(panel_url) within 30 seconds"
}

remove_docker() {
    local container remaining
    container="$(stack_container 0 | head -n 1)"
    if [ -n "$container" ] && docker exec "$container" ctx-ui extension list 2> /dev/null | extension_listed; then
        docker exec "$container" ctx-ui extension remove "$extension_id" > /dev/null
    fi
    remove_containers
    remaining="$(compose_file_value 0)"
    if [ "$remaining" = "$stack_dir/compose.yaml" ]; then
        delete_value "$stack_dir/.env" COMPOSE_FILE
    else
        write_value "$stack_dir/.env" COMPOSE_FILE "$remaining"
    fi
    delete_value "$stack_dir/.env" CTXB_VERSION
    delete_value "$stack_dir/.env" CTXB_RUNNER_TOKEN
    rm -f "$stack_dir/$compose_asset" "$stack_dir/$overlay_asset"
    stack_compose 0 up -d --no-deps ctx-ui > /dev/null
    container="$(stack_container 0)"
    wait_for 60 stack_healthy "$container" || fail "ctx-ui did not become healthy within 60 seconds"
}

main() {
    local version="${CTXB_VERSION:-$default_version}" remove=0

    while [ $# -gt 0 ]; do
        case "$1" in
            --version)
                if [ $# -lt 2 ]; then
                    fail "$1 needs a value"
                fi
                version="${2#v}"
                shift 2
                ;;
            --remove)
                remove=1
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
    check_tools
    check_docker
    detect_install

    if [ "$remove" -eq 1 ]; then
        case "$mode" in
            systemd) remove_systemd ;;
            docker) remove_docker ;;
            *) remove_containers ;;
        esac
        echo "Server builder removed. ctx-ui keeps running; networks and volumes of the builder were kept."
        return 0
    fi

    if [ -z "$mode" ]; then
        fail "ctx-ui is not installed here: no $unit and no running compose project with a ctx-ui service. Install ctx-ui first, see docs/install.md"
    fi
    if [ -z "$version" ]; then
        fail "no version given: add --version or take builder-install.sh from the release page"
    fi

    work="$(mktemp -d)"
    trap cleanup EXIT
    download "$version"

    case "$mode" in
        systemd) install_systemd "$version" ;;
        docker) install_docker "$version" ;;
    esac
}

main "$@"
