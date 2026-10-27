using EnglishTrainer.Api.Seo;
using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EnglishTrainer.Api.Pages;

/// <summary>
/// Страница «Английские слова по темам» — оглавление публичного каталога.
/// </summary>
/// <remarks>
/// Это точка входа для человека из поиска: он ищет не приложение, а список слов,
/// и попадает на страницу, где видны все темы сразу. Приложение упоминается
/// ниже — как способ выучить эти слова, а не как условие доступа к списку.
/// </remarks>
public sealed class DecksModel(PublicCatalogService catalog, IOptions<SeoOptions> options) : PageModel
{
    public IReadOnlyList<PublicDeckSummaryDto> Decks { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Decks = await catalog.GetDecksAsync(ct);

        var baseUrl = options.Value.BaseUrl;
        var url = $"{baseUrl}/decks";
        var totalWords = Decks.Sum(deck => deck.WordCount);

        var title = "Английские слова по темам: колоды с переводом и транскрипцией";
        var description = $"Тематические колоды английских слов: {Decks.Count} тем, "
            + $"{totalWords} слов с переводом, транскрипцией и примерами. Списки открыты без регистрации.";

        ViewData["Title"] = title;
        ViewData["Description"] = description;
        ViewData["Canonical"] = url;

        // Разрешение на рекламный блок: сам блок выводит партиал и только если
        // реклама включена в настройках. Здесь лишь сообщаем макету, что на этой
        // странице блок есть, — иначе загрузчик РСЯ не подключается вовсе.
        ViewData["AdSlot"] = true;

        ViewData["JsonLd"] = StructuredData.Graph(
            StructuredData.Node(
                ("@type", "CollectionPage"),
                ("name", title),
                ("description", description),
                ("url", url),
                ("inLanguage", "ru"),
                ("isPartOf", StructuredData.WebSite(baseUrl))),
            StructuredData.Breadcrumbs(
                ("Главная", $"{baseUrl}/"),
                ("Английские слова по темам", url)));
    }
}
