using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── 채팅창 1개에 대응하는 디시콘 버튼 세션 ──────────────────────────────
sealed class DcconSession(IntPtr chatHwnd, LogSink? logSink = null)
{
    const uint TimerId = 1;

    public IntPtr ChatHwnd   { get; }      = chatHwnd;
    public IntPtr ButtonHwnd { get; private set; }

    public Action<DcconSession>? Clicked { get; set; }

    int     _size;
    bool    _hovered;
    bool    _pressed;
    bool    _trackingMouse;
    Color   _toolbarBackground = Color.White;
    Bitmap? _bmpNormal;
    Bitmap? _bmpHovered;
    Bitmap? _bmpPressed;

    // ── 버튼 생성 ────────────────────────────────────────────────────────────
    public bool CreateButton(Image? icon)
    {
        var (clientX, clientY, size) = CalcButtonPosition(ChatHwnd, logSink);
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
        var (clientX, clientY, newSize) = CalcButtonPosition(ChatHwnd, logSink);
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
        graphics.Clear(_toolbarBackground);
    }

    void CreateBitmaps(Image? icon)
    {
        DisposeBitmaps();
        _toolbarBackground = SampleToolbarBackgroundColor(ChatHwnd);
        _bmpNormal  = RenderButtonToBitmap(1.0f, icon);
        _bmpHovered = RenderButtonToBitmap(0.85f, icon);
        _bmpPressed = RenderButtonToBitmap(0.70f, icon);
    }

    void DisposeBitmaps()
    {
        _bmpNormal?.Dispose();  _bmpNormal  = null;
        _bmpHovered?.Dispose(); _bmpHovered = null;
        _bmpPressed?.Dispose(); _bmpPressed = null;
    }

    Bitmap RenderButtonToBitmap(float iconOpacity, Image? icon)
    {
        int size = Math.Max(_size, 1);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(_toolbarBackground);
        if (icon != null)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (iconOpacity < 1.0f)
            {
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                float[][] matrix =
                [
                    [1, 0, 0, 0,           0],
                    [0, 1, 0, 0,           0],
                    [0, 0, 1, 0,           0],
                    [0, 0, 0, iconOpacity, 0],
                    [0, 0, 0, 0,           1],
                ];
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(matrix));
                graphics.DrawImage(icon,
                    new Rectangle(0, 0, size, size),
                    0, 0, icon.Width, icon.Height,
                    GraphicsUnit.Pixel, attributes);
            }
            else
            {
                graphics.DrawImage(icon, 0, 0, size, size);
            }
        }
        else
        {
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            var textSize = graphics.MeasureString("Dc", font);
            graphics.DrawString("Dc", font, Brushes.LightGray,
                (size - textSize.Width) / 2f, (size - textSize.Height) / 2f);
        }
        return bitmap;
    }

    // ── 버튼 위치 계산 (물리픽셀 screen → chatWindow client 좌표) ────────────
    // kakaoButton = toolbarH - 10 은 실제 버튼 폭보다 ~1.5배 크게 추정됨
    // 따라서 * 2 가 경험적으로 (+, 이모티콘, 파일) 3개 건너뛰기에 해당함
    public static (int clientX, int clientY, int size) CalcButtonPosition(IntPtr chatHwnd, LogSink? logSink = null)
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
            int toolbarTop      = richRect.Bottom;
            int toolbarH        = wndRect.Bottom - toolbarTop;
            int kakaoButtonSlot = (int)(toolbarH * 0.875);            // 메신저 버튼 슬롯 폭 (비율 기반, DPI 독립)
            int ourSize         = toolbarH / 2;                       // 디시콘 버튼 크기 (툴바 50%)
            int ourBtnTop       = toolbarTop + (int)((toolbarH - ourSize) * 0.45);  // 세로 중앙보다 살짝 위
            int ourBtnX         = richRect.Left                       // +/이모티콘/파일 3개 건너뜀
                                + (int)(kakaoButtonSlot * (IsReplyThreadWindow(chatHwnd) ? 2.125 : 2.875))
                                + (kakaoButtonSlot - ourSize) / 2;    // 슬롯 내 수평 중앙 정렬
            var pt = new Win32.POINT { X = ourBtnX, Y = ourBtnTop };
            Win32.ScreenToClient(chatHwnd, ref pt);
            return (pt.X, pt.Y, ourSize);
        }

        uint windowDpi = Win32.GetDpiForWindow(chatHwnd);
        double dpiScale = windowDpi / 96.0;
        logSink?.Warn($"0x{chatHwnd:X8}: RICHEDIT50W 앵커 없음 — DPI 기반 폴백 사용 (DPI: {windowDpi}, scale: {dpiScale:F2}x)");
        int fallbackSize = (int)(18 * dpiScale);
        var fallbackPt = new Win32.POINT
        {
            X = wndRect.Left + (int)(39 * dpiScale),
            Y = wndRect.Bottom - (int)(21 * dpiScale),
        };
        Win32.ScreenToClient(chatHwnd, ref fallbackPt);
        return (fallbackPt.X, fallbackPt.Y, fallbackSize);
    }

    // ── 답장(쓰레드) 창 판별 ─────────────────────────────────────────────────
    // Edit 클래스(ctrlId=100) 자식이 없으면 쓰레드 창 (보조 구분법)
    static bool IsReplyThreadWindow(IntPtr chatHwnd)
    {
        bool hasEditControl = false;
        Win32.EnumChildWindows(chatHwnd, (childHwnd, _) =>
        {
            if (Win32.GetClassNameString(childHwnd) == "Edit" && Win32.GetDlgCtrlID(childHwnd) == 100)
            {
                hasEditControl = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return !hasEditControl;
    }

    // ── 툴바 배경색 자동 감지 (버튼 배치 영역 근처 픽셀 샘플링) ──────────────
    static Color SampleToolbarBackgroundColor(IntPtr chatHwnd)
    {
        Win32.GetWindowRect(chatHwnd, out var wndRect);
        // 툴바 좌측 하단 모서리 근처에서 배경 픽셀 샘플링
        int sampleX = wndRect.Left + 5;
        int sampleY = wndRect.Bottom - 5;

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return Color.White;

        try
        {
            uint colorRef = Win32.GetPixel(screenDc, sampleX, sampleY);
            if (colorRef == Win32.CLR_INVALID) return Color.White;
            int red   = (int)(colorRef & 0xFF);
            int green = (int)((colorRef >> 8) & 0xFF);
            int blue  = (int)((colorRef >> 16) & 0xFF);
            return Color.FromArgb(red, green, blue);
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
