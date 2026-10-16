using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAISchedule.Commands;
using RevitAISchedule.Events;
using RevitAISchedule.Llm;
using RevitAISchedule.Model;
using RevitAISchedule.RevitApi;

namespace RevitAISchedule.Ui;

/// <summary>
/// Простое окно плагина. Никакой Revit API вне обработчика ExternalEvent.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UIDocument _uiDocument;
    private readonly ExternalEvent _externalEvent;
    private readonly CreateScheduleEventHandler _eventHandler;
    private readonly LmStudioProvider _llm;
    private bool _busy;

    public MainWindow(UIDocument uiDocument, ExternalEvent externalEvent, CreateScheduleEventHandler eventHandler)
    {
        _uiDocument = uiDocument;
        _externalEvent = externalEvent;
        _eventHandler = eventHandler;
        _llm = new LmStudioProvider();

        InitializeComponent();

        ResultBox.Text = "Опишите ведомость и нажмите «Создать».";
        StatusLabel.Text = $"LLM: {_llm.DisplayName}";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Clear();
        ResultBox.Clear();
        StatusLabel.Text = "Готово.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AiSettingsHost.Show();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(
                this,
                "Не удалось открыть окно настроек ИИ: " + ex.Message,
                "Revit AI Schedule",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string prompt = PromptBox.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            ShowError("Введите запрос, например: «Создай ведомость дверей с параметрами Марка, Тип, Ширина, Высота».");
            return;
        }

        SetBusy(true);
        ResultBox.Clear();
        StatusLabel.Text = $"Обращение к локальной LLM ({_llm.DisplayName})...";

        ScheduleRequest request;
        try
        {
            // Revit API читается с главного потока Revit. Здесь только чтение справочных данных,
            // без изменений модели — это допустимо и без транзакции.
            Document doc = _uiDocument.Document;
            string categories = CategoryResolver.BuildCategoryListForPrompt(doc);

            string raw = await _llm.CompleteAsync(prompt, categories, CancellationToken.None)
                                   .ConfigureAwait(true);

            StatusLabel.Text = "Проверка ответа LLM...";
            request = LlmResponseParser.Parse(raw);
        }
        catch (LlmException ex)
        {
            SetBusy(false);
            ShowError(ex.Message);
            return;
        }
        catch (System.Exception ex)
        {
            SetBusy(false);
            ShowError("Ошибка связи с LLM: " + ex.Message);
            return;
        }

        StatusLabel.Text = "Создание ведомости в Revit...";

        // Дальше — только Revit API через ExternalEvent + Transaction.
        _eventHandler.Enqueue(request, outcome =>
        {
            Dispatcher.Invoke(() =>
            {
                SetBusy(false);
                if (outcome.Success)
                {
                    ResultBox.Foreground = System.Windows.Media.Brushes.DarkGreen;
                    ResultBox.Text = outcome.Message;
                    StatusLabel.Text = $"Готово. Категория: {outcome.CategoryName}.";
                }
                else
                {
                    ResultBox.Foreground = System.Windows.Media.Brushes.Firebrick;
                    ResultBox.Text = outcome.Message;
                    StatusLabel.Text = "Ведомость не создана.";
                }
            });
        });

        _externalEvent.Raise();
    }

    private void ShowError(string message)
    {
        ResultBox.Foreground = System.Windows.Media.Brushes.Firebrick;
        ResultBox.Text = message;
        StatusLabel.Text = "Ошибка.";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CreateButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        Progress.Visibility = busy
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        _llm.Dispose();
        base.OnClosed(e);
    }
}