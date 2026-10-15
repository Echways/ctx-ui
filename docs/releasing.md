# Releasing

**English** · [Русский](ru/releasing.md)

A release builds itself when a tag of the form `vX.Y.Z` reaches the
repository.

```bash
git switch main
git pull
git tag v0.1.0
git push origin v0.1.0
```

The `release` workflow runs the tests, builds the archives and the image and
creates a GitHub Release with notes from the commits. If the tests fail,
nothing is published.

## Version number

The tag sets the version: `v0.1.0` gives version `0.1.0`. It does not need
to be written anywhere else. A tag that does not look like `v1.2.3` or
`v1.2.3-rc.1` stops the workflow at the first step.

A tag with a suffix, such as `v0.2.0-rc.1`, gives a trial release: it is
marked as a pre-release, the image gets only the `0.2.0-rc.1` tag, and
`latest` and the `releases/latest` link keep pointing at the previous
regular release.

## What is published

| Artifact | What it is |
| --- | --- |
| `ctx-ui-X.Y.Z-linux-x64.tar.gz`, `ctx-ui-X.Y.Z-linux-arm64.tar.gz` | the program, the systemd unit, the management command |
| `install.sh` | the installer with the release version written into it |
| `ctx-ui-X.Y.Z-docker.tar.gz` | `compose.yaml`, `Caddyfile`, `.env.example` with the version |
| `SHA256SUMS` | checksums of the files above |
| `ghcr.io/echways/ctx-ui` | the image for amd64 and arm64 with the tags `X.Y.Z`, `X.Y`, `latest` |

## After the first release

The `ctx-ui` package on ghcr.io is created private. It has to be made public
once: the package page on GitHub, Package settings, Change visibility,
Public. Until then `docker compose pull` answers `denied` for other people.

On the same page check that the package is linked to the `mcp-ui`
repository: then later releases can write to it.

## Checks before tagging

The same checks that run in CI can be run locally. They need the .NET 10 SDK
and Docker.

```bash
dotnet test
deploy/tests/docker.sh
deploy/tests/linux.sh
```

`deploy/tests/linux.sh` starts a privileged Ubuntu container with systemd and
installs the program into it with the real installer.

To build all release files without publishing:

```bash
deploy/package.sh 0.1.0 dist
```

## If a release fails

A failed job is rerun from its run page in Actions. If the tag was put in the
wrong place, delete it together with the release and tag again:

```bash
gh release delete v0.1.0 --yes
git push origin :refs/tags/v0.1.0
git tag -d v0.1.0
```
