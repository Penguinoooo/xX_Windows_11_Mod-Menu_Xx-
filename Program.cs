using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Security;
using System.Security.Cryptography;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace WindowMenu;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (TrayRestoreHost.TryRun(args))
            return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            ErrorReporter.Write(e.Exception, "Application.ThreadException");
            ErrorReporter.ShowFailure("The application encountered an unexpected error.");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                ErrorReporter.Write(exception, "AppDomain.UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorReporter.Write(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        MessageBox.Show(
            "WindowMenu Is Running",
            "WindowMenu",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        Application.Run(new TrayContext());
    }

}

internal static class ErrorReporter
{
    public static void Write(Exception exception, string source)
    {
        try
        {
            var directory = FindErrorLogDirectory();
            Directory.CreateDirectory(directory);

            var timestamp = DateTime.Now;
            var fileName = $"WindowMenu-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md";
            var report = $"""
                # WindowMenu Error Report

                - Timestamp (local): {timestamp:O}
                - Source: {Sanitize(source)}
                - App version: {typeof(Program).Assembly.GetName().Version?.ToString() ?? "(unknown)"}
                - OS: {Sanitize(Environment.OSVersion.VersionString)}
                - Runtime: {Sanitize(Environment.Version.ToString())}

                ## Exception

                - Type: `{Sanitize(exception.GetType().FullName ?? exception.GetType().Name)}`
                - Message: {Sanitize(exception.Message)}

                ```text
                {Sanitize(exception.ToString())}
                ```

                """;

            File.WriteAllText(Path.Combine(directory, fileName), report);
        }
        catch
        {
            // Diagnostics must never replace or interrupt the original failure.
        }
    }

    public static void ShowFailure(string message)
    {
        try
        {
            MessageBox.Show(
                $"{message}\n\nA sanitized report was saved in the Error Logs folder.",
                "WindowMenu Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // The process may be shutting down or the UI may already be unavailable.
        }
    }

    private static string FindErrorLogDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "Error Logs");
            if (Directory.Exists(candidate) || File.Exists(Path.Combine(directory.FullName, "WindowMenu.csproj")))
                return candidate;
            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Error Logs");
    }

    private static string Sanitize(string value)
    {
        var sanitized = value;
        sanitized = ReplaceIfPresent(sanitized, Environment.UserName, "<user>");
        sanitized = ReplaceIfPresent(sanitized, Environment.UserDomainName, "<domain>");
        sanitized = ReplaceIfPresent(
            sanitized,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "<user-profile>");
        sanitized = ReplaceIfPresent(sanitized, AppContext.BaseDirectory, "<app-directory>");

        return sanitized.Replace("```", "'''", StringComparison.Ordinal);
    }

    private static string ReplaceIfPresent(string value, string oldValue, string replacement)
    {
        return string.IsNullOrEmpty(oldValue)
            ? value
            : value.Replace(oldValue, replacement, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TrayRestoreHost
{
    private const string HostSwitch = "--tray-host";

    public static bool TryRun(string[] args)
    {
        if (args.Length != 5 || !string.Equals(args[0], HostSwitch, StringComparison.Ordinal))
            return false;
        if (!long.TryParse(args[1], out var hwndValue) ||
            !uint.TryParse(args[2], out var processId) ||
            string.IsNullOrWhiteSpace(args[3]))
            return true;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayRestoreContext(
            new IntPtr(hwndValue), processId, args[3], args[4]));
        return true;
    }

    public static void Start(IntPtr hwnd, uint processId, string executablePath, string title)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Could not determine the WindowMenu executable path.");

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = typeof(Program).Assembly.Location;
            if (string.IsNullOrEmpty(assemblyPath))
                throw new InvalidOperationException("Could not determine the WindowMenu assembly path.");
            startInfo.ArgumentList.Add(assemblyPath);
        }
        startInfo.ArgumentList.Add(HostSwitch);
        startInfo.ArgumentList.Add(hwnd.ToInt64().ToString());
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add(title);
        Process.Start(startInfo);
    }

    private sealed class TrayRestoreContext : ApplicationContext
    {
        private readonly IntPtr _hwnd;
        private readonly NotifyIcon _trayIcon;
        private readonly Icon? _targetIcon;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _watchTimer;

        public TrayRestoreContext(IntPtr hwnd, uint processId, string executablePath, string title)
        {
            _hwnd = hwnd;
            _targetIcon = TryExtractIcon(executablePath);
            _menu = new ContextMenuStrip();
            var restore = new ToolStripMenuItem("Restore Window");
            restore.Click += (_, _) => Restore();
            var close = new ToolStripMenuItem("Close Tray Item");
            close.Click += (_, _) => ExitThread();
            _menu.Items.Add(restore);
            _menu.Items.Add(close);

            var displayName = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(executablePath)
                : title;
            var applicationName = Path.GetFileNameWithoutExtension(executablePath);
            _menu.Items.Add(new ToolStripMenuItem($"{applicationName} — {displayName}")
            {
                Enabled = false
            });
            _menu.Items.Add(new ToolStripSeparator());
            _trayIcon = new NotifyIcon
            {
                Icon = _targetIcon ?? SystemIcons.Application,
                Text = TruncateTooltip(applicationName + " — " + displayName),
                ContextMenuStrip = _menu,
                Visible = true
            };
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    Restore();
            };

            _watchTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _watchTimer.Tick += (_, _) =>
            {
                if (!IsWindow(_hwnd))
                    ExitThread();
            };
            _watchTimer.Start();
        }

        private void Restore()
        {
            if (IsWindow(_hwnd))
            {
                ShowWindow(_hwnd, SW_RESTORE);
                SetForegroundWindow(_hwnd);
            }
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            _watchTimer.Stop();
            _watchTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _menu.Dispose();
            _targetIcon?.Dispose();
            base.ExitThreadCore();
        }

        private static Icon? TryExtractIcon(string executablePath)
        {
            try
            {
                using var extracted = Icon.ExtractAssociatedIcon(executablePath);
                return extracted?.Clone() as Icon;
            }
            catch
            {
                return null;
            }
        }

        private static string TruncateTooltip(string value)
        {
            var clean = string.IsNullOrWhiteSpace(value) ? "Minimized window" : value.Trim();
            return clean.Length <= 63 ? clean : clean[..60] + "...";
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
    }
}

// ----------------------------------------------------------------------------
// Runs invisibly. Owns the low-level mouse hook and the tray icon for
// THIS app (not per-target-window tray icons — those live in MinimizeToTray).
// ----------------------------------------------------------------------------
internal sealed class TrayContext : ApplicationContext
{
    private readonly MouseHook _hook;
    private readonly NotifyIcon _selfTray;
    private readonly Control _invoker;
    private readonly System.Windows.Forms.Timer _openingPositionTimer;

    // Track the currently-open top-level menu so global clicks can close it.
    private ContextMenuStrip? _openMenu;
    private readonly object _menuLock = new object();

    public TrayContext()
    {
        WindowMenuSettings.Load();
        _hook = new MouseHook();
        _hook.AltRightClickOnCorner += OnAltRightClickOnCorner;
        _hook.GlobalMouseDown += OnGlobalMouseDown;
        _hook.Start();

        // Create a hidden control on the UI thread to marshal calls to the UI.
        _invoker = new Control();
        _invoker.CreateControl();

        _selfTray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "WindowMenu - Performance Monitor and Voice Access placement"
        };
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        var ctx = new ContextMenuStrip();
        var voiceAccessItem = new ToolStripMenuItem("Move Voice Access to Secondary Monitor...");
        voiceAccessItem.Click += (_, _) =>
        {
            try
            {
                WindowActions.ShowVoiceAccessWindowPicker();
            }
            catch (Exception ex)
            {
                ErrorReporter.Write(ex, "Move Voice Access to Secondary Monitor");
                ErrorReporter.ShowFailure("Voice Access could not be moved.");
            }
        };
        var performanceItem = new ToolStripMenuItem("Performance Monitor...");
        performanceItem.Click += (_, _) =>
        {
            try
            {
                WindowActions.ShowPerformanceWindowPicker();
            }
            catch (Exception ex)
            {
                ErrorReporter.Write(ex, "Open Performance Monitor");
                ErrorReporter.ShowFailure("The Performance Monitor could not be opened.");
            }
        };
        var thumbnailPreviewItem = new ToolStripMenuItem("Window Thumbnail Preview...");
        thumbnailPreviewItem.Click += (_, _) =>
        {
            try
            {
                WindowActions.ShowWindowThumbnailPreview();
            }
            catch (Exception ex)
            {
                ErrorReporter.Write(ex, "Open Window Thumbnail Preview");
                ErrorReporter.ShowFailure("Window Thumbnail Preview could not be opened.");
            }
        };
        ctx.Items.Add(voiceAccessItem);
        ctx.Items.Add(performanceItem);
        ctx.Items.Add(thumbnailPreviewItem);
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add(exitItem);
        _selfTray.ContextMenuStrip = ctx;

