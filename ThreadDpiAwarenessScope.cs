namespace YellowInsideLib;

// ──── 스레드 DPI 인식 컨텍스트 임시 전환 ─────────────────────────────────────
// 채팅창은 다른 프로세스의 창이므로, 그 좌표계(클라이언트 좌표)에서 계산하거나
// 자식 창을 만들 때는 호출 스레드의 DPI 인식 컨텍스트를 채팅창과 일치시켜야 한다.
// 컨텍스트가 어긋나면 Windows가 프로세스의 기존 창들을 채팅창 인식 모드로 강제
// 변환하고, 버튼 좌표와 비트맵 크기가 서로 다른 공간 기준이 되어 아이콘이 잘린다.
readonly struct ThreadDpiAwarenessScope(IntPtr previousContext, bool isActive) : IDisposable
{
    public static ThreadDpiAwarenessScope Enter(IntPtr awarenessContext)
    {
        if (awarenessContext == IntPtr.Zero) return default;
        IntPtr previousContext = Win32.SetThreadDpiAwarenessContext(awarenessContext);
        if (previousContext == IntPtr.Zero) return default;
        return new ThreadDpiAwarenessScope(previousContext, true);
    }

    public static ThreadDpiAwarenessScope EnterForWindow(IntPtr hwnd) =>
        Enter(Win32.GetWindowDpiAwarenessContext(hwnd));

    public bool IsActive => isActive;

    public void Dispose()
    {
        if (isActive)
        {
            Win32.SetThreadDpiAwarenessContext(previousContext);
        }
    }
}
