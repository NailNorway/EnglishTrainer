using EnglishTrainer.Api.Authentication;
using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Services;

namespace EnglishTrainer.Api.Endpoints;

/// <summary>
/// Обучение: получение очереди карточек и отправка ответов.
/// </summary>
/// <remarks>
/// Эти два эндпоинта — сердце сервиса. Сессия создаётся на сервере и хранится в базе,
/// поэтому обновление страницы или переход на другое устройство не меняет набор слов.
/// </remarks>
public static class StudyEndpoints
{
    public static IEndpointRouteBuilder MapStudyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/study")
            .WithTags("Обучение")
            .RequireAuthorization();

        group.MapGet("/session", async (
            HttpContext context,
            int? deckId,
            bool? cram,
            bool? onlyDue,
            StudySessionService service,
            CancellationToken ct) =>
        {
            var session = await service.GetOrCreateSessionAsync(
                context.User.GetUserId(), deckId, cram ?? false, onlyDue ?? false, ct);

            return Results.Ok(session);
        })
        .WithSummary("Текущая сессия или новая очередь карточек")
        .WithDescription(
            "Возвращает активную сессию, если она есть. Иначе собирает новую: " +
            "просроченные повторения плюс новые слова в пределах дневного лимита. " +
            "Правильного ответа в ответе нет — он проверяется на сервере. " +
            "Если повторять нечего, сессия не создаётся: приходит пустая очередь " +
            "с датой ближайшего повторения. Параметр cram=true собирает очередь " +
            "вне расписания — карточки с самым близким сроком. onlyDue=true " +
            "оставляет только просроченные повторения и не расходует лимит новых слов.");

        group.MapGet("/session/active", async (
            HttpContext context,
            StudySessionService service,
            CancellationToken ct) =>
        {
            var session = await service.GetActiveSessionAsync(context.User.GetUserId(), ct);

            // 204 вместо пустого объекта: «сессии нет» — это отсутствие ресурса,
            // и клиенту не нужно разбирать поля, чтобы это понять.
            return session is null ? Results.NoContent() : Results.Ok(session);
        })
        .WithSummary("Активная сессия, если она есть")
        .WithDescription(
            "Ничего не создаёт и не расходует дневной лимит новых слов — в отличие от " +
            "обычного запроса сессии. Нужен, чтобы показать «продолжить» на списке колод.");

        group.MapPost("/favorites", async (
            HttpContext context,
            StudySessionService service,
            CancellationToken ct) =>
        {
            var session = await service.StartFavoritesSessionAsync(context.User.GetUserId(), ct);
            return Results.Ok(session);
        })
        .WithSummary("Тренировка только по избранным словам")
        .WithDescription(
            "Избранные слова не ждут назначенного срока — иначе «повторить перед " +
            "собеседованием» было бы невозможно. Текущая активная сессия закрывается: " +
            "две очереди одновременно держать нельзя.");

        group.MapPost("/review", async (
            HttpContext context,
            SubmitAnswerRequestDto request,
            StudySessionService service,
            CancellationToken ct) =>
        {
            var result = await service.SubmitAnswerAsync(context.User.GetUserId(), request, ct);
            return Results.Ok(result);
        })
        .WithSummary("Ответ на карточку")
        .WithDescription(
            "Проверяет ответ, пересчитывает интервал повторения и возвращает разбор " +
            "вместе со следующей карточкой.");

        group.MapDelete("/session", async (
            HttpContext context,
            StudySessionService service,
            CancellationToken ct) =>
        {
            await service.AbandonSessionAsync(context.User.GetUserId(), ct);
            return Results.NoContent();
        })
        .WithSummary("Прервать сессию")
        .WithDescription("Незавершённая сессия помечается брошенной, чтобы не блокировать следующую.");

        return app;
    }
}
