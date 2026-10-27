<#
.SYNOPSIS
    Разворачивает приложение на сервере: сайт, мини-апп и бот.

.DESCRIPTION
    Копирует рабочую копию на сервер, готовит .env, собирает образы и запускает
    всё через docker compose. Скрипт идемпотентен: повторный запуск обновляет
    приложение, а не ломает работающее.

    Пароль SSH вводится в самом ssh, а не в этот скрипт: секреты не проходят
    через командную строку и не попадают в историю. Если установить ключ, дальше
    всё будет без пароля — скрипт предложит готовую команду.

    Файл .env создаётся один раз и сохраняется в рабочей копии (он в .gitignore).
    Это важно: в нём пароль базы, а у уже созданной базы PostgreSQL смена пароля
    в .env не меняет сам пароль — приложение просто перестало бы подключаться.
    Поэтому при повторных запусках берётся существующий .env.

.PARAMETER Server
    Адрес для ssh, например root@203.0.113.10. Обязателен: адреса по умолчанию
    у скрипта нет намеренно — иначе развёртывание, запущенное без параметра,
    ушло бы на чужой или уже не существующий сервер.

.PARAMETER PublicUrl
    Публичный адрес приложения. Обязателен HTTPS: по HTTP Telegram кнопку
    мини-аппа не открывает. Домена нет — подойдёт sslip.io: он резолвит любой
    вписанный в имя IP, например https://203.0.113.10.sslip.io

.PARAMETER RemotePath
    Каталог на сервере, по умолчанию /opt/english-trainer.

.EXAMPLE
    .\scripts\deploy.ps1 -Server root@203.0.113.10 -PublicUrl https://203.0.113.10.sslip.io
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Server,
    [Parameter(Mandatory)] [string] $PublicUrl,
    [string] $RemotePath = '/opt/english-trainer',
    [int] $HttpsPort = 443,
    [string] $BotToken,
    [string] $BotUsername,
    [switch] $ForceNewSecrets
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# Корень репозитория.
#
# Обычный запуск — файлом, и тогда скрипт лежит в scripts\ внутри репозитория.
# Но на машинах с политикой целостности кода загрузка неподписанного .ps1
# запрещена, и скрипт запускают содержимым:
#   & ([scriptblock]::Create((Get-Content .\scripts\deploy.ps1 -Raw))) -PublicUrl …
# В этом случае $PSScriptRoot пуст, а текущим каталогом оказывается корень
# репозитория — из него скрипт и запускают.
if ($PSScriptRoot) {
    $root = Split-Path $PSScriptRoot -Parent
}
else {
    $root = (Get-Location).Path
}

$envFile = Join-Path $root '.env'
$configFile = Join-Path $root 'appsettings.Local.json'

function Write-Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Note($text) { Write-Host "   $text" -ForegroundColor DarkGray }

# Текст, который уходит на сервер, обязан быть в LF.
#
# Сам файл скрипта в рабочей копии — CRLF: так требует .gitattributes для *.ps1,
# и в свежем клоне иначе нельзя. Но here-string с командами тогда содержит \r\n,
# а bash на сервере считает \r обычным символом: «set -e» превращается в «set»
# с неверным ключом, а «mkdir -p /opt/english-trainer» создаёт каталог, имя
# которого заканчивается возвратом каретки. Развёртывание падает при этом
# с «net/url: invalid control character in URL», и связь с переводами строк
# в сообщении не видна совсем: искать начинают в конфигурации Caddy.
function ConvertTo-UnixText([string] $text) {
    return ($text -replace "`r`n", "`n") -replace "`r", "`n"
}

# Криптостойкая случайная строка в base64.
#
# Через Create()+GetBytes, а не статическим RandomNumberGenerator.Fill: Fill
# появился только в .NET Core, а Windows PowerShell работает на .NET Framework,
# где такого метода нет — вызов падал бы с «метода с именем Fill не существует»
# на первом же запуске.
function New-RandomBase64([int] $byteCount) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

    try {
        $bytes = New-Object byte[] $byteCount
        $rng.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        $rng.Dispose()
    }
}

# ── Проверки ─────────────────────────────────────────────────────────────────
if ($PublicUrl -notmatch '^https://') {
    throw "PublicUrl должен начинаться с https:// — по HTTP Telegram мини-апп не открывает."
}

