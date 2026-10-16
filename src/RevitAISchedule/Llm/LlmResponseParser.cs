using System.Text.Json;
using RevitAISchedule.Model;

namespace RevitAISchedule.Llm;

/// <summary>
/// Разбирает необработанный ответ LLM в <see cref="ScheduleRequest"/>.
/// Модели часто оборачивают JSON в markdown, а reasoning-модели могут обрезать
/// ответ по лимиту токенов прямо в середине объекта — оба случая обрабатываются.
/// </summary>
internal static class LlmResponseParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ScheduleRequest Parse(string raw) => Parse(raw, null);

    /// <summary>
    /// finishReason — значение finish_reason от сервера. Нужно, чтобы отличить
    /// «модель не успела дописать JSON» от «модель ответила ерундой».
    /// </summary>
    public static ScheduleRequest Parse(string raw, string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new LlmException(
                finishReason == "length"
                    ? "Локальная модель не успела ответить: закончился лимит токенов. " +
                      "Откройте «Настройки ИИ-модели» и увеличьте «Токенов ответа»."
                    : "LLM вернула пустой ответ.");
        }

        string json;
        try
        {
            json = ExtractJsonObject(raw);
        }
        catch (LlmException ex)
        {
            throw new LlmException(Explain(ex.Message, finishReason));
        }

        ScheduleRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ScheduleRequest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new LlmException(
                Explain($"LLM вернула некорректный JSON: {ex.Message}", finishReason));
        }

        if (request is null)
            throw new LlmException("LLM вернула JSON, который не соответствует формату ScheduleRequest.");

        return Normalize(request);
    }

    /// <summary>
    /// Приводит ответ к рабочему виду: срезает пробелы, убирает пустые элементы.
    /// Модели часто добавляют переносы строк внутри массивов.
    /// </summary>
    private static ScheduleRequest Normalize(ScheduleRequest request)
    {
        request.ScheduleName = (request.ScheduleName ?? string.Empty).Trim();
        request.Category = (request.Category ?? string.Empty).Trim();

        request.Parameters = Clean(request.Parameters);

        if (request.SortBy is not null)
            request.SortBy = Clean(request.SortBy);

        if (request.Filters is not null)
        {
            foreach (ScheduleFilterSpec filter in request.Filters)
            {
                filter.Parameter = (filter.Parameter ?? string.Empty).Trim();
                filter.Operator = string.IsNullOrWhiteSpace(filter.Operator)
                    ? "notEmpty"
                    : filter.Operator.Trim();
            }
        }

        return request;
    }

    private static List<string> Clean(List<string>? items)
    {
        var result = new List<string>();
        if (items is null) return result;

        foreach (string item in items)
        {
            string trimmed = (item ?? string.Empty).Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }

        return result;
    }

    /// <summary>
    /// Уточняет сообщение об ошибке, если ответ был обрезан по лимиту токенов.
    /// </summary>
    private static string Explain(string message, string? finishReason) =>
        finishReason == "length"
            ? message + " Ответ обрезан: закончился лимит токенов. " +
              "Откройте «Настройки ИИ-модели» и увеличьте «Токенов ответа»."
            : message;

    /// <summary>Вырезает первый корректный JSON-объект из текста, игнорируя markdown и пролог.</summary>
    private static string ExtractJsonObject(string text)
    {
        text = text.Trim();

        int start = text.IndexOf('{');
        if (start < 0)
            throw new LlmException($"В ответе LLM нет JSON-объекта. Ответ модели: {Cut(text, 400)}");

        // Идём вглубь, считая баланс скобок, учитывая строковые литералы.
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }

        throw new LlmException(
            $"JSON-объект в ответе не закрыт — ответ оборвался на полпути. Ответ модели: {Cut(text, 400)}");
    }

    private static string Cut(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}