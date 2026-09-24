using System;
using VintageStoryHDR.Interop;

namespace VintageStoryHDR.Rendering;

/// <summary>
/// Where <see cref="HdrPresenter"/> sends a finished frame: a GL texture it renders the
/// encoded frame into, and whatever puts that texture on screen. Windows presents through a
/// DXGI swapchain (<see cref="DxgiOutput"/>), Linux through a Vulkan swapchain on a Wayland
/// subsurface (<see cref="WaylandVulkanOutput"/>).
///
/// Every method is called on the render thread with the game's GL context current.
/// </summary>
internal interface IHdrOutput : IDisposable
{
    /// <summary>The GL texture a frame is rendered into. Changes after <see cref="Resize"/>.</summary>
    int Texture { get; }

    DisplayInfo Display { get; }

    bool TearingSupported { get; }

    /// <summary>
    /// True when <see cref="Texture"/> holds PQ-encoded Rec.2020 (HDR10, 10 bits); false when
    /// it holds linear scRGB (1.0 = 80 nits, 16-bit float).
    /// </summary>
    bool EncodesPq { get; }

    /// <summary>
    /// What the compositor's processing multiplies encoded luminance by, inverted: the
    /// presenter scales what it writes by this so the display shows the nits it meant. 1 where
    /// the signal is shown as encoded.
    /// </summary>
    float ContentScale { get; }

    /// <summary>Runs <paramref name="action"/> while GL may attach or otherwise use <see cref="Texture"/>, outside of a frame.</summary>
    void WithTexture(Action action);

    /// <summary>Hands <see cref="Texture"/> to GL for this frame's write.</summary>
    void BeginWrite();

    /// <summary>Hands <see cref="Texture"/> back once GL has written the frame.</summary>
    void EndWrite();

    /// <summary>Puts the last written frame on screen.</summary>
    void Present(bool vsync, float peakNits);

    void Resize(int width, int height);
}