foreach ($tool in 'ssh', 'scp', 'tar') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Не найдена команда $tool. В Windows она входит в состав OpenSSH и tar."
    }
}

$PublicUrl = $PublicUrl.TrimEnd('/')

# ── Токен бота ───────────────────────────────────────────────────────────────
if (-not $BotToken -or -not $BotUsername) {
    if (Test-Path $configFile) {
        $json = Get-Content $configFile -Raw

        if (-not $BotToken) {
            $BotToken = [regex]::Match($json, '"BotToken"\s*:\s*"([^"]*)"').Groups[1].Value
        }

        if (-not $BotUsername) {
            $BotUsername = [regex]::Match($json, '"BotUsername"\s*:\s*"([^"]*)"').Groups[1].Value
        }
    }
}

if (-not $BotToken) {
    throw "Не задан токен бота. Укажите его в appsettings.Local.json или параметром -BotToken."
}

# ── Почта ────────────────────────────────────────────────────────────────────
# Настройки берутся из того же appsettings.Local.json, что и токен бота:
# в развёртывании этого файла нет, поэтому единственный источник правды для
# локальной копии — он, а на сервере значения приходят переменными окружения.
# Так письмо со ссылкой для сброса пароля работает и на сервере, без отдельной
# правки .env руками — а забытая правка выглядела бы как «почта настроена,
# а письма не приходят».
function Read-LocalEmailSettings([string] $path) {
    if (-not (Test-Path $path)) {
        return $null
    }

    $json = Get-Content $path -Raw

    # Читается регулярками, а не ConvertFrom-Json: в файле есть комментарии,
    # а Windows PowerShell 5.1 разбирать такой JSON не умеет.
    #
    # Осторожно с ключами, которые начинаются одинаково: «From» и «FromName».
    # Регулярка ищет «"From"» вместе с закрывающей кавычкой, поэтому «"FromName"»
    # под неё не попадает — на этом различии всё и держится.
    $settings = [ordered]@{
        # Provider появился позже: пустое значение означает smtp, и старый файл
        # секретов продолжает работать без правок.
        Provider  = [regex]::Match($json, '"Provider"\s*:\s*"([^"]*)"').Groups[1].Value
        ApiKey    = [regex]::Match($json, '"ApiKey"\s*:\s*"([^"]*)"').Groups[1].Value
        Host      = [regex]::Match($json, '"Host"\s*:\s*"([^"]*)"').Groups[1].Value
        Port      = [regex]::Match($json, '"Port"\s*:\s*(\d+)').Groups[1].Value
        User      = [regex]::Match($json, '"User"\s*:\s*"([^"]*)"').Groups[1].Value
        Password  = [regex]::Match($json, '"Password"\s*:\s*"([^"]*)"').Groups[1].Value
        EnableSsl = [regex]::Match($json, '"EnableSsl"\s*:\s*(true|false)').Groups[1].Value
        Timeout   = [regex]::Match($json, '"TimeoutSeconds"\s*:\s*(\d+)').Groups[1].Value
        From      = [regex]::Match($json, '"From"\s*:\s*"([^"]*)"').Groups[1].Value
        FromName  = [regex]::Match($json, '"FromName"\s*:\s*"([^"]*)"').Groups[1].Value
    }

    if (-not $settings.Provider) { $settings.Provider = 'smtp' }

    # Настроенной почта считается только целиком: без адреса отправителя писать
    # некуда, а без ключа или пароля сервис отклонит отправку. Ровно по этому же
    # правилу приложение выбирает журнальный режим — расходиться они не должны.
    if (-not $settings.From) {
        return $null
    }

    if ($settings.Provider -eq 'brevo') {
        if (-not $settings.ApiKey) {
            Write-Host 'ВНИМАНИЕ: выбран Provider=brevo, но ключ Email:Brevo:ApiKey не задан.' -ForegroundColor Yellow
            Write-Host '         Письма будут выводиться в журнал сервера.' -ForegroundColor Yellow
            return $null
        }

        if (-not $settings.Timeout) { $settings.Timeout = '20' }
        if (-not $settings.FromName) { $settings.FromName = 'EnglishTrainer' }

        return $settings
    }

    if (-not $settings.Host) {
        return $null
    }

    if ($settings.User -and -not $settings.Password) {
        Write-Host 'ВНИМАНИЕ: в appsettings.Local.json задан логин почты без пароля.' -ForegroundColor Yellow
        Write-Host '         Приложение будет писать письма в журнал сервера. Пароль —' -ForegroundColor Yellow
        Write-Host '         «пароль для внешнего приложения», а не пароль от ящика.' -ForegroundColor Yellow
        return $null
    }

    if (-not $settings.Port) { $settings.Port = '587' }
    if (-not $settings.EnableSsl) { $settings.EnableSsl = 'true' }
    if (-not $settings.Timeout) { $settings.Timeout = '20' }
    if (-not $settings.FromName) { $settings.FromName = 'EnglishTrainer' }

    return $settings
}

