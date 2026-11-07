# REST API

[English](../en/api.md) · [Русский](api.md) · [Документация](README.md)

API покрывает всё, что умеет панель. Он живёт под секретным путём панели, поэтому его нельзя найти сканированием.

| Адрес | Что это |
| ----- | ------- |
| `https://<хост>/<секрет>/api/v1/…` | API. Ключ передаётся в `Authorization: Bearer ctxk_…`. |
| `https://<хост>/<секрет>/swagger/` | Swagger UI. Ключ вводится в «Authorize». |
| `https://<хост>/<секрет>/openapi/v1.json` | Документ OpenAPI 3.1. |

## Ключи

```bash
sudo ctx-ui apikey add ci
```

Команда печатает ключ один раз вместе с адресами API и Swagger. Ключи выпускаются и отзываются и в разделе **Ключи API** панели. Отозванный ключ сразу получает 401.

Сеанс панели API не открывает: браузер прикладывает cookie к запросам с любого сайта.

```bash
curl -H "Authorization: Bearer ctxk_..." https://mcp.example.com/<секрет>/api/v1/clients
```

## Ресурсы

| Ресурс | Методы |
| ------ | ------ |
| `inbounds`, `inbounds/{slug}` | Список, один, `PUT` создаёт или заменяет, `enable`, `disable`, `DELETE`. |
| `clients`, `clients/{id}` | Список (`?name=` находит одного), один, `POST` выпускает, `PATCH` меняет, `connection`, `enable`, `disable`, `reset-quota`, `reissue`, `DELETE`. |
| `clients/{id}/devices`, `…/devices/{deviceId}` | Список устройств, `DELETE` отключает одно или все. |
| `clients/{id}/invite`, `clients/{id}/links`, `…/links/{linkId}`, `…/links/{provider}` | `POST` выпускает ссылку-приглашение, список привязок, `DELETE` отвязывает один аккаунт или все аккаунты провайдера. |
| `oauth`, `oauth/providers/{name}` | Настройки (`PATCH`), провайдеры (`PUT`, `enable`, `disable`, `DELETE`). Секреты не возвращаются никогда. |
| `usage/days`, `usage/log`, `overview`, `metrics` | Итоги по дням, последние вызовы, «Обзор» панели, страница «Метрики» (`days`, `clientId`, `inboundId`, `tool`). |

## Ошибки

Ошибки приходят как `application/problem+json`. Текст `detail` тот же, что печатает CLI.

| Статус | Значение |
| ------ | -------- |
| 400 | Неверные данные. |
| 401 | Нет ключа или он неизвестен. |
| 404 | Нет такого объекта. |
| 409 | Токен показать нельзя: потерян каталог `keys/`. |

Изменения доходят до шлюза сразу.

## Расширения

Расширение может добавить свои эндпоинты под `/<секрет>/extensions/<id>/`. Они принимают те же ключи. [Конструктор серверов](builder.md#api-для-ci) использует это для CI.
