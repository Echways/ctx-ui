# Расширения панели

[English](../en/extensions.md) · [Русский](extensions.md) · [Документация](README.md)

Расширение добавляет в панель ctx-ui разделы, блоки и действия, меняет её навигацию и внешний вид, приносит свои сервисы, эндпоинты и базу данных. Оно собирается в zip-архив. Администратор ставит архив одной командой и перезапускает службу, сам ctx-ui пересобирать не нужно.

## Первое расширение за 5 минут

Нужны .NET 10 SDK и исполняемый файл сервера ctx-ui.

```bash
dotnet new install CtxUi.Extensions.Templates
dotnet new ctxui-extension -n Acme.Reports --id acme-reports
cd Acme.Reports

export CTXUI_BINARY=/opt/ctx-ui/ctx-ui
dotnet build -t:CtxUiPreview
```

`CtxUiPreview` публикует расширение в `obj/ctxui-preview/`, запускает с ним панель на
`http://127.0.0.1:5199` и печатает адрес панели, логин и пароль. Данные сохраняются между
запусками. Остановите его через Ctrl+C, измените код и запустите снова. Другой порт:
`-p:CtxUiPreviewPort=5200`. Сервер запускается из своей папки, поэтому файлы архива
сервера должны лежать рядом с исполняемым файлом.

Когда расширение готово, соберите архив:

```bash
dotnet publish -c Release
```

Архив `acme-reports-1.0.0.zip` появится рядом с папкой `publish`. Версия берётся из
`<Version>` в проекте.

## Блоки расширения

Всё начинается в `Register`. Метод выполняется один раз при старте сервера.

```csharp
public sealed class ReportsExtension : IPanelExtension
{
    public void Register(ExtensionContext context)
    {
        context.Panel.AddSection<ReportsPage>(() => Text.Reports);
    }
}
```

`ExtensionContext` даёт `Id`, `Panel`, `Services`, `Configuration` и `DataDirectory`.

### Разделы и подписи

Новый раздел встаёт перед подписью «Доступ», если не указано другое место.
Несколько добавлений подряд от одного расширения сохраняют порядок.

```csharp
context.Panel
    .AddCaption(() => Text.Billing)
    .AddSection<InvoicesPage>(() => Text.Invoices)
    .AddSection<PaymentsPage>(() => Text.Payments, setup: section => section.Icon(Icons.Payments))
    .AddSection<StatusPage>(() => Text.Status, PanelPosition.After(PanelSections.Overview));
```

Страницы это Razor-компоненты с `@page`. Каждая страница требует входа администратора.
Раздел для страницы не обязателен: страница подробностей открывается по своему адресу.
`PanelSections.Of<TPage>()` называет ваш раздел, чтобы передать его в `Hide`, `Move`
или `Rename`.

### Перестановка, переименование, скрытие

```csharp
context.Panel
    .Move(PanelSections.Journal, PanelPosition.Before(PanelSections.Overview))
    .Rename(PanelSections.Clients, () => Text.Customers)
    .Hide(PanelSections.Metrics);
```

Пункты: `Overview`, `Metrics`, `Inbounds`, `Clients`, `Journal`, `AccessCaption`,
`OAuth`, `ApiKeys`. Скрытие убирает пункт навигации, маршрут остаётся открыт, потому
что на него ведут ссылки из других разделов. Подпись без оставшихся разделов исчезает.
Неизвестный пункт останавливает старт с ошибкой, в которой он назван.

### Слоты

Слот это типизированное место на встроенной странице. Компилятор проверяет, что блок
подходит к контексту слота.

