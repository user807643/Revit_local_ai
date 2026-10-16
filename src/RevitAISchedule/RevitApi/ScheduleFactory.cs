using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAISchedule.Model;

namespace RevitAISchedule.RevitApi;

/// <summary>
/// Создаёт настоящую ViewSchedule через Revit API 2026.
/// Вызывается ТОЛЬКО из IExternalEventHandler внутри Transaction.
/// </summary>
public static class ScheduleFactory
{
    /// <summary>
    /// Создаёт ведомость в текущем открытом проекте.
    /// Бросает <see cref="ScheduleValidationException"/>, если параметры не найдены —
    /// транзакция откатывается, ведомость в проекте не остаётся.
    /// </summary>
    public static ScheduleOutcome Create(UIDocument uiDoc, ScheduleRequest request)
    {
        Document doc = uiDoc.Document;

        if (doc.IsFamilyDocument)
            throw new ScheduleValidationException("Ведомости можно создавать только в проекте, а не в семействе.");

        if (!CategoryResolver.TryResolve(doc, request.Category, out ElementId categoryId, out string categoryName))
        {
            throw new ScheduleValidationException(
                $"Категория '{request.Category}' не найдена в этом проекте. " +
                "Укажите категорию, для которой Revit разрешает ведомость.");
        }

        if (!ViewSchedule.IsValidCategoryForSchedule(categoryId))
        {
            throw new ScheduleValidationException(
                $"Для категории '{categoryName}' Revit не допускает создание ведомости.");
        }

        // Создаём ведомость — это нужно и для создания, и для получения списка полей.
        ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, categoryId);

        ScheduleDefinition definition = schedule.Definition;

        // Источник истины по параметрам — реальные поля этой категории в Revit.
        List<ScheduleFieldInfo> available = ScheduleFieldCatalog.GetFields(schedule, doc);

        ParameterMatchResult match = ParameterResolver.Resolve(request.Parameters, available);

        if (!match.IsValid)
        {
            string categoryLabel = CategoryResolver.TryResolve(doc, request.Category, out _, out string n) ? n : request.Category;
            throw new ScheduleValidationException(BuildMissingParametersMessage(match.NotFound, categoryLabel, available));
        }

        if (match.Resolved.Count == 0)
        {
            throw new ScheduleValidationException(
                "Не указано ни одного поля ведомости. Напишите, например: " +
                "\"Создай ведомость дверей с параметрами Марка, Тип, Ширина, Высота\".");
        }

        // Добавляем поля в ведомость.
        var addedFields = new List<ScheduleFieldInfo>();
        foreach (string displayName in match.Resolved)
        {
            ScheduleFieldInfo? info = available.FirstOrDefault(f => f.DisplayName == displayName);
            if (info is null) continue;

            ScheduleField field = definition.AddField(info.SchedulableField);
            if (field != null)
                addedFields.Add(info);
        }

        if (addedFields.Count == 0)
        {
            throw new ScheduleValidationException("Не удалось добавить ни одного поля в ведомость.");
        }

        ApplySorting(definition, addedFields, request.SortBy);
        ApplyFilters(definition, addedFields, request.Filters);

        // Имя ведомости.
        string scheduleName = string.IsNullOrWhiteSpace(request.ScheduleName)
            ? schedule.Name
            : request.ScheduleName.Trim();

        schedule.Name = scheduleName;

        doc.Regenerate();