# Значение для .env.
#
# Простое значение пишется как есть, остальное — в одинарных кавычках: в .env
# символы #, $, пробелы и кавычки значат больше, чем значат, и пароль, попавший
# туда без кавычек, был бы обрезан или разобран как подстановка. Одинарная
# кавычка внутри значения не экранируется вовсе, поэтому такой пароль честнее
# отклонить, чем записать испорченным.
function Format-EnvValue([string] $name, [string] $value) {
    if ($value -match "^[A-Za-z0-9._@:/+=-]*$") {
        return $value
    }

    if ($value -match "'") {
        throw "Значение $name содержит одинарную кавычку — её нельзя записать в .env. Смените пароль приложения."
    }

    return "'$value'"
}

$emailSettings = Read-LocalEmailSettings $configFile

# ── Ключ SSH ─────────────────────────────────────────────────────────────────
Write-Step "Ключ SSH"

$keyPath = Join-Path $env:USERPROFILE '.ssh\id_ed25519'

if (-not (Test-Path $keyPath)) {
    Write-Note "Ключа нет, создаю $keyPath"

    # Пустая фраза передаётся как '""': PowerShell не передаёт пустую строку
    # аргументом, а ssh-keygen без -N спрашивает её интерактивно.
    #
    # Подавляется только стандартный вывод. Перенаправлять stderr у внешней
    # команды нельзя: при $ErrorActionPreference = 'Stop' любая её жалоба
    # становится прерыванием скрипта, а нам нужен код возврата, а не исключение.
    & ssh-keygen -t ed25519 -N '""' -f $keyPath -C 'english-trainer-deploy' > $null

    if (-not (Test-Path "$keyPath.pub")) {
        throw "Не удалось создать ключ $keyPath. Создайте его вручную: ssh-keygen -t ed25519"
    }
}

$passwordless = $false

if (-not $ForceNewSecrets) {
    # stderr не подавляется намеренно: при $ErrorActionPreference = 'Stop'
    # перенаправление stderr у внешней команды прерывает скрипт, а неудача
    # здесь — ожидаемая. Поэтому сообщение «Permission denied» просто
    # печатается, а решение принимается по коду возврата.
    & ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new $Server 'true'
    $passwordless = $LASTEXITCODE -eq 0
}

