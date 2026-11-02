# Server builder

The builder is a panel extension. It builds MCP servers on FastMCP in the panel,
runs each one in its own isolated Docker container and connects it as an HTTP
inbound, so it reaches clients like any other server. Tokens, limits, journal,
metrics and OAuth work for built servers without extra settings.

[English](builder.md) · [Русский](ru/builder.md)

The builder is not part of ctx-ui. Without it the panel shows no trace of it, and
the main image and the systemd install need neither Python nor Docker.

## Install

Requires Docker Engine 29 or newer on the host, an installed ctx-ui (systemd or
Docker) and root.

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/builder-install.sh | sudo bash
```

The installer detects how ctx-ui is installed, checks Docker and the release
`SHA256SUMS`, and stops before changing anything if something is wrong. Then it
generates the Runner token, starts the containers, writes the settings
`CtxUi__Extensions__builder__RunnerUrl`, `CtxUi__Extensions__builder__RunnerIngressUrl`
and `CtxUi__Extensions__builder__RunnerToken`, installs the extension and
restarts ctx-ui. The token and the address are not set by hand.

| Option | Meaning |
| --- | --- |
| `--version <v>` | install this release instead of the latest |
| `--remove` | remove the containers labelled `ctx-ui.builder` and the extension; ctx-ui keeps running |

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/builder-install.sh \
    | sudo bash -s -- --version 0.2.0
```

Run the command again to update: the token and the built servers are kept. The
section "Builder" appears in the panel navigation. If the Runner is not
reachable, the section shows the reason and this command.

The installer pulls three images for `amd64` and `arm64`:
`ghcr.io/echways/ctx-ui-builder-runner`, `ghcr.io/echways/ctx-ui-builder-runtime`
and `ghcr.io/echways/ctx-ui-builder-egress`. Rootless Docker and Podman are not
supported.

## First server in five minutes

1. Open "Builder" > "Servers" and press "New server". Enter a name and a slug,
   then "Create". The slug cannot be changed later. You can also start from an
   OpenAPI document ("OpenAPI document (optional)") or from a project archive.
2. Add a tool. Either:
   - press `+` next to "Form tools": choose the method, the path, write the
     description and the parameters, press "Save". Tools of this kind call an HTTP
     API; set the API address in "Settings" > "API" first;
   - press `+` next to "Code tools", enter a tool name and press "Add". The tool
     opens with a working template.
3. Press "Publish". The draft is saved automatically, and nothing published
   changes until you publish. The build runs in the background; the status in
   the header shows the progress.
4. Open "Check", choose the tool, fill the arguments and press "Call" to make a
   real MCP call.
5. After the first successful publication the connect sheet opens (later, press
   "Connect"). Choose the inbound slug, the clients that get access, and confirm.
   The panel shows the address of the inbound. Give it to a client the usual way.

"Build without activating" in the publish menu builds a version and lets you call
it in "Check" before it becomes active.

## What a server holds

A server is an ordinary FastMCP project:

```
fastmcp.json     dependencies, Python version, transport
main.py          the same for every server, not edited
builder.json     resources and prompts made in the form
openapi.json     tools made in the form or imported
mcp/*.py         your code: @tool, @resource, @prompt
```

- **Tool from a form** edits one operation of `openapi.json`: method, path,
  description, parameters, an optional response example. "Import OpenAPI" reads a
  JSON document from an address or from pasted text and lets you tick the
  operations to import; none is ticked by default. YAML and Swagger 2.0 are not
  accepted.
- **Tool in code** is a Python file in `mcp/`. "Turn into code" converts a form
  tool into a file once; there is no way back.
- **Resources and prompts** are made in forms. A `{name}` in a prompt template
  becomes a required argument.
- **Settings**: base address and authorization of the API (none, Bearer, header,
  Basic), dependencies (one Python requirement per line), secrets, network and
  limits, export.
- **Secrets** are write-only, the panel shows names. A server gets them as
  environment variables.
- **Versions** list every publication with its build log and let you roll back
  with "Roll back to this version". A failed publication marks the version
  failed and the active one keeps working. Removed tools are shown as a warning.
- **Logs** show the output of the active version and the last build.

Changing only the code does not install packages again: the environment is reused
until dependencies, the Python version or the FastMCP version change. The last 5
versions with their environments are kept.

## Network access

Isolation is the same for every server and cannot be turned off: the server runs
as a non-root user with all capabilities dropped and `no-new-privileges`, with a
read-only root file system, a `tmpfs` on `/tmp` and its own volume `/data`. It
cannot reach the data of ctx-ui, the Docker socket or other servers. Default
limits: 512 MB, 1 CPU, 256 processes; change them in "Settings" > "Network and
limits".

One switch per server, "Network":

| | Internet only (default) | Full network |
| --- | --- | --- |
| Container network | its own `internal` network, the only way out is the Squid proxy | ordinary bridge |
| Reachable | HTTP and HTTPS to public addresses | any address and port, including the local network and databases |

With "Internet only" the proxy checks the destination after name resolution and
refuses private, reserved and link-local ranges, which covers cloud metadata, and
refuses mail ports 25, 465 and 587. Other ports on public addresses are open. The
server gets `HTTP_PROXY` and `HTTPS_PROXY`; a client that ignores them has no
network. When the proxy refuses a request, "Check" and the logs say so and link to
the setting.

"Full network" asks for confirmation that lists what becomes reachable. Neighbour
servers and the data of ctx-ui stay unreachable in both modes.

### What isolation leaves open

- **The kernel is shared.** Containers do not protect against a kernel
  vulnerability. Setting `BUILDER_RUNTIME=runsc` on the Runner runs servers in
  gVisor when it is installed on the host. It is off by default.
