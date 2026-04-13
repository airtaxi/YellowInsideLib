using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── 채팅창 1개에 대응하는 디시콘 버튼 세션 ──────────────────────────────
sealed class DcconSession(IntPtr chatHwnd, Action<string>? log = null)
{
    const uint TimerId = 1;

    public IntPtr ChatHwnd   { get; }      = chatHwnd;
    public IntPtr ButtonHwnd { get; private set; }

    public Action<DcconSession>? Clicked { get; set; }

    int     _size;
    bool    _hovered;
    bool    _pressed;
    bool    _trackingMouse;
    Bitmap? _bmpNormal;
    Bitmap? _bmpHovered;
    Bitmap? _bmpPressed;

    // ── 버튼 생성 ────────────────────────────────────────────────────────────
    public bool CreateButton(Image? icon)
    {
        var (clientX, clientY, size) = CalcButtonPosition(ChatHwnd, log);
        _size = size;
        CreateBitmaps(icon);

        ButtonHwnd = Win32.CreateWindowEx(
            0, SessionManager.ButtonWndClassName, "",
            (uint)(Win32.WS_CHILD | Win32.WS_VISIBLE),
            clientX, clientY, size, size,
            ChatHwnd, IntPtr.Zero,
            Win32.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

        if (ButtonHwnd == IntPtr.Zero) return false;

        Win32.SetTimer(ButtonHwnd, new IntPtr(TimerId), 16, IntPtr.Zero);
        return true;
    }

    // ── 버튼 제거 ────────────────────────────────────────────────────────────
    public void DestroyButton()
    {
        if (ButtonHwnd == IntPtr.Zero) return;
        IntPtr hwnd = ButtonHwnd;
        ButtonHwnd = IntPtr.Zero;  // WM_DESTROY에서 이중 정리 방지
        Win32.KillTimer(hwnd, new IntPtr(TimerId));
        if (Win32.IsWindow(hwnd)) Win32.DestroyWindow(hwnd);
        DisposeBitmaps();
    }

    // ── 위치 갱신 (채팅창 이동·리사이즈 시) ─────────────────────────────────
    public void UpdatePosition(Image? icon)
    {
        if (ButtonHwnd == IntPtr.Zero) return;
        var (clientX, clientY, newSize) = CalcButtonPosition(ChatHwnd, log);
        if (newSize != _size) { _size = newSize; CreateBitmaps(icon); }
        Win32.MoveWindow(ButtonHwnd, clientX, clientY, _size, _size, true);
    }

    // ── WndProc 메시지 처리 ───────────────────────────────────────────────────
    public IntPtr HandleMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Win32.WM_PAINT:
            {
                var paintStruct = new Win32.PAINTSTRUCT();
                IntPtr hdc = Win32.BeginPaint(hwnd, ref paintStruct);
                using (var graphics = Graphics.FromHdc(hdc)) PaintButton(graphics);
                Win32.EndPaint(hwnd, in paintStruct);
                return IntPtr.Zero;
            }
            case Win32.WM_LBUTTONDOWN:
                _pressed = true;
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case Win32.WM_LBUTTONUP:
                if (_pressed)
                {
                    _pressed = false;
                    Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                    Clicked?.Invoke(this);
                }
                return IntPtr.Zero;
            case Win32.WM_MOUSEMOVE:
                if (!_hovered) { _hovered = true; Win32.InvalidateRect(hwnd, IntPtr.Zero, false); }
                if (!_trackingMouse)
                {
                    _trackingMouse = true;
                    var tme = new Win32.TRACKMOUSEEVENT
                    {
                        cbSize      = (uint)Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                        dwFlags     = Win32.TME_LEAVE,
                        hwndTrack   = hwnd,
                        dwHoverTime = 0,
                    };
                    Win32.TrackMouseEvent(ref tme);
                }
                return IntPtr.Zero;
            case Win32.WM_MOUSELEAVE:
                _hovered = _pressed = _trackingMouse = false;
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case Win32.WM_TIMER:
                // EVA BitBlt이 버튼을 덮은 뒤 최대 16ms 이내 복구
                Win32.SetWindowPos(hwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                Win32.KillTimer(hwnd, new IntPtr(TimerId));
                DisposeBitmaps();
                ButtonHwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // ── 렌더링 ───────────────────────────────────────────────────────────────
    void PaintButton(Graphics graphics)
    {
        Bitmap? bmp = _pressed ? _bmpPressed : _hovered ? _bmpHovered : _bmpNormal;
        if (bmp != null) { graphics.DrawImageUnscaled(bmp, 0, 0); return; }
        // 폴백 (비트맵 준비 전)
        if      (_pressed) graphics.Clear(Color.FromArgb(80, 80, 80));
        else if (_hovered) graphics.Clear(Color.FromArgb(68, 68, 68));
        else               graphics.Clear(Color.FromArgb(50, 50, 50));
    }

    void CreateBitmaps(Image? icon)
    {
        DisposeBitmaps();
        _bmpNormal  = RenderButtonToBitmap(Color.FromArgb(50, 50, 50), icon);
        _bmpHovered = RenderButtonToBitmap(Color.FromArgb(68, 68, 68), icon);
        _bmpPressed = RenderButtonToBitmap(Color.FromArgb(80, 80, 80), icon);
    }

    void DisposeBitmaps()
    {
        _bmpNormal?.Dispose();  _bmpNormal  = null;
        _bmpHovered?.Dispose(); _bmpHovered = null;
        _bmpPressed?.Dispose(); _bmpPressed = null;
    }

    Bitmap RenderButtonToBitmap(Color backgroundColor, Image? icon)
    {
        int size = Math.Max(_size, 1);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(backgroundColor);
        if (icon != null)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            const int padding = 4;
            graphics.DrawImage(icon, padding, padding, _size - padding * 2, _size - padding * 2);
        }
        else
        {
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            var textSize = graphics.MeasureString("Dc", font);
            graphics.DrawString("Dc", font, Brushes.LightGray,
                (_size - textSize.Width) / 2f, (_size - textSize.Height) / 2f);
        }
        return bitmap;
    }

    // ── 버튼 위치 계산 (물리픽셀 screen → chatWindow client 좌표) ────────────
    // kakaoButton = toolbarH - 10 은 실제 버튼 폭보다 ~1.5배 크게 추정됨
    // 따라서 * 2 가 경험적으로 (+, 이모티콘, 파일) 3개 건너뛰기에 해당함
    public static (int clientX, int clientY, int size) CalcButtonPosition(IntPtr chatHwnd, Action<string>? log = null)
    {
        Win32.GetWindowRect(chatHwnd, out var wndRect);
        Win32.RECT richRect = default;
        Win32.EnumChildWindows(chatHwnd, (childHwnd, _) =>
        {
            if (Win32.GetClassNameString(childHwnd) != "RICHEDIT50W") return true;
            Win32.GetWindowRect(childHwnd, out richRect);
            return false;
        }, IntPtr.Zero);

        if (richRect.Width > 0)
        {
            int toolbarTop  = richRect.Bottom;
            int toolbarH    = wndRect.Bottom - toolbarTop;
            int kakaoButton = toolbarH - 10;                          // 메신저 버튼 폭 (추정)
            int ourSize     = toolbarH / 2;                           // 디시콘 버튼 크기 (툴바 50%)
            int ourBtnTop   = toolbarTop + (toolbarH - ourSize) / 2;  // 세로 중앙 정렬
            int ourBtnX     = richRect.Left                           // +/이모티콘/파일 3개 건너뜀
                            + kakaoButton * 2                         // (* 2 ≈ 실제 3개 버튼 폭)
                            + (kakaoButton - ourSize) / 2;            // 슬롯 내 수평 중앙 정렬
            var pt = new Win32.POINT { X = ourBtnX, Y = ourBtnTop };
            Win32.ScreenToClient(chatHwnd, ref pt);
            return (pt.X, pt.Y, ourSize);
        }

        log?.Invoke($"[WARN] 0x{chatHwnd:X8}: RICHEDIT50W 앵커 없음 — 폴백 사용");
        var fallbackPt = new Win32.POINT { X = wndRect.Left + 78, Y = wndRect.Bottom - 42 };
        Win32.ScreenToClient(chatHwnd, ref fallbackPt);
        return (fallbackPt.X, fallbackPt.Y, 36);
    }
}
