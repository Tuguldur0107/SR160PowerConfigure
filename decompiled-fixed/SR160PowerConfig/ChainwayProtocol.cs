using System;
using System.IO.Ports;
using System.Threading;

namespace SR160PowerConfig;

public class ChainwayProtocol : IDisposable
{
	private const byte FRAME_HEAD = 187;

	private const byte FRAME_END = 126;

	private const byte CMD_GET_POWER = 183;

	private const byte CMD_SET_POWER = 182;

	private const byte CMD_INVENTORY = 34;

	private const byte CMD_STOP = 40;

	private SerialPort? _serial;

	private bool _useNativeUsb;

	public bool IsConnected { get; private set; }

	public string LastError { get; private set; } = string.Empty;

	public int SendInventoryCommand(bool start, bool single = false)
	{
		if (!IsConnected)
		{
			return -1;
		}
		if (_useNativeUsb)
		{
			try
			{
				if (!start)
				{
					int num = UHFAPI.UHFStopGet();
					LastError = $"UHFStopGet returned {num}, io={SafeGetLastIoReturn()}";
					return num;
				}
				int num2 = UHFAPI.UHFInventory();
				LastError = $"UHFInventory returned {num2}, io={SafeGetLastIoReturn()}";
				return num2;
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name + ": " + ex.Message;
				return -99;
			}
		}
		byte b = (byte)(start ? 34 : 40);
		byte[] array = BuildFrame(0, b, Array.Empty<byte>());
		try
		{
			_serial?.Write(array, 0, array.Length);
			LastError = $"Serial command 0x{b:X2} sent";
			return 0;
		}
		catch (Exception ex2)
		{
			LastError = ex2.GetType().Name + ": " + ex2.Message;
			return -2;
		}
	}

	public int ConnectSerial(string portName, int baud = 115200)
	{
		try
		{
			_serial?.Dispose();
			_serial = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
			{
				ReadTimeout = 2000
			};
			_serial.Open();
			IsConnected = true;
			_useNativeUsb = false;
			LastError = "Serial connected: " + portName;
			return 0;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
			return -1;
		}
	}

	public int ConnectUsb()
	{
		try
		{
			int num = UHFAPI.UsbOpen();
			if (num == 0)
			{
				_useNativeUsb = true;
				IsConnected = true;
			}
			LastError = ((num == 0) ? "UsbOpen returned 0" : $"UsbOpen returned {num}, io={SafeGetLastIoReturn()}");
			return num;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
			return -99;
		}
	}

	public int GetPower(ref byte power)
	{
		if (!IsConnected)
		{
			return -1;
		}
		if (_useNativeUsb)
		{
			try
			{
				int num = UHFAPI.UHFGetPower(ref power);
				LastError = $"UHFGetPower returned {num}, io={SafeGetLastIoReturn()}";
				return num;
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name + ": " + ex.Message;
				return -99;
			}
		}
		byte[] tx = BuildFrame(0, 183, Array.Empty<byte>());
		byte[] array = SerialTransact(tx);
		if (array == null || array.Length < 7)
		{
			return -2;
		}
		power = array[5];
		return 0;
	}

	public int SetBuzzer(bool enabled, bool save = true)
	{
		if (!IsConnected)
		{
			return -1;
		}
		if (!_useNativeUsb)
		{
			return -3;
		}
		try
		{
			byte b = (enabled ? ((byte)1) : ((byte)0));
			byte b2 = (save ? ((byte)1) : ((byte)0));
			int num = UHFAPI.UHFSetBeep(b2, b);
			int num2 = UHFAPI.UHFSetBeep(b, b2);
			int num3 = (enabled ? num2 : UHFAPI.UHFSetBeep(0, 0));
			LastError = $"UHFSetBeep attempts: save-first={num}, enable-first={num2}, off-last={num3}, io={SafeGetLastIoReturn()}";
			return (num != 0 && num2 != 0 && num3 != 0) ? num3 : 0;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
			return -99;
		}
	}

	public int SetPower(byte uPower)
	{
		if (!IsConnected)
		{
			return -1;
		}
		if (_useNativeUsb)
		{
			try
			{
				int num = UHFAPI.UHFSetPower(1, uPower);
				LastError = $"UHFSetPower returned {num}, io={SafeGetLastIoReturn()}";
				return num;
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name + ": " + ex.Message;
				return -99;
			}
		}
		byte[] tx = BuildFrame(0, 182, new byte[2] { 1, uPower });
		if (SerialTransact(tx) == null)
		{
			return -2;
		}
		return 0;
	}

	private byte[] BuildFrame(byte type, byte cmd, byte[] data)
	{
		int num = data.Length;
		byte[] array = new byte[5 + num + 2];
		array[0] = 187;
		array[1] = type;
		array[2] = cmd;
		array[3] = (byte)((num >> 8) & 0xFF);
		array[4] = (byte)(num & 0xFF);
		Array.Copy(data, 0, array, 5, num);
		byte b = 0;
		for (int i = 1; i < 5 + num; i++)
		{
			b += array[i];
		}
		array[5 + num] = b;
		array[6 + num] = 126;
		return array;
	}

	private byte[]? SerialTransact(byte[] tx)
	{
		if (_serial == null || !_serial.IsOpen)
		{
			return null;
		}
		_serial.DiscardInBuffer();
		_serial.Write(tx, 0, tx.Length);
		Thread.Sleep(150);
		if (_serial.BytesToRead > 0)
		{
			byte[] array = new byte[_serial.BytesToRead];
			_serial.Read(array, 0, array.Length);
			return array;
		}
		return null;
	}

	public void Disconnect()
	{
		if (_useNativeUsb)
		{
			try
			{
				UHFAPI.UsbClose();
			}
			catch
			{
			}
		}
		else
		{
			_serial?.Close();
		}
		IsConnected = false;
		_useNativeUsb = false;
	}

	public void Dispose()
	{
		Disconnect();
	}

	private static int SafeGetLastIoReturn()
	{
		try
		{
			return UHFAPI.UHFGetLastIOReturn();
		}
		catch
		{
			return 0;
		}
	}
}
