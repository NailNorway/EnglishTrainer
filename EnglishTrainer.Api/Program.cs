using EnglishTrainer.Api.Ads;
using EnglishTrainer.Api.Authentication;
using EnglishTrainer.Api.Cli;
using EnglishTrainer.Api.Endpoints;
using EnglishTrainer.Api.ErrorHandling;
using EnglishTrainer.Api.Legal;
using EnglishTrainer.Api.Seo;
using EnglishTrainer.Api.Startup;
using EnglishTrainer.Application;
using EnglishTrainer.Application.Media;
using EnglishTrainer.Infrastructure;
using EnglishTrainer.Infrastructure.DependencyInjection;
using EnglishTrainer.Infrastructure.Persistence;
using EnglishTrainer.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.WebEncoders;
using System.Text.Encodings.Web;
using System.Text.Unicode;

var builder = WebApplication.CreateBuilder(args);

// ── Локальные секреты ────────────────────────────────────────────────────────
// Токен бота и строка подключения к своей базе лежат в appsettings.Local.json —
// файле вне репозитория. Тот же файл читает процесс бота, поэтому секрет
// задаётся один раз, а не по копии на каждый процесс.
LocalSettings.AddLocalOverrides(builder.Configuration, builder.Environment.ContentRootPath);

// ── Логирование ──────────────────────────────────────────────────────────────
// Провайдеры заданы явно, и это не косметика. По умолчанию хост на Windows
// добавляет приёмник Event Log, который пишет в системный журнал. Если прав
// на запись нет, попытка залогировать ошибку сама завершается исключением —
// и приложение падает вместо того, чтобы записать сообщение. Лог не должен
// ронять сервис: для приложения, которое запускается в контейнере,
// правильный приёмник — консоль.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = false;
    options.TimestampFormat = "HH:mm:ss ";
});

// ── Слои приложения ──────────────────────────────────────────────────────────
// HTTP-слой ничего не знает о PostgreSQL, EF Core и алгоритме повторений:
// он лишь вызывает сервисы Application. Это и есть разделение ответственности
// в действии — замена хранилища или алгоритма не трогает этот файл.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

// ── Аутентификация по токену ─────────────────────────────────────────────────
builder.Services
    .AddAuthentication(TokenAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationDefaults.Scheme,
        _ => { });

builder.Services.AddAuthorization();

// ── Ошибки ───────────────────────────────────────────────────────────────────
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

builder.Services.Configure<FormOptions>(options =>
{
    // Предел длины составной формы. Без него загружаемый файл целиком оказывался
    // бы в памяти до первой проверки, и «аватар на 300 МиБ» стал бы способом
    // уронить сервис. Предел чуть выше допустимого размера аватара, чтобы
    // пользователь получил понятный текст, а не отказ конвейера.
    options.MultipartBodyLengthLimit = AvatarPolicy.MaxBytes + (1024 * 1024);
});

// ── Обратный прокси ──────────────────────────────────────────────────────────
// За прокси приложение видит только внутренний адрес, если не доверять
// заголовкам X-Forwarded-*. Без них ссылка для сброса пароля собирается как
// http://api:8080/…, а перенаправление на HTTPS зацикливается: прокси
// присылает обычный HTTP, приложение отвечает «перейди на HTTPS», и так
// по кругу.
var trustProxy = builder.Configuration.GetValue<bool>("ReverseProxy:Enabled");