if (-not $passwordless) {
    $publicKey = Get-Content "$keyPath.pub" -Raw

    Write-Host ''
    Write-Host 'Пароль будет спрошен у ssh. Чтобы больше его не вводить, выполните' -ForegroundColor Yellow
    Write-Host 'одну команду — она добавит ключ на сервер (пароль спросят один раз):' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  type `"$keyPath.pub`" | ssh $Server `"mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys`""
    Write-Host ''
    Read-Host 'Нажмите Enter, когда будете готовы (или сразу — пароль спросят по ходу)'

    & ssh -o ConnectTimeout=15 $Server 'echo connected'
    if ($LASTEXITCODE -ne 0) { throw "Не удалось подключиться к $Server." }
}

# ── Настройки развёртывания ──────────────────────────────────────────────────
Write-Step "Настройки"

$freshSecrets = $false

if ((Test-Path $envFile) -and -not $ForceNewSecrets) {
    Write-Note "Использую существующий .env (пароль базы и секрет токенов сохраняются)"
    $envText = Get-Content $envFile -Raw
}
else {
    # Пароля базы может не быть в локальном .env, а на сервере уже стоять
    # запущенная база. Забрать .env с сервера дешевле, чем обнаружить, что
    # приложение больше не подключается к своей же базе.
    #
    # Без перенаправления stderr — по той же причине, что и у проверки выше:
    # «нет файла» здесь нормальный ответ, а не ошибка скрипта.
    & ssh -o BatchMode=yes -o ConnectTimeout=10 $Server "test -f $RemotePath/.env"

    if ($LASTEXITCODE -eq 0 -and -not $ForceNewSecrets) {
        Write-Note "На сервере уже есть .env — забираю его, чтобы не сломать базу"
        & scp "${Server}:$RemotePath/.env" $envFile
        $envText = Get-Content $envFile -Raw
    }
    else {
        Write-Note "Создаю новый .env со случайными секретами"
        $freshSecrets = $true

        $jwtSecret = New-RandomBase64 48

        # Из пароля убираются +, / и = : он попадает в строку подключения
        # и в .env, где эти символы требуют экранирования или кавычек.
        # Алфавит base64 без них остаётся достаточно широким.
        $dbPassword = (New-RandomBase64 24) -replace '[+/=]', 'x'

        $envText = @"
PUBLIC_URL=$PublicUrl
POSTGRES_DB=english_trainer
POSTGRES_USER=postgres
POSTGRES_PASSWORD=$dbPassword
JWT_SECRET=$jwtSecret
TELEGRAM_BOT_TOKEN=$BotToken
TELEGRAM_BOT_USERNAME=$BotUsername
"@

        # Настройки почты здесь не дублируются: их записывает общий блок ниже —
        # он же обновляет ключи в уже существующем .env сервера. Два места,
        # пишущие одни и те же ключи, расходятся при первой же правке.
    }
}

# Публичный адрес обновляется всегда: он меняется чаще всего остального
# (сменился домен, появился постоянный вместо sslip.io).
if ($envText -match '(?m)^PUBLIC_URL=') {
    $envText = [regex]::Replace($envText, '(?m)^PUBLIC_URL=.*$', "PUBLIC_URL=$PublicUrl")
}
else {
    $envText = "PUBLIC_URL=$PublicUrl`n" + $envText
}

# Порт HTTPS тоже задаётся при каждом запуске: его меняют, когда 443 занят
# другой службой. Порт 80 при этом остаётся за Caddy — через него
# Let's Encrypt подтверждает владение доменом.
#
# Значение проверяется явно: пустая строка в .env выглядит безобидно, но
# подстановка ${HTTPS_PORT:-443} в compose считает пустое значение пустым,
# а не отсутствующим, и порт молча уезжает на 443 — что верно по случайности,
# а не по замыслу.
$httpsPort = if ($HttpsPort -gt 0) { $HttpsPort } else { 443 }

if ($envText -match '(?m)^HTTPS_PORT=') {
    $envText = [regex]::Replace($envText, '(?m)^HTTPS_PORT=.*$', "HTTPS_PORT=$httpsPort")
}
else {
    $envText = $envText.TrimEnd("`r", "`n") + "`nHTTPS_PORT=$httpsPort`n"
}

# ── Почта в .env ─────────────────────────────────────────────────────────────
# Ключи обновляются, а не добавляются один раз: пароль приложения меняют, и
# старый, оставшийся в .env, давал бы «Authentication failed» при живом
# локальном конфиге — самую неприятную поломку, потому что искать причину
# в серверном файле никто не идёт.
function Set-EnvValue([string] $text, [string] $name, [string] $value) {
    $line = "$name=$value"
    $pattern = "(?m)^$([regex]::Escape($name))=.*$"

    if ($text -match $pattern) {
        return [regex]::Replace($text, $pattern, $line)
    }

    return $text.TrimEnd("`r", "`n") + "`n$line`n"
}

