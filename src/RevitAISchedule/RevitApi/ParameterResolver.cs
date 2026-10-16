namespace RevitAISchedule.RevitApi;

/// <summary>Результат сопоставления запрошенных параметров с реальными полями Revit.</summary>
public sealed class ParameterMatchResult
{
    public List<string> Resolved { get; } = new();
    public List<string> NotFound { get; } = new();

    public bool IsValid => NotFound.Count == 0;
}

/// <summary>
/// Сопоставляет названия, запрошенные пользователем/LLM, с реальными полями Revit.
///
/// Порядок шагов подтверждён автотестом на настоящем проекте в Revit 2026
/// (категория «Двери», 271 поле):
///
///   1a) точное совпадение по DisplayName — по имени поля, которое Revit
///       показывает пользователю. Это приоритетнее любых алиасов: у поля
///       «Rough Width» есть алиас-часть «WIDTH», которая иначе перехватила бы
///       запрос «Width»;
///   1b) точное совпадение по остальным реальным именам поля (метки LabelUtils
///       для текущего языка/English/Russian, имя BuiltInParameter и его части);
///   2)  словарь синонимов RU/EN поверх реальных имён Revit;
///   3)  обратный словарь (реальное имя поля ищем среди ключей синонимов);
///   4)  и только в самом конце — DisplayName, содержащий искомое слово.
///
/// Если на одном шаге подходит несколько полей, побеждает поле с САМЫМ КОРОТКИМ
/// именем: для «Height» это «Height», а не «Head Height»; для «Width» — «Width»,
/// а не «Rough Width». Правило детерминированное, поэтому результат воспроизводим.
///
/// Если параметр не найден, он попадает в NotFound и ведомость не создаётся.
/// </summary>
public static class ParameterResolver
{
    /// <summary>Сопоставляет запрошенные параметры с реальными полями категории.</summary>
    public static ParameterMatchResult Resolve<T>(
        IReadOnlyList<string> requested,
        IReadOnlyList<T> availableFields)
        where T : IScheduleField
    {
        var result = new ParameterMatchResult();
        if (requested is null || requested.Count == 0)
            return result;

        var claimed = new HashSet<int>(); // индексы уже занятых полей

        foreach (string raw in requested)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            int index = FindFieldIndex(raw, availableFields, claimed);
            if (index < 0)
            {
                result.NotFound.Add(raw.Trim());
                continue;
            }

            claimed.Add(index);
            result.Resolved.Add(availableFields[index].DisplayName);
        }

        return result;
    }

    /// <summary>Ищет индекс подходящего поля. Возвращает -1, если ничего не найдено.</summary>
    private static int FindFieldIndex<T>(string requested, IReadOnlyList<T> fields, HashSet<int> claimed)
        where T : IScheduleField
    {
        string needle = NameNormalizer.Normalize(requested);
        if (needle.Length == 0) return -1;

        // Шаг 1a: точное совпадение по имени поля, которое видит пользователь в Revit.
        int index = ShortestMatch(fields, claimed,
            f => NameNormalizer.Normalize(f.DisplayName) == needle);
        if (index >= 0) return index;

        // Шаг 1b: точное совпадение по остальным реальным именам поля.
        index = ShortestMatch(fields, claimed, f => Matches(f, needle, exact: true));
        if (index >= 0) return index;

        // Шаг 2: словарь синонимов — подставляем кандидатов и ищем среди полей Revit.
        if (NameNormalizer.Synonyms.TryGetValue(needle, out string[]? candidates))
        {
            foreach (string candidate in candidates)
            {
                string candidateNeedle = NameNormalizer.Normalize(candidate);

                index = ShortestMatch(fields, claimed,
                    f => NameNormalizer.Normalize(f.DisplayName) == candidateNeedle);
                if (index >= 0) return index;

                index = ShortestMatch(fields, claimed,
                    f => Matches(f, candidateNeedle, exact: true));
                if (index >= 0) return index;
            }
        }

        // Шаг 3: имя поля, являющееся ключом словаря синонимов, ведёт к его значениям.
        index = ShortestMatch(fields, claimed, f => IsSynonymKeyOf(f, needle));
        if (index >= 0) return index;

        // Шаг 4 (последний): DisplayName, содержащий искомое слово.
        if (needle.Length >= 3)
            return ShortestMatch(fields, claimed, f => ContainsWholeWord(NormalizeDisplay(f), needle));

        return -1;
    }

    private static string NormalizeDisplay<T>(T field) where T : IScheduleField
        => NameNormalizer.Normalize(field.DisplayName);

    /// <summary>Шаг 3: имя поля — ключ словаря синонимов, ведущий к искомому имени.</summary>
    private static bool IsSynonymKeyOf<T>(T field, string needle) where T : IScheduleField
    {
        foreach (string alias in field.Aliases)
        {
            if (!NameNormalizer.Synonyms.TryGetValue(NameNormalizer.Normalize(alias), out string[]? targets))
                continue;

            if (targets.Any(t => NameNormalizer.Normalize(t) == needle))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Возвращает индекс поля с наименьшим именем среди подходящих.
    /// Это разрешает неоднозначность вроде «Высота» -&gt; Height, а не Head Height.
    /// </summary>
    private static int ShortestMatch<T>(IReadOnlyList<T> fields, HashSet<int> claimed, Func<T, bool> predicate)
        where T : IScheduleField
    {
        int best = -1;
        int bestLength = int.MaxValue;

        for (int i = 0; i < fields.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            if (!predicate(fields[i])) continue;

            int length = fields[i].DisplayName.Length;
            if (length < bestLength)
            {
                bestLength = length;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Ищет needle как отдельное слово (границы по буквам/цифрам).</summary>
    private static bool ContainsWholeWord(string haystack, string needle)
    {
        if (haystack.Length < needle.Length) return false;

        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            bool leftOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
            bool rightOk = at + needle.Length == haystack.Length ||
                           !char.IsLetterOrDigit(haystack[at + needle.Length]);

            if (leftOk && rightOk) return true;

            at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Совпадает ли поле с искомым именем по любому из реальных имён.</summary>
    public static bool Matches(IScheduleField field, string needle, bool exact)
    {
        foreach (string alias in field.Aliases)
        {
            string other = NameNormalizer.Normalize(alias);
            if (other.Length == 0) continue;

            if (exact)
            {
                if (other == needle) return true;
            }
            else if (other.Contains(needle, StringComparison.Ordinal) ||
                     needle.Contains(other, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Нормализация имени (прокси к <see cref="NameNormalizer.Normalize"/>).</summary>
    public static string Normalize(string? s) => NameNormalizer.Normalize(s);
}