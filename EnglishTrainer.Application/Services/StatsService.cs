using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Mapping;
using EnglishTrainer.Domain.Abstractions;
using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Enums;
using EnglishTrainer.Domain.Exceptions;
using EnglishTrainer.Domain.Repositories;
using EnglishTrainer.Domain.Stats;

namespace EnglishTrainer.Application.Services;

/// <summary>Прогресс пользователя: сводка, активность и кривая удержания.</summary>
public sealed class StatsService(
    IUserRepository users,
    ICardStateRepository cardStates,
    IDailyStatRepository dailyStats,
    IReviewLogRepository reviewLogs,
    IWordRepository words,
    IClock clock)
{
    private const int RetentionWindowDays = 30;

    public async Task<StatsSummaryDto> GetSummaryAsync(int userId, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        var nowUtc = clock.UtcNow;
        var summary = await cardStates.GetSummaryAsync(userId, nowUtc, ct);
        var recent = await dailyStats.GetRecentAsync(userId, 90, ct);

        var localDate = clock.TodayFor(user.TimeZoneId);
        var today = recent.FirstOrDefault(d => d.Date == localDate);

        var streak = StreakCalculator.Calculate(StreakCalculator.ActiveDates(recent), localDate);

        var fromUtc = nowUtc.AddDays(-RetentionWindowDays);
        var retention = await reviewLogs.GetRetentionAsync(userId, fromUtc, ct);
        var reviewed = retention.Sum(p => p.Reviewed);
        var correct = retention.Sum(p => p.Correct);

        return new StatsSummaryDto(
            summary.Total,
            summary.Learned,
            Math.Max(0, summary.Total - summary.Learned),
            summary.DueNow,
            streak.Current,
            streak.Longest,
            today?.ReviewedCount ?? 0,
            reviewed == 0 ? 0 : (int)Math.Round(correct * 100.0 / reviewed),
            // Цель отдаётся вместе со сводкой: день идёт в серию только при её
            // выполнении, и человек должен видеть, сколько ответов до неё осталось.
            (user.Settings ?? new UserSettings()).DailyGoal);
    }

    /// <summary>
    /// Слова для страниц прогресса: «в работе», «пора повторить», «выучено».
    /// </summary>
    /// <remarks>
    /// Состояния и слова читаются двумя запросами, а не join-ом в репозитории:
    /// словарь общий, и его удобнее доставать тем же методом, что и очередь
    /// тренировки — с переводами и примерами, без N+1.
    /// </remarks>
    public async Task<IReadOnlyList<WordProgressDto>> GetWordsAsync(
        int userId, WordProgressScope scope, CancellationToken ct = default)
    {
        var states = await cardStates.GetProgressAsync(userId, scope, clock.UtcNow, ct);

        if (states.Count == 0)
            return [];

        var details = await words.GetWithDetailsByIdsAsync(
            [.. states.Select(state => state.WordId)], ct);

        var result = new List<WordProgressDto>(states.Count);

        foreach (var state in states)
        {
            if (!details.TryGetValue(state.WordId, out var word))
                continue;

            result.Add(new WordProgressDto(
                word.ToDto(),
                state.Phase,
                state.IsLearned,
                state.IntervalDays,
                state.DueAt,
                state.LastReviewedAt));
        }

        return result;
    }

    public async Task<ActivityDto> GetActivityAsync(
        int userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        if (to < from)
            throw new ValidationException("Дата окончания периода раньше даты начала.");

        if (to.DayNumber - from.DayNumber > 366)
            throw new ValidationException("Период не должен превышать 366 дней.");

        var stats = await dailyStats.GetRangeAsync(userId, from, to, ct);
        var statsByDate = stats.ToDictionary(s => s.Date);

        // Дни без занятий тоже возвращаем — иначе график «склеивает» пропуски
        // и создаёт ложное ощущение регулярности.
        var days = new List<DailyStatDto>(to.DayNumber - from.DayNumber + 1);
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var stat = statsByDate.GetValueOrDefault(date);
            days.Add(new DailyStatDto(
                date,
                stat?.ReviewedCount ?? 0,
                stat?.NewWordsStarted ?? 0,
                stat?.CorrectCount ?? 0,
                stat?.Accuracy));
        }

        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var retention = await reviewLogs.GetRetentionAsync(userId, fromUtc, ct);

        return new ActivityDto(
            from,
            to,
            days,
            retention
                .Select(p => new RetentionPointDto(
                    p.Date,
                    p.Reviewed,
                    p.Correct,
                    p.Reviewed == 0 ? 0 : Math.Round((double)p.Correct / p.Reviewed, 3)))
                .ToList());
    }
}