| Слот | Вид | Контекст | Где |
| --- | --- | --- | --- |
| `PanelSlots.PageTop(section)` | блок | `PanelPage` | под заголовком страницы |
| `PanelSlots.PageBottom(section)` | блок | `PanelPage` | в конце страницы |
| `PanelSlots.PageActions(section)` | действие | `PanelPage` | кнопки в заголовке страницы |
| `PanelSlots.ClientActions` | действие | `ClientSummary` | меню строки клиента, перед «Удалить» |
| `PanelSlots.ClientSheet` | блок | `ClientSummary` | конец формы клиента, только существующего |
| `PanelSlots.InboundActions` | действие | `InboundSummary` | меню строки inbound'а, перед «Удалить» |
| `PanelSlots.InboundSheet` | блок | `InboundSummary` | конец формы inbound'а, только существующего |
| `PanelSlots.ApiKeyActions` | действие | `ApiKeySummary` | меню строки ключа API, перед «Отозвать» |

`section` это одна из констант `PanelSections`, кроме `AccessCaption`. Любое другое
значение останавливает запуск с ошибкой, где оно названо.

### Блоки

```razor
@inherits PanelBlock<ClientSummary>
@inject IPanelData Data

<p>@Context.Name: @Context.UsedCalls</p>
```

```csharp
context.Panel.In(PanelSlots.ClientSheet).AddBlock<UsageBlock>();
```

Блоки сортируются по `order`, затем по id расширения, затем по порядку регистрации.
Блок, бросивший исключение, показывает ошибку в своей рамке и попадает в лог, а страница продолжает
работать. Загружайте данные при показе блока, а не в `Register`.

### Действия

```csharp
context.Panel
    .In(PanelSlots.ClientActions).AddAction(() => Text.Notify, args =>
        args.Services.GetRequiredService<Notifier>().SendAsync(args.Context.Id))
    .In(PanelSlots.ClientActions).AddSheet<ReportsSheet>(() => Text.Reports, client => client.Name);
```

`AddAction` получает `PanelActionArgs<TContext>`: `Context`, `Dialogs` и `Services`.
Когда действие завершится, страница заново загрузит данные, так что изменение видно
сразу. Исключение показывает сообщение об ошибке и попадает в лог. Чтобы показать своё
сообщение, бросьте `Enfilade.PanelException` или зарегистрируйте свой тип исключения
в `Register` через `context.Services.ShowMessageOf<TException>(failure => failure.Message)`.
`AddSheet` открывает блок в боковой панели и передаёт строку в `Context`; панель
закрывается словом «Закрыть», клавишей Escape или кликом мимо неё. Контекст строки
создаётся только при клике, так что меню ничего не стоит, пока им не пользуются, если у
действия нет предиката `When`.

`When` ограничивает следующий `AddAction` или `AddSheet` синхронным предикатом
`Func<TContext, bool>`. Меню строки и кнопки страницы показывают такое действие, только
если предикат возвращает true для строки. Предикат должен быть быстрым и
работать из памяти: он вызывается при каждой отрисовке. Предикат, который бросил
исключение, скрывает своё действие и попадает в лог.

```csharp
context.Panel.In(PanelSlots.InboundActions)
    .When(inbound => known.Contains(inbound.Id))
    .AddAction(() => Text.Open, Open);
```

### Внешний вид и стили

```csharp
context.Panel
    .Customize(look =>
    {
        look.Brand = "Acme";
        look.Accent = "#0a7d4f";
        look.Favicon = "_content/Acme.Reports/favicon.svg";
        look.Appearance = Appearance.Light;
    })
    .AddStylesheet("acme.css")
    .AddAccountLink(() => Text.Help, "https://help.example.com");
```

`AddStylesheet` принимает путь внутри `wwwroot` расширения. Ваши стили загружаются
после `panel.css`, поэтому могут переопределить его и токены оформления Enfilade.
Файлы `.razor.css` работают как обычно и подключаются сами. Менять можно только эти
четыре настройки: настройки входа в панель недоступны. `AddAccountLink` добавляет
ссылку рядом со ссылкой выхода.

### Данные панели

`IPanelData` это singleton: клиенты, inbound'ы и использование по дням, без секретов.
Токенов, адресов и заголовков upstream там нет.

