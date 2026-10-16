using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAISchedule.RevitApi;

namespace RevitAISchedule.Commands;

/// <summary>
/// Диагностика связки Revit API 2026 -&gt; ViewSchedule без обращения к LLM.
/// Позволяет убедиться, что ведомость реально создаётся, даже если LM Studio не запущена.
/// Отчёт: %TEMP%\RevitAISchedule_SelfTest.txt
/// </summary>
public sealed class ScheduleSelfTestCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;
        if (Run(uiApp, null))
        {
            message = "SelfTest: ведомость создана. Отчёт: " + SelfTestReport.Path;
            return Result.Succeeded;
        }

        message = "SelfTest: обнаружена ошибка. Отчёт: " + SelfTestReport.Path;
        return Result.Failed;
    }

    /// <summary>Выполняет проверку. Возвращает true, если ведомость создана.</summary>
    public static bool Run(UIApplication uiApp) => Run(uiApp, null);

    /// <summary>Выполняет проверку, при необходимости открыв указанный проект.</summary>
    public static bool Run(UIApplication uiApp, string? projectPath)
    {
        (bool created, string _) = RunAsync(uiApp, projectPath).GetAwaiter().GetResult();
        return created;
    }

    private static async Task<(bool Created, string Report)> RunAsync(UIApplication uiApp, string? projectPath)
    {
        var log = new SelfTestReport();
        bool created = false;

        try
        {
            UIDocument uiDoc = uiApp.ActiveUIDocument;

            // Автозапуск: открыть проект из флага или создать новый из шаблона Revit.
            if (uiDoc is null && !string.IsNullOrWhiteSpace(projectPath))
            {
                if (!TryOpenProject(uiApp, projectPath, log, out uiDoc))
                {
                    log.Line($"ОШИБКА: не удалось открыть проект '{projectPath}'.");
                    log.Save();
                    return (false, log.ToString());
                }
                log.Line($"Автозапуск: открыт проект {uiDoc.Document.Title}");
                log.Line();
            }
            else if (uiDoc is null)
            {
                if (!TryCreateProject(uiApp, out uiDoc))
                {
                    log.Line("ОШИБКА: нет открытого документа, и не удалось создать новый проект.");
                    log.Line("Откройте проект вручную и запустите SelfTest ещё раз.");
                    log.Save();
                    return (false, log.ToString());
                }
                log.Line("Автозапуск: создан новый проект из шаблона Revit.");
                log.Line();
            }

            Document doc = uiDoc.Document;
            log.Line($"Revit AI Schedule SelfTest — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.Line($"Revit: {uiApp.Application.VersionName} / build {uiApp.Application.VersionBuild}");
            log.Line($"Документ: {doc.Title}   семейство: {doc.IsFamilyDocument}");
            log.Line();

            // --- 1. Категории, доступные для ведомостей ---
            ICollection<ElementId> validCats = ViewSchedule.GetValidCategoriesForSchedule();
            log.Line($"1. Категорий, доступных для ведомости: {validCats.Count}");

            bool doorsOk = CategoryResolver.TryResolve(doc, "Doors", out ElementId doorsId, out string doorsName);
            log.Line($"   резолв \"Doors\"   : {(doorsOk ? $"OK -> Id={doorsId.Value} ({doorsName})" : "НЕ НАЙДЕНА")}");
            bool ostOk = CategoryResolver.TryResolve(doc, "OST_Doors", out _, out string ostName);
            log.Line($"   резолв \"OST_Doors\": {(ostOk ? $"OK ({ostName})" : "НЕ НАЙДЕНА")}");

            if (!doorsOk)
            {
                log.Save();
                return (false, log.ToString());
            }
            log.Line();

            log.Line($"   язык Revit        : {doc.Application.Language}");

            // --- 1a. Резолв категорий при разных вариантах написания ---
            log.Line("1a. РЕЗОЛВ КАТЕГОРИЙ (встроенное имя / без префикса / с пробелами / метка Revit)");
            CheckCategoryResolution(doc, log);

            log.Line("1b. РУССКИЕ МЕТКИ КАТЕГОРИЙ В ЭТОМ ИНТЕРФЕЙСЕ REVIT");
            DumpRussianLabels(doc, log);
            // --- 2. Реальные поля категории ---
            if (doorsOk)
            {
                using var t = new Transaction(doc, "SelfTest: чтение полей");
                t.Start();
                try
                {
                    ViewSchedule probe = ViewSchedule.CreateSchedule(doc, doorsId);
                    List<ScheduleFieldInfo> fields = ScheduleFieldCatalog.GetFields(probe, doc);

                    log.Line($"2. Полей у категории «{doorsName}»: {fields.Count}");
                    foreach (ScheduleFieldInfo f in fields.Take(45))
                    {
                        log.Line($"   - \"{f.DisplayName}\"  builtin={(f.BuiltIn?.ToString() ?? "-")}");
                        log.Line($"       aliases: {string.Join(" | ", f.Aliases.Take(6))}");
                    }
                    if (fields.Count > 45) log.Line($"   ... ещё {fields.Count - 45}");

                    doc.Delete(probe.Id);
                    t.Commit();
                }
                catch (System.Exception ex)
                {
                    t.RollBack();
                    log.Line($"   ОШИБКА чтения полей: {ex.GetType().Name}: {ex.Message}");
                }
                log.Line();
            }

            // --- 3. Основной сценарий: ведомость дверей с 4 параметрами ---
            var request = new Model.ScheduleRequest
            {
                ScheduleName = "Ведомость дверей (self-test)",
                Category = "Doors",
                Parameters = new List<string> { "Mark", "Type", "Width", "Height" }
            };

            Model.ScheduleOutcome outcome = CreateInTransaction(doc, uiDoc, request, "SelfTest: создание ведомости");

            log.Line($"3. СЦЕНАРИЙ: «Ведомость дверей», поля Mark / Type / Width / Height");
            log.Line($"   успех          : {outcome.Success}");
            log.Line($"   сообщение      : {outcome.Message}");

            if (outcome.Success)
            {
                created = true;
                var view = doc.GetElement(new ElementId(outcome.ScheduleId)) as ViewSchedule;
                if (view is not null)
                {
                    ScheduleDefinition def = view.Definition;
                    log.Line($"   элемент        : {view.GetType().Name}, имя = \"{view.Name}\", Id = {view.Id.Value}");
                    log.Line($"   полей в ведомости: {def.GetFieldCount()}");
                    for (int i = 0; i < def.GetFieldCount(); i++)
                    {
                        ScheduleField f = def.GetField(i);
                        log.Line($"     [{i}] \"{f.GetName()}\"  type={f.FieldType}");
                    }
                    log.Line($"   сортировок     : {def.GetSortGroupFieldCount()}");
                    log.Line($"   фильтров       : {def.GetFilterCount()}");
                }
                else
                {
                    log.Line("   ОШИБКА: созданный элемент не найден по Id.");
                    created = false;
                }
            }
            log.Line();

            // --- 4. Негативный сценарий: несуществующий параметр ---
            var badRequest = new Model.ScheduleRequest
            {
                ScheduleName = "Ведомость дверей (невалидный параметр)",
                Category = "Doors",
                Parameters = new List<string> { "Mark", "АбсолютноНесуществующийПараметр" }
            };

            Model.ScheduleOutcome bad = CreateInTransaction(doc, uiDoc, badRequest, "SelfTest: невалидный параметр");

            log.Line("4. СЦЕНАРИЙ: несуществующий параметр (ожидаем отказ)");
            log.Line($"   успех          : {bad.Success}  (ожидается False)");
            log.Line($"   сообщение      : {FirstLines(bad.Message, 4)}");

            if (bad.Success && bad.ScheduleId != -1)
            {
                using var cleanup = new Transaction(doc, "SelfTest: очистка");
                cleanup.Start();
                Element? junk = doc.GetElement(new ElementId(bad.ScheduleId));
                if (junk is not null) doc.Delete(junk.Id);
                cleanup.Commit();
                log.Line("   ВНИМАНИЕ: ведомость с невалидным параметром была создана и удалена вручную.");
                created = false;
            }

            log.Line();
            log.Line(created
                ? "ИТОГ: УСПЕХ — Revit API 2026 создал ViewSchedule с запрошенными полями."
                : "ИТОГ: ПРОВАЛ — сценарий не выполнен, смотрите сообщения выше.");

            // --- 5. Окно настроек ИИ: создаётся и читает config.json ---
            log.Line();
            log.Line("5. ОКНО НАСТРОЕК ИИ");
            CheckAiSettingsWindow(log);

            // --- 6. Живой прогон LLM -> JSON -> ведомость (если сервер доступен) ---
            log.Line();
            log.Line("6. ЖИВОЙ ПРОГОН LLM (проверка стыка LLM -> Revit)");
            CheckLiveLlm(uiApp, doc, log);
        }
        catch (System.Exception ex)
        {
            log.Line();
            log.Line("КРИТИЧЕСКАЯ ОШИБКА:");
            log.Line(ex.ToString());
            created = false;
        }

        log.Save();
        return (created, log.ToString());
    }

    /// <summary>
    /// Живой прогон: локальная LLM -> JSON -> создание ведомости в Revit.
    /// Выполняется, только если сервер ИИ отвечает; иначе фиксируется в отчёте.
    /// </summary>
    private static void CheckLiveLlm(UIApplication uiApp, Document doc, SelfTestReport log)
    {
        try
        {
            Llm.AiSettings settings = Llm.AiSettings.LoadOrDefault();
            log.Line($"   модель            : {settings.Model}");
            log.Line($"   адрес             : {settings.Endpoint}");
            log.Line($"   токенов ответа    : {settings.MaxTokens}");

            using var provider = new Llm.LmStudioProvider(settings);
            string categories = RevitApi.CategoryResolver.BuildCategoryListForPrompt(doc);

            // Сетевой вызов уходит в Task.Run: главный поток Revit занят ожиданием и
            // не качает сообщения, поэтому обычный await здесь дал бы дедлок.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string raw = Task.Run(async () =>
                await provider.CompleteAsync("Создай ведомость стен", categories, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();
            sw.Stop();

            log.Line($"   ответ модели      : {sw.Elapsed.TotalSeconds:0.0} с, {raw.Length} симв.");
            log.Line($"   сырой ответ       : {FirstLines(raw, 2)}");

            Model.ScheduleRequest request = Llm.LlmResponseParser.Parse(raw);
            log.Line($"   имя ведомости     : {request.ScheduleName}");
            log.Line($"   категория         : {request.Category}");
            log.Line($"   параметры         : {string.Join(", ", request.Parameters)}");

            if (request.Parameters.Count == 0)
            {
                log.Line("   ВНИМАНИЕ: LLM не вернула параметры.");
                return;
            }

            var wallRequest = new Model.ScheduleRequest
            {
                ScheduleName = "Ведомость стен (живой прогон)",
                Category = request.Category,
                Parameters = request.Parameters
            };

            Model.ScheduleOutcome outcome = CreateInTransaction(doc, uiApp.ActiveUIDocument!, wallRequest, "SelfTest: живой прогон LLM");

            // Те же категории, на которые жаловался пользователь.
            var extraCategories = new (string Category, string[] Fields)[]
            {
                ("OST_LightingFixtures", new[] { "Mark", "Type", "Family and Type" }),
                ("OST_ElectricalEquipment", new[] { "Mark", "Type", "Family and Type" }),
                ("OST_Furniture", new[] { "Mark", "Type", "Family and Type" })
            };

            foreach ((string extra, string[] fields) in extraCategories)
            {
                var req = new Model.ScheduleRequest
                {
                    ScheduleName = "Ведомость " + extra + " (живой прогон)",
                    Category = extra,
                    Parameters = fields.ToList()
                };

                Model.ScheduleOutcome res = CreateInTransaction(
                    doc, uiApp.ActiveUIDocument!, req, "SelfTest: живой прогон LLM");

                log.Line($"   {extra,-28} -> создана: {res.Success}" +
                         (res.Success ? "" : " (" + FirstLines(res.Message, 1) + ")"));
            }
            log.Line($"   ведомость создана : {outcome.Success}");
            log.Line($"   сообщение         : {FirstLines(outcome.Message, 2)}");
            log.Line(outcome.Success
                ? "   ИТОГ ЖИВОГО ПРОГОНА: УСПЕХ — LLM и Revit API работают вместе."
                : "   ИТОГ ЖИВОГО ПРОГОНА: ведомость не создана, смотрите сообщение выше.");
        }
        catch (Llm.LlmException ex)
        {
            log.Line($"   сервер ИИ недоступен или ответ не разобран: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            log.Line($"   ОШИБКА живого прогона: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет, что категория находится по разным вариантам написания:
    /// встроенное имя OST_*, имя без префикса, имя с пробелами и метка языка Revit.
    /// Именно на этом ломалось определение категории в русском интерфейсе Revit.
    /// </summary>
    private static void CheckCategoryResolution(Document doc, SelfTestReport log)
    {
        var probes = new (string Request, BuiltInCategory Expected)[]
        {
            // Встроенные имена и английские варианты
            ("OST_Walls", BuiltInCategory.OST_Walls),
            ("Walls", BuiltInCategory.OST_Walls),
            ("OST_Doors", BuiltInCategory.OST_Doors),
            ("Doors", BuiltInCategory.OST_Doors),
            ("LightingFixtures", BuiltInCategory.OST_LightingFixtures),
            ("Lighting Fixtures", BuiltInCategory.OST_LightingFixtures),
            ("ElectricalEquipment", BuiltInCategory.OST_ElectricalEquipment),
            ("Electrical Equipment", BuiltInCategory.OST_ElectricalEquipment),
            ("MechanicalEquipment", BuiltInCategory.OST_MechanicalEquipment),

            // Русские написания — именно эти слова использует пользователь
            ("Стены", BuiltInCategory.OST_Walls),
            ("стены", BuiltInCategory.OST_Walls),
            ("стена", BuiltInCategory.OST_Walls),
            ("Мебель", BuiltInCategory.OST_Furniture),
            ("мебель", BuiltInCategory.OST_Furniture),
            ("Осветительные приборы", BuiltInCategory.OST_LightingFixtures),
            ("светильники", BuiltInCategory.OST_LightingFixtures),
            ("Электрические приборы", BuiltInCategory.OST_ElectricalEquipment),
            ("электрические приборы", BuiltInCategory.OST_ElectricalEquipment),
            ("Электрооборудование", BuiltInCategory.OST_ElectricalEquipment),
            ("Механическое оборудование", BuiltInCategory.OST_MechanicalEquipment),
            ("Сантехническое оборудование", BuiltInCategory.OST_PlumbingEquipment),
            ("Колонны", BuiltInCategory.OST_StructuralColumns),
            ("Помещения", BuiltInCategory.OST_Rooms),
            ("Перекрытия", BuiltInCategory.OST_Floors),
            ("Потолки", BuiltInCategory.OST_Ceilings),
            ("Двери", BuiltInCategory.OST_Doors),
            ("Окна", BuiltInCategory.OST_Windows)
        };

        int ok = 0;
        foreach (var probe in probes)
        {
            bool found = CategoryResolver.TryResolve(doc, probe.Request, out ElementId id, out string label);
            Category? cat = Category.GetCategory(doc, id);
            bool correct = found && cat is not null && cat.BuiltInCategory == probe.Expected;

            if (correct) ok++;

            log.Line($"   {probe.Request,-30} -> {(correct ? "OK" : "НЕ НАЙДЕНА")}" +
                     $"{(found ? $"  (метка Revit: {label})" : "")}");
        }

        log.Line($"   итого: {ok} из {probes.Length} вариантов написания распознано");

        // Показываем, как категория выглядит в этом интерфейсе Revit.
        foreach (BuiltInCategory bic in new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_LightingFixtures })
        {
            Category? cat = Category.GetCategory(doc, new ElementId(bic));
            if (cat is not null)
            {
                log.Line($"   {bic}: имя категории в Revit = {cat.Name}");
            }
        }

    }
    /// <summary>
    /// Печатает метки категорий в текущем языке Revit, чтобы опереться на факты,
    /// а не на догадки при составлении словаря синонимов.
    /// </summary>
    private static void DumpRussianLabels(Document doc, SelfTestReport log)
    {
        BuiltInCategory[] categories =
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_FurnitureSystems,
            BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_PlumbingEquipment,
            BuiltInCategory.OST_StructuralColumns
        };

        foreach (BuiltInCategory bic in categories)
        {
            Category? cat = Category.GetCategory(doc, new ElementId(bic));
            log.Line($"   {bic,-32} = {cat?.Name ?? "(категория не найдена)"}");
        }
    }


    /// Проверяет, что окно настроек ИИ создаётся и корректно читает config.json.
    /// Окно показывается и сразу закрывается — так проверяется полный путь
    /// создания элементов WPF и загрузки настроек (главный поток Revit — STA).
    /// </summary>
    private static void CheckAiSettingsWindow(SelfTestReport log)
    {
        try
        {
            Llm.AiSettings settings = Llm.AiSettings.Load(out string note);

            log.Line($"   config.json        : {Llm.AiSettings.ConfigPath}");
            log.Line($"   адрес по умолчанию : {settings.Endpoint}");
            log.Line($"   модель по умолчанию: {settings.Model}");
            log.Line($"   примечание          : {(string.IsNullOrEmpty(note) ? "(нет)" : note)}");

            IReadOnlyList<string> problems = settings.Validate();
            log.Line($"   валидация           : {(problems.Count == 0 ? "ошибок нет" : string.Join("; ", problems))}");

            var window = new Ui.AiSettingsWindow();
            window.Show();   // здесь срабатывает Loaded -> настройки грузятся в поля
            window.Close();

            log.Line("   окно                : создано и закрыто без ошибок");
        }
        catch (System.Exception ex)
        {
            log.Line($"   ОШИБКА окна настроек: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Открывает существующий проект .rvt — нужно только для автоматического SelfTest.
    /// Revit API 2026: UIApplication.OpenAndActivateDocument(String fileName).
    /// </summary>
    private static bool TryOpenProject(UIApplication uiApp, string path, SelfTestReport log, out UIDocument? uiDoc)
    {
        uiDoc = null;

        try
        {
            if (!System.IO.File.Exists(path)) return false;

            log.Line($"Открываю проект: {path}");
            uiDoc = uiApp.OpenAndActivateDocument(path);
            return uiDoc is not null;
        }
        catch (System.Exception ex)
        {
            log.Line($"   Ошибка открытия проекта: {ex.GetType().Name}: {ex.Message}");
            uiDoc = uiApp.ActiveUIDocument;
            return uiDoc is not null;
        }
    }

    /// <summary>
    /// Создаёт новый проект из шаблона Revit — нужно только для автозапуска SelfTest,
    /// когда пользователь ещё не открыл модель.
    /// </summary>
    private static bool TryCreateProject(UIApplication uiApp, out UIDocument? uiDoc)
    {
        uiDoc = null;

        try
        {
            // Revit API 2026: Application.NewProjectDocument(String templateFileName).
            string? template = FindDefaultTemplate();
            if (template is null) return false;

            Autodesk.Revit.DB.Document doc = uiApp.Application.NewProjectDocument(template);
            uiDoc = uiApp.ActiveUIDocument;

            return doc is not null && uiDoc is not null;
        }
        catch
        {
            uiDoc = uiApp.ActiveUIDocument;
            return uiDoc is not null;
        }
    }

    /// <summary>Ищет шаблон Revit (.rte) в стандартных папках Templates.</summary>
    private static string? FindDefaultTemplate()
    {
        string[] roots =
        {
            @"C:\ProgramData\Autodesk\RVT 2026\Templates",
            @"C:\ProgramData\Autodesk\RVT 2026\Family Templates"
        };

        foreach (string dir in roots)
        {
            if (!System.IO.Directory.Exists(dir)) continue;

            string? english = System.IO.Directory.GetFiles(dir, "*.rte")
                .FirstOrDefault(f => f.Contains("_ENU", StringComparison.OrdinalIgnoreCase));
            if (english is not null) return english;

            string? any = System.IO.Directory.GetFiles(dir, "*.rte").FirstOrDefault();
            if (any is not null) return any;
        }

        return null;
    }

    /// <summary>Создаёт ведомость в транзакции с корректным откатом при ошибке.</summary>
    private static Model.ScheduleOutcome CreateInTransaction(
        Document doc, UIDocument uiDoc, Model.ScheduleRequest request, string txName)
    {
        using var transaction = new Transaction(doc, txName);
        transaction.Start();
        try
        {
            Model.ScheduleOutcome result = ScheduleFactory.Create(uiDoc, request);
            transaction.Commit();
            return result;
        }
        catch (ScheduleValidationException vex)
        {
            transaction.RollBack();
            return Model.ScheduleOutcome.Fail(vex.Message);
        }
        catch (System.Exception ex)
        {
            try { transaction.RollBack(); } catch { }
            return Model.ScheduleOutcome.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FirstLines(string text, int count)
    {
        string[] parts = (text ?? string.Empty).Split('\n');
        return string.Join(" | ", parts.Take(count).Select(s => s.Trim()).Where(s => s.Length > 0));
    }
}

/// <summary>Накопитель строк отчёта SelfTest.</summary>
public sealed class SelfTestReport
{
    private readonly System.Text.StringBuilder _sb = new();

    /// <summary>
    /// Путь отчёта. Кладём в %APPDATA%, а не %TEMP%: процесс Revit может иметь
    /// другое значение TEMP, а %APPDATA% у него всегда один.
    /// </summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitAISchedule_SelfTest.txt");

    public void Line(string text = "") => _sb.AppendLine(text);

    public override string ToString() => _sb.ToString();

    public void Save()
    {
        try { System.IO.File.WriteAllText(Path, ToString(), System.Text.Encoding.UTF8); }
        catch { /* отчёт не критичен для работы плагина */ }
    }
}
