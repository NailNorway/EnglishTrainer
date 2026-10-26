using System.Xml.Linq;
using EnglishTrainer.Api.Seo;
using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Services;
using Microsoft.Extensions.Options;

namespace EnglishTrainer.Api.Endpoints;

/// <summary>
/// Служебные адреса для поисковых систем: правила обхода, карта сайта,
/// загрузчик счётчика и файлы подтверждения прав.
/// </summary>
/// <remarks>
/// <para>
/// Эти адреса отдаются приложением, а не лежат файлами в <c>wwwroot</c>, потому
/// что их содержимое зависит от настроек: адрес сайта, номер счётчика и имена
/// файлов подтверждения приходят из окружения. Файл <c>sitemap.xml</c> в
/// репозитории пришлось бы править вручную при добавлении каждой колоды,
/// и он бы неизбежно разошёлся с базой.
/// </para>
/// <para>
/// Ответы дешёвые: карта сайта собирается из девятнадцати адресов, остальное —
/// константы. Кеш всё равно выставлен, чтобы робот не дёргал базу на каждый
/// обход, а посетитель не ждал.
/// </para>
/// </remarks>
public static class SeoEndpoints
{
    public static IEndpointRouteBuilder MapSeoEndpoints(
        this IEndpointRouteBuilder app,
        IConfiguration configuration)
    {
        var seo = configuration.GetSection(SeoOptions.SectionName).Get<SeoOptions>() ?? new SeoOptions();

        app.MapGet("/robots.txt", (IOptions<SeoOptions> options) =>
            {
                var value = options.Value;

                return Results.Text(BuildRobots(value), "text/plain; charset=utf-8");
            })
            .WithTags("Служебное")
            .AllowAnonymous();

        app.MapGet("/sitemap.xml", async (
                PublicCatalogService catalog,
                IOptions<SeoOptions> options,
                HttpContext context,
                CancellationToken ct) =>
            {
                var decks = await catalog.GetDecksAsync(ct);
                var xml = BuildSitemap(options.Value, decks);

                // Час кеша: колоды меняются импортом словаря, а не в процессе
                // работы, так что чаще пересобирать карту незачем.
                context.Response.Headers.CacheControl = "public, max-age=3600";

                return Results.Text(xml, "application/xml; charset=utf-8");
            })
            .WithTags("Служебное")
            .AllowAnonymous();

        // Загрузчик счётчика отдаётся отдельным файлом, а не вставкой в разметку:
        // главная страница — статический index.html, и номер счётчика попал бы
        // в него навсегда. Так номер меняется переменной окружения.
        app.MapGet("/seo.js", (IOptions<SeoOptions> options) =>
            {
                var value = options.Value;

                return Results.Text(BuildSeoScript(value), "text/javascript; charset=utf-8");
            })
            .WithTags("Служебное")
            .AllowAnonymous();

        MapVerificationFiles(app, seo);

        return app;
    }

    /// <summary>
    /// Файлы подтверждения прав: Яндекс и Google проверяют владение доменом,
    /// запрашивая файл с выданным именем в корне сайта.
    /// </summary>
    /// <remarks>
    /// Имя файла известно только после регистрации сайта в панели, поэтому
    /// маршруты регистрируются по значению настройки, а не универсальным
    /// шаблоном <c>/{file}</c>: шаблон перехватывал бы любой несуществующий
    /// адрес и отвечал бы на него вместо честного 404.
    /// </remarks>
    private static void MapVerificationFiles(IEndpointRouteBuilder app, SeoOptions seo)
    {
        var yandex = NormalizeFileName(seo.YandexVerificationFile);

        if (yandex is not null)
        {
            app.MapGet($"/{yandex}", () => Results.Text(YandexVerificationPage(yandex), "text/html; charset=utf-8"))
                .WithTags("Служебное")
                .AllowAnonymous();
        }

        var google = NormalizeFileName(seo.GoogleVerificationFile);

        if (google is not null)
        {
            // Содержимое этого файла задано Google дословно: проверяется сама
            // строка, а не наличие файла.
            app.MapGet($"/{google}", () => Results.Text($"google-site-verification: {google}", "text/html; charset=utf-8"))
                .WithTags("Служебное")
                .AllowAnonymous();
        }
    }

    /// <summary>
    /// Имя файла в виде, пригодном для маршрута, или <c>null</c>, если значение
    /// пустое или содержит символы, которые сломали бы шаблон адреса.
    /// </summary>
    private static string? NormalizeFileName(string value)
    {
        var name = value.Trim().TrimStart('/');

        if (name.Length == 0)
            return null;

        // «{» и «}» в шаблоне маршрута — это параметр, а «/» — граница сегмента.
        // Подставлять такие значения в маршрут нельзя: приложение упало бы
        // на регистрации, а не показало понятную ошибку настройки.
        return name.IndexOfAny(['{', '}', '/', '\\']) >= 0 ? null : name;
    }

    private static string BuildRobots(SeoOptions seo)
    {
        // Закрываются только те адреса, где обход не имеет смысла или вреден:
        // API, служебные проверки и страницы восстановления доступа — в адресе
        // сброса лежит одноразовый токен, и попадать в индекс он не должен.
        //
        // Разделы приложения не закрываются: они отвечают заголовком
        // X-Robots-Tag: noindex, а закрытый в robots.txt адрес робот не прочитает
        // и не увидит этого запрета.
        return $"""
            User-agent: *
            Allow: /
            Disallow: /api/
            Disallow: /reset
            Disallow: /forgot
            Disallow: /health

            # Yandex: метки переходов не создают отдельных страниц.
            Clean-param: utm_source&utm_medium&utm_campaign&utm_term&utm_content&yclid&gclid

            Sitemap: {seo.BaseUrl}/sitemap.xml
            """;
    }