```csharp
var clients = await data.ClientsAsync();
await data.SetClientEnabledAsync(clients[0].Id, false);
var days = await data.UsageDaysAsync(from, to, clients[0].Id);
```

С версии API `2.0` расширение также создаёт HTTP inbound'ы и связывает с ними клиентов.
`SetInboundClientsAsync` заменяет клиентов одного inbound'а и не трогает другие их
inbound'ы:

```csharp
await data.AddHttpInboundAsync(new HttpInboundDraft("built", "Built", "https://mcp.example.com/mcp"));
await data.SetInboundClientsAsync("built", [clients[0].Id]);
var linked = await data.InboundClientsAsync("built");
await data.RemoveInboundAsync("built");
```

Занятый slug, неверный адрес или неизвестный клиент дают исключение с локализованным
сообщением панели. Enfilade сам показывает это сообщение пользователю. Чтобы назвать
причину своими словами, перехватите исключение и прочитайте сообщение через
`Enfilade.ErrorMessages.Of(exception)`; для любой другой ошибки оно вернёт `null`. Типы
исключений ядра расширениям недоступны. Изменения сразу действуют в шлюзе.

### Сервисы и настройки

Сервисы регистрируются в `context.Services`, в том числе `AddHostedService`.
`context.Configuration` это секция `CtxUi:Extensions:<id>`:

```bash
CtxUi__Extensions__acme-reports__Url=https://reports.example.com
```

### Эндпоинты

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    endpoints.MapGet("/status", (Notifier notifier) => notifier.Status);
}
```

Они находятся под `/<путь панели>/extensions/<id>/` и требуют вошедшего администратора
или ключ API.

### Работа при старте

Реализуйте `IExtensionInitializer` и зарегистрируйте его. Он выполняется один раз при
старте, до приёма запросов. Инициализаторы выполняются по очереди в одном scope.

```csharp
context.Services.AddScoped<IExtensionInitializer, ImportDefaults>();
```

### База данных

```csharp
context.AddDatabase<ReportsDatabase>();
```

Контекст регистрируется на SQLite в `<DataDirectory>/data.db`, миграции применяются при
каждом старте. Доступны и scoped-контекст, и `IDbContextFactory<TContext>`; компонентам
Blazor лучше брать фабрику и создавать контекст на операцию, потому что scope компонента
живёт столько же, сколько вкладка браузера. Чтобы создавать миграции, добавьте пакет и фабрику:

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

Остальные файлы расширения держите в `context.DataDirectory`.

## Правила

- Id подходит под `^[a-z][a-z0-9-]{0,31}$` (это проверяет сборка), не повторяет пути
  сервера (`login`, `clients`, `api` и другие) и совпадает с именем папки.
- Все маршруты `@page` лежат под `/<id>`. Расширение с другими маршрутами не загружается.
- Ровно один класс реализует `IPanelExtension`, с открытым конструктором без параметров.
- Начинайте группу разделов со своей подписи, чтобы было видно, откуда они.
- Недоступны: страница входа и содержимое встроенных страниц вне слотов.

## Версии и совместимость

Манифест пакета хранит версию API расширений, сейчас `2.0`. Сервер загружает расширение,
если мажорная версия равна его собственной, а минорная не выше. Несовместимое расширение
пропускается с ошибкой в логе, сервер продолжает работать.

Версии этих библиотек определяет сервер, расширения используют его копии:
`CtxUi.Extensions`, `Enfilade`, `MudBlazor`, `Jeffijoe.MessageFormat`, ASP.NET Core,
`Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore*`, `Microsoft.Data.Sqlite`,
`SQLitePCLRaw`, `System.*`. Остальные библиотеки остаются частными для расширения, и их
версия может отличаться от серверной. Нативные библиотеки расширения и статика приватных
Razor-библиотек официально не поддерживаются.

## Тестирование

`CtxUi.Extensions.Testing` запускает настоящий сервер отдельным процессом с вашим
расширением. Укажите в `CTXUI_BINARY` путь к серверу и один раз установите браузер:
`pwsh bin/Debug/net10.0/playwright.ps1 install chromium`.

```csharp
await using var panel = await TestPanel.StartAsync(new TestPanelOptions(extensionsDir));
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync();
var page = await browser.NewPageAsync();

