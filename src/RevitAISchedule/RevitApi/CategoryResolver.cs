using Autodesk.Revit.DB;

namespace RevitAISchedule.RevitApi;

/// <summary>
/// Определяет категорию по строке, полученной от LLM или введённой пользователем.
///
/// Почему нельзя полагаться только на Category.Name:
/// по документации Revit 2026 (LabelUtils.GetLabelFor(BuiltInCategory), Remarks:
/// «The name is obtained in the current Revit language») это имя зависит от языка
/// интерфейса Revit. В русском Revit Category.Name = «Стены», «Электрооборудование»,
/// а LLM и пользователь пишут «Walls», «электрические приборы» — и категория не
/// находилась.
///
/// Порядок поиска (от надёжного к приблизительному):
///   1) встроенное имя перечисления: OST_Walls            — не зависит от языка;
///   2) то же без префикса и с пробелами: Walls,
///      Electrical Equipment;
///   3) словарь синонимов RU/EN: «электрические приборы»
///      -&gt; OST_ElectricalEquipment, «светильники» -&gt; OST_LightingFixtures;
///   4) метка в языке интерфейса Revit: Стены / Walls;
///   5) приблизительное совпадение по основе слова: «стенах» -&gt; Стены.
/// </summary>
public static class CategoryResolver
{
    /// <summary>
    /// Синонимы категорий. Ключ — нормализованная строка (строчные буквы,
    /// без пробелов и знаков). Значение — встроенное имя категории.
    /// Составлен по фактическим меткам Revit 2026 в русском интерфейсе.
    /// </summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        // Стены
        ["стена"] = "OST_Walls",
        ["стены"] = "OST_Walls",
        ["стен"] = "OST_Walls",
        ["стенах"] = "OST_Walls",
        ["wall"] = "OST_Walls",
        ["walls"] = "OST_Walls",
        ["перегородка"] = "OST_Walls",

        // Мебель
        ["мебель"] = "OST_Furniture",
        ["мебели"] = "OST_Furniture",
        ["furniture"] = "OST_Furniture",
        ["системымебели"] = "OST_FurnitureSystems",
        ["системамебели"] = "OST_FurnitureSystems",
        ["furnituresystems"] = "OST_FurnitureSystems",
        ["furnituresystem"] = "OST_FurnitureSystems",

        // Осветительные приборы
        ["осветительныеприборы"] = "OST_LightingFixtures",
        ["осветительныйприбор"] = "OST_LightingFixtures",
        ["светильник"] = "OST_LightingFixtures",
        ["светильники"] = "OST_LightingFixtures",
        ["освещение"] = "OST_LightingFixtures",
        ["свет"] = "OST_LightingFixtures",
        ["lightingfixtures"] = "OST_LightingFixtures",
        ["lightingfixture"] = "OST_LightingFixtures",
        ["lights"] = "OST_LightingFixtures",

        // Электрооборудование.
        // В русском Revit категория называется «Электрооборудование»,
        // поэтому слова «электрические приборы» в словаре необходимы.
        ["электрооборудование"] = "OST_ElectricalEquipment",
        ["электрооборудования"] = "OST_ElectricalEquipment",
        ["электрооборудованию"] = "OST_ElectricalEquipment",
        ["электрическоеоборудование"] = "OST_ElectricalEquipment",
        ["электрическиеприборы"] = "OST_ElectricalEquipment",
        ["электрическийприбор"] = "OST_ElectricalEquipment",
        ["электроприборы"] = "OST_ElectricalEquipment",
        ["электроприбор"] = "OST_ElectricalEquipment",
        ["электрика"] = "OST_ElectricalEquipment",
        ["электротехника"] = "OST_ElectricalEquipment",
        ["electricalequipment"] = "OST_ElectricalEquipment",

        // Механическое оборудование
        ["механическоеоборудование"] = "OST_MechanicalEquipment",
        ["вентиляционноеоборудование"] = "OST_MechanicalEquipment",
        ["вентиляция"] = "OST_MechanicalEquipment",
        ["кондиционеры"] = "OST_MechanicalEquipment",
        ["кондиционер"] = "OST_MechanicalEquipment",
        ["отопление"] = "OST_MechanicalEquipment",
        ["mechanicalequipment"] = "OST_MechanicalEquipment",

