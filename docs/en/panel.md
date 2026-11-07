# Panel

[English](panel.md) · [Русский](../ru/panel.md) · [Documentation](README.md)

The panel manages inbounds, clients, API keys and sign-in, and shows what the gateway does. Everything it changes applies to the running gateway at once.

## Address

The panel is served only under a random 16-character path. Any other path answers 404, so the panel cannot be found by scanning. Show the address with `sudo ctx-ui admin path`; `--new` issues a new one that takes effect after a restart.

## Language

The panel speaks the `CtxUi__Language` language until an administrator picks one. The button in the top bar, and the link under the sign-in form, switch between English and Russian. The choice is kept in a cookie for a year. See [Configuration](configuration.md#language).

## Sections

| Section | Contents |
| ------- | -------- |
| Overview | Active clients, calls and upstream failures over 24 hours; totals per inbound and per day. |
| Metrics | 24 hours, 7, 30 or 90 days: calls, errors, response time p50, p95 and p99, traffic, the path of clients from the first call to a tool answer, breakdowns by inbound, tool, method, client and device, frequent errors with HTTP status, gateway refusals by reason. Any row narrows the view to that client, inbound or tool. |
| Inbounds | Create, edit, enable or disable, delete. |
| Clients | Create, edit, enable or disable, search, the **Connection** window, new token, quota reset, devices. |
| Journal | Recent calls with HTTP status, filter by client. |
| API keys | Issue, revoke, link to Swagger UI. |
| OAuth | Browser sign-in switch, providers, defaults for devices. See [Browser sign-in](oauth.md). |

The **Connection** window of a client shows the token, the addresses and a ready-to-send message for the person.

The metrics periods follow the call log retention: 90 days needs `CtxUi__UsageRetentionDays` of at least `90`.

## Inbound settings

The request timeout of an inbound (300 seconds by default, 1 to 3600) and the request body limit (4 MB by default, 1 KB to 64 MB) are set only in the panel.

## Signing in

The password of an administrator is generated, 20 characters long, shown once and stored as a hash. Five failed attempts lock the login for 15 minutes. The session cookie lives 7 days with sliding expiration.

```bash
sudo ctx-ui admin password admin
sudo ctx-ui admin list
```

`admin password` issues a new password, closes the sessions and lifts a lockout. `admin list` shows lockouts.

## Extensions

[Panel extensions](extensions.md) add sections, blocks and actions, and can change the navigation and the look. The [server builder](builder.md) is one of them.
