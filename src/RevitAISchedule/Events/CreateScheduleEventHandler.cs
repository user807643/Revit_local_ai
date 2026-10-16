using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAISchedule.Model;
using RevitAISchedule.RevitApi;

namespace RevitAISchedule.Events;

/// <summary>
/// Обработчик Revit для создания ведомости.
///
/// Схема взаимодействия (обязательный паттерн Revit API):
///   UI-поток WPF  --(ExternalEvent.Raise)--&gt;  Revit main thread
///   Revit main thread: IExternalEventHandler.Execute(UIApplication) --&gt; Transaction
///
/// LLM никогда не трогает Revit API и никогда не открывает Transaction.
/// </summary>
public sealed class CreateScheduleEventHandler : IExternalEventHandler
{
    private ScheduleRequest? _pendingRequest;
    private Action<ScheduleOutcome>? _onCompleted;

    /// <summary>Задать данные и поднять событие.</summary>
    public void Enqueue(ScheduleRequest request, Action<ScheduleOutcome> onCompleted)
    {
        _pendingRequest = request;
        _onCompleted = onCompleted;
    }

    /// <summary>Вызывается из Revit main thread.</summary>
    public void Execute(UIApplication app)
    {
        ScheduleRequest? request = _pendingRequest;
        Action<ScheduleOutcome>? callback = _onCompleted;

        _pendingRequest = null;
        _onCompleted = null;

        if (request is null || callback is null)
            return;

        ScheduleOutcome outcome;

        try
        {
            UIDocument uiDoc = app.ActiveUIDocument;
            if (uiDoc is null)
            {
                callback(ScheduleOutcome.Fail("Нет открытого документа. Откройте проект и повторите."));
                return;
            }

            Document doc = uiDoc.Document;

            // Валидация категории ДО открытия транзакции — так дешевле и понятнее.
            if (!CategoryResolver.TryResolve(doc, request.Category, out _, out string catName))
            {
                callback(ScheduleOutcome.Fail(
                    $"Категория '{request.Category}' не найдена в текущем проекте."));
                return;
            }

            // ---- Начало транзакции: все изменения Revit только здесь ----
            using (var transaction = new Transaction(doc, "Revit AI: создание ведомости"))
            {
                transaction.Start();

                try
                {
                    outcome = ScheduleFactory.Create(uiDoc, request);
                    transaction.Commit();
                }
                catch (ScheduleValidationException vex)
                {
                    transaction.RollBack();
                    outcome = ScheduleOutcome.Fail(vex.Message);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    transaction.RollBack();
                    outcome = ScheduleOutcome.Fail("Операция была отменена в Revit.");
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException aex)
                {
                    transaction.RollBack();
                    outcome = ScheduleOutcome.Fail("Revit отклонил операцию: " + aex.Message);
                }
                catch (System.Exception ex)
                {
                    try { transaction.RollBack(); } catch { /* уже откатилась */ }
                    outcome = ScheduleOutcome.Fail("Ошибка при создании ведомости: " + ex.Message);
                }
            }
            // ---- Конец транзакции ----

            if (outcome.Success)
            {
                // Показываем созданную ведомость пользователю.
                try
                {
                    var created = doc.GetElement(new ElementId(outcome.ScheduleId));
                    if (created is not null)
                        uiDoc.ShowElements(created.Id);
                }
                catch
                {
                    // Не критично: ведомость создана, просто не подсветили её.
                }
            }
        }
        catch (System.Exception ex)
        {
            outcome = ScheduleOutcome.Fail("Не удалось обработать запрос: " + ex.Message);
        }

        callback(outcome);
    }

    public string GetName() => "Revit AI: создание ведомости";
}