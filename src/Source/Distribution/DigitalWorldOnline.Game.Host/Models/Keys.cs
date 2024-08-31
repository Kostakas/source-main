using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

public class KeySender
{
    // Define necessary constants
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    [DllImport("user32.dll",CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName,string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd,uint Msg,IntPtr wParam,IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowText(IntPtr hWnd,StringBuilder text,int count);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc,IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd,IntPtr lParam);

    public static IntPtr FindWindowByExecutableName(string exeName)
    {
        IntPtr foundWindowHandle = IntPtr.Zero;

        EnumWindows((hWnd,lParam) =>
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hWnd,sb,sb.Capacity);

            if (Process.GetProcesses().Any(p => p.MainWindowHandle == hWnd && p.MainModule.FileName.EndsWith(exeName,StringComparison.OrdinalIgnoreCase)))
            {
                foundWindowHandle = hWnd;
                return false; // Stop enumeration
            }

            return true; // Continue enumeration
        },IntPtr.Zero);

        return foundWindowHandle;
    }

    // Define the INPUT structure
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
        public KEYBDINPUT ki;
        public HARDWAREINPUT hi;
    }

    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static async Task SendKeyToWindowAsync(IntPtr hWnd,ushort keyCode)
    {
        // Send WM_KEYDOWN message
        PostMessage(hWnd,WM_KEYDOWN,(IntPtr)keyCode,IntPtr.Zero);
        await Task.Delay(50);

        // Send WM_KEYUP message
        PostMessage(hWnd,WM_KEYUP,(IntPtr)keyCode,IntPtr.Zero);
    }
}
