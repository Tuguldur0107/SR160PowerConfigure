using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CompiledAvaloniaXaml;

namespace SR160PowerConfig;

public class MainWindow : Window
{
	private const int SingleReadDelayMs = 500;

	private const int UsbInitialReadDelayMs = 500;

	private const int ExternalOutputFocusDelayMs = 2000;

	private const int SingleLoopIntervalMs = 500;

	private const int UsbPollIntervalMs = 500;

	private const int SingleTriggerBurstMs = 180;

	private const int TriggerToggleDebounceMs = 700;

	private const int MaxUsbReadsPerPoll = 200;

	private readonly ChainwayProtocol _device = new ChainwayProtocol();

	private readonly Dictionary<string, TagEntry> tagMap = new Dictionary<string, TagEntry>();

	private readonly ObservableCollection<TagEntry> tags = new ObservableCollection<TagEntry>();

	private readonly DispatcherTimer _inputTimer;

	private readonly DispatcherTimer _usbReadTimer;

	private readonly DispatcherTimer _singleTriggerTimer;

	private readonly string settingsFile = Path.Combine(AppContext.BaseDirectory, "settings.txt");

	private bool _usbReadActive;

	private bool _usbPollInProgress;

	private bool _suppressTextChanged;

	private string _lastUsbRawDebug = string.Empty;

	private DateTime _lastUsbTriggerToggleUtc = DateTime.MinValue;

	private bool _singleTriggerQueued;

	private readonly GlobalKeyboardHook? _globalKeyboardHook;

	private nint _externalOutputTargetWindow;

	private nint _externalOutputTargetControl;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal RadioButton rbUsb;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal RadioButton rbBluetooth;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblBtHint;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblStatus;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal StackPanel pnlUsbButtons;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Button btnConnect;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Button btnDisconnect;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblDebug;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Border pnlAntenna;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblCurrentPower;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Button btnRefresh;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Border pnlPower;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Slider trackPower;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblPowerValue;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal NumericUpDown numPower;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Button btnSave;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal RadioButton rbSingle;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal RadioButton rbContinuous;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblReadStatus;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal CheckBox chkSendToActiveApp;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblTriggerDebug;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBox txtEpcInput;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Button btnStartRead;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal Button btnClearList;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal TextBlock lblTagCount;

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	internal DataGrid dgTags;

	[CompilerGenerated]
	private static Action<object> _0021XamlIlPopulateOverride;