await panel.SignInAsync(page);
await page.GetByRole(AriaRole.Link, new() { Name = "Отчёты" }).ClickAsync();
Assert.DoesNotContain("fail:", panel.Log);
```

В `extensionsDir` лежит папка `<id>` с опубликованным расширением, например после
`dotnet publish -o extensions/acme-reports`. `Settings` передают настройки, `CliAsync`
выполняет команды ctx-ui на тех же данных. Панель говорит по-русски, другой язык задаёт
`Language`. Данные лежат во временной папке, порт свободный, администратор создаётся сам.

## Примеры

- Чтобы начать новое расширение, возьмите шаблон из раздела
  [Первое расширение за 5 минут](#первое-расширение-за-5-минут):
  `dotnet new ctxui-extension`.
- [Конструктор серверов](builder.md) это большой настоящий пример, построенный только
  на публичном контракте: [`extensions/builder/CtxUi.Builder`](../../extensions/builder/CtxUi.Builder).
  В нём есть разделы и страницы, слоты на странице инбаундов, база данных, фоновые
  службы, эндпоинты и методы инбаундов в `IPanelData`.

## Установка, обновление, удаление

```bash
sudo ctx-ui extension install acme-reports-1.0.0.zip
sudo systemctl restart ctx-ui
ctx-ui extension list
```

Команда работает от пользователя службы, поэтому архив должен лежать там, где он доступен этому пользователю: например, в `/tmp`, а не в закрытой домашней папке.

`install` проверяет архив по тем же правилам, что и сервер: архив, который сервер бы
пропустил, отклоняется с указанием причины, а установленная версия остаётся на месте.
Новая версия заменяет старую и сохраняет её данные.

`list` показывает одно из состояний: `ok`, `disabled`, `incompatible`, `invalid`, а если
сервер не загрузит расширение, то и причину.

```bash
sudo ctx-ui extension remove acme-reports
sudo ctx-ui extension remove acme-reports --purge
```

Вторая форма удаляет и данные расширения. После каждого изменения нужен перезапуск:
горячей загрузки и установки из панели нет.

В Docker смонтируйте распакованное расширение в `/data/extensions/<id>` или выполните
`ctx-ui extension install` внутри контейнера и перезапустите его.

Файлы лежат в `<DataDir>/extensions/<id>/`, данные в `<DataDir>/extension-data/<id>/`.
`ctx-ui backup` включает и то и другое и копирует файлы `*.db` целостно, даже пока сервер
в них пишет. Каталог можно перенести настройкой `CtxUi__ExtensionsDir`.

Расширение можно отключить, не удаляя:

```bash
CtxUi__DisabledExtensions=acme-reports,other
```

Исключение в `Register`, `MapEndpoints` или инициализаторе останавливает старт сервера.
В сообщении названы расширение и эта настройка.

## Безопасность и надёжность

Расширение работает с полными правами процесса сервера. В .NET нет песочницы внутри
процесса. Расширение может читать базу данных и ключи шифрования и действовать от имени
любого клиента. Ставьте только то, чему доверяете.

Необработанное исключение в `BackgroundService` останавливает весь сервер. Ловите
исключения внутри фоновых циклов.

Эндпоинты принимают cookie администратора, поэтому относитесь к меняющим состояние
эндпоинтам как к действиям панели.

## Лицензия

Расширения используют пакеты `CtxUi.Extensions*` под GPL-3.0, поэтому расширение тоже под
GPL-3.0. Кто получает программу, получает и её исходный код. За разработку можно брать
деньги, публиковать код не требуется.
