# Internals

**English** · [Русский](ru/internals.md)

Design decisions not evident from the code.

## Projects

| Project | Role |
| --- | --- |
| `CtxUi.Core` | domain, EF Core model, migrations, services |
| `CtxUi.Gateway` | `/mcp/<slug>` handling, limits, quota, call log |
| `CtxUi.Panel` | Blazor Server panel (MudBlazor), sign-in |
| `CtxUi.Server` | entry point: server or CLI |

## Server and CLI

One executable. First argument `inbound`, `client`, `usage` or `admin` →
CLI; otherwise → server. Both register services via
`ServiceRegistration.AddCtxUi` so they share the database and keys.

- `ServerApp.Build` takes a callback: tests override port, data directory,
  clock.
- `ServerApp.InitializeAsync` migrates and loads routes and the panel path
  before start; the first request never sees an empty table.
- The application name is set explicitly: ASP.NET Core uses it to find the
  static web assets manifest, and under the test host it would be
  `testhost`.
- `UseStaticWebAssets` is always called: from source and in tests MudBlazor
  assets are in the NuGet cache; in a published build it is a no-op.
- CLI logs go to stderr; stdout is parseable.
- `DomainException` = broken domain rule. Its message is shown to the
  administrator verbatim: Russian, no internal details.

## Routing table

The gateway does not query the database per request. It reads a
`RoutingSnapshot`, an immutable table of routes and clients. A change
produces a new snapshot that replaces the old one; `SnapshotStore` serves it
lock-free.

Cross-process change detection uses one row with a counter
(`RoutingState`):

- `CtxUiDbContext.SaveChangesAsync` increments it in the same transaction
  when an `Inbound`, `Client` or `ClientInbound` changed. The increment is
  SQL, not in memory, so concurrent processes do not lose increments;
- `SnapshotRefresher` compares it with the loaded revision every
  `RefreshInterval` and reloads only on mismatch;
- the loader reads the revision before the data. A change in between yields
  old revision + new data, fixed on the next check. The reverse order could
  miss a change;
- the initial snapshot has revision `-1`, which matches nothing.

Last-use stamps and quota counters are written with `ExecuteUpdateAsync`,
bypassing change tracking; the call log tables are not routing entities.
None of them trigger a reload.

## Token never reaches the log

`/k/<token>/mcp/<slug>` exists for clients without custom headers. The
middleware that strips the token is first in the pipeline, before routing
and logging. Pipeline order is fixed in `ServerApp.Build`; do not change it.

Log categories that print the raw URL or are noisy per request are
filtered in code (`AddCtxUiFilters`), not in `appsettings.json`, so leak
protection does not depend on a replaceable file.

Request log entries contain only the slug of a known inbound, never an
arbitrary path segment.

The plaintext token exists only in `IssuedClient` at issue time. The
database stores a SHA-256 hash and an encrypted copy.

## Forwarding

`HttpUpstream` streams request and response bodies without parsing them, so
any MCP protocol revision and SSE work.

- Upstream URL is the inbound's URL as is; the client's path and query are
  not appended.
- The client token is not forwarded.
- `X-Mcp-Client-*` and `X-Mcp-Meta-*` are set only by the gateway; client
  copies are stripped first.
- Header values are ASCII, so names and metadata are percent-encoded UTF-8.

## Clients

- Names are unique ignoring case. The key (`ClientName.Key`: NFKC +
  upper case) is a separate column because SQLite's case folding covers only
  Latin letters. Requires ICU.
- Inbound links are edited by diff: remove + re-add of the same link in one
  context conflicts on the composite key.
- `ClientUpdate`: `ExpiresAt = null` means "never" only with
  `ChangeExpiry = true`; `InboundSlugs = null` means "unchanged".
- `TouchThrottle` limits last-use writes. `ClientTouch` never throws: a
  stats failure must not fail the request.
- `--expires 2027-07-01` is parsed as 00:00 UTC. Default parsing would use
  server local time.

## Limits and quota

Check order in `GatewayHandler`: token/access → rate → concurrency → quota.
Cheapest first; the body is read only after the first three pass.

