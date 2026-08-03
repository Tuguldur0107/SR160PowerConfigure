using System;
using System.Runtime.InteropServices;

namespace SR160PowerConfig
{
    public static class UHFAPI
    {
        private const string DLL = "UHFAPI.dll";

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UsbOpen();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void UsbClose();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFSetPower(byte save, byte uPower);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetPower(ref byte uPower);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetAntennaPower(byte[] ppower, ref int nBytesReturned);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFSetAntennaPower(byte save, byte num, byte read_power, byte write_power);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetHardwareVersion(byte[] version);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetSoftwareVersion(byte[] version);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetReaderVersion(byte[] version);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFInventory();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFStopGet();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHF_GetReceived_EX(ref int uLenUii, byte[] uUii);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetBeep(ref byte mode);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFSetBeep(byte save, byte mode);

        // mode: 0 = Dual (continuous inventory), 1 = Single (one tag then stop).
        // Governs this reader's own on-board inventory session — the same
        // session our SDK-driven scan loop (UHFInventory/UHF_GetReceived_EX)
        // uses. save=1 persists the choice in the reader's flash.
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFSetDualSingelMode(byte save, byte mode);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetDualSingelMode(ref byte mode);

    }
}