- **A compromised Runner is root on the host** with rootful Docker. The service
  is small, does not run user code and takes no container parameters.
- **The administrator writes the code and sees its result.** The secrets of a
  server are available to its code, so also to the administrator. This is not a
  boundary between administrators: everyone who signs in to the panel sees and
  edits all servers.
- **Logs are not masked.** A secret that the code prints ends up in the logs of
  the panel.

## Manual install

Use it when you do not want the script. You need the `deploy/builder` folder of
the release, for example from the repository at the tag `v<version>`, and the
archive `builder-<version>.zip` from the release page.

systemd install:

```bash
export CTX_UI_VERSION=0.2.0
export CTXB_RUNNER_TOKEN=$(openssl rand -hex 32)
docker compose -f deploy/builder/compose.yaml up -d
```

This is the project `ctx-ui-builder`. The Runner listens on `127.0.0.1:8091`
(control) and `127.0.0.1:8090` (servers). Add to `/etc/ctx-ui/ctx-ui.env`:

```
CtxUi__Extensions__builder__RunnerUrl=http://127.0.0.1:8091
CtxUi__Extensions__builder__RunnerIngressUrl=http://127.0.0.1:8090
CtxUi__Extensions__builder__RunnerToken=<the same token>
```

```bash
sudo ctx-ui extension install builder-0.2.0.zip
sudo systemctl restart ctx-ui
```

Docker install, in the `ctx-ui` directory. The order of the files matters, the
overlay goes last; it keeps the project name `ctx-ui`, joins ctx-ui to the Runner
network and publishes no ports:

```bash
docker compose -f deploy/docker/compose.yaml -f deploy/builder/compose.yaml -f deploy/builder/compose.docker.yaml up -d
```

Put `CTXB_RUNNER_TOKEN` next to `CTX_UI_VERSION` in `.env`. The settings go to the
`environment` of the `ctx-ui` service:

```
CtxUi__Extensions__builder__RunnerUrl=http://10.212.0.2:8091
CtxUi__Extensions__builder__RunnerIngressUrl=http://10.212.0.2:8090
CtxUi__Extensions__builder__RunnerToken=<the same token>
```

Then run `ctx-ui extension install` inside the container, or mount the unpacked
extension (see [extensions](extensions.md#install-update-remove)), and restart it.

Other Runner settings, in the `environment` of the `runner` service:
`RUNNER_SUBNET_POOL` (subnets of server networks, default `10.213.0.0/16`) and
`BUILDER_RUNTIME`.

## Deleting and cleaning up

- Removing an inbound on the inbounds page does not delete the server. The page
  of the server then shows "Not connected" and you can connect it again.
- "Delete server" on a connected server asks for confirmation that shows the
  inbound and the number of its clients, and deletes the server, all its versions
  and the inbound with its links to clients. This cannot be undone.
- `docker volume prune -a` deletes the volumes of the builder, including the
  activation markers and the `/data` volumes of the servers. Do not run it on a
  host with builder servers.
- `docker system prune` removes a stopped container, including the active
  server's if it was stopped. Start it again from the panel ("Start again").
- After restarts a server can keep containers without an activation marker. They
  are not routed and stay until the next activation or deletion of the server, so
  such containers can accumulate.
- Removing the extension, also with `--purge`, does not stop the containers.
  Servers and inbounds keep working. `builder-install.sh --remove` removes the
  containers labelled `ctx-ui.builder` together with the extension.
- If the Runner is down, you can edit drafts but not publish, and calls of clients
  to built servers fail until it returns.
- `ctx-ui backup` covers the drafts, versions and secrets of the builder (the
  extension data), not the `/data` volumes of the servers.

## API for CI

The extension has endpoints under `/<panel>/extensions/builder/`. They need an API key
in `Authorization: Bearer ctxk_…`, as in the [REST API](../README.md#rest-api).

| Request | Result |
| --- | --- |
| `GET servers` | list: `id`, `slug`, `name`, `networkAccess`, `state`, `activeRelease`, `buildingRelease`, `inboundId` |
| `GET servers/{id}` | server with its limits, base address, secret names and versions |
| `POST servers/{id}/publish` | starts a publication, `202` with `{"release": n}` |
| `POST servers/{id}/rollback/{n}` | activates version `n`, `202` with `{"release": n}` |
| `GET servers/{id}/export` | the project as a `tar.gz` archive |

`publish` takes the optional query parameter `mode`: `Activate` (default) or
`BuildOnly`. The call returns at once; poll `GET servers/{id}` and read
`releases[].status`. Errors are `application/problem+json`: 404 no such server or
version, 409 a build is running, the server is being deleted or the version
cannot be restored, 503 the Runner settings are incomplete.

```bash
curl -X POST -H "Authorization: Bearer ctxk_..." \
    "https://mcp.example.com/<secret>/extensions/builder/servers/$ID/publish?mode=BuildOnly"
```

## Export and running outside the panel

"Export" > "Download archive" gives the project as `tar.gz`. It runs with plain
FastMCP, without the panel:

```bash
fastmcp run fastmcp.json
```

A project with `openapi.json` needs `BUILDER_BASE_URL`, and for authorization
`BUILDER_API_HEADERS`, a JSON object of headers:

```bash
BUILDER_BASE_URL=https://api.example.com \
BUILDER_API_HEADERS='{"Authorization": "Bearer ..."}' \
fastmcp run fastmcp.json
```

Secret values are never in the archive; set them as environment variables where
you run it. The same archive can be imported when creating a server.
