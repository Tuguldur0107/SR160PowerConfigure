using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SR160Setup
{
    // Self-contained per-user installer. The application files are embedded
    // as resources in this exe, so Setup.exe is the only thing that needs
    // handing to anyone. Installs under %LOCALAPPDATA% on purpose: no UAC
    // prompt, and it matches the app's own settings, which live in HKCU.
    public class SetupForm : Form
    {
        private const string AppTitle = "SR160 Power Config";
        private const string FolderName = "SR160PowerConfig";
        private const string MainExe = "SR160PowerConfig.exe";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SR160PowerConfig";

        // Everything the app touches at runtime: the exe, the two native
        // DLLs it P/Invokes, and the logo it loads for the tray icon.
        private static readonly string[] Payload =
        {
            MainExe,
            "UHFAPI.dll",
            "libusb-1.0.dll",
            "Logo.png"
        };

        private readonly string targetDir;
        private Label lblStatus;
        private Button btnInstall;
        private Button btnClose;

        public SetupForm()
        {
            targetDir = Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                FolderName);

            Text = AppTitle + " — Суулгах / Setup";
            ClientSize = new Size(460, 210);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            var pnlHeader = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(460, 52),
                BackColor = Color.FromArgb(33, 37, 41)
            };
            pnlHeader.Controls.Add(new Label
            {
                Text = "CHIPMO",
                Location = new Point(15, 5),
                AutoSize = true,
                Font = new Font("Segoe UI", 15, FontStyle.Bold),
                ForeColor = Color.White
            });
            pnlHeader.Controls.Add(new Label
            {
                Text = "Chainway SR160 UHF RFID",
                Location = new Point(16, 30),
                AutoSize = true,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(173, 181, 189)
            });

            var lblWhere = new Label
            {
                Text = "Суулгах хавтас / Install folder:",
                Location = new Point(14, 66),
                AutoSize = true
            };
            var txtPath = new TextBox
            {
                Text = targetDir,
                Location = new Point(14, 86),
                Size = new Size(430, 22),
                ReadOnly = true,
                Font = new Font("Consolas", 8)
            };

            lblStatus = new Label
            {
                Text = "Start цэс болон Desktop-д товчлол үүсгэнэ.\r\nCreates Start Menu and Desktop shortcuts.",
                Location = new Point(14, 116),
                Size = new Size(430, 34),
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8)
            };

            btnInstall = new Button
            {
                Text = "Суулгах / Install",
                Location = new Point(232, 162),
                Size = new Size(120, 32),
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(76, 175, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnInstall.FlatAppearance.BorderSize = 0;
            btnInstall.Click += BtnInstall_Click;

            btnClose = new Button
            {
                Text = "Хаах / Close",
                Location = new Point(358, 162),
                Size = new Size(88, 32)
            };
            btnClose.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { pnlHeader, lblWhere, txtPath, lblStatus, btnInstall, btnClose });
        }

        private void BtnInstall_Click(object sender, EventArgs e)
        {
            btnInstall.Enabled = false;
            try
            {
                if (!EnsureAppClosed(false)) { btnInstall.Enabled = true; return; }
                InstallFiles();
                lblStatus.ForeColor = Color.Green;
                lblStatus.Text = "Амжилттай суулгалаа.\r\nInstalled successfully.";
                btnClose.Text = "Дуусгах / Finish";
            }
            catch (Exception ex)
            {
                lblStatus.ForeColor = Color.Firebrick;
                lblStatus.Text = "Алдаа / Error: " + ex.Message;
                btnInstall.Enabled = true;
            }
        }

        // Shared by the Install button and by /silent, so an in-app update
        // performs exactly the same install a manual one does.
        private string InstallFiles()
        {
            Directory.CreateDirectory(targetDir);
            foreach (string name in Payload)
                ExtractResource(name, Path.Combine(targetDir, name));

            string exePath = Path.Combine(targetDir, MainExe);
            CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                AppTitle + ".lnk"), exePath);
            CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                AppTitle + ".lnk"), exePath);

            RepointAutostartIfPresent(exePath);
            return exePath;
        }

        // Used by the app's own updater: install with no window and no
        // prompting, then relaunch so the user lands back where they were.
        private static void RunSilent()
        {
            SetupForm form = new SetupForm();
            form.EnsureAppClosed(true);
            string exePath = form.InstallFiles();
            try { Process.Start(exePath); }
            catch { }
        }

        // The payload can't overwrite a running copy, so deal with that up
        // front rather than failing halfway through with a locked-file error.
        // In silent mode there is no window to prompt into, so consent is
        // implied by the user having accepted the update.
        private bool EnsureAppClosed(bool silent)
        {
            Process[] running = Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(MainExe));
            if (running.Length == 0) return true;

            DialogResult answer = silent ? DialogResult.OK : MessageBox.Show(
                "Программ ажиллаж байна. Хаагаад үргэлжлүүлэх үү?\r\n\r\n" +
                "The application is running. Close it and continue?",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return false;

            foreach (Process proc in running)
            {
                try
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(4000)) proc.Kill();
                    proc.WaitForExit(4000);
                }
                catch { }
            }
            return true;
        }

        private static void ExtractResource(string resourceName, string destination)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream src = asm.GetManifestResourceStream(resourceName))
            {
                if (src == null)
                    throw new InvalidOperationException("Missing embedded file: " + resourceName);
                using (FileStream dst = File.Create(destination))
                    src.CopyTo(dst);
            }
        }

        // Built through the WScript.Shell COM object by late binding, which
        // avoids needing an interop assembly reference at build time.
        private static void CreateShortcut(string linkPath, string targetPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell unavailable.");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link,
                new object[] { targetPath });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                new object[] { Path.GetDirectoryName(targetPath) });
            linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                new object[] { targetPath + ",0" });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                new object[] { AppTitle });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }

        // If "Start with Windows" was switched on against an older copy (for
        // example one run straight from the Desktop), leave the setting alone
        // but point it at the freshly installed exe — otherwise Windows would
        // keep launching the stale copy after this install.
        private static void RepointAutostartIfPresent(string exePath)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (key.GetValue(RunValueName) == null) return;
                    key.SetValue(RunValueName, "\"" + exePath + "\"");
                }
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false;
            if (args != null)
            {
                foreach (string arg in args)
                    if (string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            }

            if (silent)
            {
                // Deliberately swallowed: a failed update must leave the
                // existing install untouched and not surface errors to
                // someone who is mid-shift.
                try { RunSilent(); }
                catch { }
                return;
            }

            Application.Run(new SetupForm());
        }
    }
}
