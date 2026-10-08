using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── UI Automation 클라이언트 (UIAutomationCore COM 직접 호출) ─────────────────
// NativeAOT 호환을 위해 런타임 COM 마샬링 없이 vtable 슬롯을 직접 호출한다.
// 슬롯 번호는 Windows SDK UIAutomationClient.h 의 C 스타일 vtable 선언 순서를 따른다.
// 모든 메서드는 이 인스턴스를 만든 UIA 전용 스레드에서만 호출해야 한다.
sealed unsafe class UiAutomationClient(IntPtr automation, IntPtr trueCondition, IntPtr cacheRequest) : IDisposable
{
    public const int ButtonControlTypeId = 50000;

    const int BoundingRectanglePropertyId = 30001;
    const int ControlTypePropertyId = 30003;
    const int ClassNamePropertyId = 30012;
    const int TreeScopeChildren = 2;

    // IUnknown
    const int QueryInterfaceSlot = 0;
    const int ReleaseSlot = 2;

    // IUIAutomation / IUIAutomation2
    const int ElementFromHandleBuildCacheSlot = 10;
    const int CreateCacheRequestSlot = 20;
    const int CreateTrueConditionSlot = 21;
    const int PutConnectionTimeoutSlot = 61;
    const int PutTransactionTimeoutSlot = 63;

    // IUIAutomationCacheRequest
    const int AddPropertySlot = 3;
    const int PutTreeFilterSlot = 9;

    // IUIAutomationElement
    const int FindAllBuildCacheSlot = 8;
    const int GetCachedControlTypeSlot = 53;
    const int GetCachedClassNameSlot = 62;
    const int GetCachedBoundingRectangleSlot = 75;

    // IUIAutomationElementArray
    const int GetLengthSlot = 3;
    const int GetElementSlot = 4;

    static readonly Guid s_automation8ClassId = new("e22ad333-b25f-460c-83d0-0581107395c9");
    static readonly Guid s_automationInterfaceId = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    static readonly Guid s_automation2InterfaceId = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");

    // ── 생성 ─────────────────────────────────────────────────────────────────
    // 캐시 요청에 클래스 이름 / 컨트롤 형식 / 사각형을 등록해 요소 조회 1회에 필요한 속성을 함께 가져온다
    public static UiAutomationClient? TryCreate(uint connectionTimeoutMilliseconds, uint transactionTimeoutMilliseconds, out int hresult)
    {
        var automationPointer = IntPtr.Zero;
        var trueConditionPointer = IntPtr.Zero;
        var cacheRequestPointer = IntPtr.Zero;

        hresult = Win32.CoCreateInstance(in s_automation8ClassId, IntPtr.Zero, Win32.CLSCTX_INPROC_SERVER, in s_automationInterfaceId, out automationPointer);
        if (hresult >= 0) hresult = CallWithOutPointer(automationPointer, CreateTrueConditionSlot, &trueConditionPointer);
        if (hresult >= 0) hresult = CallWithOutPointer(automationPointer, CreateCacheRequestSlot, &cacheRequestPointer);
        if (hresult >= 0) hresult = CallWithInt32(cacheRequestPointer, AddPropertySlot, ClassNamePropertyId);
        if (hresult >= 0) hresult = CallWithInt32(cacheRequestPointer, AddPropertySlot, ControlTypePropertyId);
        if (hresult >= 0) hresult = CallWithInt32(cacheRequestPointer, AddPropertySlot, BoundingRectanglePropertyId);
        // 기본 TreeFilter(컨트롤 뷰) 대신 원시 뷰 기준으로 자식을 찾는다
        if (hresult >= 0) hresult = CallWithPointer(cacheRequestPointer, PutTreeFilterSlot, trueConditionPointer);

        if (hresult < 0)
        {
            Release(cacheRequestPointer);
            Release(trueConditionPointer);
            Release(automationPointer);
            return null;
        }

        ApplyTimeouts(automationPointer, connectionTimeoutMilliseconds, transactionTimeoutMilliseconds);
        return new UiAutomationClient(automationPointer, trueConditionPointer, cacheRequestPointer);
    }

