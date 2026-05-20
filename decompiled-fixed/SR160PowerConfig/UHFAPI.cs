using System.Runtime.InteropServices;

namespace SR160PowerConfig;

public static class UHFAPI
{
	private const string DLL = "UHFAPI";

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UsbOpen();

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern void UsbClose();

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFSetPower(byte save, byte uPower);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetPower(ref byte uPower);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFSetBeep(byte save, byte enable);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetBeep(ref byte enable);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetAntennaPower(byte[] ppower, ref int nBytesReturned);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFSetAntennaPower(byte save, byte num, byte read_power, byte write_power);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetHardwareVersion(byte[] version);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetSoftwareVersion(byte[] version);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetReaderVersion(byte[] version);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFInventory();

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFStopGet();

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHF_GetReceived_EX(ref int uLenUii, byte[] uUii);

	[DllImport("UHFAPI", CallingConvention = CallingConvention.Cdecl)]
	public static extern int UHFGetLastIOReturn();
}
