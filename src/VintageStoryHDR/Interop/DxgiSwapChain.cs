using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VintageStoryHDR.Interop;

/// <summary>What DXGI reports about the display the swapchain's window is on.</summary>
/// <param name="SdrWhiteNits">Where the system puts SDR white, or 0 if it does not say.</param>
internal readonly record struct DisplayInfo(bool HdrEnabled, float MinNits, float MaxNits, float MaxFullFrameNits, float SdrWhiteNits = 0f);

/// <summary>
/// A D3D11 device, an scRGB flip-model swapchain on a window, and one RGBA16F texture of
/// the same size for OpenGL to render into.
///
/// <para>
/// OpenGL on Windows has no portable way to ask for an HDR surface, but DXGI does, and
/// <c>WGL_NV_DX_interop2</c> lets GL render straight into a D3D11 texture. So the frame is
/// drawn by GL into <see cref="SharedTexture"/>, copied to the back buffer on the GPU, and
/// presented by DXGI. scRGB (linear, Rec.709 primaries, 1.0 = 80 nits) is the one HDR
/// format the compositor accepts without any metadata.
/// </para>
///
/// <para>
/// The COM interfaces are called through their vtables directly. The slot numbers below
/// are fixed by the published headers, and tests/DxgiSwapChainTests exercises every one
/// of them against the real runtime.
/// </para>
/// </summary>
internal sealed unsafe class DxgiSwapChain : IDisposable
{
    private const uint FormatR16G16B16A16Float = 10;
    private const uint ColorSpaceScRgb = 1;      // DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
    private const uint ColorSpaceHdr10 = 12;     // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
    private const uint SwapChainFlagAllowTearing = 2048;
    private const uint PresentAllowTearing = 0x200;
    private const int BufferCount = 2;

    private static readonly Guid IidFactory5 = new("7632e1f5-ee65-4dca-87fd-84cd75f8838d");
    private static readonly Guid IidSwapChain3 = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");
    private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private void* factory;
    private void* device;
    private void* context;
    private void* swapChain;
    private void* backBuffer;
    private void* sharedTexture;
    private uint swapChainFlags;

    private DxgiSwapChain()
    {
    }

    /// <summary>The ID3D11Device, for <c>wglDXOpenDeviceNV</c>.</summary>
    internal nint Device => (nint)device;

    /// <summary>The ID3D11Texture2D that GL renders into. Replaced by <see cref="Resize"/>.</summary>
    internal nint SharedTexture => (nint)sharedTexture;

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    /// <summary>Whether unsynchronised presents may tear, which is what makes vsync-off actually uncapped.</summary>
    internal bool TearingSupported { get; private set; }

    internal static DxgiSwapChain Create(nint hwnd, int width, int height)
    {
        DxgiSwapChain result = new();
        try
        {
            result.Initialise(hwnd, width, height);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void Initialise(nint hwnd, int width, int height)
    {
        Guid iid = IidFactory5;
        void* created;
        Check(NativeMethods.CreateDXGIFactory2(0, &iid, &created), "CreateDXGIFactory2");
        factory = created;

        int allowTearing = 0;
        // IDXGIFactory5::CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING)
        int hr = ((delegate* unmanaged[Stdcall]<void*, uint, void*, uint, int>)Slot(factory, 28))(factory, 0, &allowTearing, sizeof(int));
        TearingSupported = hr >= 0 && allowTearing != 0;
        swapChainFlags = TearingSupported ? SwapChainFlagAllowTearing : 0;

        uint featureLevel = 0xb000; // 11_0
        void* createdDevice;
        void* createdContext;
        const uint DriverTypeHardware = 1;
        const uint CreateDeviceBgraSupport = 0x20;
        const uint SdkVersion = 7;
        Check(
            NativeMethods.D3D11CreateDevice(null, DriverTypeHardware, 0, CreateDeviceBgraSupport, &featureLevel, 1, SdkVersion, &createdDevice, null, &createdContext),
            "D3D11CreateDevice");
        device = createdDevice;
        context = createdContext;

        SwapChainDesc1 desc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = FormatR16G16B16A16Float,
            SampleCount = 1,
            BufferUsage = 0x20,          // DXGI_USAGE_RENDER_TARGET_OUTPUT
            BufferCount = BufferCount,
            Scaling = 0,                 // DXGI_SCALING_STRETCH: the window is resized a frame before the buffers are
            SwapEffect = 4,              // DXGI_SWAP_EFFECT_FLIP_DISCARD
            AlphaMode = 3,               // DXGI_ALPHA_MODE_IGNORE
            Flags = swapChainFlags,
        };

        void* swapChain1;
        // IDXGIFactory2::CreateSwapChainForHwnd
        Check(
            ((delegate* unmanaged[Stdcall]<void*, void*, nint, SwapChainDesc1*, void*, void*, void**, int>)Slot(factory, 15))(
                factory, device, hwnd, &desc, null, null, &swapChain1),
            "IDXGIFactory2::CreateSwapChainForHwnd");

        iid = IidSwapChain3;
        void* swapChain3;
        hr = QueryInterface(swapChain1, &iid, &swapChain3);
        Release(swapChain1);
        Check(hr, "QueryInterface(IDXGISwapChain3)");
        swapChain = swapChain3;

        // IDXGIFactory::MakeWindowAssociation -- DXGI must not react to Alt+Enter or resize the game's window.
        const uint NoWindowChanges = 1;
        const uint NoAltEnter = 2;
        _ = ((delegate* unmanaged[Stdcall]<void*, nint, uint, int>)Slot(factory, 8))(factory, hwnd, NoWindowChanges | NoAltEnter);

        uint support = 0;
        // IDXGISwapChain3::CheckColorSpaceSupport
        Check(
            ((delegate* unmanaged[Stdcall]<void*, uint, uint*, int>)Slot(swapChain, 37))(swapChain, ColorSpaceScRgb, &support),
            "IDXGISwapChain3::CheckColorSpaceSupport");
        if ((support & 1) == 0)
        {
            throw new HdrUnavailableException("The swapchain does not support the scRGB colour space.");
        }

        // IDXGISwapChain3::SetColorSpace1
        Check(((delegate* unmanaged[Stdcall]<void*, uint, int>)Slot(swapChain, 38))(swapChain, ColorSpaceScRgb), "IDXGISwapChain3::SetColorSpace1");

        Width = width;
        Height = height;
        AcquireBuffers();
    }

    /// <summary>Asks DXGI about the output the window is currently on.</summary>
    internal DisplayInfo QueryDisplay()
    {
        void* output;
        // IDXGISwapChain::GetContainingOutput
        Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(swapChain, 15))(swapChain, &output), "IDXGISwapChain::GetContainingOutput");

