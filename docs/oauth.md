# Browser sign-in (OAuth)

**English** · [Русский](ru/oauth.md)

Off by default. While it is off, `/.well-known/*` and `/connect/*` answer an
empty 404 and everything else works exactly as before.

Turn it on with `ctx-ui oauth on` or the **OAuth** section of the panel. It
needs `CtxUi__PublicUrl` to start with `https://` (`http://localhost` is fine
for development).

## How it works for a person

1. The person adds the server address `https://<host>/mcp/<slug>` to an app
   (Claude, ChatGPT, Cursor and others). No key is copied.
2. The app finds the sign-in page by itself and opens it in the browser.
3. The person signs in with the client's key or, if an administrator linked
   it, with Google, GitHub or another account.
4. The browser asks once whether this app may use the server, and returns to
   the app. The app now works; it renews its access without asking again.

The address is the same as for a direct key, so a client can use either way.

## Devices and the limit

A browser that signs in is a **device**, remembered by a cookie for a year. A
second app opened in the same browser is the same device and needs no new
sign-in.

- A client may have a device limit (`--devices N`, the field in the client
  window, a default in the OAuth section). Past the limit the sign-in page
  lists the devices and lets the person replace one, unless the administrator
  turned that off.
- A device that is not used for the idle period (30 days by default, 1–365)
  is disconnected. A refresh token lives as long as that period and is moved
  forward each time it is used.
- Administrators disconnect devices in the panel (**Devices** in the client
  menu), with `ctx-ui client device-off` or through the API. The app is
  refused at its next call.
- A refresh token used a second time (after 30 seconds) means a copy leaked:
  the device is disconnected.
- `--direct-key off` makes a client's key work only for signing in; it can no
  longer be put in a header or an address.

## Invites

An outside account is bound to a client only through a one-time link, so a
stranger who signs in with Google gets nothing.

```bash
sudo ctx-ui client invite "Ivanov 4419"
```

The link `https://<host>/connect/invite/<code>` works once and lives 7 days.
The panel (connection window), the REST API (`POST /clients/{id}/invite`) and
the CLI issue it. `client links` and `client unlink` show and remove bindings.

## Providers

Every provider needs an application created in its console and a return
address `https://<host>/connect/callback/<name>`. Set a provider up in the
panel, with `ctx-ui oauth provider set <name> --client-id <id> --client-secret-stdin`
or through the API. A change applies at once, without a restart.

| Name | Where to create the application | Scopes |
| --- | --- | --- |
| `google` | Google Cloud console → APIs & Services → Credentials → OAuth client ID, type "Web application" | `openid profile email` |
| `microsoft` | Microsoft Entra → App registrations, platform "Web" | `User.Read` |
| `github` | GitHub → Settings → Developer settings → OAuth Apps | none beyond the default |
| `gitlab` | GitLab → Preferences → Applications | `read_user` |
| `yandex` | oauth.yandex.ru, platform "Web services" | login and e-mail |
| `vkid` | VK ID for business, platform "Web" | the default (name) |
| `discord` | Discord developer portal → OAuth2 | `identify` |
| `telegram` | @BotFather → "Log in with Telegram" (OpenID Connect) | `openid profile` |
| `oidc` | any OpenID Connect provider (Keycloak, Authentik, Okta…); needs an issuer address with `https://` and a title | `openid profile email` |

The secret is stored encrypted and is never shown again. In the panel an
empty secret field keeps the stored one.

## Compatibility

The server follows the MCP authorization rules: protected resource metadata
at `/.well-known/oauth-protected-resource/mcp/<slug>`, authorization server
metadata at `/.well-known/oauth-authorization-server`, dynamic client
registration, client ID metadata documents, PKCE (`S256` only) and the
`resource` parameter. Claude Code, claude.ai (custom connector), ChatGPT
(connector) and Cursor (`url` in `mcp.json`) connect this way.

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn
```

## If sign-in does not open

- `CtxUi__PublicUrl` must be the address the browser uses and start with
  `https://`. The service logs a warning at start when OAuth is on and it does
  not.
- `curl https://<host>/.well-known/oauth-authorization-server` must answer
  JSON whose `issuer` is that address. An empty 404 means OAuth is off.
- Behind a proxy set `CtxUi__TrustedProxies` (see
  [configuration.md](configuration.md)), or every device is recorded with the
  proxy's address. The proxy must send `X-Forwarded-For` and
  `X-Forwarded-Proto`.
- `ctx-ui oauth status` prints the state and the return address of each
  provider.
