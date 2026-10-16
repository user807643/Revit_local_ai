using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RevitAISchedule.Llm;

/// <summary>
/// Провайдер для LM Studio.
/// LM Studio — локальный сервер с OpenAI-совместимым HTTP API:
/// POST http://127.0.0.1:1234/v1/chat/completions
/// Модель работает локально, данные никуда не отправляются.
///
/// Адрес и имя модели берутся из настроек %APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\config.json,
/// которые редактируются в окне «Настройка локальной ИИ-модели».
/// </summary>
public sealed class LmStudioProvider : ILLMProvider, IDisposable
{
    private readonly AiSettings _settings;
    private readonly HttpClient _http;

    public LmStudioProvider(AiSettings? settings = null)
    {
        _settings = settings ?? AiSettings.LoadOrDefault();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds) };
    }

    /// <summary>Конструктор с явными параметрами (используется в тестах).</summary>
    public LmStudioProvider(string baseUrl, string model, int timeoutSeconds)
        : this(new AiSettings
        {
            Endpoint = baseUrl,
            Model = model,
            RequestTimeoutSeconds = timeoutSeconds
        })
    {
    }

    public string DisplayName => $"{_settings.Model} ({ModelEndpoints.TryBuildApiBase(_settings.Endpoint) ?? _settings.Endpoint})";

    public AiSettings Settings => _settings;

    public async Task<string> CompleteAsync(string userPrompt, string availableCategories, CancellationToken ct)
    {
        string? url = ModelEndpoints.TryBuildChatEndpoint(_settings.Endpoint);
        if (url is null)
            throw new LlmException(ModelEndpoints.DescribeInvalidEndpoint(_settings.Endpoint));

        var payload = new
        {
            model = _settings.Model,
            messages = new object[]
            {
                new { role = "system", content = PromptBuilder.SystemPrompt(availableCategories) },
                new { role = "user", content = userPrompt }
            },
            temperature = _settings.Temperature,
            max_tokens = _settings.MaxTokens,
            stream = false
        };

        string json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(
                $"Локальная модель не ответила за {_settings.RequestTimeoutSeconds} с. " +
                "Первый запрос может быть долгим: LM Studio загружает модель с диска.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(
                $"Не удалось подключиться к локальному серверу ИИ по адресу {url}. " +
                "Убедитесь, что LM Studio запущена и в ней включён локальный сервер. " +
                $"({ex.Message})", ex);
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException(
                $"Локальный сервер вернул ошибку {(int)response.StatusCode} {response.ReasonPhrase}. " +
                $"Ответ: {Trim(body, 400)}");
        }

        // Сервер может отдать {"error": ...} с кодом 200 — это тоже ошибка.
        if (ModelResponse.TryReadServerError(body, out string? serverError))
            throw new LlmException("Локальный сервер вернул ошибку: " + serverError);

        string content = ExtractContent(body, _settings.MaxTokens, out string finishReason);

        // Сервер обрезал ответ по лимиту токенов, и JSON остался незакрытым.
        // Для reasoning-моделей это обычная ситуация: они тратят токены на размышления,
        // а на сам JSON остаётся меньше, чем нужно.
        if (finishReason == "length" && !IsCompleteJson(content))
            throw new LlmException(BuildTruncatedMessage(content));

        return content;
    }

    /// <summary>
    /// Достаёт текст ответа из OpenAI-совместимого ответа.
    /// Всё чтение внутри using: JsonElement из JsonDocument становится недействительным,
    /// как только документ закрыт. Наружу отдаются только готовые строки.
    /// </summary>
    private static string ExtractContent(string body, int maxTokens, out string finishReason)
    {
        string content;
        int reasoningLength;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                finishReason = string.Empty;
                throw new LlmException($"Не удалось прочитать ответ локального сервера: {Trim(body, 400)}");
            }

            JsonElement choice = choices[0];

            finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                ? fr.GetString() ?? string.Empty
                : string.Empty;

            if (!choice.TryGetProperty("message", out JsonElement message))
                throw new LlmException($"В ответе нет поля message: {Trim(body, 400)}");

            // content бывает строкой (классика) или массивом частей
            // [{"type":"text","text":"..."}] — второй формат используют новые
            // версии LM Studio и OpenAI.
            content = ReadContent(message);

            // reasoning-модели (qwen3, ministral-reasoning) могут положить весь
            // текст в reasoning_content, оставив content пустым.
            string reasoning = content.Length == 0 ? ReadReasoning(message) : string.Empty;
            reasoningLength = reasoning.Length;

            if (content.Length == 0) content = reasoning;
        }
        catch (JsonException ex)
        {
            finishReason = string.Empty;
            throw new LlmException($"Локальный сервер вернул некорректный JSON: {ex.Message}", ex);
        }

        if (content.Trim().Length > 0) return content;

        // Ничего нет — объясняем почему, вместо безликого «пустой ответ».
        throw new LlmException(BuildEmptyAnswerMessage(finishReason, reasoningLength, maxTokens));
    }

    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => ReadContentParts(content),
            JsonValueKind.Null => string.Empty,
            _ => content.ToString()
        };
    }

    /// <summary>Новый формат: content = [ {"type":"text","text":"..."}, ... ].</summary>
    private static string ReadContentParts(JsonElement array)
    {
        var sb = new StringBuilder();

        foreach (JsonElement part in array.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                sb.Append(part.GetString());
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// reasoning-модели кладут текст в reasoning_content.
    /// </summary>
    private static string ReadReasoning(JsonElement message)
    {
        if (!message.TryGetProperty("reasoning_content", out var reasoning)) return string.Empty;

        return reasoning.ValueKind switch
        {
            JsonValueKind.String => reasoning.GetString() ?? string.Empty,
            JsonValueKind.Array => ReadContentParts(reasoning),
            _ => string.Empty
        };
    }

    /// <summary>Проверяет, что в тексте есть закрытый JSON-объект.</summary>
    private static bool IsCompleteJson(string text)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        foreach (char c in text)
        {
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return true;
                    break;
            }
        }

        return false;
    }

    private string BuildTruncatedMessage(string content)
    {
        int braces = content.Count(c => c == '{') - content.Count(c => c == '}');
        return "Локальная модель не успела дописать ответ: закончился лимит токенов " +
               $"(осталось незакрытых скобок: {braces}). " +
               $"Сейчас «Токенов ответа» = {_settings.MaxTokens}. " +
               "Откройте «Настройки ИИ-модели» и увеличьте значение. " +
               "Если и это не помогает — выберите модель поменьше или отключите режим размышлений " +
               "(reasoning) в LM Studio.";
    }

    /// <summary>
    /// Понятное объяснение пустого ответа. Самая частая причина — модель
    /// размышляет в reasoning_content и не успевает выдать ответ: сервер
    /// возвращает finish_reason=length, когда исчерпан max_tokens.
    /// </summary>
    private static string BuildEmptyAnswerMessage(string finishReason, int reasoningLength, int maxTokens)
    {
        if (finishReason == "length")
        {
            string reason = reasoningLength > 0
                ? "Модель потратила весь лимит на размышления (reasoning_content) и не успела выдать ответ."
                : "Модель не успела завершить ответ.";

            return $"Локальная модель вернула пустой ответ: {reason} " +
                   $"Сейчас «Токенов ответа» = {maxTokens}. " +
                   "Откройте «Настройки ИИ-модели» и увеличьте значение (например до 8000).";
        }

        return "Локальная модель вернула пустой ответ (finish_reason=" +
               (string.IsNullOrEmpty(finishReason) ? "?" : finishReason) + "). " +
               "Проверьте модель в LM Studio: возможно, она не загружена или не поддерживает чат.";
    }

    private static string Trim(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }

    public void Dispose() => _http.Dispose();
}
