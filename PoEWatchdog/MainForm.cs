// PoE Watchdog für Huawei S5731-S
// Benötigt NuGet-Paket: SSH.NET
// Eine einzige Datei – kein Form1, kein Program.cs nötig.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;

[STAThread]
static void AppMain()
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
}
AppMain();

// ═════════════════════════════════════════════════════════════════════════════
public class MainForm : Form
{
    // ── Eingabefelder ─────────────────────────────────────────────────────────
    private TextBox         txtMonitorIP  = new();
    private TextBox         txtSwitchIP   = new();
    private TextBox         txtUser       = new();
    private TextBox         txtPassword   = new();
    private TextBox         txtPoEPort    = new();
    private NumericUpDown   numInterval   = new();
    private NumericUpDown   numRetries    = new();
    private NumericUpDown   numPoEOff     = new();
    private CheckBox        chkSavePass   = new();

    // ── Persistenz ────────────────────────────────────────────────────────────
    static readonly string SettingsDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoEWatchdog");
    static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    static readonly string LogPath      = Path.Combine(SettingsDir, "poewatchdog.log");
    static readonly object LogFileLock  = new();

    // ── Buttons & Log ─────────────────────────────────────────────────────────
    private Button          btnStart      = new();
    private Button          btnStop       = new();
    private RichTextBox     rtbLog        = new();
    private Label           lblStatus     = new();
    private Panel           pnlDot        = new();

    // ── Laufzeitstatus ────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private int   _failCount  = 0;
    private int   _resetTotal = 0;
    private Color _dotColor   = Color.Gray;

