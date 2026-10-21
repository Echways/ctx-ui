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
| `CtxUi__Language` | `CtxUi:Language` | `en` | language of gateway errors, CLI, API and the panel until an administrator picks one: `en` or `ru` |
| `CtxUi__RefreshInterval` | `CtxUi:RefreshInterval` | `00:00:01` | how often the server checks for CLI changes |
| `CtxUi__FlushInterval` | `CtxUi:FlushInterval` | `00:00:05` | how often quota counters and the call log are written |
| `CtxUi__UsageRetentionDays` | `CtxUi:UsageRetentionDays` | `30` | call log retention; daily totals are kept forever |
| `CtxUi__ConcurrencyWait` | `CtxUi:ConcurrencyWait` | `00:00:01` | how long a request over the concurrency limit waits for a slot before 429 |
| `CtxUi__SessionCheckInterval` | `CtxUi:SessionCheckInterval` | `00:01:00` | how often an open panel rechecks the administrator's session |

Use `Urls`, not `ASPNETCORE_URLS`: the latter is overridden by
`appsettings.json`.

The panel path is stored in the database, not in settings:
`ctx-ui admin path [--new]`.

### Language

`CtxUi__Language` sets the language of gateway error messages, CLI output,
REST API errors and the panel. Each administrator can still switch the panel
language; the choice is kept in a cookie. Logs, the OpenAPI document and the
install scripts are always in English. Any value other than `en` or `ru` stops
the service at start with an error that names the allowed values.

Upgrading from an earlier release: gateway, CLI and API messages used to be in Russian and
are now in English by default. To keep Russian, add the line to
`/etc/ctx-ui/ctx-ui.env` and restart:

```
CtxUi__Language=ru
```

For Docker, add `CTX_UI_LANGUAGE=ru` to `.env` and run `docker compose up -d`.

## Docker

`.env`:

| Variable | Meaning |
| --- | --- |
| `CTX_UI_VERSION` | image tag |
| `CTX_UI_DOMAIN` | domain; sets `CtxUi__PublicUrl` and the Caddy site |
| `CTX_UI_LANGUAGE` | sets `CtxUi__Language`, `en` by default |

The image sets `Urls=http://0.0.0.0:8080` and `CtxUi__DataDir=/data`.

## Data directory

| Path | Contents |
| --- | --- |
| `ctx-ui.db` | SQLite database |
| `keys/` | Data Protection keys |

Losing `keys/`: tokens still work but cannot be shown, only reissued;
inbounds with secret headers are disabled until the headers are re-entered;
panel sessions are invalidated.
