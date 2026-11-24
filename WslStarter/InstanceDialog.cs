// Dialog für Name und Speicherort einer neuen, unabhängigen WSL-Instanz.

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WslStarter
{
    class InstanceDialog : Form
    {
        readonly TextBox txtName, txtLocation;
        readonly Func<string, bool> nameTaken;
        string baseFolder;
        bool locationEdited;

        public string InstanceName { get { return txtName.Text.Trim(); } }
        public string InstallPath { get { return txtLocation.Text.Trim(); } }

        /// <param name="info">Erklärtext oben im Dialog</param>
        /// <param name="suggestedName">Vorschlag für den Instanznamen</param>
        /// <param name="baseFolder">Basisordner, in dem pro Instanz ein Unterordner angelegt wird</param>
        /// <param name="nameTaken">Prüft, ob ein Name schon vergeben ist</param>
        public InstanceDialog(string title, string info, string suggestedName, string baseFolder, Func<string, bool> nameTaken)
        {
            this.baseFolder = baseFolder;
            this.nameTaken = nameTaken;

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9.5f);
            Padding = new Padding(10);

            var lblInfo = new Label { Text = info, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 3, 3, 12) };
            txtName = new TextBox { Width = 300, Text = suggestedName };
            txtLocation = new TextBox { Width = 460 };
            var btnBrowse = new Button { Text = "…", Width = 36 };
            var btnOk = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.None };
            var btnCancel = new Button { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };

            txtName.TextChanged += (s, e) => { if (!locationEdited) UpdateLocation(); };
            txtLocation.TextChanged += (s, e) => { if (txtLocation.Focused) locationEdited = true; };
            btnBrowse.Click += (s, e) => Browse();
            btnOk.Click += (s, e) => { if (CheckInput(out string err)) DialogResult = DialogResult.OK; else MessageBox.Show(this, err, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); };

            // Kein Dock: Ein angedocktes Panel liefert dem AutoSize-Formular keine Wunschgröße (Fenster wäre zu klein).
            var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Location = new Point(10, 10) };
            grid.Controls.Add(lblInfo, 0, 0);
            grid.SetColumnSpan(lblInfo, 3);
            grid.Controls.Add(new Label { Text = "Instanzname:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(txtName, 1, 1);
            grid.Controls.Add(new Label { Text = "Speicherort:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            grid.Controls.Add(txtLocation, 1, 2);
            grid.Controls.Add(btnBrowse, 2, 2);
            var hint = new Label
            {
                Text = "Erlaubt: Buchstaben, Ziffern, Punkt, Bindestrich, Unterstrich. Jede Instanz bekommt eine eigene virtuelle Festplatte (ext4.vhdx) in diesem Ordner.",
                AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 10)
            };
            grid.Controls.Add(hint, 0, 3);
            grid.SetColumnSpan(hint, 3);
            var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right };
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnOk);
            grid.Controls.Add(bar, 0, 4);
            grid.SetColumnSpan(bar, 3);
            Controls.Add(grid);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            UpdateLocation();
            Shown += (s, e) => { txtName.Focus(); txtName.SelectAll(); };
        }

        void UpdateLocation()
        {
            string n = txtName.Text.Trim();
            txtLocation.Text = Wsl.IsSafeName(n) ? Path.Combine(baseFolder, n) : baseFolder;
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Basisordner für die Instanz wählen (darin wird ein Unterordner mit dem Instanznamen angelegt)", UseDescriptionForTitle = true, ShowNewFolderButton = true })
            {
                if (Directory.Exists(baseFolder)) dlg.SelectedPath = baseFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                baseFolder = dlg.SelectedPath;
                locationEdited = false;
                UpdateLocation();
            }
        }

        bool CheckInput(out string error)
        {
            string n = InstanceName, loc = InstallPath;
            if (!Wsl.IsSafeName(n)) { error = "Ungültiger Instanzname."; return false; }
            if (nameTaken(n)) { error = "Eine Distribution namens \"" + n + "\" gibt es bereits."; return false; }
            if (!Wsl.IsSafePath(loc)) { error = "Ungültiger Speicherort. Bitte einen vollständigen Pfad ohne die Zeichen \" % & ^ | < > ! angeben."; return false; }
            if (Directory.Exists(loc) && Directory.EnumerateFileSystemEntries(loc).Any()) { error = "Der Ordner\n" + loc + "\nexistiert bereits und ist nicht leer."; return false; }
            error = null;
            return true;
        }
    }
}
