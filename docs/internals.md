# How it works

**English** · [Русский](ru/internals.md)

This file records the decisions that are not obvious from the code at first
glance. The code itself has no comments: the explanations live here.

## Server and command line

The server and the `ctx-ui` command are one executable. A first argument of
`inbound`, `client`, `usage` or `admin` switches to command-line mode,
otherwise the server starts. Both modes register services with one function
(`ServiceRegistration.AddCtxUi`), because they must see the same database
and the same encryption keys.

`ServerApp.Build` takes a callback. Tests need it to replace the port, the
data directory and the clock. `ServerApp.InitializeAsync` applies migrations
and loads the routes and the panel path before the start, so the first
request never sees an empty table.

The application name in `ServerApp.Build` is set explicitly. ASP.NET Core
uses it to find the static files manifest, and under the test host the name
would be `testhost`. `UseStaticWebAssets` is always called: when running from
source and in tests the MudBlazor files live in the NuGet cache rather than
in `wwwroot`; in the published program the call does nothing.

The command writes its log to stderr so its output can be parsed by a
script.

`DomainException` means a domain rule was broken. Its text is shown to the
administrator as is, so it is written in Russian and without internal
details.

## Routing table

The gateway does not go to the database on every request. It reads a
`RoutingSnapshot`: an immutable table of routes and clients. After a change
in the database a new snapshot is built and replaces the old one entirely;
`SnapshotStore` hands out the current one without locks.

For the server to learn about a change made by another process, the database
has a single row with a change counter (`RoutingState`):

- any tracked change in `CtxUiDbContext` increments the counter. The
  increment happens in SQL rather than in memory, so two processes do not
  overwrite each other's changes;
- `SnapshotRefresher` compares the counter with the revision of the loaded
  snapshot every `RefreshInterval` and rereads the table only if they
  differ;
- the loader reads the revision before the data. If the data changes
  between the two reads, the snapshot gets the old revision with the new
  data and is reread on the next check. The opposite order could miss a
  change;
- before the first load the snapshot has revision `-1`: it matches no
  revision in the database.

The client's last-use timestamp bypasses change tracking and goes through
`ExecuteUpdateAsync`. It does not increment the counter and does not make
the server reread the table on every request.

## The token must not reach the log

An address like `/k/<token>/mcp/<slug>` exists for clients that cannot set
headers. The middleware that cuts the token out of the path is first in the
pipeline, so routing and the log never see the token. The pipeline order is
assembled by `ServerApp.Build` and must not be changed.

Log categories that write the original request address or are noisy on
every request are silenced in code (`AddCtxUiFilters`) rather than in
`appsettings.json`. This way the protection against token leaks does not
depend on a settings file the administrator might replace.

The request log entry carries only the slug of a known inbound. An arbitrary
string from the address never reaches the log.

The plain token exists only in the `IssuedClient` value returned at issue
time. The database holds its hash and an encrypted copy.

## Forwarding to the upstream

`HttpUpstream` forwards the request transparently: the body and the response
stream through without being parsed. That is why any MCP protocol revision
and SSE responses work.

The upstream address is taken from the inbound as a whole. The client's path
and query string do not get into it.

The client token is meant for the gateway and is not passed on. Identity
headers (`X-Mcp-Client-*`, `X-Mcp-Meta-*`) are set only by the gateway: those
sent by the client are stripped before the real ones are added. HTTP headers
carry only ASCII, so the client name and metadata values are percent-encoded
UTF-8.

## Clients

A client name is unique regardless of case. The uniqueness key
(`ClientName.Key`) is stored in a separate column, because SQLite compares
case-insensitively only for Latin letters, while names are usually Cyrillic.
The key is built with NFKC normalization and upper-casing, which requires
the ICU library.

When a client's set of inbounds changes, the set is edited by difference:
removing and re-adding the same link in one context conflicts on the
composite key.

In `ClientUpdate`, `ExpiresAt = null` means "never expires" only with
`ChangeExpiry = true`; `InboundSlugs = null` means "leave the set as is".

Client use is not recorded on every request: `TouchThrottle` decides whether
it is time to write. A failure to write statistics must not break the
client's request, so `ClientTouch` does not throw.

The date in `--expires 2027-07-01` means midnight UTC: access closes at that
moment. Default parsing would take midnight by the server clock and shift
the expiry by the time zone.

## Limits and quota

The order of checks in `GatewayHandler`: token and access, rate,
concurrency, quota. Each check costs more than the previous one, so the
request body is read only after the first three.

