# Проверка Docker, 26 ноября 2026

Проверено на Docker Desktop / Linux ARM64: Docker Engine 29.9.2,
Docker Compose 5.6.1. Сборка x86-64 отдельно не выполнялась.

| Проверка | Результат |
|---|---|
| `docker compose config --quiet` | PASS: стандартный volume, порт и настройки разрешаются |
| Конфигурация с существующей БД | PASS: bind mount каталога work, нужное имя БД, UID/GID владельца |
| `docker compose build` | PASS: Angular на Node 24, .NET 10 restore/publish, runtime-образ |
| Docker healthcheck | PASS: состояние healthy, `/health` возвращает ok |
| Непривилегированный процесс | PASS: UID 1654 с volume, UID 501 с bind mount на этой машине |
| Состав образа | PASS: собранный wwwroot присутствует, Node и SDK отсутствуют, локальных SQLite в /app нет |
| HTTP | PASS: `/sessions`, JS-модули, admin API, создание сессии, мок и журнал доступны через порт 15080 |
| Маскирование | PASS: тестовый секрет отсутствует в подробностях, REDACTED присутствует |
| Пересоздание контейнера | PASS: compose down без volumes и последующий up сохраняют id сессии и запись журнала |
| Существующая SQLite | PASS: согласованная backup-копия cabinet-staging прошла миграцию в контейнере; сохранены сессия и все 54 запроса |
| Настоящая production-панель | PASS: Chromium открыл панель из контейнера, выбрал сессию, вкладку журнала и подробности; pageerror отсутствуют |
| Независимый read-only обзор | PASS: важных замечаний к Dockerfile, compose.yaml, .dockerignore и инструкции нет |
| `git diff --check` | PASS: ошибок пробелов нет |

Проверочные проекты: `mock-container-check-20261126` и
`mock-container-import-20261126`, порты 15080 и 15081. Они использовали отдельный
volume и отдельную копию базы. Исходная `work/published-sessions-smoke.db`
не запускалась и не мигрировалась в контейнере. Скриншот production-панели:
`work/docker-panel.png`.

Delivery Gate: PASS для упаковки и документации. Продуктовый интерфейс не
изменялся; его работа проверена в собранном контейнере. Новые комментарии
содержат только назначение формата Dockerfile и пример UID/GID в инструкции.

Официальные образы и многоэтапная сборка:
[документация ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0).
