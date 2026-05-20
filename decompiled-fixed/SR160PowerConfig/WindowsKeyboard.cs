using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SR160PowerConfig;

internal static class WindowsKeyboard
{
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

	private struct KEYBDINPUT
	{
		public ushort wVk;

		public char wScan;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct GUITHREADINFO
	{
		public int cbSize;

		public int flags;

		public nint hwndActive;

		public nint hwndFocus;

		public nint hwndCapture;

		public nint hwndMenuOwner;

		public nint hwndMoveSize;

		public nint hwndCaret;

		public RECT rcCaret;
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private const uint INPUT_KEYBOARD = 1u;

	private const uint KEYEVENTF_KEYUP = 2u;

	private const uint KEYEVENTF_UNICODE = 4u;

	private const int WM_CHAR = 258;

	private const int WM_KEYDOWN = 256;

	private const int WM_KEYUP = 257;

	public const int VK_RETURN = 13;

	private const ushort VK_SHIFT = 16;

	private const int SW_RESTORE = 9;

	public static KeyboardTarget GetCurrentInputTarget()
	{
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero)
		{
			return default(KeyboardTarget);
		}
		uint windowThreadProcessId = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
		GUITHREADINFO lpgui = new GUITHREADINFO
		{
			cbSize = Marshal.SizeOf<GUITHREADINFO>()
		};
		nint control = ((GetGUIThreadInfo(windowThreadProcessId, ref lpgui) && lpgui.hwndFocus != IntPtr.Zero) ? lpgui.hwndFocus : foregroundWindow);
		return new KeyboardTarget(foregroundWindow, control);
	}

	public static bool IsIgnoredTriggerKey(int virtualKey)
	{
		if ((uint)(virtualKey - 16) <= 2u || (uint)(virtualKey - 160) <= 5u)
		{
			return true;
		}
		return false;
	}

	public static bool IsSingleTriggerKey(int virtualKey)
	{
		if ((uint)(virtualKey - 71) <= 1u || virtualKey == 79)
		{
			return true;
		}
		return false;
	}

	public static void PlayDefaultBeep()
	{
		try
		{
			Console.Beep(950, 120);
		}
		catch
		{
			MessageBeep(0u);
		}
	}

	public static void SendTextLine(nint targetWindow, nint targetControl, string text)
	{
		nint num = ((targetWindow != IntPtr.Zero) ? targetWindow : GetForegroundWindow());
		if (num == IntPtr.Zero || !IsWindow(num))
		{
			throw new InvalidOperationException("No foreground window.");
		}
		if (targetControl != IntPtr.Zero && IsWindow(targetControl))
		{
			SendTextLineByMessage(targetControl, text);
			return;
		}
		FocusWindow(num);
		List<INPUT> list = new List<INPUT>();
		foreach (char character in text)
		{
			AddCharacterInputs(list, character);
		}
		list.Add(CreateVirtualKeyInput(13, keyUp: false));
		list.Add(CreateVirtualKeyInput(13, keyUp: true));
		uint num2 = SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
		if (num2 != list.Count)
		{
			throw new InvalidOperationException($"SendInput sent {num2} of {list.Count} events.");
		}
		Thread.Sleep(120);
	}

	private static void SendTextLineByMessage(nint targetControl, string text)
	{
		nint lParam = new IntPtr(1);
		foreach (char c in text)
		{
			if (!PostMessage(targetControl, 258, c, lParam))
			{
				throw new InvalidOperationException("PostMessage WM_CHAR failed.");
			}
		}
		PostMessage(targetControl, 256, 13, lParam);
		PostMessage(targetControl, 257, 13, lParam);
		Thread.Sleep(120);
	}

	private static void FocusWindow(nint hwnd)
	{
		ShowWindow(hwnd, 9);
		uint windowThreadProcessId = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
		uint windowThreadProcessId2 = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
		uint currentThreadId = GetCurrentThreadId();
		bool flag = windowThreadProcessId != 0 && windowThreadProcessId != currentThreadId && AttachThreadInput(currentThreadId, windowThreadProcessId, fAttach: true);
		bool flag2 = windowThreadProcessId2 != 0 && windowThreadProcessId2 != currentThreadId && AttachThreadInput(currentThreadId, windowThreadProcessId2, fAttach: true);
		try
		{
			SetForegroundWindow(hwnd);
		}
		finally
		{
			if (flag2)
			{
				AttachThreadInput(currentThreadId, windowThreadProcessId2, fAttach: false);
			}
			if (flag)
			{
				AttachThreadInput(currentThreadId, windowThreadProcessId, fAttach: false);
			}
		}
		Thread.Sleep(80);
	}

	private static void AddCharacterInputs(List<INPUT> inputs, char character)
	{
		if (character >= '0' && character <= '9')
		{
			ushort key = character;
			inputs.Add(CreateVirtualKeyInput(key, keyUp: false));
			inputs.Add(CreateVirtualKeyInput(key, keyUp: true));
			return;
		}
		char c = char.ToUpperInvariant(character);
		if (c >= 'A' && c <= 'Z')
		{
			ushort key2 = c;
			inputs.Add(CreateVirtualKeyInput(16, keyUp: false));
			inputs.Add(CreateVirtualKeyInput(key2, keyUp: false));
			inputs.Add(CreateVirtualKeyInput(key2, keyUp: true));
			inputs.Add(CreateVirtualKeyInput(16, keyUp: true));
		}
		else
		{
			inputs.Add(CreateUnicodeInput(character, keyUp: false));
			inputs.Add(CreateUnicodeInput(character, keyUp: true));
		}
	}

	private static INPUT CreateVirtualKeyInput(ushort key, bool keyUp)
	{
		return new INPUT
		{
			type = 1u,
			U = new InputUnion
			{
				ki = new KEYBDINPUT
				{
					wVk = key,
					wScan = '\0',
					dwFlags = (keyUp ? 2u : 0u)
				}
			}
		};
	}

	private static INPUT CreateUnicodeInput(char character, bool keyUp)
	{
		return new INPUT
		{
			type = 1u,
			U = new InputUnion
			{
				ki = new KEYBDINPUT
				{
					wVk = 0,
					wScan = character,
					dwFlags = (uint)(4 | (keyUp ? 2 : 0))
				}
			}
		};
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool MessageBeep(uint uType);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint GetWindowThreadProcessId(nint hWnd, nint lpdwProcessId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();
}
