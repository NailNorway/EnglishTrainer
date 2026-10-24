using EnglishTrainer.Infrastructure.Repositories;
using EnglishTrainer.IntegrationTests.Database;

namespace EnglishTrainer.IntegrationTests.Repositories;

/// <summary>
/// Словарь: отбор новых слов, поиск и выборка по частям речи.
/// </summary>
/// <remarks>
/// Отбор новых слов — единственное место, где решается, что человек увидит
/// в первые дни занятий. Ошибка здесь не падает, а тихо портит продукт:
/// пользователь получает уже начатые слова или самые редкие вместо частых
/// и делает вывод, что словарь подобран плохо.
/// </remarks>
public sealed class WordRepositoryTests
{
    [Fact]
    public async Task GetNewWordsForUserAsync_ExcludesWordsAlreadyStarted()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        var user = await TestData.AddUserAsync(scope.Context);

        var started = await TestData.AddWordAsync(scope.Context);
        var fresh = await TestData.AddWordAsync(scope.Context);

        var deck = await TestData.AddDeckAsync(scope.Context, wordIds: [started.Id, fresh.Id]);

        await TestData.AddCardStateAsync(
            scope.Context, user.Id, started.Id, intervalDays: 1, dueAtUtc: DateTime.UtcNow);

        var newWords = await repository.GetNewWordsForUserAsync(user.Id, deck.Id, limit: 10);

