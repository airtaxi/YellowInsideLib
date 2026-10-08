namespace YellowInsideLib;

// ──── UIA 요소 (캐시된 속성 + COM 참조) ──────────────────────────────────────
sealed class UiAutomationElement(IntPtr pointer, string className, int controlType, Win32.RECT boundingRectangle) : IDisposable
{
    public IntPtr Pointer { get; private set; } = pointer;
    public string ClassName { get; } = className;
    public int ControlType { get; } = controlType;
    public Win32.RECT BoundingRectangle { get; } = boundingRectangle;

    public void Dispose()
    {
        UiAutomationClient.Release(Pointer);
        Pointer = IntPtr.Zero;
    }

    public static void DisposeAll(List<UiAutomationElement> elements)
    {
        foreach (var element in elements) element.Dispose();
        elements.Clear();
    }
}