**Rate and concurrency.** `ClientLimiter` keeps one
`System.Threading.RateLimiting` limiter per client, partitioned by client +
limit value: a new value applies on the next request and the library
disposes the old limiter. Rate is a token bucket with capacity = limit and
continuous refill. `Retry-After` is computed by the gateway as time until
one token returns; the library would report a full minute.

Limiters use the system clock (the library does not accept
`TimeProvider`), so the refill test sleeps.

Concurrency counts only `POST`. MCP clients keep a `GET` notification stream
open; counting it would block a client with limit 1 forever. Rate counts all
methods.

**Body.** For `POST` the gateway buffers the body up to
`Inbound.MaxBodyBytes` (default 4 MB) and forwards the same bytes. The
response still streams.

`RpcEnvelope` extracts only `id`, `method`, `params.name` and rejects input
two parsers could read differently: duplicate keys, trailing documents,
depth > 256. Otherwise a `tools/call` could pass as `ping` and skip the
quota. Such requests get 400 and are not forwarded.

A JSON-RPC batch (protocol revisions before 2025-06-18) is charged per
`tools/call` it contains and passes all-or-nothing. Max 100 messages.

Method and tool names are client input shown in the panel and stored in the
database: truncated to 128 characters, control characters replaced, never
written to the server log.

**Quota.** The period is computed, not stored: `QuotaPeriod.StartAt` counts
whole reset intervals from `Client.PeriodStartedAt`. Single writer per
field:

| Field | Writer |
| --- | --- |
| `PeriodStartedAt` | administrator (`client reset`, first quota, new interval) |
| `UsedCalls`, `UsedSince` | server |

A counter whose `UsedSince` is not the current period start reads as zero.
This one rule covers scheduled reset, manual reset and period change.
Changing the quota size does not touch the period.

Counters live in `QuotaLedger`. `QuotaSaver` writes changed ones every
`FlushInterval` and on shutdown; they are loaded at start. A crash loses at
most one interval.

A call is charged before forwarding and refunded on upstream status ≥ 400 or
no response. 4xx is refunded too: the call did not run, and a typical cause
(wrong upstream header) is not the client's fault. A client-aborted call is
not refunded: the upstream may have run it.

Quota refusal is HTTP 200 with a tool result, `isError: true`: MCP clients
show HTTP errors as "server unavailable", while a tool result reaches the
model. `id` is copied from the request byte for byte. Other messages in the
batch get JSON-RPC `-32000`; a request with no `id` gets 202 with no body.

## Call log

`UsageJournal` is an in-memory queue (10 000 records). `UsageWriter` flushes
it every `FlushInterval` and on shutdown: `UsageEvents` rows and `UsageDays`
counters in one transaction. On overflow records are dropped and the count
is logged as a warning; accounting never blocks requests.

| Request | Log row | Day counter |
| --- | --- | --- |
| message with a method | yes | yes |
| refused before the method is read (rate, concurrency, body size/format) | no | yes |
| `GET` stream, `DELETE` session | no | no |

Pre-parse refusals stay out of the log so a client cannot flood it with
requests that were never served.

`UpstreamRejected` = 4xx; `UpstreamFailed` = 5xx or no response. JSON-RPC
errors inside a 200 are invisible: responses are not parsed.

Accounting tables have no foreign keys to clients and inbounds: a batch
write does not fail on a just-deleted client, and history of deleted ones
remains (`usage log` prints «удалён»).

Rows older than `UsageRetentionDays` are pruned hourly; day counters are
kept forever. Days are UTC.

## Panel

**Secret path.** Random 16 characters, stored in `Settings`, created on
first access, read once at server start (changing it requires a restart).
`PanelGate` runs right after token stripping:

| Request path | Result |
| --- | --- |
| starts with the secret | passes; prefix moved to `PathBase` |
| `/mcp/<slug>` | passes to the gateway |
| anything else under `/mcp` | 404 from the gateway |
| anything else | 404, empty body |

The open-path list is in `ServerApp.Build`; a new endpoint is closed until
added. The gateway owns all of `/mcp` so the panel's not-found route cannot
answer `/mcp` or `/mcp/a/b` with a redirect to its login.

The secret path is stripped before routing and never logged. The cookie is
scoped to it.

**Sign-in.** ASP.NET Core Identity over one table, `Admins`, via a custom
`AdminStore`. The EF Core store would pull an ASP.NET package and four
tables into `CtxUi.Core`. `AdminAccounts` needs only the password hasher, so
`ctx-ui admin` runs without a web host.