**Rate and concurrency.** `ClientLimiter` keeps one limiter from
`System.Threading.RateLimiting` per client. The partition key is made of the
client and the limit value, so a new value applies from the next request,
and the library disposes of the old limiter by itself. The rate is counted
by a token bucket: its capacity equals the limit and it refills evenly
rather than once a minute. The gateway computes the `Retry-After` hint
itself as the time until one request comes back: for any bucket the library
would say a whole minute.

The limiters run on the system clock rather than `TimeProvider`: the library
accepts no other. That is why the refill test really waits.

Concurrency is counted only for `POST`. An MCP client keeps a `GET` stream
open for notifications the whole time it runs; if the stream took a slot, a
client with a limit of 1 could never make a call. The rate counts requests
of every method.

**Request body.** To learn the method, the gateway reads the `POST` body into
memory in full, but no more than `Inbound.MaxBodyBytes` (4 MB by default,
editable in the panel), and gives the upstream the same bytes. The response
still streams.

`RpcEnvelope` takes only `id`, `method` and `params.name` from the body and
rejects anything two parsers could read differently: a repeated field, a
second document after the first, nesting deeper than 256. Otherwise a call
could be slipped past the quota: the gateway would see `ping` while the
upstream would see `tools/call`. Such a request gets 400 and never reaches
the upstream.

A JSON-RPC batch (an array of messages, protocol revisions before
2025-06-18) charges the quota as many calls as it has `tools/call`, and it
passes either whole or not at all. A batch holds at most 100 messages.

The method name and the tool name come from the client but end up in the
database and on the administrator's screen. `RpcEnvelope` cuts them to 128
characters and replaces control characters. They are never written to the
server log.

