# Установка

[English](../install.md) · **Русский**

Два способа: бинарник + systemd или Docker + Caddy. Программа и команда
`ctx-ui` одни и те же.

Платформы: Linux x86_64, aarch64. Сборок под Windows, macOS, Alpine нет.

## Бинарник и systemd

Нужны root, systemd, `curl`, `tar`, `sha256sum`, `runuser`, `useradd`.

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh | sudo bash
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
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh \
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

Шлюз, CLI и API по умолчанию отвечают на английском. Для русского добавьте
в `/etc/ctx-ui/ctx-ui.env` строку `CtxUi__Language=ru` и выполните
`sudo systemctl restart ctx-ui`; подробнее — в
[configuration.md](configuration.md#язык).

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
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
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

### Остановка и запуск

```bash
sudo ctx-ui down     # остановить сервис, после перезагрузки он не поднимется
sudo ctx-ui up       # запустить и вернуть автозапуск
```

`down` останавливает весь процесс: панель, API и шлюз. Настройки и данные
остаются; `update` запускает сервис снова.

### Обновление

```bash
sudo ctx-ui update           # последний релиз
sudo ctx-ui update 0.2.0     # конкретная версия
ctx-ui version               # установленная версия
```

`update` скачивает установщик релиза и запускает его: архив сверяется с
`SHA256SUMS`, настройки и данные сохраняются, сервис перезапускается. Если
эта версия уже работает, ничего не меняется. Повторный запуск команды
установки делает то же самое.

### Удаление

```bash
curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/install.sh \
    | sudo bash -s -- --uninstall
```

Удаляет программу, unit и CLI; настройки и данные остаются. С `--purge`
удаляются ещё `/etc/ctx-ui`, `/var/lib/ctx-ui` и пользователь `ctx-ui`.

## Docker и Caddy

Нужны Docker Compose, домен, указывающий на сервер, открытые порты 80 и
443. Сертификат получает Caddy.

```bash
VERSION=0.1.0
curl -fsSL https://github.com/Echways/ctx-ui/releases/download/v$VERSION/ctx-ui-$VERSION-docker.tar.gz | tar -xz
cd ctx-ui
cp .env.example .env
```

`.env`:

```
CTX_UI_VERSION=0.1.0
CTX_UI_DOMAIN=mcp.example.com
CTX_UI_LANGUAGE=ru
```

`CTX_UI_LANGUAGE=ru` переводит шлюз, CLI и API на русский; без этой строки
они отвечают на английском.

```bash
docker compose up -d
docker compose exec ctx-ui ctx-ui admin add admin
docker compose exec ctx-ui ctx-ui inbound add learn --url https://learn.microsoft.com/api/mcp
docker compose exec ctx-ui ctx-ui client add "Иванов" --inbound learn
```

Контейнер работает не от root, без capabilities; порт 8080 не
публикуется, наружу смотрит только Caddy. Данные в томе `ctx-ui-data`.

### Обновление

В каталоге `ctx-ui`:

```bash
sed -i 's/^CTX_UI_VERSION=.*/CTX_UI_VERSION=0.2.0/' .env && docker compose up -d
```

Compose скачает новый образ и пересоздаст контейнер; данные остаются в томе.

## Резервная копия

| Способ | Данные | Настройки |
| --- | --- | --- |
| бинарник | `/var/lib/ctx-ui/` | `/etc/ctx-ui/ctx-ui.env` |
| Docker | том `ctx-ui-data` | `.env` |

`ctx-ui backup <каталог>` записывает согласованную копию базы и каталога
`keys/`, сервис при этом продолжает работать. Каталог должен не существовать
или быть пустым. Копировать файл базы работающего сервиса нельзя: SQLite
держит свежие изменения в `ctx-ui.db-wal`, и простая копия может выйти
несогласованной. Без `keys/` сохранённые токены и заголовки upstream не
расшифровать; подробнее — в [configuration.md](configuration.md#каталог-данных).

Бинарник (команда работает от пользователя `ctx-ui`, поэтому ей нужен
каталог, куда этот пользователь может писать):

```bash
backup=/var/backups/ctx-ui-$(date +%F)
sudo install -d -o ctx-ui -g ctx-ui -m 700 "$backup"
sudo ctx-ui backup "$backup"
```

Docker, в каталоге `ctx-ui`:

```bash
backup=backup-$(date +%F)
docker compose exec ctx-ui ctx-ui backup /data/$backup
docker compose cp ctx-ui:/data/$backup ./$backup
docker compose exec ctx-ui rm -r /data/$backup
```

Файл настроек (`ctx-ui.env` или `.env`) хранится рядом с копией.

### Восстановление

Остановить сервис, заменить всё содержимое каталога данных копией, вернуть
владельца, запустить сервис. Старые `ctx-ui.db-wal` и `ctx-ui.db-shm`
удаляются вместе с остальным.

Бинарник:

```bash
sudo systemctl stop ctx-ui
sudo find /var/lib/ctx-ui -mindepth 1 -delete
sudo cp -r /var/backups/ctx-ui-2026-10-21/. /var/lib/ctx-ui/
sudo chown -R ctx-ui:ctx-ui /var/lib/ctx-ui
sudo systemctl start ctx-ui
```

Docker, в каталоге `ctx-ui`:

```bash
docker compose stop ctx-ui
docker run --rm -v ctx-ui_ctx-ui-data:/data -v "$PWD/backup-2026-10-21:/restore:ro" alpine \
    sh -c 'find /data -mindepth 1 -delete && cp -r /restore/. /data/ && chown -R 1654:1654 /data'
docker compose start ctx-ui
```
