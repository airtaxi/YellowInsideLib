using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── 채팅창 1개에 대응하는 디시콘 버튼 세션 ──────────────────────────────
sealed class DcconSession(IntPtr chatHwnd, Image? icon, LogSink? logSink = null)
{
    const uint TimerId = 1;

    public IntPtr ChatHwnd   { get; }      = chatHwnd;
    public IntPtr ButtonHwnd { get; private set; }

    public Action<DcconSession>? Clicked { get; set; }

    int     _clientX;
    int     _clientY;
    int     _size;
    bool    _hovered;
    bool    _pressed;
    bool    _trackingMouse;
    Color   _toolbarBackground = Color.White;
    Bitmap? _bmpNormal;
    Bitmap? _bmpHovered;
    Bitmap? _bmpPressed;
    IntPtr  _coordinateSpaceContext;   // 버튼 좌표를 계산한 공간(채팅창의 DPI 인식 컨텍스트)
    uint    _coordinateSpaceDpi;       // 같은 시점의 채팅창 DPI
    double  _deviceScale = 1.0;        // 좌표 1단위가 실제 화면에서 차지하는 픽셀 수

    // ── 버튼 생성 ────────────────────────────────────────────────────────────
    public bool CreateButton()
    {
        // 채팅창과 같은 DPI 인식 컨텍스트에서 계산하고 생성해야 좌표 공간이 일치한다.
        // 어긋난 상태로 자식 창을 만들면 Windows가 프로세스의 기존 창들까지 채팅창의
        // 인식 모드로 강제 변환하며, 버튼 좌표와 비트맵 크기가 서로 다른 공간 기준이 되어 잘린다.
        using var dpiScope = ThreadDpiAwarenessScope.EnterForWindow(ChatHwnd);
        if (!dpiScope.IsActive) logSink?.Warn($"[DPI] 0x{ChatHwnd:X8} 채팅창 DPI 인식 컨텍스트 전환 실패 — 호출 스레드 컨텍스트로 계산합니다");

        var (clientX, clientY, size) = CalcButtonPosition(ChatHwnd, logSink);
        _clientX = clientX;
        _clientY = clientY;
        _size = size;
        RefreshCoordinateSpace();
        CreateBitmaps();

        ButtonHwnd = Win32.CreateWindowEx(
            0, SessionManager.ButtonWndClassName, "",
            (uint)(Win32.WS_CHILD | Win32.WS_VISIBLE),
            clientX, clientY, size, size,
            ChatHwnd, IntPtr.Zero,
            Win32.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

        if (ButtonHwnd == IntPtr.Zero) return false;

        logSink?.Info($"[DPI] 0x{ChatHwnd:X8} 채팅창 인식: {Win32.DescribeDpiAwarenessContext(_coordinateSpaceContext)}, 채팅창 DPI: {_coordinateSpaceDpi}, 물리 배율: {_deviceScale:F2}x, 버튼: {size}px @ ({clientX},{clientY})");
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

    // ── 위치 갱신 (채팅창 이동·리사이즈·DPI 변경 시) ─────────────────────────
    public void UpdatePosition()
    {
        if (ButtonHwnd == IntPtr.Zero) return;

        using var dpiScope = ThreadDpiAwarenessScope.EnterForWindow(ChatHwnd);

        var (clientX, clientY, newSize) = CalcButtonPosition(ChatHwnd, logSink);
        if (newSize <= 0) return;   // 창 정리 중 등 좌표가 유효하지 않은 순간은 건너뛴다

        bool sizeChanged = newSize != _size;
        bool spaceChanged = RefreshCoordinateSpace();

        if (!sizeChanged && !spaceChanged && clientX == _clientX && clientY == _clientY) return;

        if (sizeChanged || spaceChanged)
        {
            logSink?.Info($"[DPI] 0x{ChatHwnd:X8} 버튼 재생성 — 크기 {_size}px → {newSize}px, 인식: {Win32.DescribeDpiAwarenessContext(_coordinateSpaceContext)}, 채팅창 DPI: {_coordinateSpaceDpi}, 물리 배율: {_deviceScale:F2}x");
            _size = newSize;
            CreateBitmaps();
        }

        _clientX = clientX;
        _clientY = clientY;
        Win32.MoveWindow(ButtonHwnd, clientX, clientY, _size, _size, true);
        Win32.InvalidateRect(ButtonHwnd, IntPtr.Zero, false);
    }

    // ── 좌표 공간 기억 / 변경 감지 ───────────────────────────────────────────
    bool RefreshCoordinateSpace()
    {
        IntPtr awarenessContext = Win32.GetWindowDpiAwarenessContext(ChatHwnd);
        uint windowDpi = Win32.GetDpiForWindow(ChatHwnd);
        double deviceScale = MeasureDeviceScale(ChatHwnd);

        bool changed = awarenessContext != _coordinateSpaceContext || windowDpi != _coordinateSpaceDpi || Math.Abs(deviceScale - _deviceScale) > 0.01;

        _coordinateSpaceContext = awarenessContext;
        _coordinateSpaceDpi = windowDpi;
        _deviceScale = deviceScale;
        return changed;
    }

    // 좌표 1단위가 실제 화면에서 차지하는 픽셀 수를 측정한다.
    // GDI 스케일링이 적용된 채팅창에서는 좌표 공간이 96 DPI 기준으로 보고되므로 이 값이 1보다 커진다.
    static double MeasureDeviceScale(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var coordinateRect) || coordinateRect.Width <= 0) return 1.0;

        using var physicalScope = ThreadDpiAwarenessScope.Enter(Win32.DPI_CONTEXT_PER_MONITOR_V2);
        if (!Win32.GetWindowRect(hwnd, out var physicalRect) || physicalRect.Width <= 0) return 1.0;

        return Math.Clamp((double)physicalRect.Width / coordinateRect.Width, 0.5, 4.0);
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
                using (var graphics = Graphics.FromHdc(hdc))
                {
                    Win32.GetClientRect(hwnd, out var clientRect);
                    PaintButton(graphics, clientRect.Width, clientRect.Height);
                }
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
            case Win32.WM_DPICHANGED:
            case Win32.WM_DPICHANGED_AFTERPARENT:
                // 채팅창이 DPI 변경을 통지하면 버튼 좌표와 비트맵을 새 공간 기준으로 다시 만든다.
                // (WM_DPICHANGED_BEFOREPARENT 는 부모 재배치 전이므로 무시하고, 부모의 후속
                //  위치 변경 이벤트와 주기 검증이 최종 좌표를 확정한다)
                UpdatePosition();
                return IntPtr.Zero;
            case Win32.WM_DPICHANGED_BEFOREPARENT:
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
    void PaintButton(Graphics graphics, int clientWidth, int clientHeight)
    {
        Bitmap? bitmap = _pressed ? _bmpPressed : _hovered ? _bmpHovered : _bmpNormal;
        if (bitmap == null)
        {
            graphics.Clear(_toolbarBackground);
            return;
        }

        // 항상 목적 사각형을 지정해 그린다.
        // DrawImageUnscaled 는 이미지 DPI와 DC DPI가 다르면 배율을 한 번 더 곱하므로
        // (예: 96 DPI 비트맵을 192 DPI DC 에 그리면 2배) 아이콘이 확대되어 잘려 보인다.
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, new Rectangle(0, 0, Math.Max(clientWidth, 1), Math.Max(clientHeight, 1)));
    }

