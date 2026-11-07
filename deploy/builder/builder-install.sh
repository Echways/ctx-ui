#!/usr/bin/env bash
set -euo pipefail

default_version=""
repo="Echways/ctx-ui"
label="ctx-ui.builder"
extension_id="builder"
minimum_docker=29
semver='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
panel_image_pattern=' ghcr\.io/echways/ctx-ui:[^ ]+$'
control_subnet="10.212.0.0/29"
control_network="ctxb-control"
default_project="ctx-ui"
runner_container="ctxb-runner"
runtime_image="ghcr.io/echways/ctx-ui-builder-runtime"
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
fresh=0
undo=()

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
    local status=$? index
    trap - EXIT
    if [ "$status" -ne 0 ]; then
        for ((index = ${#undo[@]} - 1; index >= 0; index--)); do
            "${undo[$index]}" || true
        done
    fi
    if [ -n "$work" ]; then
        rm -rf "$work"
    fi
    exit "$status"
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
    if [ "$1" -eq 0 ]; then
        return 0
    fi
    major="${version%%.*}"
    if ! [[ "$major" =~ ^[0-9]+$ ]] || [ "$major" -lt "$minimum_docker" ]; then
        fail "Docker Engine $minimum_docker or newer is required, this host runs $version"
    fi
    docker compose version > /dev/null 2>&1 || fail "the Docker Compose plugin is required"
}

panel_candidates() {
    docker ps "$@" --filter label=com.docker.compose.service=ctx-ui --format '{{.ID}} {{.Image}}' | grep -E -- "$panel_image_pattern" || true
}

detect_install() {
    local candidates files panel_container state=running
    if [ -f "$unit" ]; then
        mode=systemd
        return 0
    fi
    candidates="$(panel_candidates)"
    if [ -z "$candidates" ] && [ "$1" -eq 1 ]; then
        state=stopped
        candidates="$(panel_candidates -a)"
    fi
    if [ -z "$candidates" ]; then
        mode=""
        return 0
    fi
    if [ "$(wc -l <<< "$candidates")" -ne 1 ]; then
        echo "$candidates" >&2
        fail "more than one $state ctx-ui container found (listed above), stop or remove the extra ones and run the command again"
    fi
    panel_container="${candidates%% *}"
    mode=docker
    stack_project="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$panel_container")"
    stack_dir="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' "$panel_container")"
    files="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.config_files"}}' "$panel_container")"
    IFS=, read -r -a stack_files <<< "$files"
    if [ -z "$stack_project" ] || [ ! -d "$stack_dir" ] || [ "${#stack_files[@]}" -eq 0 ]; then
        fail "cannot read where the ctx-ui compose files are from the ctx-ui container"
    fi
}

panel_cli() {
    if [ "$mode" = systemd ]; then
        (cd / && ctx-ui "$@")
    else
        docker exec "$(stack_container 0 | head -n 1)" ctx-ui "$@"
    fi
}

port_busy() {
    (exec 3<> "/dev/tcp/127.0.0.1/$1") 2> /dev/null
}

check_ports() {
    local port
    if docker inspect "$runner_container" > /dev/null 2>&1; then
        return 0
    fi
    for port in "$ingress_port" "$control_port"; do
        if port_busy "$port"; then
            fail "port $port on 127.0.0.1 is already in use, free it and run the command again"
        fi
    done
}

address_number() {
    local a b c d
    IFS=. read -r a b c d <<< "$1"
    echo $(((a << 24) | (b << 16) | (c << 8) | d))
}

subnet_overlaps() {
    local address="${1%/*}" prefix="${1#*/}" size start ours_address="${control_subnet%/*}" ours_prefix="${control_subnet#*/}" ours_size ours_start
    size=$((1 << (32 - prefix)))
    start=$(($(address_number "$address") / size * size))
    ours_size=$((1 << (32 - ours_prefix)))
    ours_start=$(($(address_number "$ours_address") / ours_size * ours_size))
    [ "$start" -le $((ours_start + ours_size - 1)) ] && [ $((start + size - 1)) -ge "$ours_start" ]
}

check_subnet() {
    local ids=() name subnet subnets
    if docker network inspect "$control_network" > /dev/null 2>&1; then
        return 0
    fi
    mapfile -t ids < <(docker network ls -q)
    if [ "${#ids[@]}" -eq 0 ]; then
        return 0
    fi
    while read -r name subnets; do
        for subnet in $subnets; do
            if [[ "$subnet" != *:* ]] && subnet_overlaps "$subnet"; then
                fail "the subnet $subnet of the Docker network $name overlaps $control_subnet, which the Runner network needs"
            fi
        done
    done < <(docker network inspect -f '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}' "${ids[@]}")
}

pull_images() {
    local failure="the images of builder $1 could not be pulled, nothing was changed"
    echo "Pulling the images" >&2
    CTXB_VERSION="$1" CTXB_RUNNER_TOKEN=pull docker compose -f "$work/$compose_asset" pull --policy missing --quiet || fail "$failure"
    if ! docker image inspect "$runtime_image:$1" > /dev/null 2>&1; then
        docker pull --quiet "$runtime_image:$1" > /dev/null || fail "$failure"
    fi
}

extension_listed() {
    local listing
    listing="$(cat)"
    grep -qE "^${extension_id}[[:space:]]" <<< "$listing"
}

undo_extension() {
    panel_cli extension remove "$extension_id" > /dev/null
}

