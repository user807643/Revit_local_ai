using Autodesk.Revit.UI;

namespace RevitAISchedule.Commands;

/// <summary>
/// Размещает кнопки плагина на ленте Revit 2026.
///
/// Кнопки создаются на вкладке «Add-Ins» (в русской ленте — «Надстройки»)
/// в панели «Revit AI Schedule»:
///   - «Revit AI Schedule» — запуск плагина;
///   - «Настройки ИИ»      — адрес и имя локальной модели.
///
/// Путь к сборке берётся из Location текущей сборки: Revit загрузил её оттуда,
/// поэтому путь корректен независимо от того, куда установлены файлы.
/// </summary>
public sealed class ScheduleRibbonApplication : IExternalApplication
{
    private const string PanelName = "Revit ИИ";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            RibbonPanel panel = application.CreateRibbonPanel(Tab.AddIns, PanelName);
            string assemblyPath = typeof(ScheduleRibbonApplication).Assembly.Location;

            // --- Запуск плагина ---
            var openData = new PushButtonData(
                "RevitAIScheduleButton",
                "Revit AI\nSchedule",
                assemblyPath,
                typeof(ScheduleCreatorCommand).FullName!)
            {
                ToolTip = "Создать ведомость (schedule) по описанию на естественном языке. "
                        + "Запрос обрабатывается локальной LLM.",
                LongDescription = "Открывает окно Revit AI Schedule.\n"
                                + "Введите, например: «Создай ведомость дверей с параметрами "
                                + "Марка, Тип, Ширина, Высота» и нажмите «Создать»."
            };
            panel.AddItem(openData);

            // --- Настройки локальной ИИ ---
            var settingsData = new PushButtonData(
                "RevitAIScheduleSettingsButton",
                "Настройки\nИИ-модели",
                assemblyPath,
                typeof(ShowAiSettingsCommand).FullName!)
            {
                ToolTip = "Настройка локальной ИИ-модели: адрес, имя модели, параметры запроса, "
                        + "проверка подключения и список моделей.",
                LongDescription = "Открывает окно настройки локальной ИИ-модели (LM Studio или любой "
                                + "сервер с OpenAI-совместимым API).\n"
                                + "Позволяет проверить подключение и получить список доступных моделей.\n"
                                + "Настройки сохраняются в "
                                + "%APPDATA%\\Autodesk\\Revit\\Addins\\2026\\RevitAISchedule\\config.json."
            };
            panel.AddItem(settingsData);

            return Result.Succeeded;
        }
        catch
        {
            // Не удалось разместить кнопки — это не повод ломать запуск Revit.
            // Плагин остаётся доступен через «Внешние инструменты».
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
