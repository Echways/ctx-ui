# Gateway

[English](gateway.md) · [Русский](../ru/gateway.md) · [Documentation](README.md)

The gateway answers MCP requests at `/mcp/<slug>`, checks the client, applies its limits and forwards the request to the upstream server.

## Authentication

| Method | Address |
| ------ | ------- |
| header `Authorization: Bearer <token>` | `https://<host>/mcp/<slug>` |
| token in the path, for clients without custom headers | `https://<host>/k/<token>/mcp/<slug>` |

A path with a token is a credential. The installation guide turns access logs off in nginx for this reason. See [Installation](install.md#tls).

Apps can also connect with the address alone and sign in in the browser. See [Browser sign-in](oauth.md).

## Limits

Each limit is optional. A limit that is not set does not apply.

| Limit | Counts | When exceeded |
| ----- | ------ | ------------- |
| `--rate` | All requests, token bucket. | HTTP 429, `Retry-After: <seconds until the next>`. |
| `--concurrency` | In-flight `POST` requests. | HTTP 429 after waiting up to 1 second for a slot, `Retry-After: 1`. |
| `--quota` | `tools/call` per period. | HTTP 200, a tool result with `isError: true` and the reset time. |

The quota is spent only by `tools/call`. With an exhausted quota the client still connects and lists tools. The refusal is a tool result, so the model relays it to the user. Calls that the upstream answered with HTTP 400 or higher, or did not answer, are refunded.

Without `--reset-days` the quota is one-off. Set it with `client add` or `client set`, see [CLI](cli.md#client).

The wait for a concurrency slot is set by `CtxUi__ConcurrencyWait`, see [Configuration](configuration.md#variables).

## Per-inbound limits

The request timeout (300 seconds by default) and the request body limit (4 MB by default) belong to the inbound and are set in the panel. A body over the limit gets 413.

## Responses

| Status | `error` | Cause |
| ------ | ------- | ----- |
| 401 | `token_missing` | No token. |
| 401 | `token_unknown` | Token not found. |
| 401 | `client_disabled` | Client disabled. |
| 401 | `client_expired` | The expiry has passed. |
| 404 | `inbound_not_found` | No such inbound, it is disabled, or it is not granted to the client. |
| 429 | `rate_limited`, `too_many_concurrent` | A limit is exceeded. |
| 413 | `body_too_large` | The body is over the limit of the inbound. |
| 400 | JSON-RPC `-32600` | The body is not valid JSON-RPC, or a batch has more than 100 messages. |

The language of these messages follows `CtxUi__Language`.

## What the upstream receives

The request of the client with its token removed, plus the headers of the inbound.

With `--forward-identity` the gateway also sends `X-Mcp-Client-Id`, `X-Mcp-Client-Name` and `X-Mcp-Meta-<key>` for every `--meta` of the client. Values are percent-encoded UTF-8. Client-sent headers with these names are stripped.

The headers of an inbound are stored encrypted.

## Health

`GET /healthz` answers 200 when the service and its database are healthy. The Docker image uses it for its health check.
