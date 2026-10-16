using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RevitAISchedule.Llm;

/// <summary>
/// Настройки подключения к локальной ИИ-модели.
/// Хранятся в %APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\config.json и читаются заново при каждом
/// запуске окна плагина, поэтому изменения применяются без перезапуска Revit.
///
/// Реализовано по образцу окна настроек из проекта RevitMcpChat.
/// </summary>
public sealed class AiSettings
{
    /// <summary>Базовый адрес сервера модели, совместимого с OpenAI API.</summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Имя модели, как её называет сервер.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Таймаут обращения к модели, секунд.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Значение temperature, передаваемое в запрос.</summary>
    public double Temperature { get; set; } = 0.0;

    /// <summary>Максимум токенов в ответе.</summary>
    public int MaxTokens { get; set; } = 4096;

    public const string DefaultEndpoint = "http://127.0.0.1:1234/v1";

    /// <summary>Модель по умолчанию: лёгкая и без режима размышлений (reasoning).</summary>
    /// <remarks>
    /// Выбрана сознательно: reasoning-модели (qwen3.5, ministral-reasoning) тратят
    /// тысячи токенов на размышления и часто не успевают выдать JSON — ответ
    /// приходит пустым. Для простой задачи «запрос -&gt; JSON» reasoning не нужен.
    /// </remarks>
    public const string DefaultModel = "qwen3-vl-2b-instruct";

    /// <summary>
    /// Формат config.json. Кириллицу пишем буквально, а не как \uXXXX:
    /// System.Text.Json по умолчанию экранирует всё не-ASCII, и в «Блокноте»
    /// файл становится нечитаемым.
    /// </summary>
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Папка настроек — рядом с файлами плагина:
    /// %APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule
    ///
    /// Так конфиг лежит рядом с DLL, его видно в проводнике, и удаление плагина
    /// удаляет вместе с ним настройки — не остаётся «осиротевших» файлов.
    /// Год Revit (2026) вычисляется из папки, куда Revit реально загрузил плагин,
    /// поэтому путь останется верным и на других версиях.
    /// </summary>
    public static string ConfigDirectory => ResolveConfigDirectory();

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");


    /// <summary>
    /// Ищем папку плагина рядом с загруженной сборкой, и только если она недоступна —
    /// строим стандартный путь %APPDATA%\Autodesk\Revit\Addins\&lt;год&gt;\RevitAISchedule.
    /// </summary>
    private static string ResolveConfigDirectory()
    {
        // 1. Папка, из которой Revit загрузил плагин: надёжнее всего.
        try
        {
            string assemblyPath = typeof(AiSettings).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(assemblyPath))
            {
                string? dir = Path.GetDirectoryName(assemblyPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir!;
            }
        }
        catch
        {
            // Location бывает пустым в редких сценариях загрузки — переходим к запасному пути.
        }

        // 2. Запасной путь по умолчанию.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2026", "RevitAISchedule");
    }

    /// <summary>Настройки по умолчанию (LM Studio).</summary>
    public static AiSettings CreateDefault() => new();

    /// <summary>
    /// Читает настройки. При отсутствии файла создаёт его со значениями по умолчанию.
    /// out note — понятное пользователю сообщение о том, что произошло.
    /// </summary>
    public static AiSettings Load(out string note) => LoadFrom(ConfigPath, createIfMissing: true, out note);

    /// <summary>Читает настройки из указанного файла.</summary>
    public static AiSettings LoadFrom(string path, bool createIfMissing, out string note)
    {
        note = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                if (!createIfMissing)
                {
                    note = "Файл настроек не найден, используются значения по умолчанию: " + path;
                    return CreateDefault();
                }

                AiSettings template = CreateDefault();
                SaveTo(path, template);
                note = "Создан файл настроек по умолчанию: " + path;
                return template;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<AiSettings>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config is null)
            {
                note = "Файл настроек пуст, используются значения по умолчанию: " + path;
                return CreateDefault();
            }

            // Подстраховка от битых значений: молча чиним и продолжаем работу.
            if (string.IsNullOrWhiteSpace(config.Endpoint))
                config.Endpoint = DefaultEndpoint;
            if (string.IsNullOrWhiteSpace(config.Model))
                config.Model = DefaultModel;
            if (config.RequestTimeoutSeconds <= 0)
                config.RequestTimeoutSeconds = 300;
            if (config.MaxTokens <= 0)
                config.MaxTokens = 4096;
            if (config.Temperature < 0)
                config.Temperature = 0;

            return config;
        }
        catch (Exception ex)
        {
            note = "Не удалось прочитать " + path + " (" + ex.Message +
                   "). Используются значения по умолчанию.";
            return CreateDefault();
        }
    }

    /// <summary>Читает настройки без создания файла — безопасный вариант для фоновых операций.</summary>
    public static AiSettings LoadOrDefault() => Load(out _);

    public static void Save(AiSettings config) => SaveTo(ConfigPath, config);

    /// <summary>Записывает настройки в указанный файл (UTF-8 без BOM).</summary>
    public static void SaveTo(string path, AiSettings config)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(config, SaveOptions), new UTF8Encoding(false));
    }

    /// <summary>Глубокая копия — чтобы окно правило и отменяло без побочных эффектов.</summary>
    public AiSettings Clone() => new()
    {
        Endpoint = Endpoint,
        Model = Model,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        Temperature = Temperature,
        MaxTokens = MaxTokens
    };

    /// <summary>
    /// Проверяет настройки перед сохранением. Пустой список означает, что сохранять можно.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            problems.Add("Не указан адрес локальной модели (endpoint).");
        }
        else if (ModelEndpoints.TryBuildChatEndpoint(Endpoint) is null)
        {
            problems.Add(
                "Адрес модели должен быть http(s)-адресом, например " +
                "http://127.0.0.1:1234/v1 или http://127.0.0.1:1234/v1/chat/completions");
        }

        if (string.IsNullOrWhiteSpace(Model))
            problems.Add("Не указано имя модели.");

        if (RequestTimeoutSeconds <= 0)
            problems.Add("Таймаут обращения к модели должен быть больше нуля.");

        if (MaxTokens <= 0)
            problems.Add("Лимит токенов ответа должен быть больше нуля.");

        return problems;
    }
}