# Выпуск релиза

[English](../releasing.md) · **Русский**

Пуш тега `vX.Y.Z` запускает workflow `release`: тесты, архивы, образ,
GitHub Release с заметками из коммитов. Упавшие тесты — ничего не
публикуется.

```bash
git switch main
git pull
git tag v0.1.0
git push origin v0.1.0
```

## Версия

Берётся только из тега: `v0.1.0` → `0.1.0`. Допустимы `v1.2.3` и
`v1.2.3-rc.1`; иное валит первый шаг.

Тег с суффиксом — пре-релиз: образ получает только точный тег
(`0.2.0-rc.1`); `latest` и `releases/latest` остаются на последнем обычном
релизе.

## Артефакты

| Артефакт | Содержимое |
| --- | --- |
| `ctx-ui-X.Y.Z-linux-x64.tar.gz`, `ctx-ui-X.Y.Z-linux-arm64.tar.gz` | программа, unit systemd, обёртка CLI |
| `install.sh` | установщик с вшитой версией |
| `ctx-ui-X.Y.Z-docker.tar.gz` | `compose.yaml`, `Caddyfile`, `.env.example` |
| `SHA256SUMS` | контрольные суммы перечисленного |
| `ghcr.io/echways/ctx-ui` | образ amd64 + arm64, теги `X.Y.Z`, `X.Y`, `latest` |

## После первого релиза

Пакет на ghcr.io создаётся приватным. Один раз: страница пакета → Package
settings → Change visibility → Public. До этого `docker compose pull` у
других отвечает `denied`.

Там же проверить, что пакет связан с репозиторием `mcp-ui`, иначе
следующие релизы не смогут в него писать.

## Локальные проверки

Те же, что в CI. Нужны .NET 10 SDK и Docker.

```bash
dotnet test
deploy/tests/docker.sh
deploy/tests/linux.sh          # привилегированный контейнер Ubuntu с systemd, настоящий установщик
deploy/package.sh 0.1.0 dist   # собрать все файлы релиза без публикации
```

## Неудачный релиз

Упавшую задачу перезапустить со страницы запуска в Actions. Тег не на том
коммите: удалить релиз и тег, поставить заново.

```bash
gh release delete v0.1.0 --yes
git push origin :refs/tags/v0.1.0
git tag -d v0.1.0
```
