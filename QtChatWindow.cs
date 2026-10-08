using System.Text.RegularExpressions;

namespace YellowInsideLib;

// ──── Qt 메신저(KakaoTalkUI.exe) 채팅창 판별 및 버튼 위치 계산 ────────────────
// Qt Quick 채팅창은 내부 컨트롤이 HWND가 아니므로 UI Automation으로 하단 입력 툴바의
// 아이콘 버튼(+, 이모티콘, 파일) 위치를 읽고, 같은 간격으로 이어지는 다음 슬롯에 디시콘 버튼을 둔다.
// UIA 클래스 이름 뒤의 QMLTYPE 번호는 빌드마다 바뀔 수 있으므로 앞부분만 비교한다.
static partial class QtChatWindow
{
    const string ChatWindowClassNameMarker = "ChatWindow";        // NormalChatWindow_QMLTYPE_736 등
    const string FooterClassNamePrefix = "ChatViewFooter";        // ChatViewFooterNormal_QMLTYPE_738
    const string IconButtonClassNamePrefix = "GrayIconButton";    // GrayIconButton_QMLTYPE_102_QML_633
    const float IconScale = 0.75f;                                // 디시콘 아이콘이 버튼 슬롯에서 차지하는 비율 (메신저 툴바 아이콘과 같은 약 60%로 그리면 작아 보인다)

    [GeneratedRegex(@"^Qt\d+QWindowIcon$", RegexOptions.CultureInvariant)]
    private static partial Regex QtWindowClassNameRegex();

    public static bool IsQtWindow(IntPtr hwnd) => QtWindowClassNameRegex().IsMatch(Win32.GetClassNameString(hwnd));

    // ── 1차 후보 판별 (Win32 정보만 사용, UIA 호출 없음) ─────────────────────
    public static bool IsCandidateWindow(IntPtr hwnd, uint processId)
    {
        if (processId == 0 || !Win32.IsWindowVisible(hwnd)) return false;
        if ((Win32.GetWindowLong(hwnd, Win32.GWL_STYLE).ToInt64() & Win32.WS_CHILD) != 0) return false;
        _ = Win32.GetWindowThreadProcessId(hwnd, out var windowProcessId);
        return windowProcessId == processId && IsQtWindow(hwnd);
    }

    // ── 채팅창 판별 (UIA 전용 스레드에서 호출) ──────────────────────────────
    public static bool IsChatWindow(UiAutomationClient client, IntPtr hwnd)
    {
        using var window = client.ElementFromHandle(hwnd);
        return window is not null && IsChatWindowClassName(window.ClassName);
    }

    // ── 채팅창 판별 + 버튼 앵커 측정 (UIA 전용 스레드에서 호출) ─────────────
    public static QtChatWindowProbeResult Probe(UiAutomationClient client, IntPtr chatHwnd)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterForWindow(chatHwnd);
        using var window = client.ElementFromHandle(chatHwnd);
        if (window is null) return QtChatWindowProbeResult.Failed("UIA 루트 요소를 가져오지 못함");
        // 접근성 초기화 직후 등 클래스 이름이 비어 있는 순간은 채팅창이 아니라고 단정하지 않는다
        if (window.ClassName.Length == 0) return QtChatWindowProbeResult.Failed("UIA 클래스 이름 없음");
        if (!IsChatWindowClassName(window.ClassName)) return QtChatWindowProbeResult.NotChatWindow;

