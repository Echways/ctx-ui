# Установка

[English](../install.md) · **Русский**

Есть два способа: бинарник с systemd и Docker с Caddy. Оба ставят один и
тот же шлюз и управляются одной и той же командой `ctx-ui`.

Нужен Linux на x86_64 или aarch64. Сборок под Windows, macOS и Alpine нет.

## Бинарник и systemd

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

Установщик спрашивает два адреса:

- **внешний адрес** — тот, по которому клиенты видят шлюз, например
  `https://mcp.example.com`. С него начинаются адреса, которые печатает
  `ctx-ui client add`;
- **адрес прослушивания** — где слушает сам сервис. По умолчанию
  `http://127.0.0.1:8080`, то есть только для обратного прокси на той же
  машине.

Без терминала вопросы не задаются, значения берутся из параметров или
переменных:

| Параметр | Переменная | По умолчанию |
| --- | --- | --- |
| `--public-url` | `CTX_UI_PUBLIC_URL` | `http://localhost:8080` |
| `--listen` | `CTX_UI_LISTEN` | `http://127.0.0.1:8080` |
| `--version` | `CTX_UI_VERSION` | версия релиза, из которого взят установщик |
| `--archive` | нет | архив скачивается со страницы релиза |
| нет | `CTX_UI_DOWNLOAD_URL` | страница релиза на GitHub; адрес зеркала, где лежат архив и `SHA256SUMS` |

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --public-url https://mcp.example.com
```

Скачанный архив сверяется с `SHA256SUMS` того же релиза. При несовпадении
установка прерывается, система не меняется.

### Что куда ставится

| Путь | Что там | При обновлении |
| --- | --- | --- |
| `/opt/ctx-ui/` | программа | заменяется целиком |
| `/etc/ctx-ui/ctx-ui.env` | настройки | не трогается |
| `/var/lib/ctx-ui/` | база и ключи шифрования | не трогается |
| `/usr/local/bin/ctx-ui` | команда управления | заменяется |
| `/etc/systemd/system/ctx-ui.service` | сервис | заменяется |

Сервис работает от системного пользователя `ctx-ui`. Команда `ctx-ui`
запускается через `sudo` и сама переключается на этого пользователя.

### TLS

Сервис TLS не поднимает. Перед ним ставится обратный прокси.

Caddy:

```
mcp.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
server {
    listen 443 ssl;
    server_name mcp.example.com;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_buffering off;
        proxy_read_timeout 1h;
        access_log off;
    }
}
```

Переменная `$connection_upgrade` задаётся один раз в блоке `http`:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}
```

`proxy_buffering off` нужен для потоковых ответов MCP. `Upgrade` и
`Connection` нужны панели: она держит WebSocket. `X-Forwarded-Proto`
сообщает сервису, что снаружи HTTPS: без него cookie входа в панель не
получит отметку `Secure`. `access_log off` нужен потому, что адрес вида
`/k/<токен>/mcp/<slug>` содержит токен, а адрес панели содержит её
секретный путь: журнал доступа записал бы их целиком. Caddy всё это делает
сам.

### Панель

```bash
sudo ctx-ui admin add admin
```

Команда печатает логин, пароль и адрес панели. Пароль показывается один
раз; забытый заменяется командой `sudo ctx-ui admin password admin`. Адрес
панели повторно показывает `sudo ctx-ui admin path`.

### Обновление

Тот же установщик из нового релиза. Настройки и данные остаются, вопросы
не задаются.

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

Определённая версия:

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/download/v0.1.0/install.sh | sudo bash
```

### Удаление

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --uninstall
```

Программа, сервис и команда удаляются, настройки и данные остаются.
`--uninstall --purge` удаляет и их, и пользователя `ctx-ui`.

## Docker и Caddy

Нужны Docker с Compose и домен, который указывает на сервер: Caddy сам
получает для него сертификат, порты 80 и 443 должны быть открыты.

Версия берётся со страницы релизов и подставляется в первую строку.

```bash
VERSION=0.1.0
curl -fsSL https://github.com/Echways/mcp-ui/releases/download/v$VERSION/ctx-ui-$VERSION-docker.tar.gz | tar -xz
cd ctx-ui
cp .env.example .env
```

В `.env` вписывается домен:

```
CTX_UI_VERSION=0.1.0
CTX_UI_DOMAIN=mcp.example.com
```

```bash
docker compose up -d
docker compose exec ctx-ui ctx-ui admin add admin
```

Команда печатает адрес панели и пароль; inbound'ы и клиенты заводятся в
панели или теми же командами, что и при установке бинарником:

```bash
docker compose exec ctx-ui ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
docker compose exec ctx-ui ctx-ui client add "Иванов" --inbound learn
```

Контейнер работает не от root, порт шлюза наружу не публикуется, наружу
смотрит только Caddy. База и ключи лежат в томе `ctx-ui-data`.

### Обновление

В `.env` меняется `CTX_UI_VERSION`, затем:

```bash
docker compose pull
docker compose up -d
```

### Первый релиз

Образ лежит в `ghcr.io/echways/ctx-ui`. Если `docker compose pull` отвечает
`denied`, пакет ещё не сделан публичным, см. `releasing.md`.

## Резервная копия

Копировать нужно данные и настройки вместе:

| Способ | Данные | Настройки |
| --- | --- | --- |
| бинарник | `/var/lib/ctx-ui/` | `/etc/ctx-ui/ctx-ui.env` |
| Docker | том `ctx-ui-data` | `.env` |

Каталог `keys` внутри данных так же важен, как база: без него выпущенные
токены продолжают работать, но показать их нельзя, только перевыпустить, а
inbound'ы с секретными заголовками выключаются до повторного добавления.
Открытые сеансы панели без него тоже закрываются: входить придётся заново.

Перед копированием сервис лучше остановить: база SQLite состоит из
нескольких файлов, и копия на ходу может получиться несогласованной.
