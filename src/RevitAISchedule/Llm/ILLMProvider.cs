namespace RevitAISchedule.Llm;

/// <summary>
/// Абстракция локальной LLM.
/// LLM возвращает ТОЛЬКО текст (JSON). Она никогда не обращается к Revit API.
/// </summary>
public interface ILLMProvider
{
    /// <summary>Человекочитаемое имя провайдера для UI и логов.</summary>
    string DisplayName { get; }

    /// <summary>Преобразует произвольный текст пользователя в подсказку для модели.</summary>
    /// <param name="userPrompt">Запрос пользователя из окна плагина.</param>
    /// <param name="availableCategories">Список категорий, реально доступных в текущем проекте.</param>
    Task<string> CompleteAsync(string userPrompt, string availableCategories, CancellationToken ct);
}

/// <summary>Ошибка связи с LLM, понятная пользователю.</summary>
public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
    public LlmException(string message, Exception inner) : base(message, inner) { }
}