// RingAction - radial ring menu triggered by a keyboard shortcut (e.g. a mouse button that emits Win+D).
// C# 5 / .NET Framework 4.8, no third-party dependencies. See README.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("RingAction")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]
[assembly: System.Reflection.AssemblyDescription("Radial ring menu for Windows")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 RingAction contributors. MIT License.")]

namespace RingAction
{
    // ---------------------------------------------------------------- Win32
    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204,
                         WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
        public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10;
        public static readonly IntPtr MarkerExtraInfo = (IntPtr)0x41524E47; // "ARNG": tags our own injected input

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        // Gives this process keyboard focus on a window it owns. A background process is normally refused the foreground;
        // briefly sharing input state with the current foreground thread is the documented way around that. No input is sent.
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        public static IntPtr Foreground() { return GetForegroundWindow(); }
        public static bool AnyMouseButtonDown() { return ((GetAsyncKeyState(1) | GetAsyncKeyState(2) | GetAsyncKeyState(4)) & 0x8000) != 0; }
        public static void Focus(IntPtr hwnd)
        {
            uint pid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid), me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { SetForegroundWindow(hwnd); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
        }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        public static IntPtr InstallHook(int id, HookProc proc)
        {
            return SetWindowsHookEx(id, proc, GetModuleHandle(null), 0);
        }
        public static void Unhook(IntPtr h) { if (h != IntPtr.Zero) UnhookWindowsHookEx(h); }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        // Name of the foreground window's process, as Process.ProcessName would give it ("chrome", "Figma").
        // Runs inside the keyboard hook, so it asks only for this one process instead of letting
        // Process.GetProcessById snapshot every process on the machine.
        public static string ForegroundProcessName()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == 0) return "";
                IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        uint len = (uint)sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref len)) return Path.GetFileNameWithoutExtension(sb.ToString());
                    }
                    finally { CloseHandle(h); }
                }
                // Rare (protected processes): fall back to the slow but complete lookup.
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return ""; }
        }

        static bool IsExtended(int vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C ||
                   vk == 0x5D || vk == 0x6F || vk == 0x90 || vk == 0xA3 || vk == 0xA5;
        }

        static INPUT Key(int vk, bool up)
        {
            INPUT i = new INPUT();
            i.type = 1; // INPUT_KEYBOARD
            i.u.ki.wVk = (ushort)vk;
            i.u.ki.dwFlags = (up ? 2u : 0u) | (IsExtended(vk) ? 1u : 0u); // KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY
            i.u.ki.dwExtraInfo = MarkerExtraInfo;
            return i;
        }

        public static void SendKeys(List<int> downOrder) { SendKeys(downOrder, 0); }

        // holdMs > 0: press, keep the keys down for that long, then release (must not run on the hook/UI thread).
        public static void SendKeys(List<int> downOrder, int holdMs)
        {
            List<INPUT> l = new List<INPUT>();
            foreach (int vk in downOrder) l.Add(Key(vk, false));
            if (holdMs <= 0)
            {
                for (int i = downOrder.Count - 1; i >= 0; i--) l.Add(Key(downOrder[i], true));
                SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(INPUT)));
                return;
            }
            try
            {
                SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(INPUT)));
                System.Threading.Thread.Sleep(holdMs);
            }
            finally   // never leave keys down
            {
                l.Clear();
                for (int i = downOrder.Count - 1; i >= 0; i--) l.Add(Key(downOrder[i], true));
                SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            }
        }

        public const int TextKeyGapMs = 15;

        // Types text as Unicode characters (KEYEVENTF_UNICODE), independent of keyboard layout.
        // One character per SendInput with a short gap: a single big batch overflows the target's input queue on long
        // text (characters are lost), and apps that handle input asynchronously (Windows 11 Notepad) garble a burst.
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint uPeriod);

        public static void SendText(string s)
        {
            timeBeginPeriod(1);   // 1 ms sleeps while typing, so the gap is ~15 ms rather than one or two 15.6 ms ticks
            try { SendTextPaced(s); }
            finally { timeEndPeriod(1); }
        }

        static void SendTextPaced(string s)
        {
            INPUT[] pair = new INPUT[2];
            foreach (char ch in s)
            {
                if (ch == '\r') continue;
                if (ch == '\n') { pair[0] = Key(0x0D, false); pair[1] = Key(0x0D, true); }
                else
                {
                    INPUT d = new INPUT();
                    d.type = 1;
                    d.u.ki.wScan = ch;
                    d.u.ki.dwFlags = 4;                 // KEYEVENTF_UNICODE
                    d.u.ki.dwExtraInfo = MarkerExtraInfo;
                    INPUT u = d;
                    u.u.ki.dwFlags = 4 | 2;             // KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    pair[0] = d; pair[1] = u;
                }
                SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
                System.Threading.Thread.Sleep(TextKeyGapMs);
            }
        }

        // Waits (up to maxMs) until no modifier key is down, e.g. the Win of a Win+D trigger the user is still holding,
        // so it does not combine with the keys a macro sends. True if they all came up in time.
        public static bool WaitModifiersUp(int maxMs)
        {
            int start = Environment.TickCount;
            int[] mods = { 0x5B, 0x5C, 0x10, 0x11, 0x12 };
            while (true)
            {
                bool any = false;
                foreach (int vk in mods) if ((GetAsyncKeyState(vk) & 0x8000) != 0) { any = true; break; }
                if (!any) return true;
                if (Environment.TickCount - start > maxMs) return false;
                System.Threading.Thread.Sleep(10);
            }
        }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

        // Effective DPI of the monitor under the point, as a scale factor (1.0 = 96 DPI).
        public static float MonitorScale(Point p)
        {
            try
            {
                POINT pt = new POINT(); pt.x = p.X; pt.y = p.Y;
                IntPtr mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
            catch { }   // shcore.dll needs Windows 8.1+
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        // Settings > Accessibility > Visual effects > Animation effects (SPI_GETCLIENTAREAANIMATION).
        public static bool AnimationsEnabled()
        {
            int on = 1;
            try { if (!SystemParametersInfo(0x1042, 0, ref on, 0)) return true; } catch { return true; }
            return on != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        // A 32-bit premultiplied DIB that stays selected into a memory DC for the life of the ring, so a frame is
        // "draw into memory, UpdateLayeredWindow" with no per-frame bitmap allocation or conversion.
        public sealed class Surface : IDisposable
        {
            IntPtr memDc, hBmp, oldBmp, bits;
            public Bitmap Bmp;
            public Graphics G;
            public int W, H;

            public void Ensure(int w, int h)
            {
                if (G != null && w == W && h == H) return;
                Dispose();
                BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                bi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bi.biWidth = w; bi.biHeight = -h;   // negative = top-down, matching GDI+ row order
                bi.biPlanes = 1; bi.biBitCount = 32; bi.biCompression = 0;   // BI_RGB
                IntPtr screen = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screen);
                ReleaseDC(IntPtr.Zero, screen);
                hBmp = CreateDIBSection(memDc, ref bi, 0 /* DIB_RGB_COLORS */, out bits, IntPtr.Zero, 0);
                if (hBmp == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                oldBmp = SelectObject(memDc, hBmp);
                Bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                G = Graphics.FromImage(Bmp);
                G.SmoothingMode = SmoothingMode.AntiAlias;
                G.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                W = w; H = h;
            }

            public void Present(IntPtr hwnd, Point loc, byte alpha)
            {
                if (G == null) return;
                IntPtr screen = GetDC(IntPtr.Zero);
                try
                {
                    POINT dst = new POINT(); dst.x = loc.X; dst.y = loc.Y;
                    POINT src = new POINT();
                    SIZE size = new SIZE(); size.cx = W; size.cy = H;
                    BLENDFUNCTION bf = new BLENDFUNCTION();
                    bf.BlendOp = 0; bf.SourceConstantAlpha = alpha; bf.AlphaFormat = 1;   // AC_SRC_OVER, AC_SRC_ALPHA
                    UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memDc, ref src, 0, ref bf, 2 /* ULW_ALPHA */);
                }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }

            public void Dispose()
            {
                if (G != null) { G.Dispose(); G = null; }
                if (Bmp != null) { Bmp.Dispose(); Bmp = null; }
                if (memDc != IntPtr.Zero)
                {
                    if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    DeleteDC(memDc);
                }
                memDc = hBmp = oldBmp = bits = IntPtr.Zero;
                W = H = 0;
            }
        }

    }

    // ---------------------------------------------------------------- Chords ("Win+D", "Ctrl+Shift+T")
    sealed class Chord
    {
        public bool Win, Ctrl, Alt, Shift;
        public int Vk;
        public string Text;

        public static bool IsWin(int vk) { return vk == 0x5B || vk == 0x5C; }
        public static bool IsCtrl(int vk) { return vk == 0x11 || vk == 0xA2 || vk == 0xA3; }
        public static bool IsAlt(int vk) { return vk == 0x12 || vk == 0xA4 || vk == 0xA5; }
        public static bool IsShift(int vk) { return vk == 0x10 || vk == 0xA0 || vk == 0xA1; }
        public static bool IsModifier(int vk) { return IsWin(vk) || IsCtrl(vk) || IsAlt(vk) || IsShift(vk); }

        public static Chord Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) throw new FormatException("empty shortcut");
            Chord c = new Chord(); c.Text = s;
            string[] parts = s.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim().ToLowerInvariant();
                if (t.Length == 0) { if (i == parts.Length - 1) t = "+"; else continue; }
                bool last = i == parts.Length - 1;
                if (!last)
                {
                    if (t == "win" || t == "windows" || t == "meta" || t == "super") c.Win = true;
                    else if (t == "ctrl" || t == "control") c.Ctrl = true;
                    else if (t == "alt") c.Alt = true;
                    else if (t == "shift") c.Shift = true;
                    else throw new FormatException("'" + parts[i] + "' is not a modifier in '" + s + "'");
                }
                else
                {
                    c.Vk = KeyVk(t);
                    if (c.Vk < 0) throw new FormatException("unknown key '" + parts[i] + "' in '" + s + "'");
                }
            }
            return c;
        }

        static int KeyVk(string s)
        {
            switch (s)
            {
                case "esc": case "escape": return 0x1B;
                case "enter": case "return": return 0x0D;
                case "space": return 0x20;
                case "tab": return 0x09;
                case "backspace": return 0x08;
                case "del": case "delete": return 0x2E;
                case "ins": case "insert": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pgup": case "pageup": return 0x21;
                case "pgdn": case "pagedown": return 0x22;
                case "left": return 0x25;
                case "up": return 0x26;
                case "right": return 0x27;
                case "down": return 0x28;
                case "printscreen": case "prtsc": return 0x2C;
                case "-": return 0xBD;
                case "=": return 0xBB;
                case ",": return 0xBC;
                case ".": return 0xBE;
                case ";": return 0xBA;
                case "/": return 0xBF;
                case "`": return 0xC0;
                case "[": return 0xDB;
                case "\\": return 0xDC;
                case "]": return 0xDD;
                case "'": return 0xDE;
            }
            switch (s)   // a modifier on its own (or the last key of Ctrl+Shift)
            {
                case "ctrl": case "control": case "lctrl": return 0xA2;
                case "rctrl": return 0xA3;
                case "alt": case "lalt": return 0xA4;
                case "ralt": return 0xA5;
                case "shift": case "lshift": return 0xA0;
                case "rshift": return 0xA1;
                case "win": case "windows": case "meta": case "super": case "lwin": return 0x5B;
                case "rwin": return 0x5C;
            }
            if (s.Length == 1)
            {
                char ch = char.ToUpperInvariant(s[0]);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) return ch;
                return -1;
            }
            if (s.Length >= 2 && s[0] == 'f')
            {
                int n;
                if (int.TryParse(s.Substring(1), out n) && n >= 1 && n <= 24) return 0x70 + n - 1;
            }
            Keys k;
            if (Enum.TryParse(s, true, out k) && Enum.IsDefined(typeof(Keys), k) && ((int)k & ~0xFF) == 0 && (int)k != 0)
                return (int)k;
            return -1;
        }

        public static string NameOf(int vk)
        {
            if ((vk >= '0' && vk <= '9') || (vk >= 'A' && vk <= 'Z')) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            switch (vk)
            {
                case 0x11: case 0xA2: return "Ctrl";
                case 0xA3: return "RCtrl";
                case 0x12: case 0xA4: return "Alt";
                case 0xA5: return "RAlt";
                case 0x10: case 0xA0: return "Shift";
                case 0xA1: return "RShift";
                case 0x5B: return "Win";
                case 0x5C: return "RWin";
                case 0x1B: return "Esc";
                case 0x0D: return "Enter";
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0x2E: return "Del";
                case 0x2D: return "Insert";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x2C: return "PrintScreen";
                case 0xBD: return "-";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBE: return ".";
                case 0xBA: return ";";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }
            return ((Keys)vk).ToString();
        }

        // Chord text for a modifier pressed on its own ("Ctrl", or "Ctrl+Shift" when Ctrl was already down). The other modifiers
        // are the flags minus the key itself. AltGr arrives as a fake LCtrl then RAlt within a few ms: that Ctrl is not the user's.
        public static string ModifierOnly(int vk, uint time, int prevVk, uint prevTime, bool win, bool ctrl, bool alt, bool shift)
        {
            if (vk == 0xA5 && prevVk == 0xA2 && time - prevTime <= 10) ctrl = false;
            return Compose(win && !IsWin(vk), ctrl && !IsCtrl(vk), alt && !IsAlt(vk), shift && !IsShift(vk), vk);
        }

        public static string Compose(bool win, bool ctrl, bool alt, bool shift, int vk)
        {
            List<string> p = new List<string>();
            if (win) p.Add("Win");
            if (ctrl) p.Add("Ctrl");
            if (alt) p.Add("Alt");
            if (shift) p.Add("Shift");
            p.Add(NameOf(vk));
            return string.Join("+", p.ToArray());
        }

        public List<int> SendOrder()
        {
            List<int> l = new List<int>();
            if (Win) l.Add(0x5B);
            if (Ctrl) l.Add(0xA2);
            if (Alt) l.Add(0xA4);
            if (Shift) l.Add(0xA0);
            l.Add(Vk);
            return l;
        }
    }

    // ---------------------------------------------------------------- Config
    sealed class Step
    {
        public string Kind, Value;   // keys | text | delay
        public Chord Chord;
        public int Ms;
        public int Hold;             // keys steps: how long to keep the keys pressed (ms, 0 = quick tap)
        public const int MaxHold = 10000;
        public int After;            // keys and text steps: pause after the step before the next one runs (ms, 0 = the short default)
        public const int MaxAfter = 60000;

        public static List<Step> ParseText(string text)
        {
            List<Step> l = new List<Step>();
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;
                int c = line.IndexOf(':');
                if (c < 0) throw new FormatException("step " + (i + 1) + ": expected 'keys: ...', 'text: ...' or 'delay: ...'");
                string v = line.Substring(c + 1);
                if (v.StartsWith(" ")) v = v.Substring(1);
                l.Add(Make(line.Substring(0, c), v, i + 1));
            }
            return l;
        }

        // One step from its kind and raw value (text is kept exactly, so it may hold spaces only, colons or newlines).
        public static Step Make(string kind, string value, int no)
        {
            Step s = new Step();
            s.Kind = (kind ?? "").Trim().ToLowerInvariant();
            value = value ?? "";
            s.Value = s.Kind == "text" ? value : value.Trim();
            if (s.Kind == "keys")
            {
                int at = s.Value.LastIndexOf('@');
                if (at >= 0)
                {
                    if (!int.TryParse(s.Value.Substring(at + 1).Trim(), out s.Hold) || s.Hold < 0 || s.Hold > MaxHold)
                        throw new FormatException("step " + no + ": hold must be 0-" + MaxHold + " ms");
                    s.Value = s.Value.Substring(0, at).Trim();
                }
            }
            switch (s.Kind)
            {
                case "keys":
                    try { s.Chord = Chord.Parse(s.Value); }
                    catch (FormatException ex) { throw new FormatException("step " + no + ": " + ex.Message); }
                    break;
                case "delay":
                    if (!int.TryParse(s.Value, out s.Ms) || s.Ms < 0 || s.Ms > 60000)
                        throw new FormatException("step " + no + ": delay must be 0-60000 ms");
                    break;
                case "text":
                    if (s.Value.Length == 0) throw new FormatException("step " + no + ": text is empty");
                    break;
                default:
                    throw new FormatException("step " + no + ": unknown step '" + s.Kind + "'");
            }
            return s;
        }

        public static string ToText(List<Step> steps)
        {
            StringBuilder sb = new StringBuilder();
            if (steps != null)
                foreach (Step s in steps)
                {
                    sb.Append(s.Kind).Append(": ").Append(s.Value);
                    if (s.Kind == "keys" && s.Hold > 0) sb.Append(" @").Append(s.Hold);
                    sb.Append("\r\n");
                }
            return sb.ToString();
        }
    }

    sealed class Item
    {
        public string Label, Type, Target, Args, KeysText;
        public Chord Keys;
        public List<Step> Steps = new List<Step>();

        public override string ToString()
        {
            return (string.IsNullOrEmpty(Label) ? "(no label)" : Label) + "  -  " + Type;
        }

        public Item Clone()
        {
            Item c = (Item)MemberwiseClone();
            c.Steps = new List<Step>(Steps);
            return c;
        }

        // Validates and parses the item; throws FormatException with a message that starts with 'at'.
        public void Prepare(string at)
        {
            Type = (Type ?? "").ToLowerInvariant();
            switch (Type)
            {
                case "launch": case "url": case "command":
                    if (string.IsNullOrEmpty(Target)) throw new FormatException(at + ": '" + Type + "' needs a target");
                    break;
                case "shortcut":
                    if (string.IsNullOrEmpty(KeysText)) throw new FormatException(at + ": 'shortcut' needs keys");
                    try { Keys = Chord.Parse(KeysText); }
                    catch (FormatException ex) { throw new FormatException(at + ": " + ex.Message); }
                    break;
                case "preset":      // flips the open ring to its next set of tiles
                    break;
                case "sequence":
                    try
                    {
                        if (Steps == null || Steps.Count == 0) throw new FormatException("sequence has no steps");
                    }
                    catch (FormatException ex) { throw new FormatException(at + ": " + ex.Message); }
                    break;
                default:
                    throw new FormatException(at + ": unknown type '" + Type + "' (launch|shortcut|url|command|sequence|preset)");
            }
            if (string.IsNullOrEmpty(Label)) Label = Type == "preset" ? "Next set" : (KeysText ?? Target ?? Type);
        }
    }

    sealed class Page { public string Name; public List<Item> Items = new List<Item>(); }

    sealed class Config
    {
        public const string DefaultKey = "(default)";
        public const int MaxExtraPages = 3;
        // sets 2..4 of a ring (set 1 is the ring's normal list), keyed by app name or DefaultKey
        public Dictionary<string, List<Page>> Pages = new Dictionary<string, List<Page>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> FirstSetName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The sets the ring for this process cycles through; the first is the normal list. Empty extra sets are skipped.
        public List<List<Item>> PagesFor(string process)
        {
            bool app = !string.IsNullOrEmpty(process) && Apps.ContainsKey(process);
            List<List<Item>> l = new List<List<Item>>();
            l.Add(app ? Apps[process] : Default);
            List<Page> ex;
            if (Pages.TryGetValue(app ? process : DefaultKey, out ex))
                foreach (Page p in ex) if (p.Items.Count > 0) l.Add(p.Items);
            return l;
        }

        public const int MaxItems = 12;
        public Chord Trigger;
        public int HoldSelectMs = 300;
        public string TriggerDevice = "";   // "VID_xxxx&PID_xxxx": only keys from this hardware open the ring ("" = any)
        public bool SelectOnHover;          // off by default: run a tile's action the moment the pointer enters it
        // per-app override of SelectOnHover (only for apps that have their own ring)
        public Dictionary<string, bool> AppHover = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public bool HoverFor(string process)
        {
            bool b;
            if (!string.IsNullOrEmpty(process) && Apps.ContainsKey(process) && AppHover.TryGetValue(process, out b)) return b;
            return SelectOnHover;
        }
        public List<Item> Default = new List<Item>();
        public Dictionary<string, List<Item>> Apps = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);

        public const string SampleJson =
