namespace RevitAISchedule.Model;

/// <summary>
/// Результат попытки создать ведомость.
/// Возвращается в UI и показывается пользователю как есть.
/// </summary>
public sealed class ScheduleOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Имя созданной ведомости (только при успехе).</summary>
    public string ScheduleName { get; init; } = string.Empty;

    /// <summary>Id созданного элемента (только при успехе).</summary>
    public long ScheduleId { get; init; } = -1;

    /// <summary>Локализованное имя категории, для которой создана ведомость.</summary>
    public string CategoryName { get; init; } = string.Empty;

    /// <summary>Человекочитаемый список добавленных полей.</summary>
    public IReadOnlyList<string> AddedFields { get; init; } = Array.Empty<string>();

    public static ScheduleOutcome Ok(string scheduleName, long id, string categoryName, IReadOnlyList<string> fields, string? extra = null)
    {
        string message = $"Ведомость '{scheduleName}' создана.";
        if (fields.Count > 0)
            message += " Поля: " + string.Join(", ", fields) + ".";
        if (!string.IsNullOrWhiteSpace(extra))
            message += " " + extra;
        return new ScheduleOutcome
        {
            Success = true,
            Message = message,
            ScheduleName = scheduleName,
            ScheduleId = id,
            CategoryName = categoryName,
            AddedFields = fields
        };
    }

    public static ScheduleOutcome Fail(string message) => new() { Success = false, Message = message };
}