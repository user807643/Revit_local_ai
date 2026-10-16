using System.Text;

namespace RevitAISchedule.RevitApi;

/// <summary>
/// Нормализация имён полей и словарь синонимов.
/// Вынесено отдельно от Revit API, чтобы логику можно было тестировать
/// без запущенного Revit.
/// </summary>
public static class NameNormalizer
{
    /// <summary>
    /// Приводит строку к сравнимому виду: нижний регистр, убрать пробелы,
    /// подчёркивания и дефисы, срезать хвостовую букву русского множественного числа.
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        string result = sb.ToString();

        // Русские формы множественного числа: "Ширина" -> "ширин", "Ширины" -> "ширин".
        if (result.Length >= 4)
        {
            char last = result[^1];
            if (last == '\u044B' ||   // ы
                last == '\u0430' ||   // а
                last == '\u0435')     // е
            {
                result = result[..^1];
            }
        }

        return result;
    }

    /// <summary>
    /// Словарь синонимов типовых полей Revit: ключ — как пишет пользователь или LLM,
    /// значение — реальные имена, которые ищем среди полей Revit.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Synonyms { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Марка / Mark
            ["марка"] = new[] { "Mark" },
            ["mark"] = new[] { "Mark" },
            ["код"] = new[] { "Mark" },
            ["шифр"] = new[] { "Mark" },
            ["маркировка"] = new[] { "Mark" },

            // Тип / Type
            ["тип"] = new[] { "Type", "Family and Type", "Family Name", "Family" },
            ["type"] = new[] { "Type", "Family and Type", "Family Name", "Family" },
            ["наименованиетипа"] = new[] { "Type", "Family and Type", "Family Name" },
            ["typename"] = new[] { "Type", "Family and Type", "Family Name" },
            ["семействоитип"] = new[] { "Family and Type" },
            ["семейство"] = new[] { "Family Name", "Family" },
            ["family"] = new[] { "Family Name", "Family" },
            ["familyname"] = new[] { "Family Name" },
            ["названиесемейства"] = new[] { "Family Name" },

            // Геометрия
            ["ширина"] = new[] { "Width" },
            ["width"] = new[] { "Width" },
            ["толщина"] = new[] { "Width" },
            ["высота"] = new[] { "Height" },
            ["height"] = new[] { "Height" },
            ["длина"] = new[] { "Length" },
            ["length"] = new[] { "Length" },
            ["площадь"] = new[] { "Area" },
            ["area"] = new[] { "Area" },
            ["объем"] = new[] { "Volume" },
            ["volume"] = new[] { "Volume" },
            ["периметр"] = new[] { "Perimeter" },
            ["perimeter"] = new[] { "Perimeter" },

            // Описание / комментарии
            ["описание"] = new[] { "Description", "Comments" },
            ["description"] = new[] { "Description", "Comments" },
            ["комментарий"] = new[] { "Comments" },
            ["комментарии"] = new[] { "Comments" },
            ["comments"] = new[] { "Comments" },
            ["примечание"] = new[] { "Comments", "Description" },
            ["производитель"] = new[] { "Manufacturer" },
            ["manufacturer"] = new[] { "Manufacturer" },
            ["url"] = new[] { "URL" },
            ["ссылка"] = new[] { "URL" },
            ["стоимость"] = new[] { "Cost" },
            ["cost"] = new[] { "Cost" },
            ["модель"] = new[] { "Model" },
            ["model"] = new[] { "Model" },

            // Количество / уровень / категория
            ["количество"] = new[] { "Count" },
            ["count"] = new[] { "Count" },
            ["уровень"] = new[] { "Level", "Level Name" },
            ["level"] = new[] { "Level", "Level Name" },
            ["этаж"] = new[] { "Level", "Level Name" },
            ["категория"] = new[] { "Category" },
            ["category"] = new[] { "Category" },

            // Прочее
            ["имяэлемента"] = new[] { "Name" },
            ["name"] = new[] { "Name" },
            ["идентификатор"] = new[] { "Unique ID" },
            ["uniqueid"] = new[] { "Unique ID" }
        };
}