        _openingPositionTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _openingPositionTimer.Tick += (_, _) => WindowActions.ApplySavedOpeningPositions();
        _openingPositionTimer.Start();
    }

    private void OnAltRightClickOnCorner(object? sender, TargetWindowState target)
    {
        // MouseHook raises this on the hook thread. Marshal to the UI thread
        // using the hidden invoker Control which was created on the UI thread.
        void show()
        {
            var menu = WindowActionMenu.Build(target);

            lock (_menuLock)
            {
                // Close any previously-open menu if still around.
                if (_openMenu != null && !_openMenu.IsDisposed)
                {
                    try { _openMenu.Close(); } catch { }
                    DisposeMenuLater(_openMenu);
                }
                _openMenu = menu;

                // Dispose only after WinForms finishes processing the close/click
                // sequence. Disposing immediately inside the Closed event can race
                // with the current item click and throw ObjectDisposedException.
                menu.Closed += (_, _) =>
                {
                    try
                    {
                        lock (_menuLock)
                        {
                            if (_openMenu == menu) _openMenu = null;
                        }
                        DisposeMenuLater(menu);
                    }
                    catch { }
                };
            }

            menu.Show(Cursor.Position);
        }

        if (_invoker != null && _invoker.IsHandleCreated && _invoker.InvokeRequired)
        {
            _invoker.BeginInvoke((MethodInvoker)show);
        }
        else
        {
            show();
        }
    }

    private void DisposeMenuLater(ContextMenuStrip menu)
    {
        if (menu.IsDisposed) return;

        try
        {
            if (_invoker != null && _invoker.IsHandleCreated)
            {
                _invoker.BeginInvoke((MethodInvoker)(() =>
                {
                    try { if (!menu.IsDisposed) menu.Dispose(); } catch { }
                }));
                return;
            }
        }
        catch { }

        try { if (!menu.IsDisposed) menu.Dispose(); } catch { }
    }

    // Close top-level menu when the user clicks anywhere outside the menu or its
    // submenus. This is invoked from the hook thread; marshal to UI thread.
    private void OnGlobalMouseDown(object? sender, MouseHook.RawMouseEventArgs e)
    {
        lock (_menuLock)
        {
            if (_openMenu == null) return;
        }

        if (_invoker != null && _invoker.IsHandleCreated && _invoker.InvokeRequired)
        {
            // Forward to UI thread
            _invoker.BeginInvoke((MethodInvoker)(() => HandleGlobalMouseDown(e)));
        }
        else
        {
            HandleGlobalMouseDown(e);
        }
    }

    private void HandleGlobalMouseDown(MouseHook.RawMouseEventArgs e)
    {
        lock (_menuLock)
        {
            if (_openMenu == null) return;
            try
            {
                var pt = new System.Drawing.Point(e.Pt.X, e.Pt.Y);
                if (IsPointInsideMenuOrSubmenus(_openMenu, pt))
                {
                    // Click was inside the menu or a submenu — do nothing.
                    return;
                }

                // Otherwise close the menu. Delay the actual Dispose so we do not
                // race with the active WinForms click/close sequence.
                try { _openMenu.Close(); } catch { }
                var menu = _openMenu;
                _openMenu = null;
                DisposeMenuLater(menu);
            }
            catch { }
        }
    }

    private bool IsPointInsideMenuOrSubmenus(ContextMenuStrip menu, System.Drawing.Point pt)
    {
        try
        {
            if (menu.Bounds.Contains(pt)) return true;
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripMenuItem tmi)
                {
                    var dd = tmi.DropDown;
                    if (dd != null && dd.Visible && dd.Bounds.Contains(pt)) return true;
                    // Check nested dropdowns recursively
                    if (IsPointInsideDropDownRecursive(tmi, pt)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private bool IsPointInsideDropDownRecursive(ToolStripMenuItem item, System.Drawing.Point pt)
    {
        try
        {
            var dd = item.DropDown;
            if (dd != null && dd.Visible && dd.Bounds.Contains(pt)) return true;
            foreach (ToolStripItem sub in item.DropDownItems)
            {
                if (sub is ToolStripMenuItem subTmi)
                {
                    if (IsPointInsideDropDownRecursive(subTmi, pt)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private void ExitApp()
    {
        _hook.Stop();
        _openingPositionTimer.Stop();
        _openingPositionTimer.Dispose();
        _selfTray.Visible = false;
        Application.Exit();
    }
}

// ----------------------------------------------------------------------------
// Info about the window the user Alt+RightClicked on. Passed into every
// menu action. Add fields here (not new globals) if a function needs more.
// ----------------------------------------------------------------------------
internal sealed class TargetWindowState
{
    public required IntPtr Hwnd { get; init; }
    public required uint ProcessId { get; init; }
    public bool IsTopMost { get; set; }
    public bool IsHiddenFromTaskbar { get; set; }
}

// ----------------------------------------------------------------------------
// Low-level global mouse hook. Detects: Alt held + Right mouse button down +
// cursor is over the top-right "hot corner" of some top-level window.
// ----------------------------------------------------------------------------
internal sealed class MouseHook
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int VK_MENU = 0x12; // Alt key
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;

    // Raised for any global mouse-down so the tray context can close open
    // menus when the user clicks anywhere outside them.
    public event EventHandler<RawMouseEventArgs>? GlobalMouseDown;

    public class RawMouseEventArgs : EventArgs
    {
        public POINT Pt { get; set; }
        public int Message { get; set; }
    }

    // Size of the hot corner, in pixels, measured from the window's top-right.
    private const int HotCornerWidth = 140;
    private const int HotCornerHeight = 40;

    private LowLevelMouseProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public event EventHandler<TargetWindowState>? AltRightClickOnCorner;

    public void Start()
    {
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var isConfiguredButton = WindowMenuSettings.Current.TriggerMouseButton switch
            {
                "Left" => message == WM_LBUTTONDOWN,
                "Middle" => message == WM_MBUTTONDOWN,
                _ => message == WM_RBUTTONDOWN
            };

            if (isConfiguredButton && IsConfiguredModifierDown())
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    TryFireIfOnHotCorner(hookStruct.pt);
                }
                catch { }
            }
            else if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN ||
                     message == WM_MBUTTONDOWN)
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    GlobalMouseDown?.Invoke(this, new RawMouseEventArgs
                    {
                        Pt = hookStruct.pt,
                        Message = message
                    });
                }

                catch { }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsConfiguredModifierDown()
    {
        return WindowMenuSettings.Current.TriggerModifier switch
        {
            "Control" => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
            "Shift" => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0,
            "None" => true,
            _ => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
        };
    }

    private void TryFireIfOnHotCorner(POINT screenPt)
    {
        IntPtr hwnd = WindowFromPoint(screenPt);
        if (hwnd == IntPtr.Zero) return;

        // Walk up to the top-level (root) window — WindowFromPoint can return
        // a child control.
        hwnd = GetAncestor(hwnd, GA_ROOT);
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out RECT r)) return;

        bool inHotCorner =
            screenPt.X >= r.Left && screenPt.X <= r.Left + HotCornerWidth &&
            screenPt.Y >= r.Top && screenPt.Y <= r.Top + HotCornerHeight;

        if (!inHotCorner) return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        var state = new TargetWindowState { Hwnd = hwnd, ProcessId = pid };
        AltRightClickOnCorner?.Invoke(this, state);
    }

    // ---- P/Invoke plumbing ----
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

// ----------------------------------------------------------------------------
// Builds the popup menu shown at the cursor. Each item calls into
// WindowActions.*. Add new items here + a matching method in WindowActions.
// ----------------------------------------------------------------------------
internal static class WindowActionMenu
{
    public static ContextMenuStrip Build(TargetWindowState target)
    {
        var menu = new ContextMenuStrip();
        var settings = WindowMenuSettings.Load();
        // Ensure the menu auto-closes when the user clicks elsewhere.
        menu.AutoClose = true;
        // The TrayContext owns disposal of the menu to avoid races — do NOT
        // dispose here (other threads/hooks may still reference the menu).

        menu.Items.Add(Toggle(
            WindowActions.IsTopMost(target) ? "Unpin From Top" : "Always on top (Pin To Top)",
            () => WindowActions.ToggleAlwaysOnTop(target)));

        menu.Items.Add(Simple(
            WindowActions.IsMuted(target) ? "Unmute Window" : "Mute Window",
            () => WindowActions.MuteWindow(target)));

        // Window Properties is a submenu (opens on hover) so build it as a
        // ToolStripMenuItem with dynamically populated DropDownItems.
        menu.Items.Add(BuildWindowPropertiesSubmenu(target));
        menu.Items.Add(Simple("Performance",
            () => WindowActions.ShowPerformance(target)));
        menu.Items.Add(Simple("Window Thumbnail Preview...",
            () => WindowActions.ShowWindowThumbnailPreview()));
        menu.Items.Add(Simple("Move Voice Access to Secondary Monitor...",
            () => WindowActions.ShowVoiceAccessWindowPicker()));

        menu.Items.Add(BuildCpuPrioritySubmenu(target));

        menu.Items.Add(Simple(
            WindowLockManager.IsLocked(target.Hwnd)
                ? "Allow Window Closing"
                : "Prevent Window Closing (Best Effort)",
            () => WindowActions.PreventWindowClosing(target)));

        menu.Items.Add(Simple("Freeze the Focus",
            () => WindowActions.FreezeFocus(target)));

        if (settings.ShowSuspend)
            menu.Items.Add(Simple(
                WindowActions.IsSuspended(target.ProcessId) ? "Unsuspend Window" : "Suspend Window",
                () => WindowActions.ToggleSuspend(target)));

        menu.Items.Add(Simple("Minimize to tray",
            () => WindowActions.MinimizeToTray(target)));

        menu.Items.Add(Toggle("Remove Taskbar",
            () => WindowActions.ToggleTaskbarVisibility(target)));

        menu.Items.Add(Simple("Title Rename",
            () => WindowActions.RenameTitle(target)));

        menu.Items.Add(Simple("Save Opening Position",
            () => WindowActions.SaveOpeningPosition(target)));

        menu.Items.Add(Simple("Close And Restart as Admin",
            () => WindowActions.CloseAndRestartAsAdmin(target)));

        menu.Items.Add(Simple("Restart In Debugger",
            () => WindowActions.RestartInDebugger(target)));

        menu.Items.Add(BuildForceQuitSubmenu(target));

        if (settings.ShowNetwork)
            menu.Items.Add(BuildNetworkSubmenu(target));
        if (settings.ShowProcessActivity)
            menu.Items.Add(Simple("View Process Activity",
                    () => WindowActions.ViewProcessActivity(target)));
        if (settings.ShowExternalTools)
            menu.Items.Add(BuildExternalToolsSubmenu(target));
        menu.Items.Add(Simple("Process Unlocker",
            () => WindowActions.OpenProcessUnlocker(target)));
        menu.Items.Add(Simple("Decompiler",
            () => WindowActions.OpenDecompiler(target)));
        menu.Items.Add(Simple("Launch Isolated Instance",
            () => WindowActions.LaunchIsolatedInstance(target)));
        menu.Items.Add(BuildDrCloakySubmenu(target));

        if (settings.ShowWhenClosed)
            menu.Items.Add(BuildWhenClosedSubmenu(target));
        menu.Items.Add(Simple("Settings", () => WindowMenuSettings.ShowWindow()));

        return menu;
    }

    private static ToolStripMenuItem Simple(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => RunAction(text, action);
        return item;
    }

    private static ToolStripMenuItem Toggle(string text, Action action) => Simple(text, action);

    private static void RunAction(string actionName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorReporter.Write(ex, $"Menu action: {actionName}");
            ErrorReporter.ShowFailure($"The menu action '{actionName}' failed.");
        }
    }

    private static ToolStripMenuItem BuildCpuPrioritySubmenu(TargetWindowState target)
    {
        var root = new ToolStripMenuItem("CPU Priority");
        foreach (var level in new[]
                 {
                     ProcessPriorityClass.Idle,
                     ProcessPriorityClass.BelowNormal,
                     ProcessPriorityClass.Normal,
                     ProcessPriorityClass.AboveNormal,
                     ProcessPriorityClass.High,
                     ProcessPriorityClass.RealTime
                 })
        {
            var item = new ToolStripMenuItem(level.ToString());
            item.Click += (_, _) => RunAction($"CPU Priority: {level}",
                () => WindowActions.SetCpuPriority(target, level));
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private static ToolStripMenuItem BuildWhenClosedSubmenu(TargetWindowState target)
    {
        // Implemented — see WindowCloseWatcher. Picking an option arms it for
        // this hwnd; fires once when the window is destroyed, then disarms.
        var root = new ToolStripMenuItem("When Closed");
        string[] options =
        {
            "ShutDown PC", "Put PC To Sleep", "Play A Sound",
            "Execute A Command", "Reopen Application"
        };
        foreach (var opt in options)
        {
            var item = new ToolStripMenuItem(opt);
            item.Click += (_, _) => RunAction($"When Closed: {opt}",
                () => WindowActions.RegisterWhenClosedAction(target, opt));
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private static ToolStripMenuItem BuildForceQuitSubmenu(TargetWindowState target)
    {
        var root = new ToolStripMenuItem("Quit Forceably");
        root.DropDownItems.Add(Simple("Quit Forceably (This Window)",
            () => WindowActions.QuitWindow(target)));
        root.DropDownItems.Add(Simple("Quit Forceably (Every Window)",
            () => WindowActions.QuitEveryWindow(target)));
        root.DropDownItems.Add(Simple("This Window And Restart as Admin",
            () => WindowActions.CloseAndRestartAsAdmin(target)));
        root.DropDownItems.Add(Simple("Every Window And Restart as Admin",
            () => WindowActions.CloseAllAndRestartAsAdmin(target)));
        root.DropDownItems.Add(Simple("Every Window And Uninstall",
            () => WindowActions.ForciblyQuitAndUninstall(target)));
        return root;
    }

    private static ToolStripMenuItem BuildNetworkSubmenu(TargetWindowState target)
    {
        var root = new ToolStripMenuItem("Network");
        root.DropDownItems.Add(Simple("Show Active Ports",
            () => WindowActions.ShowNetworkActivity(target, WindowActions.NetworkActivityView.Ports)));
        root.DropDownItems.Add(Simple("Show Active Connections",
            () => WindowActions.ShowNetworkActivity(target, WindowActions.NetworkActivityView.Connections)));
        root.DropDownItems.Add(Simple("Listen in Wireshark",
            () => WindowActions.ListenInWireshark(target)));
        root.DropDownItems.Add(Toggle(
            WindowActions.IsInternetBlocked(target) ? "Allow Internet Access" : "Block Internet Access",
            () => WindowActions.ToggleInternetAccess(target)));
        return root;
    }

    private static ToolStripMenuItem BuildExternalToolsSubmenu(TargetWindowState target)
    {
        var root = new ToolStripMenuItem("External Tools");

        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "exe");
        if (Directory.Exists(toolsDirectory))
        {
            foreach (var toolPath in Directory.EnumerateFiles(toolsDirectory, "*.exe")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(toolPath);
                if (string.Equals(name, "processactivityview", StringComparison.OrdinalIgnoreCase))
                    continue;

                root.DropDownItems.Add(Simple(name,
                    () => WindowActions.LaunchExternalTool(toolPath, target)));
            }
        }

        return root;
    }

    private static ToolStripMenuItem BuildDrCloakySubmenu(TargetWindowState target)
    {
        var root = new ToolStripMenuItem("DrCloaky");
        root.DropDownItems.Add(Simple(
            WindowCloakManager.IsCaptureExcluded(target.Hwnd)
                ? "Show Window In Screen Capture"
                : "Hide Window From Screen Capture",
            () => WindowCloakManager.ToggleCaptureExclusion(target.Hwnd)));
        root.DropDownItems.Add(Simple("Hide/Show Window From Taskbar",
            () => WindowActions.ToggleTaskbarVisibility(target)));
        root.DropDownItems.Add(Simple("Hide/Show Window From Alt + Tab",
            () => WindowCloakManager.ToggleAltTabVisibility(target.Hwnd)));
        return root;
    }

    private static ToolStripMenuItem BuildWindowPropertiesSubmenu(TargetWindowState target)
    {
        var root = new ToolStripMenuItem("Window Properties");

        root.DropDownOpening += (_, _) =>
        {
            try
            {
                root.DropDownItems.Clear();
                var props = WindowActions.GetWindowProperties(target);
                if (props.Count == 0) return;

                // first entry is the header executable name
                var header = new ToolStripMenuItem(string.IsNullOrEmpty(props[0].Value) ? "(Unknown)" : props[0].Value)
                { Enabled = false };
                header.Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, 9F, System.Drawing.FontStyle.Bold);
                root.DropDownItems.Add(header);

                for (int i = 1; i < props.Count; i++)
                {
                    var kv = props[i];
                    root.DropDownItems.Add(new ToolStripMenuItem($"{kv.Key} = {kv.Value}") { Enabled = false });
                }

                var copyItem = new ToolStripMenuItem("Copy All to Clipboard");
                copyItem.Click += (_, _) =>
                {
                    try
                    {
                        var sb = new StringBuilder();
                        foreach (var kv in props)
                        {
                            if (kv.Key == "__header") sb.AppendLine(kv.Value);
                            else sb.AppendLine($"{kv.Key} = {kv.Value}");
                        }
                        Clipboard.SetText(sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        ErrorReporter.Write(ex, "Copy Window Properties");
                        ErrorReporter.ShowFailure("Window properties could not be copied.");
                    }
                };
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(copyItem);
            }
            catch (Exception ex)
            {
                ErrorReporter.Write(ex, "Window Properties submenu");
            }
        };

        return root;
    }
}


// ----------------------------------------------------------------------------
// The actual actions. EASY ones are fully implemented. HARD ones are stubs —
// each stub explains the approach so the next AI can implement it without
// re-deriving the design.
// ----------------------------------------------------------------------------
internal static class WindowActions
{
    private static readonly HashSet<uint> SuspendedProcesses = new();

    internal enum NetworkActivityView
    {
        Ports,
        Connections
    }

    internal sealed record NetworkRow(string Protocol, string Local, string Remote, string State);

    public static List<KeyValuePair<string,string>> GetWindowProperties(TargetWindowState target)
    {
        var list = new List<KeyValuePair<string,string>>();
        string build = "(Unknown)";
        string className = "(Unknown)";
        string doc = "(Unknown or N/A)";
        string folder = "";
        string executable = "";
        string handle = "";
        string pid = target.ProcessId.ToString();
        string runtime = "(Unknown)";
        string size = "(Unknown)";
        string title = "(Unknown)";
        string uniqueId = "(Unknown)";
        string where = "(Unknown)";

        try
        {
            var sb = new StringBuilder(256);
            if (GetClassName(target.Hwnd, sb, sb.Capacity) > 0)
                className = sb.ToString();

            int len = GetWindowTextLength(target.Hwnd);
            if (len > 0)
            {
                var tb = new StringBuilder(len + 1);
                if (GetWindowText(target.Hwnd, tb, tb.Capacity) > 0)
                    title = tb.ToString();
            }

            if (GetWindowRect(target.Hwnd, out RECT r))
            {
                size = $"{r.Right - r.Left} wide (w), {r.Bottom - r.Top} high (h)";
                where = $"{r.Left} left (x), {r.Top} top (y)";
                handle = $"{target.Hwnd.ToInt64()} / 0x{target.Hwnd.ToInt64():X}";
            }

            try
            {
                using var proc = Process.GetProcessById((int)target.ProcessId);
                try
                {
                    var path = proc.MainModule?.FileName ?? string.Empty;
                    executable = Path.GetFileName(path);
                    folder = Path.GetDirectoryName(path) ?? string.Empty;
                    build = "Win32 executable";

                    try
                    {
                        foreach (ProcessModule mod in proc.Modules)
                        {
                            var nm = mod.ModuleName;
                            if (string.Equals(nm, "coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(nm, "clr.dll", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(nm, "mscoree.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                build = ".NET";
                                break;
                            }
                        }
                    }
                    catch { }
                }
                catch { }

                try
                {
                    var ts = DateTime.Now - proc.StartTime;
                    runtime = $"{ts.Days}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
                }
                catch { }

                uniqueId = ((uint)target.ProcessId << 16 ^ (uint)target.Hwnd.ToInt32()).ToString("X8");
            }
            catch { }
        }
        catch { }

        list.Add(new KeyValuePair<string,string>("__header", string.IsNullOrEmpty(executable) ? "(Unknown)" : executable));
        list.Add(new KeyValuePair<string,string>("Build", build));
        list.Add(new KeyValuePair<string,string>("Class", className));
        list.Add(new KeyValuePair<string,string>("Doc", doc));
        list.Add(new KeyValuePair<string,string>("Folder", string.IsNullOrEmpty(folder) ? "(Unknown)" : folder));
        list.Add(new KeyValuePair<string,string>("Executable", string.IsNullOrEmpty(executable) ? "(Unknown)" : executable));
        list.Add(new KeyValuePair<string,string>("Handle (hWnd)", handle));
        list.Add(new KeyValuePair<string,string>("Process ID (PID)", pid));
        list.Add(new KeyValuePair<string,string>("Runtime", runtime));
        list.Add(new KeyValuePair<string,string>("Size", size));
        list.Add(new KeyValuePair<string,string>("Title", string.IsNullOrEmpty(title) ? "(Unknown)" : title));
        list.Add(new KeyValuePair<string,string>("Unique ID", uniqueId));
        list.Add(new KeyValuePair<string,string>("Where", where));

        return list;
    }

    // ---- EASY: DONE ----

    public static void ToggleAlwaysOnTop(TargetWindowState target)
    {
        target.IsTopMost = !IsTopMost(target);
        IntPtr insertAfter = target.IsTopMost ? HWND_TOPMOST : HWND_NOTOPMOST;
        SetWindowPos(target.Hwnd, insertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE);
    }

    public static bool IsTopMost(TargetWindowState target)
    {
        return (GetWindowLong(target.Hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    public static void ToggleTaskbarVisibility(TargetWindowState target)
    {
        target.IsHiddenFromTaskbar = !target.IsHiddenFromTaskbar;
        int exStyle = GetWindowLong(target.Hwnd, GWL_EXSTYLE);
        if (target.IsHiddenFromTaskbar)
            exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        else
            exStyle = (exStyle & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
        SetWindowLong(target.Hwnd, GWL_EXSTYLE, exStyle);

        // Re-show so Explorer refreshes the taskbar entry.
        ShowWindow(target.Hwnd, SW_HIDE);
        ShowWindow(target.Hwnd, SW_SHOW);
    }

    public static void RenameTitle(TargetWindowState target)
    {
        using var prompt = new RenamePrompt();
        if (prompt.ShowDialog() == DialogResult.OK)
            SetWindowText(target.Hwnd, prompt.NewTitle);
    }

    public static void QuitForcibly(TargetWindowState target)
    {
        try
        {
            using var proc = Process.GetProcessById((int)target.ProcessId);
            proc.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Process already gone — nothing to do.
        }
    }

    public static void QuitEveryWindow(TargetWindowState target)
    {
        var exePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Could not determine the target executable path.");
            return;
        }
        KillAllProcessesForExecutable(exePath);
    }

    public static void QuitWindow(TargetWindowState target)
    {
        if (!PostMessage(target.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void CloseAllAndRestartAsAdmin(TargetWindowState target)
    {
        var exePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Could not find the target executable path.");
            return;
        }

        KillAllProcessesForExecutable(exePath);
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    public static void ToggleSuspend(TargetWindowState target)
    {
        using var process = Process.GetProcessById((int)target.ProcessId);
        if (IsSuspended(target.ProcessId))
        {
            var status = NtResumeProcess(process.Handle);
            if (status != 0) throw new Win32Exception(status);
            SuspendedProcesses.Remove(target.ProcessId);
        }
        else
        {
            var status = NtSuspendProcess(process.Handle);
            if (status != 0) throw new Win32Exception(status);
            SuspendedProcesses.Add(target.ProcessId);
        }
    }

    public static bool IsSuspended(uint processId) => SuspendedProcesses.Contains(processId);

    public static void ViewProcessActivity(TargetWindowState target)
    {
        new ProcessActivityForm(target.ProcessId).Show();
    }

    public static void ShowPerformance(TargetWindowState target)
    {
        new ProcessPerformanceForm(target.ProcessId).Show();
    }

    public static void ShowPerformanceWindowPicker()
    {
        using var picker = new WindowTargetPickerForm(
            "Performance Monitor",
            "Select any window/process to open its live Performance view.",
            Screen.AllScreens,
            moveToMonitor: false);
        if (picker.ShowDialog() == DialogResult.OK && picker.SelectedWindow is { } selected)
            ShowPerformance(new TargetWindowState { Hwnd = selected.Handle, ProcessId = selected.ProcessId });
    }

    public static void ShowWindowThumbnailPreview()
    {
        using var preview = new WindowTargetPickerForm(
            "Window Thumbnail Preview",
            "Select a window to see its live thumbnail. Use this to identify the correct Voice Access window before moving it.",
            Screen.AllScreens,
            moveToMonitor: false,
            previewOnly: true);
        preview.ShowDialog();
    }

    public static void ShowVoiceAccessWindowPicker()
    {
        var screens = Screen.AllScreens.Where(screen => !screen.Primary).ToArray();
        if (screens.Length == 0)
        {
            MessageBox.Show("A secondary monitor was not detected.", "Voice Access");
            return;
        }

        using var picker = new WindowTargetPickerForm(
            "Move Voice Access",
            "Select the Voice Access toolbar from all visible windows. Likely matches are highlighted. If direct positioning is ignored, WindowMenu uses Windows+Shift+Arrow, which briefly activates the selected window.",
            screens,
            moveToMonitor: true);
        if (picker.ShowDialog() != DialogResult.OK ||
            picker.SelectedWindow is not { } selected ||
            picker.SelectedScreen is not { } screen)
            return;

        MoveWindowToScreen(selected.Handle, screen);
        VoiceAccessPlacementManager.KeepOnScreen(selected.Handle, screen);
    }

    public static void ShowNetworkActivity(TargetWindowState target, NetworkActivityView view)
    {
        var form = new NetworkActivityForm(target.ProcessId, view);
        form.Show();
    }

    public static void LaunchExternalTool(string toolPath, TargetWindowState target)
    {
        Process.Start(new ProcessStartInfo(toolPath)
        {
            UseShellExecute = true,
            Arguments = target.ProcessId.ToString()
        });
    }

    public static void ListenInWireshark(TargetWindowState target)
    {
        var wireshark = FindInstalledExecutable("wireshark.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "Wireshark.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wireshark", "Wireshark.exe"));
        if (wireshark == null)
        {
            MessageBox.Show("Wireshark was not found. Install Wireshark or add wireshark.exe to PATH.", "Wireshark");
            return;
        }

        Process.Start(new ProcessStartInfo(wireshark) { UseShellExecute = true, Arguments = "-k" });
        MessageBox.Show(
            $"Wireshark is listening. Select the interface carrying traffic for PID {target.ProcessId}; " +
            "WindowMenu cannot infer a capture interface from a process ID.",
            "Wireshark");
    }

    public static void OpenProcessUnlocker(TargetWindowState target)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select a locked file or folder",
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        var unlocker = FindInstalledExecutable("LockHunter.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LockHunter", "LockHunter.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LockHunter", "LockHunter.exe"));
        if (unlocker == null)
        {
            MessageBox.Show("LockHunter was not found. Install it or add LockHunter.exe to PATH.", "Process Unlocker");
            return;
        }

        Process.Start(new ProcessStartInfo(unlocker)
        {
            UseShellExecute = true,
            Arguments = $"\"{dialog.FileName}\""
        });
    }

    public static void OpenDecompiler(TargetWindowState target)
    {
        var executablePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(executablePath))
        {
            MessageBox.Show("Could not determine the target executable path.", "Decompiler");
            return;
        }

        var decompiler = FindInstalledExecutable("ILSpy.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ILSpy", "ILSpy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ILSpy", "ILSpy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dnSpy", "dnSpy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dnSpy", "dnSpy.exe"));
        if (decompiler == null)
        {
            MessageBox.Show("ILSpy or dnSpy was not found. Install one or add its executable to PATH.", "Decompiler");
            return;
        }

        Process.Start(new ProcessStartInfo(decompiler)
        {
            UseShellExecute = true,
            Arguments = $"\"{executablePath}\""
        });
    }

    public static void LaunchIsolatedInstance(TargetWindowState target)
    {
        var executablePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(executablePath))
        {
            MessageBox.Show("Could not determine the target executable path.", "Isolated Instance");
            return;
        }

        var sandbox = FindInstalledExecutable("WindowsSandbox.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsSandbox.exe"));
        if (sandbox == null)
        {
            MessageBox.Show(
                "Windows Sandbox is not enabled. Enable the optional Windows Sandbox feature and try again. " +
                "A normal second process is not presented as isolated.",
                "Isolated Instance");
            return;
        }

        var targetDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        var sandboxPath = Path.Combine(Path.GetTempPath(), $"WindowMenu-{Guid.NewGuid():N}.wsb");
        var escapedDirectory = SecurityElement.Escape(targetDirectory);
        var sandboxExecutable = "C:\\WindowMenuTarget\\" + Path.GetFileName(executablePath);
        var configuration = $"""
            <Configuration>
              <MappedFolders>
                <MappedFolder>
                  <HostFolder>{escapedDirectory}</HostFolder>
                  <SandboxFolder>C:\WindowMenuTarget</SandboxFolder>
                  <ReadOnly>true</ReadOnly>
                </MappedFolder>
              </MappedFolders>
              <LogonCommand>
                <Command>cmd.exe /c start "" "{sandboxExecutable}"</Command>
              </LogonCommand>
            </Configuration>
            """;
        File.WriteAllText(sandboxPath, configuration);
        Process.Start(new ProcessStartInfo(sandbox, $"\"{sandboxPath}\"") { UseShellExecute = true });
    }

    private static string? FindInstalledExecutable(string fileName, params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void ShowActivePorts(TargetWindowState target)
    {
        var rows = GetNetstatRows(target.ProcessId);
        var text = rows.Count == 0
            ? "No active TCP or UDP ports were found for this process."
            : string.Join(Environment.NewLine, rows.Select(row => $"{row.Protocol}: {row.Local} -> {row.Remote} {row.State}"));
        MessageBox.Show(text, "Active Ports");
    }

    public static void ShowActiveConnections(TargetWindowState target)
    {
        var rows = GetNetstatRows(target.ProcessId)
            .Where(row => !string.Equals(row.Remote, "*:*", StringComparison.Ordinal))
            .ToList();
        var text = rows.Count == 0
            ? "No active network connections were found for this process."
            : string.Join(Environment.NewLine, rows.Select(row => $"{row.Local} -> {row.Remote} {row.State}"));
        MessageBox.Show(text, "Active Connections");
    }

    public static bool IsInternetBlocked(TargetWindowState target)
    {
        var exePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(exePath)) return false;
        var ruleName = GetFirewallRuleName(exePath);
        var output = RunNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"");
        return output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    public static void SaveOpeningPosition(TargetWindowState target)
    {
        if (!GetWindowRect(target.Hwnd, out RECT rect))
        {
            MessageBox.Show("Could not read the window rectangle.");
            return;
        }

        var exePath = GetExecutablePathForTarget(target) ?? $"pid:{target.ProcessId}";
        var savedMap = LoadSavedOpeningPositions();
        var key = GetSavedPositionKey(exePath);
        savedMap[key] = new SavedOpeningPosition
        {
            ExecutablePath = exePath,
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
            SavedUtc = DateTime.UtcNow
        };

        var file = GetSavedPositionsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(savedMap, new JsonSerializerOptions { WriteIndented = true }));

        var label = Path.GetFileNameWithoutExtension(exePath);
        MessageBox.Show($"Saved opening position for '{label}'.");
    }

    public static void ApplySavedOpeningPositions()
    {
        var saved = LoadSavedOpeningPositions();
        if (saved.Count == 0) return;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var processId);
            if (AppliedOpeningPositionProcesses.Contains(processId)) return true;

            var exePath = GetExecutablePathForTarget(new TargetWindowState
            {
                Hwnd = hwnd,
                ProcessId = processId
            });
            if (string.IsNullOrEmpty(exePath)) return true;

            var key = GetSavedPositionKey(exePath);
            if (!saved.TryGetValue(key, out var position)) return true;

            SetWindowPos(hwnd, IntPtr.Zero, position.Left, position.Top,
                position.Width, position.Height, SWP_NOZORDER);
            AppliedOpeningPositionProcesses.Add(processId);
            return true;
        }, IntPtr.Zero);
    }

    public static List<WindowTargetInfo> GetVisibleWindowTargets()
    {
        var results = new List<WindowTargetInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return true;

            var title = GetWindowTitle(hwnd);
            var processName = $"PID {processId}";
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch
            {
                // Retain the PID label if the process exits during enumeration.
            }

            if (!GetWindowRect(hwnd, out var bounds))
                results.Add(new WindowTargetInfo(hwnd, processId, title, processName, Rectangle.Empty));
            else
                results.Add(new WindowTargetInfo(hwnd, processId, title, processName,
                    Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)));
            return true;
        }, IntPtr.Zero);

        return results
            .OrderByDescending(window => window.LooksLikeVoiceAccess)
            .ThenBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void MoveWindowToScreen(IntPtr hwnd, Screen screen)
    {
        if (!IsWindow(hwnd))
            throw new InvalidOperationException("The selected window no longer exists.");
        if (!GetWindowRect(hwnd, out var windowRect))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var desiredCenter = new Point(
            screen.WorkingArea.Left + screen.WorkingArea.Width / 2,
            screen.WorkingArea.Top + screen.WorkingArea.Height / 2);
        var windowCenter = GetCenter(windowRect);
        if (screen.Bounds.Contains(windowCenter))
            return;

        var width = Math.Min(Math.Max(1, windowRect.Right - windowRect.Left), screen.WorkingArea.Width);
        var height = Math.Min(Math.Max(1, windowRect.Bottom - windowRect.Top), screen.WorkingArea.Height);
        var left = screen.WorkingArea.Left + Math.Max(0, (screen.WorkingArea.Width - width) / 2);
        var top = screen.WorkingArea.Top + Math.Max(0, (screen.WorkingArea.Height - height) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (WaitForWindowOnScreen(hwnd, screen, 250))
            return;

        for (var attempt = 0; attempt < Screen.AllScreens.Length; attempt++)
        {
            if (!GetWindowRect(hwnd, out var attemptRect))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var currentCenter = GetCenter(attemptRect);
            if (screen.Bounds.Contains(currentCenter))
                return;

            MoveUsingWindowsMonitorShortcut(hwnd, currentCenter, desiredCenter);
            if (WaitForWindowOnScreen(hwnd, screen, 300))
                return;
        }

        if (!GetWindowRect(hwnd, out var currentRect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        throw new InvalidOperationException(
            $"Windows did not move the selected window to {screen.DeviceName}. " +
            $"Its current center is ({GetCenter(currentRect).X}, {GetCenter(currentRect).Y}); " +
            $"the selected display bounds are {screen.Bounds}. Try selecting the actual Voice Access toolbar window.");
    }

    private static Point GetCenter(RECT rect) =>
        new(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);

    private static bool WaitForWindowOnScreen(IntPtr hwnd, Screen screen, int timeoutMilliseconds)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            if (!IsWindow(hwnd)) return false;
            if (GetWindowRect(hwnd, out var rect) && screen.Bounds.Contains(GetCenter(rect)))
                return true;
            Thread.Sleep(50);
        } while (timeout.ElapsedMilliseconds < timeoutMilliseconds);
        return false;
    }

    private static void MoveUsingWindowsMonitorShortcut(IntPtr hwnd, Point currentCenter, Point destinationCenter)
    {
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        Thread.Sleep(100);

        ushort direction;
        var dx = destinationCenter.X - currentCenter.X;
        var dy = destinationCenter.Y - currentCenter.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            direction = dx >= 0 ? VK_RIGHT : VK_LEFT;
        else
            direction = dy >= 0 ? VK_DOWN : VK_UP;

        var inputs = new[]
        {
            KeyInput(VK_LWIN, 0),
            KeyInput(VK_SHIFT, 0),
            KeyInput(direction, 0),
            KeyInput(direction, KEYEVENTF_KEYUP),
            KeyInput(VK_SHIFT, KEYEVENTF_KEYUP),
            KeyInput(VK_LWIN, KEYEVENTF_KEYUP)
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not send the Windows+Shift+Arrow monitor-move shortcut.");
    }

    private static Input KeyInput(ushort key, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    public static void CloseAndRestartAsAdmin(TargetWindowState target)
    {
        var exePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Could not find the target executable path.");
            return;
        }

        try { using var proc = Process.GetProcessById((int)target.ProcessId); proc.Kill(entireProcessTree: true); }
        catch (Exception) { }

        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not restart as Administrator:\n{ex.Message}");
        }
    }

    public static void RestartInDebugger(TargetWindowState target)
    {
        var exePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Could not find the target executable path.");
            return;
        }

        try { using var proc = Process.GetProcessById((int)target.ProcessId); proc.Kill(entireProcessTree: true); }
        catch (Exception) { }

        string? debuggerPath = FindVisualStudioDebugger();
        if (string.IsNullOrEmpty(debuggerPath))
        {
            MessageBox.Show("No Visual Studio debugger was found. Install VS with the Desktop development workload.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(debuggerPath)
            {
                UseShellExecute = true,
                Arguments = $"/debugexe \"{exePath}\""
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not launch the debugger:\n{ex.Message}");
        }
    }

    public static void ForciblyQuitAndUninstall(TargetWindowState target)
    {
        try { using var proc = Process.GetProcessById((int)target.ProcessId); proc.Kill(entireProcessTree: true); }
        catch (Exception) { }

        var exePath = GetExecutablePathForTarget(target);
        string? bcuPath = FindBulkCrapUninstaller();
        if (!string.IsNullOrEmpty(bcuPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(bcuPath) { UseShellExecute = true });
                MessageBox.Show("The app was terminated and Bulk Crap Uninstaller was launched.");
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Bulk Crap Uninstaller could not be launched:\n{ex.Message}");
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch { }

        MessageBox.Show("The app was terminated. The app uninstall page has been opened.");
    }

    public static void ToggleInternetAccess(TargetWindowState target)
    {
        var exePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Could not determine the target program path for firewall rules.");
            return;
        }

        var ruleName = GetFirewallRuleName(exePath);
        var show = RunNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"");
        bool exists = !string.IsNullOrWhiteSpace(show) && show.Contains(ruleName, StringComparison.OrdinalIgnoreCase);

        if (exists)
        {
            var deletedOut = RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\" dir=out");
            var deletedIn = RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}_in\" dir=in");
            if (deletedOut || deletedIn)
            {
                MessageBox.Show("Internet access has been restored for this program.");
                return;
            }

            MessageBox.Show("The firewall rule was removed but the system reported no change.");
            return;
        }

        var addedOut = RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block program=\"{exePath}\" enable=yes");
        var addedIn = RunNetsh($"advfirewall firewall add rule name=\"{ruleName}_in\" dir=in action=block program=\"{exePath}\" enable=yes");
        if (addedOut || addedIn)
        {
            MessageBox.Show("Internet access for this program is now blocked.");
            return;
        }

        MessageBox.Show("Firewall rule creation failed. This action usually needs Administrator rights.");
    }

    private static string? GetExecutablePathForTarget(TargetWindowState target)
    {
        try
        {
            using var proc = Process.GetProcessById((int)target.ProcessId);
            return proc.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void KillAllProcessesForExecutable(string executablePath)
    {
        var normalizedPath = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) &&
                    string.Equals(Path.GetFullPath(path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Protected or already-exited processes are not actionable.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string? FindVisualStudioDebugger()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? FindBulkCrapUninstaller()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bulk Crap Uninstaller", "BulkCrapUninstaller.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Bulk Crap Uninstaller", "BulkCrapUninstaller.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Bulk Crap Uninstaller", "BulkCrapUninstaller.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string GetSavedPositionsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowMenu");
        return Path.Combine(dir, "saved-opening-positions.json");
    }

    private static Dictionary<string, SavedOpeningPosition> LoadSavedOpeningPositions()
    {
        var file = GetSavedPositionsFilePath();
        if (!File.Exists(file)) return new Dictionary<string, SavedOpeningPosition>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, SavedOpeningPosition>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, SavedOpeningPosition>>(json)
                ?? new Dictionary<string, SavedOpeningPosition>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, SavedOpeningPosition>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GetSavedPositionKey(string executablePath)
    {
        var normalized = executablePath.Trim();
        return Path.GetFileNameWithoutExtension(normalized) + "::" + normalized;
    }

    private static string GetFirewallRuleName(string executablePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(executablePath.Trim()));
        var stableHash = Convert.ToHexString(digest.AsSpan(0, 8));
        return $"WindowMenuBlock_{fileName}_{stableHash}";
    }

    private static string RunNetshOutput(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    internal static List<NetworkRow> GetNetstatRows(uint processId)
    {
        using var process = Process.Start(new ProcessStartInfo("netstat", "-ano")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start netstat.");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var rows = new List<NetworkRow>();

        foreach (var rawLine in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !uint.TryParse(parts[^1], out var rowPid) || rowPid != processId)
                continue;

            if (string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5)
                rows.Add(new NetworkRow(parts[0], parts[1], parts[2], parts[3]));
            else if (string.Equals(parts[0], "UDP", StringComparison.OrdinalIgnoreCase))
                rows.Add(new NetworkRow(parts[0], parts[1], "*:*", "Listening"));
        }

        return rows;
    }

    private static string? FindBundledTool(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "exe", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private sealed class SavedOpeningPosition
    {
        public string ExecutablePath { get; set; } = string.Empty;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    public static void SetCpuPriority(TargetWindowState target, ProcessPriorityClass level)
    {
        try
        {
            using var proc = Process.GetProcessById((int)target.ProcessId);
            proc.PriorityClass = level;
        }
        catch (ArgumentException) { /* process gone */ }
        catch (Win32Exception) { /* access denied — needs admin for some procs */ }
    }

    public static void MinimizeToTray(TargetWindowState target)
    {
        var executablePath = GetExecutablePathForTarget(target);
        if (string.IsNullOrEmpty(executablePath))
        {
            MessageBox.Show("Could not determine the application path for the tray icon.");
            return;
        }

        var title = GetWindowTitle(target.Hwnd);
        ShowWindow(target.Hwnd, SW_HIDE);
        try
        {
            TrayRestoreHost.Start(target.Hwnd, target.ProcessId, executablePath, title);
        }
        catch
        {
            ShowWindow(target.Hwnd, SW_RESTORE);
            throw;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    // ---- Window actions that require broader Windows integration ----

    public static void MuteWindow(TargetWindowState target)
    {
        var pids = new HashSet<uint> { target.ProcessId };
        foreach (var childPid in GetChildProcessIds(target.ProcessId))
            pids.Add(childPid);

        try
        {
            int toggled = CoreAudioHelper.ToggleMuteByProcessIds(pids);
            if (toggled == 0)
                MessageBox.Show("No audio session found for this window.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"MuteWindow failed: {ex.Message}");
        }
    }

    public static bool IsMuted(TargetWindowState target)
    {
        try
        {
            var pids = new HashSet<uint> { target.ProcessId };
            foreach (var childPid in GetChildProcessIds(target.ProcessId))
                pids.Add(childPid);
            return CoreAudioHelper.IsMutedByProcessIds(pids);
        }
        catch
        {
            return false;
        }
    }

    // Direct children only (one level). Good enough for the browser/game
    // renderer-process case; nested grandchildren are rare for audio.
    private static IEnumerable<uint> GetChildProcessIds(uint parentPid)
    {
        var children = new List<uint>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return children;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.th32ParentProcessID == parentPid)
                        children.Add(entry.th32ProcessID);
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return children;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int priClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static void LockWindow(TargetWindowState target)
    {
        // Implemented as option B (polling) PLUS two no-injection extras —
        // see WindowLockManager for the full approach and its limits.
        // Toggle: selecting this again on the same window unlocks it.
        WindowLockManager.Toggle(target.Hwnd);
    }

    public static void PreventWindowClosing(TargetWindowState target)
    {
        var wasLocked = WindowLockManager.IsLocked(target.Hwnd);
        WindowLockManager.Toggle(target.Hwnd);
        if (wasLocked) return;

        MessageBox.Show(
            "WindowMenu now blocks the window's normal close controls (including its Close system-menu item and Alt+F4) and keeps its position fixed.\n\n" +
            "This is best-effort window-close protection, not process protection. Windows does not provide a supported user-mode way for WindowMenu to prevent Task Manager, taskkill, or another process with termination rights from terminating the app.",
            "Prevent Window Closing",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public static void FreezeFocus(TargetWindowState target)
    {
        // See FreezeFocusManager. Toggle: selecting this again on the same
        // window releases it; selecting it on a different window switches
        // the freeze to that one.
        FreezeFocusManager.Toggle(target.Hwnd);
    }

    public static void RegisterWhenClosedAction(TargetWindowState target, string action)
    {
        // See WindowCloseWatcher for the actual detection + dispatch.
        WindowCloseWatcher.Register(target, action);
    }

    public static void ShowWindowProperties(TargetWindowState target)
    {
        // Gather window properties and show them in a temporary context menu.
        string build = "(Unknown)";
        string className = "(Unknown)";
        string doc = "(Unknown or N/A)";
        string folder = "";
        string executable = "";
        string handle = "";
        string pid = target.ProcessId.ToString();
        string runtime = "(Unknown)";
        string size = "(Unknown)";
        string title = "(Unknown)";
        string uniqueId = "(Unknown)";
        string where = "(Unknown)";

        try
        {
            // Class name and title
            var sb = new StringBuilder(256);
            if (GetClassName(target.Hwnd, sb, sb.Capacity) > 0)
                className = sb.ToString();

            int len = GetWindowTextLength(target.Hwnd);
            if (len > 0)
            {
                var tb = new StringBuilder(len + 1);
                if (GetWindowText(target.Hwnd, tb, tb.Capacity) > 0)
                    title = tb.ToString();
            }

            // Rect / size / where
            if (GetWindowRect(target.Hwnd, out RECT r))
            {
                size = $"{r.Right - r.Left} wide (w), {r.Bottom - r.Top} high (h)";
                where = $"{r.Left} left (x), {r.Top} top (y)";
                handle = $"{target.Hwnd.ToInt64()} / 0x{target.Hwnd.ToInt64():X}";
            }

            // Process info
            try
            {
                using var proc = Process.GetProcessById((int)target.ProcessId);
                try
                {
                    var path = proc.MainModule?.FileName ?? string.Empty;
                    executable = Path.GetFileName(path);
                    folder = Path.GetDirectoryName(path) ?? string.Empty;
                    build = "Win32 executable"; // best-effort default

                    // Try to detect managed/.NET processes by probing loaded modules
                    try
                    {
                        foreach (ProcessModule mod in proc.Modules)
                        {
                            var nm = mod.ModuleName;
                            if (string.Equals(nm, "coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(nm, "clr.dll", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(nm, "mscoree.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                build = ".NET";
                                break;
                            }
                        }
                    }
                    catch { /* some processes restrict module enumeration */ }
                }
                catch { /* access denied or not available */ }

                try
                {
                    var ts = DateTime.Now - proc.StartTime;
                    runtime = $"{ts.Days}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
                }
                catch { }

                uniqueId = ((uint)target.ProcessId << 16 ^ (uint)target.Hwnd.ToInt32()).ToString("X8");
            }
            catch { }
        }
        catch { }

        var props = new ContextMenuStrip();
        props.AutoClose = true;
        // Do not dispose immediately in the Closed event. WinForms is still in the
        // middle of the click/close chain when the item selection finishes, and
        // disposing the menu during that time triggers the same ObjectDisposedException.
        props.Items.Add(new ToolStripMenuItem($"{executable}") { Enabled = false, Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, 9F, System.Drawing.FontStyle.Bold) });
        void add(string k, string v) => props.Items.Add(new ToolStripMenuItem($"{k} = {v}") { Enabled = false });

                add("Build", build);
        add("Class", className);
        add("Doc", doc);
        add("Folder", string.IsNullOrEmpty(folder) ? "(Unknown)" : folder);
        add("Executable", string.IsNullOrEmpty(executable) ? "(Unknown)" : executable);
        add("Handle (hWnd)", handle);
        add("Process ID (PID)", pid);
        add("Runtime", runtime);
        add("Size", size);
        add("Title", string.IsNullOrEmpty(title) ? "(Unknown)" : title);
        add("Unique ID", uniqueId);
        add("Where", where);

        // Show near the cursor — menu will auto-close when clicking away.
        props.Show(Cursor.Position);
    }

    // ---- P/Invoke plumbing for the easy functions ----

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private static readonly HashSet<uint> AppliedOpeningPositionProcesses = new();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}

// ----------------------------------------------------------------------------
// Backs "Keep locked here". Runs entirely in THIS process, no injection —
// same trick as MouseHook: WH_KEYBOARD_LL executes on the hooking thread.
//
// What it actually blocks:
//   - Move / resize:  poll GetWindowRect every 50ms, snap back if changed.
//   - X button + Alt+F4: remove SC_CLOSE from the window's system menu
//     (GetSystemMenu/RemoveMenu — a normal cross-process user32 call, not
//     injection) AND swallow the Alt+F4 keystroke globally when the locked
//     window is foreground.
//
// This is best-effort UI protection: direct WM_CLOSE requests, Task Manager,
// taskkill, and application self-termination can still close the window.
// ----------------------------------------------------------------------------
internal static class CoreAudioHelper
{
    public static bool IsMutedByProcessIds(HashSet<uint> pids)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out var managerObject);
        var manager = (IAudioSessionManager2)managerObject;
        manager.GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var count);

        for (var i = 0; i < count; i++)
        {
            sessions.GetSession(i, out var control);
            var control2 = (IAudioSessionControl2)control;
            control2.GetProcessId(out var processId);
            if (!pids.Contains(processId)) continue;

            var volume = (ISimpleAudioVolume)control;
            volume.GetMute(out var muted);
            if (muted) return true;
        }

        return false;
    }

    public static int ToggleMuteByProcessIds(HashSet<uint> pids)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);

        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out var managerObject);
        var manager = (IAudioSessionManager2)managerObject;
        manager.GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var count);

        var toggled = 0;
        for (var i = 0; i < count; i++)
        {
            sessions.GetSession(i, out var control);
            var control2 = (IAudioSessionControl2)control;
            control2.GetProcessId(out var processId);
            if (!pids.Contains(processId)) continue;

            var volume = (ISimpleAudioVolume)control;
            volume.GetMute(out var muted);
            volume.SetMute(!muted, Guid.Empty);
            toggled++;
        }

        return toggled;
    }
}

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

[Flags]
internal enum CLSCTX : uint
{
    INPROC_SERVER = 0x1,
    INPROC_HANDLER = 0x2,
    LOCAL_SERVER = 0x4,
    ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
[ClassInterface(ClassInterfaceType.None)]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out object devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, CLSCTX clsContext, IntPtr activationParams,
        [MarshalAs(UnmanagedType.Interface)] out object interfacePointer);
    void OpenPropertyStore(uint access, out object properties);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    void GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags,
        out IAudioSessionControl sessionControl);
    void GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags,
        out ISimpleAudioVolume audioVolume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    void RegisterSessionNotification(IntPtr client);
    void UnregisterSessionNotification(IntPtr client);
    void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr client);
    void UnregisterDuckNotification(IntPtr client);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int sessionCount, out IAudioSessionControl session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
    void GetGroupingParam(out Guid groupingId);
    void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr client);
    void UnregisterAudioSessionNotification(IntPtr client);
}

