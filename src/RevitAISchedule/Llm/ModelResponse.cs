using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAISchedule.Llm;

/// <summary>
/// Разбор ответов локальной модели.
///
/// LM Studio и Ollama могут вернуть {"error": "..."} или
/// {"error": {"message": "..."}} с кодом 200. Ниже такой ответ считается
/// ошибкой, а не успешным пустым ответом.
/// </summary>
internal static class ModelResponse
{
    public static bool TryReadServerError(JsonNode? root, out string? message)
    {
        message = null;
        if (root?["error"] is not JsonValue errorValue) return false;

        if (errorValue.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            message = text;
            return true;
        }

        // {"error": {"message": "...", "type": "..."}} — вложенный объект.
        if (root?["error"] is JsonObject errorObject
            && errorObject["message"] is JsonValue nestedValue
            && nestedValue.TryGetValue(out string? nested) && !string.IsNullOrWhiteSpace(nested))
        {
            message = nested;
            return true;
        }

        return false;
    }

    public static bool TryReadServerError(string body, out string? message)
    {
        message = null;
        try
        {
            return TryReadServerError(JsonNode.Parse(body), out message);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}