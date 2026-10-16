using Autodesk.Revit.UI;

namespace RevitAISchedule.Commands;

/// <summary>
/// Служебное приложение для автоматической проверки (SelfTest).
///
/// Обычно плагину это не нужно: SelfTest запускается кнопкой
/// «Внешние инструменты -&gt; Revit AI Schedule SelfTest».
/// Этот класс даёт возможность прогнать ту же проверку без участия пользователя.
///
/// Запуск автопроверки: Revit при старте ищет файл-флаг
/// RevitAISchedule_AutoSelfTest.txt в папках TEMP; первая строка файла —
/// путь к .rvt-проекту (пустая строка — создать новый проект из шаблона Revit).
/// Такой способ не зависит от того, как процесс наследует переменные окружения.
/// </summary>
public sealed class ScheduleSelfTestApplication : IExternalApplication
{
    private const string FlagFileName = "RevitAISchedule_AutoSelfTest.txt";

    private bool _armed;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            if (ReadAutoRunRequest(out string projectPath))
            {
                Log("SelfTest: автозапуск включён, проект = '" + projectPath + "'");
                _armed = true;
                application.Idling += OnIdling;
            }
            else
            {
                Log("SelfTest: флага автозапуска не найдено. Проверены: "
                    + string.Join("; ", CandidateFlagFiles()));
            }
        }
        catch (System.Exception ex)
        {
            // Служебная функция: любой сбой не должен ломать запуск Revit.
            _armed = false;
            Log("SelfTest: ошибка автозапуска: " + ex.Message);
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try { application.Idling -= OnIdling; } catch { }
        return Result.Succeeded;
    }

    /// <summary>Возможные пути файла-флага (Revit может использовать другой TEMP).</summary>
    private static List<string> CandidateFlagFiles()
    {
        var result = new List<string>();

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string path = System.IO.Path.Combine(dir!, FlagFileName);
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                result.Add(path);
        }

        Add(System.IO.Path.GetTempPath());
        Add(Environment.GetEnvironmentVariable("TEMP"));
        Add(Environment.GetEnvironmentVariable("TMP"));

        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            Add(System.IO.Path.Combine(localAppData!, "Temp"));

        return result;
    }

    /// <summary>Читает файл-флаг. Возвращает true, если нужно запустить SelfTest.</summary>
    private static bool ReadAutoRunRequest(out string projectPath)
    {
        projectPath = string.Empty;

        foreach (string candidate in CandidateFlagFiles())
        {
            try
            {
                if (!System.IO.File.Exists(candidate)) continue;

                string text = System.IO.File.ReadAllText(candidate) ?? string.Empty;

                // Одноразовый запуск: флаг удаляем сразу.
                try { System.IO.File.Delete(candidate); } catch { }

                projectPath = text.Trim().Trim('"');
                AutoRunProjectPath = projectPath;
                return true;
            }
            catch
            {
                // Следующий кандидат.
            }
        }

        return false;
    }

    /// <summary>Путь к проекту из файла-флага (пусто — создать новый из шаблона).</summary>
    private static string AutoRunProjectPath { get; set; } = string.Empty;

    private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (!_armed) return;
        _armed = false; // выполнить один раз

        try
        {
            if (sender is not UIApplication uiApp)
            {
                Log("SelfTest: Idling без UIApplication");
                return;
            }

            Log("SelfTest: запуск проверки...");
            ScheduleSelfTestCommand.Run(uiApp, AutoRunProjectPath);
        }
        catch (System.Exception ex)
        {
            Log("SelfTest: ошибка выполнения: " + ex);
        }
    }

    private static void Log(string text)
    {
        try
        {
            System.IO.File.AppendAllText(
                SelfTestReport.Path,
                "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch { /* отчёт не критичен */ }
    }
}