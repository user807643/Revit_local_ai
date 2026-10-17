## Revit создание ведомостей

Плагин для `Revit 2026`, который создаёт ведомости по описанию на естественном языке с помощью `локальной LLM`.

> Цель разработки - MVP: создание новой ведомости, выбор категории, добавление запрошенных
> параметров, сортировка и фильтры.

## Сборка и установка

Вариант 1. Установить файлы скачанные из релиза:

```
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule.addin
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\RevitAISchedule.dll
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\RevitAISchedule.pdb
```

Вариант 2. Собрать файлы вручную:

```powershell
.\build\Install-Plugin.ps1
.\build\Install-Plugin.ps1 -Zip
```

Вариант 3: Собрать в папку:

```powershell
dotnet build RevitAISchedule.sln -c Release
```

## Использование

1. Запустите `LM Studio bionic`, загрузите модель и включите Local Server
   (по умолчанию `http://127.0.0.1:1234`).
2. В Revit откройте проект, найдите где-то на ленте и запустите плагин `Revit ИИ`.
3. Введите, например:

   ```
   Создай ведомость дверей с параметрами Марка, Тип, Ширина, Высота
   ```
   или просто
   ```
   спека стен
   ```

4. Нажмите `Создать` и найдите ее через диспетчер прокекта.

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

Ключевое правило архитектуры: `LLM никогда не обращается к Revit API и никогда не
открывает Transaction`. LLM возвращает только JSON, всю работу с моделью Revit
делает C#.

## Требования

| Компонент | Версия |
|-----------|--------|
| Autodesk Revit | 2026 (собран на .NET 8 — проверено по `RevitAPI.dll`) |
| .NET SDK | 8.0.x |
| LM Studio | любая версия с загруженной моделью и включённым локальным сервером |
| Windows | 10/11 |

Путь к Revit API задаётся в `Directory.Build.props` → `RevitApiDir`
(по умолчанию `C:\Program Files\Autodesk\Revit 2026\`).

## Параметры: почему LLM не может «выдумать» поля

Это ключевая часть ТЗ, и она сделана строго:

1. После определения категории C# запрашивает у Revit реальный список полей:
   `ScheduleDefinition.GetSchedulableFields()` (Revit API 2026).
2. Для каждого поля собираются все реальные имена:
   * `SchedulableField.GetName(Document)` — имя в языке интерфейса Revit;
   * имя элемента `BuiltInParameter` (например `ALL_MODEL_MARK`);
   * `LabelUtils.GetLabelFor(BuiltInParameter)` для текущего языка, English и Russian.
3. Запрос пользователя сопоставляется с этими именами (точное совпадение →
   нормализованное → словарь синонимов RU/EN).
4. Если параметр не найден — ведомость не создаётся, транзакция откатывается,
   пользователь получает:

   ```
   Параметр «X» не найден для категории «Двери». Ведомость не создана.

   Доступные поля для этой категории: Марка, Тип, Наименование семейства, ...
   ```

из реально существующих категорий.

## Протестировано
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

Список категорий для подсказки LLM тоже берётся из Revit
(`ViewSchedule.GetValidCategoriesForSchedule()`), поэтому модель выбирает
из реально существующих категорий.

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

## Ограничения из-за MVP

* нет группировки, форматирования, Material Takeoff;
* не изменяет и не удаляет существующие ведомости;
* нет вычисляемых полей и web-research;
* не генерирует код;
* не моделирует элементы;
* сопоставление параметров — точное/нормализованное/по словарю синонимов
  (интеллектуальный fuzzy-match вынесен за рамки MVP).
