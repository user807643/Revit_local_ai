# Установка из архива (Revit 2026)

## Что внутри

```
dist\RevitAISchedule\
    RevitAISchedule.dll      <- сборка плагина
    RevitAISchedule.pdb      <- символы для отладки
    RevitAISchedule.addin    <- манифест аддина
src\                         <- исходный код
tests\                       <- автотесты слоёв, не требующих Revit
build\Install-Plugin.ps1     <- сборка + установка
RevitAISchedule.sln
Directory.Build.props
README.md
```

## Установка (2 файловых действия)

1. Скопировать **манифест** в корень папки Addins:

```
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule.addin
```

2. Скопировать **сборку** в подпапку:

```
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\RevitAISchedule.dll
%APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule\RevitAISchedule.pdb
```

Итого должно получиться:

```
%APPDATA%\Autodesk\Revit\Addins\2026\
    |-- RevitAISchedule.addin
    `-- RevitAISchedule\
           |-- RevitAISchedule.dll
           `-- RevitAISchedule.pdb
```

> **Важно.** В Revit 2026 манифест `.addin` обязан лежать **в корне** папки `Addins\2026`,
> а сборка — **в подпапке**. Если `.addin` положить в подпапку, Revit его молча
> проигнорирует (команда не появится в «Внешние инструменты»).
> В манифесте обязательно должен быть узел `<VendorId>`.

Запустить Revit → **Внешние инструменты → Revit AI Schedule**.

## Если нужно пересобрать из исходников

Нужен .NET SDK 8.0 и установленный Revit 2026.

```powershell
# сборка + установка + (при желании) ZIP
.\build\Install-Plugin.ps1

# прогнать 31 автотест слоёв LLM/JSON/сопоставления имён
dotnet run --project .\tests\RevitAISchedule.Tests
```

## Проверка без LM Studio

**Внешние инструменты → Revit AI Schedule SelfTest** — создаёт ведомость по
жёстко заданному запросу и пишет отчёт в `%APPDATA%\RevitAISchedule_SelfTest.txt`.
Отчёт показывает: список категорий, реальные поля категории, результат создания
ведомости и проверку отказа на несуществующем параметре.

Автоматический прогон:

```powershell
.\build\Install-Plugin.ps1 -SelfTest -ProjectPath "D:\project.rvt"
```