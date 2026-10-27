using EnglishTrainer.Api.Legal;
using EnglishTrainer.Api.Seo;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EnglishTrainer.Api.Pages;

/// <summary>
/// Страница контактов.
/// </summary>
/// <remarks>
/// Нужна не только людям, но и модерации рекламных сетей: по ней проверяют,
/// что за сайтом стоит живой владелец. Отдельная страница, а не блок в подвале:
/// письмо о проблеме со входом начинают искать именно в «Контактах».
/// </remarks>
public sealed class ContactsModel(IOptions<LegalOptions> legal, IOptions<SeoOptions> seo) : PageModel
{
    public LegalOptions Legal { get; } = legal.Value;

    public void OnGet()
    {
        var url = $"{seo.Value.BaseUrl}/contacts";

        ViewData["Title"] = "Контакты — EnglishTrainer";
        ViewData["Description"] =
            "Как связаться с EnglishTrainer: почта для вопросов о работе сервиса, ошибках в словаре и персональных данных.";
        ViewData["Canonical"] = url;
    }
}
