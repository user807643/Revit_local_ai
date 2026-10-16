# Revit AI Schedule — MVP

Плагин для **Autodesk Revit 2026**, который создаёт настоящие ведомости (`ViewSchedule`)
по описанию на естественном языке с помощью **локальной LLM** (LM Studio).

> MVP-область: создание новой ведомости, выбор категории, добавление запрошенных
> параметров, сортировка и фильтры. Ничего лишнего.

---

## Как это работает

```
Revit 2026
   ↓  IExternalCommand (кнопка «Внешние инструменты»)
WPF-окно «Revit AI Schedule»
   ↓  HTTP POST → http://127.0.0.1:1234/v1/chat/completions   (LM Studio, локально)
структурированный ScheduleRequest (JSON)
   ↓  парсинг + валидация (без Revit API)
ExternalEvent.Raise()
   ↓  главный поток Revit
IExternalEventHandler.Execute(UIApplication)
   ↓  Transaction
Revit API 2026: ViewSchedule.CreateSchedule + ScheduleDefinition.AddField
   ↓
новая ViewSchedule с нужными полями
   ↓  сообщение в окно: «Ведомость '…' создана.»
```

Ключевое правило архитектуры: **LLM никогда не обращается к Revit API и никогда не
открывает Transaction**. LLM возвращает только JSON, всю работу с моделью Revit
делает C#.

---

## Требования

| Компонент | Версия |
|-----------|--------|
| Autodesk Revit | 2026 (собран на .NET 8 — проверено по `RevitAPI.dll`) |
| .NET SDK | 8.0.x |
| LM Studio | любая версия с загруженной моделью и включённым локальным сервером |
| Windows | 10/11 |

