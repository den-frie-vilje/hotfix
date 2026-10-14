using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hotfix;

/// <summary>The notification-area icon and its menu; the whole user interface.</summary>
sealed class TrayContext : ApplicationContext
{
    const string CompanyKey = @"Software\Den Frie Vilje";
    const string SettingsKey = CompanyKey + @"\Hotfix";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Page = "https://den-frie-vilje.github.io/hotfix/";

    enum Command { None, Cpu, Gpu, Autostart, About, Exit }

    readonly NotifyIcon tray;
    readonly NativeWindow owner = new();
    readonly System.Windows.Forms.Timer status = new() { Interval = 1000 };
    readonly Icon[] gauge = Enumerable.Range(0, 5).Select(Fins).ToArray(); // nought to four fins lit
    readonly string gpuLabel, version = Application.ProductVersion.Split('+')[0];
    bool cpu, gpu;

    public TrayContext()
    {
        Heater.Start();
        owner.CreateHandle(new CreateParams());
        FollowDarkMode(owner.Handle);

        bool firstRun;
        using (var settings = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            firstRun = settings == null;
            cpu = Read(settings, "Cpu", true);
            // Without idle GPU priority the shader would compete with your work as an equal, so it defaults to off.
            gpu = Heater.Device != null && Read(settings, "Gpu", Heater.GpuIdle);
        }
        gpuLabel = Heater.Device == null ? "Warm with &GPU (no DirectX 12 GPU found)"
            : Heater.GpuIdle ? $"Warm with &GPU ({Heater.Device.Name})"
            : "Warm with &GPU (no idle priority, will compete)";

        tray = new NotifyIcon { Visible = true };
        tray.MouseUp += (_, _) => ShowMenu(); // left or right click, like Windows' own tray icons
        Apply();
        status.Tick += (_, _) => ShowStatus(); // the watcher turns the heat up and down on its own
        status.Start();

        if (firstRun)
            tray.ShowBalloonTip(8000, "Hotfix is warming the room", "Select this icon to choose the CPU, the GPU or both.", ToolTipIcon.None);
    }

    void ShowMenu()
    {
        // A native popup menu, so it is drawn by Windows itself: rounded, themed, keyboard-navigable.
        var menu = CreatePopupMenu();
        Add(menu, Command.Cpu, "Warm with &CPU", cpu);
        Add(menu, Command.Gpu, gpuLabel, gpu, Heater.Device != null);
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        Add(menu, Command.Autostart, "&Start with Windows", Autostart);
        Add(menu, Command.About, $"&About Hotfix {version}");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        Add(menu, Command.Exit, "E&xit");

        SetForegroundWindow(owner.Handle); // otherwise the menu stays open when you click elsewhere
        var at = Cursor.Position;
        int align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : 0;
        var command = (Command)TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align, at.X, at.Y, owner.Handle, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case Command.Cpu: cpu = !cpu; Apply(); break;
            case Command.Gpu: gpu = !gpu; Apply(); break;
            case Command.Autostart: Autostart = !Autostart; break;
            case Command.About: Process.Start(new ProcessStartInfo(Page) { UseShellExecute = true }); break;
            case Command.Exit: ExitThread(); break;
        }
    }

    void Apply()
    {
        Heater.Want(cpu, gpu);
        ShowStatus();
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        settings.SetValue("Cpu", cpu ? 1 : 0);
        if (Heater.Device != null) settings.SetValue("Gpu", gpu ? 1 : 0);
    }

    void ShowStatus()
    {
        var (cpuHeat, gpuHeat) = Heater.Heat;
        var wanted = new List<(string Name, double Heat)>();
        if (cpu) wanted.Add(("CPU", cpuHeat));
        if (gpu) wanted.Add(("GPU", gpuHeat));
        if (wanted.Count == 0)
        {
            tray.Icon = gauge[0];
            tray.Text = "Hotfix: off";
            return;
        }
        double mean = wanted.Average(w => w.Heat);
        tray.Icon = gauge[mean > 0 ? Math.Max(1, (int)Math.Round(mean * 4)) : 0]; // any warmth shows at least one fin
        tray.Text = "Hotfix: " + string.Join(", ", wanted.Select(w => $"{w.Name} {w.Heat:P0}"))
            + (wanted.Any(w => w.Heat < 1) ? "; turned down while you work" : "");
    }

    protected override void ExitThreadCore()
    {
        status.Stop();
        tray.Visible = false; // otherwise a dead icon lingers until the pointer passes over it
        owner.DestroyHandle();
        base.ExitThreadCore();
    }

    static bool Autostart
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue("Hotfix") != null;
        }
        set
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) run.SetValue("Hotfix", $"\"{Environment.ProcessPath}\"");
            else run.DeleteValue("Hotfix", false);
        }
    }

    /// <summary>Removes everything Hotfix wrote to the registry; the uninstaller calls this.</summary>
    public static void Forget()
    {
        Autostart = false;
        Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false);
        using var company = Registry.CurrentUser.OpenSubKey(CompanyKey);
        if (company is { SubKeyCount: 0, ValueCount: 0 })
            Registry.CurrentUser.DeleteSubKey(CompanyKey, false);
    }

    static bool Read(RegistryKey? key, string name, bool fallback) => key?.GetValue(name) is int v ? v != 0 : fallback;

    /// <summary>The app icon's four radiator fins at the tray's size, the first <paramref name="lit"/> in colour.</summary>
    static Icon Fins(int lit)
    {
        string[] heat = ["#C42B1C", "#E8590C", "#F59F00", "#FCC419"];
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float k = size / 256f; // the fins are drawn on a 256 grid: 48 wide, 16 apart, full-height pills
            for (int i = 0; i < 4; i++)
            {
                float x = (8 + i * 64) * k, y = 16 * k, w = 48 * k, h = 224 * k;
                using var fin = new System.Drawing.Drawing2D.GraphicsPath();
                fin.AddArc(x, y, w, w, 180, 180);
                fin.AddArc(x, y + h - w, w, w, 0, 180);
                fin.CloseFigure();
                using var brush = new SolidBrush(ColorTranslator.FromHtml(i < lit ? heat[i] : "#8A8A8A"));
                g.FillPath(brush, fin);
            }
        }
        return Icon.FromHandle(bmp.GetHicon()); // shortcut: the HICONs are never freed, fine for five per run
    }

    static void Add(IntPtr menu, Command id, string text, bool check = false, bool enabled = true) =>
        AppendMenu(menu, (check ? MF_CHECKED : 0) | (enabled ? 0 : MF_GRAYED), (nint)id, text);

    static void FollowDarkMode(IntPtr window)
    {
        // shortcut: uxtheme's dark menus are exported by ordinal only (as Notepad++ and Explorer clones use them);
        // on a Windows without them the menu stays light. Upgrade if Windows ever documents an API.
        try
        {
            SetPreferredAppMode(1); // AllowDark: follow the app theme in Settings
            AllowDarkModeForWindow(window, true);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }
    }

    const int MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    const int TPM_RIGHTBUTTON = 0x2, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
    const int SM_MENUDROPALIGNMENT = 40;

    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, int flags, nint id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr window, bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
}
