using EnglishTrainer.Api.Seo;
using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EnglishTrainer.Api.Pages;

/// <summary>
/// Страница одной колоды: заголовок темы и весь список слов.
/// </summary>
/// <remarks>
/// Ради этой страницы человек и приходит из поиска: он набрал «английские слова
/// на тему путешествий» и хочет увидеть список, а не форму регистрации. Поэтому
/// слова отдаются целиком и без входа, а тренировка предлагается как следующий
/// шаг. Обратный порядок (сначала «зарегистрируйтесь») — типовая причина, по
/// которой такие страницы не ранжируются: робот видит заглушку, человек уходит.
/// </remarks>
public sealed class DeckModel(PublicCatalogService catalog, IOptions<SeoOptions> options) : PageModel
{
    public PublicDeckDto Deck { get; private set; } = null!;

    /// <summary>Остальные темы — внутренние ссылки, по которым робот обходит каталог.</summary>
    public IReadOnlyList<PublicDeckSummaryDto> OtherDecks { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var deck = await catalog.GetDeckAsync(slug, ct);

        // Несуществующая или чужая колода — честный 404. Отвечать на такой адрес
        // главной значило бы плодить «мягкие 404», за которые сайт теряет позиции.
        if (deck is null)
            return NotFound();

        Deck = deck;

        var all = await catalog.GetDecksAsync(ct);
        OtherDecks = all.Where(other => other.Slug != deck.Slug).ToList();

        var baseUrl = options.Value.BaseUrl;
        var url = $"{baseUrl}/decks/{deck.Slug}";
        var wordCount = deck.Words.Count;

        var title = string.IsNullOrEmpty(deck.Description)
            ? $"{deck.Title}: английские слова с переводом и транскрипцией"
            : $"{deck.Title}: {wordCount} английских слов с переводом и транскрипцией";

        var description = string.IsNullOrEmpty(deck.Description)
            ? $"{deck.Title}: {Ui.RussianPlural.Words(wordCount)} с транскрипцией, переводом и примерами употребления."
            : $"{deck.Description}. {Ui.RussianPlural.Words(wordCount)} с транскрипцией, переводом и примерами.";

        ViewData["Title"] = title;
        ViewData["Description"] = description;
        ViewData["Canonical"] = url;

        // См. комментарий в DecksModel: это разрешение на блок, а не сам блок.
        ViewData["AdSlot"] = true;

        ViewData["JsonLd"] = BuildJsonLd(baseUrl, url, title, description, deck);

        return Page();
    }

    /// <summary>
    /// Разметка страницы: навигационная цепочка и список слов как <c>ItemList</c>.
    /// </summary>
    /// <remarks>
    /// <c>DefinedTerm</c> выбран, а не <c>Course</c>: страница содержит определения
    /// слов, а не учебный курс. Разметка, которая не соответствует содержимому,
    /// хуже её отсутствия — поисковик вправе её игнорировать целиком.
    /// </remarks>
    private static string BuildJsonLd(
        string baseUrl, string url, string title, string description, PublicDeckDto deck) =>
        StructuredData.Graph(
            StructuredData.Node(
                ("@type", "WebPage"),
                ("name", title),
                ("description", description),
                ("url", url),
                ("inLanguage", "ru"),
                ("isPartOf", StructuredData.WebSite(baseUrl))),
            StructuredData.Node(
                ("@type", "ItemList"),
                ("name", $"{deck.Title}: слова с переводом"),
                ("numberOfItems", deck.Words.Count),
                ("itemListElement", deck.Words.Select((word, index) => StructuredData.Node(
                    ("@type", "ListItem"),
                    ("position", index + 1),
                    ("item", StructuredData.Node(
                        ("@type", "DefinedTerm"),
                        ("name", word.Lemma),
                        ("description", word.Translation),
                        ("inDefinedTermSet", StructuredData.Node(
                            ("@type", "DefinedTermSet"),
                            ("name", deck.Title),
                            ("url", url))))))).ToArray())),
            StructuredData.Breadcrumbs(
                ("Главная", $"{baseUrl}/"),
                ("Английские слова по темам", $"{baseUrl}/decks"),
                (deck.Title, url)));
}
