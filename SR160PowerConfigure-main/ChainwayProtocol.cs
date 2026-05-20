using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SR160PowerConfig
{
    public class ChainwayProtocol : IDisposable
    {
        private const byte FRAME_HEAD = 0xBB;
        private const byte FRAME_END  = 0x7E;

        // Commands
        private const byte CMD_GET_POWER = 0xB7;
        private const byte CMD_SET_POWER = 0xB6;

        private static readonly int[] COMMON_BAUD_RATES = { 115200, 57600, 38400, 19200, 9600 };

        private SerialPort? _serial;
        private bool _useNativeUsb;
        private bool _disposed;
        private string _lastDebug = "";

        public bool IsConnected { get; private set; }
        public string ConnectionType => _useNativeUsb ? "USB" : "Bluetooth";
        public string LastDebug => _lastDebug;

        /// <summary>
        /// Connect via Bluetooth serial with auto baud rate detection.
        /// Tries each common baud rate and sends a GetPower command to verify.
        /// </summary>
        public int ConnectSerial(string portName, int specificBaud = 0)
        {
            int[] bauds = specificBaud > 0
                ? new[] { specificBaud }
                : COMMON_BAUD_RATES;

            foreach (int baud in bauds)
            {
                try
                {
                    _serial?.Dispose();
                    _serial = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout  = 2000,
                        WriteTimeout = 2000,
                        DtrEnable    = true,
                        RtsEnable    = true,
                        Handshake    = Handshake.None
                    };
                    _serial.Open();

                    // Flush any stale data
                    Thread.Sleep(100);
                    _serial.DiscardInBuffer();
                    _serial.DiscardOutBuffer();

                    // Test with GetPower command
                    byte[] testFrame = BuildFrame(0x00, CMD_GET_POWER, Array.Empty<byte>());
                    byte[]? resp = SerialTransact(testFrame);

                    if (resp != null && resp.Length >= 7 && resp[0] == FRAME_HEAD)
                    {
                        _useNativeUsb = false;
                        IsConnected = true;
                        _lastDebug = $"Холбогдлоо: {portName} @ {baud} baud";
                        return 0;
                    }

                    _serial.Close();
                    _serial.Dispose();
                    _serial = null;
                }
                catch
                {
                    try { _serial?.Close(); } catch { }
                    _serial?.Dispose();
                    _serial = null;
                }
            }

            _lastDebug = $"Бүх baud rate-д хариу ирсэнгүй ({string.Join(", ", bauds)})";
            // Fallback: just open at 115200 without verification
            return ConnectSerialRaw(portName, 115200);
        }

        /// <summary>
        /// Open serial port without protocol verification (fallback).
        /// </summary>
        private int ConnectSerialRaw(string portName, int baud)
        {
            try
            {
                _serial?.Dispose();
                _serial = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout  = 2000,
                    WriteTimeout = 2000,
                    DtrEnable    = true,
                    RtsEnable    = true,
                    Handshake    = Handshake.None
                };
                _serial.Open();
                Thread.Sleep(100);
                _serial.DiscardInBuffer();

                _useNativeUsb = false;
                IsConnected = true;
                _lastDebug += $"\nFallback: {portName} @ {baud} (баталгаажуулалтгүй)";
                return 0;
            }
            catch (Exception ex)
            {
                _lastDebug += $"\nFallback алдаа: {ex.Message}";
                _serial?.Dispose();
                _serial = null;
                return -1;
            }
        }

        /// <summary>
        /// Connect via USB. On macOS, uses sr160_power helper with admin privileges.
        /// On Windows, uses native UHFAPI.dll.
        /// </summary>
        public int ConnectUsb()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS: test connection by running GetPower via helper
                string result = RunHelperMac("get");
                if (result.StartsWith("OK"))
                {
                    _useNativeUsb = true;
                    IsConnected = true;
                    _lastDebug = "USB холбогдлоо (macOS helper)";
                    return 0;
                }
                _lastDebug = "USB helper: " + result;
                return -1;
            }

            try
            {
                int ret = UHFAPI.UsbOpen();
                if (ret == 0)
                {
                    _useNativeUsb = true;
                    IsConnected = true;
                    _lastDebug = "USB холбогдлоо";
                }
                return ret;
            }
            catch (Exception ex)
            {
                _lastDebug = "USB алдаа: " + ex.Message;
                return -99;
            }
        }

        public void Disconnect()
        {
            if (_useNativeUsb && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try { UHFAPI.UsbClose(); } catch { }
            }
            else if (!_useNativeUsb)
            {
                try { _serial?.Close(); } catch { }
                _serial?.Dispose();
                _serial = null;
            }
            IsConnected = false;
        }

        public int GetPower(ref byte power)
        {
            if (!IsConnected) return -1;

            if (_useNativeUsb)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string result = RunHelperMac("get");
                    _lastDebug = "GetPower: " + result;
                    if (result.StartsWith("OK ") && byte.TryParse(result.Substring(3).Trim(), out byte p))
                    {
                        power = p;
                        return 0;
                    }
                    return -2;
                }

                int ret = UHFAPI.UHFGetPower(ref power);
                try { int ior = UHFAPI.UHFGetLastIOReturn(); _lastDebug = $"GetPower ret={ret}, IOReturn=0x{ior:X8}"; }
                catch { _lastDebug = $"GetPower ret={ret}"; }
                return ret;
            }

            byte[] frame = BuildFrame(0x00, CMD_GET_POWER, Array.Empty<byte>());
            byte[]? resp = SerialTransact(frame);

            if (resp == null)
            {
                _lastDebug = "GetPower: хариу ирсэнгүй\nИлгээсэн: " + BytesToHex(frame);
                return -2;
            }

            _lastDebug = "GetPower TX: " + BytesToHex(frame) + "\nGetPower RX: " + BytesToHex(resp);

            byte[]? data = ParseResponse(resp);
            if (data == null || data.Length < 1)
            {
                _lastDebug += "\nParse алдаа: data=" + (data == null ? "null" : data.Length.ToString());
                return -3;
            }

            power = data[0];
            return 0;
        }

        public int SetPower(byte save, byte uPower)
        {
            if (!IsConnected) return -1;

            if (_useNativeUsb)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string result = RunHelperMac($"set {save} {uPower}");
                    _lastDebug = "SetPower: " + result;
                    return result.StartsWith("OK") ? 0 : -2;
                }

                int ret = UHFAPI.UHFSetPower(save, uPower);
                try { int ior = UHFAPI.UHFGetLastIOReturn(); _lastDebug = $"SetPower ret={ret}, IOReturn=0x{ior:X8}"; }
                catch { _lastDebug = $"SetPower ret={ret}"; }
                return ret;
            }

            byte[] frame = BuildFrame(0x00, CMD_SET_POWER, new byte[] { save, uPower });
            byte[]? resp = SerialTransact(frame);

            if (resp == null)
            {
                _lastDebug = "SetPower: хариу ирсэнгүй\nИлгээсэн: " + BytesToHex(frame);
                return -2;
            }

            _lastDebug = "SetPower TX: " + BytesToHex(frame) + "\nSetPower RX: " + BytesToHex(resp);

            byte[]? data = ParseResponse(resp);
            if (data == null)
            {
                _lastDebug += "\nParse алдаа";
                return -3;
            }

            return 0;
        }

        /// <summary>
        /// Run sr160_power helper on macOS with admin privileges.
        /// Uses osascript to prompt for password.
        /// </summary>
        private string RunHelperMac(string args)
        {
            try
            {
                // Find helper next to the app binary
                string appDir = AppContext.BaseDirectory;
                string helperPath = Path.Combine(appDir, "sr160_power");

                if (!File.Exists(helperPath))
                {
                    // Try native directory
                    string nativeDir = Path.Combine(Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "native");
                    helperPath = Path.Combine(nativeDir, "sr160_power");
                }

                if (!File.Exists(helperPath))
                {
                    // Try project native dir
                    helperPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "sr160_power");
                }

                if (!File.Exists(helperPath))
                    return "ERROR: sr160_power not found";

                // Use osascript to run with admin privileges
                string escapedCmd = $"{helperPath} {args}".Replace("\"", "\\\"");
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e 'do shell script \"{escapedCmd}\" with administrator privileges'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return "ERROR: process failed";

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(10000);

                if (proc.ExitCode != 0)
                    return "ERROR: " + stderr.Trim();

                return stdout.Trim();
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        /// <summary>
        /// Send raw bytes and return raw response (for debugging).
        /// </summary>
        public byte[]? SendRaw(byte[] data)
        {
            if (_serial == null || !_serial.IsOpen) return null;
            return SerialTransact(data);
        }

        public static string[] GetAvailablePorts()
        {
            try
            {
                string[] ports = SerialPort.GetPortNames();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return ports
                        .Where(p => p.StartsWith("/dev/cu."))
                        .OrderBy(p => p)
                        .ToArray();
                }

                return ports.OrderBy(p => p).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // ── Frame building / parsing ──

        private static byte[] BuildFrame(byte type, byte cmd, byte[] data)
        {
            int dlen = data.Length;
            byte[] frame = new byte[5 + dlen + 2];
            int idx = 0;
            frame[idx++] = FRAME_HEAD;
            frame[idx++] = type;
            frame[idx++] = cmd;
            frame[idx++] = (byte)((dlen >> 8) & 0xFF);
            frame[idx++] = (byte)(dlen & 0xFF);
            Array.Copy(data, 0, frame, idx, dlen);
            idx += dlen;

            byte cs = 0;
            for (int i = 1; i < idx; i++)
                cs += frame[i];
            frame[idx++] = cs;
            frame[idx++] = FRAME_END;
            return frame;
        }

        private static byte[]? ParseResponse(byte[] rx)
        {
            if (rx.Length < 7) return null;
            if (rx[0] != FRAME_HEAD) return null;

            // Find FRAME_END
            int endIdx = -1;
            for (int i = rx.Length - 1; i >= 6; i--)
            {
                if (rx[i] == FRAME_END) { endIdx = i; break; }
            }
            if (endIdx < 0) return null;

            int pl = (rx[3] << 8) | rx[4];
            if (endIdx < 5 + pl + 1) return null;

            // Checksum verification (optional — some devices don't match perfectly)
            // byte cs = 0;
            // for (int i = 1; i < 5 + pl; i++) cs += rx[i];
            // if (cs != rx[5 + pl]) return null;

            byte[] data = new byte[pl];
            if (pl > 0)
                Array.Copy(rx, 5, data, 0, pl);
            return data;
        }

        private byte[]? SerialTransact(byte[] tx)
        {
            if (_serial == null || !_serial.IsOpen) return null;

            try
            {
                _serial.DiscardInBuffer();
                _serial.Write(tx, 0, tx.Length);

                // Wait for response with adaptive timing
                var buf = new List<byte>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int timeout = 3000;
                bool gotHeader = false;
                int expectedLen = -1;

                while (sw.ElapsedMilliseconds < timeout)
                {
                    int avail = _serial.BytesToRead;
                    if (avail > 0)
                    {
                        byte[] chunk = new byte[avail];
                        int read = _serial.Read(chunk, 0, avail);
                        for (int i = 0; i < read; i++)
                        {
                            byte b = chunk[i];

                            if (!gotHeader)
                            {
                                if (b == FRAME_HEAD)
                                {
                                    gotHeader = true;
                                    buf.Add(b);
                                }
                                continue;
                            }

                            buf.Add(b);

                            // Once we have 5 bytes, calculate expected length
                            if (buf.Count == 5 && expectedLen < 0)
                            {
                                int pl = (buf[3] << 8) | buf[4];
                                expectedLen = 5 + pl + 2; // head+type+cmd+pl(2) + data + cs + end
                            }

                            // Check if we have a complete frame
                            if (expectedLen > 0 && buf.Count >= expectedLen)
                                return buf.ToArray();
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                // Return whatever we got
                return buf.Count > 0 ? buf.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string BytesToHex(byte[] data)
        {
            return string.Join(" ", data.Select(b => b.ToString("X2")));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
