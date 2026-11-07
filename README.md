<div align="center">

# ctx-ui

**A gateway and web panel for sharing access to MCP servers.**
Put a remote MCP server behind your own address, hand out tokens with an expiry date and limits, and see who calls what.

**English** · [Русский](README.ru.md)

[![Release](https://img.shields.io/github/v/release/Echways/ctx-ui)](https://github.com/Echways/ctx-ui/releases/latest)
[![CI](https://github.com/Echways/ctx-ui/actions/workflows/ci.yml/badge.svg)](https://github.com/Echways/ctx-ui/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

</div>

## Why ctx-ui

An MCP server is usually one URL and one shared secret. ctx-ui puts a layer between that server and the people who use it, so access can be given, limited and withdrawn one client at a time.

- **One address per server.** An inbound is `/mcp/<slug>`, backed by a remote MCP server over streamable HTTP. The upstream address and its secret headers never reach a client.
- **A token per client.** Each client has its own token, an expiry date and a set of inbounds. Reissue or disable it without touching anyone else.
- **Limits that behave.** Request rate, concurrent requests and a quota of tool calls per period. An exhausted quota arrives as a tool result, so the model tells the user instead of failing the connection.
- **Sign-in in the browser.** Claude, ChatGPT and Cursor connect with the server address alone. People sign in with the client's key, Google, GitHub or any OpenID Connect provider. Off by default.
- **A panel that shows what happens.** Overview, metrics with response-time percentiles, a call log, devices, API keys. It lives under a random secret path.
- **Everything is scriptable.** A CLI and a REST API with Swagger UI cover the whole panel. Changes reach the running gateway within a second.
- **Extensible.** Panel extensions add sections, blocks and actions from a zip archive, without rebuilding ctx-ui.
- **Servers without leaving the panel.** The optional builder creates FastMCP servers from forms or code, runs each in an isolated Docker container and connects it as an inbound.
- **Simple to run.** One binary with systemd or Docker with Caddy, on Linux x86_64 and aarch64. English and Russian throughout.

## Installation

Binary and systemd:

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh | sudo bash
```

Requires Linux x86_64 or aarch64 with systemd. The service speaks plain HTTP: put Caddy or nginx in front for TLS. Docker with Caddy, updating, removal and backup are in [Installation](docs/en/install.md).

## Quick start

```bash
sudo ctx-ui admin add admin
sudo ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
sudo ctx-ui client add "Ivanov 4419" --inbound learn --expires 2027-07-01
```

`admin add` prints the login, a one-time password and the secret address of the panel. `client add` prints the token and the connection addresses. Under Docker, prefix commands with `docker compose exec ctx-ui`.

Connect a client with the token in a header:

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn \
    --header "Authorization: Bearer tnk_..."
```

More in [Getting started](docs/en/getting-started.md).

## What is included

| Area            | Highlights                                                                                                      |
| --------------- | --------------------------------------------------------------------------------------------------------------- |
| Gateway         | Streamable HTTP inbounds, per-client tokens, expiry, header and path authentication.                            |
| Limits          | Rate, concurrency, tool-call quota with a reset period, request timeout and body size.                          |
| Browser sign-in | OAuth for MCP apps, nine sign-in providers, invite links, devices with a limit.                                 |
| Panel           | Overview, metrics, call log, clients, inbounds, API keys, English and Russian.                                  |
| CLI             | `admin`, `inbound`, `client`, `usage`, `oauth`, `apikey`, `backup`, `extension`.                |
| REST API        | The whole panel under `/api/v1`, OpenAPI 3.1 document, Swagger UI.                                            |
| Extensions      | Sections, slots, actions, endpoints, hosted services and a database, with a project template and browser tests. |
| Builder         | FastMCP servers from forms, OpenAPI import or code; isolated containers; versions and rollback.                 |
| Operations      | Install, update and uninstall with one command, consistent backup of a running service.                         |

## Documentation

|                                              |                                                   |
| -------------------------------------------- | ------------------------------------------------- |
| [Getting started](docs/en/getting-started.md) | Installation, the first client, connecting an app |
| [Installation](docs/en/install.md)            | systemd, Docker, TLS, updating, backup            |
| [Configuration](docs/en/configuration.md)     | Variables, language, data directory               |
| [Panel](docs/en/panel.md)                     | Sections, sign-in, security                       |
| [CLI](docs/en/cli.md)                         | Every command and option                          |
| [REST API](docs/en/api.md)                    | Resources, errors, Swagger                        |
| [Gateway](docs/en/gateway.md)                 | Authentication, limits, responses, upstream       |
| [Browser sign-in (OAuth)](docs/en/oauth.md)   | Devices, invites, providers                       |
| [Panel extensions](docs/en/extensions.md)     | Building and installing extensions                |
| [Server builder](docs/en/builder.md)          | FastMCP servers in the panel                      |
| [Troubleshooting](docs/en/troubleshooting.md) | Common problems and what to check                 |

## License

[GPL-3.0](LICENSE)