        // Сантехническое оборудование
        ["сантехническоеоборудование"] = "OST_PlumbingEquipment",
        ["сантехника"] = "OST_PlumbingEquipment",
        ["трубы"] = "OST_PlumbingEquipment",
        [" труба"] = "OST_PlumbingEquipment",
        ["plumbingequipment"] = "OST_PlumbingEquipment",

        // Прочие ходовые категории
        ["дверь"] = "OST_Doors",
        ["двери"] = "OST_Doors",
        ["door"] = "OST_Doors",
        ["doors"] = "OST_Doors",
        ["окно"] = "OST_Windows",
        ["окна"] = "OST_Windows",
        ["window"] = "OST_Windows",
        ["windows"] = "OST_Windows",
        ["помещение"] = "OST_Rooms",
        ["помещения"] = "OST_Rooms",
        ["комната"] = "OST_Rooms",
        ["комнаты"] = "OST_Rooms",
        ["room"] = "OST_Rooms",
        ["rooms"] = "OST_Rooms",
        ["перекрытие"] = "OST_Floors",
        ["перекрытия"] = "OST_Floors",
        ["пол"] = "OST_Floors",
        ["полы"] = "OST_Floors",
        ["потолок"] = "OST_Ceilings",
        ["потолки"] = "OST_Ceilings",
        ["колонна"] = "OST_StructuralColumns",
        ["колонны"] = "OST_StructuralColumns",
        ["несущиеколонны"] = "OST_StructuralColumns",
        ["columns"] = "OST_StructuralColumns"
    };

    /// <summary>
    /// Возвращает Id категории по строке: «OST_Walls», «Walls», «Стены»,
    /// «электрические приборы», «светильники» и т.п.
    /// </summary>
    public static bool TryResolve(Document doc, string? requested, out ElementId categoryId, out string localizedName)
    {
        categoryId = ElementId.InvalidElementId;
        localizedName = string.Empty;

        if (string.IsNullOrWhiteSpace(requested)) return false;

        string key = Normalize(requested);
        if (key.Length == 0) return false;

        // Допустимые категории Revit для ведомости — источник истины.
        var valid = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach (ElementId id in ViewSchedule.GetValidCategoriesForSchedule())
        {
            Category? cat = Category.GetCategory(doc, id);
            if (cat is not null) valid[cat.BuiltInCategory.ToString()] = cat;
        }

        // 1-2. Языконезависимые имена из перечисления BuiltInCategory.
        foreach (Category cat in valid.Values)
        {
            foreach (string alias in GetEnumAliases(cat.BuiltInCategory))
            {
                if (Normalize(alias) == key)
                {
                    categoryId = cat.Id;
                    localizedName = cat.Name;
                    return true;
                }
            }
        }

        // 3. Словарь синонимов.
        if (Synonyms.TryGetValue(key, out string? builtin))
        {
            if (valid.TryGetValue(builtin, out Category? synonymCategory))
            {
                categoryId = synonymCategory.Id;
                localizedName = synonymCategory.Name;
                return true;
            }
        }

        // 4. Метка в языке интерфейса Revit.
        foreach (Category cat in valid.Values)
        {
            if (Normalize(cat.Name) == key)
            {
                categoryId = cat.Id;
                localizedName = cat.Name;
                return true;
            }
        }

        // 5. Приблизительно: по основе слова. Берём самую короткую метку,
        //    чтобы «стенах» не ушёл в «Стены несущие».
        Category? fuzzy = FindFuzzy(valid, key);
        if (fuzzy is not null)
        {
            categoryId = fuzzy.Id;
            localizedName = fuzzy.Name;
            return true;
        }

        return false;
    }

    /// <summary>Языконезависимые имена категории из имени перечисления.</summary>
    private static IEnumerable<string> GetEnumAliases(BuiltInCategory category)
    {
        string full = category.ToString();
        yield return full;

        string withoutPrefix = StripPrefix(full);
        if (withoutPrefix.Length == 0) yield break;

        yield return withoutPrefix;
        yield return AddWordSpaces(withoutPrefix);
    }

    /// <summary>
    /// Ищет по основе слова: сравниваем нормализованные строки без
    /// последней буквы, чтобы «стена»/«стены»/«стен»/«стенах» сошлись.
    /// </summary>
    private static Category? FindFuzzy(Dictionary<string, Category> valid, string key)
    {
        string keyStem = Stem(key);

        Category? best = null;
        int bestLength = int.MaxValue;

        foreach (Category cat in valid.Values)
        {
            string label = Normalize(cat.Name);
            if (label.Length < 4) continue;

            // Метка Revit целиком равна основе запроса или наоборот.
            if (label == keyStem || Stem(label) == keyStem)
            {
                if (label.Length < bestLength)
                {
                    bestLength = label.Length;
                    best = cat;
                }
                continue;
            }

            // Основа — общее начало: «электрооборудование» и «электрооборудование...».
            string labelStem = Stem(label);
            if (labelStem.Length >= 6 && keyStem.Length >= 6 &&
                labelStem.StartsWith(keyStem, StringComparison.Ordinal))
            {
                if (labelStem.Length < bestLength)
                {
                    bestLength = labelStem.Length;
                    best = cat;
                }
            }
        }

        return best;
    }

    /// <summary>Срезает одну конечную букву — грубая, но предсказуемая основа слова.</summary>
    private static string Stem(string s)
    {
        if (s.Length < 4) return s;

        char last = s[^1];
        if (last is 'ы' or 'а' or 'е' or 'и' or 'у' or 'ю' or 'о' or 'я')
            return s[..^1];

        // Хвост «ов» / «ев» / «ами» / «ях».
        if (s.EndsWith("ами", StringComparison.Ordinal) ||
            s.EndsWith("ями", StringComparison.Ordinal) ||
            s.EndsWith("ях", StringComparison.Ordinal) ||
            s.EndsWith("ов", StringComparison.Ordinal) ||
            s.EndsWith("ев", StringComparison.Ordinal) ||
            s.EndsWith("ей", StringComparison.Ordinal))
        {
            return s[..^2];
        }

        return s;
    }

    /// <summary>Убирает префиксы OST_ / OST- / точку.</summary>
    private static string StripPrefix(string enumName)
    {
        foreach (string prefix in new[] { "OST_", "OST-", "." })
        {
            if (enumName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return enumName[prefix.Length..];
        }

        return string.Empty;
    }

    /// <summary>
    /// ElectricalEquipment -&gt; «Electrical Equipment».
    /// Разбиваем CamelCase, не трогая последовательности заглавных (IFC -&gt; IFC).
    /// </summary>
    private static string AddWordSpaces(string name)
    {
        if (name.Length == 0) return name;

        var sb = new System.Text.StringBuilder(name.Length + 8);

        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];

            bool previousLower = i > 0 && char.IsLower(name[i - 1]);
            bool previousDigit = i > 0 && char.IsDigit(name[i - 1]);
            bool nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
            bool currentUpper = char.IsUpper(current);
            bool currentDigit = char.IsDigit(current);

            bool startNewWord = false;

            if (i > 0)
            {
                if (currentUpper && previousLower) startNewWord = true;
                else if (currentUpper && previousDigit) startNewWord = true;
                else if (currentDigit && previousLower) startNewWord = true;
                else if (currentUpper && char.IsUpper(name[i - 1]) && nextLower) startNewWord = true;
            }

            if (startNewWord) sb.Append(' ');
            sb.Append(current);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Строит список категорий для системной подсказки LLM.
    /// Модели отдаётся встроенное имя OST_*: оно не зависит от языка интерфейса,
    /// поэтому один и тот же список работает в любой локали.
    /// </summary>
    public static string BuildCategoryListForPrompt(Document doc)
    {
        try
        {
            var items = new List<string>();
            foreach (ElementId id in ViewSchedule.GetValidCategoriesForSchedule())
            {
                Category? cat = Category.GetCategory(doc, id);
                if (cat is null) continue;

                items.Add(string.Equals(cat.Name, cat.BuiltInCategory.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? cat.BuiltInCategory.ToString()
                    : $"{cat.BuiltInCategory} — {cat.Name}");
            }

            items.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", items);
        }
        catch
        {
            // Список категорий — вспомогательная подсказка; его недоступность не ломает MVP.
            return string.Empty;
        }
    }

    /// <summary>Нижний регистр, только буквы и цифры.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}