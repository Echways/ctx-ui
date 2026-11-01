# Panel extensions

An extension adds sections, blocks and actions to the ctx-ui panel, changes its
navigation and look, and brings its own services, endpoints and database. It is
built into a zip archive. An administrator installs the archive with one command
and restarts the service; ctx-ui itself is not rebuilt.

[English](extensions.md) · [Русский](ru/extensions.md)

## Your first extension in 5 minutes

You need the .NET 10 SDK and a ctx-ui server binary.

```bash
dotnet new install CtxUi.Extensions.Templates
dotnet new ctxui-extension -n Acme.Reports --id acme-reports
cd Acme.Reports

export CTXUI_BINARY=/opt/ctx-ui/ctx-ui
dotnet build -t:CtxUiPreview
```

`CtxUiPreview` runs the server from its own folder, so keep the files of the server archive next to the binary. It publishes the extension into `obj/ctxui-preview/`, starts the panel
with it on `http://127.0.0.1:5199` and prints the panel address, login and
password. Data stays between runs. Stop it with Ctrl+C, change the code, run it again.
Another port: `-p:CtxUiPreviewPort=5200`.

Build the archive when it is ready:

```bash
dotnet publish -c Release
```

The archive `acme-reports-1.0.0.zip` appears next to the `publish` folder. The
version comes from `<Version>` in the project.

## Building blocks

Everything starts in `Register`. It runs once at server start.

```csharp
public sealed class ReportsExtension : IPanelExtension
{
    public void Register(ExtensionContext context)
    {
        context.Panel.AddSection<ReportsPage>(() => Text.Reports);
    }
}
```

`ExtensionContext` gives you `Id`, `Panel`, `Services`, `Configuration` and
`DataDirectory`.

### Sections and captions

A new section stands before the "Access" caption unless you give a position.
Consecutive additions of one extension keep their order.

```csharp
context.Panel
    .AddCaption(() => Text.Billing)
    .AddSection<InvoicesPage>(() => Text.Invoices)
    .AddSection<PaymentsPage>(() => Text.Payments, setup: section => section.Icon(Icons.Payments))
    .AddSection<StatusPage>(() => Text.Status, PanelPosition.After(PanelSections.Overview));
```

Pages are Razor components with `@page`. Every page requires an administrator
sign-in. `PanelSections.Of<TPage>()` names your own section so you can pass it
to `Hide`, `Move` or `Rename`.

### Move, rename, hide

```csharp
context.Panel
    .Move(PanelSections.Journal, PanelPosition.Before(PanelSections.Overview))
    .Rename(PanelSections.Clients, () => Text.Customers)
    .Hide(PanelSections.Metrics);
```

Entries: `Overview`, `Metrics`, `Inbounds`, `Clients`, `Journal`, `AccessCaption`,
`OAuth`, `ApiKeys`. Hiding removes the navigation item, the route stays open
because other sections link to it. A caption with no sections left disappears.
An unknown entry stops the start with an error naming it.

### Slots

A slot is a typed place on a built-in page. The compiler checks that a block
matches the context of its slot.

| Slot | Kind | Context | Where |
| --- | --- | --- | --- |
| `PanelSlots.PageTop(section)` | block | `PanelPage` | under the page head |
| `PanelSlots.PageBottom(section)` | block | `PanelPage` | end of the page |
| `PanelSlots.PageActions(section)` | action | `PanelPage` | buttons in the page head |
| `PanelSlots.ClientActions` | action | `ClientSummary` | client row menu, before "Delete" |
| `PanelSlots.ClientSheet` | block | `ClientSummary` | end of the client form, existing clients only |
| `PanelSlots.InboundActions` | action | `InboundSummary` | inbound row menu, before "Delete" |
| `PanelSlots.InboundSheet` | block | `InboundSummary` | end of the inbound form, existing inbounds only |
| `PanelSlots.ApiKeyActions` | action | `ApiKeySummary` | API key row menu, before "Revoke" |

`section` is one of the `PanelSections` constants except `AccessCaption`.

### Blocks

```razor
@inherits PanelBlock<ClientSummary>
@inject IPanelData Data

<p>@Context.Name: @Context.UsedCalls</p>
```

```csharp
context.Panel.In(PanelSlots.ClientSheet).AddBlock<UsageBlock>();
```

Blocks are sorted by `order`, then by extension id, then by registration order.
A block that throws shows an error inside its own frame; the page keeps working.
Load data when the block is shown, not in `Register`.

### Actions

```csharp
context.Panel
    .In(PanelSlots.ClientActions).AddAction(() => Text.Notify, args =>
        args.Services.GetRequiredService<Notifier>().SendAsync(args.Context.Id))
    .In(PanelSlots.ClientActions).AddSheet<ReportsSheet>(() => Text.Reports, client => client.Name);
```

`AddAction` receives `PanelActionArgs<TContext>`: `Context`, `Dialogs` and
`Services`. An exception shows an error message and is logged. To show your own
message, throw `Enfilade.PanelException`. `AddSheet` opens a block in a side
sheet and passes the row as `Context`. A row context is built only on click, so
the menu costs nothing until it is used.

### Look and styles

```csharp
context.Panel
    .Customize(look =>
    {
        look.Brand = "Acme";
        look.Accent = "#0a7d4f";
        look.Favicon = "_content/Acme.Reports/favicon.svg";
        look.Appearance = Appearance.Light;
    })
    .AddStylesheet("acme.css");
```

