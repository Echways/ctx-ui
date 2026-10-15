# ctx-ui

**English** · [Русский](docs/ru/README.md)

A panel and a gateway for sharing access to your own MCP servers, in the
spirit of 3x-ui. The administrator creates an inbound (an address and what is
served behind it), creates a client with an expiry date and hands them a
token.

Stages 1–3 are done: inbounds for remote MCP servers, limits and call
accounting, a browser panel and command-line management.

## Installation

A binary with a systemd service, Linux x86_64 or aarch64:

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

Docker with Caddy and an automatic certificate, upgrading, removal and
backups are described in [docs/install.md](docs/install.md).

The service does not terminate TLS itself: put Caddy or nginx in front of it.

## Panel

The panel lives at a secret address. The address and the password are
printed by the command:

```bash
sudo ctx-ui admin add admin
```

```text
Администратор «admin» добавлен
Логин:   admin
Пароль:  Wq7c…
Панель:  https://mcp.example.com/k3v9x0q2m7c1b8zd/
```

The program speaks Russian: `Логин` is the login, `Пароль` the password and
`Панель` the panel address.

The panel has four sections: «Обзор» (overview: clients, calls and failures
over the last 24 hours), «Inbounds», «Клиенты» (clients) and «Журнал» (call
log). The gateway applies changes at once, without a restart. After a client
is added, the «Подключение» (connection) window opens: the token, the
addresses and a ready-made message for the person who was given access.

| Command | What it does |
| --- | --- |
| `admin add <login>` | adds an administrator, prints the password and the panel address |
| `admin password <login>` | issues a new password, closes open sessions and lifts the login lockout |
| `admin list`, `admin remove <login>` | list and removal |
| `admin path` | shows the panel address; `--new` replaces it, the new one works after the service restarts |

The password is shown once and stored only as a hash. Five wrong passwords
in a row lock that login out for 15 minutes. At any address other than the
secret one the panel answers 404.

## Command-line management

Everything the panel does can also be done with a command. A running
service picks such changes up within a second, no restart needed.

```bash
sudo ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
sudo ctx-ui client add "Ivanov 4419" --inbound learn --expires 2027-07-01
sudo ctx-ui client list
```

In Docker the same commands go through `docker compose exec ctx-ui ctx-ui …`.

| Command | What it does |
| --- | --- |
| `inbound add <slug> --url <address>` | an inbound for a remote MCP server; `--header Name=value` adds a header for it, `--forward-identity` passes the client identity on |
| `inbound list`, `enable`, `disable`, `remove <slug>` | list and state |
| `client add <name> --inbound <slug>` | issues a client, prints the token and the addresses; `--expires YYYY-MM-DD`, `--meta key=value`, limits `--rate`, `--concurrency`, `--quota`, `--reset-days` |
| `client show <name>` | token, addresses, limits and quota usage |
| `client set <name>` | changes the expiry (`--expires <date>` or `never`), the set of inbounds and the limits; `none` removes a limit |
| `client reset <name>` | zeroes the quota counter and starts the period over |
| `client reissue <name>` | a new token, the old one stops working |
| `client list`, `enable`, `disable`, `remove <name>` | list and state |
| `usage days`, `usage log` | daily totals and recent calls; `--client <name>` keeps a single client |

## Connecting a client

The token goes in the `Authorization: Bearer <token>` header or, if the
client cannot set headers, right in the address:
`/k/<token>/mcp/<slug>`. An address with a token is as good as a password.

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn \
    --header "Authorization: Bearer tnk_..."
```

## Limits

A client has four optional limits. A limit that is not set does not apply.

| Option | What it limits | What the client gets when it is exceeded |
| --- | --- | --- |
| `--rate N` | requests per minute, of any kind | HTTP 429 with a `Retry-After` header |
| `--concurrency N` | simultaneous `POST` requests | HTTP 429 with `Retry-After: 1` |
| `--quota N` | `tools/call` calls per period | a tool result with `isError: true` and the time the limit resets |
| `--reset-days N` | the length of the quota period; without it the quota is granted once | |

Only `tools/call` uses up the quota. A client with an exhausted quota still
connects and sees the list of tools, and the refusal arrives as an ordinary
tool result, so the model relays it to the person. A call that the upstream
answered with an HTTP error or did not answer at all does not count.

```bash
sudo ctx-ui client set "Ivanov 4419" --quota 500 --reset-days 30 --rate 60
sudo ctx-ui client set "Ivanov 4419" --rate none
sudo ctx-ui client reset "Ivanov 4419"
sudo ctx-ui usage days --client "Ivanov 4419"
sudo ctx-ui usage log --limit 20
```

The service writes counters and the call log to the database every few
seconds, so the commands show them with a slight delay.

## What the upstream receives

The gateway removes the client token and adds the headers configured for
the inbound. With `--forward-identity` it adds `X-Mcp-Client-Id`,
`X-Mcp-Client-Name` and `X-Mcp-Meta-<key>`; the name and the values are
percent-encoded UTF-8. The gateway strips the same headers when the client
sends them itself.

## Documentation

- [Installation](docs/install.md)
- [Configuration](docs/configuration.md)
- [How it works](docs/internals.md)
- [Releasing](docs/releasing.md)

## Development

Requires the .NET 10 SDK.

```bash
cd src/CtxUi.Server
dotnet run
dotnet run -- inbound list
```

The server listens on `http://127.0.0.1:8080` and keeps the database and the
keys in the `data` directory next to the project.

```bash
dotnet test
```

Integration tests start the gateway and a fake upstream on free ports. Panel
tests open it in headless Chromium; on the first run it is downloaded to
`~/.cache/ms-playwright`, and only that needs the network. Installation
checks are described in [docs/releasing.md](docs/releasing.md).

## License

GPL-3.0.
