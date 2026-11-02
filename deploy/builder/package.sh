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

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mkdir -p "$2"
out="$(cd "$2" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet publish "$root/extensions/builder/CtxUi.Builder" -c Release -p:Version="$version" \
    -p:IncludeSourceRevisionInIdentifier=false -p:ContinuousIntegrationBuild=true -p:PublishDir="$work/publish/"
cp "$work/builder-$version.zip" "$out/"
cp "$root/deploy/builder/compose.yaml" "$out/builder-compose.yaml"
cp "$root/deploy/builder/compose.docker.yaml" "$out/builder-compose.docker.yaml"
sed "s/^default_version=.*/default_version=\"$version\"/" "$root/deploy/builder/builder-install.sh" > "$out/builder-install.sh"
chmod 755 "$out/builder-install.sh"

cd "$out"
sha256sum \
    "builder-$version.zip" \
    builder-compose.yaml \
    builder-compose.docker.yaml \
    builder-install.sh > SHA256SUMS