@"{
  ""trigger"": ""Win+D"",
  ""holdSelectMs"": 300,
  ""default"": [
    { ""label"": ""Task Mgr"",  ""type"": ""shortcut"", ""keys"": ""Ctrl+Shift+Esc"" },
    { ""label"": ""Notepad"",   ""type"": ""launch"",   ""target"": ""notepad.exe"" },
    { ""label"": ""Google"",    ""type"": ""url"",      ""target"": ""https://www.google.com"" },
    { ""label"": ""Desktop"",   ""type"": ""shortcut"", ""keys"": ""Win+D"" },
    { ""label"": ""Terminal"",  ""type"": ""launch"",   ""target"": ""cmd.exe"" },
    { ""label"": ""Snip"",      ""type"": ""shortcut"", ""keys"": ""Win+Shift+S"" }
  ],
  ""apps"": {
    ""chrome"": [
      { ""label"": ""New tab"",   ""type"": ""shortcut"", ""keys"": ""Ctrl+T"" },
      { ""label"": ""Close tab"", ""type"": ""shortcut"", ""keys"": ""Ctrl+W"" },
      { ""label"": ""Reopen"",    ""type"": ""shortcut"", ""keys"": ""Ctrl+Shift+T"" },
      { ""label"": ""Reload"",    ""type"": ""shortcut"", ""keys"": ""F5"" }
    ],
    ""explorer"": []
  }
}";

        public List<Item> ItemsFor(string process)
        {
            List<Item> l;
            if (!string.IsNullOrEmpty(process) && Apps.TryGetValue(process, out l)) return l;
            return Default;
        }

        public static Item ParseItem(object e)
        {
            return ParseItems(new object[] { e }, "item")[0];
        }

        public static Config Load(string path) { return Parse(File.ReadAllText(path)); }

        public static Config Parse(string json)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            IDictionary<string, object> root = js.DeserializeObject(json) as IDictionary<string, object>;
            if (root == null) throw new FormatException("top level must be a JSON object");
            Config c = new Config();
            object v;
            string trig = root.TryGetValue("trigger", out v) ? Convert.ToString(v) : "Win+D";
            c.Trigger = Chord.Parse(trig);
            if (Chord.IsModifier(c.Trigger.Vk)) throw new FormatException("trigger must end in a non-modifier key");
            if (root.TryGetValue("holdSelectMs", out v)) c.HoldSelectMs = Convert.ToInt32(v);
            if (root.TryGetValue("triggerDevice", out v)) c.TriggerDevice = (Convert.ToString(v) ?? "").Trim();
            if (root.TryGetValue("selectOnHover", out v)) c.SelectOnHover = Convert.ToBoolean(v);
            if (root.TryGetValue("default", out v)) c.Default = ParseItems(v, "default");
            IDictionary<string, object> pgs = root.TryGetValue("pages", out v) ? v as IDictionary<string, object> : null;
            if (pgs != null)
                foreach (KeyValuePair<string, object> kv in pgs)
                {
                    IEnumerable arr = kv.Value as IEnumerable;
                    if (arr == null || kv.Value is string) throw new FormatException("pages." + kv.Key + ": expected an array");
                    string pk = kv.Key;
                    if (pk.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) pk = pk.Substring(0, pk.Length - 4);
                    List<Page> pl = new List<Page>();
                    int pi = 0;
                    foreach (object po in arr)
                    {
                        IDictionary<string, object> pd = po as IDictionary<string, object>;
                        object itemsV;
                        if (pd == null || !pd.TryGetValue("items", out itemsV)) throw new FormatException("pages." + kv.Key + "[" + pi + "]: expected {\"name\", \"items\"}");
                        Page pg = new Page(); pg.Name = Str(pd, "name"); if (pg.Name != null && pg.Name.Length > 20) pg.Name = pg.Name.Substring(0, 20);
                        pg.Items = ParseItems(itemsV, "pages." + kv.Key + "[" + pi + "].items");
                        if (pl.Count < MaxExtraPages) pl.Add(pg);
                        pi++;
                    }
                    c.Pages[pk] = pl;
                }
            IDictionary<string, object> pnames = root.TryGetValue("pageNames", out v) ? v as IDictionary<string, object> : null;
            if (pnames != null)
                foreach (KeyValuePair<string, object> kv in pnames)
                {
                    string nk = kv.Key;
                    if (nk.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) nk = nk.Substring(0, nk.Length - 4);
                    if (kv.Value != null && Convert.ToString(kv.Value).Length > 0) { string fn = Convert.ToString(kv.Value); c.FirstSetName[nk] = fn.Length > 20 ? fn.Substring(0, 20) : fn; }
                }
            IDictionary<string, object> opts = root.TryGetValue("appOptions", out v) ? v as IDictionary<string, object> : null;
            if (opts != null)
                foreach (KeyValuePair<string, object> kv in opts)
                {
                    IDictionary<string, object> o = kv.Value as IDictionary<string, object>;
                    object hv;
                    if (o == null || !o.TryGetValue("selectOnHover", out hv) || hv == null) continue;
                    string on = kv.Key;
                    if (on.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) on = on.Substring(0, on.Length - 4);
                    c.AppHover[on] = Convert.ToBoolean(hv);
                }
            IDictionary<string, object> apps = root.TryGetValue("apps", out v) ? v as IDictionary<string, object> : null;
            if (apps != null)
                foreach (KeyValuePair<string, object> kv in apps)
                {
                    string name = kv.Key;
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    c.Apps[name] = ParseItems(kv.Value, "apps." + kv.Key);
                }
            return c;
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
        }

        static List<Item> ParseItems(object o, string where)
        {
            IEnumerable arr = o as IEnumerable;
            if (arr == null || o is string) throw new FormatException(where + ": expected an array");
            List<Item> list = new List<Item>();
            int idx = 0;
            foreach (object e in arr)
            {
                string at = where + "[" + idx++ + "]";
                IDictionary<string, object> d = e as IDictionary<string, object>;
                if (d == null) throw new FormatException(at + ": expected an object");
                Item it = new Item();
                it.Type = Str(d, "type");
                it.Target = Str(d, "target");
                it.Args = Str(d, "args");
                it.KeysText = Str(d, "keys");
                it.Label = Str(d, "label");
                object sv;
                if (d.TryGetValue("steps", out sv))
                {
                    IEnumerable sa = sv as IEnumerable;
                    if (sa == null) throw new FormatException(at + ": steps must be an array");
                    // Each JSON step becomes a Step directly. (Going through the line-based text form broke on text with a
                    // newline: it failed to parse, or its second line ran as a step of its own.)
                    List<Step> steps = new List<Step>();
                    foreach (object so in sa)
                    {
                        IDictionary<string, object> sd = so as IDictionary<string, object>;
                        object holdV = null, afterV = null;
                        bool hasHold = sd != null && sd.TryGetValue("hold", out holdV);
                        bool hasAfter = sd != null && sd.TryGetValue("after", out afterV);
                        if (sd == null || sd.Count != 1 + (hasHold ? 1 : 0) + (hasAfter ? 1 : 0)
                            || (hasHold && !sd.ContainsKey("keys")) || (hasAfter && !(sd.ContainsKey("keys") || sd.ContainsKey("text"))))
                            throw new FormatException(at + ": each step must be one of {\"keys\"|\"text\"|\"delay\": value} (keys may add \"hold\": ms; keys and text may add \"after\": ms)");
                        int no = steps.Count + 1;
                        int afterMs = hasAfter ? Convert.ToInt32(afterV) : 0;
                        if (afterMs < 0 || afterMs > Step.MaxAfter) throw new FormatException(at + ": step " + no + ": after must be 0-" + Step.MaxAfter + " ms");
                        int holdMs = hasHold ? Convert.ToInt32(holdV) : 0;
                        if (holdMs < 0 || holdMs > Step.MaxHold) throw new FormatException(at + ": step " + no + ": hold must be 0-" + Step.MaxHold + " ms");
                        foreach (KeyValuePair<string, object> kv in sd)
                        {
                            if (kv.Key == "hold" || kv.Key == "after") continue;
                            Step st;
                            try { st = Step.Make(kv.Key, Convert.ToString(kv.Value), no); }
                            catch (FormatException ex) { throw new FormatException(at + ": " + ex.Message); }
                            if (holdMs > 0) st.Hold = holdMs;
                            st.After = afterMs;
                            steps.Add(st);
                        }
                    }
                    it.Steps = steps;
                }
                it.Prepare(at);
                if (list.Count < MaxItems) list.Add(it);
            }
            return list;
        }

        // ---- writer (used by the editor) ----
        public string ToJson()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"trigger\": ").Append(js.Serialize(Trigger.Text));
            sb.Append(",\n  \"holdSelectMs\": ").Append(HoldSelectMs);
            if (!string.IsNullOrEmpty(TriggerDevice)) sb.Append(",\n  \"triggerDevice\": ").Append(js.Serialize(TriggerDevice));
            sb.Append(",\n  \"selectOnHover\": ").Append(SelectOnHover ? "true" : "false");
            sb.Append(",\n  \"default\": ");
            AppendItems(sb, js, Default, "  ");
            sb.Append(",\n  \"apps\": {");
            bool first = true;
            foreach (KeyValuePair<string, List<Item>> kv in Apps)
            {
                sb.Append(first ? "\n" : ",\n").Append("    ").Append(js.Serialize(kv.Key)).Append(": ");
                AppendItems(sb, js, kv.Value, "    ");
                first = false;
            }
            sb.Append(first ? "}" : "\n  }");
            bool firstOpt = true;
            foreach (KeyValuePair<string, bool> kv in AppHover)
            {
                if (!Apps.ContainsKey(kv.Key)) continue;      // drop options of rings that no longer exist
                sb.Append(firstOpt ? ",\n  \"appOptions\": {\n" : ",\n").Append("    ").Append(js.Serialize(kv.Key))
                  .Append(": { \"selectOnHover\": ").Append(kv.Value ? "true" : "false").Append(" }");
                firstOpt = false;
            }
            if (!firstOpt) sb.Append("\n  }");
            bool firstPg = true;
            foreach (KeyValuePair<string, List<Page>> kv in Pages)
            {
                if (kv.Value.Count == 0 || (kv.Key != DefaultKey && !Apps.ContainsKey(kv.Key))) continue;
                sb.Append(firstPg ? ",\n  \"pages\": {\n" : ",\n").Append("    ").Append(js.Serialize(kv.Key)).Append(": [\n");
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    sb.Append("      { \"name\": ").Append(js.Serialize(kv.Value[i].Name ?? ("Set " + (i + 2)))).Append(", \"items\": ");
                    AppendItems(sb, js, kv.Value[i].Items, "      ");
                    sb.Append(" }").Append(i < kv.Value.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("    ]");
                firstPg = false;
            }
            if (!firstPg) sb.Append("\n  }");
            bool firstNm = true;
            foreach (KeyValuePair<string, string> kv in FirstSetName)
            {
                if (kv.Key != DefaultKey && !Apps.ContainsKey(kv.Key)) continue;
                sb.Append(firstNm ? ",\n  \"pageNames\": {\n" : ",\n").Append("    ").Append(js.Serialize(kv.Key)).Append(": ").Append(js.Serialize(kv.Value));
                firstNm = false;
            }
            if (!firstNm) sb.Append("\n  }");
            sb.Append("\n}\n");
            return sb.ToString();
        }

        static void AppendItems(StringBuilder sb, JavaScriptSerializer js, List<Item> items, string indent)
        {
            if (items.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                sb.Append(indent).Append("  { \"label\": ").Append(js.Serialize(it.Label))
                  .Append(", \"type\": ").Append(js.Serialize(it.Type));
                switch (it.Type)
                {
                    case "shortcut":
                        sb.Append(", \"keys\": ").Append(js.Serialize(it.KeysText));
                        break;
                    case "preset":
                        break;
                    case "sequence":
                        sb.Append(", \"steps\": [");
                        for (int j = 0; j < it.Steps.Count; j++)
                        {
                            Step s = it.Steps[j];
                            sb.Append(j == 0 ? " " : ", ").Append("{ ").Append(js.Serialize(s.Kind)).Append(": ");
                            if (s.Kind == "delay") sb.Append(s.Ms); else sb.Append(js.Serialize(s.Value));
                            if (s.Kind == "keys" && s.Hold > 0) sb.Append(", \"hold\": ").Append(s.Hold);
                            if ((s.Kind == "keys" || s.Kind == "text") && s.After > 0) sb.Append(", \"after\": ").Append(s.After);
                            sb.Append(" }");
                        }
                        sb.Append(" ]");
                        break;
                    default:
                        sb.Append(", \"target\": ").Append(js.Serialize(it.Target));
                        if (it.Type == "launch" && !string.IsNullOrEmpty(it.Args))
                            sb.Append(", \"args\": ").Append(js.Serialize(it.Args));
                        break;
                }
                sb.Append(" }").Append(i < items.Count - 1 ? ",\n" : "\n");
            }
            sb.Append(indent).Append("]");
        }
    }

    // ---------------------------------------------------------------- Overlay
    sealed class RingForm : Form
    {
        public RingForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT (click-through) | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8 | 0x08000000;
                return cp;
            }
        }
    }

    // Visual tokens shared with the editor (ui.html) so the live ring and the preview match.
    sealed class RingPalette
    {
        public Color Plate, Seg, Accent, OnAccent, Text, Text2, Text3, Hub, HubStroke;
        public int ShadowAlpha;

        public static readonly RingPalette Light = new RingPalette
        {
            Plate = Color.FromArgb(230, 231, 233), Seg = Color.White, Accent = Color.FromArgb(0, 95, 184), OnAccent = Color.White,
            Text = Color.FromArgb(27, 27, 27), Text2 = Color.FromArgb(94, 94, 94), Text3 = Color.FromArgb(138, 138, 138),
            Hub = Color.FromArgb(249, 249, 249), HubStroke = Color.FromArgb(20, 0, 0, 0), ShadowAlpha = 60
        };
        public static readonly RingPalette Dark = new RingPalette
        {
            Plate = Color.FromArgb(26, 26, 26), Seg = Color.FromArgb(49, 49, 49), Accent = Color.FromArgb(96, 205, 255), OnAccent = Color.FromArgb(0, 19, 31),
            Text = Color.White, Text2 = Color.FromArgb(196, 196, 196), Text3 = Color.FromArgb(133, 133, 133),
            Hub = Color.FromArgb(39, 39, 39), HubStroke = Color.FromArgb(16, 255, 255, 255), ShadowAlpha = 140
        };

        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                                  (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Windows "Apps" theme: HKCU\...\Themes\Personalize\AppsUseLightTheme (0 = dark).
        public static bool SystemIsDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }
    }

    // Ring layout in physical pixels. Immutable, so the hook thread can hit-test while the UI thread draws.
    sealed class RingGeom
    {
        public Point Center;   // screen coordinates
        public float Scale;
        public int Outer, Inner, Hub, Pad, Count;
        public int Size { get { return 2 * (Outer + Pad); } }
        public Point Origin { get { return new Point(Center.X - Outer - Pad, Center.Y - Outer - Pad); } }

        // Proportions match the editor's 400-unit preview (outer 182, inner 64, hub 56).
        // sizeCount = the most tiles any set of this ring has. The ring grows past 8 tiles so each wedge stays wide enough
        // for a full-size label, and stays that size across sets (switching sets reuses the same window).
        public static RingGeom ForScale(float s, int count) { return ForScale(s, count, count); }
        public static RingGeom ForScale(float s, int count, int sizeCount)
        {
            RingGeom g = new RingGeom();
            g.Scale = s; g.Count = count;
            g.Outer = (int)Math.Round(Math.Max(136f, 17.5f * sizeCount) * s);
            g.Inner = (int)Math.Round(g.Outer * 64f / 182f);
            g.Hub = (int)Math.Round(g.Outer * 56f / 182f);
            g.Pad = (int)Math.Round(34f * s);
            return g;
        }

        public static RingGeom At(Point cursor, int count, int sizeCount)
        {
            RingGeom g = ForScale(Native.MonitorScale(cursor), count, sizeCount);
            Rectangle wa = Screen.FromPoint(cursor).WorkingArea;
            int keep = g.Outer + (int)(8 * g.Scale);   // the shadow may run off-screen, the ring itself may not
            g.Center = new Point(Clamp(cursor.X, wa.Left + keep, wa.Right - keep), Clamp(cursor.Y, wa.Top + keep, wa.Bottom - keep));
            return g;
        }
        static int Clamp(int v, int lo, int hi) { return hi < lo ? (lo + hi) / 2 : Math.Max(lo, Math.Min(hi, v)); }

        // Returns the item under p or -1. 'inside' = p is within the ring's footprint (hub included).
        public int HitTest(Point p, out bool inside)
        {
            double dx = p.X - Center.X, dy = p.Y - Center.Y, r = Math.Sqrt(dx * dx + dy * dy);
            double reach = Outer + 20 * Scale;
            inside = r <= reach;
            if (r < Hub || r > reach || Count == 0) return -1;
            double sweep = 360.0 / Count;
            double rel = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + sweep / 2) % 360;
            if (rel < 0) rel += 360;
            return (int)(rel / sweep) % Count;
        }
    }

    // The pop-up ring. All drawing is done once per open into cached layers; a frame only composites them
    // into a persistent DIB and calls UpdateLayeredWindow, so it costs about a millisecond.
    sealed class Ring
    {
        const float GapDeg = 1.6f;
        const float OpenMs = 140f, HoverMs = 90f, CloseMs = 90f, CommitMs = 130f;
        static readonly string[] TypeGlyph = { "shortcut", "", "sequence", "", "launch", "", "url", "", "command", "", "preset", "\uE8AB" };

        RingForm form;
        readonly System.Windows.Forms.Timer timer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Native.Surface surface = new Native.Surface();
        readonly ImageAttributes fadeAttr = new ImageAttributes();
        readonly ColorMatrix fadeMatrix = new ColorMatrix();
        long lastTick;
        RingGeom geom;
        List<Item> items;
        float[] heat = new float[0];   // 0..1 accent amount per segment (hover crossfade)
        float openT;                   // window opacity 0..1
        int phase;                     // 0 hidden, 1 open, 2 closing
        int hover = -1, flash = -1;
        bool animate = true;
        RingPalette pal = RingPalette.Light;

        // caches
        float fontScale = -1f;
        Font labelFont, hubFont, subFont, glyphFont;
        Bitmap shadowLayer; string shadowKey;
        Bitmap baseLayer;
        string layersKey; List<Item> layersList;
        Bitmap[] hotSeg = new Bitmap[0];
        Rectangle[] hotRect = new Rectangle[0];
        StringFormat sf, sfLabel;

        public Ring()
        {
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 10;
            timer.Tick += delegate { Tick(); };
            sf = new StringFormat(StringFormat.GenericTypographic);
            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            sfLabel = new StringFormat(sf); sfLabel.Trimming = StringTrimming.EllipsisCharacter;
        }

        // ---------------------------------------------------------------- lifecycle (UI thread)
        // Pays one-time costs (window handle, fonts, shadow, JIT) at startup instead of on the first press.
        public void WarmUp(List<Item> sample)
        {
            try
            {
                if (form == null) form = new RingForm();
                IntPtr h = form.Handle;
                if (sample == null || sample.Count == 0) return;
                RingGeom g = RingGeom.ForScale(Native.MonitorScale(Cursor.Position), sample.Count);
                pal = RingPalette.SystemIsDark() ? RingPalette.Dark : RingPalette.Light;
                Prepare(sample, g, -1);
                Compose();
            }
            catch { }
        }

        public void Show(List<Item> list, RingGeom g, int initialHover)
        {
            pal = RingPalette.SystemIsDark() ? RingPalette.Dark : RingPalette.Light;
            animate = Native.AnimationsEnabled();
            Prepare(list, g, initialHover);
            if (form == null) form = new RingForm();
            form.Bounds = new Rectangle(g.Origin, new Size(g.Size, g.Size));
            phase = 1;
            openT = animate ? 0f : 1f;
            Compose();
            Present();
            if (!form.Visible) form.Show();
            lastTick = clock.ElapsedMilliseconds;
            timer.Start();
        }

        // Replaces the tiles of the open ring (same centre and size) without replaying the open animation.
        public void Swap(List<Item> list, RingGeom g, int initialHover)
        {
            if (phase != 1) return;
            Prepare(list, g, initialHover);
            openT = 1f;
            Compose();
            Present();
        }

        public void SetHover(int i)
        {
            if (phase != 1 || i == hover) return;
            hover = i;
            if (!animate) { for (int k = 0; k < heat.Length; k++) heat[k] = k == hover ? 1f : 0f; Compose(); Present(); return; }
            StartTimer();
        }

        public void Hide() { Close(-1); }

        public void Commit(int index) { Close(items != null && index >= 0 && index < items.Count ? index : -1); }

        void Close(int flashIndex)
        {
            if (phase == 0) return;
            flash = flashIndex;
            if (flash >= 0) { heat[flash] = 1f; Compose(); Present(); }
            if (!animate) { HideNow(); return; }
            phase = 2;
            StartTimer();
        }

        public void HideNow()
        {
            phase = 0;
            timer.Stop();
            if (form != null) form.Hide();
        }

        void StartTimer()
        {
            if (!timer.Enabled) { lastTick = clock.ElapsedMilliseconds; timer.Start(); }
        }

        // Animation clock: time-based, so uneven timer ticks don't change speed. Stops itself when idle.
        void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            float dt = Math.Min(50f, now - lastTick);
            lastTick = now;
            bool compose = false, present = false;

            if (phase == 1 && openT < 1f) { openT = Math.Min(1f, openT + dt / OpenMs); present = true; }
            else if (phase == 2)
            {
                openT = Math.Max(0f, openT - dt / (flash >= 0 ? CommitMs : CloseMs));
                if (openT <= 0f) { HideNow(); return; }
                present = true;
            }

            for (int i = 0; i < heat.Length; i++)
            {
                float target = (phase == 1 && i == hover) || i == flash ? 1f : 0f;
                if (heat[i] == target) continue;
                float step = dt / HoverMs;
                heat[i] = target > heat[i] ? Math.Min(target, heat[i] + step) : Math.Max(target, heat[i] - step);
                compose = true;
            }

            if (compose) Compose();
            if (compose || present) Present();
            if (!compose && !present && phase == 1) timer.Stop();   // settled: no work until the next hover change
        }

        void Present()
        {
            float e = 1f - (1f - openT) * (1f - openT) * (1f - openT);   // ease-out
            surface.Present(form.Handle, geom.Origin, (byte)Math.Round(255 * e));
        }

        // ---------------------------------------------------------------- drawing
        void SetupGraphics(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // grayscale: ClearType can't blend on a layered window
        }

        static Font TryFont(string name, float px, FontStyle style)
        {
            try { using (FontFamily ff = new FontFamily(name)) { } return new Font(name, px, style, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return null; }
        }

        void MakeFonts(float s)
        {
            if (fontScale == s) return;
            foreach (Font f in new Font[] { labelFont, hubFont, subFont, glyphFont }) if (f != null) f.Dispose();
            // GDI+ draws variable fonts at their default weight, so use the static Segoe UI faces.
            labelFont = TryFont("Segoe UI Semibold", 12f * s, FontStyle.Regular) ?? new Font("Segoe UI", 12f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            hubFont = TryFont("Segoe UI Semibold", 13f * s, FontStyle.Regular) ?? new Font("Segoe UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            subFont = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            glyphFont = TryFont("Segoe Fluent Icons", 14f * s, FontStyle.Regular) ?? TryFont("Segoe MDL2 Assets", 14f * s, FontStyle.Regular);
            fontScale = s;
        }

        static string Glyph(string type)
        {
            for (int i = 0; i < TypeGlyph.Length; i += 2) if (TypeGlyph[i] == type) return TypeGlyph[i + 1];
            return "";
        }

        static string Trunc(string s, int n)
        {
            s = s ?? "";
            return s.Length > n ? s.Substring(0, n - 1) + "…" : s;
        }

        // Builds every layer for this open: shadow+plate (cached per scale/theme), idle segments, and one hot bitmap per segment.
        void Prepare(List<Item> list, RingGeom g, int initialHover)
        {
            items = list; geom = g; hover = initialHover; flash = -1;
            heat = new float[list.Count];
            if (hover >= 0 && hover < heat.Length) heat[hover] = 1f;
            MakeFonts(g.Scale);
            int size = g.Size;
            surface.Ensure(size, size);

            string key = size + "|" + g.Outer + "|" + (pal == RingPalette.Dark);
            if (shadowKey != key) { if (shadowLayer != null) shadowLayer.Dispose(); shadowLayer = RenderShadowPlate(g); shadowKey = key; }

            // Reopening the same ring (same item list object, size and theme) reuses its layers as-is.
            // A config reload creates new lists, so edits always rebuild.
            if ((object)layersList == list && layersKey == key) return;

            if (baseLayer == null || baseLayer.Width != size) { if (baseLayer != null) baseLayer.Dispose(); baseLayer = new Bitmap(size, size, PixelFormat.Format32bppPArgb); }
            using (Graphics bg = Graphics.FromImage(baseLayer))
            {
                bg.CompositingMode = CompositingMode.SourceCopy;
                bg.DrawImageUnscaled(shadowLayer, 0, 0);
                bg.CompositingMode = CompositingMode.SourceOver;
                SetupGraphics(bg);
                float c = size / 2f;
                for (int i = 0; i < list.Count; i++) DrawSegment(bg, i, c, c, false);
                using (SolidBrush hb = new SolidBrush(pal.Hub)) bg.FillEllipse(hb, c - g.Hub, c - g.Hub, 2 * g.Hub, 2 * g.Hub);
                using (Pen hp = new Pen(pal.HubStroke, Math.Max(1f, g.Scale))) bg.DrawEllipse(hp, c - g.Hub, c - g.Hub, 2 * g.Hub, 2 * g.Hub);
            }

            foreach (Bitmap b in hotSeg) if (b != null) b.Dispose();
            hotSeg = new Bitmap[list.Count];
            hotRect = new Rectangle[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                Rectangle r = SegmentBounds(i, size / 2f);
                hotRect[i] = r;
                Bitmap hb = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppPArgb);
                using (Graphics hg = Graphics.FromImage(hb))
                {
                    SetupGraphics(hg);
                    DrawSegment(hg, i, size / 2f - r.X, size / 2f - r.Y, true);
                }
                hotSeg[i] = hb;
            }
            layersKey = key; layersList = list;
        }

        Bitmap RenderShadowPlate(RingGeom g)
        {
            int size = g.Size; float c = size / 2f, s = g.Scale;
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics gr = Graphics.FromImage(bmp))
            {
                SetupGraphics(gr);
                float blur = 22f * s, oy = 6f * s, sr = g.Outer + blur;
                using (GraphicsPath sp = new GraphicsPath())
                {
                    sp.AddEllipse(c - sr, c - sr + oy, 2 * sr, 2 * sr);
                    using (PathGradientBrush pb = new PathGradientBrush(sp))
                    {
                        pb.CenterPoint = new PointF(c, c + oy);
                        pb.CenterColor = Color.FromArgb(pal.ShadowAlpha, 0, 0, 0);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 0, 0) };
                        float f = (g.Outer - 4f * s) / sr;
                        pb.FocusScales = new PointF(f, f);
                        Blend bl = new Blend();
                        bl.Positions = new float[] { 0f, .35f, .7f, 1f };
                        bl.Factors = new float[] { 0f, .18f, .6f, 1f };
                        pb.Blend = bl;
                        gr.FillPath(pb, sp);
                    }
                }
                // plate: shows through the gaps so segments read as one object on any wallpaper
                float pr = g.Outer + 0.75f * s;
                using (SolidBrush plate = new SolidBrush(pal.Plate)) gr.FillEllipse(plate, c - pr, c - pr, 2 * pr, 2 * pr);
            }
            return bmp;
        }

        Rectangle SegmentBounds(int i, float c)
        {
            using (GraphicsPath gp = SegmentPath(i, c, c))
            {
                RectangleF b = gp.GetBounds();
                Rectangle r = Rectangle.FromLTRB((int)Math.Floor(b.Left) - 2, (int)Math.Floor(b.Top) - 2, (int)Math.Ceiling(b.Right) + 2, (int)Math.Ceiling(b.Bottom) + 2);
                r.Intersect(new Rectangle(0, 0, geom.Size, geom.Size));
                return r;
            }
        }

        GraphicsPath SegmentPath(int i, float cx, float cy)
        {
            int n = items.Count;
            float sweep = 360f / n, start = -90f - sweep / 2f + i * sweep;
            RectangleF oR = new RectangleF(cx - geom.Outer, cy - geom.Outer, 2 * geom.Outer, 2 * geom.Outer);
            RectangleF iR = new RectangleF(cx - geom.Inner, cy - geom.Inner, 2 * geom.Inner, 2 * geom.Inner);
            GraphicsPath gp = new GraphicsPath();
            if (n == 1) { gp.AddEllipse(oR); gp.AddEllipse(iR); }
            else
            {
                // Parallel gaps: each radial edge is moved sideways by half the gap, so the gap is the same width at the hub and at the rim.
                // Where that offset line meets a circle of radius r, the angle is shifted by asin(half / r).
                float half = GapDeg * (float)Math.PI / 180f * (geom.Outer + geom.Inner) / 4f;   // half the gap, in pixels (the old 1.6 degrees measured at mid radius)
                float dO = (float)(Math.Asin(half / geom.Outer) * 180 / Math.PI), dI = (float)(Math.Asin(half / geom.Inner) * 180 / Math.PI);
                gp.AddArc(oR, start + dO, sweep - 2 * dO);
                gp.AddArc(iR, start + sweep - dI, -(sweep - 2 * dI));
                gp.CloseFigure();
            }
            return gp;
        }

        void DrawSegment(Graphics g, int i, float cx, float cy, bool hot)
        {
            int n = items.Count;
            float s = geom.Scale, sweep = 360f / n, start = -90f - sweep / 2f + i * sweep;
            using (GraphicsPath gp = SegmentPath(i, cx, cy))
            using (SolidBrush b = new SolidBrush(hot ? pal.Accent : pal.Seg))
                g.FillPath(b, gp);
            float mid = (geom.Outer + geom.Inner) / 2f;
            double a = (start + sweep / 2f) * Math.PI / 180;
            float mx = cx + (float)(mid * Math.Cos(a)), my = cy + (float)(mid * Math.Sin(a));
            float off = 10f * s;
            float labelW = Math.Min((float)(2 * Math.PI * mid / n * 0.9), 96f * s);
            if (glyphFont != null)
                using (SolidBrush gb = new SolidBrush(hot ? pal.OnAccent : pal.Text2))
                    g.DrawString(Glyph(items[i].Type), glyphFont, gb, new RectangleF(mx - 12 * s, my - off - 10 * s, 24 * s, 20 * s), sf);
            using (SolidBrush tb = new SolidBrush(hot ? pal.OnAccent : pal.Text))
                g.DrawString(Trunc(items[i].Label, 12), labelFont, tb,
                    new RectangleF(mx - labelW / 2, my + off - 10 * s, labelW, 20 * s), sfLabel);   // ellipsis if the wedge is too narrow
        }

        // One frame: base layer + hot segments at their crossfade alpha + hub label.
        void Compose()
        {
            Graphics g = surface.G;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(baseLayer, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            for (int i = 0; i < heat.Length; i++)
            {
                float t = heat[i];
                if (t <= 0f) continue;
                Rectangle r = hotRect[i];
                if (t >= 1f) g.DrawImageUnscaled(hotSeg[i], r.X, r.Y);
                else
                {
                    fadeMatrix.Matrix33 = t;
                    fadeAttr.SetColorMatrix(fadeMatrix);
                    g.DrawImage(hotSeg[i], r, 0, 0, r.Width, r.Height, GraphicsUnit.Pixel, fadeAttr);
                }
            }
            float c = geom.Size / 2f, hubR = geom.Hub, s = geom.Scale;
            RectangleF hr = new RectangleF(c - hubR + 6 * s, c - hubR, 2 * hubR - 12 * s, 2 * hubR);
            int show = flash >= 0 ? flash : hover;
            if (show >= 0 && show < items.Count)
                using (SolidBrush tb = new SolidBrush(pal.Text)) g.DrawString(Trunc(items[show].Label, 12), hubFont, tb, hr, sf);
            else
                using (SolidBrush tb = new SolidBrush(pal.Text3)) g.DrawString("cancel", subFont, tb, hr, sf);
            g.Flush(FlushIntention.Sync);
        }

        // ---------------------------------------------------------------- dev aid (--render)
        public Bitmap Preview(List<Item> list, int hoverIndex, float s, bool dark)
        {
            pal = dark ? RingPalette.Dark : RingPalette.Light;
            Prepare(list, RingGeom.ForScale(s, list.Count), hoverIndex);
            Compose();
            return surface.Bmp.Clone(new Rectangle(0, 0, surface.Bmp.Width, surface.Bmp.Height), PixelFormat.Format32bppPArgb);
        }
    }

    // ---------------------------------------------------------------- Tray menu (Windows 11 / Fluent look)
    // Owner-drawn through a ToolStripRenderer: rounded, theme-aware, Segoe Fluent Icons. A restyle, not WinUI 3.
    sealed class FluentMenu : ToolStripRenderer
    {
        const string CheckGlyph = "\uE73E";
        RingPalette pal = RingPalette.Light;
        bool dark; float s = 1f;
        Font text, bold, glyph;
        Color bg, border, hoverFill, sepColor;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public static void Apply(ContextMenuStrip menu)
        {
            FluentMenu r = new FluentMenu();
            menu.Renderer = r;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.HandleCreated += delegate
            {
                try   // Windows 11: round corners and a dark frame; older Windows ignores these
                {
                    int round = 2; DwmSetWindowAttribute(menu.Handle, 33, ref round, 4);
                    int d = RingPalette.SystemIsDark() ? 1 : 0; DwmSetWindowAttribute(menu.Handle, 20, ref d, 4);
                }
                catch { }
            };
        }

        void SetTheme(float scale, bool isDark)
        {
            s = scale; dark = isDark; pal = isDark ? RingPalette.Dark : RingPalette.Light;
            bg = isDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            border = isDark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);
            hoverFill = isDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0);
            sepColor = isDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
            if (text != null) text.Dispose();
            if (bold != null) bold.Dispose();
            if (glyph != null) glyph.Dispose();
            text = Pick("Segoe UI", 13f * s, FontStyle.Regular);
            bold = Pick("Segoe UI Semibold", 13f * s, FontStyle.Regular);
            glyph = Pick("Segoe Fluent Icons", 16f * s, FontStyle.Regular);
            if (glyph.Name != "Segoe Fluent Icons") glyph = Pick("Segoe MDL2 Assets", 16f * s, FontStyle.Regular);
        }

        public static void Prepare(ContextMenuStrip menu)
        {
            FluentMenu r = menu.Renderer as FluentMenu;
            if (r != null) r.Layout(menu, Native.MonitorScale(Cursor.Position), RingPalette.SystemIsDark());
        }

        static Font Pick(string name, float px, FontStyle st)
        {
            try { using (FontFamily ff = new FontFamily(name)) { } return new Font(name, px, st, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return new Font("Segoe UI", px, st, GraphicsUnit.Pixel); }
        }

        // Sizes every item before the menu is shown.
        // Sizes are computed by hand from the measured text: the native autosize clipped the right edge.
        void Layout(ContextMenuStrip menu, float scale, bool isDark)
        {
            SetTheme(scale, isDark);
            int p = (int)Math.Round(4 * s);
            int left = (int)Math.Round(44 * s), right = (int)Math.Round(40 * s), rowH = (int)Math.Round(36 * s), sepH = (int)Math.Round(9 * s);
            int lineH = TextRenderer.MeasureText("Ag", text, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
            int vpad = Math.Max(0, (rowH - lineH) / 2);
            menu.Padding = new Padding(0, p, 0, p);   // side margins come from the rows themselves (PaintItem insets the hover pill)
            int w = 0, total = 0;
            foreach (ToolStripItem it in menu.Items)
            {
                it.Margin = Padding.Empty;
                if (it is ToolStripSeparator) { it.AutoSize = false; it.Height = sepH; total += sepH; continue; }
                bool header = !it.Enabled && it.Tag == null;
                it.Font = header ? bold : text;
                int tw = TextRenderer.MeasureText(it.Text, it.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                w = Math.Max(w, tw + left + right);
                it.Padding = new Padding(left, vpad, right, vpad);
                total += rowH;
            }
            foreach (ToolStripItem it in menu.Items)
            {
                it.AutoSize = false;
                it.Size = new Size(w + 2 * p, it is ToolStripSeparator ? sepH : rowH);
            }
            menu.AutoSize = false;
            menu.Size = new Size(w + 2 * p, total + 2 * p);
        }

        static GraphicsPath Round(Rectangle r, float rad)
        {
            float d = rad * 2; GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        void PaintBorder(Graphics g, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 8 * s))
            using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
        }

        void PaintSeparator(Graphics g, Rectangle r)
        {
            int inset = (int)Math.Round(12 * s);
            using (Pen pen = new Pen(sepColor, 1f)) g.DrawLine(pen, r.X + inset, r.Y + r.Height / 2, r.Right - inset, r.Y + r.Height / 2);
        }

        void PaintItem(Graphics g, ToolStripItem it, Rectangle r, bool hot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool header = !it.Enabled && it.Tag == null;
            int vm = (int)Math.Round(2 * s), pad = (int)Math.Round(12 * s), icon = (int)Math.Round(20 * s);
            int side = (int)Math.Round(4 * s);
            r = new Rectangle(r.X + side, r.Y, r.Width - 2 * side, r.Height);   // even margin left and right of every row
            if (hot && it.Enabled)
                using (GraphicsPath p = Round(new Rectangle(r.X, r.Y + vm, r.Width, r.Height - 2 * vm), 4 * s))
                using (SolidBrush b = new SolidBrush(hoverFill)) g.FillPath(b, p);
            Color tc = it.Enabled ? pal.Text : pal.Text3;
            TextFormatFlags cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            string gl = it.Tag as string;
            if (gl != null) TextRenderer.DrawText(g, gl, glyph, new Rectangle(r.X + pad, r.Y, icon, r.Height), tc, cf);
            Rectangle tr = new Rectangle(r.X + pad + icon + pad, r.Y, r.Width - (pad * 3 + icon + (int)Math.Round(20 * s)), r.Height);
            TextRenderer.DrawText(g, it.Text, header ? bold : text, tr, header ? pal.Text2 : tc,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null && mi.Checked)
                TextRenderer.DrawText(g, CheckGlyph, glyph, new Rectangle(r.Right - pad - icon, r.Y, icon, r.Height), pal.Accent, cf);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { PaintBorder(e.Graphics, new Rectangle(Point.Empty, e.ToolStrip.Size)); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { PaintItem(e.Graphics, e.Item, new Rectangle(Point.Empty, e.Item.Size), e.Item.Selected); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { }       // drawn with the item
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { PaintSeparator(e.Graphics, new Rectangle(Point.Empty, e.Item.Size)); }

        // Dev aid (--render-menu): paints the menu to an image without showing it.
        public static Bitmap Preview(ContextMenuStrip menu, int hot, float scale, bool isDark)
        {
            FluentMenu r = new FluentMenu();
            r.Layout(menu, scale, isDark);
            int p = (int)Math.Round(4 * scale), m = (int)Math.Round(28 * scale), w = menu.Items[0].Width - 2 * p, h = 0;
            foreach (ToolStripItem it in menu.Items) h += it.Height;
            Rectangle box = new Rectangle(m, m, w + 2 * p, h + 2 * p);
            Bitmap bmp = new Bitmap(box.Width + 2 * m, box.Height + 2 * m, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(isDark ? Color.FromArgb(30, 34, 46) : Color.FromArgb(198, 210, 232));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 6; i >= 1; i--)   // soft shadow
                    using (GraphicsPath sp = Round(Rectangle.Inflate(box, i, i), 8 * scale + i))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(isDark ? 14 : 10, 0, 0, 0))) { g.TranslateTransform(0, 4 * scale); g.FillPath(sb, sp); g.ResetTransform(); }
                using (GraphicsPath face = Round(box, 8 * scale))
                using (SolidBrush fb = new SolidBrush(r.bg)) g.FillPath(fb, face);
                int y = box.Y + p, idx = 0;       // TextRenderer ignores Graphics transforms, so every rect is absolute
                foreach (ToolStripItem it in menu.Items)
                {
                    Rectangle ir = new Rectangle(box.X, y, w + 2 * p, it.Height);   // rows span the full menu width
                    if (it is ToolStripSeparator) r.PaintSeparator(g, ir); else r.PaintItem(g, it, ir, idx == hot);
                    y += it.Height; idx++;
                }
                r.PaintBorder(g, box);
            }
            return bmp;
        }
    }

    // Finds where the tray icon (or the overflow flyout holding it) is, so the menu can open beside it instead of on top of it.
    static class TrayPlacement
    {
        [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

        static Rectangle R(RECT r) { return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom); }

        public static Rectangle AnchorFor(NotifyIcon ni)
        {
            Rectangle icon = Rectangle.Empty;
            try   // NotifyIcon keeps its window and id private; the shell needs both to locate the icon
            {
                System.Reflection.BindingFlags f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                NativeWindow w = (NativeWindow)typeof(NotifyIcon).GetField("window", f).GetValue(ni);
                int id = (int)typeof(NotifyIcon).GetField("id", f).GetValue(ni);
                NOTIFYICONIDENTIFIER n = new NOTIFYICONIDENTIFIER();
                n.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)); n.hWnd = w.Handle; n.uID = (uint)id;
                RECT rc;
                if (Shell_NotifyIconGetRect(ref n, out rc) == 0) icon = R(rc);
            }
            catch { }
            if (icon.IsEmpty || icon.Width <= 0) { Point c = Cursor.Position; icon = new Rectangle(c.X - 8, c.Y - 8, 16, 16); }
            // an icon inside the overflow flyout: keep clear of the whole flyout, not just the icon
            foreach (string cls in new string[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
            {
                IntPtr h = FindWindow(cls, null);
                RECT r;
                if (h != IntPtr.Zero && IsWindowVisible(h) && GetWindowRect(h, out r))
                {
                    Rectangle fr = R(r);
                    if (fr.Width > 0 && fr.IntersectsWith(icon)) return fr;
                }
            }
            return icon;
        }

        // Top-left for a menu of this size: to the left of the anchor, bottom-aligned, kept inside the work area.
        public static Point MenuOrigin(Rectangle anchor, Size menu, int gap)
        {
            Rectangle wa = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left - gap - menu.Width;
            if (x < wa.Left) x = anchor.Right + gap;                       // no room on the left: go to the right
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - menu.Width));
            int y = anchor.Bottom - menu.Height;
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - menu.Height));
            return new Point(x, y);
        }
    }

    // ---------------------------------------------------------------- Tray app
    // Records a whole sequence of keystrokes from the keyboard hook, with how long each was held and the gap before the next.
    static class SeqRecorder
    {
        static readonly object gate = new object();
        static readonly List<string> chords = new List<string>();
        static readonly List<int[]> times = new List<int[]>();   // { hold, downTime, upTime }
        static volatile bool active;
        static int curVk = -1; static string curChord; static uint curDown; static int startedTick;
        static int pendVk = -1; static string pendChord; static uint pendDown;   // a modifier pressed on its own, waiting to see if a key follows

        public static bool Active { get { return active; } }
        public static int Count { get { lock (gate) return chords.Count; } }

        public static void Start()
        {
            lock (gate) { chords.Clear(); times.Clear(); curVk = -1; pendVk = -1; startedTick = Environment.TickCount; active = true; }
        }

        // Hook thread. Returns true to swallow the key (everything is swallowed while recording, like the single-shortcut recorder).
        public static bool Handle(bool down, int vk, uint time, bool w, bool c, bool a, bool s)
        {
            if (Environment.TickCount - startedTick > 180000) { active = false; return false; }   // forgotten recording: give the keyboard back
            lock (gate)
            {
                if (curVk >= 0)
                {
                    if (!down && vk == curVk) { AddStep(curChord, curDown, time); curVk = -1; }
                    return true;
                }
                if (down && !Chord.IsModifier(vk))
                {
                    if (vk == 0x1B && !w && !c && !a && !s) { active = false; return true; }   // bare Esc finishes
                    pendVk = -1;   // the modifiers belong to this key now
                    curChord = Chord.Compose(w, c, a, s, vk); curDown = time; curVk = vk;
                }
                else if (down)
                {
                    if (pendVk != vk)   // not an auto-repeat: remember it (the latest modifier wins, earlier ones stay in the chord)
                    {
                        pendChord = Chord.ModifierOnly(vk, time, pendVk, pendDown, w, c, a, s);
                        pendVk = vk; pendDown = time;
                    }
                }
                else if (vk == pendVk)   // released with no key in between: it was a step by itself
                {
                    AddStep(pendChord, pendDown, time);
                    pendVk = -1;
                }
                return true;
            }
        }

        static void AddStep(string chord, uint down, uint up)
        {
            int held = (int)(up - down);
            chords.Add(chord);
            times.Add(new int[] { held < 60 ? 0 : Math.Min(held, Step.MaxHold), (int)down, (int)up });
        }

        // Stops recording and returns {"steps":[{"keys","hold","after"}...]}; "after" is the real gap to the next keystroke.
        public static string Stop()
        {
            lock (gate)
            {
                active = false;
                if (pendVk >= 0) { AddStep(pendChord, pendDown, (uint)Environment.TickCount); pendVk = -1; }   // a modifier still held when recording ended
                StringBuilder sb = new StringBuilder("{\"steps\":[");
                JavaScriptSerializer js = new JavaScriptSerializer();
                for (int i = 0; i < chords.Count; i++)
                {
                    int after = 0;
                    if (i + 1 < chords.Count) { after = times[i + 1][1] - times[i][2]; if (after < 60) after = 0; if (after > Step.MaxAfter) after = Step.MaxAfter; }
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"keys\":").Append(js.Serialize(chords[i])).Append(",\"hold\":").Append(times[i][0]).Append(",\"after\":").Append(after).Append('}');
                }
                sb.Append("]}");
                chords.Clear(); times.Clear();
                return sb.ToString();
            }
        }
    }

    // Tells which physical keyboard (or mouse that presents as one) a key came from, via Raw Input.
    // The low-level hook cannot see the device, so the two are matched by key and time. Everything here fails open:
    // if the device cannot be identified the caller treats the key as the trigger, exactly as before.
    static class RawKeyboard
    {
        [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
        [StructLayout(LayoutKind.Sequential)] struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, Extra; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint cb);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint cbHeader);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, StringBuilder data, ref uint size);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern IntPtr DefRawInputProc(IntPtr[] raw, int n, uint cbHeader);

        static readonly object gate = new object();
        static readonly Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();
        static int lastVk = -1, lastTick; static string lastName; static bool lastBreak;

        public static void Register(IntPtr hwnd)
        {
            RAWINPUTDEVICE[] d = new RAWINPUTDEVICE[1];
            d[0].UsagePage = 1; d[0].Usage = 6; d[0].Flags = 0x100 /* RIDEV_INPUTSINK */; d[0].Target = hwnd;
            RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        static string NameOf(IntPtr dev)
        {
            string n;
            lock (gate) if (names.TryGetValue(dev, out n)) return n;
            uint size = 0;
            GetRawInputDeviceInfo(dev, 0x20000007 /* RIDI_DEVICENAME */, null, ref size);
            StringBuilder sb = new StringBuilder((int)Math.Max(size, 2));
            GetRawInputDeviceInfo(dev, 0x20000007, sb, ref size);
            n = sb.ToString();
            lock (gate) names[dev] = n;
            return n;
        }

        // WM_INPUT handler (message loop) and the drain below both end up here.
        public static void Process(IntPtr hRaw)
        {
            uint hdr = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)), size = 0;
            if (GetRawInputData(hRaw, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, hdr) != 0 || size == 0 || size > 4096) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRaw, 0x10000003, buf, ref size, hdr) == unchecked((uint)-1)) return;
                RAWINPUTHEADER h = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                if (h.Type != 1) return;   // keyboard only
                RAWKEYBOARD kb = (RAWKEYBOARD)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + hdr), typeof(RAWKEYBOARD));
                string name = NameOf(h.Device);
                lock (gate) { lastVk = kb.VKey; lastBreak = (kb.Flags & 1) != 0; lastName = name; lastTick = Environment.TickCount; }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Pulls any WM_INPUT already waiting for this thread so a key that the hook saw first can still be matched.
        static void Drain()
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0x00FF, 0x00FF, 1 /* PM_REMOVE */))
            {
                Process(m.lParam);
                try { DefRawInputProc(new IntPtr[] { m.lParam }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))); } catch { }
            }
        }

        // The device path for the most recent press of this key, waiting up to waitMs for its raw event. null = unknown.
        public static string DeviceFor(int vk, int waitMs)
        {
            int start = Environment.TickCount;
            do
            {
                Drain();
                lock (gate) if (lastVk == vk && !lastBreak && Environment.TickCount - lastTick < 400) return lastName;
                Thread.Sleep(1);
            } while (Environment.TickCount - start < waitMs);
            return null;
        }

        // "VID_046D&PID_C52B": identifies the hardware but not one of its several interfaces.
        public static string ShortId(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(path, "VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}");
            return m.Success ? m.Value.ToUpperInvariant() : "";
        }
    }

    // Window on the hook thread that receives WM_INPUT.
    sealed class RawWnd : Control
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF) { try { RawKeyboard.Process(m.LParam); } catch { } }
            base.WndProc(ref m);   // DefWindowProc also does the Raw Input cleanup
        }
    }

    // Plain Control windows get a blank taskbar button when they take the foreground; a tool window never does.
    // The tray menu is a top-level popup with no owner; without WS_EX_TOOLWINDOW Windows gives it a blank taskbar button while it is open.
    sealed class ToolWindowMenu : ContextMenuStrip
    {
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ExStyle &= ~0x40000; return cp; } }   // + TOOLWINDOW, - APPWINDOW
    }

    sealed class ToolWindowControl : Control
    {
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }   // WS_EX_TOOLWINDOW
    }

    sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "RingAction";

        readonly string configPath;
        volatile Config cfg;
        readonly Ring ring = new Ring();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Native.HookProc keyProc, mouseProc;   // kept in fields so the GC can't collect them
        IntPtr keyHook, mouseHook;
        volatile bool paused;

        // The hooks run on their own thread so input is never held up by drawing on the UI thread.
        // Everything below this line is owned by that hook thread; the UI is told what to do via Ui(...).
        Thread hookThread;
        Control hookInvoker;
        int keyHookError, mouseHookError;
        readonly ManualResetEvent hooksReady = new ManualResetEvent(false);
        volatile bool ringOpen;
        volatile bool openHoverSel;   // select-on-hover for the ring that is open (global or per-app)
        int ringGen;   // bumped on every open/close from either thread; a queued Show only runs if it is still current
        RingGeom openGeom;
        List<Item> openItems;
        List<List<Item>> openPages;   // the sets the open ring cycles through
        int openPage, openSizeN;   // openSizeN: tiles in the biggest set, which sizes the ring
        int openHover = -1;
        bool trigHeld, maskPending;
        uint trigDownTime;
        bool swallowL, swallowR, swallowM;
        bool wDown, cDown, aDown, sDown;
        ToolStripMenuItem pauseItem, startupItem;
        WebUi web;
        Control invoker;   // lets the web server's worker threads run code on the UI thread

        // Set by the editor while it records a shortcut; receives "Ctrl+Shift+T" or null (Esc = cancel).
        public static volatile Action<string, int> CaptureCallback;
        public static volatile int CapVk = -1;
        public static volatile string CapDevice = "";   // hardware id of the key just recorded       // key being recorded (waiting for its release)
        static volatile string capChord; static uint capTime;
        public static volatile bool CapAllowMod;                            // macro steps may be a lone modifier; the trigger may not
        static int capPendVk = -1; static string capPendChord; static uint capPendTime;

        public TrayApp(bool openEditor)
        {
            configPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ring.json");
            if (!File.Exists(configPath)) File.WriteAllText(configPath, Config.SampleJson);
            try { cfg = Config.Load(configPath); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read ring.json:\n" + ex.Message + "\n\nUsing built-in defaults.", "RingAction");
                File.WriteAllText(configPath + ".sample", Config.SampleJson);
                string tmp = Path.GetTempFileName(); File.WriteAllText(tmp, Config.SampleJson);
                cfg = Config.Load(tmp); File.Delete(tmp);
            }

            invoker = new ToolWindowControl();
            IntPtr unused = invoker.Handle;   // force handle creation on the UI thread

            keyProc = KeyProc; mouseProc = MouseProc;
            hookThread = new Thread(HookThreadMain);
            hookThread.IsBackground = true;
            hookThread.Name = "RingAction hooks";
            hookThread.Priority = ThreadPriority.Highest;   // keeps input latency low; the callbacks do very little work
            hookThread.SetApartmentState(ApartmentState.STA);
            hookThread.Start();
            // Both hooks are required: without the mouse hook the ring would open but ignore hover and clicks.
            string hookProblem = null;
            if (!hooksReady.WaitOne(5000)) hookProblem = "The input hooks did not start within 5 seconds.";
            else if (keyHook == IntPtr.Zero) hookProblem = "Could not install the keyboard hook (Windows error " + keyHookError + ").";
            else if (mouseHook == IntPtr.Zero) hookProblem = "Could not install the mouse hook (Windows error " + mouseHookError + ").";
            if (hookProblem != null)
            {
                MessageBox.Show(hookProblem + "\n\nRingAction will close. Security software can block input hooks.", "RingAction");
                Environment.Exit(1);
            }

            ContextMenuStrip menu = new ToolWindowMenu();
            ToolStripMenuItem hdr = new ToolStripMenuItem("RingAction"); hdr.Enabled = false;
            pauseItem = new ToolStripMenuItem("Pause"); pauseItem.CheckOnClick = true;
            pauseItem.Click += delegate
            {
                paused = pauseItem.Checked;
                RefreshTrayIcon();
                if (paused) { ringOpen = false; Interlocked.Increment(ref ringGen); ring.Hide(); }   // also voids a Show still queued
            };
            ToolStripMenuItem reload = new ToolStripMenuItem("Reload config"); reload.Click += delegate { Reload(); };
            ToolStripMenuItem editRings = new ToolStripMenuItem("Open editor");
            editRings.Click += delegate { OpenEditor(); };
            ToolStripMenuItem edit = new ToolStripMenuItem("Open ring.json");
            edit.Click += delegate { Process.Start("notepad.exe", "\"" + configPath + "\""); };
            startupItem = new ToolStripMenuItem("Run at startup"); startupItem.CheckOnClick = true;
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += delegate { SetStartup(startupItem.Checked); };
            ToolStripMenuItem exit = new ToolStripMenuItem("Exit"); exit.Click += delegate { Quit(); };
            menu.Items.AddRange(new ToolStripItem[] { hdr, new ToolStripSeparator(), editRings, pauseItem, reload, edit, startupItem,
                new ToolStripSeparator(), exit });
            editRings.Tag = "\uE70F"; pauseItem.Tag = "\uE769"; reload.Tag = "\uE72C"; edit.Tag = "\uE8A5"; startupItem.Tag = "\uE7E8"; exit.Tag = "\uE711";
            FluentMenu.Apply(menu);

            RefreshTrayIcon();
            prefHandler = delegate { try { invoker.BeginInvoke(new Action(RefreshTrayIcon)); } catch { } };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += prefHandler;
            tray.Text = "RingAction (" + cfg.Trigger.Text + ")";
            tray.MouseUp += delegate(object o, MouseEventArgs me) { if (me.Button == MouseButtons.Right || me.Button == MouseButtons.Left) ShowTrayMenu(menu); };
            tray.Visible = true;
            if (openEditor) invoker.BeginInvoke(new Action(OpenEditor));   // runs once the message loop is up
            invoker.BeginInvoke(new Action(delegate { ring.WarmUp(cfg.Default); }));
        }

        System.Windows.Forms.Timer menuWatch;

        // The menu belongs to a hidden window in a background process, so Windows does not always tell it that focus moved
        // (clicking the desktop, or the tray flyout closing). Close it ourselves when that happens.
        void WatchTrayMenu(ContextMenuStrip menu)
        {
            if (menuWatch == null) menuWatch = new System.Windows.Forms.Timer { Interval = 80 };
            IntPtr fg0 = Native.Foreground();
            int born = Environment.TickCount;
            menuWatch.Tick -= MenuWatchTick; menuWatch.Tick += MenuWatchTick;
            menuWatch.Tag = new object[] { menu, fg0, born };
            menuWatch.Start();
        }

        void MenuWatchTick(object o, EventArgs e)
        {
            object[] st = menuWatch.Tag as object[];
            ContextMenuStrip m = st == null ? null : (ContextMenuStrip)st[0];
            if (m == null || !m.Visible) { menuWatch.Stop(); return; }
            bool grace = Environment.TickCount - (int)st[2] < 400;
            if ((Native.AnyMouseButtonDown() && !grace && !m.Bounds.Contains(Cursor.Position)) || (!grace && Native.Foreground() != (IntPtr)st[1]))
            { menuWatch.Stop(); m.Close(); }
        }

        void ShowTrayMenu(ContextMenuStrip menu)
        {
            try
            {
                FluentMenu.Prepare(menu);
                menu.PerformLayout();
                Size sz = menu.Size;
                Rectangle anchor = TrayPlacement.AnchorFor(tray);
                int gap = (int)Math.Round(8 * Native.MonitorScale(new Point(anchor.Left, anchor.Top)));
                Point at = TrayPlacement.MenuOrigin(anchor, sz, gap);
                Native.Focus(invoker.Handle);   // so a click elsewhere dismisses the menu
                menu.Show(at);
                WatchTrayMenu(menu);
            }
            catch { try { FluentMenu.Prepare(menu); } catch { } menu.Show(Cursor.Position); }
        }

        // LL hook callbacks are delivered to the thread that installed the hook, which must pump messages.
        void HookThreadMain()
        {
            hookInvoker = new RawWnd();
            IntPtr unused = hookInvoker.Handle;
            try { RawKeyboard.Register(unused); } catch { }   // device filter only; the trigger works without it
            keyHook = Native.InstallHook(Native.WH_KEYBOARD_LL, keyProc);
            if (keyHook == IntPtr.Zero) keyHookError = Marshal.GetLastWin32Error();
            mouseHook = Native.InstallHook(Native.WH_MOUSE_LL, mouseProc);
            if (mouseHook == IntPtr.Zero) mouseHookError = Marshal.GetLastWin32Error();
            hooksReady.Set();
            Application.Run();
        }

        // Hook thread -> UI thread, fire and forget.
        void Ui(Action a)
        {
            try { invoker.BeginInvoke(a); } catch { }
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        IntPtr trayHandle = IntPtr.Zero;
        Microsoft.Win32.UserPreferenceChangedEventHandler prefHandler;
        int trayKey = -1;   // (light, paused) of the icon currently shown; skip rebuilding when unchanged

        // White mark on a dark taskbar, black on a light one; dimmed while paused.
        void RefreshTrayIcon()
        {
            try
            {
                bool light = false;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SoftwareMicrosoftWindowsCurrentVersionThemesPersonalize"))
                { object v = k == null ? null : k.GetValue("SystemUsesLightTheme"); light = v is int && (int)v == 1; }
                int key = (light ? 1 : 0) | (paused ? 2 : 0) | (SystemInformation.SmallIconSize.Width << 2);
                if (key == trayKey && tray.Icon != null) return;
                Size sz = SystemInformation.SmallIconSize;
                using (Stream st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(light ? "tray-black-32.png" : "tray-white-32.png"))
                using (Bitmap src = new Bitmap(st))
                using (Bitmap b = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(b))
                using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float[][] m = { new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, paused ? 0.4f : 1f, 0 }, new float[] { 0, 0, 0, 0, 1 } };
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(m));
                    float k = 1.10f; int dw = (int)Math.Round(sz.Width * k), dh = (int)Math.Round(sz.Height * k);   // the mark has margin in its art box; 10% larger fills the slot better
                    g.DrawImage(src, new Rectangle((sz.Width - dw) / 2, (sz.Height - dh) / 2, dw, dh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    IntPtr h = b.GetHicon();
                    Icon ic = Icon.FromHandle(h);
                    tray.Icon = ic;
                    if (trayHandle != IntPtr.Zero) DestroyIcon(trayHandle);
                    trayHandle = h; trayKey = key;
                }
            }
            catch { if (tray.Icon == null) tray.Icon = SystemIcons.Application; }   // never leave the tray entry invisible
        }

        void Balloon(string title, string text)
        {
            tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Warning);
        }

        public bool Reload()
        {
            try
            {
                Config c = Config.Load(configPath);
                cfg = c;
                tray.Text = "RingAction (" + cfg.Trigger.Text + ")";
                tray.ShowBalloonTip(1500, "RingAction", "Config reloaded", ToolTipIcon.Info);
                return true;
            }
            catch (Exception ex) { Balloon("Config not reloaded", ex.Message); return false; }
        }

        void OpenEditor()
        {
            try
            {
                if (web == null) { web = new WebUi(this, configPath); web.Start(); }
                web.OpenWindow();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "RingAction editor"); web = null; }
        }

        // Called from server threads.
        public object OnUi(Func<object> f)
        {
            return invoker.InvokeRequired ? invoker.Invoke(f) : f();
        }

        public void RunItem(Item it) { Execute(it); }

        // Opens the Windows file picker above the editor window. Returns the chosen path (shortcuts resolve to their target), or null.
        public string PickFile(string purpose)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            using (Form owner = new Form())
            using (OpenFileDialog d = new OpenFileDialog())
            {
                // An invisible always-on-top owner keeps the dialog in front of the editor's browser window.
                owner.FormBorderStyle = FormBorderStyle.None; owner.ShowInTaskbar = false; owner.TopMost = true; owner.Opacity = 0;
                owner.StartPosition = FormStartPosition.Manual; owner.Size = new Size(1, 1);
                owner.Location = new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 3);
                d.CheckFileExists = true; d.DereferenceLinks = true;
                if (purpose == "app")
                {
                    d.Title = "Choose the app to add a ring for";
                    d.Filter = "Programs (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*";
                    string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    if (Directory.Exists(pf)) d.InitialDirectory = pf;
                }
                else
                {
                    d.Title = "Choose a program or file";
                    d.Filter = "All files (*.*)|*.*|Programs (*.exe)|*.exe";
                }
                owner.Show();
                Native.Focus(owner.Handle);   // the dialog inherits focus from its owner
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
            }
        }

        public string ConfigText()
        {
            return File.Exists(configPath) ? File.ReadAllText(configPath) : Config.SampleJson;
        }

        // Validates, normalizes and writes the config. Throws FormatException/ArgumentException with a user-facing message.
        public string SaveConfig(string json)
        {
            Config c = Config.Parse(json);
            string normalized = c.ToJson();
            if (File.Exists(configPath)) File.Copy(configPath, configPath + ".bak", true);
            File.WriteAllText(configPath, normalized);
            Reload();
            return normalized;
        }

        bool IsStartupEnabled()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    k.DeleteValue("ActionRing", false);   // value written by the app's earlier name
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { Balloon("Startup setting failed", ex.Message); startupItem.Checked = IsStartupEnabled(); }
        }

        void Quit()
        {
            if (prefHandler != null) Microsoft.Win32.SystemEvents.UserPreferenceChanged -= prefHandler;
            ring.HideNow();
            CaptureCallback = null;
            if (web != null) web.Stop();
            try
            {
                hookInvoker.Invoke(new Action(delegate
                {
                    Native.Unhook(keyHook); Native.Unhook(mouseHook);
                    Application.ExitThread();
                }));
            }
            catch { }
            tray.Visible = false; tray.Dispose();
            ExitThread();
        }

        // ---- keyboard hook -------------------------------------------------
        IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (HandleKey(wParam.ToInt32(), k)) return (IntPtr)1;
                }
                catch { }
            }
            return Native.CallNextHookEx(keyHook, code, wParam, lParam);
        }

        // returns true to swallow the event
        bool HandleKey(int msg, Native.KBDLLHOOKSTRUCT k)
        {
            if ((k.flags & Native.LLKHF_INJECTED) != 0) return false;   // ignore injected input (incl. our own)
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            int vk = (int)k.vkCode;

            if (Chord.IsWin(vk)) wDown = down;
            else if (Chord.IsCtrl(vk)) cDown = down;
            else if (Chord.IsAlt(vk)) aDown = down;
            else if (Chord.IsShift(vk)) sDown = down;

            if (SeqRecorder.Active) return SeqRecorder.Handle(down, vk, k.time, wDown, cDown, aDown, sDown);   // editor is recording a whole macro

            if (CaptureCallback != null)   // editor is recording a shortcut: swallow everything
            {
                if (CapVk >= 0)
                {
                    if (!down && vk == CapVk)   // released: report the chord and how long it was held
                    {
                        Action<string, int> done = CaptureCallback;
                        CaptureCallback = null;
                        int held = (int)(k.time - capTime);
                        CapVk = -1;
                        done(capChord, held < 60 ? 0 : Math.Min(held, Step.MaxHold));
                    }
                    return true;
                }
                if (CapAllowMod && Chord.IsModifier(vk))
                {
                    if (down)
                    {
                        if (capPendVk != vk) { capPendChord = Chord.ModifierOnly(vk, k.time, capPendVk, capPendTime, wDown, cDown, aDown, sDown); capPendVk = vk; capPendTime = k.time; }
                    }
                    else if (vk == capPendVk)   // released with nothing pressed in between: record the modifier itself
                    {
                        Action<string, int> done = CaptureCallback;
                        CaptureCallback = null;
                        int held = (int)(k.time - capPendTime);
                        string chord = capPendChord; capPendVk = -1;
                        done(chord, held < 60 ? 0 : Math.Min(held, Step.MaxHold));
                    }
                    return true;
                }
                if (down && !Chord.IsModifier(vk))
                {
                    capPendVk = -1;
                    bool bare = !wDown && !cDown && !aDown && !sDown;
                    if (vk == 0x1B && bare)
                    {
                        Action<string, int> cb = CaptureCallback;
                        CaptureCallback = null;
                        cb(null, 0);
                    }
                    else
                    {
                        capChord = Chord.Compose(wDown, cDown, aDown, sDown, vk); capTime = k.time; CapVk = vk;
                        try { CapDevice = RawKeyboard.ShortId(RawKeyboard.DeviceFor(vk, 25)); } catch { CapDevice = ""; }   // which hardware pressed it
                    }
                }
                return true;
            }

            if (paused) { trigHeld = false; return false; }

            Chord t = cfg.Trigger;
            if (vk == t.Vk && !Chord.IsModifier(vk))
            {
                if (down)
                {
                    if (trigHeld) return true;                         // swallow auto-repeat
                    if (wDown == t.Win && cDown == t.Ctrl && aDown == t.Alt && sDown == t.Shift)
                    {
                        if (cfg.TriggerDevice.Length > 0)   // locked to one device: a press from any other keyboard is just a keystroke
                        {
                            string dev = null;
                            try { dev = RawKeyboard.DeviceFor(vk, 25); } catch { }
                            if (dev != null && dev.IndexOf(cfg.TriggerDevice, StringComparison.OrdinalIgnoreCase) < 0) return false;   // unknown device => fail open
                        }
                        string proc = Native.ForegroundProcessName();
                        List<List<Item>> pages = cfg.PagesFor(proc);
                        List<Item> items = pages[0];
                        if (items.Count == 0) return false;           // empty ring for this app: let the key through
                        trigHeld = true; trigDownTime = k.time;
                        if (t.Win) maskPending = true;
                        Point cur = Cursor.Position;
                        openSizeN = 0; foreach (List<Item> pg in pages) openSizeN = Math.Max(openSizeN, pg.Count);
                        RingGeom g = RingGeom.At(cur, items.Count, openSizeN);
                        bool inside;
                        int h0 = g.HitTest(cur, out inside);
                        openHoverSel = cfg.HoverFor(proc);
                        openGeom = g; openItems = items; openPages = pages; openPage = 0; openHover = h0; ringOpen = true;
                        int gen = Interlocked.Increment(ref ringGen);
                        Ui(delegate { if (ringOpen && Volatile.Read(ref ringGen) == gen) ring.Show(items, g, h0); });
                        return true;
                    }
                }
                else if (trigHeld)
                {
                    trigHeld = false;
                    // Held past the threshold => classic "release to select"; a quick tap leaves the ring open for a click.
                    if (ringOpen && k.time - trigDownTime >= (uint)cfg.HoldSelectMs) CommitFromHook(openHover);
                    return true;
                }
            }

            // Win was used in a chord we swallowed: stop Windows treating its release as a Start-menu tap.
            if (!down && Chord.IsWin(vk) && maskPending)
            {
                maskPending = false;
                Native.SendKeys(new List<int> { 0xE8 });   // VK 0xE8 is unassigned
            }

            if (down && vk == 0x1B && ringOpen) { CancelFromHook(); return true; }   // Esc cancels
            return false;
        }

        void CancelFromHook()
        {
            ringOpen = false;
            Interlocked.Increment(ref ringGen);
            Ui(delegate { ring.Hide(); });
        }

        // A "switch set" tile flips the open ring to its next set and keeps it open.
        void SwitchSet()
        {
            openPage = (openPage + 1) % openPages.Count;
            List<Item> items = openPages[openPage];
            RingGeom g = RingGeom.ForScale(openGeom.Scale, items.Count, openSizeN);
            g.Center = openGeom.Center;
            bool inside;
            int h0 = g.HitTest(Cursor.Position, out inside);   // the tile now under the pointer must not fire by itself
            openGeom = g; openItems = items; openHover = h0;
            int gen = Volatile.Read(ref ringGen);
            Ui(delegate { if (ringOpen && Volatile.Read(ref ringGen) == gen) ring.Swap(items, g, h0); });
        }

        void CommitFromHook(int index)
        {
            List<Item> cur = openItems;
            if (ringOpen && openPages != null && openPages.Count > 1 && cur != null && index >= 0 && index < cur.Count && cur[index].Type == "preset")
            {
                SwitchSet();
                return;
            }
            ringOpen = false;
            Interlocked.Increment(ref ringGen);
            List<Item> items = openItems;
            Ui(delegate { Select(index, items); });
        }

        // ---- mouse hook ----------------------------------------------------
        IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    int m = wParam.ToInt32();
                    if (HandleMouse(m, (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT))))
                        return (IntPtr)1;
                }
                catch { }
            }
            return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        bool HandleMouse(int msg, Native.MSLLHOOKSTRUCT ms)
        {
            if ((ms.flags & 1) != 0) return false; // LLMHF_INJECTED
            if (msg == Native.WM_LBUTTONUP) { bool s = swallowL; swallowL = false; return s; }
            if (msg == Native.WM_RBUTTONUP) { bool s = swallowR; swallowR = false; return s; }
            if (msg == Native.WM_MBUTTONUP) { bool s = swallowM; swallowM = false; return s; }
            if (!ringOpen) return false;
            bool inside;
            int hit = openGeom.HitTest(new Point(ms.x, ms.y), out inside);

            if (msg == 0x200 /* WM_MOUSEMOVE */)   // hover comes straight from mouse movement, never swallowed
            {
                if (hit != openHover)
                {
                    bool enteredTile = hit >= 0 && openHoverSel;   // a tile that was not already under the pointer at open
                    openHover = hit;
                    if (enteredTile) { CommitFromHook(hit); return false; }
                    Ui(delegate { ring.SetHover(hit); });
                }
                return false;
            }
            bool isDown = msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN;
            if (!isDown) return false;

            if (msg == Native.WM_LBUTTONDOWN && hit >= 0) { swallowL = true; CommitFromHook(hit); return true; }
            CancelFromHook();   // click on the hub, a non-left button, or outside: cancel
            if (!inside) return false;                // outside click still reaches the app
            if (msg == Native.WM_LBUTTONDOWN) swallowL = true;
            else if (msg == Native.WM_RBUTTONDOWN) swallowR = true;
            else swallowM = true;
            return true;
        }

        // ---- actions ---------------------------------------------------------
        void Select(int index, List<Item> items)
        {
            ring.Commit(index);
            if (items == null || index < 0 || index >= items.Count) return;
            Item it = items[index];
            // let the overlay vanish and modifier state settle before acting
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer(); t.Interval = 40;
            t.Tick += delegate { t.Stop(); t.Dispose(); Execute(it); };
            t.Start();
        }

        void Execute(Item it)
        {
            try
            {
                switch (it.Type)
                {
                    case "launch":
                        Process.Start(new ProcessStartInfo(it.Target, it.Args ?? "") { UseShellExecute = true });
                        break;
                    case "url":
                        Process.Start(new ProcessStartInfo(it.Target) { UseShellExecute = true });
                        break;
                    case "command":
                        Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + it.Target) { UseShellExecute = false, CreateNoWindow = true });
                        break;
                    case "shortcut":
                        Native.SendKeys(it.Keys.SendOrder());
                        break;
                    case "sequence":
                        List<Step> steps = it.Steps;
                        ThreadPool.QueueUserWorkItem(delegate { RunSteps(steps); });
                        break;
                }
            }
            catch (Exception ex) { Balloon("Action failed: " + it.Label, ex.Message); }
        }

        static readonly object stepsGate = new object();   // one macro at a time: two running together would interleave their keys

        static void RunSteps(List<Step> steps)
        {
            lock (stepsGate)
            try
            {
                // release-to-select leaves the trigger's modifier (Win of Win+D) physically down for a moment;
                // keys sent meanwhile would become Win+Ctrl+A and so on
                Native.WaitModifiersUp(3000);
                foreach (Step s in steps)
                {
                    if (s.Kind == "keys") { Native.SendKeys(s.Chord.SendOrder(), s.Hold); Thread.Sleep(s.After > 0 ? s.After : 20); }
                    else if (s.Kind == "text") { Native.SendText(s.Value); Thread.Sleep(s.After > 0 ? s.After : 20); }
                    else if (s.Kind == "delay") Thread.Sleep(s.Ms);
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- Local web UI (loopback only, token-protected)
    sealed class WebUi
    {
        readonly TrayApp app;
        HttpListener listener;
        string token, baseUrl, hostHeader;

        public WebUi(TrayApp app, string configPath) { this.app = app; }

        public void Start()
        {
            byte[] b = new byte[16];
            using (System.Security.Cryptography.RNGCryptoServiceProvider r = new System.Security.Cryptography.RNGCryptoServiceProvider())
                r.GetBytes(b);
            token = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

            Random rnd = new Random();
            for (int i = 0; i < 25; i++)
            {
                int port = rnd.Next(49200, 65000);
                HttpListener l = new HttpListener();
                l.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    l.Start();
                    listener = l;
                    baseUrl = "http://127.0.0.1:" + port;
                    hostHeader = "127.0.0.1:" + port;
                    break;
                }
                catch (HttpListenerException) { l.Close(); }
            }
            if (listener == null) throw new InvalidOperationException("Could not open a local port for the editor.");
            listener.BeginGetContext(OnContext, null);
        }

        public void Stop()
        {
            try { if (listener != null) listener.Close(); } catch { }
        }

        public void OpenWindow()
        {
            string url = baseUrl + "/?t=" + token;
            // Test hook: lets automated checks drive the editor in their own browser instead of launching Edge.
            string urlFile = Environment.GetEnvironmentVariable("RINGACTION_URLFILE");
            if (!string.IsNullOrEmpty(urlFile)) { File.WriteAllText(urlFile, url); return; }
            foreach (string exe in new string[] { "msedge", "chrome" })
            {
                try { Process.Start(new ProcessStartInfo(exe, "--app=" + url) { UseShellExecute = true }); return; }
                catch { }
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        void OnContext(IAsyncResult ar)
        {
            HttpListenerContext ctx;
            try { ctx = listener.EndGetContext(ar); }
            catch { return; }   // listener closed
            try { listener.BeginGetContext(OnContext, null); } catch { }
            try { Handle(ctx); }
            catch (Exception ex) { Send(ctx, 500, "{\"error\":" + Js(ex.Message) + "}"); }
        }

        static string Js(string s) { return new JavaScriptSerializer().Serialize(s); }

        static void Send(HttpListenerContext ctx, int status, string body, string type)
        {
            byte[] data = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }

        static void SendBytes(HttpListenerContext ctx, byte[] data, string type)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = type;
            ctx.Response.Headers["Cache-Control"] = "private, max-age=600";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }

        // ---- app icons: memory cache, then icons\<name>.png next to the exe, then the running process ----
        readonly Dictionary<string, byte[]> iconCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        static readonly System.Text.RegularExpressions.Regex SafeName =
            new System.Text.RegularExpressions.Regex("^[A-Za-z0-9._ -]{1,64}$");

        byte[] AppIcon(string name, bool keep)
        {
            byte[] png;
            lock (iconCache) if (iconCache.TryGetValue(name, out png)) { if (keep) Persist(name, png); return png; }
            string file = Path.Combine(IconDir, name + ".png");
            try { if (File.Exists(file)) png = File.ReadAllBytes(file); } catch { }
            if (png == null)
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try { if (png == null) png = IconPng(p.MainModule.FileName); }
                    catch { }   // elevated or protected processes can't be read
                    p.Dispose();
                }
            }
            if (png == null) return null;
            lock (iconCache) iconCache[name] = png;
            if (keep) Persist(name, png);
            return png;
        }

        // The file's own icon as PNG bytes, or null if it has none or can't be read.
        static byte[] IconPng(string exePath)
        {
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(exePath))
                using (Bitmap bmp = ic.ToBitmap())
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        // Names that usually mean "starter for another program": the running app has a different process name.
        static readonly string[] LauncherNames = { "update", "squirrel", "launcher", "setup", "install", "installer", "uninstall", "unins000" };

        static string IconDir { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "icons"); } }

        static void Persist(string name, byte[] png)
        {
            try
            {
                string file = Path.Combine(IconDir, name + ".png");
                if (File.Exists(file)) return;
                Directory.CreateDirectory(IconDir);
                File.WriteAllBytes(file, png);
            }
            catch { }
        }

        static void Send(HttpListenerContext ctx, int status, string json)
        {
            Send(ctx, status, json, "application/json; charset=utf-8");
        }

        static string ReadBody(HttpListenerRequest rq)
        {
            if (rq.ContentLength64 > 1024 * 1024) throw new InvalidOperationException("request too large");
            using (StreamReader sr = new StreamReader(rq.InputStream, Encoding.UTF8)) return sr.ReadToEnd();
        }

        void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest rq = ctx.Request;
            // DNS-rebinding guard: only our own loopback host name is accepted.
            if (rq.Headers["Host"] != hostHeader) { Send(ctx, 403, "{\"error\":\"bad host\"}"); return; }
            string path = rq.Url.AbsolutePath;

            if (path == "/" && rq.HttpMethod == "GET")
            {
                if (rq.QueryString["t"] != token) { Send(ctx, 403, "Open the editor from the RingAction tray icon.", "text/plain"); return; }
                using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html"))
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    Send(ctx, 200, sr.ReadToEnd(), "text/html; charset=utf-8");
                return;
            }

            // <img> tags can't send headers, so the icon route also accepts the token as ?t=
            bool authed = rq.Headers["X-Token"] == token || (path == "/api/icon" && rq.QueryString["t"] == token);
            if (!path.StartsWith("/api/") || !authed)
            { Send(ctx, 403, "{\"error\":\"forbidden\"}"); return; }

            if (path == "/api/icon")
            {
                string name = rq.QueryString["name"] ?? "";
                byte[] png = SafeName.IsMatch(name) ? AppIcon(name, rq.QueryString["keep"] == "1") : null;
                if (png == null) Send(ctx, 404, "{\"error\":\"no icon\"}");
                else SendBytes(ctx, png, "image/png");
                return;
            }

            JavaScriptSerializer js = new JavaScriptSerializer();
            switch (path)
            {
                case "/api/config":
                    if (rq.HttpMethod == "GET") { Send(ctx, 200, (string)app.OnUi(delegate { return app.ConfigText(); })); return; }
                    if (rq.HttpMethod == "PUT")
                    {
                        string body = ReadBody(rq);
                        try
                        {
                            if (rq.QueryString["check"] == "1") { Send(ctx, 200, Config.Parse(body).ToJson()); return; }   // dry run: validate and normalize, write nothing
                            string saved = (string)app.OnUi(delegate { return app.SaveConfig(body); });
                            Send(ctx, 200, saved);
                        }
                        catch (Exception ex)
                        {
                            Exception e = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                            Send(ctx, 400, "{\"error\":" + Js(e.Message) + "}");
                        }
                        return;
                    }
                    break;

                case "/api/processes":
                    {
                        List<string> names = new List<string>();
                        foreach (Process p in Process.GetProcesses())
                        {
                            try { if (p.MainWindowHandle != IntPtr.Zero && !names.Contains(p.ProcessName)) names.Add(p.ProcessName); }
                            catch { }
                            p.Dispose();
                        }
                        names.Sort(StringComparer.OrdinalIgnoreCase);
                        Send(ctx, 200, js.Serialize(names));
                        return;
                    }

                case "/api/foreground":
                    Send(ctx, 200, "{\"name\":" + Js(Native.ForegroundProcessName()) + "}");
                    return;

                case "/api/pick":   // browsers hide file paths from pages, so the exe opens the picker itself
                    {
                        string purpose = rq.QueryString["purpose"] == "target" ? "target" : "app";
                        string picked = (string)app.OnUi(delegate { return app.PickFile(purpose); });
                        if (picked == null) { Send(ctx, 200, "{\"cancelled\":true}"); return; }
                        string name = Path.GetFileNameWithoutExtension(picked);
                        if (purpose == "app" && SafeName.IsMatch(name))
                        {
                            byte[] png = IconPng(picked);   // so the real icon shows even if the app has never been run
                            if (png != null) { lock (iconCache) iconCache[name] = png; Persist(name, png); }
                        }
                        bool launcher = Array.IndexOf(LauncherNames, name.ToLowerInvariant()) >= 0;
                        Send(ctx, 200, "{\"path\":" + Js(picked) + ",\"name\":" + Js(name) + ",\"launcher\":" + (launcher ? "true" : "false") + "}");
                        return;
                    }

                case "/api/record":
                    {
                        string result = null; int heldMs = 0;
                        ManualResetEvent ev = new ManualResetEvent(false);
                        app.OnUi(delegate
                        {
                            TrayApp.CapVk = -1;
                            TrayApp.CapAllowMod = rq.QueryString["mod"] == "1";
                            TrayApp.CaptureCallback = delegate(string s, int ms) { result = s; heldMs = ms; ev.Set(); };
                            return null;
                        });
                        bool got = ev.WaitOne(10000);
                        app.OnUi(delegate { if (!got) { TrayApp.CaptureCallback = null; TrayApp.CapVk = -1; } return null; });
                        Send(ctx, 200, got && result != null ? "{\"keys\":" + Js(result) + ",\"hold\":" + heldMs + ",\"device\":" + Js(TrayApp.CapDevice ?? "") + "}" : "{\"cancelled\":true}");
                        return;
                    }

                case "/api/record/seq/start":
                    app.OnUi(delegate { TrayApp.CaptureCallback = null; TrayApp.CapVk = -1; SeqRecorder.Start(); return null; });
                    Send(ctx, 200, "{}");
                    return;

                case "/api/record/seq/status":
                    Send(ctx, 200, "{\"active\":" + (SeqRecorder.Active ? "true" : "false") + ",\"count\":" + SeqRecorder.Count + "}");
                    return;

                case "/api/record/seq/stop":
                    Send(ctx, 200, SeqRecorder.Stop());
                    return;

                case "/api/record/cancel":
                    app.OnUi(delegate { TrayApp.CaptureCallback = null; TrayApp.CapVk = -1; return null; });
                    Send(ctx, 200, "{}");
                    return;

                case "/api/test":
                    {
                        try
                        {
                            IDictionary<string, object> d = js.DeserializeObject(ReadBody(rq)) as IDictionary<string, object>;
                            Item it = Config.ParseItem(d);
                            app.OnUi(delegate { app.RunItem(it); return null; });
                            Send(ctx, 200, "{}");
                        }
                        catch (FormatException ex) { Send(ctx, 400, "{\"error\":" + Js(ex.Message) + "}"); }
                        return;
                    }
            }
            Send(ctx, 404, "{\"error\":\"not found\"}");
        }
    }

    // ---------------------------------------------------------------- Key logger (step 0: verify what the button sends)
    sealed class KeyLogForm : Form
    {
        readonly TextBox box = new TextBox();
        readonly Native.HookProc proc;
        IntPtr hook;
        uint lastTime;
        readonly string logPath;

        public KeyLogForm()
        {
            Text = "RingAction key logger - press your mouse button now (nothing is suppressed)";
            Width = 760; Height = 420;
            box.Multiline = true; box.ReadOnly = true; box.Dock = DockStyle.Fill; box.ScrollBars = ScrollBars.Vertical;
            box.Font = new Font("Consolas", 10f);
            Controls.Add(box);
            logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "keylog.txt");
            proc = Proc;
            hook = Native.InstallHook(Native.WH_KEYBOARD_LL, proc);
            box.Text = "Columns: delta-ms since previous event | DOWN/UP | vk | scan | flags\r\n" +
                       "A mouse macro shows as Win+D with ~0-5 ms gaps and an instant D release.\r\n" +
                       "Also logged to " + logPath + "\r\n\r\n";
            FormClosed += delegate { Native.Unhook(hook); };
        }

        IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    uint delta = lastTime == 0 ? 0 : k.time - lastTime; lastTime = k.time;
                    string flags = ((k.flags & Native.LLKHF_EXTENDED) != 0 ? "ext " : "") +
                                   ((k.flags & Native.LLKHF_INJECTED) != 0 ? "injected" : "hw");
                    string line = string.Format("{0,6} ms  {1,-4} vk=0x{2:X2} ({3})  scan=0x{4:X2}  {5}",
                        delta, down ? "DOWN" : "UP", k.vkCode, (Keys)k.vkCode, k.scanCode, flags);
                    box.AppendText(line + "\r\n");
                    File.AppendAllText(logPath, line + "\r\n");
                }
                catch { }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            List<string> a = new List<string>(args);

            if (a.Contains("--render"))   // dev aid: --render out.png [--dark] [--scale 1.5] [--hover 0]
            {
                int i = a.IndexOf("--render");
                string path = i + 1 < a.Count ? a[i + 1] : "ring.png";
                float sc = 1f; int hover = 1, j;
                if ((j = a.IndexOf("--scale")) >= 0 && j + 1 < a.Count) float.TryParse(a[j + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sc);
                if ((j = a.IndexOf("--hover")) >= 0 && j + 1 < a.Count) int.TryParse(a[j + 1], out hover);
                Config c = Config.Load(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ring.json"));
                Ring r = new Ring();
                using (Bitmap bmp = r.Preview(c.Default, hover, sc, a.Contains("--dark"))) bmp.Save(path, ImageFormat.Png);
                return;
            }

            if (a.Contains("--render-menu"))   // dev aid: --render-menu out.png [--dark] [--scale 1.5] [--hover 3]
            {
                int i = a.IndexOf("--render-menu");
                string path = i + 1 < a.Count ? a[i + 1] : "menu.png";
                float sc = 1f; int hover = 3, j;
                if ((j = a.IndexOf("--scale")) >= 0 && j + 1 < a.Count) float.TryParse(a[j + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sc);
                if ((j = a.IndexOf("--hover")) >= 0 && j + 1 < a.Count) int.TryParse(a[j + 1], out hover);
                ContextMenuStrip m = new ContextMenuStrip();
                ToolStripMenuItem hd = new ToolStripMenuItem("RingAction"); hd.Enabled = false;
                ToolStripMenuItem ed = new ToolStripMenuItem("Open editor"); ed.Tag = "\uE70F";
                ToolStripMenuItem pz = new ToolStripMenuItem("Pause"); pz.Tag = "\uE769"; pz.Checked = true;
                ToolStripMenuItem rl = new ToolStripMenuItem("Reload config"); rl.Tag = "\uE72C";
                ToolStripMenuItem ej = new ToolStripMenuItem("Open ring.json"); ej.Tag = "\uE8A5";
                ToolStripMenuItem st = new ToolStripMenuItem("Run at startup"); st.Tag = "\uE7E8"; st.Checked = true;
                ToolStripMenuItem ex = new ToolStripMenuItem("Exit"); ex.Tag = "\uE711";
                m.Items.AddRange(new ToolStripItem[] { hd, new ToolStripSeparator(), ed, pz, rl, ej, st, new ToolStripSeparator(), ex });
                using (Bitmap bmp = FluentMenu.Preview(m, hover, sc, a.Contains("--dark"))) bmp.Save(path, ImageFormat.Png);
                return;
            }

            bool log = a.Contains("--keylog");
            bool created;
            using (Mutex mx = new Mutex(true, log ? @"Local\RingAction.KeyLog" : @"Local\RingAction.Main", out created))
            {
                if (!created) return;
                if (log) Application.Run(new KeyLogForm());
                else Application.Run(new TrayApp(a.Contains("--editor")));
            }
        }
    }
}