`AddStylesheet` takes a path inside `wwwroot` of the extension. Your stylesheets
load after `panel.css`, so they can override it and the Enfilade design tokens.
`.razor.css` files work as usual and are connected automatically. Only these four
look settings can be changed: the sign-in settings of the panel stay out of reach.

### Panel data

`IPanelData` is a singleton: clients, inbounds and daily usage, without secrets.
Tokens, upstream addresses and upstream headers are not part of it.

```csharp
var clients = await data.ClientsAsync();
await data.SetClientEnabledAsync(clients[0].Id, false);
var days = await data.UsageDaysAsync(from, to, clients[0].Id);
```

Changes take effect in the gateway at once.

### Services and settings

Register services in `context.Services`, including `AddHostedService`.
`context.Configuration` is the section `CtxUi:Extensions:<id>`:

```bash
CtxUi__Extensions__acme-reports__Url=https://reports.example.com
```

### Endpoints

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    endpoints.MapGet("/status", (Notifier notifier) => notifier.Status);
}
```

They live under `/<panel path>/extensions/<id>/` and require a signed-in
administrator or an API key.

### Start-up work

Implement `IExtensionInitializer` and register it. It runs once at start, before
the server accepts requests, in its own scope.

```csharp
context.Services.AddScoped<IExtensionInitializer, ImportDefaults>();
```

### Database

```csharp
context.AddDatabase<ReportsDatabase>();
```

This registers the context on SQLite in `<DataDirectory>/data.db` and applies
migrations at every start. Add the design package and a factory to create migrations:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.*" PrivateAssets="all" />
```

```csharp
public sealed class ReportsDatabaseFactory : IDesignTimeDbContextFactory<ReportsDatabase>
{
    public ReportsDatabase CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ReportsDatabase>().UseSqlite("Data Source=design.db").Options);
}
```

```bash
dotnet ef migrations add Initial
```

Keep other files of the extension in `context.DataDirectory`.

## Rules

- The id matches `^[a-z][a-z0-9-]{0,31}$`, does not repeat a path of the server
  (`login`, `clients`, `api` and so on) and equals the folder name.
- All `@page` routes stand under `/<id>`. An extension that breaks this is not loaded.
- Exactly one class implements `IPanelExtension`, with a public constructor
  without parameters.
- Start a group of sections with your own caption, so the user sees where they come from.
- Out of reach: the sign-in page and built-in page content outside slots.

## Versions and compatibility

The manifest of a package records the extension API version, currently `1.0`. A
server loads an extension when the major version equals its own and the minor
version is not higher. An incompatible extension is skipped with an error in the
log and the server keeps working.

The server decides the versions of these libraries, and extensions use its copies:
`CtxUi.Extensions`, `Enfilade`, `MudBlazor`, `Jeffijoe.MessageFormat`, ASP.NET Core,
`Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore*`, `Microsoft.Data.Sqlite`,
`SQLitePCLRaw`, `System.*`. Any other library stays private to the extension and can
differ from the version on the server. Native libraries of an extension are not
officially supported, nor are static files of private Razor libraries.

## Testing

`CtxUi.Extensions.Testing` starts a real server as a separate process with your
extension installed. Point `CTXUI_BINARY` at a server binary.

```csharp
await using var panel = await TestPanel.StartAsync(new TestPanelOptions(extensionsDir));
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync();
var page = await browser.NewPageAsync();

await panel.SignInAsync(page);
await page.GetByRole(AriaRole.Link, new() { Name = "Reports" }).ClickAsync();
Assert.DoesNotContain("fail:", panel.Log);
```

`extensionsDir` holds a folder `<id>` with the published extension. `Settings` pass
configuration, `CliAsync` runs ctx-ui commands against the same data. Data lives in
a temporary folder, the port is free, an administrator is created for you.

## Install, update, remove

```bash
sudo ctx-ui extension install acme-reports-1.0.0.zip
sudo systemctl restart ctx-ui
ctx-ui extension list
```

The command runs as the service user, so keep the archive where that user can read it, for example in `/tmp`, not in a private home folder.

`list` shows one of `ok`, `disabled`, `incompatible`, `invalid`. Installing a new
version replaces the old one and keeps its data. A broken archive leaves the
installed version intact.

```bash
sudo ctx-ui extension remove acme-reports
sudo ctx-ui extension remove acme-reports --purge
```

The second form also deletes the data of the extension. Restart after every change;
there is no hot reload and no installation from the panel.

In Docker, mount an unpacked extension into `/data/extensions/<id>`, or run
`ctx-ui extension install` inside the container and restart it.

Files live in `<DataDir>/extensions/<id>/`, data in `<DataDir>/extension-data/<id>/`.
`ctx-ui backup` includes both and copies `*.db` files consistently while the server
is writing to them. The folder can be moved with `CtxUi__ExtensionsDir`.

An extension can be turned off without removing it:

```bash
CtxUi__DisabledExtensions=acme-reports,other
```

An exception in `Register`, `MapEndpoints` or an initializer stops the server start.
The message names the extension and this setting.

## Security and reliability

An extension runs with the full rights of the server process. .NET has no sandbox
inside a process. It can read the database and the encryption keys and act on behalf
of every client. Install only what you trust.

An unhandled exception in a `BackgroundService` stops the whole server. Catch
exceptions inside background loops.

Endpoints accept the administrator cookie, so treat any state-changing endpoint as
you would a panel action.

## License

Extensions use the GPL-3.0 packages `CtxUi.Extensions*`, so an extension is licensed
under GPL-3.0. Whoever receives the program also receives its source code. You may
charge for development and you do not have to publish the code.
