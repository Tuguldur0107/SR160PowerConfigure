using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace SR160PowerConfig
{
    public class TagEntry : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string Epc { get; set; } = "";

        private int _count;
        public int Count
        {
            get => _count;
            set { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private readonly ChainwayProtocol _device = new();
        private bool suppressSync;
        private bool _usbConnected;
        private readonly Dictionary<string, TagEntry> tagMap = new();
        private readonly ObservableCollection<TagEntry> tags = new();

        // Timer-based auto-process
        private DispatcherTimer? _inputTimer;
        private bool _suppressTextChanged;

        // Settings
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sr160config.txt");

        public MainWindow()
        {
            InitializeComponent();
            dgTags.ItemsSource = tags;
            trackPower.PropertyChanged += TrackPower_PropertyChanged;
            numPower.ValueChanged += NumPower_ValueChanged;

            _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _inputTimer.Tick += InputTimer_Tick;

            LoadSettings();
        }

        // ── Settings хадгалах / ачаалах ──

        private void SaveSettings()
        {
            try
            {
                string mode = rbContinuous.IsChecked == true ? "continuous" : "single";
                File.WriteAllText(SettingsPath, mode);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string mode = File.ReadAllText(SettingsPath).Trim();
                    if (mode == "continuous")
                        rbContinuous.IsChecked = true;
                    else
                        rbSingle.IsChecked = true;
                }
            }
            catch { }
        }

        // ── Холболтын төрөл ──

        private void ConnType_Changed(object? sender, RoutedEventArgs e)
        {
            bool bt = rbBluetooth.IsChecked == true;
            lblBtHint.IsVisible = bt;

            if (bt)
            {
                // Bluetooth HID: USB холболт шаардлагагүй, EPC шууд ирнэ
                pnlUsbButtons.IsVisible = false;
                lblStatus.Text = "● Bluetooth HID горим";
                lblStatus.Foreground = Brushes.Green;
                lblDebug.Text = "";
            }
            else
            {
                pnlUsbButtons.IsVisible = true;
                if (!_usbConnected)
                {
                    lblStatus.Text = "● Салсан";
                    lblStatus.Foreground = Brushes.Red;
                }
            }
        }

        // ── USB холбогдох / Салгах ──

        private void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ShowError("macOS дээр USB-ээр хүч тохируулах боломжгүй.\n\n"
                    + "SR160 firmware зөвхөн Windows дээр USB командыг дэмждэг.\n\n"
                    + "Хүч тохируулахын тулд Windows компьютер ашиглана уу.\n"
                    + "EPC уншилт macOS дээр Bluetooth/USB HID горимоор хэвийн ажиллана.");
                return;
            }

            try
            {
                int ret = _device.ConnectUsb();
                lblDebug.Text = _device.LastDebug;

                if (ret == 0)
                {
                    _usbConnected = true;
                    SetUsbState(true);
                    ReadCurrentPower();
                }
                else
                {
                    ShowError("USB төхөөрөмжтэй холбогдож чадсангүй.\nАлдааны код: " + ret
                        + "\n\nUSB кабелиар холбосон эсэхээ шалгана уу.");
                }
            }
            catch (Exception ex)
            {
                ShowError("Алдаа: " + ex.Message);
            }
        }

        private void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
        {
            _device.Disconnect();
            _usbConnected = false;
            SetUsbState(false);
            lblDebug.Text = "";
        }

        private void SetUsbState(bool connected)
        {
            lblStatus.Text = connected ? "● Холбогдсон (USB)" : "● Салсан";
            lblStatus.Foreground = connected ? Brushes.Green : Brushes.Red;
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;

            pnlAntenna.IsEnabled = connected;
            pnlAntenna.Opacity = connected ? 1.0 : 0.5;
            pnlPower.IsEnabled = connected;
            pnlPower.Opacity = connected ? 1.0 : 0.5;

            if (!connected)
            {
                lblCurrentPower.Text = "Одоогийн хүч:  —";
                lblPowerValue.Text = "— dBm";
            }
        }

        // ── Power ──

        private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
        {
            ReadCurrentPower();
        }

        private void ReadCurrentPower()
        {
            byte power = 0;
            int ret = _device.GetPower(ref power);
            lblDebug.Text = _device.LastDebug;

            if (ret == 0)
            {
                int val = Math.Max(5, Math.Min(30, (int)power));
                lblCurrentPower.Text = "Одоогийн хүч:  " + power + " dBm";

                suppressSync = true;
                trackPower.Value = val;
                numPower.Value = val;
                suppressSync = false;

                lblPowerValue.Text = power + " dBm";
            }
            else
            {
                lblCurrentPower.Text = "Одоогийн хүч:  уншиж чадсангүй (код: " + ret + ")";
            }
        }

        private void TrackPower_PropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name != "Value" || suppressSync) return;
            suppressSync = true;
            numPower.Value = (decimal)trackPower.Value;
            suppressSync = false;
            lblPowerValue.Text = (int)trackPower.Value + " dBm";
        }

        private void NumPower_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (suppressSync) return;
            suppressSync = true;
            trackPower.Value = (double)(numPower.Value ?? 20);
            suppressSync = false;
            lblPowerValue.Text = (int)(numPower.Value ?? 20) + " dBm";
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            byte power = (byte)(numPower.Value ?? 20);
            int ret = _device.SetPower(1, power);
            lblDebug.Text = _device.LastDebug;

            if (ret == 0)
            {
                ReadCurrentPower();
                ShowInfo("Хүчийг " + power + " dBm болгож хадгаллаа.");
            }
            else
            {
                ShowError("Хүч тохируулж чадсангүй.\nАлдааны код: " + ret);
            }
        }

        // ── EPC уншилт ──

        private void ReadMode_Changed(object? sender, RoutedEventArgs e)
        {
            lblReadStatus.Text = "";
            SaveSettings();
            txtEpcInput.Focus();
        }

        // Enter, Return, Tab дарахад EPC-г process хийх
        private void TxtEpcInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return || e.Key == Key.Tab)
            {
                e.Handled = true;
                _inputTimer?.Stop();
                ProcessCurrentInput();
            }
        }

        // Текст өөрчлөгдөхөд timer-г дахин эхлүүлэх (continuous mode-д auto-process)
        private void TxtEpcInput_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged) return;

            // Timer дахин эхлүүлэх — 200ms дотор шинэ keystroke ирэхгүй бол process хийнэ
            _inputTimer?.Stop();

            string text = txtEpcInput.Text ?? "";

            // Хэрэв текст дотор newline/return байвал шууд process хийх
            if (text.Contains('\n') || text.Contains('\r') || text.Contains('\t'))
            {
                ProcessCurrentInput();
                return;
            }

            // Текст байвал timer эхлүүлэх (auto-process 200ms-ийн дараа)
            if (text.Length > 0)
            {
                _inputTimer?.Start();
            }
        }

        private void InputTimer_Tick(object? sender, EventArgs e)
        {
            _inputTimer?.Stop();
            ProcessCurrentInput();
        }

        private void ProcessCurrentInput()
        {
            string raw = txtEpcInput.Text ?? "";

            // Бүх delimiter-уудыг салгах
            string[] parts = raw.Split(new[] { '\r', '\n', '\t', ' ' },
                                       StringSplitOptions.RemoveEmptyEntries);

            // Бүх ирсэн EPC-г жагсаалтад нэмнэ
            // Single/Continuous горим SR160 төхөөрөмж дээр удирдагдана
            foreach (string part in parts)
            {
                string epc = part.Trim().ToUpper();
                if (epc.Length == 0) continue;
                AddEpcToList(epc);
            }

            if (parts.Length > 0)
            {
                bool isSingle = rbSingle.IsChecked == true;
                if (isSingle)
                {
                    lblReadStatus.Text = "✓ Таг уншлаа";
                    lblReadStatus.Foreground = Brushes.Green;
                }
                else
                {
                    lblReadStatus.Text = "● Тасралтгүй уншиж байна...";
                    lblReadStatus.Foreground = Brushes.Green;
                }
            }

            // Текст цэвэрлэх
            _suppressTextChanged = true;
            txtEpcInput.Text = "";
            _suppressTextChanged = false;

            txtEpcInput.Focus();
        }

        private void AddEpcToList(string epc)
        {
            if (tagMap.TryGetValue(epc, out var existing))
            {
                existing.Count++;
            }
            else
            {
                var entry = new TagEntry
                {
                    Index = tags.Count + 1,
                    Epc = epc,
                    Count = 1
                };
                tagMap[epc] = entry;
                tags.Add(entry);
            }

            int total = 0;
            foreach (var t in tags) total += t.Count;
            lblTagCount.Text = $"Нийт: {tagMap.Count} таг, {total} удаа уншсан";

            if (tags.Count > 0)
                dgTags.ScrollIntoView(tags[tags.Count - 1], null);
        }

        private void BtnClearList_Click(object? sender, RoutedEventArgs e)
        {
            tags.Clear();
            tagMap.Clear();
            lblTagCount.Text = "Нийт: 0 таг";
            _suppressTextChanged = true;
            txtEpcInput.Text = "";
            _suppressTextChanged = false;
            txtEpcInput.Focus();
        }

        // ── Dialogs ──

        private async void ShowError(string message)
        {
            var dlg = new Window
            {
                Title = "Алдаа",
                Width = 420, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 15,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                        new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Width = 80 }
                    }
                }
            };
            ((Button)((StackPanel)dlg.Content).Children[1]).Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(this);
        }

        private async void ShowInfo(string message)
        {
            var dlg = new Window
            {
                Title = "Амжилттай",
                Width = 380, Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 15,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                        new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Width = 80 }
                    }
                }
            };
            ((Button)((StackPanel)dlg.Content).Children[1]).Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(this);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _inputTimer?.Stop();
            if (_usbConnected)
                _device.Dispose();
            base.OnClosing(e);
        }
    }
}


