# Configuration

**English** · [Русский](ru/configuration.md)

Settings come from environment variables. Names are the same for every
install method.

| Install method | File | Apply |
| --- | --- | --- |
| binary | `/etc/ctx-ui/ctx-ui.env` | `sudo systemctl restart ctx-ui` |
| Docker | `.env` next to `compose.yaml` | `docker compose up -d` |
| source | `src/CtxUi.Server/appsettings.json` or environment | restart |

## Variables

| Variable | `appsettings.json` key | Default | Meaning |
| --- | --- | --- | --- |
| `Urls` | `Urls` | `http://127.0.0.1:8080` | listen address |
| `CtxUi__PublicUrl` | `CtxUi:PublicUrl` | `http://localhost:8080` | external address; prefix of client and panel addresses |
| `CtxUi__DataDir` | `CtxUi:DataDir` | `data` | database and keys directory |
| `CtxUi__RefreshInterval` | `CtxUi:RefreshInterval` | `00:00:01` | how often the server checks for CLI changes |
| `CtxUi__FlushInterval` | `CtxUi:FlushInterval` | `00:00:05` | how often quota counters and the call log are written |
| `CtxUi__UsageRetentionDays` | `CtxUi:UsageRetentionDays` | `30` | call log retention; daily totals are kept forever |
| `CtxUi__ConcurrencyWait` | `CtxUi:ConcurrencyWait` | `00:00:01` | how long a request over the concurrency limit waits for a slot before 429 |
| `CtxUi__SessionCheckInterval` | `CtxUi:SessionCheckInterval` | `00:01:00` | how often an open panel rechecks the administrator's session |

Use `Urls`, not `ASPNETCORE_URLS`: the latter is overridden by
`appsettings.json`.

The panel path is stored in the database, not in settings:
`ctx-ui admin path [--new]`.

## Docker

`.env`:

| Variable | Meaning |
| --- | --- |
| `CTX_UI_VERSION` | image tag |
| `CTX_UI_DOMAIN` | domain; sets `CtxUi__PublicUrl` and the Caddy site |

The image sets `Urls=http://0.0.0.0:8080` and `CtxUi__DataDir=/data`.

## Data directory

| Path | Contents |
| --- | --- |
| `ctx-ui.db` | SQLite database |
| `keys/` | Data Protection keys |

Losing `keys/`: tokens still work but cannot be shown, only reissued;
inbounds with secret headers are disabled until the headers are re-entered;
panel sessions are invalidated.
