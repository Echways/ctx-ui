# Installation

**English** · [Русский](ru/install.md)

There are two ways: a binary with systemd and Docker with Caddy. Both install
the same gateway and are managed with the same `ctx-ui` command.

Linux on x86_64 or aarch64 is required. There are no builds for Windows,
macOS or Alpine.

## Binary and systemd

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

The installer asks for two addresses:

- **the external address**: the one clients see the gateway at, for example
  `https://mcp.example.com`. The addresses printed by `ctx-ui client add`
  start with it;
- **the listen address**: where the service itself listens. The default is
  `http://127.0.0.1:8080`, that is, only for a reverse proxy on the same
  machine.

Without a terminal no questions are asked; the values come from options or
variables:

| Option | Variable | Default |
| --- | --- | --- |
| `--public-url` | `CTX_UI_PUBLIC_URL` | `http://localhost:8080` |
| `--listen` | `CTX_UI_LISTEN` | `http://127.0.0.1:8080` |
| `--version` | `CTX_UI_VERSION` | the version of the release the installer came from |
| `--archive` | none | the archive is downloaded from the release page |
| none | `CTX_UI_DOWNLOAD_URL` | the GitHub release page; the address of a mirror holding the archive and `SHA256SUMS` |

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --public-url https://mcp.example.com
```

The downloaded archive is checked against `SHA256SUMS` of the same release.
On a mismatch the installation stops and the system is left unchanged.

### What goes where

| Path | What is there | On upgrade |
| --- | --- | --- |
| `/opt/ctx-ui/` | the program | replaced entirely |
| `/etc/ctx-ui/ctx-ui.env` | settings | left alone |
| `/var/lib/ctx-ui/` | the database and the encryption keys | left alone |
| `/usr/local/bin/ctx-ui` | the management command | replaced |
| `/etc/systemd/system/ctx-ui.service` | the service | replaced |

The service runs as the `ctx-ui` system user. The `ctx-ui` command is run
with `sudo` and switches to that user by itself.

### TLS

The service does not terminate TLS. A reverse proxy goes in front of it.

Caddy:

```
mcp.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
server {
    listen 443 ssl;
    server_name mcp.example.com;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_buffering off;
        proxy_read_timeout 1h;
        access_log off;
    }
}
```

The `$connection_upgrade` variable is defined once in the `http` block:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}
```

`proxy_buffering off` is needed for streaming MCP responses. `Upgrade` and
`Connection` are needed by the panel: it holds a WebSocket.
`X-Forwarded-Proto` tells the service that the outside is HTTPS: without it
the panel login cookie does not get the `Secure` flag. `access_log off` is
needed because an address like `/k/<token>/mcp/<slug>` contains the token and
the panel address contains its secret path: the access log would record them
in full. Caddy does all of this by itself.

### Panel

```bash
sudo ctx-ui admin add admin
```

The command prints the login, the password and the panel address. The
password is shown once; a forgotten one is replaced with
`sudo ctx-ui admin password admin`. `sudo ctx-ui admin path` shows the panel
address again.

### Upgrade

The same installer from the new release. Settings and data stay, no
questions are asked.

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

A specific version:

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/download/v0.1.0/install.sh | sudo bash
```

### Removal

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --uninstall
```

The program, the service and the command are removed; settings and data
stay. `--uninstall --purge` removes them too, along with the `ctx-ui` user.

## Docker and Caddy

You need Docker with Compose and a domain pointing at the server: Caddy gets
a certificate for it by itself, so ports 80 and 443 must be open.

Take the version from the releases page and put it in the first line.

```bash
VERSION=0.1.0
curl -fsSL https://github.com/Echways/mcp-ui/releases/download/v$VERSION/ctx-ui-$VERSION-docker.tar.gz | tar -xz
cd ctx-ui
cp .env.example .env
```

Write the domain into `.env`:

```
CTX_UI_VERSION=0.1.0
CTX_UI_DOMAIN=mcp.example.com
```

```bash
docker compose up -d
docker compose exec ctx-ui ctx-ui admin add admin
```

The command prints the panel address and the password; inbounds and clients
are created in the panel or with the same commands as in the binary
installation:

```bash
docker compose exec ctx-ui ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
docker compose exec ctx-ui ctx-ui client add "Ivanov" --inbound learn
```

The container does not run as root, the gateway port is not published, and
only Caddy faces the outside. The database and the keys live in the
`ctx-ui-data` volume.

### Upgrade

Change `CTX_UI_VERSION` in `.env`, then:

```bash
docker compose pull
docker compose up -d
```

### First release

The image lives at `ghcr.io/echways/ctx-ui`. If `docker compose pull`
answers `denied`, the package has not been made public yet, see
[releasing.md](releasing.md).

## Backup

Copy the data and the settings together:

| Method | Data | Settings |
| --- | --- | --- |
| binary | `/var/lib/ctx-ui/` | `/etc/ctx-ui/ctx-ui.env` |
| Docker | the `ctx-ui-data` volume | `.env` |

The `keys` directory inside the data matters as much as the database:
without it issued tokens keep working but cannot be shown, only reissued,
and inbounds with secret headers are disabled until they are added again.
Open panel sessions are closed without it too: everyone has to sign in
again.

Better stop the service before copying: an SQLite database consists of
several files, and a copy taken while it runs may come out inconsistent.
