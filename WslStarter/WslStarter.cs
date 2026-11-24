// WSL-Starter – WSL-Distributionen abfragen, installieren und entfernen.
// Visual-Studio-Projekt: WSL-Starter.slnx öffnen, F5 zum Starten.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WslStarter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize(); // Visual Styles, DPI-Modus, Standardschrift (aus .csproj)
            Application.Run(new MainForm());
        }
    }

    class CmdResult
    {
        public int ExitCode;
        public string Output;
    }

    class Distro
    {
        public string Name;
        public string FriendlyName;
        public string State;
        public string Version;
        public bool IsDefault;
        public string BasePath;   // Ordner mit ext4.vhdx (aus der Registry)
        public string RegKey;     // GUID-Schlüssel unter ...\Lxss
        public int DefaultUid = -1;
    }

    static class Wsl
    {
        static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9._\-]+$");
        const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

        public static bool IsSafeName(string name)
        {
            return !string.IsNullOrEmpty(name) && SafeName.IsMatch(name);
        }

        /// Vollständiger Windows-Pfad ohne Zeichen, die in der cmd-Befehlszeile stören würden.
        public static bool IsSafePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny("\"%&^|<>!".ToCharArray()) >= 0) return false;
            try { return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0; }
            catch { return false; }
        }

        /// Ergänzt Speicherort und Standardbenutzer aus HKCU\...\Lxss (dort registriert WSL jede Distribution).
        public static void ReadRegistry(List<Distro> list)
        {
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LxssKey))
            {
                if (root == null) return;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(sub))
                    {
                        string name = k?.GetValue("DistributionName") as string;
                        var d = list.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (d == null) continue;
                        d.RegKey = sub;
                        d.BasePath = k.GetValue("BasePath") as string;
                        if (d.BasePath != null && d.BasePath.StartsWith(@"\\?\")) d.BasePath = d.BasePath.Substring(4);
                        if (k.GetValue("DefaultUid") is int uid) d.DefaultUid = uid;
                    }
                }
            }
        }

        /// Setzt den Standardbenutzer (UID) einer Distribution, z. B. nach einem Import (sonst wäre es root).
        public static bool SetDefaultUid(string distroName, int uid)
        {
            var tmp = new List<Distro> { new Distro { Name = distroName } };
            ReadRegistry(tmp);
            if (tmp[0].RegKey == null) return false;
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LxssKey + "\\" + tmp[0].RegKey, true))
            {
                if (k == null) return false;
                k.SetValue("DefaultUid", uid, Microsoft.Win32.RegistryValueKind.DWord);
                return true;
            }
        }

        /// Bash-Skript (läuft als root in der Instanz): gibt dem Benutzer mit UID $1 eine neue UID,
        /// die nicht unter den übrigen Argumenten vorkommt, und zieht den Dateibesitz nach (ohne /mnt).
        /// Grund: Alle WSL2-Instanzen teilen sich die systemd-Cgroups. Zwei gleichzeitig laufende
        /// Instanzen mit derselben UID können nicht beide ihre Benutzersitzung (user@UID.service) starten.
        public const string ChangeUidScript = @"
set -u
OLD=""$1""; shift; FORBID="" $* ""
U=$(getent passwd ""$OLD"" | cut -d: -f1)
[ -n ""$U"" ] || { echo ""Kein Benutzer mit UID $OLD""; exit 2; }
NEW=1000
while :; do
  case ""$FORBID"" in *"" $NEW ""*) NEW=$((NEW+1)); continue;; esac
  if getent passwd ""$NEW"" >/dev/null || getent group ""$NEW"" >/dev/null; then NEW=$((NEW+1)); continue; fi
  break
