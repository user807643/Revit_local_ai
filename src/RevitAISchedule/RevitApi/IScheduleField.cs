namespace RevitAISchedule.RevitApi;

/// <summary>
/// Поле, доступное в ведомости, в виде, понятном резолверу.
/// Отделяет логику сопоставления имён от типов Revit API.
/// </summary>
public interface IScheduleField
{
    /// <summary>Имя поля так, как Revit показывает его пользователю.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Все реальные имена поля, полученные из Revit:
    /// имя в языке интерфейса, английская и русская метки, имя BuiltInParameter
    /// и его части без технического префикса.
    /// </summary>
    IReadOnlyList<string> Aliases { get; }
}