**Quota.** The quota period is not stored but computed: `QuotaPeriod.StartAt`
counts a whole number of reset intervals from `Client.PeriodStartedAt`. The
administrator changes only that starting point (the `client reset` command,
a client's first quota, a new reset interval); the server changes only the
`UsedCalls` counter and the `UsedSince` mark of which period the counter
belongs to. Each field has one writer, so the command and the service do not
overwrite each other's changes. A counter whose mark is not the start of the
current period counts as zero: that is how a scheduled reset, a reset by the
administrator and a start after the period changed all work.

Changing the quota size does not touch the period: what was used stays used.

The counters live in memory, in `QuotaLedger`. `QuotaSaver` writes the ones
that changed every `FlushInterval` and at shutdown through
`ExecuteUpdateAsync`, without incrementing the routing revision. At start
the counters are read from the database. After a crash no more than one
interval is lost.

A call is charged before forwarding and refunded if the upstream answered
with a status of 400 or more or did not answer. The spec names only 5xx and a
dropped connection; 4xx was added because the call did not run either, and a
common cause, a wrong upstream header, does not depend on the client. A call
the client aborted is not refunded: the upstream may have run it.

A quota refusal arrives as HTTP 200 with a tool result and `isError: true`.
MCP clients show an HTTP error as "server unavailable", while a tool result
is read and relayed by the model. The `id` is copied from the request byte
for byte, so large numbers and escaping stay intact. The other requests of
the same batch get JSON-RPC error `-32000`; a message without an `id` gets 202
with no body.

## Call log

The gateway puts call records into an in-memory queue (`UsageJournal`).
`UsageWriter` writes them in a batch every `FlushInterval` and at shutdown:
rows into `UsageEvents` and day counters into `UsageDays` in one
transaction. If the queue of 10,000 records is full, new records are lost,
and a warning with their number goes to the server log: accounting must not
stop requests.

Every message with a method gets a log row. Refusals the gateway makes
before reading the method (rate, concurrency, a body too large or
unreadable) go only into the day counter. Otherwise a client could fill the
log with requests the gateway never even served. `GET` streams and `DELETE`
session closes are not counted.

The `UpstreamRejected` outcome means a 4xx response; `UpstreamFailed` means
5xx or no response. The gateway does not see a JSON-RPC error inside a 200
response: it does not parse responses.

The accounting tables are not linked to clients and inbounds by foreign
keys. A batch write does not fail if a client was removed a second ago, and
the history of removed ones stays: `usage log` writes «удалён» (removed)
instead of the name.

Only changes to inbounds, clients and their links increment the routing
revision. Otherwise every log batch would make the server reread the routing
table.

Rows older than `UsageRetentionDays` are deleted once an hour; day counters
are kept forever. Days are counted in UTC.

## Panel

**Secret path.** The panel lives under a random 16-character path. It is
stored in the `Settings` table, created on first access and read by the
server once at start, so replacing the path needs a restart. `PanelGate`
stands in the pipeline right after the token is cut out: a request whose
path starts with the secret goes on without it (the prefix moves to
`PathBase`), a request to `/mcp` passes as is, and everything else gets 404
with no body. The list of open addresses is set in `ServerApp.Build`. A new
address that is not on that list stays closed rather than opening by
accident.

Like the token, the secret path is cut out of the address before routing and
never reaches the server log. The login cookie is bound to the secret path
and the browser does not send it outside of it.

**Sign-in.** Passwords, lockout and the cookie are handled by ASP.NET Core
Identity on top of a single table of our own, `Admins`. For that there is
the `AdminStore` store instead of the ready-made EF Core one: that would
bring an ASP.NET package and four tables into `CtxUi.Core` for the sake of
one row. Accounts are created by `AdminAccounts`, which needs only the
password hasher from Identity, so the `ctx-ui admin` command works without a
web server.

The program issues the administrator's password: 20 random letters and
digits. A password of one's own cannot be set yet, so guessing is pointless,
and the lockout after five mistakes is a second line of defence. Identity
counts the lockout time by the system clock rather than `TimeProvider`: the
second exception to the rule after the rate limiters. The failure counter is
incremented by a read and a write rather than by one SQL statement: with
parallel guessing there may be slightly more than five attempts.

The form answers a wrong password, an unknown login and a locked login the
same way, so the answer does not reveal whether such a login exists.

The sign-in and sign-out pages are rendered on the server without
interactivity: a cookie can be set only in an HTTP response. The other pages
are interactive, without prerendering, so data is not loaded twice.

The «Выйти» (sign out) button does a full load of the sign-out page. A
regular link would not work: the panel has a "no such page" page for any
unknown address, and the interactive router would give it the sign-out
address too, because it does not see non-interactive pages.

**Sessions.** The cookie holds the administrator's `SecurityStamp`. A new
password and removing the administrator change or remove it. Every
`SessionCheckInterval` the stamp is checked both by the cookie handler and by
the open page (`AdminSessionState`): an open page needs no HTTP requests, so
without its own check it would live until the tab is closed.

Permissions are checked twice: on the endpoints (`RequireAuthorization`,
which includes the WebSocket connection) and on the pages (`[Authorize]` in
`Pages/_Imports.razor`). The second is needed so an open page closes when the
session stops being valid.

**Data access.** The services in `CtxUi.Core` are meant for a single request,
while an open page lives for hours and handles events in parallel. So pages
do not get the services directly; they ask `PanelBackend` to run an action
in a fresh scope: `ReadAsync` for reading, `ChangeAsync` for changes. After
the action `ChangeAsync` rereads the routing table at once through
`IRoutingRefresh`, without waiting for `RefreshInterval`. The interface is
declared in `CtxUi.Core` and implemented in the gateway: the panel knows
nothing about the gateway.

**Secrets on screen.** Upstream header values are not loaded into the panel:
the edit window gets only their names. The «Подключение» (connection) window
shows the client token on purpose: that is what it is for. The tool name and
the method in the call log come from clients; Blazor renders them as text,
so markup in them never runs.

**Behind a reverse proxy.** The service trusts the `X-Forwarded-Proto`
header from any sender: it gives the cookie the `Secure` flag and keeps
redirects on `https`. A stranger who forges the header affects only the
responses to themselves. The sender address from `X-Forwarded-For` is not
read.

**No external addresses.** The service serves the panel styles and scripts
itself, and no fonts are pulled from other servers: the panel works in a
closed network and tells no one its address.

Times in the panel are shown in UTC and labelled so, as on the command line.

## Storage

SQLite cannot compare `DateTimeOffset`, so time is stored as a number of
milliseconds UTC (`UnixMillisConverter`).

`ISecretVault` is reversible encryption for secrets that must be shown or
used again: token copies and upstream headers. `Open` returns `null` if the
value cannot be decrypted: the keys are lost or the data is damaged. Reading
an inbound's headers behaves the same way, and such an inbound drops out of
the routing table with a warning while the server keeps running.

`DesignTimeFactory` is needed only by the `dotnet ef` command, to build the
model without starting the server.

Generated migrations in `Data/Migrations` are stripped of the comments the
generator writes (`// <auto-generated />`, `/// <inheritdoc />`): the code has
no comments anywhere.

## Tests

- `TestDatabase`: an in-memory SQLite with migrations applied. It lives as
  long as the connection is open.
- `FakeVault.Broken` imitates lost keys: nothing sealed opens any more.
- `GatewayHost`: the gateway on a free port with its own temporary data
  directory. Data can be changed in two ways: through the services with
  immediate effect, and the way another process would, without refreshing
  the routing table.
- `FakeUpstream`: a real HTTP server on a free port that remembers the
  requests it got.
- `CapturingLogs` collects everything the server writes to the log so a test
  can look for token leaks.
- `Eventually` waits until a condition becomes true. It is needed where a
  background task applies a change.
- `Pipeline` runs a request through the token cutting without a server.
- `McpEndToEndTests` puts a real MCP server behind the gateway and a real MCP
  client in front of it.
- `GatewayHost.RestartAsync` stops the server and starts it on the same data
  directory. This checks that counters and the log survive a restart.
- `CliRunner` runs the `ctx-ui` command inside a test on a given data
  directory, including the directory of a running `GatewayHost`.
- A replaced clock moves expiry and the quota period. The rate limiters run
  on the real clock, so their tests do not wait a minute but check the
  refusal right after the bucket runs out.
- `PanelClient` talks to the panel over HTTP like a browser without scripts:
  it keeps cookies, does not follow redirects and takes the anti-forgery
  token from the form itself. It checks sign-in, sign-out and the secret
  path.
- `PanelSession` opens the panel in a real Chromium through Playwright. There
  is one browser for the whole run, downloaded on the first run, and each test
  gets a clean profile of its own. Tests find elements by roles and labels,
  the way a screen reader finds them, not by MudBlazor classes. The system
  libraries for Chromium are installed in CI by a separate workflow step; the
  `playwright` version in it must match the version of the
  `Microsoft.Playwright` package in `Directory.Packages.props`.
- Tests of "at once, without a restart" start the server with a
  `RefreshInterval` of one hour. Otherwise the background check every 50 ms
  would apply the change itself, and the test could not tell a panel that
  refreshes the routes from one that does not.
- Panel tests do not replace the clock: the browser checks the cookie
  expiry against its own clock. An expired client is made by editing the
  date right in the database.

Temporary test directories are deleted at the end. A deletion error is
ignored: if an antivirus or the OS holds the directory, it goes at the next
temp folder cleanup.

## Build and delivery

**ICU inside the archive.** A self-contained binary needs ICU, and minimal
servers do not have it: without it the program crashes at start. The
invariant mode does not fit, because NFKC normalization does not work in it
and client name keys would come out different. So a self-contained publish
puts its own ICU into the archive (the `Microsoft.ICU.ICU4C.Runtime`
package, a `SelfContained` condition in `CtxUi.Server.csproj`). The Docker
image uses the ICU of the base image.

**Libraries next to the binary.** `libe_sqlite3.so` and ICU lie as files
rather than being bundled inside. Bundled libraries are extracted at start
into the home directory, which the `ctx-ui` user does not have.

**Working directory.** The program looks for `appsettings.json` in the
current directory. It is fixed by the unit (`WorkingDirectory`), the
management command (`cd`) and the image (`WORKDIR`). The program does not
guess paths itself, so `dotnet run` in development behaves as before.

**Management command.** `/usr/local/bin/ctx-ui` reads `/etc/ctx-ui/ctx-ui.env`
and runs the binary as the `ctx-ui` user. Otherwise a command run as root
would leave files in the data directory that the service cannot read. The
settings file is parsed line by line and not executed as shell.

**Panel static files.** Styles and scripts lie in `wwwroot` next to the
binary, and their list in `ctx-ui.staticwebassets.endpoints.json`. The
service does not start without that file, so packaging does not delete it.
The Blazor script gets into `wwwroot` only with `RequiresAspNetWebAssets` in
`CtxUi.Server.csproj`: the SDK adds it by itself only when `.razor` files are
in the same project, and ours are in `CtxUi.Panel`.

**Archive check in the installer.** The presence of the binary is checked as
the presence of a file rather than an execute permission: on servers with
`/tmp` mounted `noexec`, the execute permission check is always negative.

**Image.** The build stage runs on the runner's platform and publishes for
the target architecture. The final stage has no `RUN` commands, so the arm64
image is built on x64 without emulation. `.dockerignore` sits next to the
Dockerfile as `Dockerfile.dockerignore` so as not to take up the root.

**One packaging script.** `deploy/package.sh` builds all release files. Both
the `release` workflow and `deploy/tests/linux.sh` use it, so the installer
is checked on the same archives that go into the release.
