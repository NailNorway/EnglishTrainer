using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Repositories;
using EnglishTrainer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EnglishTrainer.Infrastructure.Repositories;

/// <summary>Слова и колоды. Справочные данные, только чтение (кроме импорта).</summary>
public sealed class WordRepository(AppDbContext context) : IWordRepository
{
    public Task<Word?> GetByIdAsync(int id, CancellationToken ct = default) =>
        context.Words.FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<Word?> GetWithDetailsAsync(int id, CancellationToken ct = default) =>
        context.Words
            .Include(w => w.Translations)
            .Include(w => w.Examples)
            // Две коллекции в одном запросе дают декартово произведение строк:
            // переводы × примеры. Раздельные запросы дешевле.
            .AsSplitQuery()
            .FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<Word?> GetByLemmaAsync(string lemma, string partOfSpeech, CancellationToken ct = default) =>
        context.Words.FirstOrDefaultAsync(w => w.Lemma == lemma && w.PartOfSpeech == partOfSpeech, ct);

    public async Task<IReadOnlyList<Word>> GetByDeckAsync(
        int deckId, bool includeDetails, CancellationToken ct = default)
    {
        // Порядок слов хранится в связи колоды, поэтому сначала читается он.
        var orderedIds = await context.DeckWords
            .Where(dw => dw.DeckId == deckId)
            .OrderBy(dw => dw.Order)
            .Select(dw => dw.WordId)
            .ToListAsync(ct);

        if (orderedIds.Count == 0)
            return [];

        // Отбор идёт от Words, а не от связей колоды. Причина техническая:
        // Include нельзя применять после Select, который проецирует другой тип,
        // а отбор через DeckWords требует именно такой проекции.
        var query = context.Words.Where(w => orderedIds.Contains(w.Id));

        query = includeDetails
            ? query.Include(w => w.Translations).Include(w => w.Examples).AsSplitQuery()
            // Для списка переводы нужны, а примеры — нет: экономим трафик и память.
            : query.Include(w => w.Translations);

        var words = await query.AsNoTracking().ToListAsync(ct);

        // Contains не задаёт порядок результата, поэтому он восстанавливается явно.
        return [.. orderedIds.Select(id => words.Find(w => w.Id == id)).OfType<Word>()];
    }

    public async Task<IReadOnlyDictionary<int, Word>> GetWithDetailsByIdsAsync(
        IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<int, Word>();

        // Один запрос на все слова вместо N обращений — именно это убирает
        // проблему N+1 при сборке очереди повторений.
        var list = await context.Words
            .Include(w => w.Translations)
            .Include(w => w.Examples)
            .Where(w => ids.Contains(w.Id))
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);