if ($emailSettings) {
    $envText = Set-EnvValue $envText 'EMAIL_PROVIDER' $emailSettings.Provider
    $envText = Set-EnvValue $envText 'EMAIL_TIMEOUT_SECONDS' $emailSettings.Timeout
    $envText = Set-EnvValue $envText 'EMAIL_FROM' (Format-EnvValue 'EMAIL_FROM' $emailSettings.From)
    $envText = Set-EnvValue $envText 'EMAIL_FROM_NAME' (Format-EnvValue 'EMAIL_FROM_NAME' $emailSettings.FromName)

    if ($emailSettings.Provider -eq 'brevo') {
        $envText = Set-EnvValue $envText 'EMAIL_BREVO_API_KEY' (Format-EnvValue 'EMAIL_BREVO_API_KEY' $emailSettings.ApiKey)

        # Ключи SMTP очищаются, а не остаются «на всякий случай». Иначе переключение
        # на HTTP-сервис не работает: в .env остаются старые EMAIL_SMTP_*, Provider
        # по умолчанию smtp, и приложение продолжает стучаться в закрытый порт —
        # при том что в конфиге выбран другой способ. Именно так это и выяснилось:
        # в локальном файле brevo, а сервер писал «Почта: smtp.mail.ru:587».
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_HOST' ''
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_USER' ''
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_PASSWORD' ''
    }
    else {
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_HOST' (Format-EnvValue 'EMAIL_SMTP_HOST' $emailSettings.Host)
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_PORT' $emailSettings.Port
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_USER' (Format-EnvValue 'EMAIL_SMTP_USER' $emailSettings.User)
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_PASSWORD' (Format-EnvValue 'EMAIL_SMTP_PASSWORD' $emailSettings.Password)
        $envText = Set-EnvValue $envText 'EMAIL_SMTP_SSL' $emailSettings.EnableSsl
        $envText = Set-EnvValue $envText 'EMAIL_BREVO_API_KEY' ''
    }
}

# .env тоже пишется в LF.
#
# Файл читает не только docker compose (он переносит \r терпимо), но и
# remote-setup.sh через «. ./.env» — а в bash возврат каретки остаётся частью
# значения: PUBLIC_URL получил бы \r в конце, и Caddy искал бы домен с ним.
# Тот же \r уехал бы в образы, потому что .env попадает в архив.
$envText = ConvertTo-UnixText $envText

[System.IO.File]::WriteAllText($envFile, $envText, [System.Text.UTF8Encoding]::new($false))

Write-Note "Адрес: $PublicUrl (порт HTTPS: $httpsPort)"

if ($emailSettings) {
    if ($emailSettings.Provider -eq 'brevo') {
        Write-Note "Почта: Brevo (HTTP API), отправитель $($emailSettings.From)"
    }
    else {
        Write-Note "Почта: $($emailSettings.Host):$($emailSettings.Port) (SMTP), отправитель $($emailSettings.From)"
    }
}
else {
    Write-Note 'Почта не настроена: письмо со ссылкой будет выводиться в журнал сервера'
}

if ($freshSecrets) {
    $dbPasswordShown = [regex]::Match($envText, '(?m)^POSTGRES_PASSWORD=(.*)$').Groups[1].Value
    Write-Host ''
    Write-Host "   Пароль базы: $dbPasswordShown" -ForegroundColor Yellow
    Write-Note 'Он сохранён в .env (файл в .gitignore) — запишите его отдельно, если понадобится доступ к базе.'
}

# ── Передача файлов ──────────────────────────────────────────────────────────
Write-Step "Передача файлов"

$tarball = Join-Path $env:TEMP 'english-trainer-deploy.tar.gz'

# Исключения: результаты сборки, репозиторий, локальные данные и секреты.
# appsettings.Local.json не нужен на сервере: там настройки приходят
# переменными окружения, а этот файл содержит токен бота и пароль базы.
#
# Caddyfile и docker-compose.prod.yml не перезаписываются: на сервере в них есть
# домен приложения и отдельный маршрут для WebSocket-профиля, которых нет в
# локальной копии. Архив с кодом не должен затирать эту серверную настройку.
$excludeArgs = @(
    '--exclude=bin', '--exclude=obj', '--exclude=*/bin/*', '--exclude=*/obj/*',
    '--exclude=.git', '--exclude=.vs', '--exclude=.pgdata', '--exclude=.dotnet-home',
    '--exclude=tests', '--exclude=uploads',
    '--exclude=appsettings.Local.json', '--exclude=EnglishTrainer.Api/wwwroot/avatars',
    '--exclude=deploy/Caddyfile', '--exclude=docker-compose.prod.yml'
)

& tar -czf $tarball @excludeArgs -C $root .
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать архив.' }

