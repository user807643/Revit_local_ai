![Скриншот](https://github.com/user807643/Revit_local_ai/blob/main/img/Скриншот.png)
## Revit создание ведомостей

Плагин для `Revit 2026`, который создаёт ведомости по описанию на естественном языке с помощью `локального ИИ`.

> Цель разработки - MVP: создание новой ведомости, выбор категории, добавление запрошенных
> параметров, сортировка и фильтры.

## Установка

Сохранить файлы скачанные из релиза:

```
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule.addin
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\RevitAISchedule.dll
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\RevitAISchedule.pdb
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\config.json
```

## Использование

1. Запусти [LM Studio Bionic](https://lmstudio.ai/download/bionic/latest/win32/x64)

3. Скачай модель, например [qwen3-vl-2b-instruct](https://huggingface.co/unsloth/Qwen3-VL-2B-Instruct-GGUF/blob/main/Qwen3-VL-2B-Instruct-Q8_0.gguf) сохранив сюда:

```
%userprofile%\.lmstudio\models\qwen\
```
  
5. Включи Local Model API Server `http://localhost:1234/v1`
6. В Revit открой проект -> `Надстройки` -> `Revit ИИ` -> `Настройки`
7. Там через `Список моделей` и `Проверить` загрузи ИИ
8. В `Чате` плагина введи, например:

   ```
   Создай ведомость дверей с параметрами Марка, Тип, Ширина, Высота
   ```
   или просто
   ```
   спека стен
   ```

10. Нажми `Создать` и найди ее через диспетчер проекта.

## Как это работает

![Схема работы](img/Схема%20работы.png)

Ключевое правило архитектуры: `ИИ никогда не обращается к Revit API и никогда не
открывает Transaction`. ИИ возвращает только JSON, всю работу с моделью Revit
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

## Параметры: почему ИИ не может «выдумать» поля

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
- Автотест выполнен в установленном Revit 2026.4 (build 26.4.0.32) на реальном
проекте, категория «Двери»

- Первый прогон в Revit выявил дефект: `Width` попадал в `Rough Width`,
а `Height` — в `Head Height`. Причина — нестрогий матч плюс алиас-часть
`WIDTH` у поля `Rough Width`. Исправлено приоритетом имени поля и правилом
"самое короткое имя"; добавлен регрессионный тест с реальными данными Revit.

- Список категорий для подсказки ИИ тоже берётся из Revit
(`ViewSchedule.GetValidCategoriesForSchedule()`), поэтому модель выбирает
из реально существующих категорий.

## Формат JSON от ИИ

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