- Passwords are generated (20 letters/digits); user-chosen passwords are not
  supported, so lockout (5 attempts, 15 min) is a second line of defence.
- Identity's lockout uses the system clock, not `TimeProvider` (second
  exception after rate limiters).
- The failure counter is read-then-write, not atomic: parallel guessing may
  get slightly more than five attempts.
- Wrong password, unknown login and locked login produce the same response.

Sign-in and sign-out pages are static SSR: cookies can only be set in an
HTTP response. Other pages are interactive server without prerendering, so
data loads once.

«Выйти» does a full page load: the interactive router does not know static
pages and would route the sign-out URL to the not-found page.

**Sessions.** The cookie carries the admin's `SecurityStamp`; a new password
or deletion changes it. Every `SessionCheckInterval` both the cookie handler
and the open page (`AdminSessionState`) validate it; an interactive page
makes no HTTP requests and would otherwise live until the tab closes.

Authorization is enforced on endpoints (`RequireAuthorization`, including the
WebSocket) and on pages (`[Authorize]` in `Pages/_Imports.razor`) so an open
page closes when the session becomes invalid.

**Data access.** `CtxUi.Core` services are scoped per request; a panel page
lives for hours and handles events concurrently. Pages call `PanelBackend`,
which runs each action in a new scope: `ReadAsync`, `ChangeAsync`.
`ChangeAsync` then reloads routes via `IRoutingRefresh` without waiting for
`RefreshInterval`. The interface lives in `CtxUi.Core` and is implemented in
the gateway; the panel does not reference the gateway.

**Secrets on screen.** Upstream header values are never sent to the panel;
the edit dialog gets names only. The connection dialog shows the token by
design. Tool and method names in the log are client input; Blazor renders
them as text.

**Reverse proxy.** `X-Forwarded-Proto` is trusted from any source: it sets
the cookie's `Secure` flag and keeps redirects on `https`. A forged header
affects only the forger's responses. `X-Forwarded-For` is ignored.

**No external requests.** Styles, scripts and fonts are served locally; the
panel works offline and leaks its address to no one.

Times are shown in UTC and labelled as such.

## REST API

| What | How |
| --- | --- |
| Middleware order | `UseForwardedHeaders` → `UseTokenPath` → `UseCtxUiPanelGate` → `UseCtxUiApiDocs` → `UseRouting` → … Swagger UI sits behind the gate, so it does not exist without the secret path |
| Sign-in | scheme `ApiKey`; policy `api` accepts only that scheme. The panel cookie does not open the API, so there is no CSRF |
| Errors | filter `ApiChanges`: `NotFoundException` → 404, `DomainException` → 400, `detail` is the exception text capitalised. Broken JSON and 401 are shaped by `UseStatusCodePages`, only under `/api`, so empty 404s and panel responses stay as they were |
| Unknown address | `/api/{**rest}` → 404. Without it the request falls to the panel's `NotFound` page and gets a redirect to the login form |
| Changes | after every non-GET request the filter calls `IRoutingRefresh.RefreshAsync`, as `PanelBackend.ChangeAsync` does |
| Document | `MapOpenApi("/openapi/{documentName}.json")`, only `api/` paths. The server address is built from the request including `PathBase`; `UseForwardedHeaders` supplies `https` |
| Swagger UI | `UseSwaggerUI`, `RoutePrefix = "swagger"`, document at the relative `../openapi/v1.json`. No external requests: `validatorUrl` is `null` by default and all files are embedded |

**Keys.** `ctxk_` plus 40 letters and digits, a different prefix and length
from client tokens so the two are never confused. Stored as SHA-256 and an
11-character prefix for lists; lost keys are reissued, not recovered.
Keys do not bump the routing revision.

**Addressing.** Clients by id: names may contain `/`, and `%2F` in a path
segment is not decoded. `GET /clients?name=` finds one by name.

**Not found.** `NotFoundException` derives from `DomainException`, so the
CLI and the panel keep catching one type while the API tells 404 from 400.

## Storage

- SQLite cannot compare `DateTimeOffset`; time is stored as UTC milliseconds
  (`UnixMillisConverter`).
