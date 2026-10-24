using EnglishTrainer.Domain.Achievements;

namespace EnglishTrainer.UnitTests.Domain;

/// <summary>
/// Каталог достижений и правила их выдачи.
/// </summary>
/// <remarks>
/// <para>
/// Каталог — это данные, от которых зависит и база (код достижения хранится
/// строкой), и интерфейс, и текст награды. Ошибка в нём не падает и не видна
/// в логах: просто одна из наград никогда не выдаётся или, хуже, две награды
/// получают один код и вторая затирает первую. Поэтому проверяется структура,
/// а не только арифметика порога.
/// </para>
/// <para>
/// Отдельно закреплено устройство частичной загрузки показателей: значения,
/// которых проверка не запрашивала, обязаны читаться как ноль. На этом держится
/// обещание «достижение не выдаётся ошибочно, если показатель не считали».
/// </para>
/// </remarks>
public sealed class AchievementCatalogTests
{
    [Fact]
    public void AllCodes_AreUnique()
    {
        // Код — ключ в базе. Дубликат означал бы, что второе достижение
        // с тем же кодом никогда не будет выдано: уникальный индекс отклонит
        // вставку, а проверка посчитает награду уже полученной.
        var duplicates = AchievementCatalog.All
            .GroupBy(achievement => achievement.Code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void AllCodes_AreNonEmptyAndWithinColumnLength()
    {
        // Длина столбца в базе — 64 символа. Код длиннее не влезет, и падение
        // случилось бы при вставке, то есть у пользователя, а не при сборке.
        Assert.All(AchievementCatalog.All, achievement =>
        {
            Assert.False(string.IsNullOrWhiteSpace(achievement.Code));
            Assert.InRange(achievement.Code.Length, 1, 64);
        });
    }

    [Fact]
    public void AllAchievements_HaveTitleDescriptionAndPositiveTarget()
    {
        Assert.All(AchievementCatalog.All, achievement =>
        {
            Assert.False(string.IsNullOrWhiteSpace(achievement.Title));
            Assert.False(string.IsNullOrWhiteSpace(achievement.Description));
            Assert.True(achievement.Target > 0, $"У достижения '{achievement.Code}' порог не положительный.");
        });
    }

    [Fact]
    public void AllAchievements_HaveUniqueNonEmptyIcons()
    {
        // Иконка — часть различимости наград. Два одинаковых значка у разных
        // достижений снова превращают список в ряд одинаковых медалей, ради
        // чего и убирались общие 🏆/🔒.
        Assert.All(AchievementCatalog.All, achievement =>
            Assert.False(string.IsNullOrWhiteSpace(achievement.Icon)));

        var duplicates = AchievementCatalog.All
            .GroupBy(achievement => achievement.Icon, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Metrics_CoverEveryMetricUsedByCatalogue()
    {
        // Список показателей нужен странице достижений: она считает сразу все.
        // Показатель, использованный в каталоге и забытый в списке, дал бы
        // вечный ноль в прогресс-баре.
        Assert.All(AchievementCatalog.All, achievement =>
            Assert.Contains(achievement.Metric, AchievementCatalog.Metrics));
    }

    [Fact]
    public void FirstAchievement_IsReachableOnTheFirstDay()
    {
        // Порядок каталога — часть замысла: первым идёт то, что закрывается
        // в первый же день. Награда, до которой месяц работы, мотивирует
        // только после того, как получена первая.
        var first = AchievementCatalog.All[0];

        Assert.Equal(AchievementMetric.StartedWords, first.Metric);
        Assert.Equal(1, first.Target);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void IsReached_ComparesProgressWithTarget(int learned, bool expected)
    {
        var definition = AchievementCatalog.ByCode("words-10");

        Assert.NotNull(definition);

        var metrics = new AchievementMetrics(
            startedWords: learned, learnedWords: learned, longestStreak: 0, totalReviews: 0, completedDecks: 0);

        Assert.Equal(expected, definition!.IsReached(metrics));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0.5)]
    [InlineData(10, 1)]
    [InlineData(25, 1)]
    public void Progress_IsClampedToUnitInterval(int learned, double expected)
    {
        var definition = AchievementCatalog.ByCode("words-10")!;

        var metrics = new AchievementMetrics(0, learned, 0, 0, 0);

        Assert.Equal(expected, definition.Progress(metrics), precision: 6);
    }

    [Fact]
    public void MetricThatWasNotMeasured_ReadsAsZero()
    {
        // Частичная загрузка: проверка спрашивает только те показатели, которые
        // нужны незакрытым достижениям. Незапрошенный обязан читаться как ноль —
        // тогда достижение просто остаётся закрытым, а не выдаётся ошибочно.
        var metrics = new AchievementMetrics(
            new Dictionary<AchievementMetric, int> { [AchievementMetric.LearnedWords] = 42 });

        Assert.Equal(42, metrics[AchievementMetric.LearnedWords]);
        Assert.Equal(0, metrics[AchievementMetric.TotalReviews]);
        Assert.True(metrics.Has(AchievementMetric.LearnedWords));
        Assert.False(metrics.Has(AchievementMetric.TotalReviews));
    }

    [Fact]
    public void ByCode_ForUnknownCode_ReturnsNull() =>
        // Код приходит из базы, а каталог может измениться: достижение убрали,
        // а строка осталась. Падать на этом незачем.
        Assert.Null(AchievementCatalog.ByCode("нет-такого"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ByCode_ForMissingCode_ReturnsNull(string? code) =>
        Assert.Null(AchievementCatalog.ByCode(code));

    [Fact]
    public void LongestStreakAchievements_UseLongestStreakNotCurrent()
    {
        // Награда за семь дней подряд не должна отбираться из-за пропущенного
        // дня: награда, которую можно потерять, перестаёт мотивировать.
        var streakAchievements = AchievementCatalog.All
            .Where(achievement => achievement.Code.StartsWith("streak-", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(streakAchievements);
        Assert.All(streakAchievements, achievement =>
            Assert.Equal(AchievementMetric.LongestStreak, achievement.Metric));
    }
}
