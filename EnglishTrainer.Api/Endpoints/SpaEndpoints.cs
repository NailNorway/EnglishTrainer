using Microsoft.AspNetCore.Mvc;

namespace EnglishTrainer.Api.Endpoints;

/// <summary>
/// Адреса интерфейса приложения.
/// </summary>
/// <remarks>
/// <para>
/// Оболочка приложения — один документ <c>index.html</c>, который сам решает,
/// что показать по адресу. Раньше адрес жил в хеше (<c>/#/friends</c>), и сервер
/// о разделах не знал вовсе. Теперь адреса обычные, поэтому каждый из них нужно
/// отдать серверу — иначе прямой заход на <c>/friends</c> или обновление
/// страницы (F5) вернуло бы 404.
/// </para>
/// <para>
/// Маршруты перечислены явно, а не шаблоном-перехватчиком на любой путь.
/// Перехватчик отвечал бы 200 на любой несуществующий адрес, и поисковик получил
/// бы бесконечное число «мягких» копий главной — это типовая причина потери
/// позиций, которую потом трудно заметить.
/// </para>
/// <para>
/// Разделы закрыты входом, поэтому каждый ответ несёт <c>noindex</c>: в выдаче
/// им делать нечего, а без заголовка робот проиндексировал бы пустую оболочку
/// и счёл бы сайт набором страниц без содержания.
/// </para>
/// </remarks>
public static class SpaEndpoints
{
    private static readonly string[] Routes =
    [
        "/study",
        "/favorites",
        "/friends",
        "/settings",
        "/profile",
        "/updates",
        "/forgot",
        "/reset",
        "/words",
        "/words/{scope}",
        "/user/{username}"
    ];

    public static IEndpointRouteBuilder MapSpaEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var route in Routes)
        {
            app.MapGet(route, (
                    HttpContext context,
                    [FromServices] IWebHostEnvironment environment) =>
                {
                    context.Response.Headers["X-Robots-Tag"] = "noindex, follow";

                    // Оболочка не кешируется: номер версии и состояние входа
                    // меняются, а браузер не должен показывать прошлую копию.
                    context.Response.Headers.CacheControl = "no-cache, must-revalidate";

                    var indexPath = Path.Combine(environment.WebRootPath, "index.html");
                    return Results.File(indexPath, "text/html; charset=utf-8");
                })
                .WithTags("Интерфейс")
                .AllowAnonymous();
        }

        return app;
    }
}
