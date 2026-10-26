using System.Text.Json;

namespace EnglishTrainer.Api.Seo;

/// <summary>
/// Разметка schema.org для публичных страниц.
/// </summary>
/// <remarks>
/// <para>
/// Разметка собирается на сервере из тех же объектов, что и сама страница.
/// Писать её руками в разметке — значит поддерживать второй список слов,
/// который разойдётся с базой при первом же импорте словаря.
/// </para>
/// <para>
/// Узлы собираются словарями, а не анонимными типами. Причина в именах полей:
/// в schema.org они начинаются с «@» (<c>@type</c>, <c>@context</c>), а в C#
/// такая запись означает служебный идентификатор — свойство получило бы имя
/// <c>type</c>, и разметка молча теряла бы тип. Словарь с ключом-строкой
/// пишет ровно то, что нужно.
/// </para>
/// <para>
/// Сериализатор намеренно оставлен с настройками по умолчанию: кириллица
/// уедет в <c>\uXXXX</c>, и это правильно. Настройки «человекочитаемого»
/// JSON отключают экранирование символов <c>&lt;</c> и <c>&gt;</c>, а разметка
/// вставляется внутрь <c>&lt;script&gt;</c> — там такая строка превращается
/// в способ закрыть тег содержимым словаря.
/// </para>
/// </remarks>
internal static class StructuredData
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Узел разметки с полями в заданном порядке.</summary>
    public static Dictionary<string, object?> Node(params (string Key, object? Value)[] fields)
    {
        // Порядок полей сохраняется: словарь только пополняется, а это
        // единственное, что нужно для читаемого результата.
        var node = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);

        foreach (var (key, value) in fields)
        {
            node[key] = value;
        }

        return node;
    }

    /// <summary>Несколько узлов под общим <c>@context</c>.</summary>
    public static string Graph(params object[] nodes) => Serialize(Node(
        ("@context", "https://schema.org"),
        ("@graph", nodes)));

    /// <summary>
    /// Цепочка навигации. Первым всегда идёт главная: робот показывает её
    /// в выдаче вместо длинного адреса.
    /// </summary>
    public static object Breadcrumbs(params (string Name, string Url)[] crumbs) => Node(
        ("@type", "BreadcrumbList"),
        ("itemListElement", crumbs.Select((crumb, index) => Node(
            ("@type", "ListItem"),
            ("position", index + 1),
            ("name", crumb.Name),
            ("item", crumb.Url))).ToArray()));

    /// <summary>Веб-сайт: общий владелец всех страниц каталога.</summary>
    public static object WebSite(string baseUrl) => Node(
        ("@type", "WebSite"),
        ("name", "EnglishTrainer"),
        ("url", $"{baseUrl}/"));

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
