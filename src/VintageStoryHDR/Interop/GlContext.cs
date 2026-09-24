using System;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageStoryHDR.Interop;

internal static unsafe class GlContext
{
    /// <summary>Whether a GL context is current on this thread, so GL objects can be deleted.</summary>
    internal static bool IsCurrent =>
        OperatingSystem.IsWindows()
            ? NativeMethods.WglGetCurrentContext() != 0
            : GLFW.GetCurrentContext() != null;
}