done
pkill -KILL -u ""$OLD"" 2>/dev/null; sleep 1
systemctl reset-failed ""user@$OLD.service"" 2>/dev/null
NG=
if [ ""$(getent group ""$U"" | cut -d: -f3)"" = ""$OLD"" ]; then groupmod -g ""$NEW"" ""$U"" || exit 3; NG=$NEW; fi
usermod -u ""$NEW"" ${NG:+-g ""$NG""} ""$U"" || exit 4
find / -xdev -path /mnt -prune -o \( -uid ""$OLD"" -o -gid ""$OLD"" \) -print0 2>/dev/null |
while IFS= read -r -d '' f; do
  o=$(stat -c %u ""$f""); g=$(stat -c %g ""$f"")
  [ ""$o"" = ""$OLD"" ] && o=$NEW
  [ -n ""$NG"" ] && [ ""$g"" = ""$OLD"" ] && g=$NEW
  chown -h ""$o:$g"" ""$f"" 2>/dev/null
done
echo ""NEWUID=$NEW USER=$U""
";

        // ---- Einstellungen: zuletzt verwendeter Basisordner ----
        static string SettingsFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WSL-Starter", "basisordner.txt"); }
        }

        public static string BaseFolder
        {
            get
            {
                try
                {
                    string s = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile).Trim() : null;
                    if (IsSafePath(s)) return s;
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSL");
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                    File.WriteAllText(SettingsFile, value);
                }
                catch { }
            }
        }

        public static string WslExe
        {
            get
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string dir = (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) ? "Sysnative" : "System32";
                return Path.Combine(win, dir, "wsl.exe");
            }
        }

        public static bool WslExeExists
        {
            get { return File.Exists(WslExe); }
        }

        public static Task<CmdResult> RunAsync(string args)
        {
            return Task.Run(() => Run(args));
        }

        public static Task<CmdResult> RunAsync(string args, string stdin)
        {
            return Task.Run(() => Run(args, stdin));
        }

        public static CmdResult Run(string args, string stdin = null)
        {
            try
            {
                var psi = new ProcessStartInfo(WslExe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = stdin != null;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.EnvironmentVariables["WSL_UTF8"] = "1";
                using (var p = Process.Start(psi))
                {
                    if (stdin != null)
                    {
                        // Linux erwartet UTF-8 ohne BOM und LF-Zeilenenden.
                        byte[] data = new UTF8Encoding(false).GetBytes(stdin.Replace("\r\n", "\n"));
                        p.StandardInput.BaseStream.Write(data, 0, data.Length);
                        p.StandardInput.Close();
                    }
                    var errTask = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return new CmdResult { ExitCode = p.ExitCode, Output = Clean(o + errTask.Result) };
                }
            }
            catch (Exception ex)
            {
                return new CmdResult { ExitCode = -1, Output = "Fehler beim Start von wsl.exe: " + ex.Message };
            }
        }

        // Ältere wsl.exe-Versionen ignorieren WSL_UTF8 und liefern UTF-16 – Nullbytes entfernen hilft.
        static string Clean(string s)
        {
            return s.Replace("\0", "").Replace("﻿", "").Replace("\r", "").Trim();
        }

        public static List<Distro> ParseOnline(string text)
        {
            var list = new List<Distro>();
            bool header = false;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (!header)
                {
                    if (line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) header = true;
                    continue;
                }
                string[] parts = Regex.Split(line, @"\s{2,}");
                if (!IsSafeName(parts[0])) continue;
                list.Add(new Distro { Name = parts[0], FriendlyName = parts.Length > 1 ? parts[1] : parts[0] });
            }
            return list;
        }

        public static List<Distro> ParseInstalled(string text)
        {
            var list = new List<Distro>();
            var rx = new Regex(@"^\s*(\*)?\s*(\S+)\s+(\S+)\s+(\d+)\s*$");
            foreach (var line in text.Split('\n'))
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                list.Add(new Distro
                {
                    IsDefault = m.Groups[1].Success,
                    Name = m.Groups[2].Value,
                    State = m.Groups[3].Value,
                    Version = m.Groups[4].Value
                });
            }
            return list;
        }

        /// Startet einen Befehl in einem sichtbaren Konsolenfenster (optional als Administrator)
        /// und liefert einen Task, der nach dem Schließen des Fensters endet.
        public static Task<bool> RunConsoleAsync(string cmdLine, bool elevated)
        {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c \"" + cmdLine + " & echo. & echo Fertig. Zum Schliessen eine Taste druecken. & pause >nul\"");
            psi.UseShellExecute = true;
            if (elevated) psi.Verb = "runas";
            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Win32Exception)
            {
                // z. B. UAC-Abfrage abgebrochen
                return Task.FromResult(false);
            }
            if (p == null) return Task.FromResult(false);
            return Task.Run(() => { p.WaitForExit(); p.Dispose(); return true; });
        }
    }

    class MainForm : Form
    {
        Label lblStatus;
        ListView lvOnline, lvInstalled;
        TextBox txtLog;
        Button btnRefresh, btnInstallWsl, btnUninstallWsl, btnInstallDistro, btnStart, btnStop, btnDefault, btnClone, btnFolder, btnRemove;
        List<Distro> installed = new List<Distro>();

        public MainForm()
        {
            Text = "WSL-Starter";
            Size = new Size(1250, 720);
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            // --- Kopfzeile ---
            lblStatus = new Label { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(3, 8, 20, 3) };
            btnRefresh = MakeButton("Aktualisieren", async (s, e) => await RefreshAll());
            btnInstallWsl = MakeButton("WSL installieren", async (s, e) => await InstallWsl());
            btnUninstallWsl = MakeButton("WSL komplett entfernen", async (s, e) => await UninstallWsl());
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
            top.Controls.AddRange(new Control[] { lblStatus, btnRefresh, btnInstallWsl, btnUninstallWsl });

            // --- Verfügbare Distributionen ---
            lvOnline = MakeListView(new[] { "Name", "Bezeichnung" }, new[] { 200, 260 });
            lvOnline.DoubleClick += async (s, e) => await InstallDistro();
            btnInstallDistro = MakeButton("Als neue Instanz installieren …", async (s, e) => await InstallDistro());
            var grpOnline = MakeGroup("Verfügbare Betriebssysteme (online)", lvOnline, btnInstallDistro);

            // --- Installierte Distributionen / Instanzen ---
            lvInstalled = MakeListView(new[] { "Name", "Status", "WSL", "Standard", "Speicherort" }, new[] { 150, 80, 45, 65, 260 });
            lvInstalled.DoubleClick += (s, e) => StartDistro();
            lvInstalled.ShowItemToolTips = true; // Maus über Zeile: Name und vollständiger Speicherort
            btnStart = MakeButton("Starten", (s, e) => StartDistro());
            btnStop = MakeButton("Beenden", async (s, e) => await StopDistro());
            btnDefault = MakeButton("Als Standard", async (s, e) => await SetDefault());
            btnClone = MakeButton("Klonen …", async (s, e) => await CloneDistro());
            btnFolder = MakeButton("Ordner", (s, e) => OpenFolder());
            btnRemove = MakeButton("Entfernen", async (s, e) => await RemoveDistro());
            var grpInstalled = MakeGroup("Installierte Instanzen (jede unabhängig)", lvInstalled, btnStart, btnStop, btnDefault, btnClone, btnFolder, btnRemove);
            lvInstalled.SelectedIndexChanged += (s, e) => UpdateStopButton();

            var split = new SplitContainer { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(grpOnline);
            split.Panel2.Controls.Add(grpInstalled);

            // --- Protokoll ---
            txtLog = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro
            };
            var grpLog = new GroupBox { Text = "Protokoll", Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(6) };
            grpLog.Controls.Add(txtLog);

            Controls.Add(split);
            Controls.Add(grpLog);
            Controls.Add(top);

            Shown += async (s, e) =>
            {
                split.SplitterDistance = ClientSize.Width / 2;
                await RefreshAll();
                // Status (Running/Stopped) alle 3 Sekunden nachführen, z. B. nach "Starten"
                // oder wenn eine Instanz von außen beendet wird.
                var timer = new System.Windows.Forms.Timer { Interval = 3000 };
                timer.Tick += async (s2, e2) => { if (!UseWaitCursor) await RefreshInstalled(); };
                timer.Start();
            };
        }

        // ---------- UI-Helfer ----------

        Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(3) };
            b.Click += click;
            return b;
        }

        ListView MakeListView(string[] cols, int[] widths)
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            return lv;
        }

        GroupBox MakeGroup(string title, Control main, params Button[] buttons)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(6) };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            bar.Controls.AddRange(buttons);
            g.Controls.Add(main);
            g.Controls.Add(bar);
            return g;
        }

        void Log(string text)
        {
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text.Replace("\n", Environment.NewLine) + Environment.NewLine);
        }

        void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            foreach (var b in new[] { btnRefresh, btnInstallWsl, btnUninstallWsl, btnInstallDistro, btnStart, btnStop, btnDefault, btnClone, btnFolder, btnRemove })
                b.Enabled = !busy;
            if (!busy) UpdateStopButton();
        }

        bool IsRunning(string name)
        {
            return installed.Any(d => d.Name == name && d.State.Equals("Running", StringComparison.OrdinalIgnoreCase));
        }

        /// "Beenden" nur aktiv, wenn die ausgewählte Instanz läuft.
        void UpdateStopButton()
        {
            if (UseWaitCursor) return;
            string name = SelectedName(lvInstalled);
            btnStop.Enabled = name != null && IsRunning(name);        }

        string SelectedName(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Text;
        }

        bool Confirm(string text, string caption)
        {
            return MessageBox.Show(this, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        // ---------- Aktionen ----------

        async Task RefreshAll()
        {
            SetBusy(true);
            try
            {
                if (!Wsl.WslExeExists)
                {
                    lblStatus.Text = "WSL: nicht vorhanden";
                    lblStatus.ForeColor = Color.Firebrick;
                    Log("wsl.exe wurde nicht gefunden. Bitte zuerst \"WSL installieren\" ausführen.");
                    lvOnline.Items.Clear();
                    lvInstalled.Items.Clear();
                    return;
                }

                var ver = await Wsl.RunAsync("--version");
                if (ver.ExitCode == 0)
                {
                    string first = ver.Output.Split('\n')[0].Trim();
                    lblStatus.Text = "WSL installiert – " + first;
                    lblStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblStatus.Text = "WSL: nicht (vollständig) installiert";
                    lblStatus.ForeColor = Color.Firebrick;
                }

                Log("Lade Liste der verfügbaren Betriebssysteme ...");
                var online = await Wsl.RunAsync("--list --online");
                var onlineList = Wsl.ParseOnline(online.Output);
                lvOnline.BeginUpdate();
                lvOnline.Items.Clear();
                foreach (var d in onlineList) lvOnline.Items.Add(new ListViewItem(new[] { d.Name, d.FriendlyName }));
                lvOnline.EndUpdate();
                if (onlineList.Count == 0) Log("Keine Online-Liste erhalten:\n" + online.Output);
                else Log(onlineList.Count + " Betriebssysteme verfügbar.");

                await RefreshInstalled(true);
                Log(installed.Count + " Distribution(en) installiert.");
            }
            finally
            {
                SetBusy(false);
            }
        }

        bool refreshingInstalled;
        string lastInstalledState = "";

        /// Liest nur die installierten Instanzen neu ein (schnell, für die automatische Aktualisierung).
        /// Die Liste wird nur neu gezeichnet, wenn sich etwas geändert hat; die Auswahl bleibt erhalten.
        async Task RefreshInstalled(bool force = false)
        {
            if (refreshingInstalled || !Wsl.WslExeExists) return;
            refreshingInstalled = true;
            try
            {
                var inst = await Wsl.RunAsync("--list --verbose");
                var list = Wsl.ParseInstalled(inst.Output);
                try { Wsl.ReadRegistry(list); } catch (Exception ex) { if (force) Log("Registry nicht lesbar: " + ex.Message); }
                string state = string.Join("|", list.Select(d => d.Name + ";" + d.State + ";" + d.Version + ";" + d.IsDefault + ";" + d.BasePath));
                installed = list;
                if (!force && state == lastInstalledState) return;
                lastInstalledState = state;

                string selected = SelectedName(lvInstalled);
                lvInstalled.BeginUpdate();
                lvInstalled.Items.Clear();
                foreach (var d in installed)
                {
                    var item = new ListViewItem(new[] { d.Name, d.State, d.Version, d.IsDefault ? "ja" : "", d.BasePath ?? "" });
                    item.ToolTipText = d.Name + "\nSpeicherort: " + (d.BasePath ?? "unbekannt");
                    if (d.IsDefault) item.Font = new Font(lvInstalled.Font, FontStyle.Bold);
                    lvInstalled.Items.Add(item);
                    if (d.Name == selected) item.Selected = true;
                }
                lvInstalled.EndUpdate();
            }
            finally
            {
                refreshingInstalled = false;
                UpdateStopButton();
            }
        }

        async Task InstallWsl()
        {
            if (!Confirm("WSL (ohne Distribution) jetzt installieren bzw. aktualisieren?\n\nDafür sind Administratorrechte nötig, eventuell ist danach ein Neustart erforderlich.", "WSL installieren"))
                return;
            SetBusy(true);
            Log("Installiere WSL (Administratorfenster) ...");
            bool ok = await Wsl.RunConsoleAsync("wsl.exe --install --no-distribution", true);
            Log(ok ? "Installationsfenster geschlossen. Falls gefordert, Windows neu starten." : "Abgebrochen.");
            SetBusy(false);
            await RefreshAll();
        }

        bool NameTaken(string n)
        {
            return installed.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        }

        string SuggestName(string baseName)
        {
            if (!NameTaken(baseName)) return baseName;
            for (int i = 2; ; i++)
                if (!NameTaken(baseName + "-" + i)) return baseName + "-" + i;
        }

        /// Fragt Instanzname und Speicherort ab. Liefert false bei Abbruch.
        bool AskInstance(string title, string info, string suggested, out string instName, out string location)
        {
            instName = location = null;
            using (var dlg = new InstanceDialog(title, info, suggested, Wsl.BaseFolder, NameTaken))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                instName = dlg.InstanceName;
                location = dlg.InstallPath;
            }
            string parent = Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(parent)) Wsl.BaseFolder = parent;
            return true;
        }

        static void DeleteIfEmpty(string dir)
        {
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { }
        }

        async Task InstallDistro()
        {
            string name = SelectedName(lvOnline);
            if (name == null) { MessageBox.Show(this, "Bitte links ein Betriebssystem auswählen.", "Installieren"); return; }
            if (!Wsl.IsSafeName(name)) return;

            string info = name + " als eigenständige Instanz installieren.\n\n" +
                          "Jede Instanz hat ihr eigenes Dateisystem, eigene Pakete und eigene Benutzer. " +
                          "Am Ende öffnet sich ein Konsolenfenster: dort Linux-Benutzername und Passwort festlegen, danach mit \"exit\" verlassen.";
            if (!AskInstance("Neue Instanz installieren", info, SuggestName(name), out string inst, out string loc)) return;

            SetBusy(true);
            try { Directory.CreateDirectory(Path.GetDirectoryName(loc)); } catch (Exception ex) { Log("Ordner nicht anlegbar: " + ex.Message); }
            Log("Installiere " + name + " als \"" + inst + "\" nach " + loc + " ...");
            bool ok = await Wsl.RunConsoleAsync("wsl.exe --install " + name + " --name " + inst + " --location \"" + loc + "\"", false);
            Log(ok ? "Installationsfenster geschlossen." : "Abgebrochen.");
            SetBusy(false);
            await RefreshAll();
            if (NameTaken(inst))
            {
                SetBusy(true);
                await MakeUidUnique(inst);
                SetBusy(false);
                Log("Instanz \"" + inst + "\" ist eingerichtet.");
                await RefreshAll();
            }
            else if (ok)
            {
                DeleteIfEmpty(loc);
                Log("Instanz \"" + inst + "\" wurde nicht angelegt. Unterstützt diese Distribution keinen eigenen Namen, " +
                    "einmal normal installieren und danach mit \"Klonen …\" weitere Instanzen erzeugen.");
            }
        }

        async Task CloneDistro()
        {
            string src = SelectedName(lvInstalled);
            var d = installed.FirstOrDefault(x => x.Name == src);
            if (d == null || !Wsl.IsSafeName(src)) { MessageBox.Show(this, "Bitte rechts die Instanz auswählen, die kopiert werden soll.", "Klonen"); return; }

            long size = 0;
            try
            {
                string vhdx = d.BasePath == null ? null : Path.Combine(d.BasePath, "ext4.vhdx");
                if (vhdx != null && File.Exists(vhdx)) size = new FileInfo(vhdx).Length;
            }
            catch { }

            string info = "Erstellt eine unabhängige Kopie von \"" + src + "\" mit allen Programmen, Dateien und Benutzern" +
                          (size > 0 ? " (virtuelle Festplatte derzeit " + FormatSize(size) + ")" : "") + ".\n\n" +
                          "Danach sind beide Instanzen völlig getrennt: Änderungen in der einen wirken sich nicht auf die andere aus. " +
                          "\"" + src + "\" wird dafür kurz beendet.";
            if (!AskInstance("Instanz klonen", info, SuggestName(src), out string inst, out string loc)) return;

            try
            {
                Directory.CreateDirectory(loc);
                long free = new DriveInfo(Path.GetPathRoot(loc)).AvailableFreeSpace;
                if (size > 0 && free < size * 2 &&
                    !Confirm("Auf dem Laufwerk sind nur " + FormatSize(free) + " frei; benötigt werden bis zu " + FormatSize(size * 2) +
                             " (Zwischendatei + neue Festplatte). Trotzdem fortfahren?", "Klonen"))
                {
                    DeleteIfEmpty(loc);
                    return;
                }
            }
            catch (Exception ex) { Log("Ordner nicht anlegbar: " + ex.Message); return; }

            string tar = Path.Combine(loc, "_klon.tar");
            string cmd = "wsl.exe --terminate " + src +
                         " & wsl.exe --export " + src + " \"" + tar + "\" && wsl.exe --import " + inst + " \"" + loc + "\" \"" + tar + "\"" +
                         " & del /q \"" + tar + "\"";
            SetBusy(true);
            Log("Klone " + src + " → " + inst + " (" + loc + ") ...");
            bool ok = await Wsl.RunConsoleAsync(cmd, false);
            SetBusy(false);
            try { if (File.Exists(tar)) File.Delete(tar); } catch { }
            await RefreshAll();

            if (!NameTaken(inst))
            {
                DeleteIfEmpty(loc);
                Log(ok ? "Klonen fehlgeschlagen, Meldungen im Konsolenfenster beachten." : "Abgebrochen.");
                return;
            }
            // Nach einem Import wäre sonst root der Standardbenutzer.
            if (d.DefaultUid > 0)
                Log(Wsl.SetDefaultUid(inst, d.DefaultUid)
                    ? "Standardbenutzer (UID " + d.DefaultUid + ") von " + src + " übernommen."
                    : "Standardbenutzer konnte nicht gesetzt werden, Anmeldung erfolgt als root.");
            SetBusy(true);
            await MakeUidUnique(inst);
            SetBusy(false);
            Log("Instanz \"" + inst + "\" ist fertig.");
            await RefreshAll();
        }

        /// Sorgt dafür, dass der Standardbenutzer der Instanz eine UID hat, die keine andere Instanz verwendet.
        /// Sonst scheitert bei gleichzeitig laufenden Instanzen die systemd-Benutzersitzung
        /// ("Failed to start the systemd user session").
        async Task MakeUidUnique(string inst)
        {
            var idr = await Wsl.RunAsync("-d " + inst + " -e id -u");
            if (idr.ExitCode != 0 || !int.TryParse(idr.Output.Split('\n').Last().Trim(), out int uid) || uid < 1000) return;

            // UIDs der übrigen Instanzen (Registry); 1000 ist die übliche UID des ersten Benutzers.
            var used = new HashSet<int> { 1000 };
            foreach (var d in installed)
                if (!d.Name.Equals(inst, StringComparison.OrdinalIgnoreCase) && d.DefaultUid >= 1000) used.Add(d.DefaultUid);
            if (!used.Contains(uid)) { Log("UID " + uid + " in \"" + inst + "\" wird von keiner anderen Instanz verwendet."); return; }

            Log("UID " + uid + " wird schon von einer anderen Instanz verwendet, vergebe in \"" + inst + "\" eine freie UID ...");
            await Wsl.RunAsync("--terminate " + inst);
            var r = await Wsl.RunAsync("-d " + inst + " -u root -e bash -s -- " + uid + " " + string.Join(" ", used), Wsl.ChangeUidScript);
            var m = Regex.Match(r.Output, @"NEWUID=(\d+) USER=(\S+)");
            if (r.ExitCode != 0 || !m.Success)
            {
                Log("UID konnte nicht geändert werden (" + r.ExitCode + "): " + r.Output);
                return;
            }
            int newUid = int.Parse(m.Groups[1].Value);
            Wsl.SetDefaultUid(inst, newUid);
            await Wsl.RunAsync("--terminate " + inst); // sauber neu starten, damit user@NEU.service startet
            Log("Benutzer \"" + m.Groups[2].Value + "\" in \"" + inst + "\" hat jetzt UID " + newUid + " (vorher " + uid + ").");
        }

        void OpenFolder()
        {
            var d = installed.FirstOrDefault(x => x.Name == SelectedName(lvInstalled));
            if (d == null) { MessageBox.Show(this, "Bitte rechts eine Instanz auswählen.", "Ordner"); return; }
            if (d.BasePath == null || !Directory.Exists(d.BasePath)) { Log("Speicherort von " + d.Name + " unbekannt."); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + d.BasePath + "\"") { UseShellExecute = true });
        }

        static string FormatSize(long bytes)
        {
            return bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (bytes / (double)(1L << 20)).ToString("0") + " MB";
        }

        async void StartDistro()
        {
            string name = SelectedName(lvInstalled);
            if (name == null || !Wsl.IsSafeName(name)) { MessageBox.Show(this, "Bitte rechts eine Distribution auswählen.", "Starten"); return; }
            try
            {
                Process.Start(new ProcessStartInfo(Wsl.WslExe, "-d " + name + " --cd ~") { UseShellExecute = true });
                Log(name + " gestartet.");
            }
            catch (Exception ex) { Log("Fehler: " + ex.Message); return; }
            // Der Start dauert einen Moment; danach Status sofort neu anzeigen.
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1000);
                await RefreshInstalled();
                if (installed.Any(d => d.Name == name && d.State.Equals("Running", StringComparison.OrdinalIgnoreCase))) break;
            }
        }

        async Task StopDistro()
        {
            string name = SelectedName(lvInstalled);
            if (name == null || !Wsl.IsSafeName(name)) { MessageBox.Show(this, "Bitte rechts eine Instanz auswählen.", "Beenden"); return; }
            if (!IsRunning(name)) { Log(name + " läuft nicht."); return; }
            if (!Confirm("\"" + name + "\" jetzt beenden?\n\nAlle Programme darin werden sofort gestoppt (offene Terminals, Docker-Container, Ollama, AnythingLLM …). " +
                         "Nicht gespeicherte Daten in diesen Programmen gehen verloren. Die Instanz selbst und ihre Dateien bleiben erhalten.", "Instanz beenden"))
                return;
            SetBusy(true);
            var r = await Wsl.RunAsync("--terminate " + name);
            Log(r.ExitCode == 0 ? name + " wurde beendet." : "Fehler beim Beenden: " + r.Output);
            SetBusy(false);
            await RefreshInstalled(true);
        }

        async Task SetDefault()
        {
            string name = SelectedName(lvInstalled);
            if (name == null || !Wsl.IsSafeName(name)) { MessageBox.Show(this, "Bitte rechts eine Distribution auswählen.", "Standard"); return; }
            SetBusy(true);
            var r = await Wsl.RunAsync("--set-default " + name);
            Log(r.ExitCode == 0 ? name + " ist jetzt Standard." : r.Output);
            SetBusy(false);
            await RefreshAll();
        }

        async Task RemoveDistro()
        {
            string name = SelectedName(lvInstalled);
            if (name == null || !Wsl.IsSafeName(name)) { MessageBox.Show(this, "Bitte rechts eine Distribution auswählen.", "Entfernen"); return; }
            if (!Confirm("\"" + name + "\" wirklich entfernen?\n\nALLE Dateien in dieser Linux-Installation werden unwiderruflich gelöscht!", "Distribution entfernen"))
                return;
            string basePath = installed.FirstOrDefault(x => x.Name == name)?.BasePath;
            SetBusy(true);
            Log("Entferne " + name + " ...");
            await Wsl.RunAsync("--terminate " + name);
            var r = await Wsl.RunAsync("--unregister " + name);
            Log(r.ExitCode == 0 ? name + " wurde entfernt." : "Fehler: " + r.Output);
            if (r.ExitCode == 0 && basePath != null) DeleteIfEmpty(basePath);
            SetBusy(false);
            await RefreshAll();
        }

        async Task UninstallWsl()
        {
            string names = installed.Count == 0 ? "(keine)" : string.Join(", ", installed.Select(d => d.Name));
            if (!Confirm("WSL komplett entfernen?\n\nDabei werden zuerst ALLE installierten Distributionen samt Daten unwiderruflich gelöscht:\n" + names +
                         "\n\nAnschließend wird WSL selbst deinstalliert (Administratorrechte nötig).", "WSL komplett entfernen"))
                return;

            var features = MessageBox.Show(this,
                "Sollen zusätzlich die Windows-Features \"Windows-Subsystem für Linux\" und \"VM-Plattform\" deaktiviert werden?\n\n" +
                "Hinweis: Die VM-Plattform wird evtl. auch von anderen Programmen (z. B. Docker, Android-Emulatoren) benötigt.",
                "Windows-Features", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (features == DialogResult.Cancel) return;

            SetBusy(true);
            try
            {
                Log("Fahre WSL herunter ...");
                await Wsl.RunAsync("--shutdown");
                foreach (var d in installed.ToList())
                {
                    if (!Wsl.IsSafeName(d.Name)) continue;
                    var r = await Wsl.RunAsync("--unregister " + d.Name);
                    Log(r.ExitCode == 0 ? d.Name + " entfernt." : "Fehler bei " + d.Name + ": " + r.Output);
                }

                string cmd = "wsl.exe --uninstall";
                if (features == DialogResult.Yes)
                    cmd += " & dism.exe /online /disable-feature /featurename:Microsoft-Windows-Subsystem-Linux /norestart" +
                           " & dism.exe /online /disable-feature /featurename:VirtualMachinePlatform /norestart";
                Log("Deinstalliere WSL (Administratorfenster) ...");
                bool ok = await Wsl.RunConsoleAsync(cmd, true);
                Log(ok ? "Deinstallation abgeschlossen. Bitte Windows neu starten." : "Deinstallation abgebrochen.");
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshAll();
        }
    }
}
