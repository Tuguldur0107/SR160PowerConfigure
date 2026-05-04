using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace SR160PowerConfig
{
    public partial class MainWindow : Window
    {
        private const int SingleReadDelayMs = 500;
        private const int UsbInitialReadDelayMs = 40;
        private const int UsbPollIntervalMs = 30;
        private const int ContinuousReadWindowMs = 650;
        private const int SingleTriggerBurstMs = 180;
        private const int TriggerToggleDebounceMs = 250;
        private const int ScannerKeyboardSuppressionMs = 80;
        private const int ExternalOutputSuppressionMs = 1200;
        private const int SingleScannerCooldownMs = 1200;
        private const int SingleHidAcceptedCooldownMs = 1500;
        private const int SingleReadTimeoutMs = 650;
        private const int SingleReadRetryDelayMs = 15;
        private const int SingleDuplicateIgnoreMs = 1500;
        private const int MaxScannerKeyboardChars = 64;
        private const int MaxUsbReadsPerPoll = 200;
        private const int TriggerLearningWindowMs = 2500;
        private const int TriggerLearningConfirmations = 2;
        private const int ScannerHidInputGapMs = 250;
        private const int ExpectedEpcHexLength = 24;
        private const string ScannerRawDeviceVidPid = "VID_32C2&PID_0066";

        private readonly ChainwayProtocol _device = new();
        private readonly Dictionary<string, TagEntry> tagMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<TagEntry> tags = new();
        private readonly DispatcherTimer _inputTimer;
        private readonly DispatcherTimer _usbReadTimer;
        private readonly DispatcherTimer _singleTriggerTimer;
        private readonly string settingsFile = Path.Combine(AppContext.BaseDirectory, "settings.txt");
        private bool _usbReadActive;
        private bool _usbPollInProgress;
        private bool _suppressTextChanged;
        private DateTime _ignoreScannerHidUntilUtc = DateTime.MinValue;
        private string _lastUsbRawDebug = string.Empty;
        private string? _lastSingleEpc;
        private DateTime _lastSingleEpcUtc = DateTime.MinValue;
        private DateTime _lastAcceptedSingleHidEpcUtc = DateTime.MinValue;
        private DateTime _lastSingleBeepUtc = DateTime.MinValue;
        private DateTime _lastUsbTriggerToggleUtc = DateTime.MinValue;
        private DateTime _ignoreUsbTagsUntilUtc = DateTime.MinValue;
        private bool _singleTriggerQueued;
        private long _scannerKeyboardSuppressionUntilUtcTicks;
        private IntPtr _scannerKeyboardSuppressionWindow;
        private int _scannerKeyboardSuppressedCharCount;
        private bool _keepScannerKeyboardSuppressedWhileReading;
        private IntPtr _externalOutputTargetWindow;
        private IntPtr _externalOutputTargetControl;
        private readonly HashSet<string> _forwardedSessionEpcs = new(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _scannerHidBuffer = new();
        private readonly StringBuilder _externalScannerHidBuffer = new();
        private int? _pendingTriggerVirtualKey;
        private DateTime _pendingTriggerUtc = DateTime.MinValue;
        private int? _candidateTriggerVirtualKey;
        private int _candidateTriggerSuccessCount;
        private int? _learnedTriggerVirtualKey;
        private DateTime _scannerHidLastInputUtc = DateTime.MinValue;
        private DateTime _lastScannerRawDataKeyUtc = DateTime.MinValue;
        private IntPtr _rawInputWindowHandle;
        private IntPtr _rawInputOldWndProc;
        private RawInputWindowProc? _rawInputWndProc;
        private string? _scannerRawDeviceName;
        private GlobalKeyboardHook? _globalKeyboardHook;
        private int _rawInputLogCount;
        private ushort _continuousTriggerDownVKey;

        public MainWindow()
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"MainWindow ctor start {DateTime.Now:O}{Environment.NewLine}");
            InitializeComponent();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"MainWindow after InitializeComponent {DateTime.Now:O}{Environment.NewLine}");

            dgTags.ItemsSource = tags;
            if (OperatingSystem.IsWindows())
            {
                lblTriggerDebug.Text = "Scanner trigger disabled";
            }
            else
            {
                lblTriggerDebug.Text = "Scanner trigger disabled";
            }

            _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SingleReadDelayMs) };
            _inputTimer.Tick += (_, _) =>
            {
                _inputTimer.Stop();
                ProcessCurrentInput();
            };

            _usbReadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UsbPollIntervalMs) };
            _usbReadTimer.Tick += async (_, _) => await PollUsbTagsAsync();

            _singleTriggerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SingleTriggerBurstMs) };
            _singleTriggerTimer.Tick += (_, _) =>
            {
                _singleTriggerTimer.Stop();
                if (!_singleTriggerQueued)
                {
                    return;
                }

                _singleTriggerQueued = false;
                ToggleUsbReadFromTrigger();
            };

            trackPower.PropertyChanged += (_, _) => SyncPowerValue(trackPower.Value);
            numPower.PropertyChanged += (_, _) =>
            {
                if (numPower.Value is decimal value)
                {
                    SyncPowerValue((double)value);
                }
            };

            Opened += MainWindow_Opened;
            Closed += MainWindow_Closed;

            LoadSettings();
            UpdateConnectionUi();
            UpdateReadModeUi();
            SyncPowerValue(trackPower.Value);
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"MainWindow ctor end {DateTime.Now:O}{Environment.NewLine}");
        }

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(InitializeScannerInputs, DispatcherPriority.Loaded);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            DisposeScannerInputs();
        }

        private void InitializeScannerInputs()
        {
            if (!OperatingSystem.IsWindows())
            {
                lblTriggerDebug.Text = "Scanner trigger unavailable on this OS";
                return;
            }

            InitializeRawScannerInput();
            _globalKeyboardHook ??= new GlobalKeyboardHook(OnGlobalKeyDown);

            lblTriggerDebug.Text = _globalKeyboardHook.IsInstalled
                ? "Scanner HID input ready"
                : "Scanner HID input ready; keyboard suppression unavailable";
        }

        private void DisposeScannerInputs()
        {
            DisposeRawScannerInput();
            _globalKeyboardHook?.Dispose();
            _globalKeyboardHook = null;
        }

        private void InitializeRawScannerInput()
        {
            if (!OperatingSystem.IsWindows() || _rawInputWndProc != null)
            {
                return;
            }

            _rawInputWindowHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_rawInputWindowHandle == IntPtr.Zero)
            {
                return;
            }

            var device = new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RawInputDeviceFlags.RIDEV_INPUTSINK,
                hwndTarget = _rawInputWindowHandle
            };

            if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                lblDebug.Text = $"Raw input register failed: {Marshal.GetLastWin32Error()}";
                return;
            }

            _rawInputWndProc = RawInputWndProcImpl;
            _rawInputOldWndProc = SetWindowLongPtr(_rawInputWindowHandle, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_rawInputWndProc));
        }

        private void DisposeRawScannerInput()
        {
            if (_rawInputWindowHandle != IntPtr.Zero && _rawInputOldWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_rawInputWindowHandle, GWL_WNDPROC, _rawInputOldWndProc);
            }

            _rawInputWindowHandle = IntPtr.Zero;
            _rawInputOldWndProc = IntPtr.Zero;
            _rawInputWndProc = null;
            _scannerHidBuffer.Clear();
        }

        private IntPtr RawInputWndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                TryHandleScannerRawInput(lParam);
            }

            return CallWindowProc(_rawInputOldWndProc, hWnd, msg, wParam, lParam);
        }

        private void TryHandleScannerRawInput(IntPtr lParam)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
            {
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size)
                {
                    return;
                }

                RAWINPUT raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
                if (raw.header.dwType != RawInputType.RIM_TYPEKEYBOARD)
                {
                    return;
                }

                string? deviceName = GetRawInputDeviceName(raw.header.hDevice);
                LogRawInputDevice(deviceName, raw.keyboard.VKey);
                if (!IsScannerRawDevice(deviceName))
                {
                    return;
                }

                _scannerRawDeviceName ??= deviceName;

                uint message = raw.keyboard.Message;
                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                {
                    HandleScannerRawKeyDown(raw.keyboard.VKey);
                }
                else if (message == WM_KEYUP || message == WM_SYSKEYUP)
                {
                    HandleScannerRawKeyUp(raw.keyboard.VKey);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool IsScannerRawDevice(string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            return deviceName.Contains(ScannerRawDeviceVidPid, StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("VID_32C2", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("PID_0066", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("VID_0483", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("VID_2047&PID_0301", StringComparison.OrdinalIgnoreCase)
                || (deviceName.Contains("VID_2047", StringComparison.OrdinalIgnoreCase)
                    && deviceName.Contains("PID_0301", StringComparison.OrdinalIgnoreCase));
        }

        private void LogRawInputDevice(string? deviceName, ushort virtualKey)
        {
            if (_rawInputLogCount >= 200)
            {
                return;
            }

            _rawInputLogCount++;
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "rawinput.log"),
                    $"{DateTime.Now:O} VK={virtualKey} Device={deviceName ?? "(null)"}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private void HandleScannerRawKeyDown(ushort virtualKey)
        {
            if (DateTime.UtcNow < _ignoreScannerHidUntilUtc)
            {
                if (rbUsb.IsChecked != true
                    && rbContinuous.IsChecked != true
                    && (IsScannerRawDataVirtualKey(virtualKey) || virtualKey == WindowsKeyboard.VK_RETURN))
                {
                    TryProcessScannerRawEpcKey(virtualKey);
                }

                return;
            }

            if (rbUsb.IsChecked == true && _device.IsConnected)
            {
                if (rbContinuous.IsChecked == true)
                {
                    if (!WindowsKeyboard.IsIgnoredTriggerKey(virtualKey))
                    {
                        _ignoreScannerHidUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
                        IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                        if (appWindow != IntPtr.Zero)
                        {
                            ArmScannerKeyboardSuppression(appWindow, durationMs: 1800);
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            lblTriggerDebug.Text = _usbReadActive
                                ? "Continuous: trigger pressed - stop"
                                : "Continuous: trigger pressed - start";
                            ToggleUsbReadFromTrigger();
                        });
                    }

                    return;
                }

                if (rbSingle.IsChecked == true
                    && !_usbReadActive
                    && (IsSingleScannerTriggerVirtualKey(virtualKey)
                        || IsScannerRawDataVirtualKey(virtualKey)
                        || virtualKey == WindowsKeyboard.VK_RETURN))
                {
                    IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (appWindow != IntPtr.Zero)
                    {
                        ArmScannerKeyboardSuppression(appWindow, durationMs: ExternalOutputSuppressionMs);
                    }

                    _ignoreScannerHidUntilUtc = DateTime.UtcNow.AddMilliseconds(SingleScannerCooldownMs);
                    Dispatcher.UIThread.Post(() =>
                    {
                        lblTriggerDebug.Text = "Single: trigger pressed - read one tag";
                        ToggleUsbReadFromTrigger();
                    });
                    return;
                }

                return;
            }

            if (_usbReadActive)
            {
                return;
            }

            TryProcessScannerRawEpcKey(virtualKey);
        }

        private bool TryProcessScannerRawEpcKey(ushort virtualKey)
        {
            var now = DateTime.UtcNow;
            if ((now - _scannerHidLastInputUtc).TotalMilliseconds > ScannerHidInputGapMs)
            {
                _scannerHidBuffer.Clear();
            }

            _scannerHidLastInputUtc = now;

            if (TryMapScannerRawKeyToHex(virtualKey, out char character))
            {
                _scannerHidBuffer.Append(character);
                return true;
            }

            if (WindowsKeyboard.IsIgnoredTriggerKey(virtualKey))
            {
                return true;
            }

            if (virtualKey != WindowsKeyboard.VK_RETURN)
            {
                if (_scannerHidBuffer.Length > 0)
                {
                    _scannerHidBuffer.Clear();
                    return true;
                }

                return false;
            }

            if (_scannerHidBuffer.Length == 0)
            {
                return false;
            }

            string raw = _scannerHidBuffer.ToString();
            _scannerHidBuffer.Clear();
            string? epc = NormalizeEpc(raw);
            if (epc == null)
            {
                return true;
            }

            if (rbUsb.IsChecked == true && rbSingle.IsChecked == true)
            {
                _ignoreScannerHidUntilUtc = DateTime.UtcNow.AddMilliseconds(SingleScannerCooldownMs);
            }

            Dispatcher.UIThread.Post(() => HandleScannerHidEpc(epc));
            return true;
        }

        private void HandleScannerRawKeyUp(ushort virtualKey)
        {
            if (rbContinuous.IsChecked != true || _continuousTriggerDownVKey == 0)
            {
                return;
            }

            if (virtualKey != _continuousTriggerDownVKey)
            {
                return;
            }

            _continuousTriggerDownVKey = 0;
        }

        private void HandleScannerHidEpc(string epc)
        {
            if (rbUsb.IsChecked != true)
            {
                return;
            }

            if (IsDuplicateSessionEpc(epc))
            {
                if (rbSingle.IsChecked == true)
                {
                    ResetSingleScannerInputGate();
                }

                lblDebug.Text = $"Already scanned EPC ignored: {epc}";
                return;
            }

            if (rbContinuous.IsChecked == true)
            {
                RememberSingleEpc(epc);
                ShowCurrentEpc(epc);
                AddTagIfNew(epc);
                SendEpcToActiveAppIfAppFocused(epc);
                PlayContinuousReadBeeps(1);
                UpdateTagCount();
                lblReadStatus.Text = $"Continuous: reading ({GetDisplayedUniqueTagCount()} unique tag)";
                lblReadStatus.Foreground = Brushes.Green;
                lblDebug.Text = $"Scanner HID EPC received in Continuous: {epc}";
                return;
            }

            if (rbSingle.IsChecked == true)
            {
                if ((DateTime.UtcNow - _lastAcceptedSingleHidEpcUtc).TotalMilliseconds < SingleHidAcceptedCooldownMs)
                {
                    ResetSingleScannerInputGate();
                    lblDebug.Text = $"Single: extra HID EPC ignored: {epc}";
                    return;
                }

                _lastAcceptedSingleHidEpcUtc = DateTime.UtcNow;
                RememberSingleEpc(epc);
                ShowCurrentEpc(epc);
                AddTagIfNew(epc);
                SendEpcToActiveApp(epc);
                PlaySingleReadBeep();
                UpdateTagCount();
                if (_usbReadActive)
                {
                    _device.SendInventoryCommand(false);
                    StopUsbRead();
                }

                ResetSingleScannerInputGate();
                lblReadStatus.Text = "Single: read 1 tag, ready for next scan";
                lblReadStatus.Foreground = Brushes.Green;
                lblDebug.Text = $"Scanner HID EPC received in Single: {epc}";
                return;
            }

        }

        private static bool TryMapScannerRawKeyToHex(ushort virtualKey, out char character)
        {
            character = virtualKey switch
            {
                >= 0x30 and <= 0x39 => (char)virtualKey,
                >= 0x60 and <= 0x69 => (char)('0' + (virtualKey - 0x60)),
                >= 0x41 and <= 0x46 => (char)virtualKey,
                _ => '\0'
            };

            return character != '\0';
        }

        private static string? GetRawInputDeviceName(IntPtr deviceHandle)
        {
            if (deviceHandle == IntPtr.Zero)
            {
                return null;
            }

            uint size = 0;
            _ = GetRawInputDeviceInfo(deviceHandle, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0)
            {
                return null;
            }

            var builder = new StringBuilder((int)size);
            return GetRawInputDeviceInfo(deviceHandle, RIDI_DEVICENAME, builder, ref size) > 0
                ? builder.ToString()
                : null;
        }

        private void LoadSettings()
        {
            if (!File.Exists(settingsFile))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(settingsFile))
            {
                if (line.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
                {
                    bool continuous = line.Contains("Continuous", StringComparison.OrdinalIgnoreCase);
                    rbContinuous.IsChecked = continuous;
                    rbSingle.IsChecked = !continuous;
                }

                if (line.StartsWith("Conn:", StringComparison.OrdinalIgnoreCase))
                {
                    bool usb = line.Contains("USB", StringComparison.OrdinalIgnoreCase);
                    rbUsb.IsChecked = usb;
                    rbBluetooth.IsChecked = !usb;
                }
            }
        }

        private void SaveSettings()
        {
            string mode = rbContinuous.IsChecked == true ? "Continuous" : "Single";
            string conn = rbUsb.IsChecked == true ? "USB" : "Bluetooth";
            File.WriteAllLines(settingsFile, new[] { $"Mode:{mode}", $"Conn:{conn}" });
        }

        private void ConnType_Changed(object? sender, RoutedEventArgs e)
        {
            UpdateConnectionUi();
            SaveSettings();
            FocusInternalInput(force: chkSendToActiveApp.IsChecked == true);
        }

        private void UpdateConnectionUi()
        {
            bool bluetooth = rbBluetooth.IsChecked == true;
            UpdateSingleTriggerKeySuppression();
            txtEpcInput.IsReadOnly = rbUsb.IsChecked == true && _device.IsConnected;
            lblBtHint.IsVisible = bluetooth;
            pnlUsbButtons.IsVisible = !bluetooth;
            pnlAntenna.IsEnabled = _device.IsConnected && !bluetooth;
            pnlPower.IsEnabled = _device.IsConnected && !bluetooth;
            pnlAntenna.Opacity = pnlAntenna.IsEnabled ? 1 : 0.5;
            pnlPower.Opacity = pnlPower.IsEnabled ? 1 : 0.5;

            if (bluetooth)
            {
                lblStatus.Text = "Bluetooth HID: ready";
                lblStatus.Foreground = Brushes.Green;
            }
            else if (!_device.IsConnected)
            {
                lblStatus.Text = "Disconnected";
                lblStatus.Foreground = Brushes.Red;
            }
        }

        private void ReadMode_Changed(object? sender, RoutedEventArgs e)
        {
            UpdateReadModeUi();
            SaveSettings();

            if (_device.IsConnected)
            {
                int stopResult = _device.SendInventoryCommand(false);
                if (stopResult != 0)
                {
                    lblDebug.Text = $"Inventory stop failed: {stopResult}. {_device.LastError}";
                }

                ApplyScannerBuzzerForReadMode();
            }

            FocusInternalInput();
        }

        private void UpdateReadModeUi()
        {
            bool continuous = rbContinuous.IsChecked == true;
            UpdateSingleTriggerKeySuppression();
            _inputTimer.Stop();
            StopUsbRead();
            _inputTimer.Interval = TimeSpan.FromMilliseconds(SingleReadDelayMs);

            lblReadStatus.Text = continuous
                ? "Continuous: Start reads until stopped"
                : "Single: Start reads one tag";
            lblReadStatus.Foreground = continuous ? Brushes.Green : Brushes.Orange;
            btnStartRead.Content = "Start";
        }

        private void UpdateSingleTriggerKeySuppression()
        {
        }

        private void ApplyScannerBuzzerForReadMode()
        {
            if (!_device.IsConnected || rbUsb.IsChecked != true)
            {
                return;
            }

            int result = _device.SetBuzzer(true);
            if (result != 0)
            {
                lblDebug.Text = $"Scanner buzzer enable failed: {result}. {_device.LastError}";
            }
        }

        private void TxtEpcInput_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged)
            {
                return;
            }

            if (rbUsb.IsChecked == true && _device.IsConnected && rbSingle.IsChecked != true)
            {
                if (!string.IsNullOrWhiteSpace(txtEpcInput.Text))
                {
                    ClearInput();
                }
                return;
            }

            _inputTimer.Stop();
            _inputTimer.Interval = TimeSpan.FromMilliseconds(SingleReadDelayMs);
            _inputTimer.Start();
        }

        private void TxtEpcInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            _inputTimer.Stop();
            if (rbUsb.IsChecked == true && _device.IsConnected && rbSingle.IsChecked == true)
            {
                if (!string.IsNullOrWhiteSpace(txtEpcInput.Text))
                {
                    ProcessCurrentInput();
                }

                e.Handled = true;
                return;
            }

            if (rbUsb.IsChecked == true && _device.IsConnected)
            {
                e.Handled = true;
                return;
            }

            if (rbContinuous.IsChecked == true && _usbReadActive)
            {
                if (!string.IsNullOrWhiteSpace(txtEpcInput.Text))
                {
                    ProcessCurrentInput();
                }
                StopUsbReadFromUi();
            }
            else if (!string.IsNullOrWhiteSpace(txtEpcInput.Text))
            {
                ProcessCurrentInput();
            }
            else
            {
                StartUsbRead();
            }

            e.Handled = true;
        }

        private void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (!ShouldToggleUsbReadFromDirectKey(e.Key))
            {
                return;
            }

            RememberPendingTriggerKey(e.Key);
            HandleScannerTriggerKey();
            e.Handled = true;
        }

        private bool ShouldToggleUsbReadFromDirectKey(Key key)
        {
            return rbUsb.IsChecked == true
                && _device.IsConnected
                && IsAcceptedDirectTriggerKey(key);
        }

        private static bool IsDirectTriggerKey(Key key)
        {
            return key == Key.F11;
        }

        private bool IsAcceptedDirectTriggerKey(Key key)
        {
            return IsDirectTriggerKey(key);
        }

        private bool IsAcceptedDirectTriggerVirtualKey(int virtualKey)
        {
            return IsScannerTriggerVirtualKey(virtualKey);
        }

        private bool IsContinuousScannerTriggerVirtualKey(int virtualKey)
        {
            if (WindowsKeyboard.IsIgnoredTriggerKey(virtualKey))
            {
                return false;
            }

            if (WindowsKeyboard.IsLikelyScannerDataKey(virtualKey))
            {
                return false;
            }

            return (_learnedTriggerVirtualKey.HasValue && virtualKey == _learnedTriggerVirtualKey.Value)
                || WindowsKeyboard.IsScannerTriggerKey(virtualKey)
                || WindowsKeyboard.IsDirectTriggerKey(virtualKey);
        }

        private bool IsContinuousToggleInput(int virtualKey)
        {
            return IsContinuousScannerTriggerVirtualKey(virtualKey);
        }

        private bool IsSingleScannerTriggerVirtualKey(int virtualKey)
        {
            if (WindowsKeyboard.IsIgnoredTriggerKey(virtualKey))
            {
                return false;
            }

            return (_learnedTriggerVirtualKey.HasValue && virtualKey == _learnedTriggerVirtualKey.Value)
                || WindowsKeyboard.IsScannerTriggerKey(virtualKey)
                || WindowsKeyboard.IsDirectTriggerKey(virtualKey);
        }

        private static bool IsScannerRawDataVirtualKey(int virtualKey)
        {
            return virtualKey != WindowsKeyboard.VK_RETURN
                && !WindowsKeyboard.IsIgnoredTriggerKey(virtualKey)
                && WindowsKeyboard.IsLikelyScannerDataKey(virtualKey);
        }

        private bool IsScannerTriggerVirtualKey(int virtualKey)
        {
            if (virtualKey == WindowsKeyboard.VK_RETURN || WindowsKeyboard.IsLikelyScannerDataKey(virtualKey))
            {
                return false;
            }

            return (_learnedTriggerVirtualKey.HasValue && virtualKey == _learnedTriggerVirtualKey.Value)
                || WindowsKeyboard.IsScannerTriggerKey(virtualKey)
                || WindowsKeyboard.IsDirectTriggerKey(virtualKey);
        }

        private void RememberPendingTriggerKey(Key key)
        {
            if (!WindowsKeyboard.TryMapAvaloniaKeyToVirtualKey(key, out int virtualKey))
            {
                return;
            }

            if (virtualKey == WindowsKeyboard.VK_RETURN)
            {
                return;
            }

            _pendingTriggerVirtualKey = virtualKey;
            _pendingTriggerUtc = DateTime.UtcNow;
        }

        private void RememberPendingTriggerVirtualKey(int virtualKey)
        {
            if (virtualKey == WindowsKeyboard.VK_RETURN)
            {
                return;
            }

            _pendingTriggerVirtualKey = virtualKey;
            _pendingTriggerUtc = DateTime.UtcNow;
        }

        private void ResetPendingTriggerLearning()
        {
            _pendingTriggerVirtualKey = null;
            _pendingTriggerUtc = DateTime.MinValue;
        }

        private void PromotePendingTriggerIfSuccessfulRead()
        {
            if (!_pendingTriggerVirtualKey.HasValue)
            {
                return;
            }

            if (_pendingTriggerVirtualKey.Value == WindowsKeyboard.VK_RETURN)
            {
                ResetPendingTriggerLearning();
                return;
            }

            if ((DateTime.UtcNow - _pendingTriggerUtc).TotalMilliseconds > TriggerLearningWindowMs)
            {
                ResetPendingTriggerLearning();
                return;
            }

            if (_candidateTriggerVirtualKey == _pendingTriggerVirtualKey.Value)
            {
                _candidateTriggerSuccessCount++;
            }
            else
            {
                _candidateTriggerVirtualKey = _pendingTriggerVirtualKey.Value;
                _candidateTriggerSuccessCount = 1;
            }

            if (_candidateTriggerSuccessCount >= TriggerLearningConfirmations)
            {
                _learnedTriggerVirtualKey = _pendingTriggerVirtualKey.Value;
                lblTriggerDebug.Text = $"Trigger key: locked to VK {_learnedTriggerVirtualKey.Value}";
            }

            ResetPendingTriggerLearning();
        }

        private bool OnGlobalKeyDown(int virtualKey)
        {
            if (IsScannerKeyboardSuppressionActive(virtualKey))
            {
                return true;
            }

            return false;
        }

        private bool TryHandleExternalScannerHidKey(int virtualKey, KeyboardTarget target)
        {
            var now = DateTime.UtcNow;
            if ((now - _scannerHidLastInputUtc).TotalMilliseconds > ScannerHidInputGapMs)
            {
                _externalScannerHidBuffer.Clear();
            }

            _scannerHidLastInputUtc = now;

            if (TryMapScannerRawKeyToHex((ushort)virtualKey, out char character))
            {
                _externalScannerHidBuffer.Append(character);
                return true;
            }

            if (virtualKey != WindowsKeyboard.VK_RETURN)
            {
                if (_externalScannerHidBuffer.Length > 0)
                {
                    _externalScannerHidBuffer.Clear();
                    return true;
                }

                return false;
            }

            if (_externalScannerHidBuffer.Length == 0)
            {
                return false;
            }

            string raw = _externalScannerHidBuffer.ToString();
            _externalScannerHidBuffer.Clear();
            string? epc = NormalizeEpc(raw);
            if (epc == null)
            {
                return true;
            }

            _externalOutputTargetWindow = target.Window;
            _externalOutputTargetControl = target.Control;
            Dispatcher.UIThread.Post(() => HandleExternalScannerHidEpc(epc));
            return true;
        }

        private void HandleExternalScannerHidEpc(string epc)
        {
            if (IsDuplicateSessionEpc(epc))
            {
                lblReadStatus.Text = rbContinuous.IsChecked == true
                    ? $"Continuous: reading ({GetDisplayedUniqueTagCount()} unique tag)"
                    : "Single: already scanned, ready for next scan";
                lblReadStatus.Foreground = Brushes.Orange;
                lblDebug.Text = $"Already scanned EPC ignored: {epc}";
                return;
            }

            RememberSingleEpc(epc);
            ShowCurrentEpc(epc);
            bool added = AddTagIfNew(epc);
            if (added)
            {
                SendEpcToActiveApp(epc);
            }
            UpdateTagCount();

            lblReadStatus.Text = rbContinuous.IsChecked == true
                ? $"Continuous: reading ({GetDisplayedUniqueTagCount()} unique tag)"
                : "Single: read 1 tag, ready for next scan";
            lblReadStatus.Foreground = Brushes.Green;
            lblDebug.Text = added
                ? $"External scanner EPC received: {epc}"
                : $"Already scanned EPC ignored: {epc}";
        }

        private void ArmScannerKeyboardSuppression(
            IntPtr targetWindow,
            bool keepWhileReading = false,
            int durationMs = ScannerKeyboardSuppressionMs)
        {
            _scannerKeyboardSuppressionWindow = targetWindow;
            _scannerKeyboardSuppressedCharCount = 0;
            _keepScannerKeyboardSuppressedWhileReading = keepWhileReading;
            Interlocked.Exchange(
                ref _scannerKeyboardSuppressionUntilUtcTicks,
                DateTime.UtcNow.AddMilliseconds(durationMs).Ticks);
        }

        private bool IsScannerKeyboardSuppressionActive(int virtualKey)
        {
            if (_scannerKeyboardSuppressionWindow == IntPtr.Zero)
            {
                return false;
            }

            if (!WindowsKeyboard.IsLikelyScannerDataKey(virtualKey))
            {
                return false;
            }

            if (_keepScannerKeyboardSuppressedWhileReading
                && _usbReadActive
                && rbContinuous.IsChecked == true)
            {
                return true;
            }

            long untilTicks = Interlocked.Read(ref _scannerKeyboardSuppressionUntilUtcTicks);
            if (untilTicks <= DateTime.UtcNow.Ticks)
            {
                ResetScannerKeyboardSuppression();
                return false;
            }

            _scannerKeyboardSuppressedCharCount++;
            if (virtualKey == WindowsKeyboard.VK_RETURN
                || _scannerKeyboardSuppressedCharCount >= MaxScannerKeyboardChars)
            {
                ResetScannerKeyboardSuppression();
            }

            return true;
        }

        private void ResetScannerKeyboardSuppression()
        {
            _scannerKeyboardSuppressionWindow = IntPtr.Zero;
            _scannerKeyboardSuppressedCharCount = 0;
            _keepScannerKeyboardSuppressedWhileReading = false;
            Interlocked.Exchange(ref _scannerKeyboardSuppressionUntilUtcTicks, 0);
        }

        private void HandleScannerTriggerKey()
        {
            if (rbSingle.IsChecked == true)
            {
                if (_usbReadActive)
                {
                    lblDebug.Text = "Single read is already running.";
                    return;
                }

                StartUsbRead();
                return;
            }

            ToggleUsbReadFromTrigger();
        }

        private void ToggleUsbReadFromTrigger()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUsbTriggerToggleUtc).TotalMilliseconds < TriggerToggleDebounceMs)
            {
                return;
            }

            _lastUsbTriggerToggleUtc = now;
            lblDebug.Text = $"Trigger received ({(rbSingle.IsChecked == true ? "Single" : "Continuous")}).";

            if (rbSingle.IsChecked == true)
            {
                if (_usbReadActive)
                {
                    lblDebug.Text = "Single read is already running.";
                    return;
                }

                StartUsbRead();
                return;
            }

            if (_usbReadActive)
            {
                StopUsbReadFromUi(discardTrailingTags: rbContinuous.IsChecked == true);
                return;
            }

            StartUsbRead();
        }

        private void BtnStartRead_Click(object? sender, RoutedEventArgs e)
        {
            _ignoreScannerHidUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

            if (rbSingle.IsChecked == true)
            {
                ClearInput();
                if (rbUsb.IsChecked == true && _device.IsConnected)
                {
                    StartUsbRead();
                    return;
                }

                FocusInternalInput(force: true);
                lblReadStatus.Text = "Single: ready for scanner input";
                lblReadStatus.Foreground = Brushes.Green;
                return;
            }

            if (_usbReadActive)
            {
                StopUsbReadFromUi();
                return;
            }

            StartUsbRead();
        }



        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Scanning эхлүүлэх.
        /// isContinuous=true  → trigger дарах хүртэл тасралтгүй уншина.
        /// isContinuous=false → нэг тэмдэглэгээ уншаад автоматаар зогсоно.
        /// </summary>
        public void StartScan(bool isContinuous)
        {
            if (isContinuous)
            {
                rbContinuous.IsChecked = true;
                rbSingle.IsChecked = false;
            }
            else
            {
                rbSingle.IsChecked = true;
                rbContinuous.IsChecked = false;
            }

            UpdateReadModeUi();
            StartUsbRead();
        }

        /// <summary>
        /// Scanning зогсоох.
        /// </summary>
        public void StopScan()
        {
            if (!_usbReadActive)
            {
                return;
            }

            StopUsbReadFromUi();
        }

        /// <summary>
        /// UHF_GetReceived_EX-г дуудаж нэг EPC унших.
        /// Амжилттай бол EPC hex string буцаана; олдохгүй бол null.
        /// Энэ метод background thread дээр дуудагдана.
        /// </summary>
        private static string? ReadOneEpcFromDevice()
        {
            int len = 128;
            byte[] buffer = new byte[len];
            int result = UHFAPI.UHF_GetReceived_EX(ref len, buffer);
            if (result != 0 || len <= 0)
            {
                return null;
            }

            return ExtractEpcFromReceivedBytes(buffer, len);
        }

        /// <summary>
        /// Single scan: UHFInventory → нэг шинэ EPC хүлээн авах → UHFStopGet.
        /// Олдсон EPC-г UI болон гадаад апп руу илгээнэ.
        /// </summary>
        public async Task ScanOneTagAsync()
        {
            if (!_device.IsConnected)
            {
                lblDebug.Text = "Төхөөрөмж холбогдоогүй байна.";
                return;
            }

            // UHFInventory эхлүүлэх
            int startRet = _device.SendInventoryCommand(true, single: true);
            if (startRet != 0)
            {
                lblDebug.Text = $"UHFInventory амжилтгүй: {startRet}. {_device.LastError}";
                return;
            }

            lblReadStatus.Text = "Single: уншиж байна...";
            lblReadStatus.Foreground = Brushes.Green;

            // Background thread дээр EPC хайх (UI блоклохгүй)
            string? epc = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(SingleReadTimeoutMs);

            epc = await Task.Run(() =>
            {
                while (DateTime.UtcNow <= deadline)
                {
                    string? found = ReadOneEpcFromDevice();
                    if (found != null)
                    {
                        return found;
                    }

                    Thread.Sleep(SingleReadRetryDelayMs);
                }
                return null;
            });

            // UHFStopGet дуудах
            _device.SendInventoryCommand(false);

            if (epc == null)
            {
                lblReadStatus.Text = "Single: тэмдэглэгээ олдсонгүй";
                lblReadStatus.Foreground = Brushes.Orange;
                lblDebug.Text = _device.LastError;
                return;
            }

            // Давхардсан шалгах
            if (IsDuplicateSessionEpc(epc))
            {
                lblReadStatus.Text = "Single: аль хэдийн уншсан тэмдэглэгээ";
                lblReadStatus.Foreground = Brushes.Orange;
                lblDebug.Text = $"Давхардал: {epc}";
                return;
            }

            // UI-д харуулах, гадаад апп руу илгээх
            AddTagIfNew(epc);
            ShowCurrentEpc(epc);
            SendEpcToActiveApp(epc);
            PlaySingleReadBeep();
            UpdateTagCount();
            lblReadStatus.Text = $"Single: {epc} — амжилттай";
            lblReadStatus.Foreground = Brushes.Green;
            lblDebug.Text = $"EPC: {epc}";
        }

        // ── End Public API ───────────────────────────────────────────────────────

        private void ExternalOutput_Changed(object? sender, RoutedEventArgs e)
        {
            if (chkSendToActiveApp.IsChecked != true)
            {
                _externalOutputTargetWindow = IntPtr.Zero;
                _externalOutputTargetControl = IntPtr.Zero;
                ResetScannerKeyboardSuppression();
            }

            lblDebug.Text = chkSendToActiveApp.IsChecked == true
                ? "External app output enabled."
                : "External app output disabled for USB stability.";
        }

        private void StartUsbRead()
        {
            _ignoreUsbTagsUntilUtc = DateTime.MinValue;

            if (!_device.IsConnected)
            {
                lblDebug.Text = "USB is not connected.";
                FocusInternalInput();
                return;
            }

            bool single = rbSingle.IsChecked == true;
            if (single)
            {
                CaptureExternalOutputTargetFromForeground();
                if (chkSendToActiveApp.IsChecked == true && _externalOutputTargetWindow != IntPtr.Zero)
                {
                    ArmScannerKeyboardSuppression(_externalOutputTargetWindow, durationMs: ExternalOutputSuppressionMs);
                }
                IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (appWindow != IntPtr.Zero)
                {
                    ArmScannerKeyboardSuppression(appWindow, durationMs: ExternalOutputSuppressionMs);
                }
                Activate();
                FocusInternalInput(force: true);
            }
            else
            {
                CaptureExternalOutputTargetFromForeground();
                if (chkSendToActiveApp.IsChecked == true && _externalOutputTargetWindow != IntPtr.Zero)
                {
                    ArmScannerKeyboardSuppression(
                        _externalOutputTargetWindow,
                        keepWhileReading: rbContinuous.IsChecked == true);
                    FocusInternalInput(force: true);
                }
            }

            ClearInput();
            if (_usbReadActive)
            {
                _device.SendInventoryCommand(false);
            }
            StopUsbRead();
            FlushPendingUsbTags();

            if (!single)
            {
                int startResult = _device.SendInventoryCommand(true);
                if (startResult != 0)
                {
                    lblDebug.Text = $"Inventory start failed: {startResult}. {_device.LastError}";
                    FocusInternalInput();
                    return;
                }
            }

            _usbReadActive = true;
            _usbReadTimer.Stop();
            _usbReadTimer.Interval = single
                ? TimeSpan.FromMilliseconds(GetInitialReadDelayMs())
                : TimeSpan.FromMilliseconds(GetInitialReadDelayMs());
            _usbReadTimer.Start();

            lblReadStatus.Text = rbSingle.IsChecked == true
                ? "Single: reading one tag..."
                : "Continuous: reading, press Stop to finish";
            lblReadStatus.Foreground = Brushes.Green;
            btnStartRead.Content = "Stop";
            lblDebug.Text = single ? "Single read started." : _device.LastError;
            if (!single)
            {
                IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (appWindow != IntPtr.Zero)
                {
                    ArmScannerKeyboardSuppression(appWindow, keepWhileReading: true, durationMs: 1800);
                }
            }

            if (single || chkSendToActiveApp.IsChecked != true || _externalOutputTargetWindow != IntPtr.Zero)
            {
                FocusInternalInput(force: _externalOutputTargetWindow != IntPtr.Zero);
            }
        }

        private int GetInitialReadDelayMs()
        {
            return UsbInitialReadDelayMs;
        }

        private void StopUsbRead()
        {
            _usbReadTimer.Stop();
            _usbReadTimer.Interval = TimeSpan.FromMilliseconds(UsbPollIntervalMs);
            _usbReadActive = false;
            _usbPollInProgress = false;
            long suppressionUntilTicks = Interlocked.Read(ref _scannerKeyboardSuppressionUntilUtcTicks);
            bool keepPostForwardSuppression = chkSendToActiveApp.IsChecked == true
                && _externalOutputTargetWindow != IntPtr.Zero
                && suppressionUntilTicks > DateTime.UtcNow.Ticks;

            if (keepPostForwardSuppression)
            {
                _keepScannerKeyboardSuppressedWhileReading = false;
            }
            else
            {
                ResetScannerKeyboardSuppression();
            }

            btnStartRead.Content = "Start";
        }

        private void StopUsbReadFromUi(bool discardTrailingTags = false)
        {
            if (discardTrailingTags)
            {
                _ignoreUsbTagsUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
                if (chkSendToActiveApp.IsChecked == true && _externalOutputTargetWindow != IntPtr.Zero)
                {
                    ArmScannerKeyboardSuppression(_externalOutputTargetWindow);
                }
            }

            int stopResult = _device.SendInventoryCommand(false);
            StopUsbRead();
            if (discardTrailingTags)
            {
                FlushPendingUsbTags();
            }

            string mode = rbSingle.IsChecked == true ? "Single" : "Continuous";
            lblReadStatus.Text = stopResult == 0
                ? $"{mode}: stopped"
                : $"{mode}: stop failed ({stopResult})";
            lblReadStatus.Foreground = stopResult == 0 ? Brushes.Green : Brushes.Orange;
            lblDebug.Text = _device.LastError;
            FocusInternalInput();
        }

        private async Task PollUsbTagsAsync()
        {
            if (!_usbReadActive || !_device.IsConnected)
            {
                StopUsbRead();
                return;
            }

            if (_usbPollInProgress)
            {
                return;
            }

            _usbPollInProgress = true;
            try
            {
                if (rbSingle.IsChecked == true)
                {
                    await PollSingleUsbPulseAsync();
                    return;
                }

                _usbReadTimer.Interval = TimeSpan.FromMilliseconds(UsbPollIntervalMs);
                List<string> epcs;
                try
                {
                    epcs = await Task.Run(ReadContinuousUsbTags);
                }
                catch (Exception ex)
                {
                    lblDebug.Text = $"USB read failed: {ex.GetType().Name}: {ex.Message}";
                    return;
                }

                HandleUsbTags(epcs);
            }
            finally
            {
                _usbPollInProgress = false;
            }
        }

        private List<string> ReadContinuousUsbTags()
        {
            int startResult = _device.SendInventoryCommand(true);
            if (startResult != 0)
            {
                return new List<string>();
            }

            var epcs = new List<string>();
            var deadline = DateTime.UtcNow.AddMilliseconds(ContinuousReadWindowMs);
            do
            {
                epcs.AddRange(ReadAvailableUsbTags());
                Thread.Sleep(SingleReadRetryDelayMs);
            }
            while (_usbReadActive && DateTime.UtcNow <= deadline);

            return epcs
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task PollSingleUsbPulseAsync()
        {
            _usbReadTimer.Stop();

            int startResult = _device.SendInventoryCommand(true, true);
            if (startResult != 0)
            {
                lblDebug.Text = $"Single pulse start failed: {startResult}. {_device.LastError}";
                StopUsbRead();
                lblReadStatus.Text = "Single: ready for next scan";
                lblReadStatus.Foreground = Brushes.Orange;
                return;
            }

            await Task.Delay(UsbInitialReadDelayMs);
            if (!_usbReadActive || !_device.IsConnected)
            {
                _device.SendInventoryCommand(false);
                StopUsbRead();
                return;
            }

            List<string> epcs;
            try
            {
                epcs = await ReadSingleUsbTagsAsync();
            }
            catch (Exception ex)
            {
                _device.SendInventoryCommand(false);
                lblDebug.Text = $"Single pulse read failed: {ex.GetType().Name}: {ex.Message}";
                StopUsbRead();
                lblReadStatus.Text = "Single: ready for next scan";
                lblReadStatus.Foreground = Brushes.Orange;
                return;
            }

            int stopResult = _device.SendInventoryCommand(false);
            HandleUsbTags(epcs);
            StopUsbRead();
            ResetSingleScannerInputGate();
            FlushPendingUsbTags();

            if (stopResult != 0)
            {
                lblDebug.Text = $"Single pulse stop failed: {stopResult}. {_device.LastError}";
            }
        }

        private async Task<List<string>> ReadSingleUsbTagsAsync()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(SingleReadTimeoutMs);
            var epcs = new List<string>();

            while (_usbReadActive && _device.IsConnected && DateTime.UtcNow <= deadline)
            {
                epcs = await Task.Run(ReadAvailableUsbTags);
                if (epcs.Count > 0)
                {
                    return epcs;
                }

                await Task.Delay(SingleReadRetryDelayMs);
            }

            return epcs;
        }

        private void ResetSingleScannerInputGate()
        {
            if (rbSingle.IsChecked != true)
            {
                return;
            }

            _ignoreScannerHidUntilUtc = DateTime.UtcNow.AddMilliseconds(SingleScannerCooldownMs);
            _scannerHidBuffer.Clear();
            _externalScannerHidBuffer.Clear();
            ResetScannerKeyboardSuppression();
        }

        private void CaptureExternalOutputTargetFromForeground()
        {
            if (chkSendToActiveApp.IsChecked != true || !OperatingSystem.IsWindows())
            {
                return;
            }

            KeyboardTarget target = WindowsKeyboard.GetCurrentInputTarget();
            IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (target.Window == IntPtr.Zero || target.Window == appWindow)
            {
                return;
            }

            _externalOutputTargetWindow = target.Window;
            _externalOutputTargetControl = target.Control;
        }

        private void FlushPendingUsbTags()
        {
            while (true)
            {
                int len = 128;
                byte[] buffer = new byte[len];
                int result = UHFAPI.UHF_GetReceived_EX(ref len, buffer);
                if (result != 0 || len <= 0)
                {
                    return;
                }
            }
        }

        private List<string> ReadAvailableUsbTags()
        {
            var epcs = new List<string>();
            var rawReads = new List<string>();
            _lastUsbRawDebug = string.Empty;
            for (int i = 0; i < MaxUsbReadsPerPoll; i++)
            {
                int len = 128;
                byte[] buffer = new byte[len];
                int result = UHFAPI.UHF_GetReceived_EX(ref len, buffer);
                if (result != 0 || len <= 0)
                {
                    break;
                }

                rawReads.Add(Convert.ToHexString(buffer, 0, len));
                string? epc = ExtractEpcFromReceivedBytes(buffer, len);
                if (epc != null)
                {
                    epcs.Add(epc);
                }
            }

            if (rawReads.Count > 0)
            {
                _lastUsbRawDebug = string.Join(" | ", rawReads.Take(5));
            }

            return epcs;
        }

        private static string? ExtractEpcFromReceivedBytes(byte[] buffer, int length)
        {
            if (length <= 0)
            {
                return null;
            }

            byte[] data = buffer.Take(length).ToArray();

            string? epcFromFrame = ExtractEpcFromReaderFrame(data);
            if (epcFromFrame != null)
            {
                return epcFromFrame;
            }

            // Observed SR160 payload:
            //   0E 3000 E200001D92150042234010ED 00FD8A01
            // where 0E is UII length, 3000 is PC, then 12 EPC bytes.
            if (data.Length >= 4)
            {
                int uiiLength = data[0];
                if (uiiLength >= 4 && data.Length >= 1 + uiiLength)
                {
                    ushort pcWithLength = (ushort)((data[1] << 8) | data[2]);
                    int epcLengthFromPc = ((pcWithLength >> 11) & 0x1F) * 2;
                    int epcLength = epcLengthFromPc > 0
                        ? Math.Min(epcLengthFromPc, uiiLength - 2)
                        : uiiLength - 2;

                    if (IsValidEpcByteLength(epcLength))
                    {
                        return NormalizeUsbEpc(Convert.ToHexString(data, 3, epcLength));
                    }
                }
            }

            // Some UHF APIs return UII as PC(2 bytes) + EPC. For a 96-bit EPC the
            // PC word is commonly 0x3000, so showing the whole UII prefixes the EPC
            // with 3000. Decode the PC length and return only the EPC bytes.
            if (data.Length >= 4)
            {
                ushort pc = (ushort)((data[0] << 8) | data[1]);
                int epcLength = ((pc >> 11) & 0x1F) * 2;
                if (IsValidEpcByteLength(epcLength) && data.Length >= 2 + epcLength)
                {
                    return NormalizeUsbEpc(Convert.ToHexString(data, 2, epcLength));
                }
            }

            // Some APIs return only the EPC bytes. Do not accept arbitrary longer
            // response/status payloads here, because that can display a non-EPC ID.
            if (IsValidEpcByteLength(data.Length) && !LooksLikeReaderFrame(data))
            {
                return NormalizeUsbEpc(Convert.ToHexString(data));
            }

            return null;
        }

        private static string? ExtractEpcFromReaderFrame(byte[] data)
        {
            for (int start = 0; start <= data.Length - 7; start++)
            {
                if (data[start] != 0xBB)
                {
                    continue;
                }

                int payloadLength = (data[start + 3] << 8) | data[start + 4];
                int frameLength = 5 + payloadLength + 2;
                if (payloadLength < 0 || start + frameLength > data.Length)
                {
                    continue;
                }

                if (data[start + frameLength - 1] != 0x7E)
                {
                    continue;
                }

                byte checksum = 0;
                for (int i = start + 1; i < start + 5 + payloadLength; i++)
                {
                    checksum += data[i];
                }

                if (checksum != data[start + 5 + payloadLength])
                {
                    continue;
                }

                string? epc = ExtractEpcFromReceivedBytes(data.Skip(start + 5).Take(payloadLength).ToArray(), payloadLength);
                if (epc != null)
                {
                    return epc;
                }
            }

            return null;
        }

        private static bool IsValidEpcByteLength(int byteLength)
        {
            return byteLength is >= 6 and <= 32;
        }

        private static bool IsValidEpcHexLength(int hexLength)
        {
            return hexLength % 2 == 0 && IsValidEpcByteLength(hexLength / 2);
        }

        private static bool LooksLikeReaderFrame(byte[] data)
        {
            return data.Length >= 7 && data[0] == 0xBB && data[^1] == 0x7E;
        }

        private void HandleUsbTags(List<string> epcs)
        {
            if (!_usbReadActive)
            {
                return;
            }

            if (DateTime.UtcNow < _ignoreUsbTagsUntilUtc)
            {
                lblDebug.Text = "Stop trigger: trailing tag ignored.";
                return;
            }

            if (rbSingle.IsChecked == true)
            {
                string? epc = epcs.FirstOrDefault(epc => !IsDuplicateSessionEpc(epc));
                if (epc == null && epcs.Count == 0)
                {
                    ResetPendingTriggerLearning();
                    lblReadStatus.Text = "Single: no tag, ready for next scan";
                    lblReadStatus.Foreground = Brushes.Orange;
                    lblDebug.Text = string.IsNullOrEmpty(_lastUsbRawDebug)
                        ? $"Single read found no tag. {_device.LastError}"
                        : $"Single read found no EPC. Raw USB: {_lastUsbRawDebug}. {_device.LastError}";
                    return;
                }

                if (epc == null)
                {
                    PromotePendingTriggerIfSuccessfulRead();
                    ResetSingleScannerInputGate();
                    lblReadStatus.Text = "Single: already scanned, ready for next scan";
                    lblReadStatus.Foreground = Brushes.Orange;
                    lblDebug.Text = "Already scanned EPC ignored.";
                    return;
                }

                PromotePendingTriggerIfSuccessfulRead();
                RememberSingleEpc(epc);
                ShowCurrentEpc(epc);
                AddTagIfNew(epc);
                SendEpcToActiveApp(epc);
                PlaySingleReadBeep();
                UpdateTagCount();
                lblReadStatus.Text = $"Single: read 1 tag, ready for next scan";
                lblReadStatus.Foreground = Brushes.Green;
                lblDebug.Text = string.IsNullOrEmpty(_lastUsbRawDebug)
                    ? $"EPC received: {epc}. {_device.LastError}"
                    : $"EPC received: {epc}. Raw USB: {_lastUsbRawDebug}. {_device.LastError}";
                return;
            }

            if (epcs.Count == 0)
            {
                lblDebug.Text = string.IsNullOrEmpty(_lastUsbRawDebug)
                    ? $"Continuous reading... waiting for tags. {_device.LastError}"
                    : $"Continuous reading... waiting for EPC. Raw USB: {_lastUsbRawDebug}. {_device.LastError}";
                return;
            }

            var distinctEpcs = epcs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var newEpcs = distinctEpcs
                .Where(epc => !IsDuplicateSessionEpc(epc))
                .ToList();

            if (newEpcs.Count == 0)
            {
                PromotePendingTriggerIfSuccessfulRead();
                lblReadStatus.Text = $"Continuous: reading ({GetDisplayedUniqueTagCount()} unique tag)";
                lblReadStatus.Foreground = Brushes.Green;
                lblDebug.Text = "Already scanned EPC ignored.";
                return;
            }

            PromotePendingTriggerIfSuccessfulRead();
            ShowCurrentEpc(newEpcs[^1]);
            foreach (var epc in newEpcs)
            {
                AddTagIfNew(epc);
                SendEpcToActiveApp(epc);
            }
            PlayContinuousReadBeeps(newEpcs.Count);
            UpdateTagCount();
            lblReadStatus.Text = $"Continuous: reading ({GetDisplayedUniqueTagCount()} unique tag)";
            lblReadStatus.Foreground = Brushes.Green;
            lblDebug.Text = string.IsNullOrEmpty(_lastUsbRawDebug)
                ? $"Last EPC batch: {newEpcs.Count} new tag"
                : $"Last EPC batch: {newEpcs.Count} new tag. Raw USB: {_lastUsbRawDebug}";
        }

        private void ProcessCurrentInput()
        {
            string raw = txtEpcInput.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var epcs = raw
                .Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeEpc)
                .Where(epc => epc != null)
                .Select(epc => epc!)
                .Distinct()
                .ToList();

            if (epcs.Count == 0)
            {
                ClearInput();
                return;
            }

            var newEpcs = epcs
                .Where(epc => !IsDuplicateSessionEpc(epc))
                .ToList();

            if (newEpcs.Count == 0)
            {
                lblDebug.Text = "Already scanned EPC ignored.";
                ClearInput();
                FocusInternalInput();
                return;
            }

            if (rbSingle.IsChecked == true)
            {
                RememberSingleEpc(newEpcs[0]);
                ShowCurrentEpc(newEpcs[0]);
                AddTagIfNew(newEpcs[0]);
                PlaySingleReadBeep();
                if (_device.IsConnected)
                {
                    int stopResult = _device.SendInventoryCommand(false);
                    if (stopResult != 0)
                    {
                        lblDebug.Text = $"Inventory stop failed: {stopResult}. {_device.LastError}";
                    }
                }
                StopUsbRead();
                UpdateTagCount();
                ClearInput();
                FocusInternalInput();
                return;
            }
            else
            {
                ShowCurrentEpc(newEpcs[^1]);
                foreach (var epc in newEpcs)
                {
                    AddTagIfNew(epc);
                }
            }

            ClearInput();
            UpdateTagCount();
            FocusInternalInput();
        }

        private void PlaySingleReadBeep()
        {
            if (rbSingle.IsChecked != true)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastSingleBeepUtc).TotalMilliseconds < 900)
            {
                return;
            }

            _lastSingleBeepUtc = now;

            if (OperatingSystem.IsWindows())
            {
                _ = Task.Run(WindowsKeyboard.PlaySingleBeep);
            }
        }

        private static void PlayContinuousReadBeeps(int count)
        {
            if (count <= 0 || !OperatingSystem.IsWindows())
            {
                return;
            }

            int beepCount = Math.Min(count, 5);
            _ = Task.Run(() =>
            {
                for (int i = 0; i < beepCount; i++)
                {
                    WindowsKeyboard.PlaySingleBeep();
                    Thread.Sleep(25);
                }
            });
        }

        private static string? NormalizeEpc(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string epc = new string(raw
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

            epc = CollapsePairedRawInputCharacters(epc);
            epc = CollapseRepeatedEpc(epc);

            if (epc.Length > 0 && epc.Length % 2 != 0)
            {
                epc = "0" + epc;
            }

            epc = CanonicalizeEpc(epc);

            if (!IsValidEpcHexLength(epc.Length))
            {
                return null;
            }

            if (epc.All(c => c == '0') || epc.All(c => c == 'F'))
            {
                return null;
            }

            return epc;
        }

        private static string? NormalizeUsbEpc(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string epc = new string(raw
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

            if (epc.Length > 0 && epc.Length % 2 != 0)
            {
                epc = "0" + epc;
            }

            epc = CanonicalizeEpc(epc);

            if (!IsValidEpcHexLength(epc.Length))
            {
                return null;
            }

            if (epc.All(c => c == '0') || epc.All(c => c == 'F'))
            {
                return null;
            }

            return epc;
        }

        private static string CanonicalizeEpc(string epc)
        {
            epc = CollapseRepeatedEpc(epc);
            epc = CollapseRepeatedExpectedEpc(epc);
            epc = StripPcWordIfPresent(epc);
            if (epc.Length > ExpectedEpcHexLength)
            {
                epc = ExtractBestEpcCandidate(epc);
            }

            if (epc.Length > ExpectedEpcHexLength)
            {
                epc = epc[^ExpectedEpcHexLength..];
            }

            return epc;
        }

        private static string CollapseRepeatedExpectedEpc(string epc)
        {
            if (epc.Length <= ExpectedEpcHexLength || epc.Length % ExpectedEpcHexLength != 0)
            {
                return epc;
            }

            string first = epc[..ExpectedEpcHexLength];
            for (int i = ExpectedEpcHexLength; i < epc.Length; i += ExpectedEpcHexLength)
            {
                if (!epc.Substring(i, ExpectedEpcHexLength).Equals(first, StringComparison.Ordinal))
                {
                    return epc;
                }
            }

            return first;
        }

        private static string StripPcWordIfPresent(string epc)
        {
            if (epc.Length < 12 || epc.Length % 2 != 0)
            {
                return epc;
            }

            if (!ushort.TryParse(epc[..4], System.Globalization.NumberStyles.HexNumber, null, out ushort pc))
            {
                return epc;
            }

            int epcByteLengthFromPc = ((pc >> 11) & 0x1F) * 2;
            int epcHexLengthFromPc = epcByteLengthFromPc * 2;
            bool pcLengthMatchesPayload = epcByteLengthFromPc >= 6
                && epcByteLengthFromPc <= 32
                && epc.Length == 4 + epcHexLengthFromPc;

            return pcLengthMatchesPayload ? epc[4..] : epc;
        }

        private static string CollapsePairedRawInputCharacters(string epc)
        {
            if (epc.Length <= ExpectedEpcHexLength || epc.Length % 2 != 0)
            {
                return epc;
            }

            for (int i = 0; i < epc.Length; i += 2)
            {
                if (epc[i] != epc[i + 1])
                {
                    return epc;
                }
            }

            var builder = new StringBuilder(epc.Length / 2);
            for (int i = 0; i < epc.Length; i += 2)
            {
                builder.Append(epc[i]);
            }

            return builder.ToString();
        }

        private static string ExtractBestEpcCandidate(string epc)
        {
            if (epc.Length <= 8)
            {
                return epc;
            }

            int maxCandidateLength = Math.Min(32, epc.Length);
            if (maxCandidateLength % 2 != 0)
            {
                maxCandidateLength--;
            }

            for (int candidateLength = 8; candidateLength <= maxCandidateLength; candidateLength += 2)
            {
                string candidate = epc[^candidateLength..];
                string prefix = epc[..^candidateLength];
                if (PrefixMatchesCandidate(prefix, candidate))
                {
                    return candidate;
                }
            }

            return epc;
        }

        private static bool PrefixMatchesCandidate(string prefix, string candidate)
        {
            if (prefix.Length == 0)
            {
                return false;
            }

            string reduced = prefix.Trim('0');
            if (reduced.Length == 0)
            {
                return true;
            }

            while (reduced.Length >= candidate.Length && reduced.EndsWith(candidate, StringComparison.Ordinal))
            {
                reduced = reduced[..^candidate.Length].Trim('0');
                if (reduced.Length == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string CollapseRepeatedEpc(string epc)
        {
            while (epc.Length >= 16 && epc.Length % 2 == 0)
            {
                int halfLength = epc.Length / 2;
                if (!epc[..halfLength].Equals(epc[halfLength..], StringComparison.Ordinal))
                {
                    break;
                }

                epc = epc[..halfLength];
            }

            return epc;
        }

        private static string TrimLeadingZeroPairs(string epc)
        {
            while (epc.Length > 8 && epc.StartsWith("00", StringComparison.Ordinal))
            {
                epc = epc[2..];
            }

            return epc;
        }

        private bool ShouldIgnoreSingleDuplicate(string epc)
        {
            return _lastSingleEpc == epc
                && (DateTime.UtcNow - _lastSingleEpcUtc).TotalMilliseconds < SingleDuplicateIgnoreMs;
        }

        private void RememberSingleEpc(string epc)
        {
            _lastSingleEpc = epc;
            _lastSingleEpcUtc = DateTime.UtcNow;
        }

        private bool IsDuplicateSessionEpc(string epc)
        {
            if (string.IsNullOrWhiteSpace(epc))
            {
                return true;
            }

            ReconcileTagMapWithDisplayedTags();
            return tagMap.ContainsKey(epc);
        }

        private bool AddTagIfNew(string epc)
        {
            ReconcileTagMapWithDisplayedTags();
            if (tagMap.ContainsKey(epc))
            {
                return false;
            }

            var newEntry = new TagEntry { Index = tags.Count + 1, Epc = epc, Count = 1 };
            tagMap[epc] = newEntry;
            tags.Add(newEntry);
            return true;
        }

        private void ClearInput()
        {
            _suppressTextChanged = true;
            txtEpcInput.Text = string.Empty;
            _suppressTextChanged = false;
        }

        private void ShowCurrentEpc(string epc)
        {
            _suppressTextChanged = true;
            txtEpcInput.Text = epc;
            _suppressTextChanged = false;
        }

        private void FocusInternalInput(bool force = false)
        {
            if (force || chkSendToActiveApp.IsChecked != true || _externalOutputTargetWindow == IntPtr.Zero)
            {
                if (force && OperatingSystem.IsWindows())
                {
                    IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    WindowsKeyboard.FocusTargetWindow(appWindow);
                }

                txtEpcInput.Focus();
            }
        }

        private void UpdateTagCount()
        {
            ReconcileTagMapWithDisplayedTags();
            lblTagCount.Text = $"Total: {GetDisplayedUniqueTagCount()} unique tag";
        }

        private int GetDisplayedUniqueTagCount()
        {
            return tags.Count;
        }

        private void ReconcileTagMapWithDisplayedTags()
        {
            if (tagMap.Count == tags.Count)
            {
                return;
            }

            tagMap.Clear();
            for (int i = 0; i < tags.Count; i++)
            {
                tags[i].Index = i + 1;
                tagMap[tags[i].Epc] = tags[i];
            }
        }

        private void SendEpcToActiveApp(string epc)
        {
            if (chkSendToActiveApp.IsChecked != true)
            {
                return;
            }

            if (_externalOutputTargetWindow == IntPtr.Zero)
            {
                IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                KeyboardTarget externalTarget = WindowsKeyboard.GetCurrentExternalInputTarget(appWindow);
                if (externalTarget.Window == IntPtr.Zero)
                {
                    externalTarget = WindowsKeyboard.TryFindExternalOutputTarget();
                }

                if (externalTarget.Window == IntPtr.Zero)
                {
                    return;
                }

                _externalOutputTargetWindow = externalTarget.Window;
                _externalOutputTargetControl = externalTarget.Control;
            }

            if (!OperatingSystem.IsWindows())
            {
                lblDebug.Text = "External app output is only supported on Windows.";
                return;
            }

            try
            {
                if (ShouldIgnoreForwardedDuplicate(epc))
                {
                    return;
                }

                ArmScannerKeyboardSuppression(
                    _externalOutputTargetWindow,
                    keepWhileReading: _usbReadActive && rbContinuous.IsChecked == true,
                    durationMs: rbSingle.IsChecked == true ? ExternalOutputSuppressionMs : ScannerKeyboardSuppressionMs);
                WindowsKeyboard.SendTextLine(_externalOutputTargetWindow, _externalOutputTargetControl, epc);
                RememberForwardedEpc(epc);
                if (WindowsKeyboard.ShouldCleanupDuplicateLines(_externalOutputTargetWindow))
                {
                    WindowsKeyboard.RemoveDuplicateLines(_externalOutputTargetWindow, _externalOutputTargetControl);
                }

                if (_usbReadActive && rbContinuous.IsChecked == true)
                {
                    FocusInternalInput(force: true);
                }
                ArmScannerKeyboardSuppression(
                    _externalOutputTargetWindow,
                    keepWhileReading: _usbReadActive && rbContinuous.IsChecked == true,
                    durationMs: rbSingle.IsChecked == true ? ExternalOutputSuppressionMs : ScannerKeyboardSuppressionMs);
            }
            catch (Exception ex)
            {
                lblDebug.Text = $"External output failed: {ex.GetType().Name}: {ex.Message}";
                _externalOutputTargetWindow = IntPtr.Zero;
                _externalOutputTargetControl = IntPtr.Zero;
            }
        }

        private void SendEpcToActiveAppIfAppFocused(string epc)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            IntPtr appWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            KeyboardTarget target = WindowsKeyboard.GetCurrentInputTarget();
            if (target.Window != IntPtr.Zero && target.Window != appWindow)
            {
                _externalOutputTargetWindow = target.Window;
                _externalOutputTargetControl = target.Control;
                SendEpcToActiveApp(epc);
                return;
            }

            if (target.Window != appWindow)
            {
                return;
            }

            SendEpcToActiveApp(epc);
        }

        private void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            int result = _device.ConnectUsb();
            if (result == 0)
            {
                lblStatus.Text = "USB connected";
                lblStatus.Foreground = Brushes.Green;
                btnConnect.IsEnabled = false;
                btnDisconnect.IsEnabled = true;
                pnlAntenna.IsEnabled = true;
                pnlPower.IsEnabled = true;
                pnlAntenna.Opacity = 1;
                pnlPower.Opacity = 1;
                UpdateConnectionUi();
                BtnRefresh_Click(sender, e);
                ReadMode_Changed(sender, e);
            }
            else
            {
                lblStatus.Text = $"USB connect failed ({result})";
                lblStatus.Foreground = Brushes.Red;
                lblDebug.Text = _device.LastError;
            }
        }

        private void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
        {
            _inputTimer.Stop();
            if (_device.IsConnected)
            {
                _device.SendInventoryCommand(false);
            }
            StopUsbRead();
            _device.Disconnect();
            btnConnect.IsEnabled = true;
            btnDisconnect.IsEnabled = false;
            UpdateConnectionUi();
        }

        private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
        {
            byte power = 0;
            int result = _device.GetPower(ref power);
            if (result == 0)
            {
                SyncPowerValue(power);
                lblCurrentPower.Text = $"Current power: {power} dBm";
                lblDebug.Text = string.Empty;
            }
            else
            {
                lblDebug.Text = $"Power read failed: {result}";
            }
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            byte power = (byte)Math.Clamp((int)Math.Round(trackPower.Value), 5, 30);
            int result = _device.SetPower(power);
            if (result == 0)
            {
                lblCurrentPower.Text = $"Current power: {power} dBm";
                lblDebug.Text = $"Saved: {power} dBm";
            }
            else
            {
                lblDebug.Text = $"Power save failed: {result}";
            }
        }

        private void BtnClearList_Click(object? sender, RoutedEventArgs e)
        {
            tags.Clear();
            tagMap.Clear();
            _lastSingleEpc = null;
            _lastSingleEpcUtc = DateTime.MinValue;
            _forwardedSessionEpcs.Clear();
            UpdateTagCount();
            FocusInternalInput();
        }

        private void SyncPowerValue(double value)
        {
            double rounded = Math.Clamp(Math.Round(value), 5, 30);
            if (Math.Abs(trackPower.Value - rounded) > 0.01)
            {
                trackPower.Value = rounded;
            }

            if (numPower.Value != (decimal)rounded)
            {
                numPower.Value = (decimal)rounded;
            }

            lblPowerValue.Text = $"{rounded:0} dBm";
        }

        private bool ShouldIgnoreForwardedDuplicate(string epc)
        {
            return _forwardedSessionEpcs.Contains(epc);
        }

        private void RememberForwardedEpc(string epc)
        {
            _forwardedSessionEpcs.Add(epc);
        }

        private const uint WM_INPUT = 0x00FF;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const int GWL_WNDPROC = -4;

        private delegate IntPtr RawInputWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public RawInputDeviceFlags dwFlags;
            public IntPtr hwndTarget;
        }

        [Flags]
        private enum RawInputDeviceFlags : uint
        {
            RIDEV_INPUTSINK = 0x00000100
        }

        private enum RawInputType : uint
        {
            RIM_TYPEKEYBOARD = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public RawInputType dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWKEYBOARD keyboard;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : SetWindowLong32(hWnd, nIndex, dwNewLong);
        }
    }

    public class TagEntry
    {
        public int Index { get; set; }
        public string Epc { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    internal readonly record struct KeyboardTarget(IntPtr Window, IntPtr Control);

    internal static class WindowsKeyboard
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const int WM_CHAR = 0x0102;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const int WM_SETTEXT = 0x000C;
        private const int EM_REPLACESEL = 0x00C2;
        public const int VK_RETURN = 0x0D;
        public const int VK_F11_TRIGGER = 0x7A;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private static readonly IntPtr APP_FORWARDED_INPUT_MARKER = new(unchecked((int)0x53433136));

        public static KeyboardTarget GetCurrentInputTarget()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return default;
            }

            uint thread = GetWindowThreadProcessId(window, IntPtr.Zero);
            var info = new GUITHREADINFO
            {
                cbSize = Marshal.SizeOf<GUITHREADINFO>()
            };

            IntPtr control = GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero
                ? info.hwndFocus
                : window;

            return new KeyboardTarget(window, control);
        }

        public static KeyboardTarget TryFindExternalOutputTarget()
        {
            try
            {
                IntPtr window = FindMainWindowByProcessName("notepad");
                if (window != IntPtr.Zero)
                {
                    IntPtr editControl = FindEditableChild(window);
                    return new KeyboardTarget(window, editControl != IntPtr.Zero ? editControl : window);
                }

                window = FindMainWindowByProcessName("EXCEL");
                if (window == IntPtr.Zero)
                {
                    return default;
                }

                return new KeyboardTarget(window, window);
            }
            catch
            {
                return default;
            }
        }

        public static KeyboardTarget GetCurrentExternalInputTarget(IntPtr appWindow)
        {
            KeyboardTarget target = GetCurrentInputTarget();
            if (target.Window == IntPtr.Zero || target.Window == appWindow)
            {
                return default;
            }

            return target;
        }

        private static IntPtr FindMainWindowByProcessName(string processName)
        {
            return Process.GetProcessesByName(processName)
                .Select(process => process.MainWindowHandle)
                .FirstOrDefault(handle => handle != IntPtr.Zero && IsWindow(handle));
        }

        public static bool IsIgnoredTriggerKey(int virtualKey)
        {
            return virtualKey is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
        }

        public static bool IsDirectTriggerKey(int virtualKey)
        {
            return virtualKey == VK_F11_TRIGGER;
        }

        public static bool IsScannerTriggerKey(int virtualKey)
        {
            return virtualKey is >= 0x70 and <= 0x87;
        }

        public static bool IsBootstrapGlobalTriggerKey(int virtualKey)
        {
            return IsScannerTriggerKey(virtualKey);
        }

        public static bool TryMapAvaloniaKeyToVirtualKey(Key key, out int virtualKey)
        {
            virtualKey = key switch
            {
                Key.F1 => 0x70,
                Key.F2 => 0x71,
                Key.F3 => 0x72,
                Key.F4 => 0x73,
                Key.F5 => 0x74,
                Key.F6 => 0x75,
                Key.F7 => 0x76,
                Key.F8 => 0x77,
                Key.F9 => 0x78,
                Key.F10 => 0x79,
                Key.F11 => 0x7A,
                Key.F12 => 0x7B,
                Key.Tab => 0x09,
                Key.Enter => 0x0D,
                Key.Escape => 0x1B,
                Key.Space => 0x20,
                _ => 0,
            };

            return virtualKey != 0;
        }

        public static bool IsLikelyScannerDataKey(int virtualKey)
        {
            return virtualKey switch
            {
                >= 0x30 and <= 0x39 => true,
                >= 0x60 and <= 0x69 => true,
                >= 0x41 and <= 0x5A => true,
                0x6D => true,
                0xBD => true,
                0x20 => true,
                VK_RETURN => true,
                _ => false,
            };
        }

        public static bool IsAppForwardedInput(IntPtr extraInfo)
        {
            return extraInfo == APP_FORWARDED_INPUT_MARKER;
        }

        public static void PlaySingleBeep()
        {
            if (!Beep(950, 120))
            {
                MessageBeep(0);
            }
        }

        public static void SendTextLine(IntPtr targetWindow, IntPtr targetControl, string text)
        {
            IntPtr hwnd = targetWindow != IntPtr.Zero ? targetWindow : GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                throw new InvalidOperationException("No foreground window.");
            }

            bool excelTarget = IsExcelWindow(hwnd);
            bool textEditorTarget = IsTextEditorWindow(hwnd);
            string payload = text + "\r\n";

            if (targetControl != IntPtr.Zero
                && IsWindow(targetControl)
                && SupportsDirectTextInsert(targetControl))
            {
                SendMessage(targetControl, EM_REPLACESEL, (IntPtr)1, payload);
                Thread.Sleep(40);
                if (excelTarget)
                {
                    SendReturnKey();
                }
                return;
            }

            if (!excelTarget && !textEditorTarget && targetControl != IntPtr.Zero && IsWindow(targetControl))
            {
                SendCharactersToControl(targetControl, text);
                SendMessage(targetControl, WM_CHAR, (IntPtr)'\r', IntPtr.Zero);
                Thread.Sleep(40);
                return;
            }

            FocusWindow(hwnd);

            if (!excelTarget && !textEditorTarget)
            {
                var inputs = new List<INPUT>();
                foreach (char c in text)
                {
                    AddCharacterInputs(inputs, c);
                }
                inputs.Add(CreateVirtualKeyInput((ushort)VK_RETURN, keyUp: false));
                inputs.Add(CreateVirtualKeyInput((ushort)VK_RETURN, keyUp: true));

                uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
                if (sent != inputs.Count)
                {
                    throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Count} events.");
                }

                Thread.Sleep(80);
                return;
            }

            SetClipboardText(payload);
            SendPasteShortcut();
            Thread.Sleep(60);

            if (excelTarget)
            {
                SendReturnKey();
            }

            Thread.Sleep(80);
        }

        public static void RemoveDuplicateLines(IntPtr targetWindow, IntPtr targetControl)
        {
            IntPtr hwnd = ResolveTextTarget(targetWindow, targetControl);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            string text = GetControlText(hwnd);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string[] lines = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();
            bool changed = false;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    if (line.Length > 0)
                    {
                        kept.Add(line);
                    }
                    continue;
                }

                if (seen.Add(trimmed))
                {
                    kept.Add(line);
                }
                else
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            string cleaned = string.Join("\r\n", kept.Where(line => line.Length > 0));
            if (text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n'))
            {
                cleaned += "\r\n";
            }

            SendMessage(hwnd, WM_SETTEXT, IntPtr.Zero, cleaned);
        }

        private static IntPtr ResolveTextTarget(IntPtr targetWindow, IntPtr targetControl)
        {
            if (targetControl != IntPtr.Zero && IsWindow(targetControl) && SupportsDirectTextInsert(targetControl))
            {
                return targetControl;
            }

            if (targetWindow != IntPtr.Zero && IsWindow(targetWindow))
            {
                IntPtr edit = FindEditableChild(targetWindow);
                if (edit != IntPtr.Zero)
                {
                    return edit;
                }

                if (SupportsDirectTextInsert(targetWindow))
                {
                    return targetWindow;
                }
            }

            return IntPtr.Zero;
        }

        private static string GetControlText(IntPtr hwnd)
        {
            int length = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (length <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(length + 1);
            SendMessage(hwnd, WM_GETTEXT, (IntPtr)builder.Capacity, builder);
            return builder.ToString();
        }

        private static void SendCharactersToControl(IntPtr targetControl, string text)
        {
            foreach (char c in text)
            {
                SendMessage(targetControl, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            }
        }

        private static bool SupportsDirectTextInsert(IntPtr hwnd)
        {
            var className = new StringBuilder(128);
            int length = GetClassName(hwnd, className, className.Capacity);
            if (length <= 0)
            {
                return false;
            }

            string controlClass = className.ToString();
            return controlClass.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                || controlClass.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
        }

        private static IntPtr FindEditableChild(IntPtr parent)
        {
            IntPtr result = IntPtr.Zero;
            EnumChildWindows(parent, (hwnd, _) =>
            {
                if (SupportsDirectTextInsert(hwnd))
                {
                    result = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static bool IsTextEditorWindow(IntPtr hwnd)
        {
            string? processName = TryGetProcessName(hwnd);
            return processName is not null
                && (processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                    || processName.Equals("notepad++", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsSupportedExternalOutputTarget(IntPtr hwnd)
        {
            return IsTextEditorWindow(hwnd) || IsExcelWindow(hwnd);
        }

        public static bool ShouldCleanupDuplicateLines(IntPtr hwnd)
        {
            return IsTextEditorWindow(hwnd);
        }

        private static bool IsExcelWindow(IntPtr hwnd)
        {
            string? processName = TryGetProcessName(hwnd);
            return processName is not null
                && processName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryGetProcessName(IntPtr hwnd)
        {
            try
            {
                uint processId;
                GetWindowThreadProcessId(hwnd, out processId);
                if (processId == 0)
                {
                    return null;
                }

                using Process process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        private static void FocusWindow(IntPtr hwnd)
        {
            ShowWindow(hwnd, SW_RESTORE);

            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            uint currentThread = GetCurrentThreadId();

            bool attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            bool attachedTarget = targetThread != 0 && targetThread != currentThread
                && AttachThreadInput(currentThread, targetThread, true);

            try
            {
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attachedTarget)
                {
                    AttachThreadInput(currentThread, targetThread, false);
                }

                if (attachedForeground)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            Thread.Sleep(80);
        }

        public static void FocusTargetWindow(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                FocusWindow(hwnd);
            }
        }

        private static void SendReturnKey()
        {
            var inputs = new[]
            {
                CreateVirtualKeyInput((ushort)VK_RETURN, keyUp: false),
                CreateVirtualKeyInput((ushort)VK_RETURN, keyUp: true)
            };

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events.");
            }
        }

        private static void SendPasteShortcut()
        {
            var inputs = new[]
            {
                CreateVirtualKeyInput(VK_CONTROL, keyUp: false),
                CreateVirtualKeyInput(VK_V, keyUp: false),
                CreateVirtualKeyInput(VK_V, keyUp: true),
                CreateVirtualKeyInput(VK_CONTROL, keyUp: true)
            };

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events.");
            }
        }

        private static void SetClipboardText(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(20);
                    continue;
                }

                try
                {
                    if (!EmptyClipboard())
                    {
                        throw new InvalidOperationException("EmptyClipboard failed.");
                    }

                    byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                    if (hGlobal == IntPtr.Zero)
                    {
                        throw new OutOfMemoryException("GlobalAlloc failed.");
                    }

                    IntPtr locked = IntPtr.Zero;
                    try
                    {
                        locked = GlobalLock(hGlobal);
                        if (locked == IntPtr.Zero)
                        {
                            throw new InvalidOperationException("GlobalLock failed.");
                        }

                        Marshal.Copy(bytes, 0, locked, bytes.Length);
                    }
                    finally
                    {
                        if (locked != IntPtr.Zero)
                        {
                            GlobalUnlock(hGlobal);
                        }
                    }

                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        throw new InvalidOperationException("SetClipboardData failed.");
                    }

                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            throw new InvalidOperationException("Clipboard is busy.");
        }

        private static void AddCharacterInputs(List<INPUT> inputs, char character)
        {
            if (character is >= '0' and <= '9')
            {
                ushort key = (ushort)character;
                inputs.Add(CreateVirtualKeyInput(key, keyUp: false));
                inputs.Add(CreateVirtualKeyInput(key, keyUp: true));
                return;
            }

            char upper = char.ToUpperInvariant(character);
            if (upper is >= 'A' and <= 'Z')
            {
                ushort key = (ushort)upper;
                inputs.Add(CreateVirtualKeyInput(VK_SHIFT, keyUp: false));
                inputs.Add(CreateVirtualKeyInput(key, keyUp: false));
                inputs.Add(CreateVirtualKeyInput(key, keyUp: true));
                inputs.Add(CreateVirtualKeyInput(VK_SHIFT, keyUp: true));
                return;
            }

            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        private static INPUT CreateVirtualKeyInput(ushort key, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = key,
                        wScan = '\0',
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        dwExtraInfo = APP_FORWARDED_INPUT_MARKER
                    }
                }
            };
        }

        private static INPUT CreateUnicodeInput(char character, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = character,
                        dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                        dwExtraInfo = APP_FORWARDED_INPUT_MARKER
                    }
                }
            };
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MessageBeep(uint uType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Beep(uint dwFreq, uint dwDuration);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public char wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    internal sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int LLKHF_INJECTED = 0x10;
        private readonly Func<int, bool> _onKeyDown;
        private readonly LowLevelKeyboardProc _proc;
        private readonly IntPtr _hookId;
        public bool IsInstalled => _hookId != IntPtr.Zero;

        public GlobalKeyboardHook(Func<int, bool> onKeyDown)
        {
            _onKeyDown = onKeyDown;
            _proc = HookCallback;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (WindowsKeyboard.IsAppForwardedInput(info.dwExtraInfo))
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                bool suppress = _onKeyDown(info.vkCode);
                if (suppress)
                {
                    return 1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
