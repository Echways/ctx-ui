# Installation

**English** · [Русский](ru/install.md)

Two methods: binary + systemd, or Docker + Caddy. Same program, same
`ctx-ui` command.

Platforms: Linux x86_64, aarch64. No builds for Windows, macOS, Alpine.

## Binary and systemd

Requires root, systemd, `curl`, `tar`, `sha256sum`, `runuser`, `useradd`.

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh | sudo bash
```

The installer asks for two addresses (on first install only):

- external address: what clients see, e.g. `https://mcp.example.com`;
  prefix of all client and panel addresses;
- listen address: default `http://127.0.0.1:8080`, reachable only by a local
  reverse proxy.

Without a terminal, values come from options or variables:

| Option | Variable | Default |
| --- | --- | --- |
| `--public-url` | `CTX_UI_PUBLIC_URL` | `http://localhost:8080` |
| `--listen` | `CTX_UI_LISTEN` | `http://127.0.0.1:8080` |
| `--version` | `CTX_UI_VERSION` | version of the release the installer came from |
| `--archive <file>` | — | download from the release |
| — | `CTX_UI_DOWNLOAD_URL` | release page; mirror holding the archive and `SHA256SUMS` |

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --public-url https://mcp.example.com
```

The archive is verified against the release's `SHA256SUMS`; on mismatch the
installer stops before changing anything.

If `/etc/ctx-ui/ctx-ui.env` exists, it is kept and `--public-url`/`--listen`
are ignored. Edit the file and restart the service instead.

### Layout

| Path | Contents | On upgrade |
| --- | --- | --- |
| `/opt/ctx-ui/` | program | replaced |
| `/etc/ctx-ui/ctx-ui.env` | settings | kept |
| `/var/lib/ctx-ui/` | database, keys | kept |
| `/usr/local/bin/ctx-ui` | CLI wrapper | replaced |
| `/etc/systemd/system/ctx-ui.service` | unit | replaced |

The service runs as system user `ctx-ui`. Run the CLI with `sudo`; the
wrapper loads `ctx-ui.env` and switches to `ctx-ui` itself.

### TLS

The service speaks plain HTTP. Put a reverse proxy in front.

Caddy:

```
mcp.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

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

The `map` block goes in `http`.

| Directive | Why |
| --- | --- |
| `proxy_buffering off` | streaming MCP responses (SSE) |
| `Upgrade`, `Connection` | panel WebSocket |
| `X-Forwarded-Proto` | `Secure` flag on the panel cookie, `https` redirects |
| `access_log off` | paths contain the token (`/k/<token>/…`) and the secret panel path |
| `proxy_read_timeout 1h` | long-lived MCP streams |

Caddy handles all of this by default.

### Panel

```bash
sudo ctx-ui admin add admin          # login, password, panel address
sudo ctx-ui admin password admin     # new password
sudo ctx-ui admin path               # panel address
```

### Upgrade

Rerun the installer. Settings and data are kept, no questions asked.

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh | sudo bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/download/v0.1.0/install.sh | sudo bash   # specific version
```

### Removal

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --uninstall
```

Removes the program, unit and CLI; keeps settings and data. Add `--purge` to
also delete `/etc/ctx-ui`, `/var/lib/ctx-ui` and the `ctx-ui` user.

## Docker and Caddy

Requires Docker Compose, a domain pointing at the server, open ports 80 and
443. Caddy obtains the certificate.

```bash
VERSION=0.1.0
curl -fsSL https://github.com/Echways/ctx-ui/releases/download/v$VERSION/ctx-ui-$VERSION-docker.tar.gz | tar -xz
cd ctx-ui
cp .env.example .env
```

`.env`:

```
CTX_UI_VERSION=0.1.0
CTX_UI_DOMAIN=mcp.example.com
```

```bash
docker compose up -d
docker compose exec ctx-ui ctx-ui admin add admin
docker compose exec ctx-ui ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
docker compose exec ctx-ui ctx-ui client add "Ivanov" --inbound learn
```

The container runs as non-root with all capabilities dropped; port 8080 is
not published, only Caddy is exposed. Data lives in the `ctx-ui-data`
volume.

### Upgrade

Set the new `CTX_UI_VERSION` in `.env`:

```bash
docker compose pull
docker compose up -d
```

`denied` on pull: the ghcr.io package is still private, see
[releasing.md](releasing.md#after-the-first-release).

## Backup

| Method | Data | Settings |
| --- | --- | --- |
| binary | `/var/lib/ctx-ui/` | `/etc/ctx-ui/ctx-ui.env` |
| Docker | volume `ctx-ui-data` | `.env` |

Back up `keys/` together with the database; see
[configuration.md](configuration.md#data-directory) for what is lost
without it. Stop the service first: SQLite keeps several files, and a live
copy may be inconsistent.