        return ScheduleOutcome.Ok(
            scheduleName,
            schedule.Id.Value,
            categoryName,
            addedFields.Select(f => f.DisplayName).ToList());
    }

    /// <summary>Настраивает сортировку по первому найденному полю из sortBy.</summary>
    private static void ApplySorting(
        ScheduleDefinition definition,
        List<ScheduleFieldInfo> addedFields,
        List<string>? sortBy)
    {
        if (sortBy is null || sortBy.Count == 0) return;

        foreach (string wanted in sortBy)
        {
            if (string.IsNullOrWhiteSpace(wanted)) continue;

            ScheduleFieldInfo? target = addedFields.FirstOrDefault(f =>
                f.Aliases.Any(a => ParameterResolver.Normalize(a) == ParameterResolver.Normalize(wanted)));

            if (target is null) continue;

            // Ищем ScheduleField по индексу в определении.
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField sf = definition.GetField(i);
                if (sf.ParameterId == target.SchedulableField.ParameterId &&
                    definition.CanSortByField(sf.FieldId))
                {
                    definition.AddSortGroupField(new ScheduleSortGroupField(sf.FieldId, ScheduleSortOrder.Ascending));
                    return;
                }
            }
        }
    }

    /// <summary>Добавляет фильтры, если они заданы и поддерживаются полем.</summary>
    private static void ApplyFilters(
        ScheduleDefinition definition,
        List<ScheduleFieldInfo> addedFields,
        List<ScheduleFilterSpec>? filters)
    {
        if (filters is null || filters.Count == 0) return;
        if (!definition.CanFilter()) return;

        foreach (ScheduleFilterSpec spec in filters)
        {
            if (string.IsNullOrWhiteSpace(spec.Parameter)) continue;

            ScheduleFieldInfo? target = addedFields.FirstOrDefault(f =>
                f.Aliases.Any(a => ParameterResolver.Normalize(a) == ParameterResolver.Normalize(spec.Parameter)));

            if (target is null) continue;

            if (!TryParseFilterType(spec.Operator, out ScheduleFilterType filterType))
                filterType = ScheduleFilterType.HasValue; // notEmpty по умолчанию

            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField sf = definition.GetField(i);
                if (sf.ParameterId != target.SchedulableField.ParameterId) continue;

                ScheduleFieldId fieldId = sf.FieldId;
                bool applied;

                if (filterType is ScheduleFilterType.HasValue or ScheduleFilterType.HasNoValue)
                {
                    if (!definition.CanFilterByParameterExistence(fieldId)) continue;
                    applied = TryAddFilter(definition, new ScheduleFilter(fieldId, filterType));
                }
                else
                {
                    if (!definition.CanFilterByValue(fieldId)) continue;
                    string value = spec.Value ?? string.Empty;
                    applied = TryAddFilter(definition, new ScheduleFilter(fieldId, filterType, value));
                }

                if (applied) return;
            }
        }
    }

    private static bool TryAddFilter(ScheduleDefinition definition, ScheduleFilter filter)
    {
        try
        {
            definition.AddFilter(filter);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseFilterType(string? op, out ScheduleFilterType type)
    {
        type = ScheduleFilterType.Equal;
        if (string.IsNullOrWhiteSpace(op)) { type = ScheduleFilterType.HasValue; return true; }

        switch (op.Trim().ToLowerInvariant())
        {
            case "equals":
            case "equal":
            case "=": type = ScheduleFilterType.Equal; return true;
            case "notequals":
            case "notequal":
            case "!=": type = ScheduleFilterType.NotEqual; return true;
            case "contains": type = ScheduleFilterType.Contains; return true;
            case "notcontains": type = ScheduleFilterType.NotContains; return true;
            case "beginswith": type = ScheduleFilterType.BeginsWith; return true;
            case "endswith": type = ScheduleFilterType.EndsWith; return true;
            case "greaterthan": type = ScheduleFilterType.GreaterThan; return true;
            case "lessthan": type = ScheduleFilterType.LessThan; return true;
            case "notempty": type = ScheduleFilterType.HasValue; return true;
            case "empty": type = ScheduleFilterType.HasNoValue; return true;
            default: type = ScheduleFilterType.HasValue; return true;
        }
    }

    /// <summary>
    /// Формирует понятное сообщение об ошибке согласно ТЗ:
    /// "Параметр X не найден для категории Y."
    /// </summary>
    private static string BuildMissingParametersMessage(
        List<string> missing,
        string categoryName,
        List<ScheduleFieldInfo> available)
    {
        var sb = new System.Text.StringBuilder();

        if (missing.Count == 1)
            sb.Append($"Параметр «{missing[0]}» не найден для категории «{categoryName}». ");
        else
        {
            sb.Append($"Параметры {string.Join(", ", missing.Select(m => $"«{m}»"))} " +
                      $"не найдены для категории «{categoryName}». ");
        }

        sb.Append("Ведомость не создана.");

        // Показываем доступные поля, чтобы пользователь мог сразу исправить запрос.
        var interesting = available
            .Where(f => f.DisplayName is not null)
            .Select(f => f.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        if (interesting.Count > 0)
            sb.Append("\n\nДоступные поля для этой категории: " + string.Join(", ", interesting) + ".");

        return sb.ToString();
    }
}

/// <summary>Ошибка валидации, из-за которой ведомость не должна создаваться.</summary>
public sealed class ScheduleValidationException : Exception
{
    public ScheduleValidationException(string message) : base(message) { }
}