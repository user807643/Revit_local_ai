using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAISchedule.Commands;

/// <summary>
/// Открывает окно «Настройка локальной ИИ-модели»: адрес и имя модели,
/// параметры запроса и проверки подключения.
/// Кнопка размещается на ленте рядом с кнопкой запуска плагина.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ShowAiSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        AiSettingsHost.Show();
        return Result.Succeeded;
    }
}