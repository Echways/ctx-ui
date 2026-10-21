# ctx-ui

**English** · [Русский](docs/ru/README.md)

A gateway and web panel for sharing access to MCP servers, modelled on 3x-ui.
An inbound is an address (`/mcp/<slug>`) backed by a remote MCP server. A
client is a token with an expiry date, a set of inbounds and limits.

Supports streamable HTTP inbounds, per-client limits and quotas, a call
log, a web panel, a CLI and a REST API with Swagger UI.

## Install

Binary + systemd, Linux x86_64/aarch64:

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh | sudo bash
```

Update to the latest release: `sudo ctx-ui update`.

Docker + Caddy, removal, backup: [docs/install.md](docs/install.md).
The service does not terminate TLS; put Caddy or nginx in front.

## Quick start

```bash
sudo ctx-ui admin add admin
sudo ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
sudo ctx-ui client add "Ivanov 4419" --inbound learn --expires 2027-07-01
```

`admin add` prints the login, a one-time password and the secret panel
address. `client add` prints the token and the connection addresses. In
Docker prefix commands with `docker compose exec ctx-ui`.

Program output, gateway errors and the API are in English. Set
`CtxUi__Language=ru` for Russian; see
[configuration.md](docs/configuration.md#language).

## Connecting a client

| Method | Address |
| --- | --- |
| header `Authorization: Bearer <token>` | `https://<host>/mcp/<slug>` |
| token in the path, for clients without custom headers | `https://<host>/k/<token>/mcp/<slug>` |

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn \
    --header "Authorization: Bearer tnk_..."
```

A path with a token is a credential: treat it as a password.

## Panel

Served only under a random 16-character path; any other path gets 404.
It speaks the `CtxUi__Language` language until an administrator picks one; the
button in the top bar (and the link under the sign-in form) switches between
English and Russian, the choice is kept in a cookie for a year.

| Section | Contents |
| --- | --- |
| Overview | active clients, calls and upstream failures over 24 h; per-inbound and daily totals |
| Inbounds | create, edit, enable/disable, delete |
| Clients | create, edit, enable/disable, search, connection window, new token, quota reset |
| Log | recent calls, filter by client |
| API keys | issue, revoke, link to Swagger UI |

Changes apply to the gateway immediately. The connection window shows the
token, addresses and a ready-to-send message for the client.

Request timeout (default 300 s, 1–3600) and request body limit (default
4 MB, 1 KB–64 MB) of an inbound are set only in the panel.

Sign-in: the password is generated (20 characters), shown once, stored as a
hash. 5 failed attempts lock the login for 15 minutes. The session cookie
lives 7 days with sliding expiration.

## CLI

Changes made by the CLI reach a running service within `RefreshInterval`
(1 s). No restart needed.

### admin

| Command | Effect |
| --- | --- |
| `admin add <login>` | create an administrator; prints password and panel address |
| `admin password <login>` | new password; closes sessions, lifts lockout |
| `admin list` | list with lockout state |
| `admin remove <login>` | delete |
| `admin path [--new]` | show panel address; `--new` generates a new one, active after restart |

Login: 1–64 characters, `A-Z a-z 0-9 . _ -`.

### inbound

| Command | Effect |
| --- | --- |
| `inbound add <slug> --url <url>` | add an inbound for a streamable HTTP MCP server |
| `inbound list` | slug, state, URL |
| `inbound enable\|disable <slug>` | toggle without deleting |
| `inbound remove <slug>` | delete, revoking it from all clients |

`add` options: `--name <text>` display name (default: slug);
`--header Name=value` upstream header, repeatable, stored encrypted;
`--forward-identity` send client identity headers upstream.

Slug: 1–64 characters, `a-z 0-9 -`, no leading or trailing hyphen.

### client

| Command | Effect |
| --- | --- |
| `client add <name> --inbound <slug>` | issue a client; prints token and addresses |
| `client show <name>` | token, addresses, limits, quota usage |
| `client set <name>` | change expiry, inbounds, limits |
| `client reset <name>` | zero the quota counter, restart the period |
| `client reissue <name>` | new token; the old one stops working |
| `client list` | name, state, expiry, last use, quota, inbounds |
| `client enable\|disable <name>` | toggle without deleting |
| `client remove <name>` | delete |

Options for `add` and `set`:

| Option | Meaning |
| --- | --- |
| `--inbound <slug>` | repeatable; in `set` replaces the whole set |
| `--expires YYYY-MM-DD` | access closes at 00:00 UTC of that day; `never` in `set` removes expiry |
| `--meta key=value` | `add` only; repeatable; sent as `X-Mcp-Meta-<key>` |
| `--rate N` | requests per minute |
| `--concurrency N` | simultaneous `POST` requests |
| `--quota N` | `tools/call` per period |
| `--reset-days N` | quota period length; without it the quota is one-off |

In `set`, `none` removes a limit. Omitted options keep their values.

Name: 1–100 characters, unique ignoring case, no control characters. Quote
names with spaces.

### usage

| Command | Effect |
| --- | --- |
| `usage days [--days N]` | daily totals, last N days (default 7, max 3650) |
| `usage log [--limit N]` | recent calls, newest first (default 50, max 1000) |

Both accept `--client <name>`. Times are UTC. Data reaches the database every
`FlushInterval` (5 s), so it lags slightly.

### apikey

| Command | Effect |
| --- | --- |
| `apikey add <name>` | issue a REST API key; prints it once with the API and Swagger addresses |
| `apikey list` | name, key prefix, issue date |
| `apikey remove <name>` | revoke; requests with the key get 401 at once |

Name: 1–64 characters, `A-Z a-z 0-9 . _ -`.

## REST API

Lives under the panel's secret path, like the panel itself:

| Address | What |
| --- | --- |
| `https://<host>/<secret>/api/v1/…` | API, key in `Authorization: Bearer ctxk_…` |
| `https://<host>/<secret>/swagger/` | Swagger UI, "Authorize" takes the key |
| `https://<host>/<secret>/openapi/v1.json` | OpenAPI 3.1 document |

