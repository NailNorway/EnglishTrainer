using EnglishTrainer.Api.Legal;
using EnglishTrainer.Api.Seo;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EnglishTrainer.Api.Pages;

/// <summary>
/// Политика обработки персональных данных.
/// </summary>
/// <remarks>
/// Страница обязательна по 152-ФЗ: сервис собирает адреса электронной почты
/// и пароли, то есть обрабатывает персональные данные. Кроме того, её наличие
/// проверяет модерация рекламных сетей — без политики заявку не рассматривают.
/// </remarks>
public sealed class PrivacyModel(IOptions<LegalOptions> legal, IOptions<SeoOptions> seo) : PageModel
{
    public LegalOptions Legal { get; } = legal.Value;

    public void OnGet()
    {
        var url = $"{seo.Value.BaseUrl}/privacy";

        ViewData["Title"] = "Политика обработки персональных данных — EnglishTrainer";
        ViewData["Description"] =
            "Какие данные собирает EnglishTrainer, зачем они нужны, кому передаются и как удалить аккаунт.";
        ViewData["Canonical"] = url;
    }
}
