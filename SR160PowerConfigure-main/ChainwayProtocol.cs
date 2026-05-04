using System;
using System.IO.Ports;
using System.Threading;

namespace SR160PowerConfig
{
    public class ChainwayProtocol : IDisposable
    {
        private const byte FRAME_HEAD = 0xBB;
        private const byte FRAME_END = 0x7E;

        // Reader commands used by both native USB and serial/Bluetooth modes.
        private const byte CMD_GET_POWER = 0xB7;
        private const byte CMD_SET_POWER = 0xB6;
        private const byte CMD_INVENTORY = 0x22;
        private const byte CMD_STOP = 0x28;

        private SerialPort? _serial;
        private bool _useNativeUsb;
        public bool IsConnected { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        // Sends the inventory command. start=true begins reading; start=false stops reading.
        public int SendInventoryCommand(bool start, bool single = false)
        {
            if (!IsConnected) return -1;

            if (_useNativeUsb)
            {
                try
                {
                    if (!start)
                    {
                        int stopRet = UHFAPI.UHFStopGet();
                        LastError = $"UHFStopGet returned {stopRet}, io={SafeGetLastIoReturn()}";
                        return stopRet;
                    }

                    int ret = UHFAPI.UHFInventory();
                    LastError = $"UHFInventory returned {ret}, io={SafeGetLastIoReturn()}";
                    return ret;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    return -99;
                }
            }

            // Bluetooth / serial mode.
            byte cmd = start ? CMD_INVENTORY : CMD_STOP;
            byte[] frame = BuildFrame(0x00, cmd, Array.Empty<byte>());
            try
            {
                _serial?.Write(frame, 0, frame.Length);
                LastError = $"Serial command 0x{cmd:X2} sent";
                return 0;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return -2;
            }
        }

        public int ConnectSerial(string portName, int baud = 115200)
        {
            try
            {
                _serial?.Dispose();
                _serial = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) { ReadTimeout = 2000 };
                _serial.Open();
                IsConnected = true;
                _useNativeUsb = false;
                LastError = $"Serial connected: {portName}";
                return 0;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return -1;
            }
        }

        public int ConnectUsb()
        {
            try
            {
                // Call UsbOpen from UHFAPI.cs.
                int ret = UHFAPI.UsbOpen();
                if (ret == 0) { _useNativeUsb = true; IsConnected = true; }
                LastError = ret == 0 ? "UsbOpen returned 0" : $"UsbOpen returned {ret}, io={SafeGetLastIoReturn()}";
                return ret;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return -99;
            }
        }

        public int GetPower(ref byte power)
        {
            if (!IsConnected) return -1;
            if (_useNativeUsb)
            {
                try
                {
                    int ret = UHFAPI.UHFGetPower(ref power);
                    LastError = $"UHFGetPower returned {ret}, io={SafeGetLastIoReturn()}";
                    return ret;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    return -99;
                }
            }

            byte[] frame = BuildFrame(0x00, CMD_GET_POWER, Array.Empty<byte>());
            byte[]? resp = SerialTransact(frame);
            if (resp == null || resp.Length < 7) return -2;
            power = resp[5];
            return 0;
        }

        public int SetBuzzer(bool enabled, bool save = true)
        {
            if (!IsConnected) return -1;
            if (!_useNativeUsb) return -3;

            try
            {
                byte enable = (byte)(enabled ? 1 : 0);
                byte saveFlag = (byte)(save ? 1 : 0);

                int retSaveLast = UHFAPI.UHFSetBeep(saveFlag, enable);
                int retEnableFirst = UHFAPI.UHFSetBeep(enable, saveFlag);
                int retOffLast = enabled ? retEnableFirst : UHFAPI.UHFSetBeep(0, 0);

                LastError = $"UHFSetBeep attempts: save-first={retSaveLast}, enable-first={retEnableFirst}, off-last={retOffLast}, io={SafeGetLastIoReturn()}";
                return retSaveLast == 0 || retEnableFirst == 0 || retOffLast == 0 ? 0 : retOffLast;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return -99;
            }
        }

        public int SetPower(byte uPower)
        {
            if (!IsConnected) return -1;
            if (_useNativeUsb)
            {
                try
                {
                    int ret = UHFAPI.UHFSetPower(1, uPower);
                    LastError = $"UHFSetPower returned {ret}, io={SafeGetLastIoReturn()}";
                    return ret;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    return -99;
                }
            }

            byte[] frame = BuildFrame(0x00, CMD_SET_POWER, new byte[] { 0x01, uPower });
            return SerialTransact(frame) != null ? 0 : -2;
        }

        private byte[] BuildFrame(byte type, byte cmd, byte[] data)
        {
            int dlen = data.Length;
            byte[] frame = new byte[5 + dlen + 2];
            frame[0] = FRAME_HEAD;
            frame[1] = type;
            frame[2] = cmd;
            frame[3] = (byte)((dlen >> 8) & 0xFF);
            frame[4] = (byte)(dlen & 0xFF);
            Array.Copy(data, 0, frame, 5, dlen);
            byte cs = 0;
            for (int i = 1; i < 5 + dlen; i++) cs += frame[i];
            frame[5 + dlen] = cs;
            frame[6 + dlen] = FRAME_END;
            return frame;
        }

        private byte[]? SerialTransact(byte[] tx)
        {
            if (_serial == null || !_serial.IsOpen) return null;
            _serial.DiscardInBuffer();
            _serial.Write(tx, 0, tx.Length);
            Thread.Sleep(150);
            if (_serial.BytesToRead > 0)
            {
                byte[] resp = new byte[_serial.BytesToRead];
                _serial.Read(resp, 0, resp.Length);
                return resp;
            }
            return null;
        }

        public void Disconnect()
        {
            if (_useNativeUsb) try { UHFAPI.UsbClose(); } catch { }
            else _serial?.Close();
            IsConnected = false;
            _useNativeUsb = false;
        }

        public void Dispose() => Disconnect();

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
}