[ComImport]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
    void GetGroupingParam(out Guid groupingId);
    void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr client);
    void UnregisterAudioSessionNotification(IntPtr client);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceId);
    void GetProcessId(out uint processId);
    void IsSystemSoundsSession();
    void SetDuckingPreference(bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid eventContext);
    void GetMasterVolume(out float level);
    void SetMute(bool mute, ref Guid eventContext);
    void GetMute(out bool mute);
}

internal static class WindowLockManager
{
    private static readonly HashSet<IntPtr> _locked = new();
    private static readonly Dictionary<IntPtr, RECT> _lockedRects = new();
    private static System.Windows.Forms.Timer? _pollTimer;
    private static IntPtr _keyboardHookId = IntPtr.Zero;
    private static LowLevelKeyboardProc? _keyboardProc;

    public static bool IsLocked(IntPtr hwnd) => _locked.Contains(hwnd);

    public static void Toggle(IntPtr hwnd)
    {
        if (_locked.Contains(hwnd)) Unlock(hwnd);
        else Lock(hwnd);
    }

    private static void Lock(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return;
        _locked.Add(hwnd);
        _lockedRects[hwnd] = r;

        IntPtr sysMenu = GetSystemMenu(hwnd, false);
        if (sysMenu != IntPtr.Zero)
            RemoveMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);

