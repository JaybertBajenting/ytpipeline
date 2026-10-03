using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace YtPipeline.Web;

/// <summary>Opens File Explorer on a file or folder and brings its window to the front.</summary>
/// <remarks>
/// This server is never the foreground process — the browser is — and Windows won't let a background
/// process's new window take focus, so a bare <c>explorer /select</c> opens behind the browser and only
/// flashes on the taskbar. Briefly joining the foreground window's input queue lets SetForegroundWindow
/// succeed without faking a keypress.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ExplorerWindow
{
    /// <summary>Opens the folder containing <paramref name="path"/> with it selected, or the folder itself.</summary>
    public static void Reveal(string path, bool isFile, ILogger logger)
    {
        var folder = isFile ? Path.GetDirectoryName(path)! : path;

        // Explorer opens a new window even when one already shows this folder, so remember which were
        // open beforehand — otherwise the old window gets raised and the new one stays hidden.
        var before = OnStaThread(() => WindowsShowing(folder)).ToHashSet();
        Process.Start(new ProcessStartInfo("explorer.exe", isFile ? $"/select,\"{path}\"" : $"\"{path}\""));

        var thread = new Thread(() => BringForward(folder, before, logger)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void BringForward(string folder, HashSet<IntPtr> before, ILogger logger)
    {
        try
        {
            // AttachThreadInput fails for a thread without a message queue; this creates one.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);

            for (var attempt = 0; attempt < 40; attempt++)
            {
                var showing = WindowsShowing(folder);
                var hwnd = showing.FirstOrDefault(h => !before.Contains(h));
                // No new window after a while means Explorer reused an existing one.
                if (hwnd == IntPtr.Zero && attempt >= 15 && showing.Count > 0) hwnd = showing[0];
                if (hwnd != IntPtr.Zero)
                {
                    var inFront = Activate(hwnd);
                    logger.LogInformation("Show in folder: {Folder} — {Outcome}", folder,
                        inFront ? "brought to the front" : "opened, but Windows kept it behind the current window");
                    return;
                }
                Thread.Sleep(100);
            }
            logger.LogWarning("Show in folder: no Explorer window appeared for {Folder}", folder);
        }
        catch (Exception ex)
        {
            // Focus is a nicety — Explorer is already open. An unhandled exception on this thread
            // would take the whole app down, which is far worse than a window left behind the browser.
            logger.LogWarning(ex, "Show in folder: could not bring Explorer forward for {Folder}", folder);
        }
    }

    /// <summary>Handles of Explorer windows currently showing <paramref name="folder"/>.</summary>
    private static List<IntPtr> WindowsShowing(string folder)
    {
        var found = new List<IntPtr>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return found;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            // Matched on the folder path, not the title — every run's folder is called "video".
            dynamic windows = shell.Windows();
            var target = Path.TrimEndingDirectorySeparator(folder);
            for (var i = 0; i < (int)windows.Count; i++)
            {
                try
                {
                    dynamic window = windows.Item(i);
                    if (window is null) continue;
                    string url = window.LocationURL;
                    if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                    var shown = Path.TrimEndingDirectorySeparator(new Uri(url).LocalPath);
                    if (string.Equals(shown, target, StringComparison.OrdinalIgnoreCase))
                        found.Add(new IntPtr(Convert.ToInt64(window.HWND)));
                }
                catch (COMException) { /* a window closing mid-enumeration */ }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return found;
    }

    private static bool Activate(IntPtr hwnd)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
            && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Runs <paramref name="work"/> on a short-lived STA thread, as Shell.Application requires.</summary>
    private static List<IntPtr> OnStaThread(Func<List<IntPtr>> work)
    {
        var result = new List<IntPtr>();
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception) { /* no snapshot just means any matching window counts as new */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private const int SwRestore = 9;
    private const uint PmNoRemove = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
