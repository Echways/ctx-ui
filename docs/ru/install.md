# Установка

[English](../install.md) · **Русский**

Два способа: бинарник + systemd или Docker + Caddy. Программа и команда
`ctx-ui` одни и те же.

Платформы: Linux x86_64, aarch64. Сборок под Windows, macOS, Alpine нет.

## Бинарник и systemd

Нужны root, systemd, `curl`, `tar`, `sha256sum`, `runuser`, `useradd`.

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
```

Установщик спрашивает два адреса (только при первой установке):

- внешний адрес — его видят клиенты, например `https://mcp.example.com`;
  с него начинаются адреса клиентов и панели;
- адрес прослушивания — по умолчанию `http://127.0.0.1:8080`, доступен
  только локальному обратному прокси.

Без терминала значения берутся из параметров или переменных:

| Параметр | Переменная | По умолчанию |
| --- | --- | --- |
| `--public-url` | `CTX_UI_PUBLIC_URL` | `http://localhost:8080` |
| `--listen` | `CTX_UI_LISTEN` | `http://127.0.0.1:8080` |
| `--version` | `CTX_UI_VERSION` | версия релиза, из которого взят установщик |
| `--archive <файл>` | — | скачать из релиза |
| — | `CTX_UI_DOWNLOAD_URL` | страница релиза; зеркало с архивом и `SHA256SUMS` |

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --public-url https://mcp.example.com
```

Архив сверяется с `SHA256SUMS` релиза; при несовпадении установка
прерывается, ничего не изменив.

Если `/etc/ctx-ui/ctx-ui.env` уже есть, он сохраняется, а `--public-url` и
`--listen` игнорируются. Меняйте файл и перезапускайте сервис.

### Расположение

| Путь | Содержимое | При обновлении |
| --- | --- | --- |
| `/opt/ctx-ui/` | программа | заменяется |
| `/etc/ctx-ui/ctx-ui.env` | настройки | сохраняются |
| `/var/lib/ctx-ui/` | база, ключи | сохраняются |
| `/usr/local/bin/ctx-ui` | обёртка CLI | заменяется |
| `/etc/systemd/system/ctx-ui.service` | unit | заменяется |

Сервис работает от системного пользователя `ctx-ui`. CLI запускается через
`sudo`; обёртка сама читает `ctx-ui.env` и переключается на `ctx-ui`.

### TLS

Сервис говорит по чистому HTTP. Перед ним ставится обратный прокси.

Caddy:

```
mcp.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

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

Блок `map` ставится в `http`.

| Директива | Зачем |
| --- | --- |
| `proxy_buffering off` | потоковые ответы MCP (SSE) |
| `Upgrade`, `Connection` | WebSocket панели |
| `X-Forwarded-Proto` | флаг `Secure` у cookie панели, редиректы на `https` |
| `access_log off` | в путях токен (`/k/<токен>/…`) и секретный путь панели |
| `proxy_read_timeout 1h` | долгие потоки MCP |

Caddy делает всё это сам.

### Панель

```bash
sudo ctx-ui admin add admin          # логин, пароль, адрес панели
sudo ctx-ui admin password admin     # новый пароль
sudo ctx-ui admin path               # адрес панели
```

### Обновление

Повторный запуск установщика. Настройки и данные сохраняются, вопросов нет.

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh | sudo bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/download/v0.1.0/install.sh | sudo bash   # конкретная версия
```

### Удаление

```bash
curl -fsSL https://github.com/Echways/mcp-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --uninstall
```

Удаляет программу, unit и CLI; настройки и данные остаются. С `--purge`
удаляются ещё `/etc/ctx-ui`, `/var/lib/ctx-ui` и пользователь `ctx-ui`.

## Docker и Caddy

Нужны Docker Compose, домен, указывающий на сервер, открытые порты 80 и
443. Сертификат получает Caddy.

```bash
VERSION=0.1.0
curl -fsSL https://github.com/Echways/mcp-ui/releases/download/v$VERSION/ctx-ui-$VERSION-docker.tar.gz | tar -xz
cd ctx-ui
cp .env.example .env
```

`.env`:

```
CTX_UI_VERSION=0.1.0
CTX_UI_DOMAIN=mcp.example.com
```

```bash
docker compose up -d
docker compose exec ctx-ui ctx-ui admin add admin
docker compose exec ctx-ui ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
docker compose exec ctx-ui ctx-ui client add "Иванов" --inbound learn
```

Контейнер работает не от root, без capabilities; порт 8080 не
публикуется, наружу смотрит только Caddy. Данные в томе `ctx-ui-data`.

### Обновление

Новая версия в `CTX_UI_VERSION` в `.env`:

```bash
docker compose pull
docker compose up -d
```

`denied` при pull: пакет на ghcr.io ещё приватный, см.
[releasing.md](releasing.md#после-первого-релиза).

## Резервная копия

| Способ | Данные | Настройки |
| --- | --- | --- |
| бинарник | `/var/lib/ctx-ui/` | `/etc/ctx-ui/ctx-ui.env` |
| Docker | том `ctx-ui-data` | `.env` |

`keys/` копируется вместе с базой; что теряется без него — в
[configuration.md](configuration.md#каталог-данных). Сервис перед
копированием остановить: SQLite держит несколько файлов, копия на ходу
может выйти несогласованной.
