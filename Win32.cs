using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── Win32 API ────────────────────────────────────────────────────────────────────
static partial class Win32
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint msEventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  => Right  - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) [{Width}x{Height}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint   cbSize;
        public uint   style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public unsafe char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public POINT  pt;
    }

    // hdc(8) + fErase(4) + rcPaint(16) + fRestore(4) + fIncUpdate(4) + rgbReserved[32] = 68 bytes
    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int    fErase;
        public RECT   rcPaint;
        public int    fRestore;
        public int    fIncUpdate;
        int _r0, _r1, _r2, _r3, _r4, _r5, _r6, _r7;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint   cbSize;
        public uint   dwFlags;
        public IntPtr hwndTrack;
        public uint   dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DROPFILES
    {
        public uint pFiles;
        public int  pointX;
        public int  pointY;
        public int  fNC;
        public int  fWide;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint   Flags;
        public uint   Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int    DeltaX, DeltaY;
        public uint   MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint   Message;
        public ushort ParamLow, ParamHigh;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputData
    {
        [FieldOffset(0)] public MOUSEINPUT   Mouse;
        [FieldOffset(0)] public KEYBDINPUT   Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint      Type;
        public InputData Data;
    }

    // ── DPI ─────────────────────────────────────────────────────────────────────────────
    // !! 반드시 최초 실행: 이후 모든 GetWindowRect/ScreenToClient가 물리픽셀 기준이 됨
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    // ── 창 검색 / 열거 ──────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static unsafe partial int GetWindowText(IntPtr hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static unsafe partial int GetClassName(IntPtr hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetParent(IntPtr hwnd);

    // ── WinEvent 훅 ──────────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    public static partial IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(IntPtr hook);

    // ── 창 위치 / 스타일 ────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hwnd, IntPtr rect,
        [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(IntPtr hwnd, int index);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLong64(IntPtr hwnd, int index);
    public static IntPtr GetWindowLong(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLong64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(IntPtr hwnd, int index, int value);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLong64(IntPtr hwnd, int index, IntPtr value);
    public static IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLong64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, (int)value));

    // ── 창 클래스 / 생성 / 제거 ──────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial ushort RegisterClassEx(in WNDCLASSEX wndClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string className, IntPtr hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    // ── 메시지 루프 ───────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT paintStruct);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hwnd, in PAINTSTRUCT paintStruct);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in MSG message);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr proc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hwnd, IntPtr id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT trackMouseEvent);

    // ── 기타 ─────────────────────────────────────────────────────────────────────────────
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial IntPtr GetModuleHandle(IntPtr moduleName);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursor(IntPtr hInstance, int cursorName);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial void keybd_event(byte virtualKey, byte scanCode, uint flags, IntPtr extraInfo);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SendMessageText(IntPtr hwnd, uint message, IntPtr wParam, string lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDlgItem(IntPtr dialog, int dialogItemId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilter(uint message, uint flag);

    // ── 입력 시뮬레이션 ──────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll", SetLastError = true)]
    public static unsafe partial uint SendInput(uint numberOfInputs, INPUT* inputs, int sizeOfInput);

    // ── 클립보드 ────────────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hwndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint format, IntPtr handle);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    public static partial uint EnumClipboardFormats(uint format);

    // ── 메모리 할당 ────────────────────────────────────────────────────────────────────────
    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalFree(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(IntPtr handle);

    // ── 픽셀 / DC ──────────────────────────────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial uint GetPixel(IntPtr hdc, int x, int y);

    public const uint CLR_INVALID = 0xFFFFFFFF;

    // ── 문자열 출력 헬퍼 ───────────────────────────────────────────────────────────────────
    public static unsafe string GetWindowTextString(IntPtr hwnd, int maxLength = 256)
    {
        char* buffer = stackalloc char[maxLength];
        int length = GetWindowText(hwnd, buffer, maxLength);
        return new string(buffer, 0, length);
    }

    public static unsafe string GetClassNameString(IntPtr hwnd, int maxLength = 256)
    {
        char* buffer = stackalloc char[maxLength];
        int length = GetClassName(hwnd, buffer, maxLength);
        return new string(buffer, 0, length);
    }

    // ── 상수 ────────────────────────────────────────────────────────────────────────────
    public const uint WM_PAINT        = 0x000F;
    public const uint WM_LBUTTONDOWN  = 0x0201;
    public const uint WM_LBUTTONUP    = 0x0202;
    public const uint WM_MOUSEMOVE    = 0x0200;
    public const uint WM_MOUSELEAVE   = 0x02A3;
    public const uint WM_TIMER        = 0x0113;
    public const uint WM_DESTROY      = 0x0002;
    public const uint WM_QUIT         = 0x0012;
    public const uint TME_LEAVE       = 0x0002;
    public const uint SWP_NOSIZE      = 0x0001;
    public const uint SWP_NOMOVE      = 0x0002;
    public const uint SWP_NOACTIVATE  = 0x0010;
    public const uint SWP_SHOWWINDOW  = 0x0040;
    public const int  GWL_STYLE       = -16;
    public const long WS_CHILD        = 0x40000000L;
    public const long WS_VISIBLE      = 0x10000000L;
    public const uint EVENT_OBJECT_DESTROY        = 0x8001;
    public const uint EVENT_OBJECT_SHOW           = 0x8002;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT       = 0;
    public const int  IDC_HAND        = 32649;
    public const byte VK_CONTROL      = 0x11;
    public const byte VK_T            = 0x54;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint WM_SETTEXT      = 0x000C;
    public const uint BM_CLICK        = 0x00F5;

    public const uint WM_DROPFILES       = 0x0233;
    public const uint WM_COPYGLOBALDATA  = 0x0049;
    public const uint WM_APP             = 0x8000;
    public const uint GHND               = 0x0042;
    public const uint MSGFLT_ADD         = 1;

    public const uint CF_HDROP           = 15;
    public const uint CF_UNICODETEXT     = 13;
    public const uint CF_TEXT            = 1;

    public const uint INPUT_KEYBOARD     = 1;
    public const byte VK_V              = 0x56;
    public const byte VK_RETURN         = 0x0D;

    public static readonly IntPtr DPI_CONTEXT_PER_MONITOR_V2 = new IntPtr(-4);
    public static readonly IntPtr HWND_TOP                   = IntPtr.Zero;
}
