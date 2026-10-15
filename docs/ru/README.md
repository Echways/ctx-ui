# ctx-ui

Панель и шлюз для раздачи доступа к своим MCP-серверам в духе 3x-ui.
Администратор заводит inbound (адрес и то, что за ним отдаётся), заводит
клиента со сроком и выдаёт ему токен.

Сейчас готовы этапы 1 и 2: inbound'ы на удалённые MCP-серверы, лимиты и
учёт вызовов, управление из командной строки.

## Установка

Бинарник и сервис systemd, Linux x86_64 или aarch64:

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

Docker с Caddy и автоматическим сертификатом, обновление, удаление и
резервная копия описаны в [docs/install.md](docs/install.md).

Сервис сам TLS не поднимает: перед ним ставится Caddy или nginx.

## Управление

Запущенный сервис подхватывает изменения в течение секунды, перезапуск не
нужен.

```bash
sudo ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
sudo ctx-ui client add "Иванов 4419" --inbound learn --expires 2027-07-01
sudo ctx-ui client list
```

В Docker те же команды идут через `docker compose exec ctx-ui ctx-ui …`.

| Команда | Что делает |
| --- | --- |
| `inbound add <slug> --url <адрес>` | inbound на удалённый MCP-сервер; `--header Имя=значение` добавляет заголовок для него, `--forward-identity` передаёт личность клиента |
| `inbound list`, `enable`, `disable`, `remove <slug>` | список и состояние |
| `client add <имя> --inbound <slug>` | выпускает клиента, печатает токен и адреса; `--expires ГГГГ-ММ-ДД`, `--meta ключ=значение`, лимиты `--rate`, `--concurrency`, `--quota`, `--reset-days` |
| `client show <имя>` | токен, адреса, лимиты и расход квоты |
| `client set <имя>` | меняет срок (`--expires <дата>` или `never`), набор inbound'ов и лимиты; `none` снимает лимит |
| `client reset <имя>` | обнуляет счётчик квоты и начинает период заново |
| `client reissue <имя>` | новый токен, старый перестаёт работать |
| `client list`, `enable`, `disable`, `remove <имя>` | список и состояние |
| `usage days`, `usage log` | итоги по дням и последние вызовы; `--client <имя>` оставляет одного клиента |

## Подключение клиента

Токен передаётся заголовком `Authorization: Bearer <токен>` либо, если
клиент не умеет задавать заголовки, прямо в адресе:
`/k/<токен>/mcp/<slug>`. Адрес с токеном равносилен паролю.

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn \
    --header "Authorization: Bearer tnk_..."
```

## Лимиты

У клиента четыре необязательных ограничения. Не заданное ограничение не
действует.

| Параметр | Что ограничивает | Что получает клиент при превышении |
| --- | --- | --- |
| `--rate N` | запросы в минуту, любые | HTTP 429 с заголовком `Retry-After` |
| `--concurrency N` | одновременные запросы `POST` | HTTP 429 с `Retry-After: 1` |
| `--quota N` | вызовы `tools/call` за период | ответ tool с `isError: true` и текстом, когда лимит обновится |
| `--reset-days N` | длину периода квоты; без него квота выдаётся один раз | |

Квоту расходует только `tools/call`. Клиент с исчерпанной квотой
по-прежнему подключается и видит список tools, а отказ приходит как обычный
ответ tool, поэтому модель пересказывает его человеку. Вызов, на который
upstream ответил ошибкой HTTP или не ответил вовсе, в квоту не идёт.

```bash
sudo ctx-ui client set "Иванов 4419" --quota 500 --reset-days 30 --rate 60
sudo ctx-ui client set "Иванов 4419" --rate none
sudo ctx-ui client reset "Иванов 4419"
sudo ctx-ui usage days --client "Иванов 4419"
sudo ctx-ui usage log --limit 20
```

Счётчики и журнал сервис записывает в базу раз в несколько секунд, поэтому
команды показывают их с небольшим отставанием.

## Что получает upstream

Шлюз убирает токен клиента и добавляет заголовки, заданные для inbound'а.
С флагом `--forward-identity` добавляются `X-Mcp-Client-Id`,
`X-Mcp-Client-Name` и `X-Mcp-Meta-<ключ>`; имя и значения закодированы
процентами в UTF-8. Такие же заголовки, присланные самим клиентом, шлюз
вырезает.

## Документация

- [Установка](docs/install.md)
- [Настройки](docs/configuration.md)
- [Как это устроено](docs/internals.md)
- [Выпуск релиза](docs/releasing.md)

## Разработка

Нужен .NET 10 SDK.

```bash
cd src/CtxUi.Server
dotnet run
dotnet run -- inbound list
```

Сервер слушает `http://127.0.0.1:8080` и хранит базу и ключи в каталоге
`data` рядом с проектом.

```bash
dotnet test
```

Интеграционные тесты поднимают шлюз и поддельный upstream на свободных
портах, сеть наружу им не нужна. Проверки установки описаны в
[docs/releasing.md](docs/releasing.md).

## Лицензия

GPL-3.0.
