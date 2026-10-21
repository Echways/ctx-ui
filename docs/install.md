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

### Stop and start

```bash
sudo ctx-ui down     # stop the service and keep it off after reboot
sudo ctx-ui up       # start it and turn start at boot back on
```

`down` stops the whole process: the panel, the API and the gateway. Settings
and data stay; `update` starts the service again.

### Upgrade

```bash
sudo ctx-ui update           # latest release
sudo ctx-ui update 0.2.0     # specific version
ctx-ui version               # installed version
```

`update` downloads the release installer and runs it: the archive is checked
against `SHA256SUMS`, settings and data are kept, the service restarts. If
that version is already running, nothing changes. Rerunning the install
command does the same.

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

In the `ctx-ui` directory:

```bash
sed -i 's/^CTX_UI_VERSION=.*/CTX_UI_VERSION=0.2.0/' .env && docker compose up -d
```

Compose pulls the new image and recreates the container; data stays in the
volume.

## Backup

| Method | Data | Settings |
| --- | --- | --- |
| binary | `/var/lib/ctx-ui/` | `/etc/ctx-ui/ctx-ui.env` |
| Docker | volume `ctx-ui-data` | `.env` |

`ctx-ui backup <directory>` writes a consistent copy of the database and the
`keys/` directory while the service keeps running. The directory must be
missing or empty. Never copy the database file of a running service: SQLite
keeps recent changes in `ctx-ui.db-wal`, and a plain copy may be
inconsistent. Without `keys/` the stored tokens and upstream headers cannot be
decrypted; see [configuration.md](configuration.md#data-directory).

Binary (the command runs as the `ctx-ui` user, so it needs a directory that
user can write):

```bash
backup=/var/backups/ctx-ui-$(date +%F)
sudo install -d -o ctx-ui -g ctx-ui -m 700 "$backup"
sudo ctx-ui backup "$backup"
```

Docker, in the `ctx-ui` directory:

```bash
backup=backup-$(date +%F)
docker compose exec ctx-ui ctx-ui backup /data/$backup
docker compose cp ctx-ui:/data/$backup ./$backup
docker compose exec ctx-ui rm -r /data/$backup
```

Keep the settings file (`ctx-ui.env` or `.env`) next to the copy.

### Restore

Stop the service, replace everything in the data directory with the copy,
give it back to the service user, start the service. Remove the old
`ctx-ui.db-wal` and `ctx-ui.db-shm` along with the rest.

Binary:

```bash
sudo systemctl stop ctx-ui
sudo find /var/lib/ctx-ui -mindepth 1 -delete
sudo cp -r /var/backups/ctx-ui-2026-10-21/. /var/lib/ctx-ui/
sudo chown -R ctx-ui:ctx-ui /var/lib/ctx-ui
sudo systemctl start ctx-ui
```

Docker, in the `ctx-ui` directory:

```bash
docker compose stop ctx-ui
docker run --rm -v ctx-ui_ctx-ui-data:/data -v "$PWD/backup-2026-10-21:/restore:ro" alpine \
    sh -c 'find /data -mindepth 1 -delete && cp -r /restore/. /data/ && chown -R 1654:1654 /data'
docker compose start ctx-ui
```
