namespace EnglishTrainer.Api.Legal;

/// <summary>
/// Реквизиты оператора персональных данных для юридических страниц.
/// </summary>
/// <remarks>
/// <para>
/// Вынесено в настройки, а не написано прямо в тексте страницы: по 152-ФЗ
/// политика должна называть оператора, а его статус (самозанятый, ИП или
/// организация) и ИНН меняются независимо от кода. Правка одного значения
/// в конфигурации дешевле, чем правка разметки в четырёх местах.
/// </para>
/// <para>
/// Пустые значения — рабочий режим: страница покажет нейтральную формулировку
/// вместо пустой строки. Но для реальной публикации реквизиты нужно заполнить,
/// иначе политика не выполняет свою функцию.
/// </para>
/// </remarks>
public sealed class LegalOptions
{
    public const string SectionName = "Legal";

    /// <summary>
    /// Оператор: «самозанятый Иванов Иван Иванович», «ИП Иванов И. И.» или
    /// название организации. Пусто — будет «администратор сайта EnglishTrainer».
    /// </summary>
    public string OperatorName { get; set; } = string.Empty;

    /// <summary>ИНН оператора. Для самозанятого и ИП — 12 цифр.</summary>
    public string OperatorInn { get; set; } = string.Empty;

    /// <summary>
    /// Адрес для обращений по персональным данным. Он же показывается
    /// на странице контактов — второй адрес не нужен, иначе письма начнут
    /// теряться в двух ящиках.
    /// </summary>
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>Дата редакции политики — её видно на странице.</summary>
    public string UpdatedOn { get; set; } = "27 октября 2026";

    /// <summary>Как назвать оператора в тексте, если реквизиты не заполнены.</summary>
    public string OperatorDisplay =>
        string.IsNullOrWhiteSpace(OperatorName)
            ? "администратор сайта EnglishTrainer"
            : OperatorName.Trim();
}
