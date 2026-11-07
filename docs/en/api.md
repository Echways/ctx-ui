# REST API

[English](api.md) · [Русский](../ru/api.md) · [Documentation](README.md)

The API covers everything the panel does. It lives under the secret path of the panel, so it cannot be found by scanning.

| Address | What |
| ------- | ---- |
| `https://<host>/<secret>/api/v1/…` | The API. The key goes in `Authorization: Bearer ctxk_…`. |
| `https://<host>/<secret>/swagger/` | Swagger UI. "Authorize" takes the key. |
| `https://<host>/<secret>/openapi/v1.json` | The OpenAPI 3.1 document. |

## Keys

```bash
sudo ctx-ui apikey add ci
```

The command prints the key once, with the API and Swagger addresses. Keys are also issued and revoked in the **API keys** section of the panel. A revoked key gets 401 at once.

A panel session does not open the API: browsers send the cookie with requests from any site.

```bash
curl -H "Authorization: Bearer ctxk_..." https://mcp.example.com/<secret>/api/v1/clients
```

## Resources

| Resource | Methods |
| -------- | ------- |
| `inbounds`, `inbounds/{slug}` | List, get, `PUT` creates or replaces, `enable`, `disable`, `DELETE`. |
| `clients`, `clients/{id}` | List (`?name=` finds one), get, `POST` issues, `PATCH` changes, `connection`, `enable`, `disable`, `reset-quota`, `reissue`, `DELETE`. |
| `clients/{id}/devices`, `…/devices/{deviceId}` | List devices, `DELETE` disconnects one or all. |
| `clients/{id}/invite`, `clients/{id}/links`, `…/links/{linkId}`, `…/links/{provider}` | `POST` issues an invite link, list links, `DELETE` unlinks one account or every account of a provider. |
| `oauth`, `oauth/providers/{name}` | Settings (`PATCH`), providers (`PUT`, `enable`, `disable`, `DELETE`). Secrets are never returned. |
| `usage/days`, `usage/log`, `overview`, `metrics` | Daily totals, recent calls, the overview of the panel, the Metrics page (`days`, `clientId`, `inboundId`, `tool`). |

## Errors

Errors are `application/problem+json`. The `detail` text is the same as the CLI prints.

| Status | Meaning |
| ------ | ------- |
| 400 | Wrong input. |
| 401 | No key, or an unknown key. |
| 404 | No such object. |
| 409 | The token cannot be shown: the `keys/` directory is lost. |

Changes reach the gateway at once.

## Extensions

An extension can add its own endpoints under `/<secret>/extensions/<id>/`. They accept the same keys. The [server builder](builder.md#api-for-ci) uses this for CI.
