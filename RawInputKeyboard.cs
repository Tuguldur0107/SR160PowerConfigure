using System;
using System.Runtime.InteropServices;

namespace SR160PowerConfig
{
    // Windows Raw Input API — unlike a WH_KEYBOARD_LL hook, this reports
    // WHICH physical HID device generated each keystroke (RAWINPUTHEADER.
    // hDevice), so the reader's own keyboard-wedge interface can be told
    // apart from the laptop's built-in keyboard by device identity instead
    // of guessing from which keys were pressed or how fast. That guessing
    // (a hex-digit/Enter key filter) is what caused ordinary typing to be
    // misread as trigger pulls. Synthetic input from SendInput/PostMessage
    // also does not carry a real device handle, so it's naturally excluded
    // too — no separate "injected" flag check needed here.
    internal static class RawInputKeyboard
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        private const int RIM_TYPEKEYBOARD = 1;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;

        public const int WM_INPUT = 0x00FF;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_SYSKEYDOWN = 0x0104;

        // Registers for keyboard raw input with RIDEV_INPUTSINK, so events
        // keep arriving at hwndTarget even while it isn't the focused/
        // foreground window — needed since the reader's trigger can fire
        // while any other app has focus.
        public static bool Register(IntPtr hwndTarget)
        {
            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[1];
            devices[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
            devices[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;
            devices[0].dwFlags = RIDEV_INPUTSINK;
            devices[0].hwndTarget = hwndTarget;
            return RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        // Call from WndProc when m.Msg == WM_INPUT. Returns false if this
        // wasn't a keyboard event we could parse.
        public static bool TryProcess(IntPtr lParam, out IntPtr deviceHandle, out int vkCode, out bool keyDown)
        {
            deviceHandle = IntPtr.Zero;
            vkCode = 0;
            keyDown = false;

            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint written = (uint)GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize);
                if (written != size) return false;

                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                if (header.dwType != RIM_TYPEKEYBOARD) return false;

                IntPtr keyboardPtr = new IntPtr(buffer.ToInt64() + headerSize);
                RAWKEYBOARD kb = (RAWKEYBOARD)Marshal.PtrToStructure(keyboardPtr, typeof(RAWKEYBOARD));

                deviceHandle = header.hDevice;
                vkCode = kb.VKey;
                keyDown = (kb.Message == WM_KEYDOWN || kb.Message == WM_SYSKEYDOWN);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // The device's stable interface path (encodes VID/PID), e.g.
        // "\\?\HID#VID_XXXX&PID_YYYY#...". Used only to show the user what
        // was learned; matching itself uses the raw device handle for the
        // current session (see MainForm's learnedDeviceHandle).
        public static string GetDeviceName(IntPtr hDevice)
        {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                int written = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref size);
                if (written < 0) return null;
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll")]
        private static extern int GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
    }
}
