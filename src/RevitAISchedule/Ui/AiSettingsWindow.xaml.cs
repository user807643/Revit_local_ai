using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitAISchedule.Llm;

namespace RevitAISchedule.Ui;

/// <summary>
/// Окно настройки подключения к локальной ИИ-модели.
/// Пишет один файл %APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\config.json.
/// Реализовано по образцу ModelSettingsWindow из проекта RevitMcpChat.
/// </summary>
public partial class AiSettingsWindow : Window
{
    private CancellationTokenSource? _running;
    private bool _ready;

    public AiSettingsWindow()
    {
        InitializeComponent();

        ConfigPathText.Text = AiSettings.ConfigPath;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AiSettings settings = AiSettings.Load(out string note);
        LoadInto(settings);
        _ready = true;

        StatusText.Text = string.IsNullOrWhiteSpace(note) ? "Готово к настройке." : note;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => CancelRunning();

    // ---------------------------------------------------------------- загрузка и сохранение

    private void LoadInto(AiSettings settings)
    {
        bool wasReady = _ready;
        _ready = false;
        try
        {
            EndpointBox.Text = settings.Endpoint;
            TimeoutBox.Text = settings.RequestTimeoutSeconds.ToString();
            MaxTokensBox.Text = settings.MaxTokens.ToString();
            TemperatureBox.Text = settings.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);

            ModelBox.Items.Clear();
            ModelBox.Items.Add(settings.Model);
            ModelBox.Text = settings.Model;

            ModelsHintText.Text = string.Empty;
        }
        finally
        {
            _ready = wasReady;
        }
    }

    /// <summary>Собирает настройки из полей окна.</summary>
    private AiSettings ReadFromForm() => new()
    {
        Endpoint = EndpointBox.Text.Trim(),
        Model = ModelBox.Text.Trim(),
        RequestTimeoutSeconds = ParseInt(TimeoutBox.Text, 300),
        MaxTokens = ParseInt(MaxTokensBox.Text, 700),
        Temperature = ParseDouble(TemperatureBox.Text, 0.0)
    };

    private void OnSave(object sender, RoutedEventArgs e)
    {
        AiSettings settings = ReadFromForm();

        IReadOnlyList<string> problems = settings.Validate();
        if (problems.Count > 0)
        {
            ShowProblems(problems);
            return;
        }

        try
        {
            AiSettings.Save(settings);
            SetStatus("Сохранено: " + AiSettings.ConfigPath, Brushes.DarkGreen);

            MessageBox.Show(
                this,
                "Настройки сохранены в:\n" + AiSettings.ConfigPath +
                "\n\nОни применятся при следующем создании ведомости.",
                "Настройка модели",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка сохранения: " + ex.Message, Brushes.Firebrick);
            MessageBox.Show(
                this,
                "Не удалось сохранить настройки: " + ex.Message,
                "Настройка модели",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        LoadInto(AiSettings.CreateDefault());
        SetStatus("Возвращены значения по умолчанию. Нажмите «Сохранить», чтобы применить их.",
                  Brushes.DimGray);
    }

    // ---------------------------------------------------------------- проверки подключения

    private async void OnTestModel(object sender, RoutedEventArgs e)
    {
        AiSettings settings = ReadFromForm();
        IReadOnlyList<string> problems = settings.Validate();
        if (problems.Count > 0)
        {
            ShowProblems(problems);
            return;
        }

        await RunCheckAsync(
            "Проверяю локальную модель…",
            token => ConnectionTestService.TestModelAsync(settings, token),
            result => ApplyModelList(result.Items));
    }

    private async void OnFetchModels(object sender, RoutedEventArgs e)
    {
        AiSettings settings = ReadFromForm();
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            ShowProblems(new[] { "Укажите адрес модели." });
            return;
        }

        await RunCheckAsync(
            "Запрашиваю список моделей…",
            token => ConnectionTestService.FetchModelsAsync(settings, token),
            result =>
            {
                ApplyModelList(result.Items);
                if (result.Items.Count > 0)
                {
                    SetStatus(
                        "Получено моделей: " + result.Items.Count + ". Выберите нужную в поле «Модель».",
                        result.Ok ? Brushes.DarkGreen : Brushes.Firebrick);
                }
            });
    }

    /// <summary>
    /// Общая обвязка проверки: блокирует кнопки на время запроса, отменяет его при
    /// закрытии окна и показывает итог в строке состояния.
    /// </summary>
    private async Task RunCheckAsync(
        string progress,
        Func<CancellationToken, Task<ConnectionTestResult>> check,
        Action<ConnectionTestResult>? onSuccess)
    {
        CancelRunning();

        var tokenSource = new CancellationTokenSource();
        _running = tokenSource;

        SetBusy(true);
        SetStatus(progress, Brushes.DimGray);

        try
        {
            ConnectionTestResult result = await check(tokenSource.Token);

            SetBusy(false);
            SetStatus(result.Message, result.Ok ? Brushes.DarkGreen : Brushes.Firebrick);

            if (result.Ok && onSuccess is not null) onSuccess(result);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            SetStatus("Проверка отменена.", Brushes.DimGray);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            SetStatus("Ошибка проверки: " + ex.Message, Brushes.Firebrick);
        }
        finally
        {
            _running?.Dispose();
            _running = null;
        }
    }

    private void CancelRunning()
    {
        try
        {
            _running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Проверка уже завершилась.
        }
    }

    private void SetBusy(bool busy)
    {
        TestModelButton.IsEnabled = !busy;
        FetchModelsButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
    }

    private void SetStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusText.Foreground = color;
    }

    /// <summary>Подставляет найденные модели в выпадающий список, не затирая текущий выбор.</summary>
    private void ApplyModelList(IList<string> models)
    {
        if (models.Count == 0) return;

        bool wasReady = _ready;
        _ready = false;
        try
        {
            string current = ModelBox.Text.Trim();

            ModelBox.Items.Clear();
            foreach (string model in models) ModelBox.Items.Add(model);

            if (current.Length > 0 && !models.Contains(current, StringComparer.OrdinalIgnoreCase))
                ModelBox.Items.Add(current);

            ModelBox.Text = current.Length > 0 ? current : models[0];
            ModelsHintText.Text = "Сервер сообщает о моделях: " + string.Join(", ", models);
        }
        finally
        {
            _ready = wasReady;
        }
    }

    private void ShowProblems(IReadOnlyList<string> problems)
    {
        SetStatus("Проверьте настройки: " + problems[0], Brushes.Firebrick);
        MessageBox.Show(
            this,
            string.Join("\n", problems),
            "Проверка настроек",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // ---------------------------------------------------------------- вспомогательное

    private void OnEndpointChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        string? modelsUrl = ModelEndpoints.TryBuildModelsEndpoint(EndpointBox.Text);
        string? chatUrl = ModelEndpoints.TryBuildChatEndpoint(EndpointBox.Text);

        if (chatUrl is null)
        {
            ModelsHintText.Text = "Адрес не распознан: укажите http://127.0.0.1:1234/v1 "
                               + "или http://127.0.0.1:1234/v1/chat/completions.";
            return;
        }

        ModelsHintText.Text = "Модели: " + modelsUrl + " · чат: " + chatUrl;
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse((text ?? string.Empty).Trim(), out int value) ? value : fallback;

    private static double ParseDouble(string? text, double fallback)
        => double.TryParse((text ?? string.Empty).Trim(),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture,
                           out double value)
            ? value
            : fallback;
}
