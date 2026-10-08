using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace YellowInsideLib;

// ──── OLE IDropSource ────────────────────────────────────────────────────────
[GeneratedComInterface]
[Guid("00000121-0000-0000-C000-000000000046")]
partial interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);

    [PreserveSig]
    int GiveFeedback(uint effect);
}
