using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitAISchedule.RevitApi;

/// <summary>Реальное поле ведомости категории: имена + объект Revit API.</summary>
public sealed class ScheduleFieldInfo : IScheduleField
{
    /// <summary>Имя поля так, как Revit показывает его пользователю (в языке интерфейса Revit).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Все реальные имена поля, полученные из Revit.</summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>Встроенный параметр, если поле встроенное (null для полей семейства).</summary>
    public BuiltInParameter? BuiltIn { get; init; }

    /// <summary>Объект Revit API для ScheduleDefinition.AddField(...).</summary>
    public required SchedulableField SchedulableField { get; init; }
}

/// <summary>
/// Получает реальный список полей категории из Revit.
/// Источник истины — ScheduleDefinition.GetSchedulableFields() (Revit API 2026).
/// Никаких выдуманных параметров.
/// </summary>
public static class ScheduleFieldCatalog
{
    /// <summary>
    /// Кэш соответствия ElementId параметра -&gt; BuiltInParameter.
    /// Revit API 2026 отдаёт GetAllBuiltInParameters() как IList&lt;ForgeTypeId&gt;,
    /// а обратное преобразование делает ParameterUtils.GetBuiltInParameter(ForgeTypeId).
    /// </summary>
    private static Dictionary<ElementId, BuiltInParameter>? _builtInMap;

    private static Dictionary<ElementId, BuiltInParameter> BuiltInMap
    {
        get
        {
            if (_builtInMap is not null) return _builtInMap;

            var map = new Dictionary<ElementId, BuiltInParameter>();
            try
            {
                foreach (ForgeTypeId specId in ParameterUtils.GetAllBuiltInParameters())
                {
                    BuiltInParameter bip = ParameterUtils.GetBuiltInParameter(specId);
                    var id = new ElementId(bip);
                    if (!map.ContainsKey(id))
                        map.Add(id, bip);
                }
            }
            catch
            {
                // Если карту построить не удалось, сопоставление пойдёт только по именам.
            }

            _builtInMap = map;
            return _builtInMap;
        }
    }

    /// <summary>
    /// Возвращает список полей, доступных в ведомости указанной категории.
    /// Требует уже созданный ViewSchedule (нужен его ScheduleDefinition).
    /// </summary>
    public static List<ScheduleFieldInfo> GetFields(ViewSchedule schedule, Document doc)
    {
        var result = new List<ScheduleFieldInfo>();

        ScheduleDefinition definition = schedule.Definition;
        if (!definition.IsValidObject)
            return result;

        foreach (SchedulableField sf in definition.GetSchedulableFields())
        {
            string display = GetDisplayName(sf, doc);

            var aliases = new List<string> { display };

            BuiltInParameter? bip = null;
            ElementId parameterId = sf.ParameterId;

            if (parameterId != null && parameterId != ElementId.InvalidElementId &&
                BuiltInMap.TryGetValue(parameterId, out BuiltInParameter found))
            {
                bip = found;
                string enumName = found.ToString();

                // Имя элемента перечисления: ALL_MODEL_MARK
                aliases.Add(enumName);

                // Локализованные метки из Revit API.
                AddIfNotEmpty(aliases, SafeLabel(() => LabelUtils.GetLabelFor(found)));
                AddIfNotEmpty(aliases, SafeLabel(() => LabelUtils.GetLabelFor(found, LanguageType.English_USA)));
                AddIfNotEmpty(aliases, SafeLabel(() => LabelUtils.GetLabelFor(found, LanguageType.Russian)));

                // Части имени без технического префикса: ALL_MODEL_MARK -> MARK
                foreach (string part in enumName.Split('_', StringSplitOptions.RemoveEmptyEntries))
                    aliases.Add(part);
            }

            if (aliases.Count == 0)
                continue;

            result.Add(new ScheduleFieldInfo
            {
                DisplayName = display,
                Aliases = aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BuiltIn = bip,
                SchedulableField = sf
            });
        }

        return result;
    }

    private static string GetDisplayName(SchedulableField sf, Document doc)
    {
        try
        {
            return sf.GetName(doc) ?? sf.ParameterId?.ToString() ?? "?";
        }
        catch
        {
            return sf.ParameterId?.ToString() ?? "?";
        }
    }

    private static string SafeLabel(Func<string> get)
    {
        try { return get() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void AddIfNotEmpty(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            list.Add(value);
    }
}