using System;
using System.Runtime.InteropServices;

namespace SR160PowerConfig;

internal sealed class GlobalKeyboardHook : IDisposable
{
	private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

	private struct KBDLLHOOKSTRUCT
	{
		public int vkCode;

		public int scanCode;

		public int flags;

		public int time;

		public nint dwExtraInfo;
	}

	private const int WH_KEYBOARD_LL = 13;

	private const int WM_KEYDOWN = 256;

	private const int LLKHF_INJECTED = 16;

	private readonly Func<int, bool> _onKeyDown;

	private readonly LowLevelKeyboardProc _proc;

	private readonly nint _hookId;

	public bool IsInstalled => _hookId != IntPtr.Zero;

	public GlobalKeyboardHook(Func<int, bool> onKeyDown)
	{
		_onKeyDown = onKeyDown;
		_proc = HookCallback;
		_hookId = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0u);
	}

	public void Dispose()
	{
		if (_hookId != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_hookId);
		}
	}

	private nint HookCallback(int nCode, nint wParam, nint lParam)
	{
		if (nCode >= 0 && wParam == 256)
		{
			KBDLLHOOKSTRUCT kBDLLHOOKSTRUCT = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
			if ((kBDLLHOOKSTRUCT.flags & 0x10) == 0 && _onKeyDown(kBDLLHOOKSTRUCT.vkCode))
			{
				return 1;
			}
		}
		return CallNextHookEx(_hookId, nCode, wParam, lParam);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint GetModuleHandle(string? lpModuleName);
}
