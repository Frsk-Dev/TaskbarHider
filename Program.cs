using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace TaskbarHider;

/// <summary>
/// Entry point for the TaskbarHider application.
/// </summary>
static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    const int SW_SHOW = 5;

    /// <summary>
    /// The main entry point for the application.
    /// Supports a --restore flag to force-show the taskbar and exit.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if ((args.Length > 0) && (args[0] == "--restore"))
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);

            if (taskbar != IntPtr.Zero)
            {
                ShowWindow(taskbar, SW_SHOW);
            }

            TaskbarHiderForm.ForceRestoreWorkArea();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TaskbarHiderForm());
    }
}

/// <summary>
/// A hidden form that manages the taskbar via a system tray icon and global hotkey (Ctrl+Alt+T).
/// Makes the primary taskbar transparent and click-through to reclaim the work area,
/// leaving secondary taskbars completely unaffected.
/// </summary>
class TaskbarHiderForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ScreenRect pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out ScreenRect lpRect);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// Callback delegate for EnumWindows.
    /// </summary>
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Matches the Windows RECT struct for work area and window position operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ScreenRect
    {
        /// <summary>
        /// The x-coordinate of the upper-left corner.
        /// </summary>
        public int Left;

        /// <summary>
        /// The y-coordinate of the upper-left corner.
        /// </summary>
        public int Top;

        /// <summary>
        /// The x-coordinate of the lower-right corner.
        /// </summary>
        public int Right;

        /// <summary>
        /// The y-coordinate of the lower-right corner.
        /// </summary>
        public int Bottom;
    }

    /// <summary>
    /// Represents an X/Y point on screen. Matches the Windows POINT struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint
    {
        /// <summary>
        /// The horizontal position of the cursor on screen.
        /// </summary>
        public int X;

        /// <summary>
        /// The vertical position of the cursor on screen.
        /// </summary>
        public int Y;
    }

    const int SW_MAXIMIZE = 3;
    const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    const int WM_HOTKEY = 0x0312;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_ALT = 0x0001;
    const uint VK_T = 0x54;
    const int HOTKEY_ID = 1;

    const uint SPI_GETWORKAREA = 0x0030;
    const uint SPI_SETWORKAREA = 0x002F;
    const uint SPIF_SENDCHANGE = 0x0002;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int WS_MAXIMIZE = 0x01000000;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const uint LWA_ALPHA = 0x02;

    const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "TaskbarHider";

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarHider",
        "settings.json");

    IntPtr taskbarHandle;
    bool taskbarHidden;
    int originalTaskbarExStyle;
    NotifyIcon trayIcon;
    System.Windows.Forms.Timer? autoHideTimer;
    IntPtr customIconHandle;
    ToolStripMenuItem? autoHideMenuItem;
    ScreenRect originalWorkArea;
    ScreenRect fullScreenRect;
    readonly List<IntPtr> resizedWindows = [];

    /// <summary>
    /// Initializes the form, loads saved settings, and sets up the tray icon and hotkey.
    /// </summary>
    public TaskbarHiderForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;

        taskbarHandle = FindWindow("Shell_TrayWnd", null);
        taskbarHidden = false;
        originalTaskbarExStyle = GetWindowLong(taskbarHandle, GWL_EXSTYLE);

        SaveOriginalWorkArea();

        RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_T);

        AppSettings settings = LoadSettings();
        trayIcon = CreateTrayIcon(settings.AutoHideEnabled);

        autoHideTimer = new System.Windows.Forms.Timer { Interval = 100 };
        autoHideTimer.Tick += OnAutoHideTick;

        if (settings.AutoHideEnabled)
        {
            HideTaskbar();
            autoHideTimer.Start();
        }
        else if (settings.TaskbarHidden)
        {
            HideTaskbar();
        }
    }

    /// <summary>
    /// Intercepts Windows messages to handle global hotkey presses.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if ((m.Msg == WM_HOTKEY) && (m.WParam.ToInt32() == HOTKEY_ID))
        {
            ToggleTaskbar();
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Toggles the taskbar between hidden and visible.
    /// If auto-hide is active and the user shows the taskbar, auto-hide is disabled.
    /// </summary>
    void ToggleTaskbar()
    {
        if (taskbarHandle == IntPtr.Zero)
        {
            return;
        }

        if (taskbarHidden)
        {
            if ((autoHideMenuItem != null) && (autoHideMenuItem.Checked))
            {
                autoHideMenuItem.Checked = false;
            }
            else
            {
                ShowTaskbar();
            }
        }
        else
        {
            HideTaskbar();
        }

        SaveSettings();
    }

    /// <summary>
    /// Makes the primary taskbar fully transparent and click-through instead of hiding it.
    /// This avoids triggering Explorer's work area recalculation. Sets the work area to
    /// full screen and smoothly extends maximized windows to fill the reclaimed space.
    /// </summary>
    void HideTaskbar()
    {
        if (taskbarHandle == IntPtr.Zero)
        {
            return;
        }

        MakeTaskbarTransparent();
        SetFullScreenWorkArea();
        taskbarHidden = true;
        RefreshMaximizedWindows();
    }

    /// <summary>
    /// Restores the primary taskbar to fully visible, restores the original work area,
    /// and smoothly shrinks maximized windows back to make room for the taskbar.
    /// </summary>
    void ShowTaskbar()
    {
        MakeTaskbarVisible();
        RestoreOriginalWorkArea();
        taskbarHidden = false;
        RefreshMaximizedWindows();
    }

    /// <summary>
    /// Makes the taskbar fully transparent and click-through by adding WS_EX_LAYERED
    /// and WS_EX_TRANSPARENT extended styles. Explorer still sees the taskbar as present
    /// so it does not fight the work area change.
    /// </summary>
    void MakeTaskbarTransparent()
    {
        int exStyle = GetWindowLong(taskbarHandle, GWL_EXSTYLE);
        SetWindowLong(taskbarHandle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetLayeredWindowAttributes(taskbarHandle, 0, 0, LWA_ALPHA);
    }

    /// <summary>
    /// Restores the taskbar to its original extended style, removing transparency
    /// and click-through so it is fully visible and interactive again.
    /// </summary>
    void MakeTaskbarVisible()
    {
        SetWindowLong(taskbarHandle, GWL_EXSTYLE, originalTaskbarExStyle);
    }

    /// <summary>
    /// Timer tick handler that shows/hides the taskbar based on cursor position.
    /// Also reinforces the full screen work area in case Explorer resets it.
    /// </summary>
    void OnAutoHideTick(object? sender, EventArgs e)
    {
        if ((taskbarHandle == IntPtr.Zero) || !GetCursorPos(out CursorPoint cursor))
        {
            return;
        }

        int screenHeight = Screen.PrimaryScreen?.Bounds.Height ?? 1080;
        const int triggerZone = 50;
        const int hideZone = 60;

        if ((cursor.Y >= screenHeight - triggerZone) && (taskbarHidden))
        {
            MakeTaskbarVisible();
            taskbarHidden = false;
        }
        else if ((cursor.Y < screenHeight - hideZone) && (!taskbarHidden))
        {
            MakeTaskbarTransparent();
            taskbarHidden = true;
        }

        // Reinforce full screen work area in case Explorer reset it.
        if (taskbarHidden)
        {
            EnforceFullScreenWorkArea();
        }
    }

    /// <summary>
    /// Checks if the current work area matches full screen and reapplies it if Explorer reset it.
    /// Windows resized via SetWindowPos are unaffected by work area changes so no refresh is needed.
    /// </summary>
    void EnforceFullScreenWorkArea()
    {
        var current = new ScreenRect();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref current, 0);

        if ((current.Left != fullScreenRect.Left) || (current.Top != fullScreenRect.Top)
            || (current.Right != fullScreenRect.Right) || (current.Bottom != fullScreenRect.Bottom))
        {
            SetFullScreenWorkArea();
        }
    }

    /// <summary>
    /// Smoothly resizes maximized windows on the primary monitor by adjusting the bottom
    /// edge to match the current work area. When hiding, extends windows to full screen.
    /// When showing, shrinks windows back and re-maximizes to restore proper window state.
    /// </summary>
    void RefreshMaximizedWindows()
    {
        IntPtr primaryMonitor = MonitorFromWindow(taskbarHandle, MONITOR_DEFAULTTONEAREST);

        if (taskbarHidden)
        {
            resizedWindows.Clear();

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd) && IsZoomed(hWnd) && (hWnd != Handle))
                {
                    IntPtr windowMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);

                    if (windowMonitor == primaryMonitor)
                    {
                        GetWindowRect(hWnd, out ScreenRect windowRect);

                        // The bottom frame inset is how far the window extends past the work area.
                        int frameBottom = windowRect.Bottom - originalWorkArea.Bottom;
                        int newHeight = (fullScreenRect.Bottom + frameBottom) - windowRect.Top;

                        SetWindowPos(hWnd, IntPtr.Zero, 0, 0,
                            windowRect.Right - windowRect.Left, newHeight,
                            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

                        // Remove maximized state so Explorer's work area resets cannot shrink
                        // the window back. SW_MAXIMIZE in the show path restores this later.
                        int style = GetWindowLong(hWnd, GWL_STYLE);
                        SetWindowLong(hWnd, GWL_STYLE, style & ~WS_MAXIMIZE);

                        resizedWindows.Add(hWnd);
                    }
                }

                return true;
            }, IntPtr.Zero);
        }
        else
        {
            // Collect tracked windows and any newly maximized windows.
            var windowsToRestore = new HashSet<IntPtr>(resizedWindows);

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd) && IsZoomed(hWnd) && (hWnd != Handle))
                {
                    IntPtr windowMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);

                    if (windowMonitor == primaryMonitor)
                    {
                        windowsToRestore.Add(hWnd);
                    }
                }

                return true;
            }, IntPtr.Zero);

            foreach (IntPtr hWnd in windowsToRestore)
            {
                if (!IsWindow(hWnd) || !IsWindowVisible(hWnd))
                {
                    continue;
                }

                GetWindowRect(hWnd, out ScreenRect windowRect);

                // Shrink bottom edge back to original work area.
                int frameBottom = windowRect.Bottom - fullScreenRect.Bottom;
                int newHeight = (originalWorkArea.Bottom + frameBottom) - windowRect.Top;

                SetWindowPos(hWnd, IntPtr.Zero, 0, 0,
                    windowRect.Right - windowRect.Left, newHeight,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

                // Re-maximize to restore proper window state (title bar double-click, snap, etc).
                ShowWindow(hWnd, SW_MAXIMIZE);
            }

            resizedWindows.Clear();
        }
    }

    /// <summary>
    /// Creates the system tray icon with a context menu.
    /// </summary>
    NotifyIcon CreateTrayIcon(bool autoHideEnabled)
    {
        var menu = new ContextMenuStrip();

        if (IsDarkTheme())
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            menu.ForeColor = Color.FromArgb(245, 245, 245);
        }

        var hideItem = new ToolStripMenuItem("Hide Taskbar");
        hideItem.Click += (sender, e) =>
        {
            HideTaskbar();
            SaveSettings();
        };

        autoHideMenuItem = new ToolStripMenuItem("Auto-hide (slide up)")
        {
            CheckOnClick = true,
            Checked = autoHideEnabled
        };

        autoHideMenuItem.CheckedChanged += (sender, e) =>
        {
            if (autoHideMenuItem.Checked)
            {
                HideTaskbar();
                autoHideTimer?.Start();
            }
            else
            {
                autoHideTimer?.Stop();
                ShowTaskbar();
            }

            SaveSettings();
        };

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };

        startupItem.CheckedChanged += (sender, e) =>
        {
            if (startupItem.Checked)
            {
                EnableAutoStart();
            }
            else
            {
                DisableAutoStart();
            }
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (sender, e) =>
        {
            Application.Exit();
        };

        menu.Items.Add(hideItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoHideMenuItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        Icon trayIconImage;
        Stream? iconStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TaskbarHider.trayicon.png");

        if (iconStream != null)
        {
            using var bitmap = new Bitmap(iconStream);
            customIconHandle = bitmap.GetHicon();
            trayIconImage = Icon.FromHandle(customIconHandle);
        }
        else
        {
            trayIconImage = SystemIcons.Application;
        }

        return new NotifyIcon
        {
            Icon = trayIconImage,
            Text = "TaskbarHider — Ctrl+Alt+T to toggle",
            Visible = true,
            ContextMenuStrip = menu
        };
    }

    /// <summary>
    /// Restores the taskbar, unregisters the hotkey, and cleans up resources.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        autoHideTimer?.Stop();
        autoHideTimer?.Dispose();

        MakeTaskbarVisible();
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref originalWorkArea, SPIF_SENDCHANGE);

        UnregisterHotKey(Handle, HOTKEY_ID);

        trayIcon.Visible = false;
        trayIcon.Dispose();

        if (customIconHandle != IntPtr.Zero)
        {
            DestroyIcon(customIconHandle);
        }

        base.OnFormClosing(e);
    }

    /// <summary>
    /// Saves the current settings to a JSON file.
    /// </summary>
    void SaveSettings()
    {
        var settings = new AppSettings
        {
            AutoHideEnabled = autoHideMenuItem?.Checked ?? false,
            TaskbarHidden = taskbarHidden
        };

        string directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(settings);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>
    /// Loads settings from the JSON file. Returns default settings if the file is missing or corrupt.
    /// </summary>
    static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Checks if the user has Windows dark mode enabled.
    /// </summary>
    static bool IsDarkTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        object? value = key?.GetValue("AppsUseLightTheme");
        return (value is int intValue) && (intValue == 0);
    }

    /// <summary>
    /// Checks if the app is set to auto-start with Windows.
    /// </summary>
    static bool IsAutoStartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey);
        string? value = key?.GetValue(AppName)?.ToString();
        return value != null;
    }

    /// <summary>
    /// Registers the app to start automatically when Windows logs in.
    /// </summary>
    static void EnableAutoStart()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        key?.SetValue(AppName, Application.ExecutablePath);
    }

    /// <summary>
    /// Removes the auto-start registry entry.
    /// </summary>
    static void DisableAutoStart()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        key?.DeleteValue(AppName, false);
    }

    /// <summary>
    /// Saves the current work area and computes the full screen rect for the primary monitor.
    /// </summary>
    void SaveOriginalWorkArea()
    {
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref originalWorkArea, 0);

        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        fullScreenRect = new ScreenRect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };
    }

    /// <summary>
    /// Sets the primary monitor work area to full screen so maximized windows fill the screen.
    /// Does not broadcast to avoid Explorer reclaiming control.
    /// </summary>
    void SetFullScreenWorkArea()
    {
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref fullScreenRect, 0);
    }

    /// <summary>
    /// Restores the primary monitor work area to what it was before the app launched.
    /// Does not broadcast SPIF_SENDCHANGE to avoid Explorer taking control of the work area.
    /// RefreshMaximizedWindows handles resizing existing maximized windows manually.
    /// </summary>
    void RestoreOriginalWorkArea()
    {
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref originalWorkArea, 0);
    }

    /// <summary>
    /// Restores the taskbar visibility and work area for crash recovery via --restore.
    /// Removes transparency and click-through styles in case the app crashed while
    /// the taskbar was made transparent.
    /// </summary>
    internal static void ForceRestoreWorkArea()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);

        if (taskbar != IntPtr.Zero)
        {
            int exStyle = GetWindowLong(taskbar, GWL_EXSTYLE);
            SetWindowLong(taskbar, GWL_EXSTYLE, exStyle & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT));
        }

        var currentWorkArea = new ScreenRect();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref currentWorkArea, 0);

        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        // If work area matches the screen bounds, Explorer needs a nudge to recalculate.
        if ((currentWorkArea.Left == bounds.Left) && (currentWorkArea.Top == bounds.Top)
            && (currentWorkArea.Right == bounds.Right) && (currentWorkArea.Bottom == bounds.Bottom))
        {
            var nudged = new ScreenRect
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Right,
                Bottom = bounds.Bottom - 1
            };
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref nudged, SPIF_SENDCHANGE);
        }

        // Broadcast with SENDCHANGE so Explorer recalculates using the real taskbar.
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref currentWorkArea, SPIF_SENDCHANGE);
    }
}

