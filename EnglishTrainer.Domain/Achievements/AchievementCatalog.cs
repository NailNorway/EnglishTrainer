namespace EnglishTrainer.Domain.Achievements;

/// <summary>
/// Показатель, по которому измеряется достижение.
/// </summary>
/// <remarks>
/// Перечисление, а не строка: набор показателей закрыт, и опечатка в строке
/// не должна превращать достижение в вечно заблокированное. Значения
/// считаются из уже накопленных данных — отдельных счётчиков для достижений
/// не заводится, иначе они рано или поздно разойдутся с настоящим прогрессом.
/// </remarks>
public enum AchievementMetric
{
    /// <summary>Сколько слов начато — любое состояние карточки.</summary>
    StartedWords,

    /// <summary>Сколько слов выучено: интервал повторения превысил порог.</summary>
    LearnedWords,

    /// <summary>
    /// Лучшая серия дней подряд.
    /// </summary>
    /// <remarks>
    /// Именно лучшая, а не текущая. Награда за семь дней подряд не должна
    /// отбираться из-за одного пропущенного дня: награда, которую можно
    /// потерять, перестаёт мотивировать и начинает пугать.
    /// </remarks>
    LongestStreak,

    /// <summary>Сколько ответов дано за всё время.</summary>
    TotalReviews,

    /// <summary>Сколько колод выучено целиком.</summary>
    CompletedDecks
}

/// <summary>Описание достижения.</summary>
/// <param name="Code">Устойчивый код для хранения и сравнения. Менять нельзя.</param>
/// <param name="Title">Короткое имя для плашки.</param>
/// <param name="Description">Что нужно сделать.</param>
/// <param name="Icon">
/// Своя иконка достижения. Хранится строкой, чтобы каталог оставался данными,
/// а интерфейс не разрастался отдельной таблицей соответствий «код → картинка».
/// </param>
/// <param name="Metric">По какому показателю измеряется.</param>
/// <param name="Target">Порог показателя.</param>
public sealed record AchievementDefinition(
    string Code,
    string Title,
    string Description,
    string Icon,
    AchievementMetric Metric,
    int Target);

/// <summary>Значения показателей на момент проверки.</summary>
/// <remarks>
/// Значения могут быть загружены не все: проверка читает только те, что нужны
/// ещё не полученным достижениям. Отсутствующий показатель равен нулю — это
/// безопасно, потому что достижение тогда просто остаётся заблокированным,
/// а не выдаётся ошибочно.
/// </remarks>
public sealed class AchievementMetrics
{
    private readonly Dictionary<AchievementMetric, int> _values;

    public AchievementMetrics(IEnumerable<KeyValuePair<AchievementMetric, int>> values) =>
        _values = values.ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>Удобный набор всех показателей сразу — для тестов и для страницы достижений.</summary>
    public AchievementMetrics(
        int startedWords, int learnedWords, int longestStreak, int totalReviews, int completedDecks)
        : this(new Dictionary<AchievementMetric, int>
        {
            [AchievementMetric.StartedWords] = startedWords,
            [AchievementMetric.LearnedWords] = learnedWords,
            [AchievementMetric.LongestStreak] = longestStreak,
            [AchievementMetric.TotalReviews] = totalReviews,
            [AchievementMetric.CompletedDecks] = completedDecks
        })
    {
    }

    /// <summary>Значение показателя. Незагруженный показатель равен нулю.</summary>
    public int this[AchievementMetric metric] =>
        _values.TryGetValue(metric, out var value) ? value : 0;

    public bool Has(AchievementMetric metric) => _values.ContainsKey(metric);
}

/// <summary>
/// Каталог достижений.
/// </summary>
/// <remarks>
/// <para>
/// Список задан кодом, а не таблицей в базе. Причина простая: достижение —
/// это правило («выучено 100 слов»), а правило, лежащее в данных, нельзя
/// ни отревьюить, ни покрыть тестом, ни изменить вместе с логикой, по которой
/// оно считается.
/// </para>
/// <para>
/// В базе хранятся только полученные достижения — с датой, потому что «когда
/// получил» и есть то, чем хочется похвастаться. Повторная выдача невозможна:
/// уникальный индекс по паре «пользователь + код».
/// </para>
/// </remarks>
public static class AchievementCatalog
{
    /// <summary>
    /// Все достижения в порядке от простого к сложному.
    /// </summary>
    /// <remarks>
    /// Порядок — часть замысла: интерфейс показывает список как путь, и первым
    /// должно идти то, что достижимо в первый же день. Награда, до которой
    /// месяц работы, мотивирует только после того, как получена первая.
    /// </remarks>
    public static readonly IReadOnlyList<AchievementDefinition> All =
    [
        new("first-step", "Первый шаг", "Начать первое слово", "🎓", AchievementMetric.StartedWords, 1),
        new("words-10", "Десять слов", "Выучить 10 слов", "🔟", AchievementMetric.LearnedWords, 10),
        new("streak-3", "Три дня", "Заниматься 3 дня подряд", "🔥", AchievementMetric.LongestStreak, 3),
        new("reviews-100", "Сто ответов", "Ответить на 100 карточек", "💯", AchievementMetric.TotalReviews, 100),
        new("started-50", "Пятьдесят слов", "Начать 50 слов", "🌱", AchievementMetric.StartedWords, 50),
        new("words-100", "Сотня", "Выучить 100 слов", "🏅", AchievementMetric.LearnedWords, 100),
        new("streak-7", "Неделя без пропусков", "Заниматься 7 дней подряд", "📅", AchievementMetric.LongestStreak, 7),
        new("reviews-500", "Пятьсот ответов", "Ответить на 500 карточек", "🎯", AchievementMetric.TotalReviews, 500),
        new("deck-master", "Колода пройдена", "Выучить все слова колоды", "📚", AchievementMetric.CompletedDecks, 1),
        new("streak-14", "Две недели", "Заниматься 14 дней подряд", "🌟", AchievementMetric.LongestStreak, 14),
        new("words-250", "Двести пятьдесят слов", "Выучить 250 слов", "🚀", AchievementMetric.LearnedWords, 250),
        new("reviews-1000", "Тысяча ответов", "Ответить на 1000 карточек", "🏆", AchievementMetric.TotalReviews, 1000),
        new("decks-3", "Три колоды", "Выучить целиком три колоды", "👑", AchievementMetric.CompletedDecks, 3),
        new("streak-30", "Месяц занятий", "Заниматься 30 дней подряд", "🗓️", AchievementMetric.LongestStreak, 30),
        new("reviews-5000", "Пять тысяч ответов", "Ответить на 5000 карточек", "💎", AchievementMetric.TotalReviews, 5000)
    ];

    /// <summary>Показатели, которые вообще встречаются в каталоге.</summary>
    public static readonly IReadOnlyList<AchievementMetric> Metrics =
        [.. All.Select(achievement => achievement.Metric).Distinct()];

    public static AchievementDefinition? ByCode(string? code) =>
        All.FirstOrDefault(achievement => achievement.Code == code);

    /// <summary>Достигнут ли порог.</summary>
    public static bool IsReached(this AchievementDefinition definition, AchievementMetrics metrics) =>
        metrics[definition.Metric] >= definition.Target;

    /// <summary>
    /// Доля выполнения от нуля до единицы — для полоски прогресса.
    /// </summary>
    public static double Progress(this AchievementDefinition definition, AchievementMetrics metrics) =>
        definition.Target <= 0
            ? 1
            : Math.Clamp((double)metrics[definition.Metric] / definition.Target, 0, 1);
}