	public MainWindow()
	{
		InitializeComponent();
		dgTags.ItemsSource = tags;
		AddHandler(InputElement.KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
		if (OperatingSystem.IsWindows())
		{
			_globalKeyboardHook = new GlobalKeyboardHook(OnGlobalKeyDown);
			lblTriggerDebug.Text = (_globalKeyboardHook.IsInstalled ? "Trigger key: global hook ready" : "Trigger key: global hook failed");
			base.Closed += delegate
			{
				_globalKeyboardHook?.Dispose();
			};
		}
		_inputTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(500L)
		};
		_inputTimer.Tick += delegate
		{
			_inputTimer.Stop();
			ProcessCurrentInput();
		};
		_usbReadTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(500L)
		};
		_usbReadTimer.Tick += async delegate
		{
			await PollUsbTagsAsync();
		};
		_singleTriggerTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(180L)
		};
		_singleTriggerTimer.Tick += delegate
		{
			_singleTriggerTimer.Stop();
			if (_singleTriggerQueued)
			{
				_singleTriggerQueued = false;
				ToggleUsbReadFromTrigger();
			}
		};
		trackPower.PropertyChanged += delegate
		{
			SyncPowerValue(trackPower.Value);
		};
		numPower.PropertyChanged += delegate
		{
			decimal? value = numPower.Value;
			if (value.HasValue)
			{
				decimal valueOrDefault = value.GetValueOrDefault();
				SyncPowerValue((double)valueOrDefault);
			}
		};
		LoadSettings();
		UpdateConnectionUi();
		UpdateReadModeUi();
		SyncPowerValue(trackPower.Value);
	}

	private void LoadSettings()
	{
		if (!File.Exists(settingsFile))
		{
			return;
		}
		string[] array = File.ReadAllLines(settingsFile);
		foreach (string text in array)
		{
			if (text.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
			{
				bool flag = text.Contains("Continuous", StringComparison.OrdinalIgnoreCase);
				rbContinuous.IsChecked = flag;
				rbSingle.IsChecked = !flag;
			}
			if (text.StartsWith("Conn:", StringComparison.OrdinalIgnoreCase))
			{
				bool flag2 = text.Contains("USB", StringComparison.OrdinalIgnoreCase);
				rbUsb.IsChecked = flag2;
				rbBluetooth.IsChecked = !flag2;
			}
		}
	}

	private void SaveSettings()
	{
		string text = ((rbContinuous.IsChecked == true) ? "Continuous" : "Single");
		string text2 = ((rbUsb.IsChecked == true) ? "USB" : "Bluetooth");
		File.WriteAllLines(settingsFile, new string[2]
		{
			"Mode:" + text,
			"Conn:" + text2
		});
	}

	private void ConnType_Changed(object? sender, RoutedEventArgs e)
	{
		UpdateConnectionUi();
		SaveSettings();
		FocusInternalInput();
	}

	private void UpdateConnectionUi()
	{
		bool valueOrDefault = rbBluetooth.IsChecked == true;
		UpdateSingleTriggerKeySuppression();
		lblBtHint.IsVisible = valueOrDefault;
		pnlUsbButtons.IsVisible = !valueOrDefault;
		pnlAntenna.IsEnabled = _device.IsConnected && !valueOrDefault;
		pnlPower.IsEnabled = _device.IsConnected && !valueOrDefault;
		pnlAntenna.Opacity = (pnlAntenna.IsEnabled ? 1.0 : 0.5);
		pnlPower.Opacity = (pnlPower.IsEnabled ? 1.0 : 0.5);
		if (valueOrDefault)
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
			int num = _device.SendInventoryCommand(start: false);
			if (num != 0)
			{
				lblDebug.Text = $"Inventory stop failed: {num}. {_device.LastError}";
			}
			ApplyScannerBuzzerForReadMode();
		}
		FocusInternalInput();
	}

	private void UpdateReadModeUi()
	{
		bool valueOrDefault = rbContinuous.IsChecked == true;
		UpdateSingleTriggerKeySuppression();
		_inputTimer.Stop();
		StopUsbRead();
		_inputTimer.Interval = TimeSpan.FromMilliseconds(500L);
		lblReadStatus.Text = (valueOrDefault ? "Continuous: trigger toggles start/stop" : "Single: trigger starts/stops slow loop");
		lblReadStatus.Foreground = (valueOrDefault ? Brushes.Green : Brushes.Orange);
		btnStartRead.Content = "Start";
	}

	private void UpdateSingleTriggerKeySuppression()
	{
	}

	private void ApplyScannerBuzzerForReadMode()
	{
		if (_device.IsConnected && rbUsb.IsChecked == true)
		{
			bool valueOrDefault = rbSingle.IsChecked == true;
			int num = _device.SetBuzzer(!valueOrDefault);
			if (num != 0)
			{
				lblDebug.Text = (valueOrDefault ? $"Single buzzer-off failed: {num}. {_device.LastError}" : $"Continuous buzzer-on failed: {num}. {_device.LastError}");
			}
		}
	}

	private void TxtEpcInput_TextChanged(object? sender, TextChangedEventArgs e)
	{
		if (_suppressTextChanged)
		{
			return;
		}
		if (rbUsb.IsChecked == true && _device.IsConnected)
		{
			if (!string.IsNullOrWhiteSpace(txtEpcInput.Text))
			{
				ClearInput();
			}
		}
		else
		{
			_inputTimer.Stop();
			_inputTimer.Interval = TimeSpan.FromMilliseconds(500L);
			_inputTimer.Start();
		}
	}

	private void TxtEpcInput_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Return)
		{
			return;
		}
		_inputTimer.Stop();
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
		if (ShouldToggleUsbReadFromScannerKey(e.Key))
		{
			lblTriggerDebug.Text = $"Trigger key: {e.Key}";
			HandleScannerTriggerKey();
			e.Handled = true;
		}
	}

	private bool ShouldToggleUsbReadFromScannerKey(Key key)
	{
		if (rbUsb.IsChecked == true && _device.IsConnected)
		{
			return IsScannerTriggerKey(key);
		}
		return false;
	}

	private static bool IsScannerTriggerKey(Key key)
	{
		if (key != Key.LeftShift && key != Key.RightShift && key != Key.LeftCtrl && key != Key.RightCtrl && key != Key.LeftAlt && key != Key.RightAlt && key != Key.Return && key != Key.Tab)
		{
			return key != Key.Escape;
		}
		return false;
	}

	private bool OnGlobalKeyDown(int virtualKey)
	{
		KeyboardTarget target = WindowsKeyboard.GetCurrentInputTarget();
		Avalonia.Threading.Dispatcher.UIThread.Post(delegate
		{
			lblTriggerDebug.Text = $"Trigger key: VK {virtualKey}";
			if (rbUsb.IsChecked == true && _device.IsConnected && !WindowsKeyboard.IsIgnoredTriggerKey(virtualKey) && target.Window != IntPtr.Zero)
			{
				if (target.Window != TryGetPlatformHandle()?.Handle)
				{
					_externalOutputTargetWindow = target.Window;
					_externalOutputTargetControl = target.Control;
				}
				HandleScannerTriggerKey();
			}
		});
		return false;
	}

	private void HandleScannerTriggerKey()
	{
		if (rbSingle.IsChecked == true)
		{
			_singleTriggerQueued = true;
			_singleTriggerTimer.Stop();
			_singleTriggerTimer.Start();
		}
		else
		{
			ToggleUsbReadFromTrigger();
		}
	}

	private void ToggleUsbReadFromTrigger()
	{
		DateTime utcNow = DateTime.UtcNow;
		if ((utcNow - _lastUsbTriggerToggleUtc).TotalMilliseconds < 700.0)
		{
			ClearInput();
			return;
		}
		_lastUsbTriggerToggleUtc = utcNow;
		ClearInput();
		if (rbSingle.IsChecked == true)
		{
			if (_usbReadActive)
			{
				lblDebug.Text = "Single read is already running.";
			}
			else
			{
				StartUsbRead();
			}
		}
		else if (_usbReadActive)
		{
			StopUsbReadFromUi();
		}
		else
		{
			StartUsbRead();
		}
	}

	private void BtnStartRead_Click(object? sender, RoutedEventArgs e)
	{
		if (_usbReadActive)
		{
			StopUsbReadFromUi();
		}
		else
		{
			StartUsbRead();
		}
	}

	private void ExternalOutput_Changed(object? sender, RoutedEventArgs e)
	{
		chkSendToActiveApp.IsChecked = true;
		lblDebug.Text = "System keyboard mode is always enabled. Open any typing app and scan.";
	}

	private void StartUsbRead()
	{
		if (!_device.IsConnected)
		{
			lblDebug.Text = "USB is not connected.";
			FocusInternalInput();
			return;
		}
		ClearInput();
		if (_usbReadActive)
		{
			_device.SendInventoryCommand(start: false);
		}
		StopUsbRead();
		bool valueOrDefault = rbSingle.IsChecked == true;
		if (!valueOrDefault)
		{
			int num = _device.SendInventoryCommand(start: true);
			if (num != 0)
			{
				lblDebug.Text = $"Inventory start failed: {num}. {_device.LastError}";
				FocusInternalInput();
				return;
			}
		}
		_usbReadActive = true;
		_usbReadTimer.Stop();
		_usbReadTimer.Interval = (valueOrDefault ? TimeSpan.FromMilliseconds(GetInitialReadDelayMs()) : TimeSpan.FromMilliseconds(GetInitialReadDelayMs()));
		_usbReadTimer.Start();
		lblReadStatus.Text = ((rbSingle.IsChecked == true) ? "Single: reading one tag..." : "Continuous: reading, trigger again to stop");
		lblReadStatus.Foreground = Brushes.Green;
		btnStartRead.Content = "Stop";
		lblDebug.Text = (valueOrDefault ? "Single read started." : _device.LastError);
		FocusInternalInput();
	}

	private int GetInitialReadDelayMs()
	{
		return 500;
	}

	private void StopUsbRead()
	{
		_usbReadTimer.Stop();
		_usbReadTimer.Interval = TimeSpan.FromMilliseconds(500L);
		_usbReadActive = false;
		_usbPollInProgress = false;
		btnStartRead.Content = "Start";
	}

	private void StopUsbReadFromUi()
	{
		int num = _device.SendInventoryCommand(start: false);
		StopUsbRead();
		string text = ((rbSingle.IsChecked == true) ? "Single" : "Continuous");
		lblReadStatus.Text = ((num == 0) ? (text + ": stopped") : $"{text}: stop failed ({num})");
		lblReadStatus.Foreground = ((num == 0) ? Brushes.Green : Brushes.Orange);
		lblDebug.Text = _device.LastError;
		FocusInternalInput();
	}

	private async Task PollUsbTagsAsync()
	{
		if (!_usbReadActive || !_device.IsConnected)
		{
			StopUsbRead();
		}
		else
		{
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
				_usbReadTimer.Interval = TimeSpan.FromMilliseconds(500L);
				bool continuous = rbContinuous.IsChecked == true;
				List<string> epcs;
				try
				{
					epcs = await Task.Run((Func<List<string>>)ReadAvailableUsbTags);
				}
				catch (Exception ex)
				{
					lblDebug.Text = "USB read failed: " + ex.GetType().Name + ": " + ex.Message;
					return;
				}
				HandleUsbTags(epcs);
				if (continuous && _usbReadActive)
				{
					int num = _device.SendInventoryCommand(start: true);
					if (num != 0)
					{
						lblDebug.Text = $"Continuous restart failed: {num}. {_device.LastError}";
					}
				}
			}
			finally
			{
				_usbPollInProgress = false;
			}
		}
	}

	private async Task PollSingleUsbPulseAsync()
	{
		_usbReadTimer.Stop();
		int num = _device.SendInventoryCommand(start: true, single: true);
		if (num != 0)
		{
			lblDebug.Text = $"Single pulse start failed: {num}. {_device.LastError}";
			StopUsbRead();
			lblReadStatus.Text = "Single: ready for next trigger";
			lblReadStatus.Foreground = Brushes.Orange;
			return;
		}
		await Task.Delay(500);
		if (!_usbReadActive || !_device.IsConnected)
		{
			_device.SendInventoryCommand(start: false);
			StopUsbRead();
			return;
		}
		List<string> epcs;
		try
		{
			epcs = await Task.Run((Func<List<string>>)ReadAvailableUsbTags);
		}
		catch (Exception ex)
		{
			_device.SendInventoryCommand(start: false);
			lblDebug.Text = "Single pulse read failed: " + ex.GetType().Name + ": " + ex.Message;
			StopUsbRead();
			lblReadStatus.Text = "Single: ready for next trigger";
			lblReadStatus.Foreground = Brushes.Orange;
			return;
		}
		int num2 = _device.SendInventoryCommand(start: false);
		HandleUsbTags(epcs);
		StopUsbRead();
		if (num2 != 0)
		{
			lblDebug.Text = $"Single pulse stop failed: {num2}. {_device.LastError}";
		}
	}

	private List<string> ReadAvailableUsbTags()
	{
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		_lastUsbRawDebug = string.Empty;
		for (int i = 0; i < 200; i++)
		{
			int uLenUii = 128;
			byte[] array = new byte[uLenUii];
			if (UHFAPI.UHF_GetReceived_EX(ref uLenUii, array) != 0 || uLenUii <= 0)
			{
				break;
			}
			list2.Add(Convert.ToHexString(array, 0, uLenUii));
			string text = ExtractEpcFromReceivedBytes(array, uLenUii);
			if (text != null)
			{
				list.Add(text);
			}
		}
		if (list2.Count > 0)
		{
			_lastUsbRawDebug = string.Join(" | ", list2.Take(5));
		}
		return list;
	}

	private static string? ExtractEpcFromReceivedBytes(byte[] buffer, int length)
	{
		if (length <= 0)
		{
			return null;
		}
		byte[] array = buffer.Take(length).ToArray();
		string text = ExtractEpcFromReaderFrame(array);
		if (text != null)
		{
			return text;
		}
		if (array.Length >= 4)
		{
			int num = array[0];
			if (num >= 4 && array.Length >= 1 + num)
			{
				int num2 = (((ushort)((array[1] << 8) | array[2]) >> 11) & 0x1F) * 2;
				int num3 = ((num2 > 0) ? Math.Min(num2, num - 2) : (num - 2));
				if (IsValidEpcByteLength(num3))
				{
					return NormalizeEpc(Convert.ToHexString(array, 3, num3));
				}
			}
		}
		if (array.Length >= 4)
		{
			int num4 = (((ushort)((array[0] << 8) | array[1]) >> 11) & 0x1F) * 2;
			if (IsValidEpcByteLength(num4) && array.Length >= 2 + num4)
			{
				return NormalizeEpc(Convert.ToHexString(array, 2, num4));
			}
		}
		if (IsValidEpcByteLength(array.Length) && !LooksLikeReaderFrame(array))
		{
			return NormalizeEpc(Convert.ToHexString(array));
		}
		return null;
	}

	private static string? ExtractEpcFromReaderFrame(byte[] data)
	{
		for (int i = 0; i <= data.Length - 7; i++)
		{
			if (data[i] != 187)
			{
				continue;
			}
			int num = (data[i + 3] << 8) | data[i + 4];
			int num2 = 5 + num + 2;
			if (num < 0 || i + num2 > data.Length || data[i + num2 - 1] != 126)
			{
				continue;
			}
			byte b = 0;
			for (int j = i + 1; j < i + 5 + num; j++)
			{
				b += data[j];
			}
			if (b == data[i + 5 + num])
			{
				string text = ExtractEpcFromReceivedBytes(data.Skip(i + 5).Take(num).ToArray(), num);
				if (text != null)
				{
					return text;
				}
			}
		}
		return null;
	}

	private static bool IsValidEpcByteLength(int byteLength)
	{
		if (byteLength >= 6)
		{
			return byteLength <= 32;
		}
		return false;
	}

	private static bool LooksLikeReaderFrame(byte[] data)
	{
		if (data.Length >= 7 && data[0] == 187)
		{
			return data[^1] == 126;
		}
		return false;
	}

	private void HandleUsbTags(List<string> epcs)
	{
		if (!_usbReadActive)
		{
			return;
		}
		if (rbSingle.IsChecked == true)
		{
			string text = epcs.FirstOrDefault();
			if (text == null)
			{
				lblReadStatus.Text = "Single: no tag, ready for next trigger";
				lblReadStatus.Foreground = Brushes.Orange;
				lblDebug.Text = (string.IsNullOrEmpty(_lastUsbRawDebug) ? ("Single read found no tag. " + _device.LastError) : ("Single read found no EPC. Raw USB: " + _lastUsbRawDebug + ". " + _device.LastError));
				return;
			}
			AddOrUpdateTag(text);
			PlaySingleReadBeep();
			SendEpcToActiveApp(text);
			UpdateTagCount();
			lblReadStatus.Text = "Single: read 1 tag, ready for next trigger";
			lblReadStatus.Foreground = Brushes.Green;
			lblDebug.Text = (string.IsNullOrEmpty(_lastUsbRawDebug) ? ("EPC received: " + text + ". " + _device.LastError) : $"EPC received: {text}. Raw USB: {_lastUsbRawDebug}. {_device.LastError}");
			return;
		}
		if (epcs.Count == 0)
		{
			lblDebug.Text = (string.IsNullOrEmpty(_lastUsbRawDebug) ? ("Continuous reading... waiting for tags. " + _device.LastError) : ("Continuous reading... waiting for EPC. Raw USB: " + _lastUsbRawDebug + ". " + _device.LastError));
			return;
		}
		foreach (string item in epcs.Distinct())
		{
			AddOrUpdateTag(item);
			SendEpcToActiveApp(item);
		}
		UpdateTagCount();
		lblReadStatus.Text = $"Continuous: reading ({tagMap.Count} tag)";
		lblReadStatus.Foreground = Brushes.Green;
		lblDebug.Text = (string.IsNullOrEmpty(_lastUsbRawDebug) ? $"Last EPC batch: {epcs.Count}" : $"Last EPC batch: {epcs.Count}. Raw USB: {_lastUsbRawDebug}");
	}

	private void ProcessCurrentInput()
	{
		string text = txtEpcInput.Text ?? string.Empty;
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		List<string> list = (from epc in text.Split(new char[4] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeEpc)
			where epc != null
			select (epc)).Distinct().ToList();
		if (list.Count == 0)
		{
			ClearInput();
			return;
		}
		if (rbSingle.IsChecked == true)
		{
			AddOrUpdateTag(list[0]);
			PlaySingleReadBeep();
			SendEpcToActiveApp(list[0]);
			if (_device.IsConnected)
			{
				int num = _device.SendInventoryCommand(start: false);
				if (num != 0)
				{
					lblDebug.Text = $"Inventory stop failed: {num}. {_device.LastError}";
				}
			}
			StopUsbRead();
		}
		else
		{
			foreach (string item in list)
			{
				AddOrUpdateTag(item);
				SendEpcToActiveApp(item);
			}
		}
		ClearInput();
		UpdateTagCount();
		FocusInternalInput();
	}

	private void PlaySingleReadBeep()
	{
		if (rbSingle.IsChecked == true && OperatingSystem.IsWindows())
		{
			WindowsKeyboard.PlayDefaultBeep();
		}
	}

	private static string? NormalizeEpc(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}
		string text = new string(raw.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
		if (text.Length < 12 || text.Length % 2 != 0)
		{
			return null;
		}
		if (text.All((char c) => c == '0') || text.All((char c) => c == 'F'))
		{
			return null;
		}
		return text;
	}

	private void AddOrUpdateTag(string epc)
	{
		if (tagMap.TryGetValue(epc, out TagEntry value))
		{
			value.Count++;
			int num = tags.IndexOf(value);
			if (num >= 0)
			{
				tags[num] = value;
			}
		}
		else
		{
			TagEntry tagEntry = new TagEntry
			{
				Index = tags.Count + 1,
				Epc = epc,
				Count = 1
			};
			tagMap[epc] = tagEntry;
			tags.Add(tagEntry);
		}
	}

	private void ClearInput()
	{
		_suppressTextChanged = true;
		txtEpcInput.Text = string.Empty;
		_suppressTextChanged = false;
	}

	private void FocusInternalInput()
	{
		if (_externalOutputTargetWindow == IntPtr.Zero)
		{
			txtEpcInput.Focus();
		}
	}

	private void UpdateTagCount()
	{
		lblTagCount.Text = $"Total: {tagMap.Count} tag";
	}

	private void SendEpcToActiveApp(string epc)
	{
		if (_externalOutputTargetWindow == IntPtr.Zero)
		{
			return;
		}
		if (!OperatingSystem.IsWindows())
		{
			lblDebug.Text = "External app output is only supported on Windows.";
			return;
		}
		try
		{
			WindowsKeyboard.SendTextLine(_externalOutputTargetWindow, _externalOutputTargetControl, epc);
		}
		catch (Exception ex)
		{
			lblDebug.Text = "External output failed: " + ex.GetType().Name + ": " + ex.Message;
			_externalOutputTargetWindow = IntPtr.Zero;
			_externalOutputTargetControl = IntPtr.Zero;
		}
	}

	private void BtnConnect_Click(object? sender, RoutedEventArgs e)
	{
		int num = _device.ConnectUsb();
		if (num == 0)
		{
			lblStatus.Text = "USB connected";
			lblStatus.Foreground = Brushes.Green;
			btnConnect.IsEnabled = false;
			btnDisconnect.IsEnabled = true;
			pnlAntenna.IsEnabled = true;
			pnlPower.IsEnabled = true;
			pnlAntenna.Opacity = 1.0;
			pnlPower.Opacity = 1.0;
			BtnRefresh_Click(sender, e);
			ReadMode_Changed(sender, e);
		}
		else
		{
			lblStatus.Text = $"USB connect failed ({num})";
			lblStatus.Foreground = Brushes.Red;
			lblDebug.Text = _device.LastError;
		}
	}

	private void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
	{
		_inputTimer.Stop();
		if (_device.IsConnected)
		{
			_device.SendInventoryCommand(start: false);
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
		int power2 = _device.GetPower(ref power);
		if (power2 == 0)
		{
			SyncPowerValue((int)power);
			lblCurrentPower.Text = $"Current power: {power} dBm";
			lblDebug.Text = string.Empty;
		}
		else
		{
			lblDebug.Text = $"Power read failed: {power2}";
		}
	}

	private void BtnSave_Click(object? sender, RoutedEventArgs e)
	{
		byte b = (byte)Math.Clamp((int)Math.Round(trackPower.Value), 5, 30);
		int num = _device.SetPower(b);
		if (num == 0)
		{
			lblCurrentPower.Text = $"Current power: {b} dBm";
			lblDebug.Text = $"Saved: {b} dBm";
		}
		else
		{
			lblDebug.Text = $"Power save failed: {num}";
		}
	}

	private void BtnClearList_Click(object? sender, RoutedEventArgs e)
	{
		tags.Clear();
		tagMap.Clear();
		UpdateTagCount();
		FocusInternalInput();
	}

	private void SyncPowerValue(double value)
	{
		double num = Math.Clamp(Math.Round(value), 5.0, 30.0);
		if (Math.Abs(trackPower.Value - num) > 0.01)
		{
			trackPower.Value = num;
		}
		decimal? value2 = numPower.Value;
		decimal num2 = (decimal)num;
		if (!((value2.GetValueOrDefault() == num2) & value2.HasValue))
		{
			numPower.Value = (decimal)num;
		}
		lblPowerValue.Text = $"{num:0} dBm";
	}

	[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "12.0.1.0")]
	[ExcludeFromCodeCoverage]
	public void InitializeComponent(bool loadXaml = true)
	{
		if (loadXaml)
		{
			_0021XamlIlPopulateTrampoline(this);
		}
		INameScope nameScope = this.FindNameScope();
		rbUsb = nameScope?.Find<RadioButton>("rbUsb");
		rbBluetooth = nameScope?.Find<RadioButton>("rbBluetooth");
		lblBtHint = nameScope?.Find<TextBlock>("lblBtHint");
		lblStatus = nameScope?.Find<TextBlock>("lblStatus");
		pnlUsbButtons = nameScope?.Find<StackPanel>("pnlUsbButtons");
		btnConnect = nameScope?.Find<Button>("btnConnect");
		btnDisconnect = nameScope?.Find<Button>("btnDisconnect");
		lblDebug = nameScope?.Find<TextBlock>("lblDebug");
		pnlAntenna = nameScope?.Find<Border>("pnlAntenna");
		lblCurrentPower = nameScope?.Find<TextBlock>("lblCurrentPower");
		btnRefresh = nameScope?.Find<Button>("btnRefresh");
		pnlPower = nameScope?.Find<Border>("pnlPower");
		trackPower = nameScope?.Find<Slider>("trackPower");
		lblPowerValue = nameScope?.Find<TextBlock>("lblPowerValue");
		numPower = nameScope?.Find<NumericUpDown>("numPower");
		btnSave = nameScope?.Find<Button>("btnSave");
		rbSingle = nameScope?.Find<RadioButton>("rbSingle");
		rbContinuous = nameScope?.Find<RadioButton>("rbContinuous");
		lblReadStatus = nameScope?.Find<TextBlock>("lblReadStatus");
		chkSendToActiveApp = nameScope?.Find<CheckBox>("chkSendToActiveApp");
		lblTriggerDebug = nameScope?.Find<TextBlock>("lblTriggerDebug");
		txtEpcInput = nameScope?.Find<TextBox>("txtEpcInput");
		btnStartRead = nameScope?.Find<Button>("btnStartRead");
		btnClearList = nameScope?.Find<Button>("btnClearList");
		lblTagCount = nameScope?.Find<TextBlock>("lblTagCount");
		dgTags = nameScope?.Find<DataGrid>("dgTags");
	}

	[CompilerGenerated]
	private static void _0021XamlIlPopulate(IServiceProvider P_0, MainWindow P_1)
	{
		CompiledAvaloniaXaml.XamlIlContext.Context<MainWindow> context = new CompiledAvaloniaXaml.XamlIlContext.Context<MainWindow>(P_0, new object[1] { _0021AvaloniaResources.NamespaceInfo_003A_002FMainWindow_002Eaxaml.Singleton }, "avares://SR160PowerConfig/MainWindow.axaml")
		{
			RootObject = P_1,
			IntermediateRoot = P_1
		};
		((ISupportInitialize)P_1).BeginInit();
		context.PushParent(P_1);
		P_1.Title = "CHIPMO SR160 Power Config";
		P_1.Width = 520.0;
		P_1.Height = 780.0;
		P_1.CanResize = false;
		P_1.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		StackPanel stackPanel2;
		StackPanel stackPanel = (stackPanel2 = new StackPanel());
		((ISupportInitialize)stackPanel).BeginInit();
		P_1.Content = stackPanel;
		StackPanel stackPanel4;
		StackPanel stackPanel3 = (stackPanel4 = stackPanel2);
		context.PushParent(stackPanel4);
		StackPanel stackPanel5 = stackPanel4;
		stackPanel5.Margin = new Thickness(0.0, 0.0, 0.0, 0.0);
		Controls children = stackPanel5.Children;
		Border border2;
		Border border = (border2 = new Border());
		((ISupportInitialize)border).BeginInit();
		children.Add(border);
		border2.Background = new ImmutableSolidColorBrush(4280362281u);
		border2.Padding = new Thickness(15.0, 8.0, 15.0, 10.0);
		StackPanel stackPanel7;
		StackPanel stackPanel6 = (stackPanel7 = new StackPanel());
		((ISupportInitialize)stackPanel6).BeginInit();
		border2.Child = stackPanel6;
		Controls children2 = stackPanel7.Children;
		TextBlock textBlock2;
		TextBlock textBlock = (textBlock2 = new TextBlock());
		((ISupportInitialize)textBlock).BeginInit();
		children2.Add(textBlock);
		textBlock2.Text = "CHIPMO";
		textBlock2.FontSize = 20.0;
		textBlock2.FontWeight = FontWeight.Bold;
		textBlock2.Foreground = new ImmutableSolidColorBrush(uint.MaxValue);
		((ISupportInitialize)textBlock2).EndInit();
		Controls children3 = stackPanel7.Children;
		TextBlock textBlock4;
		TextBlock textBlock3 = (textBlock4 = new TextBlock());
		((ISupportInitialize)textBlock3).BeginInit();
		children3.Add(textBlock3);
		textBlock4.Text = "Chainway SR160 UHF RFID TX Power Config";
		textBlock4.FontSize = 12.0;
		textBlock4.Foreground = new ImmutableSolidColorBrush(4289574333u);
		((ISupportInitialize)textBlock4).EndInit();
		((ISupportInitialize)stackPanel7).EndInit();
		((ISupportInitialize)border2).EndInit();
		Controls children4 = stackPanel5.Children;
		StackPanel stackPanel9;
		StackPanel stackPanel8 = (stackPanel9 = new StackPanel());
		((ISupportInitialize)stackPanel8).BeginInit();
		children4.Add(stackPanel8);
		StackPanel stackPanel10 = (stackPanel4 = stackPanel9);
		context.PushParent(stackPanel4);
		StackPanel stackPanel11 = stackPanel4;
		stackPanel11.Margin = new Thickness(12.0, 8.0, 12.0, 0.0);
		stackPanel11.Spacing = 8.0;
		Controls children5 = stackPanel11.Children;
		Border border4;
		Border border3 = (border4 = new Border());
		((ISupportInitialize)border3).BeginInit();
		children5.Add(border3);
		border4.BorderBrush = new ImmutableSolidColorBrush(4291611852u);
		border4.BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0);
		border4.CornerRadius = new CornerRadius(4.0, 4.0, 4.0, 4.0);
		border4.Padding = new Thickness(12.0, 8.0, 12.0, 8.0);
		StackPanel stackPanel13;
		StackPanel stackPanel12 = (stackPanel13 = new StackPanel());
		((ISupportInitialize)stackPanel12).BeginInit();
		border4.Child = stackPanel12;
		stackPanel13.Spacing = 8.0;
		Controls children6 = stackPanel13.Children;
		TextBlock textBlock6;
		TextBlock textBlock5 = (textBlock6 = new TextBlock());
		((ISupportInitialize)textBlock5).BeginInit();
		children6.Add(textBlock5);
		textBlock6.Text = "Connection";
		textBlock6.FontWeight = FontWeight.Bold;
		textBlock6.FontSize = 13.0;
		((ISupportInitialize)textBlock6).EndInit();
		Controls children7 = stackPanel13.Children;
		StackPanel stackPanel15;
		StackPanel stackPanel14 = (stackPanel15 = new StackPanel());
		((ISupportInitialize)stackPanel14).BeginInit();
		children7.Add(stackPanel14);
		stackPanel15.Orientation = Orientation.Horizontal;
		stackPanel15.Spacing = 10.0;
		Controls children8 = stackPanel15.Children;
		RadioButton radioButton2;
		RadioButton radioButton = (radioButton2 = new RadioButton());
		((ISupportInitialize)radioButton).BeginInit();
		children8.Add(radioButton);
		radioButton2.Name = "rbUsb";
		object element = radioButton2;
		context.AvaloniaNameScope.Register("rbUsb", element);
		radioButton2.Content = "USB";
		radioButton2.GroupName = "ConnType";
		radioButton2.IsChecked = true;
		radioButton2.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.ConnType_Changed), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)radioButton2).EndInit();
		Controls children9 = stackPanel15.Children;
		RadioButton radioButton5;
		RadioButton radioButton4 = (radioButton5 = new RadioButton());
		((ISupportInitialize)radioButton4).BeginInit();
		children9.Add(radioButton4);
		radioButton5.Name = "rbBluetooth";
		element = radioButton5;
		context.AvaloniaNameScope.Register("rbBluetooth", element);
		radioButton5.Content = "Bluetooth (HID)";
		radioButton5.GroupName = "ConnType";
		radioButton5.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.ConnType_Changed), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)radioButton5).EndInit();
		((ISupportInitialize)stackPanel15).EndInit();
		Controls children10 = stackPanel13.Children;
		TextBlock textBlock8;
		TextBlock textBlock7 = (textBlock8 = new TextBlock());
		((ISupportInitialize)textBlock7).BeginInit();
		children10.Add(textBlock7);
		textBlock8.Name = "lblBtHint";
		element = textBlock8;
		context.AvaloniaNameScope.Register("lblBtHint", element);
		textBlock8.IsVisible = false;
		textBlock8.Text = "If the SR160 is paired as Bluetooth HID, EPC input can work here.\nUse USB for power configuration.";
		textBlock8.FontSize = 11.0;
		textBlock8.Foreground = new ImmutableSolidColorBrush(4289374890u);
		textBlock8.TextWrapping = TextWrapping.Wrap;
		((ISupportInitialize)textBlock8).EndInit();
		Controls children11 = stackPanel13.Children;
		TextBlock textBlock10;
		TextBlock textBlock9 = (textBlock10 = new TextBlock());
		((ISupportInitialize)textBlock9).BeginInit();
		children11.Add(textBlock9);
		textBlock10.Name = "lblStatus";
		element = textBlock10;
		context.AvaloniaNameScope.Register("lblStatus", element);
		textBlock10.Text = "Disconnected";
		textBlock10.Foreground = new ImmutableSolidColorBrush(4294901760u);
		textBlock10.FontSize = 13.0;
		textBlock10.FontWeight = FontWeight.Bold;
		((ISupportInitialize)textBlock10).EndInit();
		Controls children12 = stackPanel13.Children;
		StackPanel stackPanel17;
		StackPanel stackPanel16 = (stackPanel17 = new StackPanel());
		((ISupportInitialize)stackPanel16).BeginInit();
		children12.Add(stackPanel16);
		stackPanel17.Name = "pnlUsbButtons";
		element = stackPanel17;
		context.AvaloniaNameScope.Register("pnlUsbButtons", element);
		stackPanel17.Orientation = Orientation.Horizontal;
		stackPanel17.Spacing = 8.0;
		Controls children13 = stackPanel17.Children;
		Button button2;
		Button button = (button2 = new Button());
		((ISupportInitialize)button).BeginInit();
		children13.Add(button);
		button2.Name = "btnConnect";
		element = button2;
		context.AvaloniaNameScope.Register("btnConnect", element);
		button2.Content = "Connect";
		button2.Width = 110.0;
		button2.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.BtnConnect_Click), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)button2).EndInit();
		Controls children14 = stackPanel17.Children;
		Button button5;
		Button button4 = (button5 = new Button());
		((ISupportInitialize)button4).BeginInit();
		children14.Add(button4);
		button5.Name = "btnDisconnect";
		element = button5;
		context.AvaloniaNameScope.Register("btnDisconnect", element);
		button5.Content = "Disconnect";
		button5.Width = 90.0;
		button5.IsEnabled = false;
		button5.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.BtnDisconnect_Click), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)button5).EndInit();
		((ISupportInitialize)stackPanel17).EndInit();
		Controls children15 = stackPanel13.Children;
		TextBlock textBlock12;
		TextBlock textBlock11 = (textBlock12 = new TextBlock());
		((ISupportInitialize)textBlock11).BeginInit();
		children15.Add(textBlock11);
		textBlock12.Name = "lblDebug";
		element = textBlock12;
		context.AvaloniaNameScope.Register("lblDebug", element);
		textBlock12.Text = "";
		textBlock12.FontSize = 10.0;
		textBlock12.Foreground = new ImmutableSolidColorBrush(4287137928u);
		textBlock12.TextWrapping = TextWrapping.Wrap;
		textBlock12.FontFamily = new FontFamily(((IUriContext)context).BaseUri, "Consolas,Menlo,monospace");
		((ISupportInitialize)textBlock12).EndInit();
		((ISupportInitialize)stackPanel13).EndInit();
		((ISupportInitialize)border4).EndInit();
		Controls children16 = stackPanel11.Children;
		Border border6;
		Border border5 = (border6 = new Border());
		((ISupportInitialize)border5).BeginInit();
		children16.Add(border5);
		border6.Name = "pnlAntenna";
		element = border6;
		context.AvaloniaNameScope.Register("pnlAntenna", element);
		border6.BorderBrush = new ImmutableSolidColorBrush(4291611852u);
		border6.BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0);
		border6.CornerRadius = new CornerRadius(4.0, 4.0, 4.0, 4.0);
		border6.Padding = new Thickness(12.0, 8.0, 12.0, 8.0);
		border6.IsEnabled = false;
		border6.Opacity = 0.5;
		StackPanel stackPanel19;
		StackPanel stackPanel18 = (stackPanel19 = new StackPanel());
		((ISupportInitialize)stackPanel18).BeginInit();
		border6.Child = stackPanel18;
		stackPanel19.Spacing = 4.0;
		Controls children17 = stackPanel19.Children;
		TextBlock textBlock14;
		TextBlock textBlock13 = (textBlock14 = new TextBlock());
		((ISupportInitialize)textBlock13).BeginInit();
		children17.Add(textBlock13);
		textBlock14.Text = "Antenna #1 (USB only)";
		textBlock14.FontWeight = FontWeight.Bold;
		textBlock14.FontSize = 13.0;
		((ISupportInitialize)textBlock14).EndInit();
		Controls children18 = stackPanel19.Children;
		Grid grid2;
		Grid grid = (grid2 = new Grid());
		((ISupportInitialize)grid).BeginInit();
		children18.Add(grid);
		ColumnDefinitions columnDefinitions = new ColumnDefinitions();
		columnDefinitions.Capacity = 2;
		columnDefinitions.Add(new ColumnDefinition(new GridLength(1.0, GridUnitType.Star)));
		columnDefinitions.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		grid2.ColumnDefinitions = columnDefinitions;
		Controls children19 = grid2.Children;
		TextBlock textBlock16;
		TextBlock textBlock15 = (textBlock16 = new TextBlock());
		((ISupportInitialize)textBlock15).BeginInit();
		children19.Add(textBlock15);
		textBlock16.Name = "lblCurrentPower";
		element = textBlock16;
		context.AvaloniaNameScope.Register("lblCurrentPower", element);
		textBlock16.Text = "Current power: --";
		textBlock16.FontSize = 14.0;
		textBlock16.VerticalAlignment = VerticalAlignment.Center;
		((ISupportInitialize)textBlock16).EndInit();
		Controls children20 = grid2.Children;
		Button button7;
		Button button6 = (button7 = new Button());
		((ISupportInitialize)button6).BeginInit();
		children20.Add(button6);
		button7.Name = "btnRefresh";
		element = button7;
		context.AvaloniaNameScope.Register("btnRefresh", element);
		button7.Content = "Refresh";
		Grid.SetColumn(button7, 1);
		button7.Width = 90.0;
		button7.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.BtnRefresh_Click), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)button7).EndInit();
		((ISupportInitialize)grid2).EndInit();
		((ISupportInitialize)stackPanel19).EndInit();
		((ISupportInitialize)border6).EndInit();
		Controls children21 = stackPanel11.Children;
		Border border8;
		Border border7 = (border8 = new Border());
		((ISupportInitialize)border7).BeginInit();
		children21.Add(border7);
		border8.Name = "pnlPower";
		element = border8;
		context.AvaloniaNameScope.Register("pnlPower", element);
		border8.BorderBrush = new ImmutableSolidColorBrush(4291611852u);
		border8.BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0);
		border8.CornerRadius = new CornerRadius(4.0, 4.0, 4.0, 4.0);
		border8.Padding = new Thickness(12.0, 8.0, 12.0, 8.0);
		border8.IsEnabled = false;
		border8.Opacity = 0.5;
		StackPanel stackPanel21;
		StackPanel stackPanel20 = (stackPanel21 = new StackPanel());
		((ISupportInitialize)stackPanel20).BeginInit();
		border8.Child = stackPanel20;
		stackPanel21.Spacing = 6.0;
		Controls children22 = stackPanel21.Children;
		TextBlock textBlock18;
		TextBlock textBlock17 = (textBlock18 = new TextBlock());
		((ISupportInitialize)textBlock17).BeginInit();
		children22.Add(textBlock17);
		textBlock18.Text = "Power setting (5 to 30 dBm, USB only)";
		textBlock18.FontWeight = FontWeight.Bold;
		textBlock18.FontSize = 13.0;
		((ISupportInitialize)textBlock18).EndInit();
		Controls children23 = stackPanel21.Children;
		Grid grid4;
		Grid grid3 = (grid4 = new Grid());
		((ISupportInitialize)grid3).BeginInit();
		children23.Add(grid3);
		ColumnDefinitions columnDefinitions2 = new ColumnDefinitions();
		columnDefinitions2.Capacity = 3;
		columnDefinitions2.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		columnDefinitions2.Add(new ColumnDefinition(new GridLength(1.0, GridUnitType.Star)));
		columnDefinitions2.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		grid4.ColumnDefinitions = columnDefinitions2;
		Controls children24 = grid4.Children;
		TextBlock textBlock20;
		TextBlock textBlock19 = (textBlock20 = new TextBlock());
		((ISupportInitialize)textBlock19).BeginInit();
		children24.Add(textBlock19);
		textBlock20.Text = "5";
		textBlock20.VerticalAlignment = VerticalAlignment.Center;
		textBlock20.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
		((ISupportInitialize)textBlock20).EndInit();
		Controls children25 = grid4.Children;
		Slider slider2;
		Slider slider = (slider2 = new Slider());
		((ISupportInitialize)slider).BeginInit();
		children25.Add(slider);
		slider2.Name = "trackPower";
		element = slider2;
		context.AvaloniaNameScope.Register("trackPower", element);
		Grid.SetColumn(slider2, 1);
		slider2.Minimum = 5.0;
		slider2.Maximum = 30.0;
		slider2.Value = 20.0;
		slider2.IsSnapToTickEnabled = true;
		slider2.TickFrequency = 1.0;
		((ISupportInitialize)slider2).EndInit();
		Controls children26 = grid4.Children;
		TextBlock textBlock22;
		TextBlock textBlock21 = (textBlock22 = new TextBlock());
		((ISupportInitialize)textBlock21).BeginInit();
		children26.Add(textBlock21);
		textBlock22.Text = "30";
		Grid.SetColumn(textBlock22, 2);
		textBlock22.VerticalAlignment = VerticalAlignment.Center;
		textBlock22.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
		((ISupportInitialize)textBlock22).EndInit();
		((ISupportInitialize)grid4).EndInit();
		Controls children27 = stackPanel21.Children;
		TextBlock textBlock24;
		TextBlock textBlock23 = (textBlock24 = new TextBlock());
		((ISupportInitialize)textBlock23).BeginInit();
		children27.Add(textBlock23);
		textBlock24.Name = "lblPowerValue";
		element = textBlock24;
		context.AvaloniaNameScope.Register("lblPowerValue", element);
		textBlock24.Text = "-- dBm";
		textBlock24.FontSize = 20.0;
		textBlock24.FontWeight = FontWeight.Bold;
		textBlock24.Foreground = new ImmutableSolidColorBrush(4279592384u);
		textBlock24.HorizontalAlignment = HorizontalAlignment.Center;
		((ISupportInitialize)textBlock24).EndInit();
		Controls children28 = stackPanel21.Children;
		Grid grid6;
		Grid grid5 = (grid6 = new Grid());
		((ISupportInitialize)grid5).BeginInit();
		children28.Add(grid5);
		ColumnDefinitions columnDefinitions3 = new ColumnDefinitions();
		columnDefinitions3.Capacity = 4;
		columnDefinitions3.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		columnDefinitions3.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		columnDefinitions3.Add(new ColumnDefinition(new GridLength(1.0, GridUnitType.Star)));
		columnDefinitions3.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		grid6.ColumnDefinitions = columnDefinitions3;
		Controls children29 = grid6.Children;
		NumericUpDown numericUpDown2;
		NumericUpDown numericUpDown = (numericUpDown2 = new NumericUpDown());
		((ISupportInitialize)numericUpDown).BeginInit();
		children29.Add(numericUpDown);
		numericUpDown2.Name = "numPower";
		element = numericUpDown2;
		context.AvaloniaNameScope.Register("numPower", element);
		numericUpDown2.Minimum = decimal.Parse("5", CultureInfo.InvariantCulture);
		numericUpDown2.Maximum = decimal.Parse("30", CultureInfo.InvariantCulture);
		numericUpDown2.Value = decimal.Parse("20", CultureInfo.InvariantCulture);
		numericUpDown2.Width = 90.0;
		numericUpDown2.FormatString = "0";
		numericUpDown2.Increment = decimal.Parse("1", CultureInfo.InvariantCulture);
		((ISupportInitialize)numericUpDown2).EndInit();
		Controls children30 = grid6.Children;
		TextBlock textBlock26;
		TextBlock textBlock25 = (textBlock26 = new TextBlock());
		((ISupportInitialize)textBlock25).BeginInit();
		children30.Add(textBlock25);
		textBlock26.Text = "dBm";
		Grid.SetColumn(textBlock26, 1);
		textBlock26.VerticalAlignment = VerticalAlignment.Center;
		textBlock26.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
		((ISupportInitialize)textBlock26).EndInit();
		Controls children31 = grid6.Children;
		Button button9;
		Button button8 = (button9 = new Button());
		((ISupportInitialize)button8).BeginInit();
		children31.Add(button8);
		button9.Name = "btnSave";
		element = button9;
		context.AvaloniaNameScope.Register("btnSave", element);
		button9.Content = "Save";
		Grid.SetColumn(button9, 3);
		button9.Width = 120.0;
		button9.Height = 36.0;
		button9.FontWeight = FontWeight.Bold;
		button9.FontSize = 14.0;
		button9.Background = new ImmutableSolidColorBrush(4283215696u);
		button9.Foreground = new ImmutableSolidColorBrush(uint.MaxValue);
		button9.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.BtnSave_Click), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)button9).EndInit();
		((ISupportInitialize)grid6).EndInit();
		((ISupportInitialize)stackPanel21).EndInit();
		((ISupportInitialize)border8).EndInit();
		Controls children32 = stackPanel11.Children;
		Border border10;
		Border border9 = (border10 = new Border());
		((ISupportInitialize)border9).BeginInit();
		children32.Add(border9);
		Border border12;
		Border border11 = (border12 = border10);
		context.PushParent(border12);
		border12.BorderBrush = new ImmutableSolidColorBrush(4291611852u);
		border12.BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0);
		border12.CornerRadius = new CornerRadius(4.0, 4.0, 4.0, 4.0);
		border12.Padding = new Thickness(12.0, 8.0, 12.0, 8.0);
		StackPanel stackPanel23;
		StackPanel stackPanel22 = (stackPanel23 = new StackPanel());
		((ISupportInitialize)stackPanel22).BeginInit();
		border12.Child = stackPanel22;
		StackPanel stackPanel24 = (stackPanel4 = stackPanel23);
		context.PushParent(stackPanel4);
		StackPanel stackPanel25 = stackPanel4;
		stackPanel25.Spacing = 6.0;
		Controls children33 = stackPanel25.Children;
		TextBlock textBlock28;
		TextBlock textBlock27 = (textBlock28 = new TextBlock());
		((ISupportInitialize)textBlock27).BeginInit();
		children33.Add(textBlock27);
		textBlock28.Text = "EPC Reading";
		textBlock28.FontWeight = FontWeight.Bold;
		textBlock28.FontSize = 13.0;
		((ISupportInitialize)textBlock28).EndInit();
		Controls children34 = stackPanel25.Children;
		StackPanel stackPanel27;
		StackPanel stackPanel26 = (stackPanel27 = new StackPanel());
		((ISupportInitialize)stackPanel26).BeginInit();
		children34.Add(stackPanel26);
		stackPanel27.Orientation = Orientation.Horizontal;
		stackPanel27.Spacing = 8.0;
		Controls children35 = stackPanel27.Children;
		RadioButton radioButton7;
		RadioButton radioButton6 = (radioButton7 = new RadioButton());
		((ISupportInitialize)radioButton6).BeginInit();
		children35.Add(radioButton6);
		radioButton7.Name = "rbSingle";
		element = radioButton7;
		context.AvaloniaNameScope.Register("rbSingle", element);
		radioButton7.Content = "Single";
		radioButton7.GroupName = "ReadMode";
		radioButton7.IsChecked = true;
		radioButton7.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.ReadMode_Changed), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)radioButton7).EndInit();
		Controls children36 = stackPanel27.Children;
		RadioButton radioButton9;
		RadioButton radioButton8 = (radioButton9 = new RadioButton());
		((ISupportInitialize)radioButton8).BeginInit();
		children36.Add(radioButton8);
		radioButton9.Name = "rbContinuous";
		element = radioButton9;
		context.AvaloniaNameScope.Register("rbContinuous", element);
		radioButton9.Content = "Continuous";
		radioButton9.GroupName = "ReadMode";
		radioButton9.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.ReadMode_Changed), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)radioButton9).EndInit();
		((ISupportInitialize)stackPanel27).EndInit();
		Controls children37 = stackPanel25.Children;
		TextBlock textBlock30;
		TextBlock textBlock29 = (textBlock30 = new TextBlock());
		((ISupportInitialize)textBlock29).BeginInit();
		children37.Add(textBlock29);
		textBlock30.Name = "lblReadStatus";
		element = textBlock30;
		context.AvaloniaNameScope.Register("lblReadStatus", element);
		textBlock30.Text = "";
		textBlock30.FontSize = 11.0;
		textBlock30.FontWeight = FontWeight.Bold;
		((ISupportInitialize)textBlock30).EndInit();
		Controls children38 = stackPanel25.Children;
		CheckBox checkBox2;
		CheckBox checkBox = (checkBox2 = new CheckBox());
		((ISupportInitialize)checkBox).BeginInit();
		children38.Add(checkBox);
		checkBox2.Name = "chkSendToActiveApp";
		element = checkBox2;
		context.AvaloniaNameScope.Register("chkSendToActiveApp", element);
		checkBox2.IsChecked = true;
		checkBox2.IsVisible = false;
		checkBox2.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.ExternalOutput_Changed), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)checkBox2).EndInit();
		Controls children39 = stackPanel25.Children;
		TextBlock textBlock32;
		TextBlock textBlock31 = (textBlock32 = new TextBlock());
		((ISupportInitialize)textBlock31).BeginInit();
		children39.Add(textBlock31);
		textBlock32.Text = "System keyboard mode: open any typing program, click where text should go, then scan.";
		textBlock32.FontSize = 11.0;
		textBlock32.Foreground = new ImmutableSolidColorBrush(4284900966u);
		textBlock32.TextWrapping = TextWrapping.Wrap;
		((ISupportInitialize)textBlock32).EndInit();
		Controls children40 = stackPanel25.Children;
		TextBlock textBlock34;
		TextBlock textBlock33 = (textBlock34 = new TextBlock());
		((ISupportInitialize)textBlock33).BeginInit();
		children40.Add(textBlock33);
		textBlock34.Name = "lblTriggerDebug";
		element = textBlock34;
		context.AvaloniaNameScope.Register("lblTriggerDebug", element);
		textBlock34.Text = "Trigger key: not detected";
		textBlock34.FontSize = 10.0;
		textBlock34.Foreground = new ImmutableSolidColorBrush(4287137928u);
		textBlock34.TextWrapping = TextWrapping.Wrap;
		((ISupportInitialize)textBlock34).EndInit();
		Controls children41 = stackPanel25.Children;
		Grid grid8;
		Grid grid7 = (grid8 = new Grid());
		((ISupportInitialize)grid7).BeginInit();
		children41.Add(grid7);
		ColumnDefinitions columnDefinitions4 = new ColumnDefinitions();
		columnDefinitions4.Capacity = 4;
		columnDefinitions4.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		columnDefinitions4.Add(new ColumnDefinition(new GridLength(1.0, GridUnitType.Star)));
		columnDefinitions4.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		columnDefinitions4.Add(new ColumnDefinition(new GridLength(0.0, GridUnitType.Auto)));
		grid8.ColumnDefinitions = columnDefinitions4;
		grid8.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
		Controls children42 = grid8.Children;
		TextBlock textBlock36;
		TextBlock textBlock35 = (textBlock36 = new TextBlock());
		((ISupportInitialize)textBlock35).BeginInit();
		children42.Add(textBlock35);
		textBlock36.Text = "EPC:";
		textBlock36.VerticalAlignment = VerticalAlignment.Center;
		textBlock36.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		((ISupportInitialize)textBlock36).EndInit();
		Controls children43 = grid8.Children;
		TextBox textBox2;
		TextBox textBox = (textBox2 = new TextBox());
		((ISupportInitialize)textBox).BeginInit();
		children43.Add(textBox);
		textBox2.Name = "txtEpcInput";
		element = textBox2;
		context.AvaloniaNameScope.Register("txtEpcInput", element);
		Grid.SetColumn(textBox2, 1);
		textBox2.FontFamily = new FontFamily(((IUriContext)context).BaseUri, "Consolas,Menlo,monospace");
		textBox2.FontSize = 13.0;
		textBox2.PlaceholderText = "Scan with SR160...";
		textBox2.AddHandler(InputElement.KeyDownEvent, context.RootObject.TxtEpcInput_KeyDown);
		textBox2.AddHandler(TextBox.TextChangedEvent, context.RootObject.TxtEpcInput_TextChanged);
		((ISupportInitialize)textBox2).EndInit();
		Controls children44 = grid8.Children;
		Button button11;
		Button button10 = (button11 = new Button());
		((ISupportInitialize)button10).BeginInit();
		children44.Add(button10);
		button11.Name = "btnStartRead";
		element = button11;
		context.AvaloniaNameScope.Register("btnStartRead", element);
		button11.Content = "Start";
		Grid.SetColumn(button11, 2);
		button11.Width = 70.0;
		button11.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		button11.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.BtnStartRead_Click), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)button11).EndInit();
		Controls children45 = grid8.Children;
		Button button13;
		Button button12 = (button13 = new Button());
		((ISupportInitialize)button12).BeginInit();
		children45.Add(button12);
		button13.Name = "btnClearList";
		element = button13;
		context.AvaloniaNameScope.Register("btnClearList", element);
		button13.Content = "Clear";
		Grid.SetColumn(button13, 3);
		button13.Width = 90.0;
		button13.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		button13.AddHandler((RoutedEvent)Button.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.BtnClearList_Click), RoutingStrategies.Direct | RoutingStrategies.Bubble, false);
		((ISupportInitialize)button13).EndInit();
		((ISupportInitialize)grid8).EndInit();
		Controls children46 = stackPanel25.Children;
		TextBlock textBlock38;
		TextBlock textBlock37 = (textBlock38 = new TextBlock());
		((ISupportInitialize)textBlock37).BeginInit();
		children46.Add(textBlock37);
		textBlock38.Name = "lblTagCount";
		element = textBlock38;
		context.AvaloniaNameScope.Register("lblTagCount", element);
		textBlock38.Text = "Total: 0 tag";
		textBlock38.FontSize = 13.0;
		textBlock38.FontWeight = FontWeight.Bold;
		((ISupportInitialize)textBlock38).EndInit();
		Controls children47 = stackPanel25.Children;
		DataGrid dataGrid2;
		DataGrid dataGrid = (dataGrid2 = new DataGrid());
		((ISupportInitialize)dataGrid).BeginInit();
		children47.Add(dataGrid);
		DataGrid dataGrid4;
		DataGrid dataGrid3 = (dataGrid4 = dataGrid2);
		context.PushParent(dataGrid4);
		dataGrid4.Name = "dgTags";
		element = dataGrid4;
		context.AvaloniaNameScope.Register("dgTags", element);
		dataGrid4.Height = 160.0;
		dataGrid4.IsReadOnly = true;
		dataGrid4.CanUserResizeColumns = true;
		dataGrid4.GridLinesVisibility = DataGridGridLinesVisibility.All;
		dataGrid4.FontFamily = new FontFamily(((IUriContext)context).BaseUri, "Consolas,Menlo,monospace");
		dataGrid4.FontSize = 12.0;
		dataGrid4.AutoGenerateColumns = false;
		ObservableCollection<DataGridColumn> columns = dataGrid4.Columns;
		DataGridTextColumn dataGridTextColumn;
		DataGridTextColumn item = (dataGridTextColumn = new DataGridTextColumn());
		context.PushParent(dataGridTextColumn);
		DataGridTextColumn dataGridTextColumn2 = dataGridTextColumn;
		dataGridTextColumn2.Header = "#";
		ReflectionBindingExtension reflectionBindingExtension = new ReflectionBindingExtension("Index");
		context.ProvideTargetProperty = CompiledAvaloniaXaml.XamlIlHelpers.Avalonia_002EControls_002EDataGridBoundColumn_002CAvalonia_002EControls_002EDataGrid_002EBinding_0021Property();
		ReflectionBinding binding = reflectionBindingExtension.ProvideValue(context);
		context.ProvideTargetProperty = null;
		dataGridTextColumn2.Binding = binding;
		dataGridTextColumn2.Width = (DataGridLength)new DataGridLengthConverter().ConvertFrom(context, CultureInfo.InvariantCulture, "50");
		context.PopParent();
		columns.Add(item);
		ObservableCollection<DataGridColumn> columns2 = dataGrid4.Columns;
		DataGridTextColumn item2 = (dataGridTextColumn = new DataGridTextColumn());
		context.PushParent(dataGridTextColumn);
		DataGridTextColumn dataGridTextColumn3 = dataGridTextColumn;
		dataGridTextColumn3.Header = "EPC Code";
		ReflectionBindingExtension reflectionBindingExtension2 = new ReflectionBindingExtension("Epc");
		context.ProvideTargetProperty = CompiledAvaloniaXaml.XamlIlHelpers.Avalonia_002EControls_002EDataGridBoundColumn_002CAvalonia_002EControls_002EDataGrid_002EBinding_0021Property();
		ReflectionBinding binding2 = reflectionBindingExtension2.ProvideValue(context);
		context.ProvideTargetProperty = null;
		dataGridTextColumn3.Binding = binding2;
		dataGridTextColumn3.Width = (DataGridLength)new DataGridLengthConverter().ConvertFrom(context, CultureInfo.InvariantCulture, "*");
		context.PopParent();
		columns2.Add(item2);
		ObservableCollection<DataGridColumn> columns3 = dataGrid4.Columns;
		DataGridTextColumn item3 = (dataGridTextColumn = new DataGridTextColumn());
		context.PushParent(dataGridTextColumn);
		DataGridTextColumn dataGridTextColumn4 = dataGridTextColumn;
		dataGridTextColumn4.Header = "Count";
		ReflectionBindingExtension reflectionBindingExtension3 = new ReflectionBindingExtension("Count");
		context.ProvideTargetProperty = CompiledAvaloniaXaml.XamlIlHelpers.Avalonia_002EControls_002EDataGridBoundColumn_002CAvalonia_002EControls_002EDataGrid_002EBinding_0021Property();
		ReflectionBinding binding3 = reflectionBindingExtension3.ProvideValue(context);
		context.ProvideTargetProperty = null;
		dataGridTextColumn4.Binding = binding3;
		dataGridTextColumn4.Width = (DataGridLength)new DataGridLengthConverter().ConvertFrom(context, CultureInfo.InvariantCulture, "70");
		context.PopParent();
		columns3.Add(item3);
		context.PopParent();
		((ISupportInitialize)dataGrid3).EndInit();
		context.PopParent();
		((ISupportInitialize)stackPanel24).EndInit();
		context.PopParent();
		((ISupportInitialize)border11).EndInit();
		context.PopParent();
		((ISupportInitialize)stackPanel10).EndInit();
		context.PopParent();
		((ISupportInitialize)stackPanel3).EndInit();
		context.PopParent();
		((ISupportInitialize)P_1).EndInit();
		if (P_1 is StyledElement styled)
		{
			NameScope.SetNameScope(styled, context.AvaloniaNameScope);
		}
		context.AvaloniaNameScope.Complete();
	}

	[CompilerGenerated]
	private static void _0021XamlIlPopulateTrampoline(MainWindow P_0)
	{
		if (_0021XamlIlPopulateOverride != null)
		{
			_0021XamlIlPopulateOverride(P_0);
		}
		else
		{
			_0021XamlIlPopulate(XamlIlRuntimeHelpers.CreateRootServiceProviderV3(null), P_0);
		}
	}
}