        EnsurePollTimerRunning();
        EnsureKeyboardHookInstalled();
    }

    private static void Unlock(IntPtr hwnd)
    {
        _locked.Remove(hwnd);
        _lockedRects.Remove(hwnd);

        // bRevert=true restores the default system menu, undoing RemoveMenu.
        GetSystemMenu(hwnd, true);

        if (_locked.Count == 0)
        {
            StopPollTimer();
            RemoveKeyboardHook();
        }
    }

    private static void EnsurePollTimerRunning()
    {
        if (_pollTimer != null) return;
        _pollTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _pollTimer.Tick += (_, _) => PollTick();
        _pollTimer.Start();
    }

    private static void StopPollTimer()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private static void PollTick()
    {
        // ToArray(): we may mutate _locked below (window closed underneath
        // us) while iterating.
        foreach (var hwnd in _locked.ToArray())
        {
            if (!IsWindow(hwnd))
            {
                _locked.Remove(hwnd);
                _lockedRects.Remove(hwnd);
                continue;
            }
            if (!GetWindowRect(hwnd, out RECT current)) continue;
            var lockedRect = _lockedRects[hwnd];
            if (current.Left != lockedRect.Left || current.Top != lockedRect.Top ||
                current.Right != lockedRect.Right || current.Bottom != lockedRect.Bottom)
            {
                SetWindowPos(hwnd, IntPtr.Zero,
                    lockedRect.Left, lockedRect.Top,
                    lockedRect.Right - lockedRect.Left, lockedRect.Bottom - lockedRect.Top,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
        if (_locked.Count == 0) StopPollTimer();
    }

    // ---- Alt+F4 swallow while a locked window is foreground ----

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F4 = 0x73;
    private const int VK_MENU = 0x12;

    private static void EnsureKeyboardHookInstalled()
    {
        if (_keyboardHookId != IntPtr.Zero) return;
        _keyboardProc = KeyboardHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    private static void RemoveKeyboardHook()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_SYSKEYDOWN)
        {
            // First DWORD of KBDLLHOOKSTRUCT is vkCode — reading it directly
            // avoids declaring the full struct for one field.
            int vkCode = Marshal.ReadInt32(lParam);
            bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            if (vkCode == VK_F4 && altDown && _locked.Contains(GetForegroundWindow()))
                return (IntPtr)1; // swallow: skip CallNextHookEx
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    // ---- P/Invoke plumbing ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

// ----------------------------------------------------------------------------
// Backs "Freeze the Focus". Global SetWinEventHook(EVENT_SYSTEM_FOREGROUND):
// whenever any window other than the frozen one gains foreground, forces it
// back. Only one window can be frozen at a time (freezing two windows to
// both be foreground is a contradiction) — freezing a new one switches away
// from the old one.
//
// Windows may block a background process from taking the foreground. The
// ForceForeground() workaround attaches the input queues of the foreground
// and target threads before requesting the switch.
// ----------------------------------------------------------------------------
internal static class FreezeFocusManager
{
    private static IntPtr _frozenHwnd = IntPtr.Zero;
    private static IntPtr _hookHandle = IntPtr.Zero;
    private static WinEventDelegate? _winEventProc;
    private static System.Windows.Forms.Timer? _aliveTimer;

    public static void Toggle(IntPtr hwnd)
    {
        if (_frozenHwnd == hwnd) Release();
        else Freeze(hwnd);
    }

    private static void Freeze(IntPtr hwnd)
    {
        _frozenHwnd = hwnd;
        EnsureHookInstalled();
        EnsureAliveTimerRunning();
        ForceForeground(hwnd);
    }

    private static void Release()
    {
        _frozenHwnd = IntPtr.Zero;
        StopAliveTimer();
        RemoveHook();
    }

    private static void EnsureHookInstalled()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _winEventProc = WinEventCallback;
        // WINEVENT_SKIPOWNPROCESS: don't fight our own menu/dialogs
        // (RenamePrompt, CommandPrompt, MessageBox) when they take focus.
        _hookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private static void RemoveHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _winEventProc = null;
        }
    }

    private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_frozenHwnd == IntPtr.Zero) return;
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        if (hwnd == IntPtr.Zero || hwnd == _frozenHwnd) return; // already what we want — avoids a fight loop
        ForceForeground(_frozenHwnd);
    }

    // Attach the relevant input queues to work around the foreground lock.
    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr currentForeground = GetForegroundWindow();
        if (currentForeground == hwnd) return;

        uint targetThreadId = GetWindowThreadProcessId(hwnd, out _);
        uint foregroundThreadId = GetWindowThreadProcessId(currentForeground, out _);
        uint thisThreadId = GetCurrentThreadId();

        bool attachedFg = foregroundThreadId != 0 && foregroundThreadId != thisThreadId &&
            AttachThreadInput(thisThreadId, foregroundThreadId, true);
        bool attachedTarget = targetThreadId != 0 && targetThreadId != thisThreadId &&
            AttachThreadInput(thisThreadId, targetThreadId, true);

        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(thisThreadId, targetThreadId, false);
            if (attachedFg) AttachThreadInput(thisThreadId, foregroundThreadId, false);
        }
    }

    // Auto-release if the frozen window is destroyed while frozen. Cheap
    // poll — reuses no other infra, easier to reason about than wiring this
    // into WindowCloseWatcher's destroy hook.
    private static void EnsureAliveTimerRunning()
    {
        if (_aliveTimer != null) return;
        _aliveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _aliveTimer.Tick += (_, _) =>
        {
            if (_frozenHwnd != IntPtr.Zero && !IsWindow(_frozenHwnd)) Release();
        };
        _aliveTimer.Start();
    }

    private static void StopAliveTimer()
    {
        _aliveTimer?.Stop();
        _aliveTimer?.Dispose();
        _aliveTimer = null;
    }

    // ---- P/Invoke plumbing ----

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