        return list.ToDictionary(w => w.Id);
    }

    public async Task<IReadOnlyList<Word>> GetNewWordsForUserAsync(
        int userId, int? deckId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        // Слова, по которым у пользователя ещё нет состояния карточки.
        // Без выбранной колоды поиск идёт по всем колодам: иначе после
        // прохождения первой системной колоды новые слова переставали
        // приходить, хотя в остальных словаре их ещё много.
        var query = context.Words
            .Where(w => !context.CardStates.Any(cs => cs.UserId == userId && cs.WordId == w.Id));

        if (deckId is { } id)
        {
            query = query.Where(w => context.DeckWords.Any(dw => dw.DeckId == id && dw.WordId == w.Id));
        }
        else
        {
            // Системные колоды и собственные колоды пользователя. Чужие личные
            // колоды в общий подбор попадать не должны: слова в них — личный
            // материал другого человека, даже если само слово есть в словаре.
            query = query.Where(w => context.DeckWords.Any(dw =>
                dw.WordId == w.Id
                && context.Decks.Any(d => d.Id == dw.DeckId
                    && (d.IsSystem || d.OwnerId == userId))));
        }

        // Сортировка по частотному рангу: первыми идут самые употребительные,
        // а не случайные. NULL в конце — слова без ранга наименее приоритетны.
        // Когда ранга нет ни у кого (словарь загружен из файлов), порядок задаёт
        // идентификатор, то есть последовательность внутри файла колоды.
        return await query
            .OrderBy(w => w.FrequencyRank == null)
            .ThenBy(w => w.FrequencyRank)
            .ThenBy(w => w.Id)
            .Take(limit)
            .Include(w => w.Translations)
            .Include(w => w.Examples)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Word>> GetDistractorsAsync(
        int excludeWordId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        // Берём слова сопоставимой частотности: редкое слово в качестве неверного
        // варианта выдаёт правильный ответ с первого взгляда.
        var reference = await context.Words
            .Where(w => w.Id == excludeWordId)
            .Select(w => new { w.FrequencyRank, w.PartOfSpeech })
            .FirstOrDefaultAsync(ct);

        var query = context.Words
            .Include(w => w.Translations)
            .Where(w => w.Id != excludeWordId);

        if (reference?.PartOfSpeech is { Length: > 0 } partOfSpeech)
            query = query.Where(w => w.PartOfSpeech == partOfSpeech);

        if (reference?.FrequencyRank is { } rank)
        {
            var from = rank - 1500;
            var to = rank + 1500;
            query = query.Where(w => w.FrequencyRank != null
                                     && w.FrequencyRank >= from
                                     && w.FrequencyRank <= to);
        }

        return await query
            .OrderBy(w => w.FrequencyRank == null)
            .ThenBy(w => w.FrequencyRank)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public Task<int> CountInDeckAsync(int deckId, CancellationToken ct = default) =>
        context.DeckWords.CountAsync(dw => dw.DeckId == deckId, ct);

    public async Task<IReadOnlyList<int>> GetWordIdsInDeckAsync(int deckId, CancellationToken ct = default) =>
        await context.DeckWords
            .Where(dw => dw.DeckId == deckId)
            .Select(dw => dw.WordId)
            .ToListAsync(ct);

    public Task<int> CountAllAsync(CancellationToken ct = default) =>
        context.Words.CountAsync(ct);

    public Task<Word?> GetWithDetailsByOffsetAsync(int offset, CancellationToken ct = default) =>
        // Тот же порядок, что и при отборе новых слов: сначала употребительные,
        // затем по идентификатору. Без устойчивого порядка «слово дня» менялось бы
        // от запуска к запуску при одном и том же смещении.
        context.Words
            .OrderBy(w => w.FrequencyRank == null)
            .ThenBy(w => w.FrequencyRank)
            .ThenBy(w => w.Id)
            .Skip(offset)
            .Take(1)
            .Include(w => w.Translations)
            .Include(w => w.Examples)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    public async Task AddRangeAsync(IEnumerable<Word> words, CancellationToken ct = default)
    {
        await context.Words.AddRangeAsync(words, ct);
    }

    public async Task AddAsync(Word word, CancellationToken ct = default)
    {
        await context.Words.AddAsync(word, ct);
    }

    public async Task<IReadOnlyList<Word>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        var normalized = query.Trim().ToLowerInvariant();

        // Совпадение ищется с начала строки: так запрос опирается на индексы
        // и не деградирует с ростом словаря. Заодно это предсказуемее для
        // человека — «land» находит landscape, но не «island».
        return await context.Words
            .Where(w => w.Lemma.ToLower().StartsWith(normalized)
                        || w.Translations.Any(t => t.Text.ToLower().StartsWith(normalized)))
            .OrderBy(w => w.Lemma)
            .Take(limit)
            .Include(w => w.Translations)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}

/// <summary>Колоды.</summary>
public sealed class DeckRepository(AppDbContext context) : IDeckRepository
{
    public async Task<IReadOnlyList<Deck>> GetSystemAsync(CancellationToken ct = default) =>
        await context.Decks
            .Where(d => d.IsSystem)
            .OrderBy(d => d.SortOrder)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Deck>> GetForUserAsync(int userId, CancellationToken ct = default) =>
        await context.Decks
            // Личные колоды идут после системных: порядок задан SortOrder
            // у системных, а личные добавляются в конец списка по дате создания.
            .Where(d => d.IsSystem || d.OwnerId == userId)
            .OrderBy(d => d.IsSystem ? 0 : 1)
            .ThenBy(d => d.SortOrder)
            .ThenBy(d => d.Id)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<Deck?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        context.Decks.FirstOrDefaultAsync(d => d.Slug == slug, ct);

    public Task<Deck?> GetByIdAsync(int id, CancellationToken ct = default) =>
        context.Decks.FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default) =>
        context.Decks.AnyAsync(d => d.Slug == slug, ct);

    public async Task RemoveAsync(Deck deck, CancellationToken ct = default)
    {
        // Связи «слово в колоде» уходят каскадом на стороне базы, но контекст
        // о них не знает; удаление сущности без загрузки связей EF выполнит
        // одним DELETE, а каскад сделает PostgreSQL.
        context.Decks.Remove(deck);

        await Task.CompletedTask;
    }

    public Task<DeckWord?> GetWordLinkAsync(int deckId, int wordId, CancellationToken ct = default) =>
        context.DeckWords.FirstOrDefaultAsync(dw => dw.DeckId == deckId && dw.WordId == wordId, ct);

    public async Task AddWordAsync(DeckWord link, CancellationToken ct = default)
    {
        await context.DeckWords.AddAsync(link, ct);
    }

    public async Task RemoveWordAsync(DeckWord link, CancellationToken ct = default)
    {
        context.DeckWords.Remove(link);

        await Task.CompletedTask;
    }

    public async Task<int> NextWordOrderAsync(int deckId, CancellationToken ct = default)
    {
        // Пустая колода начинается с нуля: Max по пустому множеству не определён,
        // и без запасного значения первое слово получило бы позицию по умолчанию.
        var max = await context.DeckWords
            .Where(dw => dw.DeckId == deckId)
            .Select(dw => (int?)dw.Order)
            .MaxAsync(ct);

        return (max ?? -1) + 1;
    }

    public Task<int> CountCompletedAsync(
        int userId, double learnedIntervalDays, CancellationToken ct = default) =>
        // Колода пройдена, когда в ней есть слова и каждое из них выучено.
        // Пустая колода не считается пройденной: «нет слов» и «все слова
        // выучены» — не одно и то же, и награда за пустую колоду была бы
        // наградой ни за что.
        context.Decks.CountAsync(
            deck => context.DeckWords.Any(dw => dw.DeckId == deck.Id)
                    && !context.DeckWords.Any(dw =>
                        dw.DeckId == deck.Id
                        && !context.CardStates.Any(state =>
                            state.UserId == userId
                            && state.WordId == dw.WordId
                            && state.IntervalDays >= learnedIntervalDays)),
            ct);

    public async Task AddAsync(Deck deck, CancellationToken ct = default)
    {
        await context.Decks.AddAsync(deck, ct);
    }
}