        Assert.Single(newWords);
        Assert.Equal(fresh.Id, newWords[0].Id);
    }

    [Fact]
    public async Task GetNewWordsForUserAsync_WithoutDeck_SearchesAllDecks()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        var user = await TestData.AddUserAsync(scope.Context);

        var started = await TestData.AddWordAsync(scope.Context);
        await TestData.AddDeckAsync(scope.Context, wordIds: [started.Id]);

        var fresh = await TestData.AddWordAsync(scope.Context);
        await TestData.AddDeckAsync(scope.Context, wordIds: [fresh.Id]);

        await TestData.AddCardStateAsync(
            scope.Context, user.Id, started.Id, intervalDays: 1, dueAtUtc: DateTime.UtcNow);

        // Общая тренировка не выбирает колоду: раньше она смотрела только
        // в первую системную колоду и после её прохождения возвращала пусто.
        var newWords = await repository.GetNewWordsForUserAsync(user.Id, null, limit: 10);

        Assert.Contains(newWords, word => word.Id == fresh.Id);
    }

    [Fact]
    public async Task GetNewWordsForUserAsync_WithoutDeck_IgnoresOtherUsersPrivateDecks()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        var user = await TestData.AddUserAsync(scope.Context);
        var other = await TestData.AddUserAsync(scope.Context);

        var foreignWord = await TestData.AddWordAsync(scope.Context);
        await TestData.AddDeckAsync(
            scope.Context, isSystem: false, ownerId: other.Id, wordIds: [foreignWord.Id]);

        var newWords = await repository.GetNewWordsForUserAsync(user.Id, null, limit: 10);

        Assert.DoesNotContain(newWords, word => word.Id == foreignWord.Id);
    }

    [Fact]
    public async Task GetNewWordsForUserAsync_OrdersByFrequencyAndPutsUnknownLast()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        var user = await TestData.AddUserAsync(scope.Context);

        var rare = await TestData.AddWordAsync(scope.Context, frequencyRank: 5000);
        var common = await TestData.AddWordAsync(scope.Context, frequencyRank: 100);
        var unknown = await TestData.AddWordAsync(scope.Context, frequencyRank: null);

        var deck = await TestData.AddDeckAsync(
            scope.Context, wordIds: [unknown.Id, rare.Id, common.Id]);

        var newWords = await repository.GetNewWordsForUserAsync(user.Id, deck.Id, limit: 10);

        // Сначала самые употребительные, слова без ранга — в конце: иначе
        // новичок получает редкую лексику вместо частотной.
        Assert.Equal([common.Id, rare.Id, unknown.Id], newWords.Select(word => word.Id));
    }

    [Fact]
    public async Task GetNewWordsForUserAsync_RespectsLimitOverride()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        var user = await TestData.AddUserAsync(scope.Context);
        var wordIds = new List<int>();

        for (var index = 0; index < 5; index++)
        {
            var word = await TestData.AddWordAsync(scope.Context, frequencyRank: index);
            wordIds.Add(word.Id);
        }

        var deck = await TestData.AddDeckAsync(scope.Context, wordIds: [.. wordIds]);

        Assert.Equal(2, (await repository.GetNewWordsForUserAsync(user.Id, deck.Id, limit: 2)).Count);
        Assert.Empty(await repository.GetNewWordsForUserAsync(user.Id, deck.Id, limit: 0));
    }

    [Fact]
    public async Task SearchAsync_MatchesLemmaPrefixCaseInsensitively()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        // Написание случайное: словарь в базе общий, и «land» нашло бы ещё
        // и landlord из поставляемых колод — тест зависел бы от чужого набора слов.
        var token = $"zq{Guid.NewGuid():N}"[..10];

        var target = await TestData.AddWordAsync(scope.Context, lemma: token, translation: "пейзаж");
        await TestData.AddWordAsync(scope.Context, lemma: $"zz{Guid.NewGuid():N}"[..10], translation: "остров");

        var found = await repository.SearchAsync(token.ToUpperInvariant(), limit: 10);

        Assert.Single(found);
        Assert.Equal(target.Id, found[0].Id);

        // Переводы подгружаются: без них результат поиска нечем показать.
        Assert.NotEmpty(found[0].Translations);
    }

    [Fact]
    public async Task SearchAsync_MatchesTranslationPrefix()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        var word = await TestData.AddWordAsync(scope.Context, lemma: "rent", translation: "аренда");

        var found = await repository.SearchAsync("арен", limit: 10);

        Assert.Contains(found, item => item.Id == word.Id);
    }

    [Fact]
    public async Task SearchAsync_WithShortOrEmptyQuery_ReturnsNothing()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        await TestData.AddWordAsync(scope.Context, lemma: "anything");

        // Пустой запрос вернул бы весь словарь: для поиска конкретного слова
        // это бесполезно, а для базы — дорого.
        Assert.Empty(await repository.SearchAsync("", limit: 10));
        Assert.Empty(await repository.SearchAsync("   ", limit: 10));
        Assert.Empty(await repository.SearchAsync("anything", limit: 0));
    }

    [Fact]
    public async Task GetByLemmaAsync_TreatsDifferentPartsOfSpeechAsDifferentWords()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        // Написание случайное: «work» в поставляемом словаре уже есть и как
        // существительное, и как глагол — уникальный индекс не дал бы его завести.
        var lemma = $"wk{Guid.NewGuid():N}"[..10];

        var asNoun = await TestData.AddWordAsync(scope.Context, lemma: lemma, partOfSpeech: "noun");
        var asVerb = await TestData.AddWordAsync(scope.Context, lemma: lemma, partOfSpeech: "verb");

        Assert.Equal(asNoun.Id, (await repository.GetByLemmaAsync(lemma, "noun"))!.Id);
        Assert.Equal(asVerb.Id, (await repository.GetByLemmaAsync(lemma, "verb"))!.Id);
        Assert.Null(await repository.GetByLemmaAsync(lemma, "adverb"));
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateLemmaAndPartOfSpeech()
    {
        await using var scope = await TestDatabase.BeginAsync();

        var lemma = $"uq{Guid.NewGuid():N}"[..10];
        await TestData.AddWordAsync(scope.Context, lemma: lemma, partOfSpeech: "noun");

        scope.Context.Words.Add(new Domain.Entities.Word { Lemma = lemma, PartOfSpeech = "noun" });

        // Именно этот индекс позволяет переиспользовать существующее слово
        // вместо создания двойника: он же и сообщает, что слово уже есть.
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            () => scope.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task GetWithDetailsByOffsetAsync_IsStableAndLoadsDetails()
    {
        await using var scope = await TestDatabase.BeginAsync();
        var repository = new WordRepository(scope.Context);

        await TestData.AddWordAsync(scope.Context, frequencyRank: 10);
        await TestData.AddWordAsync(scope.Context, frequencyRank: 20);

        var first = await repository.GetWithDetailsByOffsetAsync(0);
        var again = await repository.GetWithDetailsByOffsetAsync(0);
        var second = await repository.GetWithDetailsByOffsetAsync(1);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // Устойчивость порядка — то, ради чего этот метод существует: «слово дня»
        // выбирается по смещению, и при неустойчивом порядке оно менялось бы
        // от запроса к запросу.
        Assert.Equal(first!.Id, again!.Id);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.NotEmpty(first.Translations);
    }
}
