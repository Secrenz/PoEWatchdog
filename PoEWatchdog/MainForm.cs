// PoE Watchdog für Huawei S5731-S
// Benötigt NuGet-Paket: SSH.NET
// Eine einzige Datei – kein Form1, kein Program.cs nötig.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Renci.SshNet;

[STAThread]
static void AppMain(string[] args)
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm(Array.IndexOf(args, "--autostart") >= 0));
}
AppMain(Environment.GetCommandLineArgs()[1..]);

// ═════════════════════════════════════════════════════════════════════════════
//  EIN EINZELNER ÜBERWACHUNGS-EINTRAG (ein Switch/Port-Paar)
// ═════════════════════════════════════════════════════════════════════════════
public class WatchEntry
{
    public string Name      { get; set; } = "Neue Überwachung";
    public string MonitorIP { get; set; } = "";
    public string SwitchIP  { get; set; } = "";
    public string User      { get; set; } = "";
    public string PoEPort   { get; set; } = "";
    public int    Interval  { get; set; } = 30;
    public int    Retries   { get; set; } = 3;
    public int    PoEOff    { get; set; } = 10;
    public bool   SavePassword      { get; set; } = false;
    public string PasswordEncrypted { get; set; } = "";

    // ── Laufzeit-only, wird nicht gespeichert ───────────────────────────────
    [JsonIgnore] public string Password = "";
    [JsonIgnore] public CancellationTokenSource? Cts;
    [JsonIgnore] public int FailCount;
    [JsonIgnore] public int ResetCount;
    [JsonIgnore] public string Status = "Gestoppt";
    [JsonIgnore] public ListViewItem? Item;
    [JsonIgnore] public bool IsRunning => Cts != null && !Cts.IsCancellationRequested;
}

// ═════════════════════════════════════════════════════════════════════════════
public class MainForm : Form
{
    // ── Überwachungen ─────────────────────────────────────────────────────────
    private readonly List<WatchEntry> entries = new();
    private ListView         lvEntries  = new();
    private ComboBox         cmbTarget  = new();
    private Button           btnEdit    = new();

    private CheckBox        chkAutoStartWin       = new();
    private CheckBox        chkAutoStartAll       = new();

    // ── Tray ──────────────────────────────────────────────────────────────────
    private NotifyIcon         trayIcon = new();
    private ContextMenuStrip   trayMenu = new();
    private ToolStripMenuItem  trayToggleItem = new();
    private bool               _exitRequested  = false;
    private readonly bool      _startedMinimized;

    const string AutoStartRegKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AutoStartValName = "PoEWatchdog";

    // ── Persistenz ────────────────────────────────────────────────────────────
    static readonly string SettingsDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoEWatchdog");
    static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    static readonly string LogDir       = Path.Combine(SettingsDir, "logs");
    const long   MaxLogFileBytes  = 2 * 1024 * 1024; // 2 MB pro Datei
    const int    LogRetentionDays = 30;               // ältere Dateien automatisch löschen

    // Aktuelle Log-Datei: ein Name pro Tag, bei Überschreiten von MaxLogFileBytes
    // wird automatisch _2, _3, ... angehängt (Größen-Rotation innerhalb des Tages).
    static string LogPath
    {
        get
        {
            Directory.CreateDirectory(LogDir);
            string baseDate = DateTime.Now.ToString("yyyy-MM-dd");
            string path = Path.Combine(LogDir, $"poewatchdog_{baseDate}.log");

            int part = 1;
            while (File.Exists(path) && new FileInfo(path).Length >= MaxLogFileBytes)
            {
                part++;
                path = Path.Combine(LogDir, $"poewatchdog_{baseDate}_{part}.log");
            }
            return path;
        }
    }
    static readonly object LogFileLock  = new();

    // ── UI ────────────────────────────────────────────────────────────────────
    private RichTextBox     rtbLog        = new();
    private Label           lblStatus     = new();
    private Panel           pnlDot        = new();
    private Color           _dotColor     = Color.Gray;

    // Begrenzung der Log-Anzeige im UI, damit der RAM-Verbrauch über lange
    // Laufzeiten nicht unbegrenzt wächst (die Datei auf der Platte bleibt vollständig).
    const int MaxLogTextChars = 300_000;

    // ═════════════════════════════════════════════════════════════════════════
    public MainForm(bool startedMinimized = false)
    {
        _startedMinimized = startedMinimized;
        BuildUI();
        BuildTray();
        LoadSettings();

        FormClosing += MainForm_FormClosing;
        Resize       += MainForm_Resize;
        Load         += MainForm_Load;
    }

    void MainForm_Load(object? sender, EventArgs e)
    {
        if (_startedMinimized)
        {
            Hide();
            ShowInTaskbar = false;
        }

        if (chkAutoStartAll.Checked)
        {
            if (entries.Count == 0)
                Log("Automatischer Start übersprungen – keine Überwachungen konfiguriert.", Level.Warn);
            else
            {
                Log("Automatischer Start aller Überwachungen (Einstellung aktiv) …", Level.Info);
                StartAll();
            }
        }
    }

    void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            using var dlg = new CloseChoiceDialog();
            var result = dlg.ShowDialog(this);

            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (dlg.GoToTray)
            {
                e.Cancel = true;
                SaveSettings();
                Hide();
                ShowInTaskbar = false;
                trayIcon.ShowBalloonTip(1500, "PoE Watchdog",
                    entries.Any(en => en.IsRunning) ? "Läuft im Hintergrund weiter." : "In den Tray gelegt.",
                    ToolTipIcon.Info);
                return;
            }

