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

The installer finds how ctx-ui is installed (systemd if the service
`ctx-ui.service` exists, otherwise exactly one running container of the image
`ghcr.io/echways/ctx-ui` from a Docker compose project with a `ctx-ui` service; with
several candidates it stops and lists them), checks that Docker Engine is 29 or
newer and verifies the downloaded assets against the release `SHA256SUMS`. Before it
starts a container it checks that the subnet `10.212.0.0/29` and, with systemd, the
ports 8090 and 8091 on 127.0.0.1 are free, pulls the images
`ghcr.io/echways/ctx-ui-builder-runner`, `-egress` and `-runtime` of the release and
installs the extension (ctx-ui refuses one that does not fit its extension API and
then nothing has changed). If one of these fails it stops with a message and the
Runner is not started. Then it generates the Runner token, starts the containers
`ctxb-runner` and `ctxb-egress`, waits until the Runner reports the version of the
release, writes the settings
`CtxUi__Extensions__builder__RunnerUrl`, `CtxUi__Extensions__builder__RunnerIngressUrl`
and `CtxUi__Extensions__builder__RunnerToken` and restarts ctx-ui. The token and the
address are not set by hand.

If a step fails after the Runner has started, a first install is rolled back: the
containers, the builder files, the settings, `.env` and the extension are removed
again. An update is not rolled back: the new extension and the builder compose files
stay and, with Docker, `.env` returns to the previous `CTXB_VERSION`. Run the
installer again then; a plain `docker compose up -d` before that would move the
Runner back to the old version, and the Builder section would report a version
mismatch.

- **systemd.** The compose project `ctx-ui-builder` lives in `/opt/ctx-ui-builder`
  (`builder-compose.yaml` and a private `.env` with the token). The Runner listens on
  `127.0.0.1:8091` (control) and `127.0.0.1:8090` (servers). The settings are added
  to `/etc/ctx-ui/ctx-ui.env`.
- **Docker.** The files `builder-compose.yaml` and `builder-compose.docker.yaml` are
  put next to the `compose.yaml` of ctx-ui and listed in `COMPOSE_FILE` in `.env`,
  together with `CTXB_VERSION` and `CTXB_RUNNER_TOKEN`, so a plain
  `docker compose up -d` keeps the builder; `.env` is made readable by its owner
  only, because it holds the token, and `COMPOSE_PROJECT_NAME` is added when the
  project is not named `ctx-ui`. The Runner is at `10.212.0.2` and
  ctx-ui at `10.212.0.3` on the network `ctxb-control` (8091 control, 8090 servers);
  the Runner publishes no host ports.
  The settings reach ctx-ui through the overlay file.

| Option | Meaning |
| --- | --- |
| `--version <v>` | install this release instead of the latest |
| `--remove` | remove the containers labelled `ctx-ui.builder`, the extension and the settings; ctx-ui keeps running |

The version is also read from `CTXB_VERSION`, and `CTX_UI_DOWNLOAD_URL` replaces the
release page as the source of the assets.

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/builder-install.sh \
    | sudo bash -s -- --version 0.2.0
```

Run the command again to update: the token and the built servers are kept. `--remove`
removes the Runner, the proxy and the containers of built servers. Networks and
volumes stay, including the `/data` volumes of servers, and so does the data of the
extension (`ctx-ui extension remove builder --purge` deletes it). The
section "Builder" appears in the panel navigation. If the Runner is not
reachable, the section shows the reason and this command.

After `--remove` the inbounds of built servers stay and point at the stopped Runner.
After a new install the servers show as active but have no containers until you
press "Start again" on each of them. If ctx-ui is stopped under Docker, `--remove`
works with the single stopped ctx-ui container and starts ctx-ui again; with several
it stops and lists them. `--remove` works on a host whose Docker Engine
has fallen below 29.

The installer pulls three images for the architecture of the host (`amd64` and
`arm64` are published): `ghcr.io/echways/ctx-ui-builder-runner`,
`ghcr.io/echways/ctx-ui-builder-runtime` and `ghcr.io/echways/ctx-ui-builder-egress`. Rootless Docker and Podman are not
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

Use it when you do not want the script. Download from the release page
`builder-<version>.zip` (the extension), `builder-compose.yaml` and
`builder-compose.docker.yaml`, and check them against `SHA256SUMS`.

systemd install:

```bash
export CTXB_VERSION=0.2.0
export CTXB_RUNNER_TOKEN=$(openssl rand -hex 32)
docker compose -f builder-compose.yaml up -d
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

Docker install: put `builder-compose.yaml` and `builder-compose.docker.yaml` next to
the `compose.yaml` of ctx-ui and add to `.env`:

```
CTXB_VERSION=0.2.0
CTXB_RUNNER_TOKEN=<64 hex digits>
COMPOSE_FILE=compose.yaml:builder-compose.yaml:builder-compose.docker.yaml
```

The order of the files matters, the overlay goes last; it keeps the project name
`ctx-ui`, joins ctx-ui to the Runner network, publishes no ports and gives ctx-ui the
settings with the addresses `http://10.212.0.2:8091` and `http://10.212.0.2:8090`.
Then:

```bash
docker compose up -d
docker compose cp builder-0.2.0.zip ctx-ui:/tmp/builder.zip
docker compose exec ctx-ui ctx-ui extension install /tmp/builder.zip
docker compose restart ctx-ui
```

Or mount the unpacked extension (see [extensions](extensions.md#install-update-remove)).

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
  containers labelled `ctx-ui.builder` together with the extension and its settings.
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
`releases[].status`. Errors are `application/problem+json`: 400 input that is
refused (a name, a project file, a document), 404 no such server or version,
409 a build is running, the server is being deleted or the version cannot be
restored, 502 the Runner is unreachable or answered with an error, 503 the Runner
settings are incomplete.

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
