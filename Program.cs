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

            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TaskbarHiderForm());
    }
}

/// <summary>
/// A hidden form that manages the taskbar via a system tray icon and global hotkey (Ctrl+Alt+T).
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

    const int SW_HIDE = 0;
    const int SW_SHOW = 5;
    const int WM_HOTKEY = 0x0312;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_ALT = 0x0001;
    const uint VK_T = 0x54;
    const int HOTKEY_ID = 1;

    const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "TaskbarHider";

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarHider",
        "settings.json");

    IntPtr taskbarHandle;
    bool taskbarHidden;
    NotifyIcon trayIcon;
    System.Windows.Forms.Timer? autoHideTimer;
    IntPtr customIconHandle;
    ToolStripMenuItem? autoHideMenuItem;

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

        RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_T);

        AppSettings settings = LoadSettings();
        trayIcon = CreateTrayIcon(settings.AutoHideEnabled);

        autoHideTimer = new System.Windows.Forms.Timer { Interval = 100 };
        autoHideTimer.Tick += OnAutoHideTick;

        if (settings.AutoHideEnabled)
        {
            if (taskbarHandle != IntPtr.Zero)
            {
                ShowWindow(taskbarHandle, SW_HIDE);
                taskbarHidden = true;
            }

            autoHideTimer.Start();
        }
        else if (settings.TaskbarHidden)
        {
            if (taskbarHandle != IntPtr.Zero)
            {
                ShowWindow(taskbarHandle, SW_HIDE);
                taskbarHidden = true;
            }
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
            ShowWindow(taskbarHandle, SW_SHOW);
            taskbarHidden = false;

            if ((autoHideMenuItem != null) && (autoHideMenuItem.Checked))
            {
                autoHideMenuItem.Checked = false;
            }
        }
        else
        {
            ShowWindow(taskbarHandle, SW_HIDE);
            taskbarHidden = true;
        }

        SaveSettings();
    }

    /// <summary>
    /// Timer tick handler that shows/hides the taskbar based on cursor position.
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
            ShowWindow(taskbarHandle, SW_SHOW);
            taskbarHidden = false;
        }
        else if ((cursor.Y < screenHeight - hideZone) && (!taskbarHidden))
        {
            ShowWindow(taskbarHandle, SW_HIDE);
            taskbarHidden = true;
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
            if (taskbarHandle != IntPtr.Zero)
            {
                ShowWindow(taskbarHandle, SW_HIDE);
                taskbarHidden = true;
                SaveSettings();
            }
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
                if (taskbarHandle != IntPtr.Zero)
                {
                    ShowWindow(taskbarHandle, SW_HIDE);
                    taskbarHidden = true;
                }

                autoHideTimer?.Start();
            }
            else
            {
                autoHideTimer?.Stop();

                if (taskbarHandle != IntPtr.Zero)
                {
                    ShowWindow(taskbarHandle, SW_SHOW);
                    taskbarHidden = false;
                }
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
        if (taskbarHandle != IntPtr.Zero)
        {
            ShowWindow(taskbarHandle, SW_SHOW);
        }

        UnregisterHotKey(Handle, HOTKEY_ID);

        autoHideTimer?.Stop();
        autoHideTimer?.Dispose();

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
