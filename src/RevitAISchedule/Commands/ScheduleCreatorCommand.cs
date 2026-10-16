using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAISchedule.Events;
using RevitAISchedule.Ui;

namespace RevitAISchedule.Commands;

/// <summary>
/// Точка входа в плагин: команда Revit, открывающая окно плагина.
/// Revit 2026 запускает .addin, указывающий на этот класс.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ScheduleCreatorCommand : IExternalCommand
{
    private static ExternalEvent? _externalEvent;
    private static CreateScheduleEventHandler? _eventHandler;
    private static MainWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDocument = commandData.Application.ActiveUIDocument;

        if (uiDocument is null)
        {
            message = "Нет открытого документа. Откройте проект и повторите команду.";
            return Result.Failed;
        }

        try
        {
            // Окно уже открыто — просто выводим на передний план.
            if (_window is not null && _window.IsLoaded)
            {
                _window.Activate();
                return Result.Succeeded;
            }

            // Один обработчик + одно событие на всё время работы сессии Revit.
            _eventHandler ??= new CreateScheduleEventHandler();
            _externalEvent ??= ExternalEvent.Create(_eventHandler);

            _window = new MainWindow(uiDocument, _externalEvent, _eventHandler);

            // Revit 2026: у UIDocument больше нет ShowForm (метод удалён из API).
            // Показываем окно как обычное дочернее окно Revit.
            var helper = new System.Windows.Interop.WindowInteropHelper(_window);
            helper.Owner = System.IntPtr.Zero;

            _window.Show();
            _window.Activate();

            return Result.Succeeded;
        }
        catch (System.Exception ex)
        {
            message = "Не удалось открыть окно плагина: " + ex.Message;
            return Result.Failed;
        }
    }
}