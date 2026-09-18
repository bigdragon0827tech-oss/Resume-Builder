using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResumeBuilder;

public static class KeyboardSimulator
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT   = 0x10;
    private const ushort VK_I       = 0xBA;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    // IMPORTANT:
    // The union must contain MOUSEINPUT too so its size matches
    // the native Windows INPUT union on both x86 and x64.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint nInputs,
        INPUT[] pInputs,
        int cbSize);

    private static INPUT KeyDown(ushort vk)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static INPUT KeyUp(ushort vk)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    public static void SendCtrlShiftI()
    {
        INPUT[] inputs =
        {
            KeyDown(VK_CONTROL),
            KeyDown(VK_SHIFT),
            KeyDown(VK_I),

            KeyUp(VK_I),
            KeyUp(VK_SHIFT),
            KeyUp(VK_CONTROL)
        };

        int inputSize = Marshal.SizeOf<INPUT>();

        PerfLog.Line($"SendInput INPUT size={inputSize}");

        uint sent = SendInput(
            (uint)inputs.Length,
            inputs,
            inputSize);

        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                $"SendInput sent {sent}/{inputs.Length}; Win32 error={error}");
        }

        PerfLog.Line($"SendInput successfully sent {sent}/6 events");
    }
}