- `ISecretVault` is reversible encryption (Data Protection) for token copies
  and upstream headers. `Open` returns `null` on lost keys or corrupt data.
  An inbound with unreadable headers is dropped from routing with a warning;
  the server keeps running.
- `DesignTimeFactory` exists only for `dotnet ef`.
- Generated migrations are stripped of generator comments
  (`// <auto-generated />`, `/// <inheritdoc />`).

## Tests

| Helper | Purpose |
| --- | --- |
| `TestDatabase` | in-memory SQLite with migrations; lives while the connection is open |
| `FakeVault.Broken` | simulates lost keys |
| `GatewayHost` | gateway on a free port with its own temp data dir; changes via services (immediate) or as another process would (no refresh); `RestartAsync` restarts on the same data |
| `FakeUpstream` | real HTTP server recording requests |
| `CapturingLogs` | captures server logs to check for token leaks |
| `Eventually` | polls until a condition holds (background effects) |
| `Pipeline` | runs token stripping without a server |
| `CliRunner` | runs the CLI in-process against a data dir, including a running `GatewayHost`'s |
| `McpEndToEndTests` | real MCP server behind the gateway, real MCP client in front |
| `PanelClient` | HTTP-only browser: cookies, no redirects, reads the antiforgery token from the form |
| `PanelSession` | real Chromium via Playwright; one browser per run, clean profile per test |

- A fake clock drives expiry and quota periods. Rate limiters use the real
  clock, so their tests check refusal right after the bucket empties.
- "Immediately, no restart" tests set `RefreshInterval` to 1 h; otherwise
  the 50 ms background check would hide a panel that fails to refresh
  routes.
- Panel tests keep the real clock: the browser validates cookie expiry
  itself. Expired clients are made by editing the date in the database.
- Playwright tests locate elements by role and label, not MudBlazor classes.
- CI installs Chromium system libraries in a separate step; its `playwright`
  version must match `Microsoft.Playwright` in `Directory.Packages.props`.
- Temp directories are deleted at the end; deletion errors are ignored.

## Build and delivery

**ICU in the archive.** Self-contained builds need ICU; minimal servers lack
it and the program crashes at start. Invariant mode breaks NFKC, which
changes client name keys. Self-contained publish bundles
`Microsoft.ICU.ICU4C.Runtime` (a `SelfContained` condition in
`CtxUi.Server.csproj`). The Docker image uses the base image's ICU.

**Native libraries as files.** `libe_sqlite3.so` and ICU are not bundled into
the single file: bundled libraries are extracted to the home directory,
which the `ctx-ui` user does not have.

**Working directory.** `appsettings.json` is read from the current
directory, fixed by the unit (`WorkingDirectory`), the CLI wrapper (`cd`)
and the image (`WORKDIR`). The program does not guess paths, so `dotnet run`
works unchanged.

**CLI wrapper.** `/usr/local/bin/ctx-ui` reads `/etc/ctx-ui/ctx-ui.env` line
by line (not sourced as shell) and runs the binary as `ctx-ui`; running as
root would create files the service cannot read.

**Panel static files.** `wwwroot` plus
`ctx-ui.staticwebassets.endpoints.json` next to the binary; the service
fails to start without the manifest, so packaging keeps it. The Blazor
script lands in `wwwroot` only via `RequiresAspNetWebAssets` in
`CtxUi.Server.csproj`: the SDK adds it automatically only when `.razor`
files are in the same project, and they are in `CtxUi.Panel`.

**Installer archive check.** The binary is checked with `-f`, not `-x`:
with `/tmp` mounted `noexec`, `-x` is always false.

**Image.** The build stage runs on the build platform and cross-publishes
for the target; the final stage has no `RUN`, so arm64 builds on x64
without emulation. `.dockerignore` is `Dockerfile.dockerignore` next to the
Dockerfile to keep the root clean.

**Data directory mode.** The program sets `700` on the data directory and
`keys/` at every start, not only when it creates them. The mode of `/data`
in the image and of a volume created from it depends on the Docker version:
`COPY --chmod` alone has produced `755`.

**Packaging.** `deploy/package.sh` builds all release files. The `release`
workflow and `deploy/tests/linux.sh` both use it, so the installer is tested
on the same archives that ship.
