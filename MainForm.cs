using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SR160PowerConfig
{
    public class MainForm : Form, IMessageFilter
    {
        // Win32: keyboard layout солих
        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")]
        private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

        // Win32: system-wide low-level keyboard hook. IMessageFilter
        // (PreFilterMessage, below) only sees keystrokes sent to this app's
        // own windows, so on-screen tracking (the tag list, Auto/Hold
        // start-stop logic) goes dark the moment some other window is
        // focused. This hook mirrors the same decoding into our own list
        // regardless of focus — it never blocks anything, so the reader's
        // native typed output still reaches whatever window is actually
        // focused exactly as before; this only adds a parallel, reliable
        // capture into this app's own display.
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // Used only to tell whether this app itself currently has focus —
        // while it does, PreFilterMessage already handles the keystrokes, so
        // the hook callback steps aside to avoid double-committing the tag.
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Kept as a field, not a local — the delegate must stay alive for as
        // long as the hook is installed, or the GC can collect it out from
        // under the unmanaged callback and crash the process.
        private LowLevelKeyboardProc keyboardHookProc;
        private IntPtr keyboardHookHandle = IntPtr.Zero;
        // Mirrors txtEpcInput.Text while this app isn't focused, for the
        // disconnected/Bluetooth manual-entry path only — there's no visible
        // textbox receiving the keystrokes in that case. Connected-mode
        // trigger detection no longer uses this at all; see learnedDeviceHandle.
        private string bgEpcBuffer = "";

        // Windows Raw Input identifies WHICH physical HID device generated a
        // keystroke (RawInputKeyboard.cs), unlike the WH_KEYBOARD_LL hook
        // above, which only sees key codes and can't tell the reader's own
        // keyboard-wedge interface apart from the laptop's built-in keyboard.
        // That ambiguity caused ordinary typing (words containing 0-9/A-F)
        // to be misread as trigger pulls. Once the user runs "Learn Trigger
        // Device" (pulls the trigger once while listening), only raw input
        // from that exact device handle counts as a genuine trigger signal
        // for the rest of the session — everything else, including the
        // user's own keyboard, is structurally excluded, not just filtered
        // by guessing.
        // Raw Input handles are only meaningful for the current session —
        // they change on replug and on every reboot, so the handle alone
        // can't be remembered. What IS stable is the device's hardware
        // identity from its interface path, e.g. "VID_2047&PID_0301&MI_01"
        // (vendor + product + which HID interface). That's what gets saved,
        // and handles are matched back to it lazily at runtime.
        // Deliberately matched on VID/PID/MI rather than the full path: the
        // rest of the path encodes which USB port the reader is plugged
        // into, so a full-path match would silently stop working the moment
        // someone used a different port.
        private string learnedDeviceSignature;
        private IntPtr learnedDeviceHandle = IntPtr.Zero;
        private readonly Dictionary<IntPtr, string> rawDeviceSigCache = new Dictionary<IntPtr, string>();
        private bool isLearningTriggerDevice;
        private bool autoConnectSuppressed;
        private Timer autoConnectTimer;
        private const string SettingsRegistryKey = @"Software\SR160PowerConfig";
        private const string TriggerDeviceValueName = "TriggerDeviceSignature";
        private const string DefaultsAppliedValueName = "DefaultsApplied";
        private const string TargetProcessValueName = "TargetWindowProcess";
        private const string TargetTitleValueName = "TargetWindowTitle";
        private string savedTargetProcess;
        private string savedTargetTitle;
        private bool suppressTargetSync;

        // The reader's own firmware still types its native keystroke burst
        // into whatever window is focused regardless (confirmed elsewhere in
        // this codebase not to be something we can suppress) — this is a
        // second, independent delivery of the SDK-confirmed EPC into a
        // window the user explicitly picked beforehand (see cmbExternalTarget)
        // — never "whatever happens to be focused," after an earlier version
        // of this that captured focus automatically ended up injecting scan
        // data into an unrelated chat window.
        private IntPtr externalTargetWindow = IntPtr.Zero;

        private IntPtr originalLayout;
        private IntPtr englishLayout;

        private Button btnConnect;
        private Button btnDisconnect;
        private Button btnRefresh;
        private Button btnSave;
        private Button btnClearList;
        private Label lblStatus;
        private Label lblCurrentPower;
        private Label lblPowerValue;
        private Label lblTagCount;
        private TrackBar trackPower;
        private NumericUpDown numPower;
        private TextBox txtEpcInput;
        private ListView lvTags;
        private ComboBox cmbLang;
        private bool isConnected;
        private bool keyboardActivityDetected;
        private bool suppressSync;
        private bool suppressBeepSync;
        private bool isScanning;
        private CheckBox chkBeep;
        private Label lblConnType;
        private Dictionary<string, int> tagCounts;
        private Dictionary<string, DateTime> tagLastSeen;
        private TimeSpan recountCooldown = TimeSpan.FromMilliseconds(1000);
        private NumericUpDown numCooldown;
        private Button btnScan;
        private Timer scanTimer;
        private int scanTicksSinceInventory;
        // Gap since the last trigger keystroke required before a fresh burst
        // counts as a genuinely NEW press (vs. another tag read from the same
        // squeeze — a single pull can read several nearby tags back-to-back).
        private static readonly TimeSpan TriggerBurstGap = TimeSpan.FromMilliseconds(450);
        // Minimum spacing between trigger-driven start/stop flips. Longer
        // than TriggerBurstGap on purpose, so a squeeze that stutters
        // internally still counts as a single gesture.
        private static readonly TimeSpan TriggerToggleDebounce = TimeSpan.FromMilliseconds(900);
        private DateTime lastToggleTime = DateTime.MinValue;
        private DateTime lastTriggerKeystrokeTime;
        // Same instant as lastTriggerKeystrokeTime, kept as a 32-bit
        // tick count so the relay worker thread can read it atomically.
        private volatile int lastTriggerTickMs;
        // How long the external relay waits, with no further raw keydowns
        // from the learned reader device, before assuming its native
        // keystroke burst has finished typing and it's safe to inject our
        // own text into the same target control without interleaving.
        private static readonly TimeSpan NativeBurstQuietPeriod = TimeSpan.FromMilliseconds(350);
        // How long a natively-typed EPC stays eligible for suppression. Long
        // enough to cover the gap between the reader typing it and the SDK
        // poll reporting the same tag, short enough that re-scanning the same
        // tag a moment later still relays normally.
        private static readonly TimeSpan NativeEchoWindow = TimeSpan.FromSeconds(5);
        private string nativeEpcBuffer = "";
        private readonly object nativeEchoLock = new object();
        private readonly Dictionary<string, DateTime> nativeEchoedEpcs = new Dictionary<string, DateTime>();
        // How long to keep a trigger-driven Hold session alive with no new
        // keystrokes before assuming the trigger was released. There's no real
        // "released" signal to read, so this is a guess — set generously since
        // the reader can go quiet for several seconds while still actively
        // hunting for a stubborn last tag in a larger batch, not just when the
        // trigger was actually let go. A quick tap on the Scan button always
        // stops the session immediately regardless of this timeout.
        private DateTime lastScanActivity = DateTime.MinValue;
        private TimeSpan autoStopIdle = TimeSpan.FromSeconds(5);
        private RadioButton rbModeSingle;
        private RadioButton rbModeAuto;
        private RadioButton rbAutoClick;
        private RadioButton rbAutoHold;

        // Controls that need text updates
        private Label lblDevice;
        private GroupBox grpConn;
        private GroupBox grpPower;
        private GroupBox grpScanMode;
        private Label lblInput;
        private Label lblScanModePick;
        private Label lblAutoBehavior;
        private Label lblCooldown;
        private Label lblAutoStop;
        private NumericUpDown numAutoStop;
        private CheckBox chkRepeatKeepsAlive;
        // Mirrors chkRepeatKeepsAlive; read inside the per-frame decode
        // loop, so kept as a plain field rather than touching a control there.
        private bool repeatReadsKeepScanAlive;
        private TabControl tabs;
        private TabPage tabScan;
        private TabPage tabSetup;
        private TabPage tabAdvanced;
        private GroupBox grpAdvanced;
        private GroupBox grpTrigger;
        private Button btnLearnTrigger;
        private Label lblTriggerDeviceStatus;
        private Label lblExternalTargetPick;
        private Label lblExternalTargetStatus;
        private ComboBox cmbExternalTarget;
        private Button btnRefreshWindows;
        private CheckBox chkClearOnScan;
        private CheckBox chkMinimizeToTray;
        private CheckBox chkStartWithWindows;
        private bool suppressStartupSync;
        private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "SR160PowerConfig";
        private NotifyIcon trayIcon;
        // Distinguishes "user chose Exit" from "user clicked X" — the latter
        // hides to tray instead of quitting while the option is on.
        private bool isReallyExiting;

        public MainForm()
        {
            tagCounts = new Dictionary<string, int>();
            tagLastSeen = new Dictionary<string, DateTime>();
            InitializeComponents();
            InitializeTrayIcon();
            LoadStartWithWindowsState();
            ApplyFirstRunDefaults();
            // chkMinimizeToTray defaults to checked, but its CheckedChanged
            // ran while trayIcon was still null (InitializeComponents happens
            // before InitializeTrayIcon), so sync the icon up explicitly.
            trayIcon.Visible = chkMinimizeToTray.Checked;
            LoadTriggerDevice();
            UpdateTriggerDeviceStatus();
            LoadExternalTarget();
            ResolveExternalTarget();
            RefreshExternalTargetList();
            Application.AddMessageFilter(this);

            // English keyboard layout ачаалах (00000409 = US English)
            englishLayout = LoadKeyboardLayout("00000409", 0);

            keyboardHookProc = KeyboardHookCallback;
            keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardHookProc, GetModuleHandle(null), 0);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RawInputKeyboard.Register(this.Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            autoConnectTimer = new Timer();
            autoConnectTimer.Interval = 3000;
            autoConnectTimer.Tick += delegate { TryAutoConnect(); EnsureExternalTargetResolved(); };
            autoConnectTimer.Start();
            TryAutoConnect();
            StartUpdateCheck();
        }

        // Runs off the UI thread so a slow or unreachable network can never
        // delay startup; the reader stays usable regardless of the outcome.
        private void StartUpdateCheck()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = Updater.CheckForUpdate();
                if (info == null) return;
                try
                {
                    if (IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate { OfferUpdate(info); });
                }
                catch { }
            });
        }

        private void OfferUpdate(UpdateInfo info)
        {
            string notes = info.Notes ?? "";
            if (notes.Length > 500) notes = notes.Substring(0, 500) + "...";
            string message = Lang.Get("updateAvailable", info.Tag)
                + "\n\n" + Lang.Get("updateCurrent", Updater.CurrentVersion.ToString())
                + (notes.Length > 0 ? "\n\n" + notes : "")
                + "\n\n" + Lang.Get("updateAsk");

            if (MessageBox.Show(message, Lang.Get("updateTitle"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            string installer;
            try
            {
                installer = Updater.Download(info.DownloadUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.Get("errGeneric", ex.Message), Lang.Get("errTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // The installer closes this process, replaces the files and
                // starts the new build, so nothing is done here afterwards.
                System.Diagnostics.Process.Start(installer, "/silent");
                isReallyExiting = true;
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.Get("errGeneric", ex.Message), Lang.Get("errTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == RawInputKeyboard.WM_INPUT)
            {
                try { OnRawKeyboardInput(m.LParam); } catch { }
            }
            base.WndProc(ref m);
        }

        // Sole trigger-detection path for connected/USB mode — see the
        // learnedDeviceHandle field comment for why. Runs regardless of
        // which window has focus (Raw Input was registered with
        // RIDEV_INPUTSINK), including this app's own.
        private void ChkStartWithWindows_CheckedChanged(object sender, EventArgs e)
        {
            if (suppressStartupSync) return;
            bool wanted = chkStartWithWindows.Checked;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true))
                {
                    if (key == null) throw new InvalidOperationException("Run key unavailable.");
                    if (wanted) key.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(StartupValueName, false);
                }
            }
            catch (Exception ex)
            {
                // Put the checkbox back where it was so it never claims a
                // state the registry doesn't actually reflect.
                suppressStartupSync = true;
                chkStartWithWindows.Checked = !wanted;
                suppressStartupSync = false;
                MessageBox.Show(Lang.Get("errGeneric", ex.Message), Lang.Get("errTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // "Start with Windows" is real registry state, not just a UI
        // toggle, so it can't simply default to checked — LoadStartWithWindows
        // State would overwrite that with whatever the registry actually
        // says. Instead it's switched on once, on the very first run ever,
        // and a marker is written so a later deliberate un-tick is never
        // undone on the next launch.
        private void ApplyFirstRunDefaults()
        {
            bool alreadyApplied = false;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false))
                {
                    if (key != null) alreadyApplied = key.GetValue(DefaultsAppliedValueName) != null;
                }
            }
            catch { }
            if (alreadyApplied) return;

            // Fires ChkStartWithWindows_CheckedChanged, which writes the Run key.
            chkStartWithWindows.Checked = true;

            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsRegistryKey))
                {
                    if (key != null) key.SetValue(DefaultsAppliedValueName, 1);
                }
            }
            catch { }
        }

        private void LoadStartWithWindowsState()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false))
                {
                    if (key == null) return;
                    suppressStartupSync = true;
                    chkStartWithWindows.Checked = (key.GetValue(StartupValueName) != null);
                    suppressStartupSync = false;
                }
            }
            catch { }
        }

        private void InitializeTrayIcon()
        {
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add(Lang.Get("trayShow"), null, delegate { ShowFromTray(); });
            trayMenu.Items.Add(Lang.Get("trayExit"), null, delegate { isReallyExiting = true; Close(); });

            trayIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = Lang.Get("windowTitle"),
                ContextMenuStrip = trayMenu,
                Visible = false
            };
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private Icon LoadTrayIcon()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.StartupPath, "Logo.png");
                if (System.IO.File.Exists(path))
                {
                    using (Bitmap bmp = new Bitmap(path))
                        return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (chkMinimizeToTray != null && chkMinimizeToTray.Checked
                && WindowState == FormWindowState.Minimized)
                Hide();
        }

        private void OnRawKeyboardInput(IntPtr lParam)
        {
            IntPtr deviceHandle;
            int vkCode;
            bool keyDown;
            if (!RawInputKeyboard.TryProcess(lParam, out deviceHandle, out vkCode, out keyDown)) return;
            if (!keyDown || deviceHandle == IntPtr.Zero) return;

            if (isLearningTriggerDevice)
            {
                rawDeviceSigCache.Clear();
                learnedDeviceHandle = deviceHandle;
                learnedDeviceSignature = GetDeviceSignature(deviceHandle);
                isLearningTriggerDevice = false;
                // Learning finishes on the FIRST keydown, but the pull that
                // taught us the device is still mid-burst — ~25 more keydowns
                // (the EPC hex digits + Enter) are about to arrive from this
                // same squeeze. Without seeding the burst clock here they'd
                // fall through below with a stale (huge) gap, look like a
                // fresh pull, and kick off a scan the user never asked for.
                lastTriggerKeystrokeTime = DateTime.Now;
                lastTriggerTickMs = Environment.TickCount;
                SaveTriggerDevice();
                UpdateTriggerDeviceStatus();
                btnLearnTrigger.Text = Lang.Get("btnLearnTrigger");
                return;
            }

            if (!isConnected) return;
            if (!IsLearnedDevice(deviceHandle)) return;

            TimeSpan gap = DateTime.Now - lastTriggerKeystrokeTime;
            lastTriggerKeystrokeTime = DateTime.Now;
                lastTriggerTickMs = Environment.TickCount;
            if (gap >= TriggerBurstGap) HandleTriggerPull(gap);

            RecordNativeKeystroke((Keys)vkCode);
        }

        // The reader's firmware types one EPC per trigger pull into whatever
        // window has focus, and that can't be turned off. Relaying the same
        // EPC again means two writers filling the same control at once, and
        // their characters interleave — the observed
        // "004C79D2...66D5" + "004C79D2...66D58C7" + orphaned "8C7" mess.
        //
        // So decode what the reader itself is typing, straight off the raw
        // keystrokes from the learned device, and suppress exactly that EPC
        // when the relay later gets to it. Matching on the actual decoded
        // value rather than just "skip the first tag" matters because the
        // SDK poll doesn't necessarily report tags in the order the reader
        // decoded them, so "first" is not reliably the natively-typed one.
        private void RecordNativeKeystroke(Keys key)
        {
            if (key == Keys.Enter || key == Keys.Return)
            {
                string typed = nativeEpcBuffer;
                nativeEpcBuffer = "";
                if (typed.Length >= 8)
                {
                    lock (nativeEchoLock) nativeEchoedEpcs[typed] = DateTime.Now;
                }
                return;
            }

            char hex = MapToHex(key);
            if (hex == '\0') return;
            // Guard against a runaway buffer if an Enter is ever missed.
            if (nativeEpcBuffer.Length > 64) nativeEpcBuffer = "";
            nativeEpcBuffer += hex;
        }

        // Consumed once: a tag scanned again later is a genuinely new read
        // and should be relayed normally.
        private bool ConsumeNativeEcho(string epc)
        {
            lock (nativeEchoLock)
            {
                DateTime when;
                if (!nativeEchoedEpcs.TryGetValue(epc, out when)) return false;
                nativeEchoedEpcs.Remove(epc);
                return (DateTime.Now - when) <= NativeEchoWindow;
            }
        }

        // Pulls the stable hardware identity out of a Raw Input device path,
        // e.g. "\\?\HID#VID_2047&PID_0301&MI_01#7&71a295&0&0000#{...}"
        // becomes "VID_2047&PID_0301&MI_01". The trailing part is the USB
        // port instance, which changes between ports, so it's excluded.
        private static string DeviceSignature(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;

            // Bluetooth HID paths use a different shape entirely:
            //   \\?\HID#{0000...1812-...}_DEV_VID&021915_PID&EEEE_REV&0001_F737A3759BC2#9&78490B6&0&0000#{...}
            // note "VID&" (not "VID_") and a 6-digit VID, so the USB pattern
            // below never matches one. The 12-hex tail is the reader's
            // Bluetooth MAC — unique to the device and stable across
            // re-pairing, which makes it the better half of the identity.
            Match bt = Regex.Match(devicePath,
                @"VID&[0-9A-Fa-f]+_PID&[0-9A-Fa-f]+(?:_REV&[0-9A-Fa-f]+)?_[0-9A-Fa-f]{12}",
                RegexOptions.IgnoreCase);
            if (bt.Success) return bt.Value.ToUpperInvariant();

            // USB HID: \\?\HID#VID_2047&PID_0301&MI_01#7&71a295&0&0000#{...}
            Match usb = Regex.Match(devicePath,
                @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(?:&MI_[0-9A-Fa-f]{2})?",
                RegexOptions.IgnoreCase);
            if (usb.Success) return usb.Value.ToUpperInvariant();

            return devicePath.ToUpperInvariant();
        }

        private string GetDeviceSignature(IntPtr deviceHandle)
        {
            string sig;
            if (rawDeviceSigCache.TryGetValue(deviceHandle, out sig)) return sig;
            string path = null;
            try { path = RawInputKeyboard.GetDeviceName(deviceHandle); } catch { }
            sig = DeviceSignature(path);
            // Cached (including nulls) so the per-keystroke path stays cheap —
            // resolving a device name on every keydown of ordinary typing
            // would mean two syscalls per key.
            rawDeviceSigCache[deviceHandle] = sig;
            return sig;
        }

        private bool IsLearnedDevice(IntPtr deviceHandle)
        {
            if (string.IsNullOrEmpty(learnedDeviceSignature)) return false;
            if (deviceHandle == learnedDeviceHandle) return true;
            string sig = GetDeviceSignature(deviceHandle);
            if (sig != null && sig == learnedDeviceSignature)
            {
                // Remember the handle so later keystrokes take the cheap path.
                learnedDeviceHandle = deviceHandle;
                return true;
            }
            return false;
        }

        private void UpdateTriggerDeviceStatus()
        {
            if (isLearningTriggerDevice)
                lblTriggerDeviceStatus.Text = Lang.Get("triggerDeviceWaiting");
            else if (!string.IsNullOrEmpty(learnedDeviceSignature))
                lblTriggerDeviceStatus.Text = Lang.Get("triggerDeviceLearned", learnedDeviceSignature);
            else
                lblTriggerDeviceStatus.Text = Lang.Get("triggerDeviceNone");
        }

        private void SaveTriggerDevice()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsRegistryKey))
                {
                    if (key == null) return;
                    if (string.IsNullOrEmpty(learnedDeviceSignature))
                        key.DeleteValue(TriggerDeviceValueName, false);
                    else
                        key.SetValue(TriggerDeviceValueName, learnedDeviceSignature);
                }
            }
            catch { }
        }

        private void LoadTriggerDevice()
        {
            bool needsResave = false;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false))
                {
                    if (key == null) return;
                    // Run through DeviceSignature rather than using the stored
                    // string directly: an earlier build saved whole device
                    // paths (the Bluetooth form wasn't parsed), and those must
                    // be reduced to the same signature the live device now
                    // produces or the saved scanner silently stops matching.
                    // Normalising is idempotent, so already-clean values pass
                    // through untouched.
                    string stored = key.GetValue(TriggerDeviceValueName) as string;
                    learnedDeviceSignature = DeviceSignature(stored);
                    if (!string.IsNullOrEmpty(learnedDeviceSignature) && learnedDeviceSignature != stored)
                        needsResave = true;
                }
            }
            catch { }

            // Write the normalised form back (outside the read-only handle
            // above) so the stored value matches what matching actually uses.
            if (needsResave) SaveTriggerDevice();
        }

        private void BtnLearnTrigger_Click(object sender, EventArgs e)
        {
            isLearningTriggerDevice = true;
            btnLearnTrigger.Text = Lang.Get("btnLearnTriggerWaiting");
            UpdateTriggerDeviceStatus();
        }

        // Rebuilds the picker from currently open top-level windows. Called
        // at startup, on demand (Refresh button), and every time the
        // dropdown opens so it doesn't go stale.
        private void RefreshExternalTargetList()
        {
            IntPtr previous = externalTargetWindow;
            suppressTargetSync = true;
            cmbExternalTarget.Items.Clear();
            cmbExternalTarget.Items.Add(new WindowEntry(IntPtr.Zero, Lang.Get("externalTargetNone"), null));
            List<WindowEntry> windows = WindowsKeyboard.EnumerateTopLevelWindows(this.Handle);
            int selectIndex = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                cmbExternalTarget.Items.Add(windows[i]);
                if (windows[i].Handle == previous) selectIndex = i + 1;
            }
            cmbExternalTarget.SelectedIndex = selectIndex;
            suppressTargetSync = false;
            UpdateExternalTargetStatus();
        }

        private void CmbExternalTarget_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Rebuilding the list also fires this; only a real user choice
            // should overwrite the remembered target, otherwise a refresh
            // taken while the target app happens to be closed would wipe it.
            if (suppressTargetSync) return;
            if (cmbExternalTarget.SelectedIndex < 0)
            {
                externalTargetWindow = IntPtr.Zero;
                return;
            }
            WindowEntry entry = (WindowEntry)cmbExternalTarget.Items[cmbExternalTarget.SelectedIndex];
            externalTargetWindow = entry.Handle;
            savedTargetProcess = entry.Handle == IntPtr.Zero ? null : entry.ProcessName;
            savedTargetTitle = entry.Handle == IntPtr.Zero ? null : entry.Title;
            SaveExternalTarget();
            UpdateExternalTargetStatus();
        }

        // Re-attaches the remembered target. Handles are only valid for the
        // session that created them, so the process name plus title are what
        // actually get stored; this turns them back into a live handle.
        // Preference order matters: an exact title match within the right
        // process is the strongest signal, then any window of that process
        // (covers Excel retitling itself per workbook), then a title match
        // alone (covers the app being restarted under a different process id).
        private bool ResolveExternalTarget()
        {
            if (string.IsNullOrEmpty(savedTargetProcess) && string.IsNullOrEmpty(savedTargetTitle))
                return false;

            List<WindowEntry> windows = WindowsKeyboard.EnumerateTopLevelWindows(this.Handle);
            WindowEntry byProcess = null;
            WindowEntry byTitle = null;

            foreach (WindowEntry w in windows)
            {
                bool sameProcess = !string.IsNullOrEmpty(savedTargetProcess)
                    && string.Equals(w.ProcessName, savedTargetProcess, StringComparison.OrdinalIgnoreCase);
                bool sameTitle = !string.IsNullOrEmpty(savedTargetTitle)
                    && string.Equals(w.Title, savedTargetTitle, StringComparison.Ordinal);

                if (sameProcess && sameTitle) { externalTargetWindow = w.Handle; return true; }
                if (sameProcess && byProcess == null) byProcess = w;
                if (sameTitle && byTitle == null) byTitle = w;
            }

            WindowEntry pick = byProcess ?? byTitle;
            if (pick == null) return false;
            externalTargetWindow = pick.Handle;
            return true;
        }

        // The remembered app may not be running yet at startup, and can be
        // closed and reopened later, so this is re-checked on a timer rather
        // than resolved once.
        private void EnsureExternalTargetResolved()
        {
            if (string.IsNullOrEmpty(savedTargetProcess) && string.IsNullOrEmpty(savedTargetTitle)) return;
            if (WindowsKeyboard.IsValidWindow(externalTargetWindow)) return;
            externalTargetWindow = IntPtr.Zero;
            if (ResolveExternalTarget()) RefreshExternalTargetList();
            else UpdateExternalTargetStatus();
        }

        private void UpdateExternalTargetStatus()
        {
            if (lblExternalTargetStatus == null) return;
            if (string.IsNullOrEmpty(savedTargetProcess) && string.IsNullOrEmpty(savedTargetTitle))
            {
                lblExternalTargetStatus.Text = "";
                return;
            }
            string name = string.IsNullOrEmpty(savedTargetTitle) ? savedTargetProcess : savedTargetTitle;
            bool live = WindowsKeyboard.IsValidWindow(externalTargetWindow);
            lblExternalTargetStatus.Text = Lang.Get(live ? "targetRemembered" : "targetRememberedMissing", name);
            lblExternalTargetStatus.ForeColor = live ? Color.Green : Color.DarkOrange;
        }

        private void SaveExternalTarget()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsRegistryKey))
                {
                    if (key == null) return;
                    if (string.IsNullOrEmpty(savedTargetProcess) && string.IsNullOrEmpty(savedTargetTitle))
                    {
                        key.DeleteValue(TargetProcessValueName, false);
                        key.DeleteValue(TargetTitleValueName, false);
                    }
                    else
                    {
                        key.SetValue(TargetProcessValueName, savedTargetProcess ?? "");
                        key.SetValue(TargetTitleValueName, savedTargetTitle ?? "");
                    }
                }
            }
            catch { }
        }

        private void LoadExternalTarget()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false))
                {
                    if (key == null) return;
                    savedTargetProcess = key.GetValue(TargetProcessValueName) as string;
                    savedTargetTitle = key.GetValue(TargetTitleValueName) as string;
                }
            }
            catch { }
        }


        // Fires on every keydown system-wide. Always calls CallNextHookEx —
        // never blocks — so the reader's native typed output keeps reaching
        // whatever window is actually focused, unchanged. While this app IS
        // the foreground window, PreFilterMessage already handles the same
        // keystroke, so skip here to avoid committing the same tag twice.
        // Disconnected/Bluetooth-only mode only now — see
        // ProcessBackgroundTriggerKey.
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN)
                && GetForegroundWindow() != this.Handle)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                ProcessBackgroundTriggerKey((Keys)vkCode);
            }
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
        }

        // Disconnected/Bluetooth-only manual-entry path: mirrors the hex-
        // digit + Enter handling in PreFilterMessage into bgEpcBuffer instead
        // of txtEpcInput, since there's no visible textbox to type into
        // while some other window has focus. Connected mode no longer uses
        // this at all — see learnedDeviceHandle / OnRawKeyboardInput, which
        // handles trigger detection for that case via Raw Input instead,
        // regardless of focus.
        private void ProcessBackgroundTriggerKey(Keys key)
        {
            if (isConnected) return;

            if (key == Keys.Enter || key == Keys.Return)
            {
                string epc = bgEpcBuffer.Trim();
                bgEpcBuffer = "";
                if (epc.Length > 0)
                {
                    AddEpcToList(epc);
                }
                return;
            }

            char hex = MapToHex(key);
            if (hex == '\0') return;

            if (bgEpcBuffer.Length == 0) HandleTriggerPull(DateTime.Now - lastTriggerKeystrokeTime);
            lastTriggerKeystrokeTime = DateTime.Now;
                lastTriggerTickMs = Environment.TickCount;
            bgEpcBuffer += hex;
        }

        // Delivers the SDK-confirmed EPC into the window the user explicitly
        // picked via cmbExternalTarget — never "whatever's currently focused"
        // (see the externalTargetWindow field comment for why). Runs on a
        // background thread since PostMessage + the settle delay shouldn't
        // block the UI thread the keyboard hook and Raw Input share.
        // Queued and delivered by a single worker (below) — when Auto/Hold
        // mode finds several tags in the same poll, each used to spawn its
        // own ThreadPool send with no coordination between them, so their
        // PostMessage character streams could interleave in the target
        // window's own message queue (observed as tag EPCs mashed together
        // or missing/duplicated digits). Only one send is ever in flight now.
        private readonly object externalSendLock = new object();
        private readonly Queue<string> externalSendQueue = new Queue<string>();
        private bool externalSendWorkerRunning;

        private void SendEpcToExternalTarget(string epc)
        {
            if (externalTargetWindow == IntPtr.Zero) return;
            lock (externalSendLock)
            {
                externalSendQueue.Enqueue(epc);
                if (externalSendWorkerRunning) return;
                externalSendWorkerRunning = true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(ExternalSendWorker);
        }

        private void ExternalSendWorker(object state)
        {
            while (true)
            {
                string epc;
                lock (externalSendLock)
                {
                    if (externalSendQueue.Count == 0)
                    {
                        externalSendWorkerRunning = false;
                        return;
                    }
                    epc = externalSendQueue.Dequeue();
                }

                IntPtr window = externalTargetWindow;
                string result;
                if (window == IntPtr.Zero)
                {
                    result = "skipped (no target)";
                }
                else
                {
                    // The reader's own firmware types its native keystroke
                    // burst into whatever's focused regardless — confirmed
                    // clean and un-suppressible on its own (see the isolation
                    // test in project memory). Sending ours while that burst
                    // is still landing interleaves the two independent
                    // character streams in the target's message queue,
                    // producing exactly the mashed/truncated text reported.
                    // Waiting for a quiet period since the last raw keydown
                    // from the learned device lets the native burst finish
                    // first.
                    // Must LOOP, not sleep once: keystrokes arriving during
                    // the wait have to extend it. A single-shot sleep could
                    // wake mid-burst — before the reader's Enter, which is
                    // when the echo below gets recorded — so there'd be
                    // nothing to match yet and the duplicate went out anyway.
                    // Environment.TickCount is used rather than the DateTime
                    // field because a 32-bit int reads atomically across
                    // threads; its wraparound is handled by the subtraction.
                    int quietMs = (int)NativeBurstQuietPeriod.TotalMilliseconds;
                    int waitStarted = Environment.TickCount;
                    while (true)
                    {
                        int idle = Environment.TickCount - lastTriggerTickMs;
                        if (idle >= quietMs) break;
                        // Safety cap so a reader stuck mid-burst can't stall
                        // the queue indefinitely.
                        if (Environment.TickCount - waitStarted > 5000) break;
                        System.Threading.Thread.Sleep(quietMs - idle);
                    }

                    // Checked here, not at enqueue time: the SDK frequently
                    // reports a tag before the reader has finished typing it,
                    // so the echo isn't recorded yet when the tag is queued.
                    // After the quiet wait above, the burst is complete.
                    if (ConsumeNativeEcho(epc)) continue;

                    try
                    {
                        WindowsKeyboard.SendTextLine(window, epc);
                        result = "sent";
                    }
                    catch (Exception ex)
                    {
                        result = "FAILED: " + ex.GetType().Name + ": " + ex.Message;
                    }
                }

            }
        }

        // ─── IMessageFilter: бүх keyboard message-ийг хамгийн доод түвшинд барих ───
        public bool PreFilterMessage(ref Message m)
        {

            // WM_CHAR(0x0102), WM_SYSCHAR(0x0106), WM_UNICHAR(0x0109), WM_IME_CHAR(0x0286)
            if (m.Msg == 0x0102 || m.Msg == 0x0106 || m.Msg == 0x0109 || m.Msg == 0x0286)
            {
                Control target = Control.FromHandle(m.HWnd);
                if (IsNumericChild(target)) return false;
                return m.WParam.ToInt32() >= 0x20;
            }

            // WM_IME_COMPOSITION(0x010F)
            if (m.Msg == 0x010F)
            {
                Control target = Control.FromHandle(m.HWnd);
                if (IsNumericChild(target)) return false;
                return true;
            }

            // WM_KEYDOWN (0x0100) л боловсруулах
            if (m.Msg != 0x0100) return false;

            // NumericUpDown-д бүх key нэвтрүүлэх
            {
                Control target = Control.FromHandle(m.HWnd);
                if (IsNumericChild(target)) return false;
            }

            Keys key = (Keys)m.WParam.ToInt32();

            // Enter: EPC боловсруулах + API scan эхлүүлэх
            if (key == Keys.Enter || key == Keys.Return)
            {
                if (isConnected)
                {
                    // The SDK poll (ReadTagsFromApi) is the sole source of
                    // truth for the actual EPC while connected — just swallow
                    // the keystroke so raw hex text doesn't pile up visibly.
                    txtEpcInput.Clear();
                    return true;
                }
                string epc = txtEpcInput.Text.Trim();
                if (epc.Length > 0)
                {
                    AddEpcToList(epc);
                    txtEpcInput.Clear();
                }
                return true;
            }

            // Системийн/навигацийн товчлуурууд: нэвтрүүлэх
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu ||
                (Control.ModifierKeys & Keys.Control) != 0 ||
                (Control.ModifierKeys & Keys.Alt) != 0 ||
                key == Keys.Tab || key == Keys.Escape ||
                key == Keys.Back || key == Keys.Delete ||
                key == Keys.Left || key == Keys.Right ||
                key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End ||
                key == Keys.Space ||
                key == Keys.CapsLock || key == Keys.NumLock ||
                (key >= Keys.F1 && key <= Keys.F12))
                return false;

            // Virtual key code → HEX тэмдэгт (keyboard layout-аас 100% хамааралгүй)
            char hex = MapToHex(key);
            if (hex != '\0')
            {
                if (isConnected)
                {
                    // Connected mode: Raw Input (OnRawKeyboardInput) owns
                    // trigger detection entirely now, regardless of focus —
                    // just swallow so raw hex text doesn't pile up visibly.
                    return true;
                }

                if (!keyboardActivityDetected)
                {
                    keyboardActivityDetected = true;
                    UpdateConnTypeDisplay();
                }
                string t = txtEpcInput.Text;
                if (t.Length == 0) HandleTriggerPull(DateTime.Now - lastTriggerKeystrokeTime);
                lastTriggerKeystrokeTime = DateTime.Now;
                lastTriggerTickMs = Environment.TickCount;

                int sel = txtEpcInput.SelectionStart;
                int slen = txtEpcInput.SelectionLength;
                txtEpcInput.Text = t.Substring(0, sel) + hex + t.Substring(sel + slen);
                txtEpcInput.SelectionStart = sel + 1;
            }

            return true;
        }

        private bool IsNumericChild(Control c)
        {
            while (c != null)
            {
                if (c == numPower) return true;
                c = c.Parent;
            }
            return false;
        }

        // Форм идэвхжихэд English layout руу шилжих
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (englishLayout != IntPtr.Zero)
            {
                originalLayout = GetKeyboardLayout(0);
                ActivateKeyboardLayout(englishLayout, 0);
            }
        }

        // Форм идэвхгүй болоход анхны layout руу буцах
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (originalLayout != IntPtr.Zero)
            {
                ActivateKeyboardLayout(originalLayout, 0);
                originalLayout = IntPtr.Zero;
            }
        }

        private void InitializeComponents()
        {
            Text = Lang.Get("windowTitle");
            // Title bar / Alt-Tab / taskbar all pick this up; without it the
            // window shows the generic WinForms icon even though the exe has
            // a proper one embedded.
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(500, 620);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9);

            var pnlHeader = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(500, 55),
                BackColor = Color.FromArgb(33, 37, 41)
            };
            var lblCompany = new Label
            {
                Text = "CHIPMO",
                Location = new Point(15, 5),
                AutoSize = true,
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.White
            };
            lblDevice = new Label
            {
                Text = Lang.Get("deviceDesc"),
                Location = new Point(15, 32),
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(173, 181, 189)
            };
            cmbLang = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(390, 15),
                Size = new Size(95, 25),
                Font = new Font("Segoe UI", 9)
            };
            cmbLang.Items.AddRange(Lang.Names);
            cmbLang.SelectedIndex = 0;
            cmbLang.SelectedIndexChanged += CmbLang_SelectedIndexChanged;
            pnlHeader.Controls.AddRange(new Control[] { lblCompany, lblDevice, cmbLang });

            // Everyday scanning up front; one-time configuration and
            // rarely-touched options behind their own tabs. Keeps the screen
            // people actually use all day short enough to need no scrolling.
            tabs = new TabControl
            {
                Location = new Point(8, 60),
                Size = new Size(484, 552)
            };
            tabScan = new TabPage(Lang.Get("tabScan"));
            tabSetup = new TabPage(Lang.Get("tabSetup"));
            tabAdvanced = new TabPage(Lang.Get("tabAdvanced"));
            tabs.TabPages.AddRange(new TabPage[] { tabScan, tabSetup, tabAdvanced });

            BuildScanTab();
            BuildSetupTab();
            BuildAdvancedTab();

            Controls.AddRange(new Control[] { pnlHeader, tabs });
        }

        private void BuildScanTab()
        {
            grpConn = new GroupBox
            {
                Text = Lang.Get("grpConnection"),
                Location = new Point(8, 8),
                Size = new Size(460, 76)
            };
            lblStatus = new Label
            {
                Text = Lang.Get("statusDisconnected"),
                ForeColor = Color.Red,
                Location = new Point(12, 20),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            btnConnect = new Button
            {
                Text = Lang.Get("btnConnect"),
                Location = new Point(12, 44),
                Size = new Size(100, 26)
            };
            btnConnect.Click += BtnConnect_Click;
            btnDisconnect = new Button
            {
                Text = Lang.Get("btnDisconnect"),
                Location = new Point(118, 44),
                Size = new Size(90, 26),
                Enabled = false
            };
            btnDisconnect.Click += BtnDisconnect_Click;
            lblConnType = new Label
            {
                Text = Lang.Get("connTypeNone"),
                Location = new Point(220, 50),
                AutoSize = true,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.DimGray
            };
            grpConn.Controls.AddRange(new Control[] { lblStatus, btnConnect, btnDisconnect, lblConnType });

            grpScanMode = new GroupBox
            {
                Text = Lang.Get("grpScanMode"),
                Location = new Point(8, 92),
                Size = new Size(460, 86)
            };
            lblScanModePick = new Label { Text = Lang.Get("lblScanModePick"), Location = new Point(12, 26), AutoSize = true };
            rbModeSingle = new RadioButton { Text = Lang.Get("modeSingle"), Location = new Point(0, 0), AutoSize = true };
            rbModeAuto = new RadioButton { Text = Lang.Get("modeAuto"), Location = new Point(90, 0), AutoSize = true, Checked = true };
            rbModeSingle.CheckedChanged += ScanModeChanged;
            rbModeAuto.CheckedChanged += ScanModeChanged;
            var pnlModeTop = new Panel { Location = new Point(100, 22), Size = new Size(240, 22) };
            pnlModeTop.Controls.AddRange(new Control[] { rbModeSingle, rbModeAuto });

            lblAutoBehavior = new Label { Text = Lang.Get("lblAutoBehavior"), Location = new Point(12, 54), AutoSize = true };
            rbAutoClick = new RadioButton { Text = Lang.Get("modeAutoClick"), Location = new Point(0, 0), AutoSize = true };
            rbAutoHold = new RadioButton { Text = Lang.Get("modeAutoHold"), Location = new Point(90, 0), AutoSize = true, Checked = true };
            var pnlModeSub = new Panel { Location = new Point(100, 50), Size = new Size(240, 22) };
            pnlModeSub.Controls.AddRange(new Control[] { rbAutoClick, rbAutoHold });

            grpScanMode.Controls.AddRange(new Control[] { lblScanModePick, pnlModeTop, lblAutoBehavior, pnlModeSub });

            btnScan = new Button
            {
                Text = Lang.Get("btnScan"),
                Location = new Point(8, 188),
                Size = new Size(180, 36),
                Enabled = false,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                BackColor = Color.FromArgb(33, 150, 243),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnScan.FlatAppearance.BorderSize = 0;
            btnScan.MouseDown += BtnScan_MouseDown;
            btnScan.MouseUp += BtnScan_MouseUp;
            btnScan.Click += BtnScan_Click;
            btnScan.MouseLeave += delegate {
                if (isScanning && GetScanMode() != ScanMode.AutoClickToggle) StopScanning();
            };

            scanTimer = new Timer();
            scanTimer.Interval = 200;
            scanTimer.Tick += ScanTimer_Tick;

            lblTagCount = new Label
            {
                Text = Lang.Get("tagCount", 0),
                Location = new Point(200, 198),
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold)
            };

            lblInput = new Label { Text = Lang.Get("lblEpcInput"), Location = new Point(8, 240), AutoSize = true };
            txtEpcInput = new TextBox
            {
                Location = new Point(80, 237),
                Size = new Size(276, 24),
                Font = new Font("Consolas", 10),
                CharacterCasing = CharacterCasing.Upper,
                ReadOnly = true
            };
            btnClearList = new Button
            {
                Text = Lang.Get("btnClear"),
                Location = new Point(366, 236),
                Size = new Size(102, 26)
            };
            btnClearList.Click += delegate { ClearTagList(); };

            lvTags = new ListView
            {
                Location = new Point(8, 270),
                Size = new Size(460, 244),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Consolas", 9)
            };
            lvTags.Columns.Add("#", 40);
            lvTags.Columns.Add(Lang.Get("colEpc"), 330);
            lvTags.Columns.Add(Lang.Get("colCount"), 60);

            tabScan.Controls.AddRange(new Control[] {
                grpConn, grpScanMode, btnScan, lblTagCount, lblInput, txtEpcInput, btnClearList, lvTags
            });
        }

        private void BuildSetupTab()
        {
            grpPower = new GroupBox
            {
                Text = Lang.Get("grpPower"),
                Location = new Point(8, 8),
                Size = new Size(460, 168)
            };
            lblCurrentPower = new Label
            {
                Text = Lang.Get("currentPower"),
                Location = new Point(12, 24),
                AutoSize = true,
                Font = new Font("Segoe UI", 10)
            };
            btnRefresh = new Button
            {
                Text = Lang.Get("btnRefresh"),
                Location = new Point(366, 20),
                Size = new Size(82, 26),
                Enabled = false
            };
            btnRefresh.Click += delegate { ReadCurrentPower(); };

            var lblMin = new Label { Text = "5", Location = new Point(12, 58), AutoSize = true, Font = new Font("Segoe UI", 8) };
            var lblMax = new Label { Text = "30", Location = new Point(432, 58), AutoSize = true, Font = new Font("Segoe UI", 8) };
            trackPower = new TrackBar
            {
                Minimum = 5,
                Maximum = 30,
                Value = 20,
                TickFrequency = 1,
                Location = new Point(24, 70),
                Size = new Size(408, 45),
                Enabled = false
            };
            trackPower.ValueChanged += TrackPower_ValueChanged;

            lblPowerValue = new Label
            {
                Text = "— dBm",
                Location = new Point(190, 112),
                AutoSize = true,
                Font = new Font("Segoe UI", 15, FontStyle.Bold),
                ForeColor = Color.DarkBlue
            };
            numPower = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 30,
                Value = 20,
                Location = new Point(12, 130),
                Size = new Size(60, 25),
                Enabled = false
            };
            numPower.ValueChanged += NumPower_ValueChanged;
            var lblDbm = new Label { Text = "dBm", Location = new Point(76, 133), AutoSize = true };

            chkBeep = new CheckBox
            {
                Text = Lang.Get("chkBeep"),
                Location = new Point(126, 133),
                AutoSize = true,
                Enabled = false
            };
            chkBeep.CheckedChanged += ChkBeep_CheckedChanged;

            btnSave = new Button
            {
                Text = Lang.Get("btnSave"),
                Location = new Point(336, 126),
                Size = new Size(112, 30),
                Enabled = false,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(76, 175, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += BtnSave_Click;

            grpPower.Controls.AddRange(new Control[] {
                lblCurrentPower, btnRefresh, lblMin, lblMax, trackPower,
                lblPowerValue, numPower, lblDbm, chkBeep, btnSave
            });

            grpTrigger = new GroupBox
            {
                Text = Lang.Get("grpTrigger"),
                Location = new Point(8, 184),
                Size = new Size(460, 138)
            };
            btnLearnTrigger = new Button
            {
                Text = Lang.Get("btnLearnTrigger"),
                Location = new Point(12, 24),
                Size = new Size(180, 26)
            };
            btnLearnTrigger.Click += BtnLearnTrigger_Click;
            lblTriggerDeviceStatus = new Label
            {
                Text = Lang.Get("triggerDeviceNone"),
                Location = new Point(12, 56),
                Size = new Size(436, 16),
                AutoEllipsis = true,
                // Device signatures contain '&' (VID_2047&PID_0301&MI_01);
                // without this WinForms eats them as mnemonic markers and
                // the identity renders wrong.
                UseMnemonic = false,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.DimGray
            };
            lblExternalTargetPick = new Label { Text = Lang.Get("lblExternalTargetPick"), Location = new Point(12, 84), AutoSize = true };
            cmbExternalTarget = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(114, 80),
                Size = new Size(246, 24)
            };
            cmbExternalTarget.SelectedIndexChanged += CmbExternalTarget_SelectedIndexChanged;
            cmbExternalTarget.DropDown += delegate { RefreshExternalTargetList(); };
            btnRefreshWindows = new Button
            {
                Text = Lang.Get("btnRefreshWindows"),
                Location = new Point(366, 79),
                Size = new Size(82, 26)
            };
            btnRefreshWindows.Click += delegate { RefreshExternalTargetList(); };

            lblExternalTargetStatus = new Label
            {
                Text = "",
                Location = new Point(12, 110),
                Size = new Size(436, 16),
                AutoEllipsis = true,
                UseMnemonic = false,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.DimGray
            };

            grpTrigger.Controls.AddRange(new Control[] {
                btnLearnTrigger, lblTriggerDeviceStatus, lblExternalTargetPick,
                cmbExternalTarget, btnRefreshWindows, lblExternalTargetStatus
            });

            tabSetup.Controls.AddRange(new Control[] { grpPower, grpTrigger });
        }

        private void BuildAdvancedTab()
        {
            grpAdvanced = new GroupBox
            {
                Text = Lang.Get("grpAdvanced"),
                Location = new Point(8, 8),
                Size = new Size(460, 216)
            };
            lblCooldown = new Label { Text = Lang.Get("lblCooldown"), Location = new Point(12, 30), AutoSize = true };
            numCooldown = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 5000,
                Increment = 100,
                Value = 1000,
                Location = new Point(360, 26),
                Size = new Size(80, 25)
            };
            numCooldown.ValueChanged += delegate
            {
                recountCooldown = TimeSpan.FromMilliseconds((double)numCooldown.Value);
            };
            lblAutoStop = new Label { Text = Lang.Get("lblAutoStop"), Location = new Point(12, 62), AutoSize = true };
            numAutoStop = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 60,
                Value = 5,
                Location = new Point(360, 58),
                Size = new Size(80, 25)
            };
            numAutoStop.ValueChanged += delegate
            {
                autoStopIdle = TimeSpan.FromSeconds((double)numAutoStop.Value);
            };

            // Indented: this qualifies the auto-stop above rather than
            // standing on its own. Off = only a genuinely new EPC counts as
            // progress (a sweep is finished once everything in range is
            // collected). On = any read counts, so the session stays alive
            // for as long as any tag is visible at all.
            chkRepeatKeepsAlive = new CheckBox
            {
                Text = Lang.Get("chkRepeatKeepsAlive"),
                Location = new Point(28, 90),
                AutoSize = true
            };
            chkRepeatKeepsAlive.CheckedChanged += delegate
            {
                repeatReadsKeepScanAlive = chkRepeatKeepsAlive.Checked;
            };

            chkClearOnScan = new CheckBox { Text = Lang.Get("chkClearOnScan"), Location = new Point(12, 122), AutoSize = true, Checked = true };
            chkMinimizeToTray = new CheckBox { Text = Lang.Get("chkMinimizeToTray"), Location = new Point(12, 150), AutoSize = true, Checked = true };
            chkMinimizeToTray.CheckedChanged += delegate
            {
                if (trayIcon != null) trayIcon.Visible = chkMinimizeToTray.Checked;
            };
            chkStartWithWindows = new CheckBox { Text = Lang.Get("chkStartWithWindows"), Location = new Point(12, 178), AutoSize = true };
            chkStartWithWindows.CheckedChanged += ChkStartWithWindows_CheckedChanged;

            grpAdvanced.Controls.AddRange(new Control[] {
                lblCooldown, numCooldown, lblAutoStop, numAutoStop, chkRepeatKeepsAlive,
                chkClearOnScan, chkMinimizeToTray, chkStartWithWindows
            });

            tabAdvanced.Controls.Add(grpAdvanced);
        }

        private void CmbLang_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = cmbLang.SelectedIndex;
            if (idx < 0 || idx >= Lang.Codes.Length) return;
            Lang.Current = Lang.Codes[idx];
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Text = Lang.Get("windowTitle");
            lblDevice.Text = Lang.Get("deviceDesc");
            tabScan.Text = Lang.Get("tabScan");
            tabSetup.Text = Lang.Get("tabSetup");
            tabAdvanced.Text = Lang.Get("tabAdvanced");
            grpAdvanced.Text = Lang.Get("grpAdvanced");
            grpConn.Text = Lang.Get("grpConnection");
            btnConnect.Text = Lang.Get("btnConnect");
            btnDisconnect.Text = Lang.Get("btnDisconnect");
            chkBeep.Text = Lang.Get("chkBeep");
            UpdateConnTypeDisplay();
            btnRefresh.Text = Lang.Get("btnRefresh");
            grpPower.Text = Lang.Get("grpPower");
            btnSave.Text = Lang.Get("btnSave");
            grpScanMode.Text = Lang.Get("grpScanMode");
            lblScanModePick.Text = Lang.Get("lblScanModePick");
            lblAutoBehavior.Text = Lang.Get("lblAutoBehavior");
            lblCooldown.Text = Lang.Get("lblCooldown");
            lblAutoStop.Text = Lang.Get("lblAutoStop");
            chkRepeatKeepsAlive.Text = Lang.Get("chkRepeatKeepsAlive");
            chkClearOnScan.Text = Lang.Get("chkClearOnScan");
            chkMinimizeToTray.Text = Lang.Get("chkMinimizeToTray");
            chkStartWithWindows.Text = Lang.Get("chkStartWithWindows");
            rbModeSingle.Text = Lang.Get("modeSingle");
            rbModeAuto.Text = Lang.Get("modeAuto");
            rbAutoClick.Text = Lang.Get("modeAutoClick");
            rbAutoHold.Text = Lang.Get("modeAutoHold");
            grpTrigger.Text = Lang.Get("grpTrigger");
            if (!isLearningTriggerDevice) btnLearnTrigger.Text = Lang.Get("btnLearnTrigger");
            UpdateTriggerDeviceStatus();
            lblExternalTargetPick.Text = Lang.Get("lblExternalTargetPick");
            btnRefreshWindows.Text = Lang.Get("btnRefreshWindows");
            UpdateExternalTargetStatus();
            RefreshExternalTargetList();
            lblInput.Text = Lang.Get("lblEpcInput");
            btnClearList.Text = Lang.Get("btnClear");
            if (!isScanning) btnScan.Text = Lang.Get("btnScan");

            if (isConnected)
            {
                lblStatus.Text = Lang.Get("statusConnected");
            }
            else
            {
                lblStatus.Text = Lang.Get("statusDisconnected");
                lblCurrentPower.Text = Lang.Get("currentPower");
            }

            lblTagCount.Text = Lang.Get("tagCount", tagCounts.Count);
            lvTags.Columns[1].Text = Lang.Get("colEpc");
            lvTags.Columns[2].Text = Lang.Get("colCount");
        }

        private static char MapToHex(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return (char)('0' + (key - Keys.D0));
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return (char)('0' + (key - Keys.NumPad0));
            if (key >= Keys.A && key <= Keys.F) return (char)('A' + (key - Keys.A));
            return '\0';
        }

        private bool AddEpcToList(string epc)
        {
            bool isNew = !tagCounts.ContainsKey(epc);
            DateTime now = DateTime.Now;
            if (!isNew)
            {
                // A close/strong tag gets re-reported dozens of times per second
                // during continuous inventory, swamping the count column while
                // weaker tags barely register. Cap increments to once per cooldown.
                if (tagLastSeen.ContainsKey(epc) && (now - tagLastSeen[epc]) < recountCooldown)
                    return false;

                tagLastSeen[epc] = now;
                tagCounts[epc]++;
                for (int i = 0; i < lvTags.Items.Count; i++)
                {
                    if (lvTags.Items[i].SubItems[1].Text == epc)
                    {
                        lvTags.Items[i].SubItems[2].Text = tagCounts[epc].ToString();
                        break;
                    }
                }
            }
            else
            {
                tagLastSeen[epc] = now;
                tagCounts[epc] = 1;
                ListViewItem item = new ListViewItem((lvTags.Items.Count + 1).ToString());
                item.SubItems.Add(epc);
                item.SubItems.Add("1");
                lvTags.Items.Add(item);
                lvTags.EnsureVisible(lvTags.Items.Count - 1);
            }

            lblTagCount.Text = Lang.Get("tagCount", tagCounts.Count);
            return isNew;
        }

        private void SetConnectedState(bool connected)
        {
            isConnected = connected;
            lblStatus.Text = connected ? Lang.Get("statusConnected") : Lang.Get("statusDisconnected");
            lblStatus.ForeColor = connected ? Color.Green : Color.Red;
            if (connected) keyboardActivityDetected = false;
            UpdateConnTypeDisplay();
            btnConnect.Enabled = !connected;
            btnDisconnect.Enabled = connected;
            btnRefresh.Enabled = connected;
            trackPower.Enabled = connected;
            numPower.Enabled = connected;
            btnSave.Enabled = connected;
            btnScan.Enabled = connected;
            chkBeep.Enabled = connected;

            if (connected)
            {
                scanTimer.Start();
                // English layout идэвхжүүлэх
                if (englishLayout != IntPtr.Zero)
                {
                    originalLayout = GetKeyboardLayout(0);
                    ActivateKeyboardLayout(englishLayout, 0);
                }
            }
            else
            {
                scanTimer.Stop();
                // Анхны keyboard layout буцаах
                if (originalLayout != IntPtr.Zero)
                {
                    ActivateKeyboardLayout(originalLayout, 0);
                    originalLayout = IntPtr.Zero;
                }
            }

            if (!connected)
            {
                lblCurrentPower.Text = Lang.Get("currentPower");
                lblPowerValue.Text = "— dBm";
            }
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            // An explicit Connect clears a previous manual Disconnect, so
            // auto-reconnect resumes if the reader later drops out.
            autoConnectSuppressed = false;
            TryConnect(false);
        }

        // silent: an automatic attempt, so a missing/busy reader must stay
        // quiet — popping an error dialog every few seconds (or at every
        // Windows login, with "Start with Windows" on) would be unusable.
        private bool TryConnect(bool silent)
        {
            int ret;
            try
            {
                ret = UHFAPI.UsbOpen();
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show(Lang.Get("errGeneric", ex.Message), Lang.Get("errTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (ret == 0)
            {
                bgEpcBuffer = "";
                SetConnectedState(true);
                ReadCurrentPower();
                ReadBeepState();
                ReadScanModeState();
                return true;
            }

            if (!silent)
                MessageBox.Show(Lang.Get("errConnectMsg", ret), Lang.Get("errTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Retried rather than attempted once: with "Start with Windows" the
        // app can easily be up before USB enumeration finishes, and a reader
        // plugged in later should just start working without the user having
        // to click anything.
        private void TryAutoConnect()
        {
            if (isConnected || autoConnectSuppressed) return;
            if (TryConnect(true)) return;
            lblStatus.Text = Lang.Get("statusSearching");
            lblStatus.ForeColor = Color.DarkOrange;
        }

        private void BtnDisconnect_Click(object sender, EventArgs e)
        {
            // Without this the retry timer would reconnect within seconds,
            // making the Disconnect button look broken.
            autoConnectSuppressed = true;
            if (isScanning) StopScanning();
            try { UHFAPI.UsbClose(); } catch { }
            SetConnectedState(false);
        }

        private void ReadCurrentPower()
        {
            byte power = 0;
            int ret = UHFAPI.UHFGetPower(ref power);
            if (ret == 0)
            {
                int val = Math.Max(5, Math.Min(30, (int)power));
                lblCurrentPower.Text = Lang.Get("currentPowerVal", power);

                suppressSync = true;
                trackPower.Value = val;
                numPower.Value = val;
                suppressSync = false;

                lblPowerValue.Text = power + " dBm";
            }
            else
            {
                lblCurrentPower.Text = Lang.Get("currentPowerFail", ret);
            }
        }

        private void ReadBeepState()
        {
            byte val = 1;
            int ret = UHFAPI.UHFGetBeep(ref val);
            suppressBeepSync = true;
            chkBeep.Checked = (ret != 0) || (val != 0);
            suppressBeepSync = false;
        }

        private void ChkBeep_CheckedChanged(object sender, EventArgs e)
        {
            if (suppressBeepSync || !isConnected) return;
            byte val = (byte)(chkBeep.Checked ? 1 : 0);
            // Swapped from (0, val): every other Set* in this SDK is (save, value),
            // but that ordering only ever turns the beep off, never back on — the
            // "value" appears to actually be read from the first slot for this call.
            try { UHFAPI.UHFSetBeep(val, 0); } catch { }

        }

        // Reports which channel is actually delivering data, auto-detected
        // rather than user-toggled: USB takes priority when connected; a
        // keystroke arriving while not USB-connected can only mean a reader's
        // trigger is reaching us over Bluetooth (keyboard-wedge), so flag that
        // the first time it happens and keep showing it for the rest of the run.
        private void UpdateConnTypeDisplay()
        {
            if (isConnected)
                lblConnType.Text = Lang.Get("connTypeUsb");
            else if (keyboardActivityDetected)
                lblConnType.Text = Lang.Get("connTypeKeyboard");
            else
                lblConnType.Text = Lang.Get("connTypeNone");
        }

        private void TrackPower_ValueChanged(object sender, EventArgs e)
        {
            if (suppressSync) return;
            suppressSync = true;
            numPower.Value = trackPower.Value;
            suppressSync = false;
            lblPowerValue.Text = trackPower.Value + " dBm";
        }

        private void NumPower_ValueChanged(object sender, EventArgs e)
        {
            if (suppressSync) return;
            suppressSync = true;
            trackPower.Value = (int)numPower.Value;
            suppressSync = false;
            lblPowerValue.Text = numPower.Value + " dBm";
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            byte power = (byte)numPower.Value;
            int ret = UHFAPI.UHFSetPower(1, power);
            if (ret == 0)
            {
                ReadCurrentPower();
                MessageBox.Show(
                    Lang.Get("successSaveMsg", power),
                    Lang.Get("successTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    Lang.Get("errPowerMsg", ret),
                    Lang.Get("errTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private enum ScanMode { Single, AutoClickToggle, AutoHoldMulti }

        private ScanMode GetScanMode()
        {
            if (rbModeSingle.Checked) return ScanMode.Single;
            if (rbAutoClick.Checked) return ScanMode.AutoClickToggle;
            return ScanMode.AutoHoldMulti;
        }

        private bool suppressScanModeSync;

        private void ScanModeChanged(object sender, EventArgs e)
        {
            bool auto = rbModeAuto.Checked;
            rbAutoClick.Enabled = auto;
            rbAutoHold.Enabled = auto;
            if (isScanning) StopScanning();
            SyncDualSingleModeToHardware();
        }

        // UHFSetDualSingelMode/UHFGetDualSingelMode is this reader's own
        // on-board Single/Dual inventory-session register — the same session
        // our SDK scan loop (UHFInventory + UHF_GetReceived_EX) drives. This
        // is distinct from WorkMode, which only governs the physical
        // trigger's standalone keystroke path and was already a dead end for
        // that (see HandleTriggerPull). save=1 so the choice survives a
        // power cycle even without this app running. Auto Click vs Hold and
        // the recount cooldown have no hardware equivalent — those stay
        // software-only.
        private void SyncDualSingleModeToHardware()
        {
            if (suppressScanModeSync || !isConnected) return;
            byte mode = (byte)(rbModeSingle.Checked ? 1 : 0);
            try { UHFAPI.UHFSetDualSingelMode(1, mode); } catch { }
        }

        private void ReadScanModeState()
        {
            byte mode = 0;
            int ret = -1;
            try { ret = UHFAPI.UHFGetDualSingelMode(ref mode); } catch { }
            if (ret != 0) return;
            suppressScanModeSync = true;
            if (mode == 1) rbModeSingle.Checked = true;
            else rbModeAuto.Checked = true;
            suppressScanModeSync = false;
        }


        private void BtnScan_MouseDown(object sender, MouseEventArgs e)
        {
            if (!isConnected || isScanning) return;
            if (GetScanMode() == ScanMode.AutoClickToggle) return;
            StartScanning();
        }

        private void BtnScan_MouseUp(object sender, MouseEventArgs e)
        {
            if (GetScanMode() == ScanMode.AutoClickToggle) return;
            if (isScanning) StopScanning();
        }

        private void BtnScan_Click(object sender, EventArgs e)
        {
            if (!isConnected) return;
            if (GetScanMode() != ScanMode.AutoClickToggle) return;
            if (isScanning) StopScanning();
            else StartScanning();
        }

        // The physical trigger has no software-controllable single/continuous
        // mode of its own (confirmed dead end via WorkMode/DualSingelMode) — it
        // always just types exactly one EPC per pull via keystrokes, on its own,
        // regardless of anything we do. So:
        //  - Single: the natural keystroke path already IS single-shot capture.
        //    Don't also start the API polling loop — that was double-capturing
        //    the same physical read (once via keystroke, once via SDK poll).
        //  - Auto/Toggle: first pull starts the background poll loop, next
        //    genuinely-new pull stops it. "Genuinely new" means there was a
        //    real gap beforehand — otherwise this is just another tag from the
        //    SAME squeeze (a single pull can read several tags back-to-back),
        //    not the user pressing again to toggle it off.
        //  - Auto/Hold: keystroke messages have no "key released" signal, so
        //    true press-and-hold can't be detected. Approximate it: each pull
        //    refreshes an idle deadline (see ScanTimer_Tick); the loop keeps
        //    running as long as pulls keep coming, and auto-stops once they do.
        private void HandleTriggerPull(TimeSpan gapSinceLastKeystroke)
        {
            // Only ever called while connected — Raw Input (OnRawKeyboardInput)
            // and the disconnected keystroke paths both gate on isConnected
            // before reaching here.
            if (!isConnected) return;

            ScanMode mode = GetScanMode();

            if (mode == ScanMode.Single)
            {
                // Content always comes from the SDK poll now, never from
                // decoding the reader's keystrokes (see PreFilterMessage) —
                // so a trigger pull has to explicitly kick off one read.
                // ScanTimer_Tick already stops itself after the first new
                // tag whenever GetScanMode() is Single.
                if (!isScanning) StartScanning();
                return;
            }

            if (mode == ScanMode.AutoHoldMulti)
            {
                if (!isScanning)
                {
                    StartScanning();
                }
                return;
            }

            if (gapSinceLastKeystroke < TriggerBurstGap) return;

            // One physical squeeze does not reliably produce one tight burst:
            // the reader can pause mid-read while hunting for a stubborn tag
            // (the same behaviour the idle auto-stop allows seconds for).
            // Any pause longer than TriggerBurstGap makes the remainder
            // of that same squeeze look like a fresh pull, so the toggle
            // flipped on and straight back off and the user had to pull
            // again. Ignoring flips that land too close together collapses a
            // ragged single squeeze into one state change. The Chainway
            // reference build guards this the same way.
            if ((DateTime.Now - lastToggleTime) < TriggerToggleDebounce) return;
            lastToggleTime = DateTime.Now;

            if (isScanning) StopScanning();
            else StartScanning();
        }

        private void ClearTagList()
        {
            lvTags.Items.Clear();
            tagCounts.Clear();
            tagLastSeen.Clear();
            lblTagCount.Text = Lang.Get("tagCount", 0);
        }

        private void StartScanning()
        {
            // Opt-in: makes every scan session start with an empty list, so a
            // tag scanned in an earlier session counts as new again and gets
            // relayed to the external window a second time. See chkClearOnScan.
            if (chkClearOnScan.Checked) ClearTagList();

            isScanning = true;
            lastScanActivity = DateTime.Now;
            btnScan.Text = Lang.Get("scanning");
            btnScan.BackColor = Color.FromArgb(244, 67, 54);
            scanTicksSinceInventory = 0;
            // Keyboard-only mode (no real USB/SDK session opened via UsbOpen())
            // must never touch the native DLL — an uninitialized session isn't
            // just "returns an error", it's genuinely unverified territory, and
            // we've already seen one wrong native call crash the app outright.
            if (isConnected)
            {
                try { UHFAPI.UHFInventory(); } catch { }
            }
            scanTimer.Start();
        }

        private void StopScanning()
        {
            if (isConnected)
            {
                try { UHFAPI.UHFStopGet(); } catch { }
                // Single mode already captured (at most) its one tag — don't drain
                // leftovers.
                if (GetScanMode() != ScanMode.Single)
                    ReadTagsFromApi(false);
            }
            isScanning = false;
            btnScan.Text = Lang.Get("btnScan");
            btnScan.BackColor = Color.FromArgb(33, 150, 243);
        }

        private void ScanTimer_Tick(object sender, EventArgs e)
        {
            if (!isScanning) return;

            // Ends a scan without needing the trigger. The trigger can only
            // be detected via the keystrokes the reader emits, and it only
            // emits those when it actually decodes a tag — so pointing at
            // empty air and squeezing is invisible to us, leaving no way to
            // stop. Instead the session ends once it stops finding anything
            // NEW (see ReadTagsFromApi): a sweep is done when every tag in
            // range has been collected, so re-reads of known tags must not
            // hold it open, or pointing at a full rack would never stop.
            if (GetScanMode() != ScanMode.Single && autoStopIdle > TimeSpan.Zero
                && (DateTime.Now - lastScanActivity) > autoStopIdle)
            {
                StopScanning();
                return;
            }

            // Keyboard-only mode: no native session to poll — capture happens
            // entirely through the natural keystroke path (Enter handler).
            if (!isConnected) return;

            bool single = GetScanMode() == ScanMode.Single;
            bool gotNewTag = ReadTagsFromApi(single);

            if (single && gotNewTag)
            {
                StopScanning();
                return;
            }

            // Re-arm every ~800ms (4 ticks) instead of every tick — calling
            // UHFInventory() again before the previous round finished was
            // restarting it perpetually, so GetReceived_EX never had anything
            // to return even though the reader kept beeping.
            scanTicksSinceInventory++;
            if (scanTicksSinceInventory >= 4)
            {
                scanTicksSinceInventory = 0;
                try { UHFAPI.UHFInventory(); } catch { }
            }
        }

        private bool ReadTagsFromApi(bool stopOnNewTag)
        {
            int r, l, j;
            string s;
            return ReadTagsFromApi(stopOnNewTag, true, out r, out l, out j, out s);
        }

        // stopOnNewTag: keep draining through re-reads of already-known tags
        // (still bumping their counts) and only stop once a genuinely new EPC
        // shows up — used by Single mode to skip past tags already captured.
        // commitToList: false makes this purely observational (Listen mode) —
        // decodes and counts frames without ever touching the tag list.
        // lastRet/lastLen/rejectedCount/lastRejectedInfo surface raw API results for diagnosis.
        private bool ReadTagsFromApi(bool stopOnNewTag, bool commitToList, out int lastRet, out int lastLen, out int rejectedCount, out string lastRejectedInfo)
        {
            bool gotNew = false;
            lastRet = -1;
            lastLen = -1;
            rejectedCount = 0;
            lastRejectedInfo = "";
            while (true)
            {
                int len = 0;
                byte[] uii = new byte[256];
                int ret = UHFAPI.UHF_GetReceived_EX(ref len, uii);
                lastRet = ret;
                lastLen = len;
                if (ret != 0 || len <= 0) break;

                // Frame layout confirmed by observed bytes: [0]=marker,
                // [1..2]=PC field (big-endian; top 5 bits = EPC word count),
                // [3..]=EPC itself, remaining trailer = RSSI/antenna/CRC.
                // The EPC length lives in the PC field, not in `len`.
                bool ok = false;
                int epcBytes = 0;
                if (len >= 5 && uii.Length >= 3)
                {
                    int pc = (uii[1] << 8) | uii[2];
                    int epcWords = (pc >> 11) & 0x1F;
                    epcBytes = epcWords * 2;
                    ok = epcBytes >= 4 && epcBytes <= 32
                        && (3 + epcBytes) <= uii.Length && (3 + epcBytes) <= len;
                }

                if (ok)
                {
                    string epc = BitConverter.ToString(uii, 3, epcBytes).Replace("-", "");
                    if (commitToList)
                    {
                        // While connected, keystrokes are swallowed (PreFilterMessage)
                        // and never decoded into this box anymore — the SDK read is
                        // now the only source of EPC content, so mirror it here too,
                        // otherwise the EPC input box just looks broken/blank even
                        // though the tag list below it is updating correctly.
                        txtEpcInput.Text = epc;
                        bool isNew = AddEpcToList(epc);
                        if (isNew || repeatReadsKeepScanAlive) lastScanActivity = DateTime.Now;
                        if (isNew)
                        {
                            gotNew = true;
                            SendEpcToExternalTarget(epc);
                            if (stopOnNewTag) break;
                        }
                    }
                    else
                    {
                        gotNew = true;
                    }
                }
                else
                {
                    rejectedCount++;
                    int dumpLen = Math.Min(8, uii.Length);
                    lastRejectedInfo = string.Format("rawlen={0} pcBytes={1} bytes={2}",
                        len, epcBytes, BitConverter.ToString(uii, 0, dumpLen).Replace("-", ""));
                }
            }
            return gotNew;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // With the option on, clicking X hides to the tray instead of
            // quitting — the USB session, Raw Input trigger capture and the
            // external relay all keep running. Only the tray menu's Exit
            // (or a shutdown/task-manager close) really tears down.
            if (!isReallyExiting && chkMinimizeToTray != null && chkMinimizeToTray.Checked
                && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            if (trayIcon != null) trayIcon.Visible = false;
            if (keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHookHandle);
                keyboardHookHandle = IntPtr.Zero;
            }
            Application.RemoveMessageFilter(this);
            if (isScanning) StopScanning();
            if (isConnected)
            {
                try { UHFAPI.UsbClose(); } catch { }
            }
            // Анхны keyboard layout буцаах
            if (originalLayout != IntPtr.Zero)
            {
                ActivateKeyboardLayout(originalLayout, 0);
            }
            base.OnFormClosing(e);
        }
    }
}
