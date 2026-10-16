using System.Text.Json.Serialization;

namespace RevitAISchedule.Model;

/// <summary>
/// Структурированный запрос, который возвращает локальная LLM.
/// Это единственный контракт между LLM и Revit API: LLM не обращается к Revit.
/// </summary>
public sealed class ScheduleRequest
{
    /// <summary>Имя создаваемой ведомости, например "Ведомость дверей".</summary>
    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    /// <summary>
    /// Категория: встроенное имя (<c>OST_Doors</c>), английское имя (<c>Doors</c>)
    /// или локализованное имя (<c>Двери</c>).
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Запрошенные пользователем поля ведомости.</summary>
    [JsonPropertyName("parameters")]
    public List<string> Parameters { get; set; } = new();

    /// <summary>Необязательно: поля для сортировки по возрастанию, в порядке приоритета.</summary>
    [JsonPropertyName("sortBy")]
    public List<string>? SortBy { get; set; }

    /// <summary>Необязательно: фильтры.</summary>
    [JsonPropertyName("filters")]
    public List<ScheduleFilterSpec>? Filters { get; set; }
}

/// <summary>Один фильтр ведомости.</summary>
public sealed class ScheduleFilterSpec
{
    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = string.Empty;

    /// <summary>
    /// equals, notEquals, contains, beginsWith, endsWith,
    /// greaterThan, lessThan, notEmpty, empty
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "notEmpty";

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}