        var windowChildren = client.FindChildren(window);
        try
        {
            var footer = windowChildren.FirstOrDefault(element => element.ClassName.StartsWith(FooterClassNamePrefix, StringComparison.Ordinal) && element.BoundingRectangle.Width > 0);
            if (footer is null) return QtChatWindowProbeResult.AnchorNotFound("입력 툴바(ChatViewFooter) 없음");

            var footerChildren = client.FindChildren(footer);
            try
            {
                if (FindNextButtonSlot(footerChildren, footer.BoundingRectangle) is not { } slot) return QtChatWindowProbeResult.AnchorNotFound("툴바 아이콘 버튼 없음");
                return CreateAnchor(chatHwnd, slot.ButtonRect, slot.Gap) is { } anchor ? QtChatWindowProbeResult.Succeeded(anchor) : QtChatWindowProbeResult.Failed("채팅창 클라이언트 좌표를 가져오지 못함");
            }
            finally { UiAutomationElement.DisposeAll(footerChildren); }
        }
        finally { UiAutomationElement.DisposeAll(windowChildren); }
    }

    // ── 다음 버튼 슬롯 계산 ──────────────────────────────────────────────────
    // 툴바 맨 아래 줄 왼쪽의 정사각형 아이콘 버튼들을 찾고, 마지막 버튼 오른쪽에 같은 간격으로 이어 붙인다.
    // 버튼 이름은 메신저 언어 설정에 따라 바뀌므로 사용하지 않는다.
    static (Win32.RECT ButtonRect, int Gap)? FindNextButtonSlot(List<UiAutomationElement> footerChildren, Win32.RECT footerRect)
    {
        var footerCenterX = footerRect.Left + footerRect.Width / 2;
        List<UiAutomationElement> candidates = [.. footerChildren.Where(element => element.ControlType == UiAutomationClient.ButtonControlTypeId && IsSquare(element.BoundingRectangle) && element.BoundingRectangle.Right <= footerCenterX)];
        List<Win32.RECT> iconButtons = [.. candidates.Where(element => element.ClassName.StartsWith(IconButtonClassNamePrefix, StringComparison.Ordinal)).Select(element => element.BoundingRectangle)];
        if (iconButtons.Count == 0) iconButtons = [.. candidates.Select(element => element.BoundingRectangle)];
        if (iconButtons.Count == 0) return null;

        var bottomRowBottom = iconButtons.Max(rect => rect.Bottom);
        List<Win32.RECT> rowButtons = [.. iconButtons.Where(rect => Math.Abs(rect.Bottom - bottomRowBottom) <= rect.Height / 4).OrderBy(rect => rect.Left)];
        var lastButton = rowButtons[^1];
        var pitch = rowButtons.Count >= 2 ? lastButton.Left - rowButtons[^2].Left : lastButton.Width * 8 / 7;
        pitch = Math.Max(pitch, lastButton.Width);

        var left = lastButton.Left + pitch;
        var buttonRect = new Win32.RECT { Left = left, Top = lastButton.Top, Right = left + lastButton.Width, Bottom = lastButton.Bottom };
        return (buttonRect, pitch - lastButton.Width);
    }

    // ── 앵커 생성: UIA 화면 좌표 → 클라이언트 좌하단 기준 오프셋 ─────────────
    static QtButtonAnchor? CreateAnchor(IntPtr chatHwnd, Win32.RECT buttonRect, int gap)
    {
        if (!Win32.GetClientRect(chatHwnd, out var clientRect) || clientRect.Height <= 0) return null;
        var clientOrigin = new Win32.POINT();
        if (!Win32.ClientToScreen(chatHwnd, ref clientOrigin)) return null;

        // 배경색은 이전 버튼과 디시콘 버튼 사이 여백의 가운데에서 샘플링한다
        var backgroundSampleOffsetX = -Math.Max(gap / 2, 1);
        return new QtButtonAnchor(buttonRect.Left - clientOrigin.X, clientOrigin.Y + clientRect.Height - buttonRect.Top, buttonRect.Width, backgroundSampleOffsetX, clientRect.Width, clientRect.Height, Win32.GetDpiForWindow(chatHwnd));
    }

    // ── 버튼 배치 계산 (호출 스레드는 채팅창 DPI 인식 컨텍스트에 있어야 함) ──
    // 앵커 측정 이후 DPI가 바뀌었으면 새 앵커가 도착할 때까지 배율만큼 환산해서 사용한다
    public static ButtonLayout? CalculateButtonLayout(IntPtr chatHwnd, QtButtonAnchor anchor)
    {
        if (!Win32.GetClientRect(chatHwnd, out var clientRect) || clientRect.Height <= 0) return null;

        var windowDpi = Win32.GetDpiForWindow(chatHwnd);
        var dpiScale = anchor.Dpi == 0 || windowDpi == 0 ? 1.0 : windowDpi / (double)anchor.Dpi;
        var size = Scale(anchor.Size, dpiScale);
        var clientX = Scale(anchor.OffsetX, dpiScale);
        var clientY = clientRect.Height - Scale(anchor.OffsetFromBottom, dpiScale);
        var backgroundSamplePoint = new Win32.POINT { X = clientX + Scale(anchor.BackgroundSampleOffsetX, dpiScale), Y = clientY + size / 2 };
        return new ButtonLayout(clientX, clientY, size, IconScale, backgroundSamplePoint);
    }

    // ── 앵커 측정 이후 클라이언트 크기 변경 여부 (재측정 판단용) ──────────────
    public static bool HasClientSizeChanged(IntPtr chatHwnd, QtButtonAnchor anchor)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterForWindow(chatHwnd);
        return Win32.GetClientRect(chatHwnd, out var clientRect) && (clientRect.Width != anchor.ClientWidth || clientRect.Height != anchor.ClientHeight);
    }

    static bool IsChatWindowClassName(string className) => className.Contains(ChatWindowClassNameMarker, StringComparison.Ordinal);

    static bool IsSquare(Win32.RECT rect) => rect.Width > 0 && Math.Abs(rect.Width - rect.Height) <= Math.Max(2, rect.Width / 10);

    static int Scale(int value, double scale) => (int)Math.Round(value * scale);
}
