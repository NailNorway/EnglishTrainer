using EnglishTrainer.Api.Authentication;
using EnglishTrainer.Application.Services;
using EnglishTrainer.Domain.Enums;
using EnglishTrainer.Domain.Exceptions;

namespace EnglishTrainer.Api.Endpoints;

/// <summary>Прогресс: сводка, активность по дням и достижения.</summary>
public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stats")
            .WithTags("Прогресс")
            .RequireAuthorization();

        group.MapGet("/summary", async (
            HttpContext context,
            StatsService service,
            CancellationToken ct) =>
        {
            var summary = await service.GetSummaryAsync(context.User.GetUserId(), ct);
            return Results.Ok(summary);
        })
        .WithSummary("Сводка прогресса")
        .WithDescription("Выучено, в работе, к повторению, серия дней и удержание за 30 дней.");

        group.MapGet("/words", async (
            string? scope,
            HttpContext context,
            StatsService service,
            CancellationToken ct) =>
        {
            var parsed = scope switch
            {
                "in-progress" => WordProgressScope.InProgress,
                "due" => WordProgressScope.Due,
                "learned" => WordProgressScope.Learned,
                _ => throw new ValidationException(
                    "Неизвестный срез слов. Допустимо: in-progress, due, learned.")
            };

            var words = await service.GetWordsAsync(context.User.GetUserId(), parsed, ct);
            return Results.Ok(words);
        })
        .WithSummary("Слова для страниц прогресса")
        .WithDescription(
            "Слова с переводом, транскрипцией и сроком повторения. " +
            "scope: in-progress — начатые, due — пора повторить, learned — выученные.");

        group.MapGet("/activity", async (
            HttpContext context,
            DateOnly? from,
            DateOnly? to,
            StatsService service,
            CancellationToken ct) =>
        {
            // Период по умолчанию — последние 30 дней: это то, что нужно
            // в 95% случаев, и клиенту не приходится считать даты самому.
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var range = new DateRange(from ?? today.AddDays(-29), to ?? today);

            var activity = await service.GetActivityAsync(context.User.GetUserId(), range.From, range.To, ct);
            return Results.Ok(activity);
        })
        .WithSummary("Активность по дням и кривая удержания")
        .WithDescription("Дни без занятий возвращаются с нулями, чтобы на графике были видны пропуски.");

        // Достижения лежат под /api/stats, а не под своим корнем: это тот же
        // прогресс, только поданный наградами. Отдельный корень заставлял бы
        // клиента держать в голове ещё одну ветку маршрутов ради одного метода.
        app.MapGet("/api/achievements", async (
            HttpContext context,
            AchievementService service,
            CancellationToken ct) =>
        {
            var achievements = await service.GetAsync(context.User.GetUserId(), ct);
            return Results.Ok(achievements);
        })
        .WithTags("Прогресс")
        .RequireAuthorization()
        .WithSummary("Достижения с прогрессом и ближайшая цель")
        .WithDescription(
            "Чтение выдаёт заработанное: прогресс считается из накопленных данных, " +
            "а не хранится отдельно, поэтому достижение, порог которого уже пройден, " +
            "закрывается при первом же обращении.");

        return app;
    }

    /// <summary>Период запроса. Отдельный тип нужен, чтобы не тащить два параметра через сигнатуру.</summary>
    private readonly record struct DateRange(DateOnly From, DateOnly To);
}