internal sealed class WindowMenuSettings
{
    public bool ShowSuspend { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowProcessActivity { get; set; } = true;
    public bool ShowExternalTools { get; set; } = true;
    public bool ShowWhenClosed { get; set; } = true;
    public string TriggerModifier { get; set; } = "Alt";
    public string TriggerMouseButton { get; set; } = "Right";

    private static SettingsForm? _form;
    public static WindowMenuSettings Current { get; private set; } = new();

    public static WindowMenuSettings Load()
    {
        try
        {
            var path = GetPath();
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<WindowMenuSettings>(File.ReadAllText(path))
                    ?? new WindowMenuSettings();
                return Current;
            }
        }
        catch
        {
            // Use safe defaults if the settings file is unavailable or invalid.
        }

        Current = new WindowMenuSettings();
        return Current;
    }

    public void Save()
    {
        Current = this;
        Directory.CreateDirectory(Path.GetDirectoryName(GetPath())!);
        File.WriteAllText(GetPath(), JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    public static void ShowWindow()
    {
        if (_form is { IsDisposed: false })
        {
            _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            return;
        }

        var settings = Load();
        _form = new SettingsForm(settings);
        _form.FormClosed += (_, _) => _form = null;
        _form.Show();
    }

    private static string GetPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowMenu");
        return Path.Combine(directory, "settings.json");
    }
}

internal sealed class NetworkActivityForm : Form
{
    private readonly uint _processId;
    private readonly WindowActions.NetworkActivityView _view;
    private readonly ListView _connections = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = false,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public NetworkActivityForm(uint processId, WindowActions.NetworkActivityView view)
    {
        _processId = processId;
        _view = view;
        Text = view == WindowActions.NetworkActivityView.Ports
            ? $"Active Ports - PID {processId}"
            : $"Active Connections - PID {processId}";
        Width = 900;
        Height = 400;
        StartPosition = FormStartPosition.CenterScreen;

        if (view == WindowActions.NetworkActivityView.Ports)
        {
            _connections.Columns.Add("Protocol", 110);
            _connections.Columns.Add("Local port", 180);
            _connections.Columns.Add("Remote port", 180);
            _connections.Columns.Add("State", 180);
        }
        else
        {
            _connections.Columns.Add("Protocol", 110);
            _connections.Columns.Add("Local address", 280);
            _connections.Columns.Add("Remote address", 280);
            _connections.Columns.Add("State", 180);
        }

        var refresh = new Button
        {
            Text = "Refresh",
            Dock = DockStyle.Top,
            Height = 32
        };
        refresh.Click += (_, _) => RefreshConnections();
        Controls.Add(_connections);
        Controls.Add(_status);
        Controls.Add(refresh);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshConnections();
        FormClosed += (_, _) => _refreshTimer.Dispose();
        RefreshConnections();
        _refreshTimer.Start();
    }

