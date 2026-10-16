namespace RevitAISchedule.Llm;

/// <summary>
/// Приведение адреса локальной модели к рабочим URL.
///
/// Пользователь вводит в настройках базовый адрес сервера — так его и
/// документируют LM Studio, Ollama, vLLM и llama.cpp:
///     http://localhost:1234/v1
///     http://127.0.0.1:11434
/// Но запрос чата нужно отправлять на .../v1/chat/completions. Если требовать
/// от пользователя полный адрес, то при вводе базового сервер отвечает
/// {"error":"Unexpected endpoint or method"} — то есть «ИИ не отвечает».
/// Здесь адрес приводится к нужному виду автоматически.
///
/// Реализовано по образцу ModelEndpoints из проекта RevitMcpChat.
/// </summary>
public static class ModelEndpoints
{
    private const string ChatSuffix = "/chat/completions";
    private const string ModelsSuffix = "/models";
    private const string ApiPrefix = "/v1";

    /// <summary>Базовый адрес API (…/v1) из любого варианта ввода.</summary>
    public static string? TryBuildApiBase(string? endpoint)
    {
        Uri? uri = ParseHttpUri(endpoint);
        if (uri is null) return null;

        string path = uri.AbsolutePath.TrimEnd('/');

        const string chatCompletions = "/chat/completions";
        int index = path.LastIndexOf(chatCompletions, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            path = path[..index];
        }
        else
        {
            const string models = "/models";
            int modelsIndex = path.LastIndexOf(models, StringComparison.OrdinalIgnoreCase);
            if (modelsIndex >= 0) path = path[..modelsIndex];
        }

        const string chat = "/chat";
        if (path.EndsWith(chat, StringComparison.OrdinalIgnoreCase))
            path = path[..^chat.Length].TrimEnd('/');

        const string completions = "/completions";
        if (path.EndsWith(completions, StringComparison.OrdinalIgnoreCase))
            path = path[..^completions.Length].TrimEnd('/');

        // Голый адрес без пути трактуем как корень API: у LM Studio и Ollama
        // OpenAI-совместимые пути начинаются с /v1.
        if (path.Length == 0) path = ApiPrefix;

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>Полный адрес чат-эндпоинта: …/v1/chat/completions.</summary>
    public static string? TryBuildChatEndpoint(string? endpoint)
    {
        string? api = TryBuildApiBase(endpoint);
        return api is null ? null : api + ChatSuffix;
    }

    /// <summary>Адрес списка моделей: …/v1/models.</summary>
    public static string? TryBuildModelsEndpoint(string? endpoint)
    {
        string? api = TryBuildApiBase(endpoint);
        return api is null ? null : api + ModelsSuffix;
    }

    /// <summary>Текст ошибки для случая, когда адрес не удаётся разобрать.</summary>
    public static string DescribeInvalidEndpoint(string? endpoint)
    {
        string shown = string.IsNullOrWhiteSpace(endpoint) ? "(пусто)" : endpoint;
        return "Адрес модели указан неверно: " + shown +
               ". Ожидается базовый адрес (http://127.0.0.1:1234/v1) " +
               "или полный (http://127.0.0.1:1234/v1/chat/completions).";
    }

    private static Uri? ParseHttpUri(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? parsed))
            return null;

        return parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps
            ? parsed
            : null;
    }
}