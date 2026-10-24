using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Domain.Achievements;
using EnglishTrainer.Domain.Abstractions;
using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Exceptions;
using EnglishTrainer.Domain.Repositories;
using EnglishTrainer.Domain.Stats;

namespace EnglishTrainer.Application.Services;

/// <summary>
/// Достижения: проверка порогов и выдача наград.
/// </summary>
/// <remarks>
/// <para>
/// Прогресс не хранится, а считается из уже накопленных данных — состояний
/// карточек, дневной статистики и журнала ответов. Соблазн завести отдельные
/// счётчики велик, но у двух источников одного числа есть неприятное свойство:
/// они расходятся. Здесь расхождение невозможно по построению.
/// </para>
/// <para>
/// Проверка идёт при чтении достижений, то есть когда человек открыл страницу,
/// а не после каждого ответа. Это осознанный компромисс: награда подсвечивается
/// на несколько секунд позже, чем была заработана, зато самый горячий путь
/// приложения не обрастает лишними запросами к базе. Страница тренировки
/// запрашивает достижения при каждой отрисовке, поэтому после законченной
/// сессии награда появляется сразу.
/// </para>
/// </remarks>
public sealed class AchievementService(
    IUserAchievementRepository achievements,
    IUserRepository users,
    ICardStateRepository cardStates,
    IDailyStatRepository dailyStats,
    IReviewLogRepository reviewLogs,
    IDeckRepository decks,
    IClock clock)
{
    /// <summary>Глубина истории для подсчёта серии дней — та же, что в профиле.</summary>
    private const int StreakWindowDays = 180;

    /// <summary>
    /// Проверяет пороги и выдаёт то, что уже заработано.
    /// </summary>
    /// <returns>Выданные достижения — их можно показать человеку прямо сейчас.</returns>
    public async Task<IReadOnlyList<AchievementDefinition>> EvaluateAsync(
        int userId, CancellationToken ct = default)
    {
        var unlocked = await achievements.GetCodesAsync(userId, ct);
        var pending = AchievementCatalog.All
            .Where(achievement => !unlocked.Contains(achievement.Code))
            .ToList();

        // Всё получено — дальше считать нечего. Этот выход срабатывает почти
        // всегда, и именно он делает проверку дешёвой.
        if (pending.Count == 0)
        {
            return [];
        }

        var metrics = await MeasureAsync(
            userId, [.. pending.Select(achievement => achievement.Metric).Distinct()], ct);

        var reached = pending.Where(achievement => achievement.IsReached(metrics)).ToList();

        if (reached.Count == 0)
        {
            return [];
        }

        await achievements.UnlockAsync(
            userId, [.. reached.Select(achievement => achievement.Code)], clock.UtcNow, ct);

        return reached;
    }

    /// <summary>Все достижения с прогрессом — для страницы достижений.</summary>
    public async Task<AchievementsDto> GetAsync(int userId, CancellationToken ct = default)
    {
        // Сначала выдача, потом чтение: иначе достижение, заработанное до
        // появления этой проверки, показывалось бы закрытым до следующего захода.
        await EvaluateAsync(userId, ct);

        var unlocked = (await achievements.GetAsync(userId, ct))
            .ToDictionary(achievement => achievement.Code, achievement => achievement.UnlockedAt);

        var metrics = await MeasureAsync(userId, AchievementCatalog.Metrics, ct);

        var items = AchievementCatalog.All
            .Select(definition => ToDto(definition, metrics, unlocked))
            .ToList();

        return new AchievementsDto(
            items,
            items.Count(item => item.Unlocked),
            items.Count,
            NextGoal(items));
    }

    /// <summary>
    /// Ближайшая незакрытая цель.
    /// </summary>
    /// <remarks>
    /// Выбирается по доле выполнения, а не по «осталось меньше всего»: «три
    /// из ста» и «девять из десяти» дают разное ощущение близости, и правильное
    /// передаёт как раз доля. При равной доле выигрывает цель с меньшим порогом —
    /// её достижимость понятнее.
    /// </remarks>
    private static AchievementDto? NextGoal(IReadOnlyList<AchievementDto> items) =>
        items.Where(item => !item.Unlocked && item.Target > 0)
            .OrderByDescending(item => (double)item.Progress / item.Target)
            .ThenBy(item => item.Target)
            .FirstOrDefault();

    private static AchievementDto ToDto(
        AchievementDefinition definition,
        AchievementMetrics metrics,
        IReadOnlyDictionary<string, DateTime> unlocked) =>
        new(
            definition.Code,
            definition.Title,
            definition.Description,
            definition.Icon,
            definition.Target,
            // Прогресс обрезается по порогу: «120 из 100» читается как ошибка,
            // а не как перевыполнение — награда не выдаётся дважды.
            Math.Min(metrics[definition.Metric], definition.Target),
            unlocked.ContainsKey(definition.Code),
            unlocked.TryGetValue(definition.Code, out var unlockedAt) ? unlockedAt : null);

    /// <summary>
    /// Считает показатели, которые нужны незакрытым достижениям.
    /// </summary>
    /// <remarks>
    /// Загружается только необходимое. К моменту, когда у человека закрыты
    /// «сотня слов» и «тысяча ответов», соответствующие счётчики уже никто
    /// не спрашивает, и лишние запросы к базе не выполняются.
    /// </remarks>
    private async Task<AchievementMetrics> MeasureAsync(
        int userId, IReadOnlyCollection<AchievementMetric> needed, CancellationToken ct)
    {
        var values = new Dictionary<AchievementMetric, int>();

        if (needed.Contains(AchievementMetric.StartedWords) || needed.Contains(AchievementMetric.LearnedWords))
        {
            // Одна сводка закрывает оба показателя: начатые и выученные слова
            // считаются по одной и той же таблице состояний.
            var summary = await cardStates.GetSummaryAsync(userId, clock.UtcNow, ct);

            values[AchievementMetric.StartedWords] = summary.Total;
            values[AchievementMetric.LearnedWords] = summary.Learned;
        }

        if (needed.Contains(AchievementMetric.TotalReviews))
        {
            values[AchievementMetric.TotalReviews] = await reviewLogs.CountAsync(userId, ct);
        }

        if (needed.Contains(AchievementMetric.CompletedDecks))
        {
            values[AchievementMetric.CompletedDecks] =
                await decks.CountCompletedAsync(userId, CardState.LearnedIntervalDays, ct);
        }

        if (needed.Contains(AchievementMetric.LongestStreak))
        {
            var user = await users.GetByIdAsync(userId, ct)
                ?? throw NotFoundException.For("Пользователь", userId);

            var recent = await dailyStats.GetRecentAsync(userId, StreakWindowDays, ct);

            // «Сегодня» берётся в поясе пользователя: серия, оборванная вчера
            // по UTC, для человека в UTC+13 ещё продолжается.
            var streak = StreakCalculator.Calculate(
                StreakCalculator.ActiveDates(recent), clock.TodayFor(user.TimeZoneId));

            values[AchievementMetric.LongestStreak] = streak.Longest;
        }

        return new AchievementMetrics(values);
    }
}