$sizeMb = [Math]::Round((Get-Item $tarball).Length / 1MB, 1)
Write-Note "Архив собран: $sizeMb МБ"

& scp $tarball "${Server}:/tmp/english-trainer-deploy.tar.gz"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось скопировать архив на сервер.' }

# ── Установка на сервере ─────────────────────────────────────────────────────
Write-Step "Развёртывание на сервере"
Write-Note 'Первый раз это несколько минут: скачиваются образы .NET и PostgreSQL.'

$remoteCommand = @"
set -e
mkdir -p '$RemotePath'
tar xzf /tmp/english-trainer-deploy.tar.gz -C '$RemotePath'
rm -f /tmp/english-trainer-deploy.tar.gz
cd '$RemotePath'
bash deploy/remote-setup.sh
"@

# LF, а не CRLF: см. ConvertTo-UnixText — иначе bash получает \r в конце каждой
# строки и развёртывание падает на первом же шаге.
$remoteCommand = ConvertTo-UnixText $remoteCommand

& ssh $Server $remoteCommand
$exitCode = $LASTEXITCODE

Remove-Item $tarball -Force -ErrorAction SilentlyContinue

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host "Развёртывание на сервере не выполнилось (ssh вернул код $exitCode)." -ForegroundColor Red
    Write-Host ''
    Write-Host 'Что проверить по порядку:' -ForegroundColor Yellow
    Write-Host "  связь с сервером:  ssh -o ConnectTimeout=15 $Server 'echo connected'"
    Write-Host "  повторный запуск:  тот же скрипт с теми же параметрами (он идемпотентен,"
    Write-Host '                     архив копируется заново, уже собранные образы переиспользуются)'
    Write-Host "  журналы:           ssh $Server 'cd $RemotePath && docker compose -f docker-compose.prod.yml logs --tail 50'"
    Write-Host ''
    Write-Host 'Если ssh не соединяется — дело не в приложении: сервер недоступен,'
    Write-Host 'изменился ключ хоста или оборвалась сеть. Приложение на сервере при этом'
    Write-Host 'продолжает работать на прежней версии.'

    # Ошибка не «exit»-ом: скрипт запускают и содержимым
    # (& ([scriptblock]::Create(...))), а там exit завершает не скрипт,
    # а весь сеанс PowerShell — окно закрывается, и вместе с ним исчезает
    # причина сбоя, которую человек как раз и собирался прочитать.
    throw "Развёртывание прервано на шаге ssh (код $exitCode)."
}

Write-Host ''
Write-Host 'Готово.' -ForegroundColor Green
Write-Host "  Сайт и мини-апп: $PublicUrl"
Write-Host "  Журналы:         ssh $Server 'cd $RemotePath && docker compose -f docker-compose.prod.yml logs -f api bot'"

# ── Почта: что решило приложение ─────────────────────────────────────────────
# Строку о почте api пишет при запуске, и она же отвечает на вопрос «уходит
# письмо или остаётся в журнале». Проверять это по журналу, а не надеяться на
# настройку: половина настройки не даёт ошибки, а «почта настроена, а письма
# нет» выясняется обычно на живом человеке, который ждёт ссылку.
Write-Step 'Почта на сервере'

$mailLine = & ssh $Server "cd '$RemotePath' && docker compose -f docker-compose.prod.yml logs api 2>&1 | grep -m1 'Почта' || true"

if ($mailLine) {
    foreach ($line in @($mailLine)) {
        if ($line.Trim()) { Write-Note $line.Trim() }
    }
}
else {
    Write-Host '   Строка о почте в журнале не найдена — посмотрите сами:' -ForegroundColor Yellow
    Write-Host "   ssh $Server `"cd $RemotePath && docker compose -f docker-compose.prod.yml logs --tail 50 api`"" -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Проверить восстановление пароля: откройте сайт, «Забыли пароль?» —' -ForegroundColor DarkGray
Write-Host 'письмо должно прийти на почту. Если письма нет, причина будет в журнале api.' -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Если мини-апп не открывается с телефона, а сайт открывается —' -ForegroundColor Yellow
Write-Host 'значит IP сервера недоступен из вашей мобильной сети, и дело не в приложении.' -ForegroundColor Yellow
