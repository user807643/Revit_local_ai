using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAISchedule.Llm;

/// <summary>Итог проверки подключения: удалось ли, что именно и какие позиции нашлись.</summary>
public sealed class ConnectionTestResult
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Найденные на сервере модели.</summary>
    public IList<string> Items { get; init; } = new List<string>();
}

/// <summary>
/// Проверка подключения к локальному серверу ИИ из окна настроек.
/// Ничего не меняет ни в настройках, ни в состоянии Revit — только читает.
///
/// Реализовано по образцу ConnectionTestService из проекта RevitMcpChat.
/// </summary>
public static class ConnectionTestService
{
    /// <summary>
    /// Таймаут пробного запроса. LM Studio и Ollama выгружают модель из памяти
    /// при простое, поэтому первый запрос включает её загрузку с диска — для
    /// 4B-модели это минуты. Прежние 20 секунд давали ложный отказ
    /// «модель не отвечает» на полностью рабочей модели.
    /// </summary>
    private const int ModelProbeTimeoutSeconds = 300;

    /// <summary>Список моделей отдаётся сервером мгновенно, загрузки модели не требует.</summary>
    private const int ModelsListTimeoutSeconds = 30;

    /// <summary>
    /// Только список моделей без пробного запроса completions — быстрый способ
    /// посмотреть, что вообще запущено на локальном сервере.
    /// </summary>
    public static async Task<ConnectionTestResult> FetchModelsAsync(
        AiSettings settings,
        CancellationToken cancellationToken)
    {
        string? modelsUrl = ModelEndpoints.TryBuildModelsEndpoint(settings.Endpoint);
        if (modelsUrl is null)
        {
            return new ConnectionTestResult
            {
                Ok = false,
                Message = "Не удалось построить адрес списка моделей из: " + settings.Endpoint
            };
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(ModelsListTimeoutSeconds) };
            using HttpResponseMessage response = await http.GetAsync(modelsUrl, cancellationToken)
                                                                  .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionTestResult
                {
                    Ok = false,
                    Message = "Сервер не отдал список моделей: HTTP " + (int)response.StatusCode +
                              " по адресу " + modelsUrl + ". " + Truncate(body, 300)
                };
            }

            List<string> models = ParseModelIds(body).ToList();
            return new ConnectionTestResult
            {
                Ok = models.Count > 0,
                Message = models.Count == 0
                    ? "Сервер ответил, но список моделей пуст: " + modelsUrl
                    : "Модели на сервере: " + string.Join(", ", models),
                Items = models
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult
            {
                Ok = false,
                Message = "Не удалось обратиться к " + modelsUrl + ": " + ex.Message +
                          ". Проверьте, запущен ли локальный сервер ИИ (LM Studio)."
            };
        }
    }

    /// <summary>
    /// Проверяет локальную модель: сначала спрашивает у сервера список моделей
    /// (GET /v1/models), затем делает минимальный запрос к completions.
    /// </summary>
    public static async Task<ConnectionTestResult> TestModelAsync(
        AiSettings settings,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(ModelProbeTimeoutSeconds) };

        var notes = new List<string>();
        var models = new List<string>();

        string? modelsUrl = ModelEndpoints.TryBuildModelsEndpoint(settings.Endpoint);
        if (modelsUrl is null)
        {
            notes.Add("Адрес модели не разобран: проверьте endpoint.");
        }
        else
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(modelsUrl, cancellationToken)
                                                                  .ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    models.AddRange(ParseModelIds(body));
                    notes.Add(models.Count == 0
                        ? "Список моделей пуст."
                        : "Сервер сообщает о моделях: " + models.Count + ".");
                }
                else
                {
                    notes.Add("Список моделей недоступен (HTTP " + (int)response.StatusCode + ").");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Некоторые серверы не отдают /models — это не повод считать проверку провалом.
                notes.Add("Список моделей недоступен (" + ex.Message + ").");
            }
        }

        string? chatUrl = ModelEndpoints.TryBuildChatEndpoint(settings.Endpoint);
        if (chatUrl is null)
        {
            return new ConnectionTestResult
            {
                Ok = false,
                Message = ModelEndpoints.DescribeInvalidEndpoint(settings.Endpoint)
            };
        }

        string answer;
        try
        {
            var payload = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "user", content = "Ответь одним словом: готово" }
                },
                stream = false,
                temperature = 0,
                max_tokens = 32
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken)
                                                               .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionTestResult
                {
                    Ok = false,
                    Message = "Модель вернула HTTP " + (int)response.StatusCode +
                              (string.IsNullOrEmpty(response.ReasonPhrase) ? string.Empty : " (" + response.ReasonPhrase + ")") +
                              ". " + Truncate(body, 400) + " " + string.Join(" ", notes),
                    Items = models
                };
            }

            // Сервер может отдать {"error": ...} с кодом 200 — это тоже ошибка.
            if (ModelResponse.TryReadServerError(body, out string? serverError))
            {
                return new ConnectionTestResult
                {
                    Ok = false,
                    Message = "Модель вернула ошибку: " + serverError + " " + string.Join(" ", notes),
                    Items = models
                };
            }

            answer = ReadAnswer(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult
            {
                Ok = false,
                Message = "Не удалось обратиться к модели по адресу " + chatUrl + ": " +
                          ex.Message + " " + string.Join(" ", notes),
                Items = models
            };
        }

        return new ConnectionTestResult
        {
            Ok = true,
            Message = "Модель «" + settings.Model + "» на " + chatUrl + " отвечает" +
                      (string.IsNullOrEmpty(answer) ? string.Empty : ": «" + Truncate(answer, 120) + "»") +
                      ". " + string.Join(" ", notes),
            Items = models
        };
    }

    private static IEnumerable<string> ParseModelIds(string body)
    {
        var result = new List<string>();
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            if (root?["data"] is not JsonArray data) return result;

            foreach (JsonNode? item in data)
            {
                if (item?["id"] is JsonValue value && value.TryGetValue(out string? id)
                    && !string.IsNullOrWhiteSpace(id))
                {
                    result.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // Сервер ответил неожиданным форматом — просто ничего не перечислим.
        }

        return result;
    }

    private static string ReadAnswer(string body)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            if (root?["choices"]?[0]?["message"]?["content"] is JsonValue value
                && value.TryGetValue(out string? content))
            {
                return (content ?? string.Empty).Trim();
            }
        }
        catch (JsonException)
        {
            // Ниже вернём первые символы ответа как есть.
        }

        return Truncate(body, 120);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return string.Concat(text.AsSpan(0, maxLength), "…");
    }
}