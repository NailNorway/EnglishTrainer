namespace EnglishTrainer.Application.Dtos;

// ─────────────────────────── Публичный каталог ───────────────────────────
//
// Это не «ещё одни DTO для API»: здесь описан ровно тот объём данных, который
// можно показать человеку без аккаунта. Отдельные типы, а не переиспользование
// DeckDto, по двум причинам. Первая: у DeckDto есть прогресс пользователя,
// которого в публичном каталоге нет вовсе. Вторая: публичная страница — это
// обещание, что наружу не утечёт ничего лишнего, и обещание проще держать,
// когда типы разные.

/// <summary>Колода в списке на странице «Слова по темам».</summary>
/// <param name="Slug">Часть адреса: <c>/decks/travel-hotel</c>.</param>
/// <param name="Description">Одна строка о теме — она же идёт в описание страницы.</param>
/// <param name="WordCount">Сколько слов в колоде: человек выбирает по объёму.</param>
public sealed record PublicDeckSummaryDto(
    string Slug,
    string Title,
    string? Description,
    int WordCount);

/// <summary>Колода целиком — для её собственной страницы.</summary>
public sealed record PublicDeckDto(
    string Slug,
    string Title,
    string? Description,
    IReadOnlyList<PublicWordDto> Words);

/// <summary>Слово на публичной странице колоды.</summary>
/// <remarks>
/// Отдаётся только то, что полезно человеку, который пришёл из поиска
/// за списком слов: написание, транскрипция, перевод и один пример.
/// Идентификаторы и служебные поля здесь не нужны — по ним нельзя ни
/// обратиться к API, ни узнать что-то о пользователях.
/// </remarks>
/// <param name="Transcription">Британская транскрипция, если она есть в словаре.</param>
/// <param name="Level">Уровень CEFR строкой (<c>A1</c>…<c>C2</c>) или <c>null</c>, если неизвестен.</param>
public sealed record PublicWordDto(
    string Lemma,
    string PartOfSpeech,
    string? Transcription,
    string? Level,
    string Translation,
    string? ExampleEn,
    string? ExampleRu);
