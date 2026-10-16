#Requires -Version 5.1
<#
.SYNOPSIS
    Сборка и установка плагина Revit AI Schedule для Autodesk Revit 2026.

.DESCRIPTION
    1. Проверяет наличие Revit 2026 (RevitAPI.dll / RevitAPIUI.dll).
    2. Собирает solution в Release.
    3. Копирует готовую папку плагина в %APPDATA%\Autodesk\Revit\Addins\2026\RevitAISchedule.
    4. При -Zip собирает ZIP-архив проекта для передачи/установки на другой компьютер.

.EXAMPLE
    .\Install-Plugin.ps1
    .\Install-Plugin.ps1 -Zip
    .\Install-Plugin.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Zip,
    [string]$ZipPath,
    [switch]$SelfTest,
    [string]$ProjectPath = ''
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Solution    = Join-Path $ProjectRoot 'RevitAISchedule.sln'
$ProjectDir  = Join-Path $ProjectRoot 'src\RevitAISchedule'
$BuildDir    = Join-Path $ProjectDir "bin\$Configuration"
$RevitDir    = 'C:\Program Files\Autodesk\Revit 2026'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Err($text)  { Write-Host "    $text" -ForegroundColor Red }

Write-Step 'Проверка окружения'

# --- 1. Revit 2026 ---
$revitApi    = Join-Path $RevitDir 'RevitAPI.dll'
$revitApiUi  = Join-Path $RevitDir 'RevitAPIUI.dll'
if (-not (Test-Path $revitApi) -or -not (Test-Path $revitApiUi)) {
    Write-Err "Revit 2026 не найден в '$RevitDir'."
    Write-Err 'Установите Revit 2026 или задайте RevitApiDir в Directory.Build.props.'
    exit 1
}
Write-Ok "RevitAPI.dll и RevitAPIUI.dll найдены."

# --- 2. .NET SDK 8 ---
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) {
    Write-Err 'Не найден dotnet CLI (.NET SDK 8.0).'
    exit 1
}
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Select-String '^8\.')) {
    Write-Err 'Требуется .NET SDK 8.0.x (Revit 2026 работает на .NET 8).'
    exit 1
}
Write-Ok ".NET SDK 8 найден: $(($sdks | Select-String '^8\.' | Select-Object -First 1).Line)"

# --- 3. Сборка ---
Write-Step "Сборка ($Configuration)"
& dotnet build $Solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Err 'Сборка завершилась с ошибками.'
    exit $LASTEXITCODE
}
Write-Ok 'Сборка успешна.'

# --- 4. Установка в Addins ---
# ВАЖНО для Revit 2026: манифест .addin должен лежать в корне папки Addins\2026,
# а сборка - в подпапке (та же схема, что у HidePanels.addin).
# Адрес сборки в манифесте указывает на подпапку: RevitAISchedule\RevitAISchedule.dll
Write-Step 'Установка в папку Addins Revit 2026'
$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$targetDir  = Join-Path $addinsRoot 'RevitAISchedule'
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

foreach ($file in @('RevitAISchedule.dll', 'RevitAISchedule.pdb')) {
    $src = Join-Path $BuildDir $file
    if (Test-Path $src) {
        Copy-Item $src $targetDir -Force
        Write-Ok "DLL-файл: $file -> $targetDir"
    }
}

# Манифест - в корень Addins\2026
Copy-Item (Join-Path $ProjectDir 'RevitAISchedule.addin') $addinsRoot -Force
Write-Ok "Манифест: RevitAISchedule.addin -> $addinsRoot"

# Убираем устаревший манифест из подпапки, если он там остался от прошлой установки
$staleManifest = Join-Path $targetDir 'RevitAISchedule.addin'
if (Test-Path $staleManifest) {
    Remove-Item $staleManifest -Force
    Write-Ok 'Удалён устаревший манифест из подпапки'
}

Write-Host ''
Write-Host '    Запуск в Revit: Внешние инструменты -> Revit AI Schedule' -ForegroundColor Yellow
Write-Host '    Диагностика:   Внешние инструменты -> Revit AI Schedule SelfTest' -ForegroundColor Yellow

# --- 4.1 Автоматическая проверка (по запросу) ---
if ($SelfTest) {
    Write-Step 'Запуск Revit 2026 с автоматической проверкой (SelfTest)'
    $report = Join-Path $env:TEMP 'RevitAISchedule_SelfTest.txt'
    Remove-Item $report -ErrorAction SilentlyContinue

    # Плагин читает этот файл-флаг при старте Revit и сам запускает SelfTest.
    # Первая строка — путь к .rvt (пустая строка — создать проект из шаблона Revit).
    $flag = Join-Path $env:TEMP 'RevitAISchedule_AutoSelfTest.txt'
    Set-Content -Path $flag -Value $ProjectPath -Encoding UTF8
    Write-Ok "Создан флаг автозапуска: $flag"

    $revitExe = Join-Path $RevitDir 'Revit.exe'
    if (-not (Test-Path $revitExe)) {
        Write-Err "Не найден $revitExe"
    }
    else {
        Start-Process -FilePath $revitExe -ArgumentList '/language', 'ENU' | Out-Null
        Write-Host '    Revit запущен. Ожидание отчёта (до 3 минут)...' -ForegroundColor Yellow

        for ($i = 0; $i -lt 90; $i++) {
            Start-Sleep -Seconds 2
            if (Test-Path $report) {
                Start-Sleep -Seconds 2
                Write-Ok 'Отчёт SelfTest создан:'
                Get-Content $report -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
                break
            }
        }

        if (-not (Test-Path $report)) {
            Write-Err 'Отчёт не создан. Смотрите журнал: %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2026\Journals'
        }
    }
}

# --- 5. ZIP-архив проекта ---
if ($Zip) {
    Write-Step 'Сборка ZIP-архива проекта'
    if (-not $ZipPath) {
        $ZipPath = Join-Path $ProjectRoot ("RevitAISchedule_" + $Configuration + ".zip")
    }
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ('rais_zip_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    try {
        # Исходники проекта
        Copy-Item $Solution (Join-Path $staging 'RevitAISchedule.sln') -Force
        Copy-Item (Join-Path $ProjectRoot 'Directory.Build.props') $staging -Force
        Copy-Item (Join-Path $ProjectRoot 'README.md') $staging -Force -ErrorAction SilentlyContinue
        foreach ($item in @('src', 'build', 'docs')) {
            $p = Join-Path $ProjectRoot $item
            if (Test-Path $p) { Copy-Item $p $staging -Recurse -Force }
        }

        # Готовая сборка плагина
        $dist = Join-Path $staging 'dist\RevitAISchedule'
        New-Item -ItemType Directory -Force -Path $dist | Out-Null
        foreach ($file in @('RevitAISchedule.dll', 'RevitAISchedule.pdb', 'RevitAISchedule.addin')) {
            $src = Join-Path $BuildDir $file
            if (Test-Path $src) { Copy-Item $src $dist -Force }
        }

        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $ZipPath -CompressionLevel Optimal
        $size = [math]::Round((Get-Item $ZipPath).Length / 1MB, 2)
        Write-Ok "Архив создан: $ZipPath ($size МБ)"
    }
    finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Step 'Готово.'