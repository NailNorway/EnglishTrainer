using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Enums;
using EnglishTrainer.Domain.Repositories;

namespace EnglishTrainer.Application.Services;

/// <summary>
/// Публичный каталог колод: то, что видит человек без аккаунта и что
/// индексируют поисковые системы.
/// </summary>
/// <remarks>
/// <para>
/// Отдельный сервис, а не методы <see cref="DeckService"/>. У того каждый ответ
/// считается по прогрессу конкретного пользователя, а здесь пользователя нет
/// вовсе — зато есть требование отдать страницу целиком за один проход: колода
/// на три десятка слов не должна превращаться в три десятка обращений к базе.
/// </para>
/// <para>
/// Публичными считаются только системные колоды. Личная колода — это чужие
/// слова, набранные человеком для себя; показать их по прямой ссылке значило бы
/// выставить чужую работу напоказ и заодно дать поисковику проиндексировать
/// то, что не предназначено для индекса.
/// </para>
/// </remarks>
public sealed class PublicCatalogService(IDeckRepository decks, IWordRepository words)
{
    /// <summary>Все системные колоды в порядке показа.</summary>
    public async Task<IReadOnlyList<PublicDeckSummaryDto>> GetDecksAsync(CancellationToken ct = default)
    {
        var system = await decks.GetSystemAsync(ct);
        var result = new List<PublicDeckSummaryDto>(system.Count);

        foreach (var deck in system.OrderBy(d => d.SortOrder).ThenBy(d => d.Id))
        {
            result.Add(new PublicDeckSummaryDto(
                deck.Slug,
                deck.Title,
                deck.Description,
                await words.CountInDeckAsync(deck.Id, ct)));
        }

        return result;
    }

    /// <summary>
    /// Колода со словами или <c>null</c>, если такой публичной колоды нет.
    /// </summary>
    /// <remarks>
    /// <c>null</c>, а не исключение: страница по несуществующему адресу должна
    /// отвечать 404, и решать это должен HTTP-слой, а не сервис.
    /// </remarks>
    public async Task<PublicDeckDto?> GetDeckAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var deck = await decks.GetBySlugAsync(slug, ct);
        if (deck is null || !deck.IsSystem)
            return null;

        var list = await words.GetByDeckAsync(deck.Id, includeDetails: true, ct);

        return new PublicDeckDto(
            deck.Slug,
            deck.Title,
            deck.Description,
            list.Select(ToPublicWord).ToList());
    }

    private static PublicWordDto ToPublicWord(Word word)
    {
        // Основной перевод — тот, что человек видит первым в карточке. Если
        // основной не помечен (старые записи словаря), берётся любой: пустая
        // строка в таблице перевода выглядела бы как ошибка страницы.
        var translation = word.Translations.FirstOrDefault(t => t.IsPrimary)?.Text
            ?? word.Translations.FirstOrDefault()?.Text
            ?? string.Empty;

        var example = word.Examples.FirstOrDefault();

        return new PublicWordDto(
            word.Lemma,
            PartOfSpeechRu(word.PartOfSpeech),
            word.IpaUk ?? word.IpaUs,
            word.Level == CefrLevel.Unknown ? null : word.Level.ToString(),
            translation,
            example?.TextEn,
            example?.TextRu);
    }

    /// <summary>
    /// Часть речи по-русски.
    /// </summary>
    /// <remarks>
    /// В словаре она хранится кодом (<c>noun</c>, <c>verb</c>…), потому что по ней
    /// ищется слово и сравнивается лемма. Показывать посетителю английский код
    /// незачем, а держать перевод в базе — значит хранить одно и то же в двух
    /// местах и однажды их разойтись.
    /// </remarks>
    private static string PartOfSpeechRu(string partOfSpeech) => partOfSpeech switch
    {
        "noun" => "существительное",
        "verb" => "глагол",
        "adjective" => "прилагательное",
        "adverb" => "наречие",
        "phrase" => "фраза",
        _ => "слово"
    };
}