    // 메신저가 응답하지 않을 때 UIA 호출이 기본값(최대 20초)만큼 묶이지 않도록 제한 시간을 줄인다
    static void ApplyTimeouts(IntPtr automationPointer, uint connectionTimeoutMilliseconds, uint transactionTimeoutMilliseconds)
    {
        var interfaceId = s_automation2InterfaceId;
        var automation2Pointer = IntPtr.Zero;
        var hresult = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetMethod(automationPointer, QueryInterfaceSlot))(automationPointer, &interfaceId, &automation2Pointer);
        if (hresult < 0 || automation2Pointer == IntPtr.Zero) return;

        _ = CallWithUInt32(automation2Pointer, PutConnectionTimeoutSlot, connectionTimeoutMilliseconds);
        _ = CallWithUInt32(automation2Pointer, PutTransactionTimeoutSlot, transactionTimeoutMilliseconds);
        Release(automation2Pointer);
    }

    // ── 요소 조회 ────────────────────────────────────────────────────────────
    public UiAutomationElement? ElementFromHandle(IntPtr hwnd)
    {
        var elementPointer = IntPtr.Zero;
        var hresult = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)GetMethod(automation, ElementFromHandleBuildCacheSlot))(automation, hwnd, cacheRequest, &elementPointer);
        return hresult >= 0 && elementPointer != IntPtr.Zero ? CreateElement(elementPointer) : null;
    }

    public List<UiAutomationElement> FindChildren(UiAutomationElement parent)
    {
        List<UiAutomationElement> children = [];
        var elementArrayPointer = IntPtr.Zero;
        var hresult = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, IntPtr*, int>)GetMethod(parent.Pointer, FindAllBuildCacheSlot))(parent.Pointer, TreeScopeChildren, trueCondition, cacheRequest, &elementArrayPointer);
        if (hresult < 0 || elementArrayPointer == IntPtr.Zero) return children;

        try
        {
            var length = 0;
            hresult = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)GetMethod(elementArrayPointer, GetLengthSlot))(elementArrayPointer, &length);
            if (hresult < 0) return children;

            for (var index = 0; index < length; index++)
            {
                var elementPointer = IntPtr.Zero;
                hresult = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)GetMethod(elementArrayPointer, GetElementSlot))(elementArrayPointer, index, &elementPointer);
                if (hresult >= 0 && elementPointer != IntPtr.Zero) children.Add(CreateElement(elementPointer));
            }
        }
        finally { Release(elementArrayPointer); }

        return children;
    }

    static UiAutomationElement CreateElement(IntPtr elementPointer)
    {
        var controlType = 0;
        var classNamePointer = IntPtr.Zero;
        Win32.RECT boundingRectangle = default;
        _ = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)GetMethod(elementPointer, GetCachedControlTypeSlot))(elementPointer, &controlType);
        _ = CallWithOutPointer(elementPointer, GetCachedClassNameSlot, &classNamePointer);
        _ = ((delegate* unmanaged[Stdcall]<IntPtr, Win32.RECT*, int>)GetMethod(elementPointer, GetCachedBoundingRectangleSlot))(elementPointer, &boundingRectangle);

        var className = "";
        if (classNamePointer != IntPtr.Zero)
        {
            className = Marshal.PtrToStringBSTR(classNamePointer);
            Marshal.FreeBSTR(classNamePointer);
        }

        return new UiAutomationElement(elementPointer, className, controlType, boundingRectangle);
    }

    // ── vtable 호출 헬퍼 ─────────────────────────────────────────────────────
    static void* GetMethod(IntPtr instance, int slot) => (*(void***)instance)[slot];

    static int CallWithOutPointer(IntPtr instance, int slot, IntPtr* result) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetMethod(instance, slot))(instance, result);

    static int CallWithPointer(IntPtr instance, int slot, IntPtr value) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)GetMethod(instance, slot))(instance, value);

    static int CallWithInt32(IntPtr instance, int slot, int value) => ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)GetMethod(instance, slot))(instance, value);

    static int CallWithUInt32(IntPtr instance, int slot, uint value) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetMethod(instance, slot))(instance, value);

    public static void Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        _ = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)GetMethod(instance, ReleaseSlot))(instance);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        Release(cacheRequest);
        Release(trueCondition);
        Release(automation);
        cacheRequest = IntPtr.Zero;
        trueCondition = IntPtr.Zero;
        automation = IntPtr.Zero;
    }
}
