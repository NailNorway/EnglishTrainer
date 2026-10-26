namespace EnglishTrainer.Api.Ads;

/// <summary>
/// Рекламный блок Рекламной сети Яндекса на публичных страницах каталога.
/// </summary>
/// <remarks>
/// <para>
/// По умолчанию выключено: пока сайт не прошёл модерацию РСЯ, загрузчик рекламы
/// не должен вообще подключаться — это лишний сторонний запрос и лишний вес
/// страницы без всякой пользы.
/// </para>
/// <para>
/// Включается двумя значениями: <see cref="Enabled"/> и <see cref="BlockId"/>.
/// Идентификатор блока выдаёт панель РСЯ, он выглядит как
/// <c>R-A-1234567-1</c> и не является секретом.
/// </para>
/// <para>
/// Блок ставится только на страницы каталога (<c>/decks</c> и <c>/decks/{slug}</c>).
/// Внутрь тренировки он не попадает намеренно: человек пришёл учить слова,
/// и реклама посреди карточки разрушает то, ради чего он вернётся завтра.
/// </para>
/// </remarks>
public sealed class AdsOptions
{
    public const string SectionName = "Ads";

    /// <summary>Показывать ли рекламу. Выключено, пока сайт не одобрен в РСЯ.</summary>
    public bool Enabled { get; set; }

    /// <summary>Идентификатор блока РСЯ, например <c>R-A-1234567-1</c>.</summary>
    public string BlockId { get; set; } = string.Empty;

    /// <summary>
    /// Показывать ли блок на этой странице: включено, идентификатор задан
    /// и форма идентификатора похожа на настоящую. Проверка нужна, чтобы
    /// опечатка в конфигурации не превращалась в пустую рамку на странице.
    /// </summary>
    public bool CanRender =>
        Enabled
        && !string.IsNullOrWhiteSpace(BlockId)
        && BlockId.StartsWith("R-A-", StringComparison.OrdinalIgnoreCase)
        && BlockId.All(c => char.IsLetterOrDigit(c) || c == '-');
}
