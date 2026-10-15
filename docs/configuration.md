# Configuration

**English** · [Русский](ru/configuration.md)

Settings are read from environment variables. The names are the same for
every installation method; only the place where they are written differs.

| Installation method | Where they live | How to apply |
| --- | --- | --- |
| binary | `/etc/ctx-ui/ctx-ui.env` | `sudo systemctl restart ctx-ui` |
| Docker | `.env` next to `compose.yaml` | `docker compose up -d` |
| from source | `src/CtxUi.Server/appsettings.json` or the environment | restart |

## Variables

| Variable | Key in `appsettings.json` | Default | Meaning |
| --- | --- | --- | --- |
| `Urls` | `Urls` | `http://127.0.0.1:8080` | the address the service listens on |
| `CtxUi__PublicUrl` | `CtxUi:PublicUrl` | `http://localhost:8080` | the external address; client addresses start with it |
| `CtxUi__DataDir` | `CtxUi:DataDir` | `data` | the directory with the database and the keys |
| `CtxUi__RefreshInterval` | `CtxUi:RefreshInterval` | `00:00:01` | how often the service checks for changes made with the `ctx-ui` command |
| `CtxUi__FlushInterval` | `CtxUi:FlushInterval` | `00:00:05` | how often the service writes quota counters and the call log to the database |
| `CtxUi__UsageRetentionDays` | `CtxUi:UsageRetentionDays` | `30` | how many days call log rows are kept; daily totals are kept forever |
| `CtxUi__SessionCheckInterval` | `CtxUi:SessionCheckInterval` | `00:01:00` | how often an open panel checks that the administrator has not been removed or given a new password |

The listen address is set with the `Urls` variable specifically.
`ASPNETCORE_URLS` has no effect: the value from `appsettings.json`, which
sits next to the program, overrides it.

The panel address is not among the settings: its secret path is stored in
the database. `ctx-ui admin path` shows it, `ctx-ui admin path --new`
replaces it.

## In Docker

`.env` sets two variables; `compose.yaml` derives the rest.

| Variable | Meaning |
| --- | --- |
| `CTX_UI_VERSION` | the image tag |
| `CTX_UI_DOMAIN` | the domain; `CtxUi__PublicUrl` and the Caddy address are derived from it |

The image sets `Urls=http://0.0.0.0:8080` and `CtxUi__DataDir=/data`.

## Data directory

It holds `ctx-ui.db` and the `keys` directory. `keys` is needed to show
issued tokens and to read the secret headers of inbounds. Without it tokens
keep working but cannot be shown, only reissued, and inbounds with secret
headers are disabled until they are added again.