    private void RefreshConnections()
    {
        try
        {
            var rows = WindowActions.GetNetstatRows(_processId);
            if (_view == WindowActions.NetworkActivityView.Connections)
            {
                rows = rows.Where(row =>
                    string.Equals(row.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(row.Remote, "*:*", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(row.State, "LISTENING", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            _connections.BeginUpdate();
            _connections.Items.Clear();
            foreach (var row in rows)
            {
                var values = _view == WindowActions.NetworkActivityView.Ports
                    ? new[] { row.Protocol, GetPort(row.Local), GetPort(row.Remote), row.State }
                    : new[] { row.Protocol, row.Local, row.Remote, row.State };
                _connections.Items.Add(new ListViewItem(values));
            }
            _connections.EndUpdate();
            var description = _view == WindowActions.NetworkActivityView.Ports
                ? "active port(s)"
                : "active connection(s)";
            _status.Text = rows.Count == 0
                ? $"No {description} for PID {_processId}. Updated {DateTime.Now:T}."
                : $"{rows.Count} {description} for PID {_processId}. Updated {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read network activity: {ex.Message}";
        }
    }

    private static string GetPort(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        return separator >= 0 && separator < endpoint.Length - 1
            ? endpoint[(separator + 1)..]
            : "N/A";
    }
}

internal static class VoiceAccessPlacementManager
{
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 500 };
    private static IntPtr _window;
    private static string? _monitorDeviceName;

    static VoiceAccessPlacementManager()
    {
        Timer.Tick += (_, _) => EnforcePlacement();
    }

    public static void KeepOnScreen(IntPtr hwnd, Screen screen)
    {
        _window = hwnd;
        _monitorDeviceName = screen.DeviceName;
        if (!Timer.Enabled)
            Timer.Start();
    }

    private static void EnforcePlacement()
    {
        if (_window == IntPtr.Zero || !IsWindow(_window))
        {
            Stop();
            return;
        }

        var screen = Screen.AllScreens.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, _monitorDeviceName, StringComparison.OrdinalIgnoreCase));
        if (screen == null)
        {
            Stop();
            return;
        }

        try
        {
            if (!GetWindowRect(_window, out var rect))
                return;
            var center = new Point(rect.Left + (rect.Right - rect.Left) / 2,
                rect.Top + (rect.Bottom - rect.Top) / 2);
            if (!screen.Bounds.Contains(center))
                WindowActions.MoveWindowToScreen(_window, screen);
        }
        catch (Exception ex)
        {
            Stop();
            ErrorReporter.Write(ex, "Keep Voice Access on secondary monitor");
            MessageBox.Show(
                $"WindowMenu stopped enforcing the selected monitor:\n{ex.Message}",
                "Voice Access",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void Stop()
    {
        Timer.Stop();
        _window = IntPtr.Zero;
        _monitorDeviceName = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
}

internal sealed record WindowTargetInfo(
    IntPtr Handle,
    uint ProcessId,
    string Title,
    string ProcessName,
    Rectangle Bounds)
{
    public bool LooksLikeVoiceAccess =>
        ProcessName.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
        ProcessName.Contains("access", StringComparison.OrdinalIgnoreCase) ||
        Title.Contains("voice access", StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(No window title)" : Title;
}

internal sealed class DwmWindowThumbnail : Control
{
    private IntPtr _thumbnail;
    private IntPtr _destinationWindow;
    private Size _sourceSize;
    public string PreviewStatus { get; private set; } = "Select a window to preview.";
    public event EventHandler? PreviewStatusChanged;

    public DwmWindowThumbnail()
    {
        BackColor = Color.FromArgb(28, 28, 28);
        ForeColor = Color.White;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void SetSource(IntPtr sourceWindow)
    {
        Unregister();
        _sourceSize = Size.Empty;
        if (sourceWindow == IntPtr.Zero)
        {
            SetStatus("Select a window to preview.");
            return;
        }

        var destination = FindForm();
        if (destination == null || !destination.IsHandleCreated)
        {
            SetStatus("The thumbnail preview window is not available.");
            return;
        }
        _destinationWindow = destination.Handle;

        var result = DwmRegisterThumbnail(_destinationWindow, sourceWindow, out _thumbnail);
        if (result != 0)
        {
            _thumbnail = IntPtr.Zero;
            SetStatus($"Windows thumbnail preview unavailable (0x{result:X8}).");
            return;
        }

        result = DwmQueryThumbnailSourceSize(_thumbnail, out var size);
        if (result != 0 || size.Width <= 0 || size.Height <= 0)
        {
            Unregister();
            SetStatus($"Could not read thumbnail size (0x{result:X8}).");
            return;
        }

        _sourceSize = new Size(size.Width, size.Height);
        SetStatus("Live thumbnail preview");
        UpdateThumbnail();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateThumbnail();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_thumbnail == IntPtr.Zero)
            TextRenderer.DrawText(e.Graphics, PreviewStatus, Font, ClientRectangle,
                ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                           TextFormatFlags.WordBreak);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Unregister();
        base.OnHandleDestroyed(e);
    }

    private void UpdateThumbnail()
    {
        if (_thumbnail == IntPtr.Zero || _sourceSize.IsEmpty ||
            ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        var scale = Math.Min((double)ClientSize.Width / _sourceSize.Width,
            (double)ClientSize.Height / _sourceSize.Height);
        var width = Math.Max(1, (int)(_sourceSize.Width * scale));
        var height = Math.Max(1, (int)(_sourceSize.Height * scale));
        var left = (ClientSize.Width - width) / 2;
        var top = (ClientSize.Height - height) / 2;
        var previewOrigin = Parent?.PointToScreen(Location) ?? PointToScreen(Point.Empty);
        var destinationOrigin = FindForm()?.PointToClient(previewOrigin) ?? Point.Empty;
        var properties = new DwmThumbnailProperties
        {
            Flags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
            Destination = new NativeRect
            {
                Left = destinationOrigin.X + left,
                Top = destinationOrigin.Y + top,
                Right = destinationOrigin.X + left + width,
                Bottom = destinationOrigin.Y + top + height
            },
            Opacity = 255,
            Visible = true
        };
        var result = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        if (result != 0)
            SetStatus($"Thumbnail update failed (0x{result:X8}).");
    }

    private void SetStatus(string status)
    {
        if (PreviewStatus == status) return;
        PreviewStatus = status;
        PreviewStatusChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void Unregister()
    {
        if (_thumbnail == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
        _destinationWindow = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    private const uint DWM_TNP_OPACITY = 0x00000004;
    private const uint DWM_TNP_VISIBLE = 0x00000008;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DwmThumbnailProperties properties);
}

internal sealed class WindowTargetPickerForm : Form
{
    private readonly bool _moveToMonitor;
    private readonly bool _previewOnly;
    private readonly Screen[] _screens;
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter by window title or process..." };
    private readonly ListView _windowList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false
    };
    private readonly ComboBox _monitor = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = false,
        Height = 34,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label _previewStatus = new()
    {
        Dock = DockStyle.Bottom,
        Height = 42,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly DwmWindowThumbnail _thumbnail = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical
    };

    public WindowTargetInfo? SelectedWindow =>
        _windowList.SelectedItems.Count > 0
            ? _windowList.SelectedItems[0].Tag as WindowTargetInfo
            : null;
    public Screen? SelectedScreen =>
        _monitor.SelectedIndex >= 0 && _monitor.SelectedIndex < _screens.Length
            ? _screens[_monitor.SelectedIndex]
            : null;

    public WindowTargetPickerForm(
        string title,
        string instruction,
        Screen[] screens,
        bool moveToMonitor,
        bool previewOnly = false)
    {
        _moveToMonitor = moveToMonitor;
        _previewOnly = previewOnly;
        _screens = screens;
        Text = title;
        Width = 900;
        Height = 540;
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.CenterScreen;

        _windowList.Columns.Add("Window title", 330);
        _windowList.Columns.Add("Process", 210);
        _windowList.Columns.Add("PID", 85);
        _windowList.Columns.Add("Current display", 210);
        _windowList.DoubleClick += (_, _) => CompleteSelection();
        _windowList.SelectedIndexChanged += (_, _) => UpdateSelectedThumbnail();
        _thumbnail.PreviewStatusChanged += (_, _) => _previewStatus.Text = _thumbnail.PreviewStatus;

        _split.Panel1.Controls.Add(_windowList);
        _split.Panel2.Controls.Add(_thumbnail);
        _split.Panel2.Controls.Add(_previewStatus);

        var instructionLabel = new Label
        {
            Text = instruction,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 42,
            Padding = new Padding(8),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8),
            WrapContents = false
        };
        var refresh = new Button { Text = "Refresh windows", AutoSize = true };
        refresh.Click += (_, _) => RefreshWindows();
        controls.Controls.Add(refresh);
        if (moveToMonitor)
        {
            controls.Controls.Add(new Label
            {
                Text = "Move to:",
                AutoSize = true,
                Margin = new Padding(12, 7, 4, 0)
            });
            foreach (var screen in _screens)
                _monitor.Items.Add($"{screen.DeviceName} ({screen.Bounds.Width} x {screen.Bounds.Height})");
            if (_monitor.Items.Count > 0) _monitor.SelectedIndex = 0;
            controls.Controls.Add(_monitor);
        }

        Button? action = null;
        if (!_previewOnly)
        {
            action = new Button
            {
                Text = moveToMonitor ? "Move selected window" : "Open Performance",
                AutoSize = true
            };
            action.Click += (_, _) => CompleteSelection();
            controls.Controls.Add(action);
        }
        else
        {
            controls.Controls.Add(new Label
            {
                Text = "Live preview updates when you select a window.",
                AutoSize = true,
                Margin = new Padding(12, 7, 4, 0)
            });
        }
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        controls.Controls.Add(cancel);

        _filter.TextChanged += (_, _) => PopulateWindows();
        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(controls);
        Controls.Add(_filter);
        Controls.Add(instructionLabel);
        if (action != null)
            AcceptButton = action;
        CancelButton = cancel;
        RefreshWindows();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_split.Width > _split.Panel1MinSize + _split.Panel2MinSize)
            _split.SplitterDistance = Math.Clamp(560, _split.Panel1MinSize,
                _split.Width - _split.Panel2MinSize);
        UpdateSelectedThumbnail();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _thumbnail.SetSource(IntPtr.Zero);
        base.OnFormClosed(e);
    }

    private List<WindowTargetInfo> _allWindows = new();

    private void RefreshWindows()
    {
        _allWindows = WindowActions.GetVisibleWindowTargets();
        PopulateWindows();
    }

    private void PopulateWindows()
    {
        var query = _filter.Text.Trim();
        _windowList.BeginUpdate();
        _windowList.Items.Clear();
        foreach (var window in _allWindows)
        {
            if (!string.IsNullOrWhiteSpace(query) &&
                !window.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !window.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !window.ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            var display = Screen.FromRectangle(window.Bounds);
            var item = new ListViewItem(window.DisplayTitle);
            item.SubItems.Add(window.ProcessName);
            item.SubItems.Add(window.ProcessId.ToString());
            item.SubItems.Add(display.DeviceName);
            item.Tag = window;
            if (window.LooksLikeVoiceAccess)
                item.BackColor = Color.LightCyan;
            _windowList.Items.Add(item);
        }
        _windowList.EndUpdate();

        var voiceMatches = _allWindows.Count(window => window.LooksLikeVoiceAccess);
        _status.Text = _moveToMonitor
            ? $"{_windowList.Items.Count} visible window(s); {voiceMatches} likely Voice Access match(es) highlighted. Search or select a window."
            : $"{_windowList.Items.Count} visible window(s). Choose any process to open its live performance view.";

        if (_windowList.Items.Count > 0 && _windowList.SelectedItems.Count == 0)
        {
            var first = _moveToMonitor
                ? _windowList.Items.Cast<ListViewItem>()
                    .FirstOrDefault(item => (item.Tag as WindowTargetInfo)?.LooksLikeVoiceAccess == true)
                : null;
            (first ?? _windowList.Items[0]).Selected = true;
        }
        UpdateSelectedThumbnail();
    }

    private void UpdateSelectedThumbnail()
    {
        var selected = SelectedWindow;
        if (selected == null)
        {
            _thumbnail.SetSource(IntPtr.Zero);
            _previewStatus.Text = "Select a window to preview.";
            return;
        }

        _thumbnail.SetSource(selected.Handle);
        _previewStatus.Text = $"{_thumbnail.PreviewStatus}: {selected.ProcessName} (PID {selected.ProcessId})\r\n{selected.DisplayTitle}";
    }

    private void CompleteSelection()
    {
        if (SelectedWindow == null)
        {
            MessageBox.Show(this, "Select a window first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_moveToMonitor && SelectedScreen == null)
        {
            MessageBox.Show(this, "Select a destination display first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_previewOnly)
            return;

        try
        {
            if (_moveToMonitor)
            {
                WindowActions.MoveWindowToScreen(SelectedWindow.Handle, SelectedScreen!);
                MessageBox.Show(this, $"Moved '{SelectedWindow.DisplayTitle}' to {SelectedScreen!.DeviceName}.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                WindowActions.ShowPerformance(new TargetWindowState
                {
                    Hwnd = SelectedWindow.Handle,
                    ProcessId = SelectedWindow.ProcessId
                });
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class ProcessPerformanceForm : Form
{
    private readonly Process _process;
    private readonly uint _processId;
    private readonly GpuEngineUsageCounter _gpuCounter;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _cpu = CreateMetricLabel();
    private readonly Label _memory = CreateMetricLabel();
    private readonly Label _gpu = CreateMetricLabel();
    private readonly Label _disk = CreateMetricLabel();
    private readonly Label _uptime = CreateMetricLabel();
    private readonly Label _power = CreateMetricLabel();
    private readonly Label _status = CreateMetricLabel();
    private TimeSpan _lastCpuTime;
    private long _lastReadBytes;
    private long _lastWriteBytes;
    private long _lastSampleTimestamp;
    private bool _hasIoBaseline;

    public ProcessPerformanceForm(uint processId)
    {
        _processId = processId;
        _process = Process.GetProcessById((int)processId);
        _lastCpuTime = _process.TotalProcessorTime;
        _lastSampleTimestamp = Stopwatch.GetTimestamp();
        _gpuCounter = new GpuEngineUsageCounter(processId);

        Text = $"Performance - {_process.ProcessName} (PID {processId})";
        Width = 620;
        Height = 400;
        MinimumSize = new Size(500, 340);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(14),
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));

        AddMetric(layout, 0, "CPU", _cpu);
        AddMetric(layout, 1, "Memory (working set / private)", _memory);
        AddMetric(layout, 2, "GPU (peak engine utilization)", _gpu);
        AddMetric(layout, 3, "Process I/O (read / write rate)", _disk);
        AddMetric(layout, 4, "Uptime", _uptime);
        AddMetric(layout, 5, "Power draw", _power);
        layout.Controls.Add(_status, 0, 6);
        layout.SetColumnSpan(_status, 2);
        Controls.Add(layout);

        _power.Text = "Not exposed per process by supported Windows counters.";
        _status.Text = "Sampling once per second. Disk figures are process I/O, not disk-device throughput.";
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshMetrics();
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            _gpuCounter.Dispose();
            _process.Dispose();
        };

        RefreshMetrics();
        _timer.Start();
    }

    private void RefreshMetrics()
    {
        try
        {
            if (_process.HasExited)
            {
                _status.Text = $"Process {_processId} has exited; final sample shown.";
                _timer.Stop();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
            var cpuTime = _process.TotalProcessorTime;
            if (elapsed > 0)
            {
                var cpuPercent = (cpuTime - _lastCpuTime).TotalSeconds /
                                 (elapsed * Environment.ProcessorCount) * 100;
                _cpu.Text = $"{Math.Clamp(cpuPercent, 0, 100):F1}% (all logical CPUs)";
            }
            _lastCpuTime = cpuTime;
            _lastSampleTimestamp = now;

            _memory.Text = $"{FormatBytes(_process.WorkingSet64)} working set / " +
                           $"{FormatBytes(_process.PrivateMemorySize64)} private bytes";
            _uptime.Text = FormatDuration(DateTime.Now - _process.StartTime);

            if (_gpuCounter.TryRead(out var gpuUsage, out var gpuError))
                _gpu.Text = $"{gpuUsage:F1}% (highest active engine)";
            else
                _gpu.Text = gpuError;

            if (GetProcessIoCounters(_process.Handle, out var io))
            {
                if (_hasIoBaseline && elapsed > 0)
                {
                    var readRate = Math.Max(0, (long)io.ReadTransferCount - _lastReadBytes) / elapsed;
                    var writeRate = Math.Max(0, (long)io.WriteTransferCount - _lastWriteBytes) / elapsed;
                    _disk.Text = $"{FormatRate(readRate)} read / {FormatRate(writeRate)} write";
                }
                else
                {
                    _disk.Text = "Collecting initial sample...";
                }

                _lastReadBytes = (long)io.ReadTransferCount;
                _lastWriteBytes = (long)io.WriteTransferCount;
                _hasIoBaseline = true;
            }
            else
            {
                _disk.Text = $"Unavailable (Win32 error {Marshal.GetLastWin32Error()})";
            }

            _status.Text = $"PID {_processId}. Updated {DateTime.Now:T}. " +
                           "Power draw is reported as unavailable because Windows does not provide a reliable per-process watt reading.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _status.Text = $"Could not refresh process metrics: {ex.Message}";
            _timer.Stop();
        }
    }

    private static void AddMetric(TableLayoutPanel layout, int row, string name, Label value)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = name,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, row);
        layout.Controls.Add(value, 1, row);
    }

    private static Label CreateMetricLabel() => new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }

    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m"
            : $"{duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out ProcessIoCounters ioCounters);
}

internal sealed class GpuEngineUsageCounter : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhValidData = 0;
    private const uint PdhNewData = 1;
    private IntPtr _query;
    private IntPtr _counter;
    private readonly uint _processId;
    public string? Failure { get; private set; }

    public GpuEngineUsageCounter(uint processId)
    {
        _processId = processId;
        try
        {
            CheckStatus(PdhOpenQuery(null, UIntPtr.Zero, out _query), "open GPU counter query");
            CheckStatus(PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage",
                UIntPtr.Zero, out _counter), "find GPU Engine counters");
            CheckStatus(PdhCollectQueryData(_query), "initialize GPU counter query");
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Failure = $"Unavailable: {ex.Message}";
            Dispose();
        }
    }

    public bool TryRead(out double utilization, out string error)
    {
        utilization = 0;
        error = Failure ?? "No matching GPU engine counter is available.";
        if (Failure != null || _counter == IntPtr.Zero || _query == IntPtr.Zero)
            return false;

        var collectStatus = PdhCollectQueryData(_query);
        if (collectStatus != 0)
        {
            error = $"Unavailable (PDH 0x{collectStatus:X8}).";
            return false;
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble,
            ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData && status != 0)
        {
            error = $"Unavailable (PDH 0x{status:X8}).";
            return false;
        }
        if (itemCount == 0 || bufferSize == 0)
            return false;

        var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble,
                ref bufferSize, ref itemCount, buffer);
            if (status != 0)
            {
                error = $"Unavailable (PDH 0x{status:X8}).";
                return false;
            }

            var itemSize = Marshal.SizeOf<PdhFormattedCounterItem>();
            var processToken = $"pid_{_processId}_";
            var found = false;
            for (var i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PdhFormattedCounterItem>(
                    IntPtr.Add(buffer, checked((int)i * itemSize)));
                var instanceName = Marshal.PtrToStringUni(item.Name);
                if ((item.Value.Status != PdhValidData && item.Value.Status != PdhNewData) ||
                    instanceName == null ||
                    !instanceName.Contains(processToken, StringComparison.OrdinalIgnoreCase))
                    continue;

                utilization = Math.Max(utilization, Math.Clamp(item.Value.DoubleValue, 0, 100));
                found = true;
            }

            if (found)
            {
                error = string.Empty;
                return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }
    }

    private static void CheckStatus(uint status, string operation)
    {
        if (status != 0)
            throw new Win32Exception(unchecked((int)status), $"Could not {operation} (PDH 0x{status:X8}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterItem
    {
        public IntPtr Name;
        public PdhFormattedValue Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath,
        UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format,
        ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class ProcessActivityRecord
{
    public string Path = "(unknown)";
    public string Module = "(best effort: unavailable)";
    public long Opens, Closes, Reads, Writes, ReadBytes, WriteBytes;
    public DateTime FirstSeen, LastSeen;
}

internal sealed class ProcessActivityCollector : IDisposable
{
    private readonly uint _processId;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, string> _fileNames = new();
    private readonly Dictionary<string, ProcessActivityRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private TraceEventSession? _session;
    private Task? _worker;
    private string _module = "(best effort: unavailable)";
    public string? Failure { get; private set; }

    public ProcessActivityCollector(uint processId) { _processId = processId; }

    public IReadOnlyList<ProcessActivityRecord> Snapshot()
    {
        lock (_gate) return _records.Values.OrderByDescending(r => r.LastSeen).ToList();
    }

    public void Start()
    {
        try
        {
            using var process = Process.GetProcessById((int)_processId);
            _module = process.MainModule?.ModuleName ?? "(best effort: unavailable)";
        }
        catch { }
        _worker = Task.Run(() => Run());
    }

    private void Run()
    {
        var sessionName = $"WindowMenu-FileIO-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            using var session = new TraceEventSession(sessionName);
            _session = session;
            session.StopOnDispose = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
            var parser = new KernelTraceEventParser(session.Source);
            parser.FileIOName += e => { if (e.ProcessID == (int)_processId && e.FileKey != 0) lock (_gate) _fileNames[e.FileKey] = e.FileName; };
            parser.FileIOCreate += e => Record(e.ProcessID, e.FileObject, e.FileName, "open", 0);
            parser.FileIOClose += e => Record(e.ProcessID, e.FileObject, e.FileName, "close", 0);
            parser.FileIORead += e => Record(e.ProcessID, e.FileObject, e.FileName, "read", Math.Max(0, e.IoSize));
            parser.FileIOWrite += e => Record(e.ProcessID, e.FileObject, e.FileName, "write", Math.Max(0, e.IoSize));
            session.Source.Process();
        }
        catch (Exception ex)
        {
            Failure = ex is UnauthorizedAccessException
                ? "Kernel FileIO tracing requires Administrator rights."
                : $"ETW FileIO tracing unavailable: {ex.Message}";
            lock (_gate)
            {
                var record = GetRecord("[ETW unavailable]", DateTime.Now);
                record.Module = ex is UnauthorizedAccessException ? "Run WindowMenu as administrator to start kernel FileIO tracing." : ex.Message;
            }
        }
    }

    private void Record(int processId, ulong fileObject, string? eventName, string operation, long bytes)
    {
        if (processId != (int)_processId) return;
        string path;
        lock (_gate)
        {
            path = string.IsNullOrWhiteSpace(eventName) && fileObject != 0 && _fileNames.TryGetValue(fileObject, out var mapped) ? mapped : eventName ?? "(unknown file)";
            var now = DateTime.Now;
            var record = GetRecord(path, now);
            record.LastSeen = now;
            if (record.FirstSeen == default) record.FirstSeen = now;
            record.Module = _module;
            switch (operation) { case "open": record.Opens++; break; case "close": record.Closes++; break; case "read": record.Reads++; record.ReadBytes += bytes; break; case "write": record.Writes++; record.WriteBytes += bytes; break; }
        }
    }

    private ProcessActivityRecord GetRecord(string path, DateTime now)
    {
        if (!_records.TryGetValue(path, out var record))
        {
            record = new ProcessActivityRecord { Path = path, FirstSeen = now, LastSeen = now, Module = _module };
            _records[path] = record;
        }
        return record;
    }

    public void Dispose()
    {
        try { _session?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}

internal sealed class ProcessActivityForm : Form
{
    private readonly ProcessActivityCollector _collector;
    private readonly ListView _activity = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 30, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public ProcessActivityForm(uint processId)
    {
        Text = $"Process Activity - PID {processId}"; Width = 1250; Height = 600; StartPosition = FormStartPosition.CenterScreen;
        foreach (var column in new[] { ("File / folder", 360), ("Calling module (best effort)", 190), ("Opens", 70), ("Closes", 70), ("Reads", 70), ("Writes", 70), ("Read bytes", 100), ("Write bytes", 100), ("First seen", 155), ("Last seen", 155) }) _activity.Columns.Add(column.Item1, column.Item2);
        var refresh = new Button { Text = "Refresh", Dock = DockStyle.Top, Height = 32 };
        refresh.Click += (_, _) => RefreshActivity();
        Controls.Add(_activity); Controls.Add(_status); Controls.Add(refresh);
        _collector = new ProcessActivityCollector(processId);
        _collector.Start();
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshActivity();
        FormClosed += (_, _) => { _refreshTimer.Stop(); _refreshTimer.Dispose(); _collector.Dispose(); };
        _status.Text = "Starting Windows kernel FileIO tracing... administrator rights may be required.";
        _refreshTimer.Start();
    }

    private void RefreshActivity()
    {
        var rows = _collector.Snapshot();
        _activity.BeginUpdate(); _activity.Items.Clear();
        foreach (var row in rows)
        {
            _activity.Items.Add(new ListViewItem(new[] { row.Path, row.Module, row.Opens.ToString("N0"), row.Closes.ToString("N0"), row.Reads.ToString("N0"), row.Writes.ToString("N0"), FormatBytes(row.ReadBytes), FormatBytes(row.WriteBytes), row.FirstSeen.ToString("G"), row.LastSeen.ToString("G") }));
        }
        _activity.EndUpdate();
        _status.Text = _collector.Failure ?? (rows.Count == 0
            ? "No matching FileIO events yet. ETW captures activity from now onward; existing history is not available."
            : $"{rows.Count:N0} file/folder(s). Updated {DateTime.Now:T}.");
    }

    private uint _collectorProcessId() => 0; // replaced below by title/status-independent count
    private static string FormatBytes(long bytes) => $"{bytes:N0} B";
}
internal sealed class SettingsForm : Form
{
    public SettingsForm(WindowMenuSettings settings)
    {
        Text = "WindowMenu Settings";
        Width = 420;
        Height = 330;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(12),
            WrapContents = false,
            AutoScroll = true
        };
        layout.Controls.Add(new Label
        {
            Text = "Menu options shown after Alt + right-click:",
            AutoSize = true
        });

        var suspend = AddOption(layout, "Show Suspend / Unsuspend", settings.ShowSuspend);
        var network = AddOption(layout, "Show Network", settings.ShowNetwork);
        var activity = AddOption(layout, "Show Process Activity", settings.ShowProcessActivity);
        var tools = AddOption(layout, "Show External Tools", settings.ShowExternalTools);
        var closed = AddOption(layout, "Show When Closed", settings.ShowWhenClosed);

        layout.Controls.Add(new Label { Text = "Menu input:", AutoSize = true, Padding = new Padding(0, 12, 0, 0) });
        var modifier = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150
        };
        modifier.Items.AddRange(new object[] { "Alt", "Control", "Shift", "None" });
        modifier.SelectedItem = settings.TriggerModifier;
        var mouseButton = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150
        };
        mouseButton.Items.AddRange(new object[] { "Left", "Right", "Middle" });
        mouseButton.SelectedItem = settings.TriggerMouseButton;
        layout.Controls.Add(modifier);
        layout.Controls.Add(mouseButton);

        var record = new Button { Text = "Set input", Width = 90, Height = 28 };
        record.Click += (_, _) =>
        {
            using var capture = new InputCaptureForm();
            if (capture.ShowDialog(this) != DialogResult.OK) return;
            modifier.SelectedItem = capture.Modifier;
            mouseButton.SelectedItem = capture.MouseButton;
        };
        layout.Controls.Add(record);
        layout.Controls.Add(new Label
        {
            Text = "Set input opens a capture window. Press a modifier, then click the desired mouse button.",
            AutoSize = true,
            ForeColor = System.Drawing.Color.DimGray,
            MaximumSize = new System.Drawing.Size(370, 0)
        });

        var save = new Button { Text = "Save", Width = 90, Height = 28 };
        save.Click += (_, _) =>
        {
            settings.ShowSuspend = suspend.Checked;
            settings.ShowNetwork = network.Checked;
            settings.ShowProcessActivity = activity.Checked;
            settings.ShowExternalTools = tools.Checked;
            settings.ShowWhenClosed = closed.Checked;
            settings.TriggerModifier = modifier.SelectedItem?.ToString() ?? "Alt";
            settings.TriggerMouseButton = mouseButton.SelectedItem?.ToString() ?? "Right";
            settings.Save();
            Close();
        };
        layout.Controls.Add(save);
        Controls.Add(layout);
    }

    private static CheckBox AddOption(Control parent, string text, bool value)
    {
        var option = new CheckBox { Text = text, Checked = value, AutoSize = true };
        parent.Controls.Add(option);
        return option;
    }
}

internal sealed class InputCaptureForm : Form
{
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        Text = "Press Alt, Control, or Shift, then click Left, Right, or Middle.",
        TextAlign = ContentAlignment.MiddleCenter
    };
    private string? _modifier;

    public string Modifier { get; private set; } = "Alt";
    public string MouseButton { get; private set; } = "Right";

    public InputCaptureForm()
    {
        Text = "Set WindowMenu Input";
        Width = 430;
        Height = 150;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        Controls.Add(_status);
        KeyDown += OnKeyDown;
        MouseDown += OnMouseDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _modifier = e.KeyCode switch
        {
            Keys.Menu => "Alt",
            Keys.ControlKey => "Control",
            Keys.ShiftKey => "Shift",
            _ => _modifier
        };
        if (_modifier != null)
            _status.Text = $"{_modifier} recorded. Click the desired mouse button.";
        e.SuppressKeyPress = true;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (_modifier == null) return;
        MouseButton = e.Button switch
        {
            MouseButtons.Left => "Left",
            MouseButtons.Middle => "Middle",
            MouseButtons.Right => "Right",
            _ => MouseButton
        };
        Modifier = _modifier;
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal static class WindowCloakManager
{
    private static readonly HashSet<IntPtr> CaptureExcludedWindows = new();
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    public static bool IsCaptureExcluded(IntPtr hwnd) => CaptureExcludedWindows.Contains(hwnd);

    public static void ToggleCaptureExclusion(IntPtr hwnd)
    {
        var excluded = !IsCaptureExcluded(hwnd);
        if (!SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (excluded) CaptureExcludedWindows.Add(hwnd);
        else CaptureExcludedWindows.Remove(hwnd);
    }

    public static void ToggleAltTabVisibility(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        var hidden = (style & WS_EX_TOOLWINDOW) != 0 && (style & WS_EX_APPWINDOW) == 0;
        style = hidden
            ? (style & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW
            : (style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
        ShowWindow(hwnd, SW_HIDE);
        ShowWindow(hwnd, SW_SHOW);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

// ----------------------------------------------------------------------------
// Tiny modal prompt for "Title Rename". Kept minimal on purpose.
// ----------------------------------------------------------------------------
internal sealed class RenamePrompt : Form
{
    private readonly TextBox _textBox = new() { Dock = DockStyle.Top };
    public string NewTitle => _textBox.Text;

    public RenamePrompt()
    {
        Text = "Rename Window";
        Width = 320;
        Height = 120;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Top = 40, Left = 220 };
        Controls.Add(_textBox);
        Controls.Add(ok);
        AcceptButton = ok;
    }
}

// ----------------------------------------------------------------------------
// Backs "When Closed -> ...". Detects the target window's destruction via a
// global WinEvent hook (no injection, no polling) and dispatches the chosen
// action once, then disarms for that hwnd.
//
// SetWinEventHook(..., WINEVENT_OUTOFCONTEXT) delivers callbacks on the
// calling thread's message queue, so Register() must run on a thread that
// pumps messages. Menu-click handlers run on the WinForms UI thread.
// ----------------------------------------------------------------------------
internal static class WindowCloseWatcher
{
    private sealed class PendingAction
    {
        public required string Kind { get; init; }
        public string? Data { get; init; } // command string, or cached exe path
    }

    private static readonly Dictionary<IntPtr, PendingAction> _pending = new();
    private static IntPtr _hookHandle = IntPtr.Zero;
    private static WinEventDelegate? _winEventProc;

    public static void Register(TargetWindowState target, string action)
    {
        string? data = null;

        if (action == "Execute A Command")
        {
            using var prompt = new CommandPrompt();
            if (prompt.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(prompt.Command))
                return; // user cancelled — don't arm anything
            data = prompt.Command;
        }
        else if (action == "Play A Sound")
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select Sound File",
                Filter = "Wave files (*.wav)|*.wav|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != DialogResult.OK)
                return;
            data = dialog.FileName;
        }
        else if (action == "Reopen Application")
        {
            // Cache the exe path NOW, while the process is still alive —
            // can't read MainModule after it's gone.
            try
            {
                using var proc = Process.GetProcessById((int)target.ProcessId);
                data = proc.MainModule?.FileName;
            }
            catch (Exception) { /* process gone or access denied */ }

            if (string.IsNullOrEmpty(data))
            {
                MessageBox.Show("Couldn't read this window's executable path — can't set Reopen Application.");
                return;
            }
        }

        _pending[target.Hwnd] = new PendingAction { Kind = action, Data = data };
        EnsureHookInstalled();
    }

    private static void EnsureHookInstalled()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _winEventProc = WinEventCallback;
        _hookHandle = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // OBJID_WINDOW/CHILDID_SELF: only the top-level window itself, not
        // its child controls being torn down.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        if (!_pending.TryGetValue(hwnd, out var pending)) return;

        _pending.Remove(hwnd);
        Dispatch(pending);

        if (_pending.Count == 0)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _winEventProc = null;
        }
    }

    private static void Dispatch(PendingAction pending)
    {
        try
        {
            switch (pending.Kind)
            {
                case "ShutDown PC":
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                    { CreateNoWindow = true, UseShellExecute = false });
                    break;

                case "Put PC To Sleep":
                    // BUGFIX: SetSuspendState's own docs say the calling
                    // process needs SE_SHUTDOWN_NAME enabled first, or the
                    // call just returns false silently — no exception, no
                    // effect, nothing to catch. That silent failure was the
                    // reported "suspend doesn't work" bug. Fix: enable the
                    // privilege, then check the return value ourselves.
                    EnableShutdownPrivilege();
                    if (!SetSuspendState(false, true, false))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;

                case "Play A Sound":
                    if (!string.IsNullOrWhiteSpace(pending.Data) && File.Exists(pending.Data))
                        new System.Media.SoundPlayer(pending.Data).Play();
                    break;

                case "Execute A Command":
                    if (!string.IsNullOrWhiteSpace(pending.Data))
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {pending.Data}")
                        { CreateNoWindow = true, UseShellExecute = false });
                    break;

                case "Reopen Application":
                    if (!string.IsNullOrWhiteSpace(pending.Data))
                        Process.Start(new ProcessStartInfo(pending.Data) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"When Closed action failed: {ex.Message}");
        }
    }

    // ---- P/Invoke plumbing ----

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // BUGFIX support: SetSuspendState requires SE_SHUTDOWN_NAME to be
    // enabled on the calling process's token first (per its own MSDN page —
    // "The calling process must have the SE_SHUTDOWN_NAME privilege"). This
    // was missing, which is why sleep silently did nothing.
    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out LUID luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLengthInBytes, IntPtr previousState, IntPtr returnLengthInBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

// ----------------------------------------------------------------------------
// Tiny modal prompt for "Execute A Command". Same pattern as RenamePrompt.
// ----------------------------------------------------------------------------
internal sealed class CommandPrompt : Form
{
    private readonly TextBox _textBox = new() { Dock = DockStyle.Top };
    public string Command => _textBox.Text;

    public CommandPrompt()
    {
        Text = "Command to run when this window closes";
        Width = 420;
        Height = 120;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Top = 40, Left = 320 };
        Controls.Add(_textBox);
        Controls.Add(ok);
        AcceptButton = ok;
    }
}
