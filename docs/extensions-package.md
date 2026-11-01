# ctx-ui panel extensions

[ctx-ui](https://github.com/Echways/ctx-ui) is a gateway and web panel for sharing access to MCP servers. An extension adds sections, blocks and actions to its panel, and brings its own services, endpoints and database. It is built into a zip archive that an administrator installs with one command; ctx-ui itself is not rebuilt.

| Package | What it is for |
| --- | --- |
| `CtxUi.Extensions` | the contract and build targets, referenced by every extension |
| `CtxUi.Extensions.Templates` | `dotnet new ctxui-extension` |
| `CtxUi.Extensions.Testing` | browser tests against a real server |

```bash
dotnet new install CtxUi.Extensions.Templates
dotnet new ctxui-extension -n Acme.Reports --id acme-reports
```

Guide: [docs/extensions.md](https://github.com/Echways/ctx-ui/blob/main/docs/extensions.md).

Licensed under GPL-3.0-only.
