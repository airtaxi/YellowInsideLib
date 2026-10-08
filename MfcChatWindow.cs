namespace YellowInsideLib;

// ──── MFC 메신저(KakaoTalk.exe) 채팅창 판별 및 버튼 위치 계산 ─────────────────
static class MfcChatWindow
{
    // ── 채팅창 판별: EVA_Window_Dblclk + 메신저 PID + RICHEDIT50W 보유 ───
    public static bool IsChatWindow(IntPtr hwnd, uint processId)
    {
        if (!Win32.IsWindowVisible(hwnd)) return false;
        if (Win32.GetClassNameString(hwnd) != "EVA_Window_Dblclk") return false;
        _ = Win32.GetWindowThreadProcessId(hwnd, out var windowProcessId);
        if (windowProcessId != processId) return false;

        var hasRichEdit = false;
        Win32.EnumChildWindows(hwnd, (childHwnd, _) =>
        {
            if (Win32.GetClassNameString(childHwnd) != "RICHEDIT50W") return true;
            hasRichEdit = true;
            return false;
        }, IntPtr.Zero);
        return hasRichEdit;
    }

    // ── 버튼 위치 계산 (물리픽셀 screen → chatWindow client 좌표) ────────────
    // kakaoButton = toolbarH - 10 은 실제 버튼 폭보다 ~1.5배 크게 추정됨
    // 따라서 * 2 가 경험적으로 (+, 이모티콘, 파일) 3개 건너뛰기에 해당함
    public static ButtonLayout CalculateButtonLayout(IntPtr chatHwnd, LogSink? logSink = null)
    {
        Win32.GetWindowRect(chatHwnd, out var wndRect);
        Win32.RECT richRect = default;
        Win32.EnumChildWindows(chatHwnd, (childHwnd, _) =>
        {
            if (Win32.GetClassNameString(childHwnd) != "RICHEDIT50W") return true;
            Win32.GetWindowRect(childHwnd, out richRect);
            return false;
        }, IntPtr.Zero);

        // 툴바 좌측 하단 모서리 근처에서 배경 픽셀 샘플링
        var backgroundSamplePoint = new Win32.POINT { X = wndRect.Left + 5, Y = wndRect.Bottom - 5 };
        Win32.ScreenToClient(chatHwnd, ref backgroundSamplePoint);

        // 창이 정리되는 중에는 리치에디트 사각형이 일시적으로 어긋날 수 있으므로 툴바 높이를 검증한다
        if (richRect.Width > 0 && richRect.Bottom < wndRect.Bottom)
        {
            var toolbarTop      = richRect.Bottom;
            var toolbarH        = wndRect.Bottom - toolbarTop;
            var kakaoButtonSlot = (int)(toolbarH * 0.875);            // 메신저 버튼 슬롯 폭 (비율 기반, DPI 독립)
            var ourSize         = toolbarH / 2;                       // 디시콘 버튼 크기 (툴바 50%)
            var ourBtnTop       = toolbarTop + (int)((toolbarH - ourSize) * 0.45);  // 세로 중앙보다 살짝 위
            var ourBtnX         = richRect.Left                       // +/이모티콘/파일 3개 건너뜀
                                // + (int)(kakaoButtonSlot * (IsReplyThreadWindow(chatHwnd) ? 2.125 : 2.875)) -> 카나나 버튼 등 다시 생길 경우 아래 대신 이 코드 사용
                                + (int)(kakaoButtonSlot * 2.125)
                                + (kakaoButtonSlot - ourSize) / 2;    // 슬롯 내 수평 중앙 정렬
            var pt = new Win32.POINT { X = ourBtnX, Y = ourBtnTop };
            Win32.ScreenToClient(chatHwnd, ref pt);
            return new ButtonLayout(pt.X, pt.Y, ourSize, 1.0f, backgroundSamplePoint);
        }

        var windowDpi = Win32.GetDpiForWindow(chatHwnd);
        var dpiScale = windowDpi / 96.0;
        logSink?.Warn($"0x{chatHwnd:X8}: RICHEDIT50W 앵커 없음 — DPI 기반 폴백 사용 (DPI: {windowDpi}, scale: {dpiScale:F2}x)");
        var fallbackSize = (int)(18 * dpiScale);
        var fallbackPt = new Win32.POINT
        {
            X = wndRect.Left + (int)(39 * dpiScale),
            Y = wndRect.Bottom - (int)(21 * dpiScale),
        };
        Win32.ScreenToClient(chatHwnd, ref fallbackPt);
        return new ButtonLayout(fallbackPt.X, fallbackPt.Y, fallbackSize, 1.0f, backgroundSamplePoint);
    }

    // ── 답장(쓰레드) 창 판별 ─────────────────────────────────────────────────
    // Edit 클래스(ctrlId=100) 자식이 없으면 쓰레드 창 (보조 구분법)
    static bool IsReplyThreadWindow(IntPtr chatHwnd)
    {
        var hasEditControl = false;
        Win32.EnumChildWindows(chatHwnd, (childHwnd, _) =>
        {
            if (Win32.GetClassNameString(childHwnd) != "Edit" || Win32.GetDlgCtrlID(childHwnd) != 100) return true;
            hasEditControl = true;
            return false;
        }, IntPtr.Zero);
        return !hasEditControl;
    }
}
