# Настройки

[English](../configuration.md) · **Русский**

Настройки читаются из переменных окружения. Имена одинаковы для всех
способов установки.

| Способ установки | Файл | Применить |
| --- | --- | --- |
| бинарник | `/etc/ctx-ui/ctx-ui.env` | `sudo systemctl restart ctx-ui` |
| Docker | `.env` рядом с `compose.yaml` | `docker compose up -d` |
| из исходников | `src/CtxUi.Server/appsettings.json` или окружение | перезапуск |

## Переменные

| Переменная | Ключ в `appsettings.json` | По умолчанию | Значение |
| --- | --- | --- | --- |
| `Urls` | `Urls` | `http://127.0.0.1:8080` | адрес прослушивания |
| `CtxUi__PublicUrl` | `CtxUi:PublicUrl` | `http://localhost:8080` | внешний адрес; начало адресов клиентов и панели |
| `CtxUi__DataDir` | `CtxUi:DataDir` | `data` | каталог базы и ключей |
| `CtxUi__Language` | `CtxUi:Language` | `en` | язык ошибок шлюза, CLI, API и панели, пока администратор не выбрал свой: `en` или `ru` |
| `CtxUi__RefreshInterval` | `CtxUi:RefreshInterval` | `00:00:01` | как часто сервер проверяет изменения из CLI |
| `CtxUi__FlushInterval` | `CtxUi:FlushInterval` | `00:00:05` | как часто пишутся счётчики квот и журнал вызовов |
| `CtxUi__UsageRetentionDays` | `CtxUi:UsageRetentionDays` | `30` | срок хранения журнала; итоги по дням хранятся всегда |
| `CtxUi__ConcurrencyWait` | `CtxUi:ConcurrencyWait` | `00:00:01` | сколько запрос сверх лимита одновременных ждёт слот до ответа 429 |
| `CtxUi__SessionCheckInterval` | `CtxUi:SessionCheckInterval` | `00:01:00` | как часто открытая панель перепроверяет сеанс администратора |

Адрес прослушивания задаётся через `Urls`, не `ASPNETCORE_URLS`: второй
перекрывается `appsettings.json`.

Путь панели хранится в базе, а не в настройках:
`ctx-ui admin path [--new]`.

### Язык

`CtxUi__Language` задаёт язык сообщений шлюза, вывода CLI, ошибок REST API
и панели. Каждый администратор по-прежнему может переключить язык панели,
выбор хранится в cookie. Логи, документ OpenAPI и скрипты установки всегда
на английском. Любое значение, кроме `en` и `ru`, останавливает сервис при
запуске с ошибкой, в которой перечислены допустимые значения.

Обновление с прошлых версий: сообщения шлюза, CLI и API раньше были на русском, теперь
по умолчанию на английском. Чтобы оставить русский, добавьте строку в
`/etc/ctx-ui/ctx-ui.env` и перезапустите сервис:

```
CtxUi__Language=ru
```

Для Docker — `CTX_UI_LANGUAGE=ru` в `.env`, затем `docker compose up -d`.

## Docker

`.env`:

| Переменная | Значение |
| --- | --- |
| `CTX_UI_VERSION` | тег образа |
| `CTX_UI_DOMAIN` | домен; из него `CtxUi__PublicUrl` и сайт Caddy |
| `CTX_UI_LANGUAGE` | из него `CtxUi__Language`, по умолчанию `en` |

Образ задаёт `Urls=http://0.0.0.0:8080` и `CtxUi__DataDir=/data`.

## Каталог данных

| Путь | Содержимое |
| --- | --- |
| `ctx-ui.db` | база SQLite |
| `keys/` | ключи Data Protection |

Без `keys/`: токены работают, но показать их нельзя, только перевыпустить;
inbound'ы с секретными заголовками выключены, пока заголовки не введут
заново; сеансы панели сбрасываются.
