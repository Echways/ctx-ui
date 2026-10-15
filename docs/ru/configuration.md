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
| `CtxUi__RefreshInterval` | `CtxUi:RefreshInterval` | `00:00:01` | как часто сервер проверяет изменения из CLI |
| `CtxUi__FlushInterval` | `CtxUi:FlushInterval` | `00:00:05` | как часто пишутся счётчики квот и журнал вызовов |
| `CtxUi__UsageRetentionDays` | `CtxUi:UsageRetentionDays` | `30` | срок хранения журнала; итоги по дням хранятся всегда |
| `CtxUi__SessionCheckInterval` | `CtxUi:SessionCheckInterval` | `00:01:00` | как часто открытая панель перепроверяет сеанс администратора |

Адрес прослушивания задаётся через `Urls`, не `ASPNETCORE_URLS`: второй
перекрывается `appsettings.json`.

Путь панели хранится в базе, а не в настройках:
`ctx-ui admin path [--new]`.

## Docker

`.env`:

| Переменная | Значение |
| --- | --- |
| `CTX_UI_VERSION` | тег образа |
| `CTX_UI_DOMAIN` | домен; из него `CtxUi__PublicUrl` и сайт Caddy |

Образ задаёт `Urls=http://0.0.0.0:8080` и `CtxUi__DataDir=/data`.

## Каталог данных

| Путь | Содержимое |
| --- | --- |
| `ctx-ui.db` | база SQLite |
| `keys/` | ключи Data Protection |

Без `keys/`: токены работают, но показать их нельзя, только перевыпустить;
inbound'ы с секретными заголовками выключены, пока заголовки не введут
заново; сеансы панели сбрасываются.