install_extension() {
    local version="$1" archive container listed=0
    if panel_cli extension list 2> /dev/null | extension_listed; then
        listed=1
    fi
    if [ "$mode" = systemd ]; then
        chmod 755 "$work"
        chmod 644 "$work/builder-$version.zip"
        archive="$work/builder-$version.zip"
        panel_cli extension install "$archive" > /dev/null || fail "ctx-ui did not accept the extension (see above), nothing was started"
    else
        archive="/tmp/builder-$version.zip"
        container="$(stack_container 0 | head -n 1)"
        docker exec -i "$container" sh -c "cat > $archive" < "$work/builder-$version.zip"
        docker exec "$container" ctx-ui extension install "$archive" > /dev/null || fail "ctx-ui did not accept the extension (see above), nothing was started"
        docker exec "$container" rm -f "$archive"
    fi
    if [ "$listed" -eq 0 ]; then
        undo+=(undo_extension)
    fi
}

drop_extension() {
    if panel_cli extension list 2> /dev/null | extension_listed; then
        panel_cli extension remove "$extension_id" > /dev/null
        return 0
    fi
    return 1
}

protect_env() {
    if [ "$(stat -c %U "$1")" = root ] && getent group docker > /dev/null; then
        chgrp docker "$1"
        chmod 640 "$1"
    else
        chmod 600 "$1"
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
    body="$(curl -fsS -m 3 -H @- "$1/v1/health" 2> /dev/null <<< "Authorization: Bearer $2")" || return 1
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

undo_systemd() {
    builder_compose down > /dev/null 2>&1
    rm -rf "${builder_home:?}"
    delete_value "$env_file" "${setting_prefix}RunnerUrl"
    delete_value "$env_file" "${setting_prefix}RunnerIngressUrl"
    delete_value "$env_file" "${setting_prefix}RunnerToken"
}

install_systemd() {
    local version="$1" token existing_token existing_version
    existing_token="$(read_value "$builder_home/.env" CTXB_RUNNER_TOKEN)"
    existing_version="$(read_value "$builder_home/.env" CTXB_VERSION)"
    token="${existing_token:-$(new_token)}"

    if [ ! -d "$builder_home" ] && ! docker inspect "$runner_container" > /dev/null 2>&1; then
        undo+=(undo_systemd)
    fi
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

undo_docker() {
    if [ -f "$work/env.backup" ]; then
        cat "$work/env.backup" > "$stack_dir/.env"
        chmod "$(cat "$work/env.mode")" "$stack_dir/.env"
    else
        rm -f "${stack_dir:?}/.env"
    fi
    if [ "$fresh" -eq 1 ]; then
        rm -f "${stack_dir:?}/$compose_asset" "${stack_dir:?}/$overlay_asset"
        docker rm -f ctxb-runner ctxb-egress > /dev/null 2>&1
        stack_compose 0 up -d --no-deps ctx-ui > /dev/null 2>&1
    fi
}

install_docker() {
    local version="$1" token existing_token existing_version container
    existing_token="$(read_value "$stack_dir/.env" CTXB_RUNNER_TOKEN)"
    existing_version="$(read_value "$stack_dir/.env" CTXB_VERSION)"
    token="${existing_token:-$(new_token)}"

    fresh=0
    if [ ! -e "$stack_dir/$compose_asset" ] && ! docker inspect "$runner_container" > /dev/null 2>&1; then
        fresh=1
    fi
    if [ -f "$stack_dir/.env" ]; then
        cat "$stack_dir/.env" > "$work/env.backup"
        stat -c %a "$stack_dir/.env" > "$work/env.mode"
    fi
    undo=(undo_docker "${undo[@]}")

    install -m 644 "$work/$compose_asset" "$stack_dir/$compose_asset"
    install -m 644 "$work/$overlay_asset" "$stack_dir/$overlay_asset"
    (
        umask 077
        touch "$stack_dir/.env"
    )
    protect_env "$stack_dir/.env"
    write_value "$stack_dir/.env" CTXB_VERSION "$version"
    write_value "$stack_dir/.env" CTXB_RUNNER_TOKEN "$token"
    stack_compose 1 up -d > /dev/null
    wait_for_runner "http://$docker_runner_address:$control_port" "$token" "$version"
    write_value "$stack_dir/.env" COMPOSE_FILE "$(compose_file_value 1)"
    if [ "$stack_project" != "$default_project" ] && [ -z "$(read_value "$stack_dir/.env" COMPOSE_PROJECT_NAME)" ]; then
        write_value "$stack_dir/.env" COMPOSE_PROJECT_NAME "$stack_project"
    fi

    container="$(stack_container 1)"
    wait_for 60 stack_healthy "$container" || fail "ctx-ui did not become healthy within 60 seconds"
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
    local container remaining running=0
    if [ -n "$(stack_container 0)" ]; then
        running=1
        drop_extension || true
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
    if [ "$running" -eq 0 ] && drop_extension; then
        docker restart "$container" > /dev/null
        wait_for 60 stack_healthy "$container" || fail "ctx-ui did not become healthy within 60 seconds"
    fi
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
    check_docker "$((1 - remove))"
    detect_install "$remove"

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
        fail "ctx-ui is not installed here: no $unit and no running compose project with a ctx-ui service. Install ctx-ui first, see docs/en/install.md"
    fi
    if [ -z "$version" ]; then
        fail "no version given: add --version or take builder-install.sh from the release page"
    fi
    if ! [[ "$version" =~ $semver ]]; then
        fail "'$version' is not a version like 1.2.3 or 1.2.3-rc.1"
    fi

    work="$(mktemp -d)"
    trap cleanup EXIT
    download "$version"
    if [ "$mode" = systemd ]; then
        check_ports
    fi
    check_subnet
    pull_images "$version"
    install_extension "$version"

    case "$mode" in
        systemd) install_systemd "$version" ;;
        docker) install_docker "$version" ;;
    esac
}

main "$@"