    void CreateBitmaps()
    {
        DisposeBitmaps();
        _toolbarBackground = SampleToolbarBackgroundColor(ChatHwnd);

        // 화면 배율만큼 큰 비트맵을 만들어 두고 그릴 때 목적 크기로 축소한다.
        // 이렇게 하면 좌표 공간이 96 DPI 기준으로 보고되는 환경에서도 아이콘이 선명하게 그려진다.
        int bitmapSize = Math.Max((int)Math.Round(_size * _deviceScale), 1);
        _bmpNormal  = RenderButtonToBitmap(1.0f, bitmapSize);
        _bmpHovered = RenderButtonToBitmap(0.85f, bitmapSize);
        _bmpPressed = RenderButtonToBitmap(0.70f, bitmapSize);
    }

    void DisposeBitmaps()
    {
        _bmpNormal?.Dispose();  _bmpNormal  = null;
        _bmpHovered?.Dispose(); _bmpHovered = null;
        _bmpPressed?.Dispose(); _bmpPressed = null;
    }

    Bitmap RenderButtonToBitmap(float iconOpacity, int size)
    {
        var bitmap = new Bitmap(size, size);
        bitmap.SetResolution(96, 96);   // 이미지 DPI 차이로 인한 배율 왜곡 방지
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
            // 아이콘이 없을 때의 텍스트 폴백도 비트맵 배율에 맞춰 크기를 조정한다
            float bitmapScale = size / (float)Math.Max(_size, 1);
            using var font = new Font("Segoe UI", 7.5f * bitmapScale, FontStyle.Bold);
            var textSize = graphics.MeasureString("Dc", font);
            graphics.DrawString("Dc", font, Brushes.LightGray, (size - textSize.Width) / 2f, (size - textSize.Height) / 2f);
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

        // 창이 정리되는 중에는 리치에디트 사각형이 일시적으로 어긋날 수 있으므로 툴바 높이를 검증한다
        if (richRect.Width > 0 && richRect.Bottom < wndRect.Bottom)
        {
            int toolbarTop      = richRect.Bottom;
            int toolbarH        = wndRect.Bottom - toolbarTop;
            int kakaoButtonSlot = (int)(toolbarH * 0.875);            // 메신저 버튼 슬롯 폭 (비율 기반, DPI 독립)
            int ourSize         = toolbarH / 2;                       // 디시콘 버튼 크기 (툴바 50%)
            int ourBtnTop       = toolbarTop + (int)((toolbarH - ourSize) * 0.45);  // 세로 중앙보다 살짝 위
            int ourBtnX         = richRect.Left                       // +/이모티콘/파일 3개 건너뜀
                                // + (int)(kakaoButtonSlot * (IsReplyThreadWindow(chatHwnd) ? 2.125 : 2.875)) -> 카나나 버튼 등 다시 생길 경우 아래 대신 이 코드 사용
                                + (int)(kakaoButtonSlot * 2.125)
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