/// <summary>
/// Stores user preferences that persist between app restarts.
/// </summary>
class AppSettings
{
    /// <summary>
    /// Whether the auto-hide feature was enabled.
    /// </summary>
    public bool AutoHideEnabled { get; set; }

    /// <summary>
    /// Whether the taskbar was manually hidden.
    /// </summary>
    public bool TaskbarHidden { get; set; }
}

/// <summary>
/// Provides dark color values for the tray menu when Windows dark mode is active.
/// </summary>
class DarkMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(43, 43, 43);

    public override Color ImageMarginGradientBegin => Color.FromArgb(43, 43, 43);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(43, 43, 43);
    public override Color ImageMarginGradientEnd => Color.FromArgb(43, 43, 43);

    public override Color MenuItemSelected => Color.FromArgb(62, 62, 66);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(62, 62, 66);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(62, 62, 66);

    public override Color MenuItemPressedGradientBegin => Color.FromArgb(62, 62, 66);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(62, 62, 66);

    public override Color MenuItemBorder => Color.FromArgb(62, 62, 66);

    public override Color MenuBorder => Color.FromArgb(51, 51, 51);

    public override Color SeparatorDark => Color.FromArgb(61, 61, 67);
    public override Color SeparatorLight => Color.FromArgb(61, 61, 67);

    public override Color CheckBackground => Color.FromArgb(62, 62, 66);
    public override Color CheckSelectedBackground => Color.FromArgb(82, 82, 86);
    public override Color CheckPressedBackground => Color.FromArgb(82, 82, 86);
}
