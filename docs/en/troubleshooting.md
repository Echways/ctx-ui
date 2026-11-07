# Troubleshooting

[English](troubleshooting.md) · [Русский](../ru/troubleshooting.md) · [Documentation](README.md)

This page lists the most common problems and what to check. Messages of the gateway are listed in [Gateway](gateway.md#responses).

## A client cannot connect

| Symptom | Cause | Fix |
| ------- | ----- | --- |
| 401 `token_missing` | The request has no token. | Send `Authorization: Bearer <token>`, or use the address with the token in the path. |
| 401 `token_unknown` | The token is wrong or was reissued. | Show the current one with `ctx-ui client show <name>`. `client reissue` makes the old token stop working. |
| 401 `client_disabled` or `client_expired` | The client is turned off or its expiry has passed. | `ctx-ui client enable <name>`, or `client set <name> --expires never`. |
| 404 `inbound_not_found` | The inbound does not exist, is disabled, or is not granted to this client. | Check `ctx-ui inbound list` and the inbounds of the client in `ctx-ui client list`. |
| 429 | The rate or concurrency limit is exceeded. | Wait for `Retry-After`, or raise the limit with `client set`. |
| A tool answers that the quota is used up | The quota of the period is spent. | `ctx-ui client reset <name>`, or raise `--quota`. |
| The upstream does not answer | The address of the inbound is wrong, or the upstream is down. | Look at the **Journal** and **Metrics** of the panel for the HTTP status and failures. |

## The panel

**The panel answers 404.** Only the secret path opens it. Show it with `sudo ctx-ui admin path`.

**A login is locked.** Five failed attempts lock a login for 15 minutes. `sudo ctx-ui admin password <login>` lifts the lock and issues a new password.

**A CLI change does not show.** The running service picks up changes within `RefreshInterval`, one second by default. Check that the command ran against the same data directory as the service.

**The panel loses its connection behind a proxy.** The panel needs WebSocket. Pass `Upgrade` and `Connection` through the proxy, see [Installation](install.md#tls).

## Browser sign-in

See [If sign-in does not open](oauth.md#if-sign-in-does-not-open). The usual causes are a public address without `https://`, a missing `CtxUi__TrustedProxies` behind a proxy, and OAuth being off, which shows as an empty 404 on `/.well-known/*`.

## Installation

**`ctx-ui backup` cannot write.** The command runs as the `ctx-ui` user and needs a directory that user can write. See [Backup](install.md#backup).

**An archive of an extension is not found.** `ctx-ui extension install` runs as the service user. Keep the archive where that user can read it, for example in `/tmp`.

**An extension does not load.** `ctx-ui extension list` shows its state and the reason. The server skips an incompatible extension and logs the error; an exception in `Register` stops the start and names the extension.

**Tokens cannot be shown after a restore.** The `keys/` directory was not restored. Tokens still work; reissue them with `client reissue`. See [Configuration](configuration.md#data-directory).

## Server builder

**The section shows that the Runner is unreachable.** The section prints the reason and the command that fixes it. See [Server builder](builder.md#install).

**Built servers have no containers after a reinstall.** Press "Start again" on each server.

**A server cannot reach the network.** With "Internet only" a client that ignores `HTTP_PROXY` and `HTTPS_PROXY` has no network, and mail ports are closed. See [Network access](builder.md#network-access).