/// <summary>Пользователи и настройки.</summary>
public sealed class UserRepository(AppDbContext context) : IUserRepository
{
    public Task<User?> GetByIdAsync(int id, CancellationToken ct = default) =>
        context.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default) =>
        context.Users.FirstOrDefaultAsync(u => u.PublicId == publicId, ct);

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        context.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default) =>
        context.Users.AnyAsync(u => u.Username == username, ct);

    public async Task<IReadOnlyList<User>> SearchByUsernameAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        // Совпадение ищется с начала ника, а не «содержит»: такой запрос
        // опирается на индекс по нику и не деградирует с ростом числа людей.
        return await context.Users
            .Where(u => u.Username.StartsWith(query))
            .OrderBy(u => u.Username)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public Task<User?> GetByTelegramIdAsync(long telegramId, CancellationToken ct = default) =>
        // Настройки загружаются сразу: и напоминания, и сводка дня обращаются
        // к ним постоянно, а строка настроек одна на пользователя — лишним
        // этот Include не бывает.
        context.Users
            .Include(u => u.Settings)
            .FirstOrDefaultAsync(u => u.TelegramId == telegramId, ct);

    public Task<User?> GetWithSettingsAsync(int userId, CancellationToken ct = default) =>
        context.Users
            .Include(u => u.Settings)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

    public async Task<IReadOnlyList<User>> GetDueRemindersAsync(
        DateTime nowUtc, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        // Отслеживаемые сущности, а не AsNoTracking: после отправки расписание
        // каждого из этих пользователей сдвигается, и изменение должно попасть
        // в базу тем же SaveChanges.
        return await context.Users
            .Include(u => u.Settings)
            .Where(u => u.TelegramId != null
                        && u.Settings != null
                        && u.Settings.DailyReminderEnabled
                        && u.Settings.NextReminderAtUtc != null
                        && u.Settings.NextReminderAtUtc <= nowUtc)
            .OrderBy(u => u.Settings!.NextReminderAtUtc)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        await context.Users.AddAsync(user, ct);
    }

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct = default) =>
        context.Users.AnyAsync(u => u.Email == normalizedEmail, ct);
}