            _exitRequested = true; // Nutzer hat "Beenden" gewählt
        }

        SaveSettings();
        trayIcon.Visible = false;
    }

    void BuildTray()
    {
        trayToggleItem.Text  = "Öffnen";
        trayToggleItem.Click += (_, _) => ShowFromTray();

        var itemStart = new ToolStripMenuItem("Alle starten");
        itemStart.Click += (_, _) => StartAll();

        var itemStop = new ToolStripMenuItem("Alle stoppen");
        itemStop.Click += (_, _) => StopAll();

        var itemExit = new ToolStripMenuItem("Beenden");
        itemExit.Click += (_, _) => { _exitRequested = true; Close(); };

        trayMenu.Items.AddRange(new ToolStripItem[] {
            trayToggleItem, new ToolStripSeparator(), itemStart, itemStop,
            new ToolStripSeparator(), itemExit
        });

        trayIcon.Text          = "Huawei PoE Watchdog";
        trayIcon.Icon          = SystemIcons.Shield;
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible       = true;
        trayIcon.DoubleClick  += (_, _) => ShowFromTray();
    }

    void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState   = FormWindowState.Normal;
        Activate();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  GUI AUFBAU
    // ─────────────────────────────────────────────────────────────────────────
    void BuildUI()
    {
        // Fenster
        Text            = "Huawei PoE Watchdog";
        Size            = new Size(680, 780);
        MinimumSize     = new Size(680, 780);
        BackColor       = Color.FromArgb(28, 28, 28);
        ForeColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;

        // ── Statusleiste ──────────────────────────────────────────────────
        var bar = MakePanel(0, 0, 680, 42, Color.FromArgb(40, 40, 40));

        pnlDot.Size      = new Size(14, 14);
        pnlDot.Location  = new Point(14, 14);
        pnlDot.BackColor = Color.Transparent;
        pnlDot.Paint    += (s, e) => {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var br = new SolidBrush(_dotColor);
            e.Graphics.FillEllipse(br, 0, 0, 13, 13);
        };

        lblStatus.AutoSize  = true;
        lblStatus.Location  = new Point(36, 12);
        lblStatus.Text      = "Keine Überwachungen";
        lblStatus.ForeColor = Color.Silver;
        lblStatus.Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        bar.Controls.AddRange(new Control[] { pnlDot, lblStatus });

        // ── Überwachungen (Liste) ───────────────────────────────────────────
        var entriesPanel = MakePanel(12, 52, 648, 280, Color.FromArgb(38, 38, 38));
        SectionLabel(entriesPanel, "📡  Überwachungen", 10, 8);

        lvEntries.View          = View.Details;
        lvEntries.Location      = new Point(10, 30);
        lvEntries.Size          = new Size(628, 170);
        lvEntries.FullRowSelect = true;
        lvEntries.GridLines     = true;
        lvEntries.MultiSelect   = false;
        lvEntries.BackColor     = Color.FromArgb(18, 18, 18);
        lvEntries.ForeColor     = Color.White;
        lvEntries.BorderStyle   = BorderStyle.FixedSingle;
        lvEntries.Font          = new Font("Segoe UI", 8.5f);
        lvEntries.Columns.Add("Name", 110);
        lvEntries.Columns.Add("Überwachte IP", 105);
        lvEntries.Columns.Add("Switch IP", 100);
        lvEntries.Columns.Add("Port", 130);
        lvEntries.Columns.Add("Status", 165);

        var btnAdd      = SmallBtn("Hinzufügen", Color.FromArgb(58, 58, 58), 10,  208, 86);
        btnEdit         = SmallBtn("Bearbeiten", Color.FromArgb(58, 58, 58), 100, 208, 86);
        var btnRemove   = SmallBtn("Entfernen",  Color.FromArgb(120, 50, 50), 190, 208, 86);
        var btnStartSel = SmallBtn("Start",      Color.FromArgb(40, 130, 70), 286, 208, 64);
        var btnStopSel  = SmallBtn("Stop",       Color.FromArgb(120, 70, 30), 354, 208, 64);
        var btnStartAll = SmallBtn("Start alle", Color.FromArgb(0, 110, 190), 426, 208, 96);
        var btnStopAll  = SmallBtn("Stop alle",  Color.FromArgb(70, 70, 70),  526, 208, 96);

        btnAdd.Click      += (_, _) => OnAddEntry();
        btnEdit.Click      += (_, _) => OnEditEntry();
        btnRemove.Click   += (_, _) => OnRemoveEntry();
        btnStartSel.Click += (_, _) => { var sel = GetSelectedEntry(); if (sel != null) StartEntry(sel); };
        btnStopSel.Click  += (_, _) => { var sel = GetSelectedEntry(); if (sel != null) StopEntry(sel); };
        btnStartAll.Click += (_, _) => StartAll();
        btnStopAll.Click  += (_, _) => StopAll();
        lvEntries.DoubleClick += (_, _) => OnEditEntry();

        chkAutoStartWin.Text      = "Mit Windows starten";
        chkAutoStartWin.AutoSize  = true;
        chkAutoStartWin.ForeColor = Color.Silver;
        chkAutoStartWin.Location  = new Point(10, 244);
        chkAutoStartWin.CheckedChanged += (_, _) => SetAutoStartWithWindows(chkAutoStartWin.Checked);

        chkAutoStartAll.Text      = "Beim Start alle Überwachungen automatisch starten";
        chkAutoStartAll.AutoSize  = true;
        chkAutoStartAll.ForeColor = Color.Silver;
        chkAutoStartAll.Location  = new Point(170, 244);
        chkAutoStartAll.CheckedChanged += (_, _) => SaveSettings();

        entriesPanel.Controls.AddRange(new Control[] {
            lvEntries, btnAdd, btnEdit, btnRemove, btnStartSel, btnStopSel, btnStartAll, btnStopAll,
            chkAutoStartWin, chkAutoStartAll
        });

        // ── Manuelle Aktionen ─────────────────────────────────────────────
        var manual = MakePanel(12, 342, 648, 90, Color.FromArgb(38, 38, 38));
        SectionLabel(manual, "🖱  Manuelle Aktion für:", 10, 8);

        cmbTarget.Location    = new Point(220, 5);
        cmbTarget.Size        = new Size(260, 24);
        cmbTarget.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbTarget.BackColor   = Color.FromArgb(48, 48, 48);
        cmbTarget.ForeColor   = Color.White;

        var btnPoEOff = MakeActionBtn("⚡ PoE AUS",   Color.FromArgb(180, 60, 60),  10,  36);
        var btnPoEOn  = MakeActionBtn("⚡ PoE EIN",   Color.FromArgb(40, 140, 70),  170, 36);
        var btnIfDown = MakeActionBtn("🔽 Port DOWN", Color.FromArgb(160, 100, 20), 330, 36);
        var btnIfUp   = MakeActionBtn("🔼 Port UP",   Color.FromArgb(30, 110, 160), 490, 36);
        // Buttons etwas kompakter, damit sie in die niedrigere Manual-Leiste passen
        foreach (var b in new[] { btnPoEOff, btnPoEOn, btnIfDown, btnIfUp }) b.Size = new Size(150, 32);

        btnPoEOff.Click += async (_, _) => {
            var t = GetTargetEntry(); if (t == null) { WarnNoTarget(); return; }
            await ManualAction(t, "PoE deaktivieren",
                new[] { "system-view", $"interface {t.PoEPort}", "undo poe enable", "quit", "quit" });
        };
        btnPoEOn.Click += async (_, _) => {
            var t = GetTargetEntry(); if (t == null) { WarnNoTarget(); return; }
            await ManualAction(t, "PoE aktivieren",
                new[] { "system-view", $"interface {t.PoEPort}", "poe enable", "quit", "quit" });
        };
        btnIfDown.Click += async (_, _) => {
            var t = GetTargetEntry(); if (t == null) { WarnNoTarget(); return; }
            await ManualAction(t, "Port herunterfahren",
                new[] { "system-view", $"interface {t.PoEPort}", "shutdown", "quit", "quit" });
        };
        btnIfUp.Click += async (_, _) => {
            var t = GetTargetEntry(); if (t == null) { WarnNoTarget(); return; }
            await ManualAction(t, "Port hochfahren",
                new[] { "system-view", $"interface {t.PoEPort}", "undo shutdown", "quit", "quit" });
        };

        manual.Controls.AddRange(new Control[] { cmbTarget, btnPoEOff, btnPoEOn, btnIfDown, btnIfUp });

        // ── Log ───────────────────────────────────────────────────────────
        var logBox = MakePanel(12, 442, 648, 288, Color.FromArgb(38, 38, 38));
        SectionLabel(logBox, "📋  Protokoll", 10, 8);

        var btnOpenLog = new Button {
            Text = "Log öffnen", Location = new Point(498, 5), Size = new Size(72, 22),
            BackColor = Color.FromArgb(58, 58, 58), ForeColor = Color.Silver,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7.5f), Cursor = Cursors.Hand
        };
        btnOpenLog.FlatAppearance.BorderSize = 0;
        btnOpenLog.Click += (_, _) => OpenLogFile();

        var btnClear = new Button {
            Text = "Leeren", Location = new Point(576, 5), Size = new Size(62, 22),
            BackColor = Color.FromArgb(58, 58, 58), ForeColor = Color.Silver,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7.5f), Cursor = Cursors.Hand
        };
        btnClear.FlatAppearance.BorderSize = 0;
        btnClear.Click += (_, _) => rtbLog.Clear();

        rtbLog.Location    = new Point(8, 30);
        rtbLog.Size        = new Size(630, 246);
        rtbLog.BackColor   = Color.FromArgb(18, 18, 18);
        rtbLog.ForeColor   = Color.FromArgb(200, 200, 200);
        rtbLog.Font        = new Font("Consolas", 8.5f);
        rtbLog.ReadOnly    = true;
        rtbLog.BorderStyle = BorderStyle.None;
        rtbLog.ScrollBars  = RichTextBoxScrollBars.Vertical;
        logBox.Controls.AddRange(new Control[] { btnOpenLog, btnClear, rtbLog });

        // ── Alles auf Form ────────────────────────────────────────────────
        Controls.AddRange(new Control[] { bar, entriesPanel, manual, logBox });

        Log("Bereit. Überwachung über „Hinzufügen“ anlegen.", Level.Info);
        Log($"Protokollordner: {LogDir}", Level.Info);
        CleanupOldLogs();
    }

    void WarnNoTarget() =>
        MessageBox.Show("Bitte zuerst eine Überwachung anlegen und im Dropdown auswählen.",
            "Keine Auswahl", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ─────────────────────────────────────────────────────────────────────────
    //  ÜBERWACHUNGEN VERWALTEN
    // ─────────────────────────────────────────────────────────────────────────
    WatchEntry? GetSelectedEntry() =>
        lvEntries.SelectedItems.Count > 0 ? (WatchEntry)lvEntries.SelectedItems[0].Tag : null;

    WatchEntry? GetTargetEntry() =>
        cmbTarget.SelectedIndex >= 0 && cmbTarget.SelectedIndex < entries.Count
            ? entries[cmbTarget.SelectedIndex] : null;

    void OnAddEntry()
    {
        using var dlg = new EntryEditDialog(null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        entries.Add(dlg.Result);
        RefreshListView();
        RefreshCombo();
        SaveSettings();
        Log($"Überwachung „{dlg.Result.Name}“ hinzugefügt.", Level.Info);
    }

    void OnEditEntry()
    {
        var sel = GetSelectedEntry();
        if (sel == null)
        {
            MessageBox.Show("Bitte zuerst eine Überwachung in der Liste auswählen.",
                "Hinweis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new EntryEditDialog(sel);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        RefreshListView();
        RefreshCombo();
        SaveSettings();
        Log($"Überwachung „{sel.Name}“ aktualisiert.", Level.Info);
    }

    void OnRemoveEntry()
    {
        var sel = GetSelectedEntry();
        if (sel == null) return;

        if (MessageBox.Show($"„{sel.Name}“ wirklich entfernen?", "Entfernen",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        StopEntry(sel);
        entries.Remove(sel);
        RefreshListView();
        RefreshCombo();
        SaveSettings();
        Log($"Überwachung „{sel.Name}“ entfernt.", Level.Info);
    }

    void RefreshListView()
    {
        lvEntries.BeginUpdate();
        lvEntries.Items.Clear();
        foreach (var en in entries)
        {
            var item = new ListViewItem(new[] { en.Name, en.MonitorIP, en.SwitchIP, en.PoEPort, en.Status }) {
                Tag = en,
                ForeColor = en.IsRunning ? Color.FromArgb(120, 200, 140) : Color.Silver
            };
            en.Item = item;
            lvEntries.Items.Add(item);
        }
        lvEntries.EndUpdate();
        UpdateGlobalStatus();
    }

    void RefreshCombo()
    {
        string? prev = cmbTarget.SelectedItem as string;
        cmbTarget.Items.Clear();
        foreach (var en in entries) cmbTarget.Items.Add(en.Name);

        if (cmbTarget.Items.Count == 0) return;
        int idx = prev != null ? cmbTarget.Items.IndexOf(prev) : -1;
        cmbTarget.SelectedIndex = idx >= 0 ? idx : 0;
    }

    void UpdateGlobalStatus()
    {
        int running = entries.Count(en => en.IsRunning);
        lblStatus.Text = entries.Count == 0 ? "Keine Überwachungen" : $"{running} von {entries.Count} aktiv";
        _dotColor = running > 0 ? Color.FromArgb(80, 200, 120) : Color.Gray;
        pnlDot.Invalidate();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  WATCHDOG  –  ein async Loop pro Eintrag, RAM-schonend per Task.Delay
    //  (kein dedizierter blockierender Thread pro Überwachung)
    // ─────────────────────────────────────────────────────────────────────────
    void StartEntry(WatchEntry entry)
    {
        if (entry.IsRunning) return;

        if (string.IsNullOrWhiteSpace(entry.MonitorIP) || string.IsNullOrWhiteSpace(entry.SwitchIP) ||
            string.IsNullOrWhiteSpace(entry.User)      || string.IsNullOrWhiteSpace(entry.PoEPort))
        {
            Log($"[{entry.Name}] Start übersprungen – Konfiguration unvollständig.", Level.Warn);
            return;
        }

        entry.Cts       = new CancellationTokenSource();
        entry.FailCount = 0;
        UpdateEntryStatus(entry, "Läuft", Color.FromArgb(80, 200, 120));
        Log($"[{entry.Name}] Watchdog gestartet  ▸  Monitor: {entry.MonitorIP}  |  Switch: {entry.SwitchIP}  |  Port: {entry.PoEPort}", Level.Info);

        _ = RunWatchdogAsync(entry, entry.Cts.Token); // bewusst fire-and-forget
    }

    void StopEntry(WatchEntry entry) => entry.Cts?.Cancel();

    void StartAll() { foreach (var en in entries.ToList()) StartEntry(en); }
    void StopAll()  { foreach (var en in entries.ToList()) StopEntry(en); }

    async Task RunWatchdogAsync(WatchEntry entry, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool ok = Ping(entry.MonitorIP);

                if (ok)
                {
                    if (entry.FailCount > 0)
                        Log($"[{entry.Name}] ✔ {entry.MonitorIP} wieder erreichbar – Zähler zurückgesetzt.", Level.Ok);
                    entry.FailCount = 0;
                    UpdateEntryStatus(entry, "Läuft – OK", Color.FromArgb(80, 200, 120));
                }
                else
                {
                    entry.FailCount++;
                    Log($"[{entry.Name}] ✘ Ping fehlgeschlagen → {entry.MonitorIP} (Versuch {entry.FailCount}/{entry.Retries})", Level.Warn);
                    UpdateEntryStatus(entry, $"Fehler {entry.FailCount}/{entry.Retries}", Color.FromArgb(255, 200, 0));

                    if (entry.FailCount >= entry.Retries)
                    {
                        entry.ResetCount++;
                        Log($"[{entry.Name}] ⚡ Schwellwert erreicht – PoE-Reset #{entry.ResetCount} wird ausgeführt …", Level.Action);
                        UpdateEntryStatus(entry, "PoE-Reset läuft …", Color.FromArgb(190, 90, 255));
                        await Task.Run(() => PoEReset(entry));
                        entry.FailCount = 0;
                    }
                }

                try { await Task.Delay(entry.Interval * 1000, token); }
                catch (TaskCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* erwartet beim Stoppen */ }
        finally
        {
            entry.Cts = null;
            UpdateEntryStatus(entry, "Gestoppt", Color.Gray);
            Log($"[{entry.Name}] Watchdog gestoppt.", Level.Info);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PING
    // ─────────────────────────────────────────────────────────────────────────
    static bool Ping(string host)
    {
        try
        {
            using var p = new Ping();
            return p.Send(host, 3000)?.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SSH / PoE RESET  –  EINE durchgehende Shell-Sitzung, kein Logout/Login
    //  zwischen Ausschalten und Einschalten des PoE.
    // ─────────────────────────────────────────────────────────────────────────
    void PoEReset(WatchEntry entry)
    {
        try
        {
            var auth = new PasswordAuthenticationMethod(entry.User, entry.Password);
            var conn = new ConnectionInfo(entry.SwitchIP, entry.User, auth)
                       { Timeout = TimeSpan.FromSeconds(10) };

            using var client = new SshClient(conn);
            client.Connect();
            Log($"[{entry.Name}] SSH verbunden mit {entry.SwitchIP}", Level.Info);

            using var shell = client.CreateShellStream("vt100", 200, 50, 800, 600, 4096);
            Thread.Sleep(500);
            shell.Read(); // Login-Banner verwerfen (nicht mitprotokollieren – nur Rauschen)

            RunInShell(shell, "system-view",            line => Log($"[{entry.Name}] {line}", Level.Info));
            RunInShell(shell, $"interface {entry.PoEPort}", line => Log($"[{entry.Name}] {line}", Level.Info));
            RunInShell(shell, "undo poe enable",        line => Log($"[{entry.Name}] {line}", Level.Info));

            Log($"[{entry.Name}] PoE deaktiviert an {entry.PoEPort} – warte {entry.PoEOff}s (Sitzung bleibt offen) …", Level.Action);
            Thread.Sleep(entry.PoEOff * 1000);

            RunInShell(shell, "poe enable",             line => Log($"[{entry.Name}] {line}", Level.Info));
            RunInShell(shell, "quit",                   line => Log($"[{entry.Name}] {line}", Level.Info));
            RunInShell(shell, "quit",                   line => Log($"[{entry.Name}] {line}", Level.Info));

            Log($"[{entry.Name}] PoE wieder aktiviert an {entry.PoEPort}.", Level.Ok);
            client.Disconnect();
            Log($"[{entry.Name}] SSH-Verbindung getrennt.", Level.Info);
        }
        catch (Exception ex)
        {
            Log($"[{entry.Name}] SSH-Fehler: [{ex.GetType().Name}] {ex.Message}" +
                (ex.InnerException != null ? $"  ←  {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : ""),
                Level.Error);
        }
    }

    // Führt einen einzelnen Befehl in einer bereits offenen Shell-Sitzung aus.
    // Loggt standardmäßig nur eine knappe Zeile pro Befehl – die volle, mehrzeilige
    // Switch-Antwort (Banner/Echo) wird nur bei verdächtigen/Error-Ausgaben mitgeschrieben,
    // damit das Protokoll bei jedem PoE-Reset nicht unnötig aufgebläht wird.
    static void RunInShell(Renci.SshNet.ShellStream shell, string cmd, Action<string> onCommandLogged)
    {
        shell.WriteLine(cmd);
        Thread.Sleep(700); // Switch Zeit geben, den Befehl zu verarbeiten und zu antworten
        string chunk = shell.Read();

        bool looksLikeError = chunk.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                            || chunk.IndexOf("% ", StringComparison.Ordinal) >= 0
                            || chunk.IndexOf("Unrecognized", StringComparison.OrdinalIgnoreCase) >= 0
                            || chunk.IndexOf("Wrong", StringComparison.OrdinalIgnoreCase) >= 0;

        onCommandLogged(looksLikeError
            ? $"  $ {cmd}\n{chunk.TrimEnd()}"   // verdächtig → volle Ausgabe für die Diagnose
            : $"  $ {cmd}  ✓");                  // normal → eine knappe Zeile
    }

    // Führt mehrere Befehle in EINER durchgehenden VRP-CLI-Sitzung aus.
    // Wichtig: system-view/interface/shutdown teilen sich denselben Kontext
    // nur innerhalb derselben Shell-Session – separate RunCommand-Aufrufe
    // (=> separate Sessions) verlieren diesen Kontext und werden vom Switch
    // teils sofort wieder getrennt.
    static void RunSequenceInShell(SshClient client, IEnumerable<string> commands, Action<string> onCommandLogged)
    {
        using var shell = client.CreateShellStream("vt100", 200, 50, 800, 600, 4096);

        Thread.Sleep(500);
        shell.Read(); // Login-Banner / erstes Prompt verwerfen

        foreach (var cmd in commands)
            RunInShell(shell, cmd, onCommandLogged);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MANUELLE AKTIONEN  –  per Button ausgelöste Einzel-Befehle für den im
    //  Dropdown ausgewählten Eintrag
    // ─────────────────────────────────────────────────────────────────────────
    async Task ManualAction(WatchEntry entry, string label, string[] commands)
    {
        if (string.IsNullOrWhiteSpace(entry.SwitchIP) ||
            string.IsNullOrWhiteSpace(entry.User)     ||
            string.IsNullOrWhiteSpace(entry.PoEPort))
        {
            MessageBox.Show("Switch IP, Benutzer und PoE Port müssen für diese Überwachung gesetzt sein.",
                "Fehlende Eingabe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string swIP = entry.SwitchIP, user = entry.User, pass = entry.Password, name = entry.Name;
        Log($"[{name}] ▶ Manuelle Aktion: {label} …", Level.Action);

        await Task.Run(() =>
        {
            SshClient? client = null;
            try
            {
                var auth = new PasswordAuthenticationMethod(user, pass);
                var conn = new ConnectionInfo(swIP, user, auth) { Timeout = TimeSpan.FromSeconds(10) };

                client = new SshClient(conn);
                client.Connect();
                Log($"[{name}] SSH verbunden mit {swIP}", Level.Info);

                RunSequenceInShell(client, commands, line => Log($"[{name}] {line}", Level.Info));

                client.Disconnect();
                Log($"[{name}] ✔ {label} abgeschlossen.", Level.Ok);
            }
            catch (Exception ex)
            {
                Log($"[{name}] SSH-Fehler bei „{label}“: [{ex.GetType().Name}] {ex.Message}" +
                    (ex.InnerException != null ? $"  ←  {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : ""),
                    Level.Error);
            }
            finally
            {
                client?.Dispose();
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LOG & UI HELPER
    // ─────────────────────────────────────────────────────────────────────────
    enum Level { Info, Ok, Warn, Action, Error }

    void Log(string msg, Level lv)
    {
        if (rtbLog.InvokeRequired) { rtbLog.Invoke(() => Log(msg, lv)); return; }

        // RAM-Begrenzung: alten Text im UI-Steuerelement kappen, Datei bleibt vollständig.
        if (rtbLog.TextLength > MaxLogTextChars)
        {
            string text = rtbLog.Text;
            int cut = text.IndexOf('\n', text.Length / 4);
            if (cut > 0) rtbLog.Text = text.Substring(cut + 1);
        }

        Color c = lv switch {
            Level.Ok     => Color.FromArgb(80,  200, 120),
            Level.Warn   => Color.FromArgb(255, 200,   0),
            Level.Action => Color.FromArgb(190,  90, 255),
            Level.Error  => Color.FromArgb(255,  70,  70),
            _            => Color.FromArgb(155, 155, 155)
        };
        string ts = DateTime.Now.ToString("HH:mm:ss");

        rtbLog.SelectionStart  = rtbLog.TextLength;
        rtbLog.SelectionLength = 0;
        rtbLog.SelectionColor  = Color.FromArgb(90, 90, 90);
        rtbLog.AppendText($"[{ts}]  ");
        rtbLog.SelectionColor  = c;
        rtbLog.AppendText(msg + "\n");
        rtbLog.ScrollToCaret();

        WriteToLogFile(ts, lv, msg);
    }

    static void WriteToLogFile(string ts, Level lv, string msg)
    {
        try
        {
            lock (LogFileLock)
            {
                string date = DateTime.Now.ToString("yyyy-MM-dd");
                File.AppendAllText(LogPath,
                    $"{date} {ts} [{lv}] {msg.Replace("\n", " | ")}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging darf die App nicht zum Absturz bringen
        }
    }

    // Löscht Log-Dateien, die älter als LogRetentionDays sind – verhindert
    // unbegrenztes Wachstum des Log-Ordners über die Zeit.
    static void CleanupOldLogs()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
            foreach (var file in Directory.GetFiles(LogDir, "poewatchdog_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // best effort – darf den Start nicht verhindern
        }
    }

    void UpdateEntryStatus(WatchEntry entry, string status, Color color)
    {
        entry.Status = status;
        if (rtbLog.InvokeRequired) { rtbLog.Invoke(() => UpdateEntryStatus(entry, status, color)); return; }

        if (entry.Item != null && entry.Item.ListView != null)
        {
            entry.Item.SubItems[4].Text = status;
            entry.Item.ForeColor = color;
        }
        UpdateGlobalStatus();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UI-BUILDER HELFER
    // ─────────────────────────────────────────────────────────────────────────
    static Panel MakePanel(int x, int y, int w, int h, Color bg) => new Panel {
        Location = new Point(x, y), Size = new Size(w, h), BackColor = bg
    };

    static void SectionLabel(Control p, string t, int x, int y) =>
        p.Controls.Add(new Label {
            Text = t, Location = new Point(x, y), AutoSize = true,
            ForeColor = Color.FromArgb(0, 160, 220), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        });

    static Button SmallBtn(string text, Color bg, int x, int y, int w)
    {
        var b = new Button {
            Text = text, Location = new Point(x, y), Size = new Size(w, 26),
            BackColor = bg, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8f), Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    static Button MakeActionBtn(string text, Color bg, int x, int y)
    {
        var b = new Button {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(150, 36),
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PROTOKOLLDATEI ÖFFNEN
    // ─────────────────────────────────────────────────────────────────────────
    void OpenLogFile()
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            var newest = Directory.GetFiles(LogDir, "poewatchdog_*.log")
                                   .OrderByDescending(File.GetLastWriteTime)
                                   .FirstOrDefault();

            if (newest != null)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{newest}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", $"\"{LogDir}\"");
        }
        catch (Exception ex)
        {
            Log($"Protokollordner konnte nicht geöffnet werden: {ex.Message}", Level.Warn);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EINSTELLUNGEN  –  speichern / laden unter %AppData%\PoEWatchdog
    // ─────────────────────────────────────────────────────────────────────────
    class AppSettingsV2
    {
        public List<WatchEntry> Entries { get; set; } = new();
        public bool AutoStartAll { get; set; }
    }

    void SaveSettings()
    {
        try
        {
            foreach (var en in entries)
                en.PasswordEncrypted = en.SavePassword ? Protect(en.Password) : "";

            var s = new AppSettingsV2 { Entries = entries.ToList(), AutoStartAll = chkAutoStartAll.Checked };

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            try { WriteToLogFile(DateTime.Now.ToString("HH:mm:ss"), Level.Error, $"Einstellungen konnten nicht gespeichert werden: {ex.Message}"); } catch { }
        }
    }

    void LoadSettings()
    {
        try
        {
            chkAutoStartWin.Checked = IsAutoStartWithWindowsEnabled();

            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<AppSettingsV2>(File.ReadAllText(SettingsPath));
            if (s == null) return;

            entries.Clear();
            foreach (var en in s.Entries)
            {
                if (en.SavePassword && !string.IsNullOrEmpty(en.PasswordEncrypted))
                    en.Password = Unprotect(en.PasswordEncrypted);
                entries.Add(en);
            }

            chkAutoStartAll.Checked = s.AutoStartAll;
            RefreshListView();
            RefreshCombo();
        }
        catch (Exception ex)
        {
            Log($"Einstellungen konnten nicht geladen werden: {ex.Message}", Level.Warn);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AUTOSTART MIT WINDOWS  –  HKCU Run-Key, kein Admin nötig
    // ─────────────────────────────────────────────────────────────────────────
    static string GetExePath() =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    static bool IsAutoStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, false);
            return key?.GetValue(AutoStartValName) is string v && !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    void SetAutoStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true)
                             ?? Registry.CurrentUser.CreateSubKey(AutoStartRegKey, true);
            if (enabled)
            {
                key.SetValue(AutoStartValName, $"\"{GetExePath()}\" --autostart");
                Log("Autostart mit Windows aktiviert.", Level.Info);
            }
            else
            {
                key.DeleteValue(AutoStartValName, false);
                Log("Autostart mit Windows deaktiviert.", Level.Info);
            }
        }
        catch (Exception ex)
        {
            Log($"Autostart-Einstellung konnte nicht geändert werden: {ex.Message}", Level.Warn);
        }
    }

    // Leichter Schutz für das gespeicherte Passwort (kein Klartext auf der Platte).
    // Hinweis: DPAPI ist an den Windows-Benutzer/-Rechner gebunden – kein Ersatz
    // für einen echten Secret-Store, aber besser als Klartext.
    static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var enc   = System.Security.Cryptography.ProtectedData.Protect(
            bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    static string Unprotect(string protectedText)
    {
        try
        {
            var bytes = Convert.FromBase64String(protectedText);
            var dec   = System.Security.Cryptography.ProtectedData.Unprotect(
                bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Dialog: eine Überwachung hinzufügen / bearbeiten
// ─────────────────────────────────────────────────────────────────────────────
public class EntryEditDialog : Form
{
    public WatchEntry Result = new();
    private readonly WatchEntry? _existing;

    TextBox txtName = new(), txtMonitor = new(), txtSwitch = new(),
            txtUser  = new(), txtPass   = new(), txtPort   = new();
    NumericUpDown numInterval = new(), numRetries = new(), numPoEOff = new();
    CheckBox chkSavePass = new();

    public EntryEditDialog(WatchEntry? existing)
    {
        _existing = existing;

        Text            = existing == null ? "Überwachung hinzufügen" : "Überwachung bearbeiten";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        BackColor       = Color.FromArgb(28, 28, 28);
        ForeColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);

        int y = 18;
        AddRow("Name:",            ref y, out txtName,    existing?.Name      ?? "Neue Überwachung");
        AddRow("Überwachte IP:",   ref y, out txtMonitor, existing?.MonitorIP ?? "192.168.1.100");
        AddRow("Switch IP:",       ref y, out txtSwitch,  existing?.SwitchIP  ?? "192.168.1.1");
        AddRow("SSH Benutzer:",    ref y, out txtUser,    existing?.User      ?? "admin");
        AddRow("SSH Passwort:",    ref y, out txtPass,    existing != null && existing.SavePassword ? existing.Password : "");
        txtPass.UseSystemPasswordChar = true;

        chkSavePass.Text      = "Passwort merken (verschlüsselt, DPAPI)";
        chkSavePass.AutoSize  = true;
        chkSavePass.ForeColor = Color.Silver;
        chkSavePass.Font      = new Font("Segoe UI", 7.5f);
        chkSavePass.Location  = new Point(150, y);
        chkSavePass.Checked   = existing?.SavePassword ?? false;
        Controls.Add(chkSavePass);
        y += 26;

        AddRow("PoE Port:", ref y, out txtPort, existing?.PoEPort ?? "GigabitEthernet0/0/5");

        var sep = new Panel { Location = new Point(16, y + 2), Size = new Size(296, 1),
                               BackColor = Color.FromArgb(65, 65, 65) };
        Controls.Add(sep); y += 14;

        AddSpinRow("Prüfintervall (s):", ref y, out numInterval, 5,  300, existing?.Interval ?? 30);
        AddSpinRow("Fehlversuche:",      ref y, out numRetries,  1,   20, existing?.Retries  ?? 3);
        AddSpinRow("PoE-Pause (s):",     ref y, out numPoEOff,   3,  120, existing?.PoEOff   ?? 10);

        y += 12;
        var btnOk = new Button {
            Text = "Speichern", Location = new Point(60, y), Size = new Size(110, 36),
            BackColor = Color.FromArgb(0, 120, 212), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
        };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (_, _) => {
            if (!ValidateInput()) return;
            BuildResult();
            DialogResult = DialogResult.OK;
            Close();
        };

        var btnCancel = new Button {
            Text = "Abbrechen", Location = new Point(180, y), Size = new Size(110, 36),
            BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { btnOk, btnCancel });

        ClientSize  = new Size(360, y + 60);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    void AddRow(string label, ref int y, out TextBox tb, string def)
    {
        Controls.Add(new Label {
            Text = label, Location = new Point(16, y + 3), AutoSize = true,
            ForeColor = Color.FromArgb(175, 175, 175)
        });
        tb = new TextBox {
            Text = def, Location = new Point(150, y), Width = 196,
            BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(tb);
        y += 30;
    }

    void AddSpinRow(string label, ref int y, out NumericUpDown n, int min, int max, int val)
    {
        Controls.Add(new Label {
            Text = label, Location = new Point(16, y + 3), AutoSize = true,
            ForeColor = Color.FromArgb(175, 175, 175)
        });
        n = new NumericUpDown {
            Minimum = min, Maximum = max, Value = Math.Min(max, Math.Max(min, val)),
            Location = new Point(150, y), Width = 90,
            BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.White
        };
        Controls.Add(n);
        y += 30;
    }

    bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(txtName.Text)    || string.IsNullOrWhiteSpace(txtMonitor.Text) ||
            string.IsNullOrWhiteSpace(txtSwitch.Text)   || string.IsNullOrWhiteSpace(txtUser.Text)    ||
            string.IsNullOrWhiteSpace(txtPort.Text))
        {
            MessageBox.Show("Bitte alle Felder (außer Passwort) ausfüllen.",
                "Fehlende Eingabe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    void BuildResult()
    {
        Result = _existing ?? new WatchEntry();
        Result.Name          = txtName.Text.Trim();
        Result.MonitorIP      = txtMonitor.Text.Trim();
        Result.SwitchIP       = txtSwitch.Text.Trim();
        Result.User           = txtUser.Text.Trim();
        Result.PoEPort        = txtPort.Text.Trim();
        Result.Interval       = (int)numInterval.Value;
        Result.Retries        = (int)numRetries.Value;
        Result.PoEOff         = (int)numPoEOff.Value;
        Result.SavePassword   = chkSavePass.Checked;
        Result.Password       = txtPass.Text; // im Speicher immer verfügbar, damit der Watchdog laufen kann
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Kleiner Dialog: "Beenden" oder "In den Tray legen", erscheint beim Klick auf X
// ─────────────────────────────────────────────────────────────────────────────
public class CloseChoiceDialog : Form
{
    public bool GoToTray { get; private set; } = true;

    public CloseChoiceDialog()
    {
        Text            = "PoE Watchdog";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        Size            = new Size(360, 170);
        BackColor       = Color.FromArgb(28, 28, 28);
        ForeColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);

        var lbl = new Label {
            Text = "Was möchtest du tun?",
            Location = new Point(20, 20), AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var lblSub = new Label {
            Text = "Der Watchdog läuft im Hintergrund weiter,\nwenn du ihn in den Tray legst.",
            Location = new Point(20, 48), AutoSize = true,
            ForeColor = Color.FromArgb(170, 170, 170), Font = new Font("Segoe UI", 8.5f)
        };

        var btnTray = MakeBtn("In den Tray legen", Color.FromArgb(0, 122, 204), 20, 95);
        btnTray.Click += (_, _) => { GoToTray = true;  DialogResult = DialogResult.OK; Close(); };

        var btnExit = MakeBtn("Beenden", Color.FromArgb(70, 70, 70), 180, 95);
        btnExit.Click += (_, _) => { GoToTray = false; DialogResult = DialogResult.OK; Close(); };

        Controls.AddRange(new Control[] { lbl, lblSub, btnTray, btnExit });
        AcceptButton = btnTray;
        CancelButton = null;
    }

    static Button MakeBtn(string text, Color bg, int x, int y) => new Button {
        Text = text, Location = new Point(x, y), Size = new Size(160, 38),
        BackColor = bg, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand
    };
}