Путь к Revit API задаётся в `Directory.Build.props` → `RevitApiDir`
(по умолчанию `C:\Program Files\Autodesk\Revit 2026\`).

---

## Сборка и установка

```powershell
# Собрать и установить (рекомендуется)
.\build\Install-Plugin.ps1

# То же + ZIP-архив проекта
.\build\Install-Plugin.ps1 -Zip
```

Или вручную:

```powershell
dotnet build RevitAISchedule.sln -c Release
```

Затем разложить файлы **именно так** (требование Revit 2026):

```
%APPDATA%\Autodesk\Revit\Addins\2026\
   |-- RevitAISchedule.addin      <- манифест в КОРНЕ папки Addins
   `-- RevitAISchedule\
          |-- RevitAISchedule.dll
          `-- RevitAISchedule.pdb
```

> **Важно (проверено на Revit 2026).** Манифест `.addin` должен лежать **в корне**
> папки `Addins\2026`, а сборка — **в подпапке** (та же схема, что у `HidePanels.addin`).
> Если положить `.addin` в подпапку, Revit его молча игнорирует: команда не появляется,
> а в журнале нет ни одной записи о плагине.
> Также в манифесте обязателен узел `<VendorId>` — без него Revit показывает ошибку
> «add-in registration is missing the required value of VendorId node».

Запуск: **Внешние инструменты → Revit AI Schedule**.

---

## Использование

1. Запустите **LM Studio**, загрузите модель и включите Local Server
   (по умолчанию `http://127.0.0.1:1234`).
2. В Revit откройте проект и запустите **Внешние инструменты → Revit AI Schedule**.
3. Введите, например:

   ```
   Создай ведомость дверей с параметрами Марка, Тип, Ширина, Высота
   ```

4. Нажмите **«Создать»**.

Ожидаемый результат:

```
Ведомость 'Ведомость дверей' создана. Поля: Марка, Тип, Ширина, Высота.
```

### Диагностика без LLM

**Внешние инструменты → Revit AI Schedule SelfTest** — создаёт ведомость по
жёстко заданному запросу и пишет подробный отчёт в `%TEMP%\RevitAISchedule_SelfTest.txt`:
список категорий, реальные поля категории, результат создания и негативный сценарий
с несуществующим параметром. Позволяет отделить проблемы Revit API от проблем LM Studio.

Автоматический прогон без участия пользователя:

```powershell
.\build\Install-Plugin.ps1 -SelfTest -ProjectPath "D:\project.rvt"
```

Скрипт создаёт файл-флаг `%TEMP%\RevitAISchedule_AutoSelfTest.txt`, запускает Revit,
ждёт отчёт и печатает его в консоль. Файл-флаг: %TEMP%\RevitAISchedule_AutoSelfTest.txt.

---

## Параметры: почему LLM не может «выдумать» поля

Это ключевая часть ТЗ, и она сделана строго:

1. После определения категории C# **запрашивает у Revit реальный список полей**:
   `ScheduleDefinition.GetSchedulableFields()` (Revit API 2026).
2. Для каждого поля собираются все реальные имена:
   * `SchedulableField.GetName(Document)` — имя в языке интерфейса Revit;
   * имя элемента `BuiltInParameter` (например `ALL_MODEL_MARK`);
   * `LabelUtils.GetLabelFor(BuiltInParameter)` для текущего языка, English и Russian.
3. Запрос пользователя сопоставляется с этими именами (точное совпадение →
   нормализованное → словарь синонимов RU/EN).
4. Если параметр не найден — **ведомость не создаётся**, транзакция откатывается,
   пользователь получает:

   ```
   Параметр «X» не найден для категории «Двери». Ведомость не создана.

   Доступные поля для этой категории: Марка, Тип, Наименование семейства, ...
   ```

из реально существующих категорий.

---

## Проверено на реальном Revit 2026

Автотест выполнен в установленном Revit 2026.4 (build 26.4.0.32) на реальном
проекте, категория «Двери» (271 поле):

```
1. Категорий, доступных для ведомости: 192
   резолв "Doors"    : OK -> Id=-2000023 (Doors)
   резолв "OST_Doors": OK (Doors)
2. Полей у категории "Doors": 271
3. СЦЕНАРИЙ: ведомость дверей, поля Mark / Type / Width / Height
   успех       : True
   сообщение   : Ведомость "Ведомость дверей (self-test)" создана.
                 Поля: Mark, Type, Width, Height.
   элемент     : ViewSchedule, Id = 549359
   полей в ведомости: 4
     [0] "Mark"    [1] "Type"    [2] "Width"    [3] "Height"
4. СЦЕНАРИЙ: несуществующий параметр
   успех       : False   (ведомость не создана - корректно)
ИТОГ: УСПЕХ - Revit API 2026 создал ViewSchedule с запрошенными полями.
```

Отдельно проходят 31 автотест слоёв, не требующих Revit (LLM, разбор JSON,
сопоставление имён):

```powershell
dotnet run --project tests\RevitAISchedule.Tests
```

> Первый прогон в Revit выявил дефект: `Width` попадал в `Rough Width`,
> а `Height` — в `Head Height`. Причина — нестрогий матч плюс алиас-часть
> `WIDTH` у поля `Rough Width`. Исправлено приоритетом имени поля и правилом
> "самое короткое имя"; добавлен регрессионный тест с реальными данными Revit.

---
Список категорий для подсказки LLM тоже берётся из Revit
(`ViewSchedule.GetValidCategoriesForSchedule()`), поэтому модель выбирает
из реально существующих категорий.

---

## Структура проекта

```
RevitAISchedule.sln
Directory.Build.props              путь к Revit API, общие настройки
src/RevitAISchedule/
  RevitAISchedule.csproj           net8.0-windows, UseWPF, ссылки на RevitAPI/RevitAPIUI
  RevitAISchedule.addin            манифест аддина (2 команды)
  Commands/
    ScheduleCreatorCommand.cs      IExternalCommand: открывает WPF-окно, создаёт ExternalEvent
    ScheduleSelfTestCommand.cs     диагностика без LLM
  Events/
    CreateScheduleEventHandler.cs  IExternalEventHandler: Transaction + создание ведомости
  Llm/
    ILLMProvider.cs                интерфейс LLM
    LmStudioProvider.cs            реализация для LM Studio (OpenAI-совместимый HTTP)
    PromptBuilder.cs               системная подсказка + список категорий из Revit
    LlmResponseParser.cs           устойчивый разбор JSON из ответа модели
  Model/
    ScheduleRequest.cs             JSON-модель запроса
    ScheduleOutcome.cs             результат для UI
  RevitApi/
    CategoryResolver.cs            строка -> ElementId категории
    ScheduleFieldCatalog.cs        реальные поля категории из Revit
    ParameterResolver.cs           сопоставление имён + синонимы RU/EN
    ScheduleFactory.cs             ViewSchedule + поля + сортировка + фильтры
  Ui/
    MainWindow.xaml(.cs)           окно плагина
build/
  Install-Plugin.ps1               сборка + установка + ZIP
  SelfTest.jrn                     journal для автотеста в Revit
```

---

## Формат JSON от LLM

```json
{
  "scheduleName": "Ведомость дверей",
  "category": "Doors",
  "parameters": ["Mark", "Type", "Width", "Height"],
  "sortBy": ["Mark"],
  "filters": [
    { "parameter": "Mark", "operator": "notEmpty", "value": "" }
  ]
}
```

Обязательны только `category` и `parameters`. `sortBy` и `filters` необязательны.

---

## Ограничения MVP (осознанно)

* нет группировки, форматирования, Material Takeoff;
* не изменяет и не удаляет существующие ведомости;
* нет вычисляемых полей и web-research;
* не генерирует код;
* не моделирует элементы;
* сопоставление параметров — точное/нормализованное/по словарю синонимов
  (интеллектуальный fuzzy-match вынесен за рамки MVP).