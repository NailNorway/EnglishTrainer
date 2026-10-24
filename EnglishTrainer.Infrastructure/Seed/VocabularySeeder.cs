using System.Text.Json;
using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Enums;
using EnglishTrainer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnglishTrainer.Infrastructure.Seed;

/// <summary>Итог импорта словаря.</summary>
/// <param name="Decks">Сколько колод обработано.</param>
/// <param name="WordsAdded">Сколько слов добавлено.</param>
/// <param name="WordsUpdated">Сколько слов уже было и было дополнено.</param>
/// <param name="TranslationsAdded">Сколько переводов добавлено.</param>
/// <param name="ExamplesAdded">Сколько примеров добавлено.</param>
public readonly record struct VocabularySeedResult(
    int Decks,
    int WordsAdded,
    int WordsUpdated,
    int TranslationsAdded,
    int ExamplesAdded);

/// <summary>
/// Загрузка словаря из встроенных JSON-файлов.
/// </summary>
/// <remarks>
/// <para>
/// Импорт <b>идемпотентен</b>: повторный запуск не создаёт дубликатов, а дополняет
/// существующие записи. Это важнее, чем кажется: словарь придётся править
/// (нашлась опечатка в транскрипции, добавилось слово в колоду), и повторный
/// прогон не должен превращаться в ручную чистку базы.
/// </para>
/// <para>
/// Слова ищутся по паре «лемма + часть речи», а не по одной лемме: <c>work</c>
/// как глагол и как существительное — это два разных слова с разными переводами.
/// </para>
/// <para>
/// Примеры и переводы написаны под бытовые сценарии, а не скопированы из словарей.
/// Поэтому в базе нет полей с чужой лицензией, а контекст («deposit» в аренде жилья)
/// запоминается лучше академической фразы.
/// </para>
/// </remarks>
public sealed class VocabularySeeder(AppDbContext context, ILogger<VocabularySeeder> logger)
{
    /// <summary>Часть имени встроенного ресурса, по которой отбираются файлы словаря.</summary>
    private const string ResourceMarker = ".Seed.Data.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<VocabularySeedResult> SeedAsync(CancellationToken ct = default)
    {
        var files = ReadSeedFiles();

        var wordsAdded = 0;
        var wordsUpdated = 0;
        var translationsAdded = 0;
        var examplesAdded = 0;

        foreach (var file in files)
        {
            var deck = await context.Decks.FirstOrDefaultAsync(d => d.Slug == file.Slug, ct);
            if (deck is null)
            {
                deck = new Deck
                {
                    Slug = file.Slug,
                    Title = file.Title,
                    Description = file.Description,
                    SortOrder = file.SortOrder,
                    IsSystem = true
                };
                await context.Decks.AddAsync(deck, ct);
            }
            else
            {
                deck.Title = file.Title;
                deck.Description = file.Description;
                deck.SortOrder = file.SortOrder;
            }

            var (added, updated, translations, examples) = await SeedDeckWordsAsync(deck, file, ct);

            wordsAdded += added;
            wordsUpdated += updated;
            translationsAdded += translations;
            examplesAdded += examples;

            // Сохраняем по файлу, а не в самом конце: если на середине что-то
            // не сойдётся, уже загруженные колоды останутся целыми, а повторный
            // запуск продолжит с места сбоя.
            await context.SaveChangesAsync(ct);
        }

        var result = new VocabularySeedResult(files.Count, wordsAdded, wordsUpdated, translationsAdded, examplesAdded);
        logger.LogInformation(
            "Словарь загружен: колод {Decks}, добавлено слов {Added}, дополнено {Updated}, переводов {Translations}, примеров {Examples}.",
            result.Decks, result.WordsAdded, result.WordsUpdated, result.TranslationsAdded, result.ExamplesAdded);

        return result;
    }