        try
        {
            Guid iid = IidOutput6;
            void* output6;
            Check(QueryInterface(output, &iid, &output6), "QueryInterface(IDXGIOutput6)");
            try
            {
                OutputDesc1 desc;
                // IDXGIOutput6::GetDesc1
                Check(((delegate* unmanaged[Stdcall]<void*, OutputDesc1*, int>)Slot(output6, 27))(output6, &desc), "IDXGIOutput6::GetDesc1");
                return new DisplayInfo(desc.ColorSpace == ColorSpaceHdr10, desc.MinLuminance, desc.MaxLuminance, desc.MaxFullFrameLuminance);
            }
            finally
            {
                Release(output6);
            }
        }
        finally
        {
            Release(output);
        }
    }

    /// <summary>
    /// Resizes the back buffers and replaces <see cref="SharedTexture"/>. The caller must
    /// have unregistered the old shared texture from GL first.
    /// </summary>
    internal void Resize(int width, int height)
    {
        ReleaseBuffers();

        // IDXGISwapChain::ResizeBuffers
        Check(
            ((delegate* unmanaged[Stdcall]<void*, uint, uint, uint, uint, uint, int>)Slot(swapChain, 13))(
                swapChain, BufferCount, (uint)width, (uint)height, FormatR16G16B16A16Float, swapChainFlags),
            "IDXGISwapChain::ResizeBuffers");

        Width = width;
        Height = height;
        AcquireBuffers();
    }

    /// <summary>Copies the shared texture to the back buffer and presents it.</summary>
    internal void Present(bool vsync)
    {
        // ID3D11DeviceContext::CopyResource(dst, src)
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Slot(context, 47))(context, backBuffer, sharedTexture);

        uint interval = vsync ? 1u : 0u;
        uint flags = !vsync && TearingSupported ? PresentAllowTearing : 0;
        // IDXGISwapChain::Present
        int hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, int>)Slot(swapChain, 8))(swapChain, interval, flags);
        if (hr < 0)
        {
            Check(hr, "IDXGISwapChain::Present");
        }
    }

    private void AcquireBuffers()
    {
        Guid iid = IidTexture2D;
        void* buffer;
        // IDXGISwapChain::GetBuffer. With FLIP_DISCARD, D3D11 always exposes the current back buffer as index 0.
        Check(((delegate* unmanaged[Stdcall]<void*, uint, Guid*, void**, int>)Slot(swapChain, 9))(swapChain, 0, &iid, &buffer), "IDXGISwapChain::GetBuffer");
        backBuffer = buffer;

        Texture2DDesc desc = new()
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatR16G16B16A16Float,
            SampleCount = 1,
            BindFlags = 0x20 | 0x8, // RENDER_TARGET | SHADER_RESOURCE
        };

        void* texture;
        // ID3D11Device::CreateTexture2D
        Check(((delegate* unmanaged[Stdcall]<void*, Texture2DDesc*, void*, void**, int>)Slot(device, 5))(device, &desc, null, &texture), "ID3D11Device::CreateTexture2D");
        sharedTexture = texture;
    }

    private void ReleaseBuffers()
    {
        Release(sharedTexture);
        sharedTexture = null;
        Release(backBuffer);
        backBuffer = null;

        if (context != null)
        {
            // ID3D11DeviceContext::ClearState, then Flush: deferred destruction would otherwise keep the old back buffer alive.
            ((delegate* unmanaged[Stdcall]<void*, void>)Slot(context, 110))(context);
            ((delegate* unmanaged[Stdcall]<void*, void>)Slot(context, 111))(context);
        }
    }

    public void Dispose()
    {
        ReleaseBuffers();
        Release(swapChain);
        swapChain = null;
        Release(context);
        context = null;
        Release(device);
        device = null;
        Release(factory);
        factory = null;
    }

    private static void* Slot(void* comObject, int index) => (*(void***)comObject)[index];

    private static int QueryInterface(void* comObject, Guid* iid, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(comObject, 0))(comObject, iid, result);

    private static void Release(void* comObject)
    {
        if (comObject != null)
        {
            _ = ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(comObject, 2))(comObject);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new HdrUnavailableException(string.Format(CultureInfo.InvariantCulture, "{0} failed with HRESULT 0x{1:X8}.", what, hr));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDesc1
    {
        public uint Width;
        public uint Height;
        public uint Format;
        public int Stereo;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public uint Scaling;
        public uint SwapEffect;
        public uint AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public NativeMethods.Rect DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public uint ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }
}
