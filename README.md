# ctx-ui

Панель и шлюз для раздачи доступа к своим MCP-серверам в духе 3x-ui.
Администратор заводит inbound (адрес и то, что за ним отдаётся), заводит
клиента со сроком и выдаёт ему токен.

Сейчас готов этап 1: inbound'ы на удалённые MCP-серверы и управление из
командной строки.

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
| `client add <имя> --inbound <slug>` | выпускает клиента, печатает токен и адреса; `--expires ГГГГ-ММ-ДД`, `--meta ключ=значение` |
| `client show <имя>` | токен и адреса повторно |
| `client set <имя>` | меняет срок (`--expires <дата>` или `never`) и набор inbound'ов |
| `client reissue <имя>` | новый токен, старый перестаёт работать |
| `client list`, `enable`, `disable`, `remove <имя>` | список и состояние |

## Подключение клиента

Токен передаётся заголовком `Authorization: Bearer <токен>` либо, если
клиент не умеет задавать заголовки, прямо в адресе:
`/k/<токен>/mcp/<slug>`. Адрес с токеном равносилен паролю.

```bash
claude mcp add --transport http learn https://mcp.example.com/mcp/learn \
    --header "Authorization: Bearer tnk_..."
```

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
