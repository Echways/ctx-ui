# CLI

[English](cli.md) · [Русский](../ru/cli.md) · [Documentation](README.md)

Run commands with `sudo`: the wrapper loads the settings and switches to the service user itself. Under Docker, prefix them with `docker compose exec ctx-ui`.

Changes made by the CLI reach a running service within `RefreshInterval` (1 second). No restart is needed.

| Group | Purpose |
| ----- | ------- |
| [`admin`](#admin) | Administrators of the panel |
| [`inbound`](#inbound) | Addresses backed by MCP servers |
| [`client`](#client) | Tokens, limits, devices, linked accounts |
| [`usage`](#usage) | Daily totals and the call log |
| [`oauth`](#oauth) | Browser sign-in and its providers |
| [`apikey`](#apikey) | REST API keys |
| [`backup`](#backup) | A consistent copy of the data |
| [`extension`](#extension) | Panel extensions |
| [Service](#service) | `up`, `down`, `update`, `uninstall`, `version` |

## admin

| Command | Effect |
| ------- | ------ |
| `admin add <login>` | Create an administrator; prints the password and the panel address. |
| `admin password <login>` | New password; closes sessions, lifts a lockout. |
| `admin list` | List with the lockout state. |
| `admin remove <login>` | Delete. |
| `admin path [--new]` | Show the panel address; `--new` issues a new one, active after a restart. |

Login: 1 to 64 characters, `A-Z a-z 0-9 . _ -`.

## inbound

| Command | Effect |
| ------- | ------ |
| `inbound add <slug> --url <url>` | Add an inbound for a streamable HTTP MCP server. |
| `inbound list` | Slug, state, URL. |
| `inbound enable\|disable <slug>` | Toggle without deleting. |
| `inbound remove <slug>` | Delete, revoking it from all clients. |

Options of `add`:

| Option | Meaning |
| ------ | ------- |
| `--name <text>` | Display name; the slug by default. |
| `--header Name=value` | Upstream header, repeatable, stored encrypted. |
| `--forward-identity` | Send the client identity headers upstream, see [Gateway](gateway.md#what-the-upstream-receives). |

Slug: 1 to 64 characters, `a-z 0-9 -`, no leading or trailing hyphen.

## client

| Command | Effect |
| ------- | ------ |
| `client add <name> --inbound <slug>` | Issue a client; prints the token and the addresses. |
| `client show <name>` | Token, addresses, limits, quota usage. |
| `client set <name>` | Change expiry, inbounds, limits. |
| `client reset <name>` | Zero the quota counter and restart the period. |
| `client reissue <name>` | New token; the old one stops working. |
| `client list` | Name, state, expiry, last use, quota, inbounds. |
| `client devices <name>` | Devices with apps, sign-in method, last use, IP and state. |
| `client device-off <name> <id>\|--all` | Disconnect a device (its id or the first characters) or all of them. |
| `client invite <name>` | One-time link that binds an outside account; valid 7 days. |
| `client links <name>` | Accounts linked to the client. |
| `client unlink <name> <provider>` | Remove the links of a provider. |
| `client enable\|disable <name>` | Toggle without deleting. |
| `client remove <name>` | Delete. |

Options of `add` and `set`:

| Option | Meaning |
| ------ | ------- |
| `--inbound <slug>` | Repeatable; in `set` it replaces the whole set. |
| `--expires YYYY-MM-DD` | Access closes at 00:00 UTC of that day; `never` in `set` removes the expiry. |
| `--meta key=value` | `add` only; repeatable; sent upstream as `X-Mcp-Meta-<key>`. |
| `--rate N` | Requests per minute. |
| `--concurrency N` | Simultaneous `POST` requests. |
| `--quota N` | `tools/call` per period. |
| `--reset-days N` | Length of the quota period; without it the quota is one-off. |
| `--devices N` | Device limit for browser sign-in; `none` in `set` removes it. |
| `--direct-key on\|off` | Whether the key works in a header and an address, or only for signing in. |

In `set`, `none` removes a limit and omitted options keep their values. Limits are described in [Gateway](gateway.md#limits).

Name: 1 to 100 characters, unique ignoring case, no control characters. Quote names with spaces.

```bash
sudo ctx-ui client set "Ivanov 4419" --quota 500 --reset-days 30 --rate 60
sudo ctx-ui client set "Ivanov 4419" --rate none
```

## usage

| Command | Effect |
| ------- | ------ |
| `usage days [--days N]` | Daily totals for the last N days (7 by default, at most 3650). |
| `usage log [--limit N]` | Recent calls, newest first (50 by default, at most 1000). |

Both accept `--client <name>`. Times are UTC. Data reaches the database every `FlushInterval` (5 seconds), so it lags slightly.

## oauth

| Command | Effect |
| ------- | ------ |
| `oauth status` | State, server metadata address, defaults, providers with their return addresses; warns when the public address rules OAuth out. |
| `oauth on\|off` | Turn browser sign-in on or off; `off` warns about clients without a direct key. |
| `oauth key on\|off` | Allow or forbid signing in with a key. |
| `oauth set [--idle-days N] [--self-revoke on\|off] [--devices N\|none] [--direct-key on\|off]` | Idle period of devices, self-service disconnecting, defaults for new clients. |
| `oauth provider set <name> --client-id <id>` | Add or change a provider. |
| `oauth provider on\|off\|remove <name>` | Enable, disable, remove. |
| `oauth provider list` | Configured providers. |

`provider set` takes the secret from `--client-secret`, from the standard input with `--client-secret-stdin`, or from `CTX_UI_PROVIDER_SECRET`. For `oidc` add `--issuer <https://…>` and `--title <text>`. Names: `google`, `microsoft`, `github`, `gitlab`, `yandex`, `vkid`, `discord`, `telegram`, `oidc`. See [Browser sign-in](oauth.md).

## apikey

| Command | Effect |
| ------- | ------ |
| `apikey add <name>` | Issue a REST API key; prints it once with the API and Swagger addresses. |
| `apikey list` | Name, key prefix, issue date. |
| `apikey remove <name>` | Revoke; requests with the key get 401 at once. |

Name: 1 to 64 characters, `A-Z a-z 0-9 . _ -`. See [REST API](api.md).

## backup

`backup <directory>` writes a consistent copy of the database and the `keys/` directory while the service keeps running. The directory must be missing or empty. See [Installation](install.md#backup).

## extension

| Command | Effect |
| ------- | ------ |
| `extension install <archive>` | Install or replace an extension; checks the archive by the rules of the server. |
| `extension list` | Installed extensions and their state: `ok`, `disabled`, `incompatible`, `invalid`. |
| `extension remove <id> [--purge]` | Remove an extension; `--purge` also deletes its data. |

Restart the service after every change. See [Panel extensions](extensions.md#install-update-remove).

## Service

| Command | Effect |
| ------- | ------ |
| `up` | Start the service and turn start at boot back on. |
| `down` | Stop the service and keep it off after a reboot. |
| `update [version]` | Update to the latest release or to a given version. |
| `uninstall [--keep-data] [--yes]` | Remove the program; without `--keep-data` also settings, data and the user. |
| `version` | Installed version. |

These commands exist in the systemd install. See [Installation](install.md).