    private async Task<(int Added, int Updated, int Translations, int Examples)> SeedDeckWordsAsync(
        Deck deck, WordSeedFile file, CancellationToken ct)
    {
        var lemmas = file.Words.Select(w => w.Lemma).Distinct(StringComparer.Ordinal).ToList();

        // Слова с переводами и примерами загружаются одним запросом: иначе
        // импорт на полторы сотни слов превращается в полторы сотни обращений к БД.
        var existingWords = await context.Words
            .Include(w => w.Translations)
            .Include(w => w.Examples)
            .Where(w => lemmas.Contains(w.Lemma))
            .AsSplitQuery()
            .ToListAsync(ct);

        var known = existingWords.ToDictionary(w => (w.Lemma, w.PartOfSpeech));
        var links = await context.DeckWords.Where(dw => dw.DeckId == deck.Id).ToListAsync(ct);
        var linkedWordIds = links.Select(l => l.WordId).ToHashSet();

        var added = 0;
        var updated = 0;
        var translations = 0;
        var examples = 0;

        for (var index = 0; index < file.Words.Count; index++)
        {
            var seed = file.Words[index];
            var key = (seed.Lemma, seed.PartOfSpeech);

            if (!known.TryGetValue(key, out var word))
            {
                word = new Word
                {
                    Lemma = seed.Lemma,
                    PartOfSpeech = seed.PartOfSpeech,
                    IpaUk = seed.IpaUk,
                    IpaUs = seed.IpaUs,
                    Level = ParseLevel(seed.Level)
                };

                await context.Words.AddAsync(word, ct);
                known[key] = word;
                added++;
            }
            else
            {
                // Дополняем, но не затираем: если у слова уже есть транскрипция
                // или уровень из более надёжного источника, они остаются.
                word.IpaUk ??= seed.IpaUk;
                word.IpaUs ??= seed.IpaUs;

                if (word.Level == CefrLevel.Unknown)
                    word.Level = ParseLevel(seed.Level);

                updated++;
            }

            translations += AddMissingTranslations(word, seed);
            examples += AddMissingExamples(word, seed);

            // Идентификатор у только что созданного слова ещё нулевой, поэтому
            // признак «уже в колоде» проверяется по сохранённым связям, а не по нему.
            var link = word.Id != 0 && linkedWordIds.Contains(word.Id)
                ? links.FirstOrDefault(l => l.WordId == word.Id)
                : null;

            if (link is null)
            {
                context.DeckWords.Add(new DeckWord { Deck = deck, Word = word, Order = index });
            }
            else
            {
                // Порядок внутри колоды задаёт последовательность показа новых слов.
                link.Order = index;
            }
        }

        return (added, updated, translations, examples);
    }

    private static int AddMissingTranslations(Word word, WordSeed seed)
    {
        var added = 0;

        foreach (var translation in seed.Translations)
        {
            var existing = word.Translations.FirstOrDefault(t =>
                string.Equals(t.Text, translation.Text, StringComparison.Ordinal) &&
                string.Equals(t.Sense, translation.Sense, StringComparison.Ordinal));

            if (existing is not null)
            {
                if (translation.IsPrimary)
                {
                    // Исправление уже существующего перевода не должно оставлять
                    // два основных: словарь задаёт один основной вариант.
                    foreach (var item in word.Translations)
                        item.IsPrimary = false;

                    existing.IsPrimary = true;
                }

                continue;
            }

            // Если в словаре исправили основной перевод (например, «нужно
            // уточнить детали» → «нам нужно уточнить детали»), старую строку
            // нужно заменить, а не добавлять рядом вторую основную. Раньше
            // импорт умел только дополнять, и исправление не доходило до базы.
            if (translation.IsPrimary)
            {
                var primary = word.Translations.FirstOrDefault(t => t.IsPrimary);

                if (primary is not null)
                {
                    primary.Text = translation.Text;
                    primary.Sense = translation.Sense;
                    continue;
                }
            }

            word.Translations.Add(new WordTranslation
            {
                LanguageCode = "ru",
                Text = translation.Text,
                Sense = translation.Sense,
                IsPrimary = translation.IsPrimary
            });

            added++;
        }

        return added;
    }

    private static int AddMissingExamples(Word word, WordSeed seed)
    {
        var added = 0;

        foreach (var example in seed.Examples)
        {
            var exists = word.Examples.Any(e =>
                string.Equals(e.TextEn, example.TextEn, StringComparison.Ordinal));

            if (exists)
                continue;

            word.Examples.Add(new WordExample
            {
                TextEn = example.TextEn,
                TextRu = example.TextRu,
                AudioUrl = example.AudioUrl,
                Attribution = example.Attribution
            });

            added++;
        }

        return added;
    }

    /// <summary>
    /// Читает словарь из ресурсов сборки.
    /// </summary>
    /// <remarks>
    /// Файлы впечатаны в сборку, а не читаются с диска: иначе приложение
    /// зависит от текущего каталога запуска, и тот же код работает в тестах,
    /// в контейнере и из <c>dotnet run</c> по-разному.
    /// </remarks>
    private static IReadOnlyList<WordSeedFile> ReadSeedFiles()
    {
        var assembly = typeof(VocabularySeeder).Assembly;

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal)
                           && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (resourceNames.Count == 0)
        {
            throw new InvalidOperationException(
                "Не найдено ни одного файла словаря. Проверьте, что в проекте Infrastructure " +
                "файлы Seed\\Data\\*.json включены как EmbeddedResource.");
        }

        var files = new List<WordSeedFile>(resourceNames.Count);

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Встроенный ресурс словаря '{name}' не читается.");

            var file = JsonSerializer.Deserialize<WordSeedFile>(stream, SerializerOptions)
                ?? throw new InvalidOperationException($"Файл словаря '{name}' пуст или повреждён.");

            files.Add(file);
        }

        return files;
    }

    private static CefrLevel ParseLevel(string? value) =>
        Enum.TryParse<CefrLevel>(value, ignoreCase: true, out var level) ? level : CefrLevel.Unknown;
}
