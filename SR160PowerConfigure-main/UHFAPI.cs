using System;
using System.Runtime.InteropServices;

namespace SR160PowerConfig
{
    public static class UHFAPI
    {
        // .NET runtime automatically resolves the library name per platform:
        //   Windows: UHFAPI.dll
        //   macOS:   libUHFAPI.dylib
        //   Linux:   libUHFAPI.so
        private const string DLL = "UHFAPI";

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UsbOpen();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void UsbClose();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFSetPower(byte save, byte uPower);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetPower(ref byte uPower);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFSetBeep(byte save, byte enable);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UHFGetBeep(ref byte enable);

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
        public static extern int UHFGetLastIOReturn();
    }
}