    // ═════════════════════════════════════════════════════════════════════════
    public MainForm()
    {
        BuildUI();
        LoadSettings();
        FormClosing += (_, _) => SaveSettings();
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
        lblStatus.Text      = "Gestoppt";
        lblStatus.ForeColor = Color.Silver;
        lblStatus.Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        bar.Controls.AddRange(new Control[] { pnlDot, lblStatus });

        // ── Konfigurationsblock ───────────────────────────────────────────
        var cfg = MakePanel(12, 52, 318, 340, Color.FromArgb(38, 38, 38));
        SectionLabel(cfg, "⚙  Verbindung & Port", 10, 8);

        int y = 34;
        Row(cfg, "Überwachte IP:",       ref y); txtMonitorIP = Field(cfg, "192.168.1.100", 165, y - 22, 140);
        Row(cfg, "Switch IP:",           ref y); txtSwitchIP  = Field(cfg, "192.168.1.1",   165, y - 22, 140);
        Row(cfg, "SSH Benutzer:",        ref y); txtUser      = Field(cfg, "admin",          165, y - 22, 140);
        Row(cfg, "SSH Passwort:",        ref y);
        txtPassword = Field(cfg, "", 165, y - 22, 140);
        txtPassword.UseSystemPasswordChar = true;

        chkSavePass.Text      = "Passwort merken";
        chkSavePass.AutoSize  = true;
        chkSavePass.ForeColor = Color.Silver;
        chkSavePass.Location  = new Point(165, y + 4);
        chkSavePass.Font      = new Font("Segoe UI", 7.5f);
        cfg.Controls.Add(chkSavePass);
        y += 24;

        Row(cfg, "PoE Port:",            ref y); txtPoEPort   = Field(cfg, "GigabitEthernet0/0/5", 165, y - 22, 140);

        var sep = new Panel { Location = new Point(10, y + 4), Size = new Size(296, 1),
                              BackColor = Color.FromArgb(65, 65, 65) };
        cfg.Controls.Add(sep); y += 16;

        SectionLabel(cfg, "⏱  Timing", 10, y); y += 26;
        Row(cfg, "Prüfintervall (s):",   ref y); numInterval = Spin(cfg, 30,  5,  300, 165, y - 22, 80);
        Row(cfg, "Fehlversuche:",        ref y); numRetries  = Spin(cfg, 3,   1,   20, 165, y - 22, 80);
        Row(cfg, "PoE-Pause (s):",       ref y); numPoEOff   = Spin(cfg, 10,  3,  120, 165, y - 22, 80);

        // ── Steuerungsblock ───────────────────────────────────────────────
        var ctrl = MakePanel(342, 52, 318, 340, Color.FromArgb(38, 38, 38));
        SectionLabel(ctrl, "▶  Steuerung", 10, 8);

        btnStart.Text      = "▶  Watchdog starten";
        btnStart.Location  = new Point(10, 40);
        btnStart.Size      = new Size(296, 52);
        btnStart.BackColor = Color.FromArgb(0, 120, 212);
        btnStart.ForeColor = Color.White;
        btnStart.FlatStyle = FlatStyle.Flat;
        btnStart.Font      = new Font("Segoe UI", 11f, FontStyle.Bold);
        btnStart.Cursor    = Cursors.Hand;
        btnStart.FlatAppearance.BorderSize = 0;
        btnStart.Click    += OnStart;

        btnStop.Text       = "⏹  Stoppen";
        btnStop.Location   = new Point(10, 104);
        btnStop.Size       = new Size(296, 44);
        btnStop.BackColor  = Color.FromArgb(65, 65, 65);
        btnStop.ForeColor  = Color.White;
        btnStop.FlatStyle  = FlatStyle.Flat;
        btnStop.Font       = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnStop.Enabled    = false;
        btnStop.Cursor     = Cursors.Hand;
        btnStop.FlatAppearance.BorderSize = 0;
        btnStop.Click     += OnStop;

        ctrl.Controls.AddRange(new Control[] { btnStart, btnStop });

        // Info-Box
        var info = MakePanel(10, 162, 296, 136, Color.FromArgb(25, 25, 25));
        SectionLabel(info, "ℹ  Wie es funktioniert", 8, 6);
        info.Controls.Add(new Label {
            Text = "1. Watchdog startet und pingt die\n    überwachte IP regelmäßig.\n\n" +
                   "2. Schlägt der Ping X-mal hinter-\n    einander fehl → SSH-Login.\n\n" +
                   "3. PoE am gewählten Port kurz aus\n    und wieder ein (Neustart Gerät).",
            Location = new Point(8, 28), Size = new Size(278, 100),
            ForeColor = Color.FromArgb(175, 175, 175),
            Font = new Font("Segoe UI", 8.5f)
        });
        ctrl.Controls.Add(info);

        // ── Manuelle Aktionen ─────────────────────────────────────────────
        var manual = MakePanel(12, 404, 648, 80, Color.FromArgb(38, 38, 38));
        SectionLabel(manual, "🖱  Manuelle Aktionen", 10, 6);

        var btnPoEOff = MakeActionBtn("⚡ PoE AUS",   Color.FromArgb(180, 60, 60),  10,  28);
        var btnPoEOn  = MakeActionBtn("⚡ PoE EIN",   Color.FromArgb(40, 140, 70),  170, 28);
        var btnIfDown = MakeActionBtn("🔽 Port DOWN", Color.FromArgb(160, 100, 20), 330, 28);
        var btnIfUp   = MakeActionBtn("🔼 Port UP",   Color.FromArgb(30, 110, 160), 490, 28);

        btnPoEOff.Click += async (_, _) => await ManualAction("PoE deaktivieren",
            new[] { "system-view", $"interface {txtPoEPort.Text.Trim()}", "undo poe enable", "quit", "quit" });

        btnPoEOn.Click += async (_, _) => await ManualAction("PoE aktivieren",
            new[] { "system-view", $"interface {txtPoEPort.Text.Trim()}", "poe enable", "quit", "quit" });

        btnIfDown.Click += async (_, _) => await ManualAction("Port herunterfahren",
            new[] { "system-view", $"interface {txtPoEPort.Text.Trim()}", "shutdown", "quit", "quit" });

        btnIfUp.Click += async (_, _) => await ManualAction("Port hochfahren",
            new[] { "system-view", $"interface {txtPoEPort.Text.Trim()}", "undo shutdown", "quit", "quit" });

        manual.Controls.AddRange(new Control[] { btnPoEOff, btnPoEOn, btnIfDown, btnIfUp });

        // ── Log ───────────────────────────────────────────────────────────
        var logBox = MakePanel(12, 494, 648, 238, Color.FromArgb(38, 38, 38));
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
        rtbLog.Size        = new Size(630, 196);
        rtbLog.BackColor   = Color.FromArgb(18, 18, 18);
        rtbLog.ForeColor   = Color.FromArgb(200, 200, 200);
        rtbLog.Font        = new Font("Consolas", 8.5f);
        rtbLog.ReadOnly    = true;
        rtbLog.BorderStyle = BorderStyle.None;
        rtbLog.ScrollBars  = RichTextBoxScrollBars.Vertical;
        logBox.Controls.AddRange(new Control[] { btnOpenLog, btnClear, rtbLog });

        // ── Alles auf Form ────────────────────────────────────────────────
        Controls.AddRange(new Control[] { bar, cfg, ctrl, manual, logBox });

        Log("Bereit. Konfiguration eingeben und Starten klicken.", Level.Info);
        Log($"Protokolldatei: {LogPath}", Level.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  WATCHDOG
    // ─────────────────────────────────────────────────────────────────────────
    async void OnStart(object? s, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtMonitorIP.Text) ||
            string.IsNullOrWhiteSpace(txtSwitchIP.Text)  ||
            string.IsNullOrWhiteSpace(txtUser.Text)      ||
            string.IsNullOrWhiteSpace(txtPoEPort.Text))
        {
            MessageBox.Show("Bitte alle Felder ausfüllen.", "Fehlende Eingabe",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts        = new CancellationTokenSource();
        _failCount  = 0;
        _resetTotal = 0;
        SetUI(true);

        // Werte vor Thread-Start lesen (UI-Thread)
        string monIP    = txtMonitorIP.Text.Trim();
        string swIP     = txtSwitchIP.Text.Trim();
        string user     = txtUser.Text.Trim();
        string pass     = txtPassword.Text;
        string port     = txtPoEPort.Text.Trim();
        int    interval = (int)numInterval.Value;
        int    retries  = (int)numRetries.Value;
        int    poeOff   = (int)numPoEOff.Value;

        Log($"Gestartet  ▸  Monitor: {monIP}  |  Switch: {swIP}  |  Port: {port}", Level.Info);
        Log($"Intervall: {interval}s  |  Auslösung nach {retries} Fehlern  |  PoE-Pause: {poeOff}s", Level.Info);

        var token = _cts.Token;
        await Task.Run(() => Loop(monIP, swIP, user, pass, port, interval, retries, poeOff, token));

        SetUI(false);
        Log("Watchdog gestoppt.", Level.Info);
        SetDot(DotColor.Gray, "Gestoppt");
    }

    void OnStop(object? s, EventArgs e) => _cts?.Cancel();

    void Loop(string monIP, string swIP, string user, string pass,
              string port, int interval, int retries, int poeOff,
              CancellationToken tok)
    {
        while (!tok.IsCancellationRequested)
        {
            bool ok = Ping(monIP);

            if (ok)
            {
                if (_failCount > 0)
                    Log($"✔ {monIP} wieder erreichbar – Zähler zurückgesetzt.", Level.Ok);
                else
                    Log($"✔ Ping OK  →  {monIP}", Level.Ok);

                _failCount = 0;
                SetDot(DotColor.Green, $"Läuft – {monIP} erreichbar");
            }
            else
            {
                _failCount++;
                Log($"✘ Ping fehlgeschlagen  →  {monIP}  (Versuch {_failCount}/{retries})", Level.Warn);
                SetDot(DotColor.Yellow, $"Läuft – Ping fehlgeschlagen ({_failCount}/{retries})");

                if (_failCount >= retries)
                {
                    _resetTotal++;
                    Log($"⚡ Schwellwert erreicht – PoE-Reset #{_resetTotal} wird ausgeführt …", Level.Action);
                    SetDot(DotColor.Purple, "PoE-Reset läuft …");
                    PoEReset(swIP, user, pass, port, poeOff);
                    _failCount = 0;
                }
            }

            // Intervall in 100ms-Häppchen (damit Cancel sofort wirkt)
            for (int i = 0; i < interval * 10 && !tok.IsCancellationRequested; i++)
                Thread.Sleep(100);
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
    //  SSH / PoE RESET  –  verwendet SshCommand, kein ShellStream!
    // ─────────────────────────────────────────────────────────────────────────
    void PoEReset(string swIP, string user, string pass, string port, int poeOff)
    {
        try
        {
            var auth = new PasswordAuthenticationMethod(user, pass);
            var conn = new ConnectionInfo(swIP, user, auth)
                       { Timeout = TimeSpan.FromSeconds(10) };

            using var client = new SshClient(conn);
            client.Connect();
            Log($"SSH verbunden mit {swIP}", Level.Info);

            using var shell = client.CreateShellStream("vt100", 200, 50, 800, 600, 4096);
            Thread.Sleep(500);
            Log(shell.Read().TrimEnd(), Level.Info);

            // Einmal einloggen, im Interface bleiben, PoE aus, warten, PoE wieder ein, erst dann raus.
            RunInShell(shell, "system-view",        line => Log(line, Level.Info));
            RunInShell(shell, $"interface {port}",  line => Log(line, Level.Info));
            RunInShell(shell, "undo poe enable",    line => Log(line, Level.Info));

            Log($"PoE deaktiviert an {port} – warte {poeOff}s (Sitzung bleibt offen) …", Level.Action);
            Thread.Sleep(poeOff * 1000);

            RunInShell(shell, "poe enable",         line => Log(line, Level.Info));
            RunInShell(shell, "quit",               line => Log(line, Level.Info));
            RunInShell(shell, "quit",               line => Log(line, Level.Info));

            Log($"PoE wieder aktiviert an {port}.", Level.Ok);
            client.Disconnect();
            Log("SSH-Verbindung getrennt.", Level.Info);
        }
        catch (Exception ex)
        {
            Log($"SSH-Fehler: [{ex.GetType().Name}] {ex.Message}" +
                (ex.InnerException != null ? $"  ←  {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : ""),
                Level.Error);
        }
    }

    // Führt einen einzelnen Befehl in einer bereits offenen Shell-Sitzung aus.
    static void RunInShell(Renci.SshNet.ShellStream shell, string cmd, Action<string> onCommandLogged)
    {
        shell.WriteLine(cmd);
        Thread.Sleep(700); // Switch Zeit geben, den Befehl zu verarbeiten und zu antworten
        string chunk = shell.Read();
        onCommandLogged($"  $ {cmd}\n{chunk.TrimEnd()}");
    }

    // Führt mehrere Befehle in EINER durchgehenden VRP-CLI-Sitzung aus.
    // Wichtig: system-view/interface/shutdown teilen sich denselben Kontext
    // nur innerhalb derselben Shell-Session – separate RunCommand-Aufrufe
    // (=> separate Sessions) verlieren diesen Kontext und werden vom Switch
    // teils sofort wieder getrennt.
    string RunSequenceInShell(SshClient client, IEnumerable<string> commands, Action<string> onCommandLogged)
    {
        var fullLog = new StringBuilder();
        using var shell = client.CreateShellStream("vt100", 200, 50, 800, 600, 4096);

        // Login-Banner / erstes Prompt abwarten
        Thread.Sleep(500);
        fullLog.Append(shell.Read());

        foreach (var cmd in commands)
        {
            shell.WriteLine(cmd);
            Thread.Sleep(700); // Switch Zeit geben, den Befehl zu verarbeiten und zu antworten
            string chunk = shell.Read();
            fullLog.Append(chunk);
            onCommandLogged($"  $ {cmd}\n{chunk.TrimEnd()}");
        }

        return fullLog.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MANUELLE AKTIONEN  –  per Button ausgelöste Einzel-Befehle
    // ─────────────────────────────────────────────────────────────────────────
    async Task ManualAction(string label, string[] commands)
    {
        if (string.IsNullOrWhiteSpace(txtSwitchIP.Text) ||
            string.IsNullOrWhiteSpace(txtUser.Text)      ||
            string.IsNullOrWhiteSpace(txtPoEPort.Text))
        {
            MessageBox.Show("Bitte Switch IP, Benutzer und PoE Port ausfüllen.",
                "Fehlende Eingabe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string swIP = txtSwitchIP.Text.Trim();
        string user = txtUser.Text.Trim();
        string pass = txtPassword.Text;

        Log($"▶ Manuelle Aktion: {label} …", Level.Action);

        await Task.Run(() =>
        {
            SshClient? client = null;
            try
            {
                var auth = new PasswordAuthenticationMethod(user, pass);
                var conn = new ConnectionInfo(swIP, user, auth)
                           { Timeout = TimeSpan.FromSeconds(10) };

                client = new SshClient(conn);
                client.Connect();
                Log($"SSH verbunden mit {swIP}", Level.Info);

                RunSequenceInShell(client, commands, line => Log(line, Level.Info));

                client.Disconnect();
                Log($"✔ {label} abgeschlossen.", Level.Ok);
            }
            catch (Exception ex)
            {
                Log($"SSH-Fehler bei „{label}“: [{ex.GetType().Name}] {ex.Message}" +
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
    enum DotColor { Gray, Green, Yellow, Purple, Red }

    void Log(string msg, Level lv)
    {
        if (rtbLog.InvokeRequired) { rtbLog.Invoke(() => Log(msg, lv)); return; }

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
                Directory.CreateDirectory(SettingsDir);
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

    void SetDot(DotColor dc, string text)
    {
        if (InvokeRequired) { Invoke(() => SetDot(dc, text)); return; }
        (_dotColor, lblStatus.ForeColor, lblStatus.Text) = dc switch {
            DotColor.Green  => (Color.FromArgb(80,  200, 120), Color.FromArgb(80,  200, 120), text),
            DotColor.Yellow => (Color.FromArgb(255, 200,   0), Color.FromArgb(255, 200,   0), text),
            DotColor.Purple => (Color.FromArgb(190,  90, 255), Color.FromArgb(190,  90, 255), text),
            DotColor.Red    => (Color.FromArgb(255,  70,  70), Color.FromArgb(255,  70,  70), text),
            _               => (Color.Gray,                    Color.Silver,                  text)
        };
        pnlDot.Invalidate();
    }

    void SetUI(bool running)
    {
        if (InvokeRequired) { Invoke(() => SetUI(running)); return; }
        btnStart.Enabled = !running;
        btnStop.Enabled  =  running;
        foreach (Control c in new Control[] {
            txtMonitorIP, txtSwitchIP, txtUser, txtPassword,
            txtPoEPort, numInterval, numRetries, numPoEOff })
            c.Enabled = !running;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UI-BUILDER HELFER
    // ─────────────────────────────────────────────────────────────────────────
    static Panel MakePanel(int x, int y, int w, int h, Color bg)
    {
        var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = bg };
        // wird nach Rückgabe dem Form hinzugefügt
        return p;
    }

    static void SectionLabel(Control p, string t, int x, int y) =>
        p.Controls.Add(new Label {
            Text = t, Location = new Point(x, y), AutoSize = true,
            ForeColor = Color.FromArgb(0, 145, 255),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        });

    static void Row(Control p, string label, ref int y)
    {
        p.Controls.Add(new Label {
            Text = label, Location = new Point(10, y + 3), AutoSize = true,
            ForeColor = Color.FromArgb(175, 175, 175)
        });
        y += 28;
    }

    static TextBox Field(Control p, string def, int x, int y, int w)
    {
        var tb = new TextBox {
            Text = def, Location = new Point(x, y), Width = w,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        p.Controls.Add(tb);
        return tb;
    }

    static NumericUpDown Spin(Control p, int val, int min, int max, int x, int y, int w)
    {
        var n = new NumericUpDown {
            Value = val, Minimum = min, Maximum = max,
            Location = new Point(x, y), Width = w,
            BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.White
        };
        p.Controls.Add(n);
        return n;
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
            Directory.CreateDirectory(SettingsDir);
            if (!File.Exists(LogPath))
                File.AppendAllText(LogPath, "", Encoding.UTF8); // leere Datei anlegen, falls noch nichts geloggt wurde

            // Explorer mit markierter Datei öffnen (funktioniert auch ohne Standard-App-Zuordnung)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LogPath}\"");
        }
        catch (Exception ex)
        {
            Log($"Protokolldatei konnte nicht geöffnet werden: {ex.Message}", Level.Warn);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EINSTELLUNGEN  –  speichern / laden unter %AppData%\PoEWatchdog
    // ─────────────────────────────────────────────────────────────────────────
    void SaveSettings()
    {
        try
        {
            var s = new AppSettings
            {
                MonitorIP   = txtMonitorIP.Text.Trim(),
                SwitchIP    = txtSwitchIP.Text.Trim(),
                User        = txtUser.Text.Trim(),
                PoEPort     = txtPoEPort.Text.Trim(),
                Interval    = (int)numInterval.Value,
                Retries     = (int)numRetries.Value,
                PoEOff      = (int)numPoEOff.Value,
                SavePass    = chkSavePass.Checked,
                Password    = chkSavePass.Checked ? Protect(txtPassword.Text) : ""
            };

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            // Beim Schließen nur best-effort, keine Dialoge mehr anzeigen
            try { WriteToLogFile(DateTime.Now.ToString("HH:mm:ss"), Level.Error, $"Einstellungen konnten nicht gespeichert werden: {ex.Message}"); } catch { }
        }
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (s == null) return;

            txtMonitorIP.Text = s.MonitorIP ?? "";
            txtSwitchIP.Text  = s.SwitchIP  ?? "";
            txtUser.Text      = s.User      ?? "";
            txtPoEPort.Text   = s.PoEPort   ?? "";
            if (s.Interval > 0) numInterval.Value = Math.Min(numInterval.Maximum, Math.Max(numInterval.Minimum, s.Interval));
            if (s.Retries  > 0) numRetries.Value  = Math.Min(numRetries.Maximum,  Math.Max(numRetries.Minimum,  s.Retries));
            if (s.PoEOff   > 0) numPoEOff.Value   = Math.Min(numPoEOff.Maximum,   Math.Max(numPoEOff.Minimum,   s.PoEOff));

            chkSavePass.Checked = s.SavePass;
            if (s.SavePass && !string.IsNullOrEmpty(s.Password))
                txtPassword.Text = Unprotect(s.Password);
        }
        catch (Exception ex)
        {
            Log($"Einstellungen konnten nicht geladen werden: {ex.Message}", Level.Warn);
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

    class AppSettings
    {
        public string MonitorIP { get; set; } = "";
        public string SwitchIP  { get; set; } = "";
        public string User      { get; set; } = "";
        public string PoEPort   { get; set; } = "";
        public int    Interval  { get; set; }
        public int    Retries   { get; set; }
        public int    PoEOff    { get; set; }
        public bool   SavePass  { get; set; }
        public string Password  { get; set; } = "";
    }

}