if (trustProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;

        // Доверяем любому прокси внутри сети развёртывания. Это безопасно ровно
        // до тех пор, пока порт приложения не опубликован наружу: иначе клиент
        // подставит себе любую схему и адрес. В docker-compose наружу смотрит
        // только Caddy, а порт API доступен лишь внутри сети.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// ── Документация ─────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ── Публичные страницы и поиск ───────────────────────────────────────────────
// Razor Pages, а не минимальные API: страницы колод отдаются готовым HTML,
// потому что поисковику нужен текст в ответе, а не оболочка, которую рисует
// скрипт. Приложение при этом остаётся на месте — оно живёт на своих адресах.
builder.Services.AddRazorPages();

// Кодировщик HTML без экранирования кириллицы.
//
// По умолчанию Razor превращает каждый русский символ в &#x416;: это
// корректный HTML, но страница вырастает вдвое, а в исходнике вместо текста
// видна каша — и человек, и часть инструментов проверки читают её плохо.
// Разрешённая таблица символов оставляет «Ё», тире и кавычки как есть.
builder.Services.Configure<WebEncoderOptions>(options =>
{
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
});

// Настройки видимости в поиске: адрес сайта, счётчик, файлы подтверждения прав.
builder.Services.Configure<SeoOptions>(builder.Configuration.GetSection(SeoOptions.SectionName));

// Реквизиты для юридических страниц и рекламный блок каталога.
builder.Services.Configure<LegalOptions>(builder.Configuration.GetSection(LegalOptions.SectionName));
builder.Services.Configure<AdsOptions>(builder.Configuration.GetSection(AdsOptions.SectionName));

var app = builder.Build();

// Режим обслуживания: `dotnet run --project EnglishTrainer.Api -- migrate|seed`.
// Проверяется до сборки конвейера — веб-сервер в этом режиме не поднимается.
if (MaintenanceCommands.IsMaintenanceCommand(args))
{
    return await MaintenanceCommands.ExecuteAsync(args, app.Services);
}

// Заголовки прокси разбираются первыми: от них зависит и схема, и адрес,
// а значит — и ссылки, которые приложение строит, и перенаправление на HTTPS.
if (trustProxy)
{
    app.UseForwardedHeaders();
}

// ── Интерфейс тренировки ─────────────────────────────────────────────────────
// Страница из wwwroot обращается к тем же эндпоинтам, что и любой другой клиент.
// Отдельная сборка фронтенда не нужна: статические файлы отдаются как есть.
app.UseDefaultFiles();

// Перепроверка статики включена всегда, а не только в разработке.
//
// Без заголовка кеша браузер (и WebView мини-аппа тем более) считает файлы
// свежими «по эвристике» и не спрашивает сервер: правка стилей или скрипта
// выложена, сервер перезапущен, а на телефоне остаётся старая копия — и это
// выглядит как «ничего не изменилось». Отдача при этом дешёвая: сервер отвечает
// 304, если файл не менялся, а размер всей статики — десятки килобайт.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
    }
});

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // Схема OpenAPI и интерактивная документация — только в разработке:
    // в проде это лишняя информация о внутреннем устройстве сервиса.
    app.MapOpenApi();
}
else
{
    // В разработке перенаправление на HTTPS мешает: локальный клиент ходит
    // по http, а сертификат разработчика установлен не всегда.
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapDeckEndpoints();
app.MapFavoriteEndpoints();
app.MapFriendEndpoints();
app.MapStudyEndpoints();
app.MapStatsEndpoints();
app.MapVersionEndpoints();

// Публичные страницы каталога (/decks и /decks/{slug}) и служебные адреса
// для роботов: robots.txt, sitemap.xml, загрузчик счётчика.
app.MapRazorPages();
app.MapSeoEndpoints(builder.Configuration);

// Адреса самого приложения. Раньше они жили в хеше и до сервера не доходили;
// теперь это обычные пути, и открыть /study напрямую (или обновить страницу)
// можно только если сервер отдаёт по ним оболочку.
app.MapSpaEndpoints();

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", DateTime.UtcNow)))
    .WithTags("Служебное")
    .AllowAnonymous();

app.MapGet("/health/db", async (AppDbContext db, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new HealthResponse("ok", DateTime.UtcNow))
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        // Проверка живости не должна падать сама: её смысл ровно в том,
        // чтобы сообщить «база недоступна», а не «что-то сломалось».
        loggerFactory.CreateLogger("HealthCheck").LogError(ex, "База данных недоступна.");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
})
.WithTags("Служебное")
.AllowAnonymous();

await StartupTasks.RunAsync(app);

app.Run();
return 0;

/// <summary>Ответ служебных эндпоинтов.</summary>
internal sealed record HealthResponse(string Status, DateTime TimeUtc);
