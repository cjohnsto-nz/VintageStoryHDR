using OpenTK.Graphics.OpenGL;
using VintageStoryHDR.Rendering;
using ErrorCode = OpenTK.Graphics.OpenGL.ErrorCode;

namespace VintageStoryHDR.Interop;

internal static class GlErrors
{
    /// <summary>
    /// Clears GL errors raised before this mod's call, so a following glGetError reports only
    /// that call. Reading an error clears it, so say so: otherwise the game would have reported it.
    /// </summary>
    internal static void DrainPending()
    {
        ErrorCode pending;
        while ((pending = GL.GetError()) != ErrorCode.NoError)
        {
            HdrRuntime.Log?.VerboseDebug("GL error {0} was already pending when this mod's hook ran; it was raised by the game or another mod.", pending);
        }
    }
}
