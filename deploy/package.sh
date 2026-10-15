#!/usr/bin/env bash
set -euo pipefail

if [ $# -ne 2 ]; then
    echo "usage: package.sh <version> <output-dir>" >&2
    exit 1
fi

version="$1"
semver='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'

if ! [[ "$version" =~ $semver ]]; then
    echo "package.sh: '$version' is not a version like 1.2.3 or 1.2.3-rc.1" >&2
    exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$2"
out="$(cd "$2" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

pack() {
    chmod -R u=rwX,go=rX "$1"
    tar -C "$1" --owner=0 --group=0 --numeric-owner -czf "$out/$2" ctx-ui
}

for arch in x64 arm64; do
    stage="$work/$arch/ctx-ui"
    dotnet publish "$root/src/CtxUi.Server" -c Release -r "linux-$arch" --self-contained \
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
        -p:Version="$version" -o "$stage"
    cp "$root/LICENSE" "$root/deploy/linux/ctx-ui.sh" "$root/deploy/linux/ctx-ui.service" "$stage/"
    echo "$version" > "$stage/VERSION"
    chmod 755 "$stage/ctx-ui" "$stage/ctx-ui.sh"
    pack "$work/$arch" "ctx-ui-$version-linux-$arch.tar.gz"
done

stage="$work/docker/ctx-ui"
mkdir -p "$stage"
cp "$root/deploy/docker/compose.yaml" "$root/deploy/docker/Caddyfile" "$stage/"
sed "s/^CTX_UI_VERSION=.*/CTX_UI_VERSION=$version/" "$root/deploy/docker/.env.example" > "$stage/.env.example"
pack "$work/docker" "ctx-ui-$version-docker.tar.gz"

sed "s/^default_version=.*/default_version=\"$version\"/" "$root/deploy/linux/install.sh" > "$out/install.sh"
chmod 755 "$out/install.sh"

cd "$out"
sha256sum \
    "ctx-ui-$version-linux-x64.tar.gz" \
    "ctx-ui-$version-linux-arm64.tar.gz" \
    "ctx-ui-$version-docker.tar.gz" \
    install.sh > SHA256SUMS
