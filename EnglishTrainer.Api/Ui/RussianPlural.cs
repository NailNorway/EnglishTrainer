namespace EnglishTrainer.Api.Ui;

/// <summary>
/// Согласование числительных с существительными.
/// </summary>
/// <remarks>
/// «27 слов» и «27 слов(а/о)» — не косметика: страница, собранная генератором
/// из базы, выдаёт свой шаблонный характер ровно такими мелочами, а поисковик
/// оценивает её наравне с написанной руками. Правило одно и то же на всех
/// публичных страницах, поэтому живёт в одном месте.
/// </remarks>
public static class RussianPlural
{
    /// <summary>«1 слово», «2 слова», «27 слов».</summary>
    public static string Words(int count) => $"{count} {WordForm(count)}";

    /// <summary>Только форма слова, без числа.</summary>
    public static string WordForm(int count)
    {
        // Числа 11–14 — исключение: «11 слов», а не «11 слово».
        var lastTwo = count % 100;
        if (lastTwo is >= 11 and <= 14)
            return "слов";

        return (count % 10) switch
        {
            1 => "слово",
            2 or 3 or 4 => "слова",
            _ => "слов"
        };
    }
}
