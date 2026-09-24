using System;
using OpenTK.Graphics.OpenGL;
using VintageStoryHDR.Interop;

namespace VintageStoryHDR.Rendering;

/// <summary>
/// Windows output: a flip-model scRGB DXGI swapchain on a disabled child window covering the
/// game window's client area, with its shared D3D11 texture used by GL through
/// <c>WGL_NV_DX_interop2</c>. The game window itself has already been presented to by GL,
/// which rules it out as a flip-model target; the child is disabled, so all input still
/// reaches the game.
/// </summary>
internal sealed class DxgiOutput : IHdrOutput
{
    private readonly nint parentWindow;
    private nint childWindow;
    private DxgiSwapChain? swapChain;
    private WglDxInterop? interop;
    private nint sharedHandle;

    private DxgiOutput(nint parentWindow)
    {
        this.parentWindow = parentWindow;
    }

    public int Texture { get; private set; }

    public DisplayInfo Display { get; private set; }

    public bool TearingSupported => swapChain?.TearingSupported ?? false;

    public bool EncodesPq => false;

    public float ContentScale => 1f;

    internal static DxgiOutput Create(nint parentWindow, int width, int height)
    {
        DxgiOutput output = new(parentWindow);
        try
        {
            output.Initialise(width, height);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void Initialise(int width, int height)
    {
        childWindow = NativeMethods.CreateWindowEx(
            NativeMethods.WsExNoParentNotify,
            "STATIC",
            string.Empty,
            NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.WsDisabled,
            0,
            0,
            width,
            height,
            parentWindow,
            0,
            0,
            0);
        if (childWindow == 0)
        {
            throw new HdrUnavailableException("Could not create the presentation window.");
        }

        swapChain = DxgiSwapChain.Create(childWindow, width, height);
        Display = swapChain.QueryDisplay();
        interop = new WglDxInterop(swapChain.Device);
        RegisterTexture();
    }

    public void WithTexture(Action action)
    {
        // The texture only has storage, as far as GL is concerned, while it is locked.
        interop!.Lock(sharedHandle);
        try
        {
            action();
        }
        finally
        {
            interop.Unlock(sharedHandle);
        }
    }

    public void BeginWrite() => interop!.Lock(sharedHandle);

    public void EndWrite() => interop!.Unlock(sharedHandle);

    public void Present(bool vsync, float peakNits) => swapChain!.Present(vsync);

    public void Resize(int width, int height)
    {
        // The interop registration pins the old D3D texture, and the swapchain cannot
        // resize while anything still references its buffers.
        interop!.Unregister(sharedHandle);
        sharedHandle = 0;
        GL.DeleteTexture(Texture);
        Texture = 0;

        _ = NativeMethods.SetWindowPos(childWindow, 0, 0, 0, width, height, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        swapChain!.Resize(width, height);
        RegisterTexture();
        Display = swapChain.QueryDisplay();
    }

    public void Dispose()
    {
        bool haveContext = GlContext.IsCurrent;

        if (interop is not null && haveContext)
        {
            interop.Unregister(sharedHandle);
            sharedHandle = 0;
            interop.Dispose();
        }

        interop = null;

        if (haveContext && Texture != 0)
        {
            GL.DeleteTexture(Texture);
            Texture = 0;
        }

        swapChain?.Dispose();
        swapChain = null;

        if (childWindow != 0)
        {
            _ = NativeMethods.DestroyWindow(childWindow);
            childWindow = 0;
        }
    }

    private void RegisterTexture()
    {
        Texture = GL.GenTexture();
        sharedHandle = interop!.RegisterTexture(swapChain!.SharedTexture, Texture);
    }
}
