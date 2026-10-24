using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Enums;
using EnglishTrainer.Domain.Repositories;
using EnglishTrainer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EnglishTrainer.Infrastructure.Repositories;

/// <summary>Состояния карточек: главный источник данных для сборки сессии.</summary>
public sealed class CardStateRepository(AppDbContext context) : ICardStateRepository
{
    public Task<CardState?> GetAsync(int userId, int wordId, CancellationToken ct = default) =>
        context.CardStates.FirstOrDefaultAsync(c => c.UserId == userId && c.WordId == wordId, ct);

    public async Task<IReadOnlyList<CardState>> GetDueAsync(
        int userId, DateTime nowUtc, int limit, int? deckId = null, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        // ВАЖНО: сортировка по DueAt (когда карточка стала доступна), а не по
        // IntervalDays. Сортировка по интервалу дала бы обратный порядок:
        // давно выученные слова с большим интервалом вытеснили бы свежие.
        // Именно под этот запрос создан индекс (UserId, DueAt).
        var query = context.CardStates.Where(c => c.UserId == userId && c.DueAt <= nowUtc);

        return await InDeck(query, deckId)
            .OrderBy(c => c.DueAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public Task<int> CountDueAsync(int userId, DateTime nowUtc, CancellationToken ct = default) =>
        context.CardStates.CountAsync(c => c.UserId == userId && c.DueAt <= nowUtc, ct);

    public async Task<DateTime?> GetNextDueAtAsync(
        int userId, DateTime nowUtc, CancellationToken ct = default) =>
        await context.CardStates
            .Where(c => c.UserId == userId && c.DueAt > nowUtc)
            .OrderBy(c => c.DueAt)
            .Select(c => (DateTime?)c.DueAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CardState>> GetSoonestAsync(
        int userId, int limit, int? deckId = null, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        // Тот же индекс (UserId, DueAt), что и у выборки просроченных: отличается
        // только отсутствие условия на срок.
        var query = context.CardStates.Where(c => c.UserId == userId);

        return await InDeck(query, deckId)
            .OrderBy(c => c.DueAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Ограничивает выборку колодой, если человек выбрал её явно.
    /// </summary>
    /// <remarks>
    /// Фильтр добавляется в запрос только при выбранной колоде: долг по повторениям
    /// общий для пользователя, и общая тренировка по-прежнему берёт слова отовсюду.
    /// Проверка идёт через <c>deck_words</c>, а не через отдельный идентификатор
    /// в состоянии карточки: слово может лежать сразу в нескольких колодах, и
    /// «колода карточки» — не свойство карточки, а свойство связи.
    /// </remarks>
    private IQueryable<CardState> InDeck(IQueryable<CardState> query, int? deckId) =>
        deckId is { } id
            ? query.Where(c => context.DeckWords.Any(dw => dw.DeckId == id && dw.WordId == c.WordId))
            : query;

    public async Task<IReadOnlyList<CardState>> GetForWordsAsync(
        int userId, IReadOnlyCollection<int> wordIds, CancellationToken ct = default)
    {
        if (wordIds.Count == 0)
            return [];

        return await context.CardStates
            .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task AddAsync(CardState cardState, CancellationToken ct = default)
    {
        await context.CardStates.AddAsync(cardState, ct);
    }

    public async Task AddRangeAsync(IEnumerable<CardState> cardStates, CancellationToken ct = default)
    {
        await context.CardStates.AddRangeAsync(cardStates, ct);
    }

    public async Task<CardStateSummary> GetSummaryAsync(
        int userId, DateTime nowUtc, CancellationToken ct = default)
    {
        // Порог «выучено» вычисляется на сервере: пересылать DueAt всех карточек
        // ради одной цифры — заведомо плохая идея.
        // SingleOrDefault, а не FirstOrDefault: группировка без условия даёт ровно
        // одну строку (или ни одной). First здесь ещё и вызывал предупреждение EF
        // о запросе без OrderBy — на каждый запрос статистики.
        var row = await context.CardStates
            .Where(c => c.UserId == userId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                // «Выучено» считается по интервалу — по тому же признаку, что и
                // CardState.IsLearned и прогресс колоды. Раньше здесь стояло
                // условие «срок повторения дальше чем через месяц», и это было
                // второе определение того же слова: карточка с интервалом ровно
                // 30 дней попадала в «выучено» в колоде и не попадала в сводку.
                // Два определения одного числа расходятся — это не теория,
                // а разные цифры на двух экранах.
                Learned = g.Count(c => c.IntervalDays >= CardState.LearnedIntervalDays),
                DueNow = g.Count(c => c.DueAt <= nowUtc)
            })
            .SingleOrDefaultAsync(ct);

        return row is null
            ? new CardStateSummary(0, 0, 0)
            : new CardStateSummary(row.Total, row.Learned, row.DueNow);
    }

    public async Task<IReadOnlyList<CardState>> GetProgressAsync(
        int userId, WordProgressScope scope, DateTime nowUtc, CancellationToken ct = default)
    {
        var query = context.CardStates.Where(c => c.UserId == userId);

        query = scope switch
        {
            WordProgressScope.InProgress =>
                query.Where(c => c.IntervalDays < CardState.LearnedIntervalDays),
            WordProgressScope.Due =>
                query.Where(c => c.DueAt <= nowUtc),
            WordProgressScope.Learned =>
                query.Where(c => c.IntervalDays >= CardState.LearnedIntervalDays),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Неизвестный срез слов.")
        };

        // У выученных срока «пора» уже нет; пользователю полезнее видеть
        // недавно повторённые. У остальных ближайший срок — главный ориентир.
        query = scope == WordProgressScope.Learned
            ? query.OrderByDescending(c => c.LastReviewedAt ?? DateTime.MinValue)
                .ThenBy(c => c.WordId)
            : query.OrderBy(c => c.DueAt).ThenBy(c => c.WordId);

        return await query.AsNoTracking().ToListAsync(ct);
    }
}
