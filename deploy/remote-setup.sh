#!/usr/bin/env bash
#
# Подготовка сервера и запуск приложения. Выполняется НА сервере.
#
# Запускается скриптом scripts/deploy.ps1 с вашей машины, но его можно
# выполнить и вручную — он идемпотентен: повторный запуск обновляет
# приложение, а не ломает уже работающее.
#
set -euo pipefail

cd "$(dirname "$0")/.."
APP_DIR="$(pwd)"

echo "== Каталог приложения: $APP_DIR"

# ── Настройки ────────────────────────────────────────────────────────────────
if [ ! -f .env ]; then
    echo "ОШИБКА: нет файла .env — в нём пароль базы, секрет токенов и токен бота." >&2
    echo "Скопируйте .env.example в .env и заполните, либо запустите scripts/deploy.ps1." >&2
    exit 1
fi

# shellcheck disable=SC1091
set -a; . ./.env; set +a

if [ -z "${PUBLIC_URL:-}" ]; then
    echo "ОШИБКА: в .env не задан PUBLIC_URL — публичный адрес мини-аппа." >&2
    exit 1
fi

# ── Docker ───────────────────────────────────────────────────────────────────
if ! command -v docker >/dev/null 2>&1; then
    echo "== Docker не установлен, ставлю"
    curl -fsSL https://get.docker.com | sh
    systemctl enable --now docker
fi

if ! docker compose version >/dev/null 2>&1; then
    echo "ОШИБКА: нужен Docker Compose V2 (команда 'docker compose')." >&2
    echo "Он входит в современный Docker; проверьте: docker compose version" >&2
    exit 1
fi

echo "== Docker: $(docker --version)"

# ── Занятые порты ────────────────────────────────────────────────────────────
# Caddy должен занять 80 (сертификат) и порт HTTPS из настроек. Если их держит
# что-то другое, он не поднимется, и понятнее сказать об этом сразу, чем
# показать стек Kestrel.
if command -v ss >/dev/null 2>&1; then
    for port in 80 "${HTTPS_PORT:-443}"; do
        if ss -lntH "sport = :$port" | grep -q .; then
            echo "ВНИМАНИЕ: порт $port уже занят — Caddy не сможет его занять."

            if [ "$port" = "80" ]; then
                echo "  Через 80-й Let's Encrypt подтверждает владение доменом, замена ему есть"
                echo "  только в виде DNS-проверки. Этот порт придётся освободить."
            else
                echo "  Кто именно слушает:  ss -lntp | grep ':$port '"
                echo "  Если порт занят службой, которую трогать нельзя, задайте в .env"
                echo "  другой порт:  HTTPS_PORT=9443   (адрес станет https://домен:9443)"
            fi
        fi
    done
fi

# ── Проверка конфигурации прокси ─────────────────────────────────────────────
# Caddy при неверной конфигурации уходит в цикл перезапуска и не всегда
# объясняет причину. Проверка занимает секунды и печатает точное место ошибки,
# поэтому она идёт до сборки, а не после пяти минут ожидания.
echo "== Проверка конфигурации Caddy"
if ! docker compose -f docker-compose.prod.yml run --rm --no-deps -T \
        --entrypoint caddy caddy validate \
        --config /etc/caddy/Caddyfile --adapter caddyfile; then
    echo
    echo "ОШИБКА: конфигурация Caddy не проходит проверку — выше указано место." >&2
    exit 1
fi

# ── Сборка и запуск ──────────────────────────────────────────────────────────
#
# Сборка отделена от запуска и идёт последовательно — это не перестраховка.
# На сервере с 1 ГБ памяти без swap три параллельных `dotnet publish`
# (api, bot, migrate) выели всю память: машина ушла в свопинг, перестала
# отвечать на SSH и на HTTP, а обновление оборвалось на середине.
# COMPOSE_PARALLEL_LIMIT=1 добавляет пару минут и снимает этот сценарий.
swaptotal=$(awk '/^SwapTotal:/{print $2}' /proc/meminfo 2>/dev/null || echo 0)

if [ "${swaptotal:-0}" -eq 0 ]; then
    echo "ВНИМАНИЕ: swap не включён, а сборке .NET нужно около гигабайта сверху."
    echo "  Если памяти мало, сборка может уронить сервер в свопинг. Включить swap:"
    echo "    fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile"
    echo "    echo '/swapfile none swap sw 0 0' >> /etc/fstab"
fi

echo "== Сборка образов (последовательно)"
COMPOSE_PARALLEL_LIMIT=1 docker compose -f docker-compose.prod.yml build

echo "== Запуск"
docker compose -f docker-compose.prod.yml up -d

# ── Проверка, что прокси поднялся ────────────────────────────────────────────
# Без этого шага неверный порт или занятый адрес выглядят как «сайт не
# открывается»: скрипт пять минут ждёт ответа, который не придёт.
echo "== Проверка, что Caddy запустился"
sleep 10

# «|| true» не для красоты: при set -e и pipefail неудачная команда внутри
# подстановки завершает весь скрипт молча — без сообщения о том, что случилось.
caddy_state=$(docker compose -f docker-compose.prod.yml ps -a caddy --format '{{.State}}' 2>/dev/null | head -1 || true)

if [ "$caddy_state" != "running" ]; then
    echo
    echo "ОШИБКА: Caddy не работает (состояние: ${caddy_state:-неизвестно}). Журнал:" >&2
    docker compose -f docker-compose.prod.yml logs --tail 40 caddy >&2
    exit 1
fi

echo "== Ожидание ответа приложения на $PUBLIC_URL"
# Сертификат Let's Encrypt выдаётся при первом обращении, поэтому запас
# по времени щедрый: перевыпуск и проверка домена занимают до минуты.
if command -v curl >/dev/null 2>&1; then
    attempts=60
    until curl -fsS --max-time 5 "$PUBLIC_URL/health" >/dev/null 2>&1; do
        attempts=$((attempts - 1))
        if [ "$attempts" -le 0 ]; then
            echo
            echo "ОШИБКА: приложение не ответило за 5 минут. Последние строки журналов:"
            docker compose -f docker-compose.prod.yml logs --tail 40 api caddy
            exit 1
        fi
        sleep 5
    done
else
    # Без curl проверять нечем, а молча «подождать и решить, что всё хорошо» —
    # хуже, чем честно сказать, что проверка не выполнялась.
    echo "curl на сервере не найден — проверка доступности пропущена"
    sleep 10
fi

echo
echo "== Готово"
docker compose -f docker-compose.prod.yml ps --format 'table {{.Service}}\t{{.Status}}'

echo
echo "Сайт и мини-апп: $PUBLIC_URL"
echo "Бот подключится сам — кнопку меню он ставит на адрес из Telegram__WebAppUrl."
echo
echo "Полезное:"
echo "  docker compose -f docker-compose.prod.yml logs -f api bot"
echo "  docker compose -f docker-compose.prod.yml restart bot"
echo "  docker compose -f docker-compose.prod.yml exec postgres \\"
echo "      pg_dump -U \${POSTGRES_USER:-postgres} \${POSTGRES_DB:-english_trainer} | gzip > backup.sql.gz"
