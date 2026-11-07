# Getting started

[English](getting-started.md) · [Русский](../ru/getting-started.md) · [Documentation](README.md)

This page takes an empty Linux server to a gateway with one MCP server behind it and one connected client.

## Concepts

- An **inbound** is an address, `/mcp/<slug>`, backed by a remote MCP server that speaks streamable HTTP.
- A **client** is a token with an expiry date, a set of inbounds and limits. One client is one person, one application or one team.
- An **administrator** signs in to the panel. The panel and the REST API live under a random secret path.

## Requirements

- Linux x86_64 or aarch64 with systemd, root access, `curl`, `tar`, `sha256sum`.
- A domain pointing at the server, if clients connect over the internet.
- Caddy or nginx in front of the service for TLS. See [Installation](install.md#tls). The Docker bundle brings Caddy with it.

## Installation

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh | sudo bash
```

The installer asks for the external address that clients see, for example `https://mcp.example.com`, and the local listen address. Everything else is in [Installation](install.md).

## The first administrator

```bash
sudo ctx-ui admin add admin
```

The command prints the login, a generated one-time password and the secret address of the panel. Open it and sign in. The address is shown again by `sudo ctx-ui admin path`.

## The first inbound and client

```bash
sudo ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
sudo ctx-ui client add "Ivanov 4419" --inbound learn --expires 2027-07-01
```

`client add` prints the token and two connection addresses. Both can also be copied from the **Connection** window of the client in the panel, together with a ready-to-send message for the person.

Changes made by the CLI reach the running service within a second. No restart is needed.

## Connecting a client

| Method | Address |
| ------ | ------- |
| header `Authorization: Bearer <token>` | `https://<host>/mcp/<slug>` |
| token in the path, for clients without custom headers | `https://<host>/k/<token>/mcp/<slug>` |

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn \
    --header "Authorization: Bearer tnk_..."
```

A path with a token is a credential. Treat it as a password and keep it out of logs and screenshots.

Apps such as Claude, ChatGPT and Cursor can connect with the server address alone and sign in in the browser. That is off by default; see [Browser sign-in](oauth.md).

## Limits

Add limits when issuing a client or later:

```bash
sudo ctx-ui client set "Ivanov 4419" --rate 60 --quota 500 --reset-days 30
```

See [Gateway](gateway.md#limits).

## Where to go next

| | |
| - | - |
| [Panel](panel.md) | What every section shows |
| [CLI](cli.md) | Every command |
| [Configuration](configuration.md) | Addresses, intervals, language |
| [Browser sign-in](oauth.md) | Connecting apps without copying a key |
| [Server builder](builder.md) | Building MCP servers in the panel |
| [Troubleshooting](troubleshooting.md) | When a client cannot connect |
