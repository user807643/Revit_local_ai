using System.Windows;
using System.Windows.Interop;
using RevitAISchedule.Ui;

namespace RevitAISchedule.Commands;

/// <summary>
/// Показывает окно настроек ИИ как дочернее окно Revit.
/// Повторный вызов не создаёт второе окно, а выводит на передний план уже открытое.
/// </summary>
internal static class AiSettingsHost
{
    private static AiSettingsWindow? _window;

    public static void Show()
    {
        AiSettingsWindow? existing = _window;

        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;

            existing.Activate();
            return;
        }

        var window = new AiSettingsWindow();
        window.Closed += (_, _) => { _window = null; };
        _window = window;

        AttachToRevit(window);
        window.Show();
    }

    /// <summary>
    /// Делает окно дочерним главного окна Revit: оно всегда поверх Revit и не мешает
    /// работе в приложении. Ошибка здесь не критична — окно всё равно откроется.
    /// </summary>
    private static void AttachToRevit(Window window)
    {
        try
        {
            IntPtr owner = FindRevitMainWindow();
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = owner;
        }
        catch
        {
            // Родитель не задался — ничего страшного, окно просто не будет привязано.
        }
    }

    /// <summary>
    /// Ищет актуальный дескриптор главного окна Revit.
    /// Process.MainWindowHandle ненадёжен: окно может быть ещё не создано,
    /// а дескриптор устаревает после перезапуска — поэтому ищем актуальный.
    /// </summary>
    private static IntPtr FindRevitMainWindow()
    {
        using System.Diagnostics.Process? process = System.Diagnostics.Process.GetCurrentProcess();

        if (process?.MainWindowHandle != IntPtr.Zero)
            return process.MainWindowHandle;

        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(handle, out uint pid) != 0 &&
                pid == (uint)process!.Id &&
                NativeMethods.IsWindowVisible(handle) &&
                NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER) == IntPtr.Zero)
            {
                found = handle;
                return false; // остановиться на первом подходящем
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}

/// <summary>Минимальный набор P/Invoke для поиска главного окна Revit.</summary>
internal static class NativeMethods
{
    internal const int GW_OWNER = 4;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
}