    /// <summary>
    /// Карта сайта: главная, список тем и страница каждой колоды.
    /// </summary>
    /// <remarks>
    /// Разделы приложения в карту не входят намеренно: они закрыты входом,
    /// и робот увидел бы там пустую оболочку. Карта — это список того, что
    /// действительно стоит индексировать.
    /// </remarks>
    private static string BuildSitemap(SeoOptions seo, IReadOnlyList<PublicDeckSummaryDto> decks)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var baseUrl = seo.BaseUrl;

        static XElement Url(XNamespace ns, string loc, string changefreq, string priority) =>
            new(ns + "url",
                new XElement(ns + "loc", loc),
                new XElement(ns + "changefreq", changefreq),
                new XElement(ns + "priority", priority));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "urlset",
                Url(ns, $"{baseUrl}/", "weekly", "1.0"),
                Url(ns, $"{baseUrl}/decks", "weekly", "0.9"),
                decks.Select(deck => Url(ns, $"{baseUrl}/decks/{deck.Slug}", "monthly", "0.8"))));

        return document.Declaration + Environment.NewLine + document.ToString();
    }

    /// <summary>
    /// Загрузчик Яндекс.Метрики.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Это стандартный сниппет Метрики, но собранный на сервере: без счётчика
    /// (номер не задан) файл всё равно отдаётся, только пустой — тогда страница
    /// не грузит чужой скрипт зря. Номер живёт в настройках, а не в разметке,
    /// поэтому смена счётчика не требует правки страниц.
    /// </para>
    /// <para>
    /// Вебвизор включается настройкой <see cref="SeoOptions.MetrikaWebvisor"/>,
    /// а не по умолчанию: он записывает сессии посетителей, а это персональные
    /// данные, которым нужны согласие и политика. При включении берётся тот же
    /// адрес загрузчика с идентификатором, что выдаёт панель Метрики для
    /// счётчика с Вебвизором: файл записи отдаётся именно с <c>tag_ww.js</c>.
    /// </para>
    /// <para>
    /// Электронная коммерция (<c>ecommerce: "dataLayer"</c>) не подключается:
    /// в приложении нет оплаты, и Метрика ждала бы события, которых не будет.
    /// </para>
    /// </remarks>
    private static string BuildSeoScript(SeoOptions seo)
    {
        if (seo.MetrikaId is not { } id)
        {
            // Пустой файл вместо 404: страница ссылается на него всегда, и
            // ошибка в консоли на каждой загрузке выглядела бы как поломка.
            return "/* Счётчик Яндекс.Метрики не настроен: Seo:YandexMetrikaId пуст. */";
        }

        var webvisor = seo.MetrikaWebvisor;

        var tagUrl = webvisor
            ? $"https://mc.webvisor.org/metrika/tag_ww.js?id={id}"
            : "https://mc.yandex.ru/metrika/tag.js";

        // Параметры init. Вебвизор добавляется последним, чтобы список
        // включённого было видно одним взглядом на файл.
        var initOptions = webvisor
            ? """
                  clickmap: true,
                  trackLinks: true,
                  accurateTrackBounce: true,
                  webvisor: true
              """
            : """
                  clickmap: true,
                  trackLinks: true,
                  accurateTrackBounce: true
              """;

        return $$"""
            (function (m, e, t, r, i, k, a) {
                m[i] = m[i] || function () { (m[i].a = m[i].a || []).push(arguments); };
                m[i].l = 1 * new Date();
                for (var j = 0; j < document.scripts.length; j++) {
                    if (document.scripts[j].src === r) { return; }
                }
                k = e.createElement(t); a = e.getElementsByTagName(t)[0];
                k.async = 1; k.src = r; a.parentNode.insertBefore(k, a);
            })(window, document, 'script', '{{tagUrl}}', 'ym');

            ym({{id}}, 'init', {
            {{initOptions}}
            });

            /*
             * Просмотр страницы внутри приложения.
             *
             * Приложение меняет адрес без перезагрузки документа, поэтому
             * Метрика сама о переходе не узнает и все разделы посчитала бы
             * одной страницей. Роутер вызывает эту функцию после отрисовки;
             * referer передаётся вручную по той же причине — document.referrer
             * навсегда остался бы адресом входа на сайт.
             */
            window.__seoHit = function (url, title, referer) {
                if (typeof window.ym !== 'function') { return; }
                try {
                    window.ym({{id}}, 'hit', url, { title: title, referer: referer });
                } catch (e) {
                    /* Счётчик не должен ломать переход по сайту. */
                }
            };
            """;
    }

    /// <summary>
    /// Страница подтверждения прав для Яндекс.Вебмастера.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Разметка повторяет ту, которую Вебмастер показывает в инструкции, дословно:
    /// «Verification: токен» в теле и мета-тег с кодировкой в голове. Вебмастер
    /// проверяет и доступность адреса, и содержимое, поэтому «своя» страница
    /// с тем же токеном, но другим текстом — риск отказа на ровном месте.
    /// </para>
    /// <para>
    /// Токен берётся из имени файла: <c>yandex_76f62eca68cc377d.html</c> —
    /// <c>76f62eca68cc377d</c>. Второй раз хранить его в настройках не нужно,
    /// иначе имя и содержимое однажды разойдутся.
    /// </para>
    /// </remarks>
    private static string YandexVerificationPage(string fileName)
    {
        var token = fileName;
        const string prefix = "yandex_";
        const string suffix = ".html";

        if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            token = token[prefix.Length..];

        if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            token = token[..^suffix.Length];

        return $"""
            <html>
                <head>
                    <meta http-equiv="Content-Type" content="text/html; charset=UTF-8">
                </head>
                <body>Verification: {token}</body>
            </html>
            """;
    }
}