| Resource | Methods |
| --- | --- |
| `inbounds`, `inbounds/{slug}` | list, get, `PUT` creates or replaces, `enable`, `disable`, `DELETE` |
| `clients`, `clients/{id}` | list (`?name=` finds one), get, `POST` issues, `PATCH` changes, `connection`, `enable`, `disable`, `reset-quota`, `reissue`, `DELETE` |
| `usage/days`, `usage/log`, `overview` | daily totals, recent calls, the panel's overview |

Errors are `application/problem+json`: 400 wrong input, 401 no or unknown
key, 404 no such object, 409 the token cannot be shown (`keys/` lost). The
`detail` text is the same as the CLI prints. Changes reach the gateway at
once.

```bash
curl -H "Authorization: Bearer ctxk_..." https://mcp.example.com/<secret>/api/v1/clients
```

A panel session does not open the API: browsers send the cookie with
requests from any site.

## Limits

Each limit is optional; unset means unlimited.

| Limit | Counts | When exceeded |
| --- | --- | --- |
| `--rate` | all requests, token bucket | HTTP 429, `Retry-After: <seconds until next>` |
| `--concurrency` | in-flight `POST` | HTTP 429 after waiting up to 1 s for a slot, `Retry-After: 1` |
| `--quota` | `tools/call` per period | HTTP 200, tool result with `isError: true` and reset time |

The quota is spent only by `tools/call`. With an exhausted quota the client
still connects and lists tools; the refusal is a tool result, so the model
relays it to the user. Calls the upstream answered with HTTP ≥ 400 or did not
answer are refunded.

```bash
sudo ctx-ui client set "Ivanov 4419" --quota 500 --reset-days 30 --rate 60
sudo ctx-ui client set "Ivanov 4419" --rate none
```

## Gateway responses

| Status | `error` | Cause |
| --- | --- | --- |
| 401 | `token_missing` | no token |
| 401 | `token_unknown` | token not found |
| 401 | `client_disabled` | client disabled |
| 401 | `client_expired` | expiry passed |
| 404 | `inbound_not_found` | no such inbound, disabled, or not granted |
| 429 | `rate_limited`, `too_many_concurrent` | limit exceeded |
| 413 | `body_too_large` | body over the inbound's limit |
| 400 | JSON-RPC `-32600` | body is not valid JSON-RPC or batch > 100 |

## What the upstream receives

The client's request with its token removed, plus the inbound's headers.
With `--forward-identity`: `X-Mcp-Client-Id`, `X-Mcp-Client-Name`,
`X-Mcp-Meta-<key>`, values percent-encoded UTF-8. Client-sent headers with
these names are stripped.

## Documentation

- [Installation](docs/install.md)
- [Configuration](docs/configuration.md)

## License

GPL-3.0.
