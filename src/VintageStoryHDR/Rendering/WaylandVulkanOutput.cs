using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageStoryHDR.Interop;
using ErrorCode = OpenTK.Graphics.OpenGL.ErrorCode;
using GlfwPlatform = OpenTK.Windowing.GraphicsLibraryFramework.Platform;

namespace VintageStoryHDR.Rendering;

/// <summary>
/// Linux output: an HDR10 Vulkan swapchain on a Wayland subsurface covering the game window.
///
/// <para>
/// EGL on Wayland cannot ask for an HDR surface, but a Vulkan swapchain can, and the
/// compositor honours its colour space through the Wayland colour-management protocol. GL
/// renders the encoded frame into a texture whose memory Vulkan allocated
/// (<c>GL_EXT_memory_object_fd</c>); a pair of shared semaphores (<c>GL_EXT_semaphore_fd</c>)
/// hands it back and forth, and Vulkan blits it into the swapchain image and presents.
/// </para>
///
/// <para>
/// Per frame: <see cref="BeginWrite"/> makes GL wait for Vulkan's last read of the texture,
/// <see cref="EndWrite"/> signals that GL has written it, and <see cref="Present"/> waits for
/// that signal on the GPU, blits, signals Vulkan's read done and presents. Every signal is
/// waited exactly once, so the binary semaphores always alternate.
/// </para>
/// </summary>
internal sealed unsafe class WaylandVulkanOutput : IHdrOutput
{
    private const uint ApiVersion11 = (1u << 22) | (1u << 12);

    private const int FormatA2B10G10R10 = 64; // matches GL_RGB10_A2
    private const int FormatA2R10G10B10 = 58;
    private const int ColorSpaceHdr10 = 1000104008;

    private const int PresentModeImmediate = 0;
    private const int PresentModeMailbox = 1;
    private const int PresentModeFifo = 2;

    private const int ResultTimeout = 2;
    private const int ResultNotReady = 1;
    private const int ResultSuboptimal = 1000001003;
    private const int ResultOutOfDate = -1000001004;

    private const int LayoutUndefined = 0;
    private const int LayoutTransferSrc = 6;
    private const int LayoutTransferDst = 7;
    private const int LayoutPresentSrc = 1000001002;

    private const uint AccessTransferRead = 0x800;
    private const uint AccessTransferWrite = 0x1000;
    private const uint StageTransfer = 0x1000;
    private const uint StageBottomOfPipe = 0x2000;
    private const uint QueueFamilyIgnored = uint.MaxValue;
    private const uint QueueFamilyExternal = ~1u;
    private const uint HandleTypeOpaqueFd = 1;

    /// <summary>How long a present may wait for a swapchain image before the frame is skipped (minimised or hidden window).</summary>
    private const ulong AcquireTimeoutNs = 100_000_000;

    internal const string SdrDisplayMessage =
        "The compositor reports this display as SDR (its peak does not exceed SDR white). Turn on HDR for the display in the system settings, then type .hdr on.";

    /// <summary>Reference white of PQ content when the image description does not set one (color-management-v1).</summary>
    private const float PqReferenceWhiteNits = 203f;

    private static readonly string[] RequiredInstanceExtensions = { "VK_KHR_surface", "VK_KHR_wayland_surface", "VK_EXT_swapchain_colorspace" };
    private static readonly string[] RequiredDeviceExtensions = { "VK_KHR_swapchain", "VK_KHR_external_memory_fd", "VK_KHR_external_semaphore_fd" };

    private readonly NativeWindow window;
    private readonly List<nint> strings = new();
    private GlExternalObjects gl = null!;
    private WaylandSubsurface? overlay;
    private VSyncMode previousVSync;
    private bool vsyncChanged;

    private nint instance;
    private nint physicalDevice;
    private nint device;
    private nint queue;
    private uint queueFamily;
    private ulong surface;
    private int swapchainFormat;
    private bool mailboxSupported;
    private bool hdrMetadataSupported;

    private ulong swapchain;
    private ulong[] swapchainImages = Array.Empty<ulong>();
    private ulong[] presentSemaphores = Array.Empty<ulong>();
    private int presentMode = -1;
    private bool swapchainStale;
    private bool parentCommitNeeded;
    private float metadataPeak = -1f;

    private ulong commandPool;
    private nint commandBuffer;
    private ulong fence;
    private ulong acquireSemaphore;

    private ulong sharedImage;
    private ulong sharedMemory;
    private uint glMemoryObject;
    private ulong glDoneVk;
    private ulong vkDoneVk;
    private uint glDoneGl;
    private uint vkDoneGl;
    private bool frameWritten;
    private bool vkDonePending;

    private int width;
    private int height;

    // Device-level entry points used every frame, resolved once.
    private delegate* unmanaged<nint, uint, ulong*, uint, ulong, int> waitForFences;
    private delegate* unmanaged<nint, uint, ulong*, int> resetFences;
    private delegate* unmanaged<nint, ulong, ulong, ulong, ulong, uint*, int> acquireNextImage;
    private delegate* unmanaged<nint, uint, int> resetCommandBuffer;
    private delegate* unmanaged<nint, VkCommandBufferBeginInfo*, int> beginCommandBuffer;
    private delegate* unmanaged<nint, int> endCommandBuffer;
    private delegate* unmanaged<nint, uint, uint, uint, uint, void*, uint, void*, uint, VkImageMemoryBarrier*, void> cmdPipelineBarrier;
    private delegate* unmanaged<nint, ulong, int, ulong, int, uint, VkImageBlit*, int, void> cmdBlitImage;
    private delegate* unmanaged<nint, uint, VkSubmitInfo*, ulong, int> queueSubmit;
    private delegate* unmanaged<nint, VkPresentInfoKHR*, int> queuePresent;
    private delegate* unmanaged<nint, int> deviceWaitIdle;
    private delegate* unmanaged<nint, byte*, nint> getDeviceProcAddr;

    private WaylandVulkanOutput(NativeWindow window)
    {
        this.window = window;
    }

    public int Texture { get; private set; }

    public DisplayInfo Display { get; private set; }

    public bool TearingSupported { get; private set; }

    public bool EncodesPq => true;

    /// <summary>
    /// The HDR10 image description the Vulkan driver gives the subsurface carries the PQ
    /// default reference white, 203 cd/m², and the compositor maps that to the display's SDR
    /// white. Scaling by 203 / SDR white makes the encoded nits the displayed nits.
    /// </summary>
    public float ContentScale { get; private set; } = 1f;

    /// <summary>Whether the game is running on GLFW's Wayland backend, the only Linux setup this output supports.</summary>
    internal static bool GameIsOnWayland()
    {
        try
        {
            return GLFW.GetPlatform() == GlfwPlatform.Wayland;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static WaylandVulkanOutput Create(NativeWindow window, int width, int height)
    {
        WaylandVulkanOutput output = new(window);
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

    private void Initialise(int frameWidth, int frameHeight)
    {
        gl = new GlExternalObjects();
        Guid glDevice = gl.DeviceUuidOfContext();

        Window* handle = window.WindowPtr;
        nint display = GLFW.GetWaylandDisplay();
        nint parentSurface = GLFW.GetWaylandWindow(handle);
        if (display == 0 || parentSurface == 0)
        {
            throw new HdrUnavailableException("The game window is not a Wayland surface.");
        }

        int scale = BufferScale(frameWidth, frameHeight);
        overlay = WaylandSubsurface.Create(display, parentSurface, frameWidth / scale, frameHeight / scale, scale);

        CreateInstance();
        PickPhysicalDevice(glDevice);

        VkWaylandSurfaceCreateInfoKHR surfaceInfo = new() { SType = 1000006000, Display = display, Surface = overlay.Surface };
        ulong created;
        Check(((delegate* unmanaged<nint, VkWaylandSurfaceCreateInfoKHR*, void*, ulong*, int>)InstanceProc("vkCreateWaylandSurfaceKHR"u8))(instance, &surfaceInfo, null, &created), "vkCreateWaylandSurfaceKHR");
        surface = created;

        PickSurfaceFormat();
        PickPresentModes();
        PickQueueFamily();
        CreateDevice();
        CreateFrameObjects();
        CreateSharedTexture(frameWidth, frameHeight);
        CreateSwapchain(PresentModeFifo);

        UpdateDisplay();

        // From now on the game's own surface is committed only to show or resize the
        // subsurface, and nothing may wait on it.
        previousVSync = window.VSync;
        window.VSync = VSyncMode.Off;
        vsyncChanged = true;
        parentCommitNeeded = true;
    }

    public void WithTexture(Action action) => action();

    /// <summary>
    /// Takes the display's luminances from the compositor's colour-management feedback. A
    /// compositor without it leaves them at 0, and the presenter falls back to the configured
    /// or a default peak.
    /// </summary>
    private void UpdateDisplay()
    {
        DisplayLuminance? luminance = overlay?.Luminance;
        if (luminance is { MaxNits: > 0f } l)
        {
            // An SDR output is described with its peak at SDR white: no headroom above it.
            bool hdr = l.ReferenceNits <= 0f || l.MaxNits > l.ReferenceNits * 1.05f;
            Display = new DisplayInfo(hdr, l.MinNits, l.MaxNits, l.MaxFrameAverageNits, l.ReferenceNits);
            ContentScale = l.ReferenceNits > 0f ? PqReferenceWhiteNits / l.ReferenceNits : 1f;
            HdrRuntime.Log?.Notification(
                "Compositor reports the display ({4}) at {0:0} nits peak, {1:0} nits SDR white, {2:0.####} nits black; HDR10 output scaled by {3:0.###} to match.",
                l.MaxNits,
                l.ReferenceNits,
                l.MinNits,
                l.ReferenceNits > 0f ? PqReferenceWhiteNits / l.ReferenceNits : 1f,
                hdr ? "HDR" : "SDR, no headroom above SDR white");
        }
        else
        {
            Display = new DisplayInfo(HdrEnabled: true, MinNits: 0f, MaxNits: 0f, MaxFullFrameNits: 0f);
            ContentScale = 1f;
        }
    }

    public void BeginWrite()
    {
        if (vkDonePending)
        {
            uint texture = (uint)Texture;
            uint layout = GlExternalObjects.LayoutTransferSrc;
            gl.WaitSemaphore(vkDoneGl, 0, null, 1, &texture, &layout);
            vkDonePending = false;
        }
    }

    public void EndWrite()
    {
        uint texture = (uint)Texture;
        uint layout = GlExternalObjects.LayoutTransferSrc;
        gl.SignalSemaphore(glDoneGl, 0, null, 1, &texture, &layout);
        GL.Flush();
        frameWritten = true;
    }

    public void Present(bool vsync, float peakNits)
    {
        if (overlay!.Pump())
        {
            UpdateDisplay();
            if (!Display.HdrEnabled && !HdrRuntime.Config.ForceOnSdrDisplay)
            {
                // The window moved to an SDR display, or its HDR was switched off.
                throw new HdrUnavailableException(SdrDisplayMessage);
            }
        }

        if (!frameWritten)
        {
            return;
        }

        frameWritten = false;

        int wanted = vsync ? PresentModeFifo : TearingSupported ? PresentModeImmediate : mailboxSupported ? PresentModeMailbox : PresentModeFifo;
        if (wanted != presentMode || swapchainStale)
        {
            // GL's signal for this frame is still unconsumed; the wait-idle in here is on Vulkan only.
            CreateSwapchain(wanted);
        }

        float contentPeak = peakNits * ContentScale;
        if (hdrMetadataSupported && contentPeak != metadataPeak)
        {
            SetHdrMetadata(contentPeak);
        }

        ulong currentFence = fence;
        Check(waitForFences(device, 1, &currentFence, 1, ulong.MaxValue), "vkWaitForFences");

        uint index = 0;
        int result = acquireNextImage(device, swapchain, AcquireTimeoutNs, acquireSemaphore, 0, &index);
        bool acquired = result is 0 or ResultSuboptimal;
        if (result is ResultSuboptimal or ResultOutOfDate)
        {
            swapchainStale = true;
        }
        else if (!acquired && result is not (ResultTimeout or ResultNotReady))
        {
            throw new HdrUnavailableException($"vkAcquireNextImageKHR failed ({result}).");
        }

        Check(resetFences(device, 1, &currentFence), "vkResetFences");
        Record(acquired ? swapchainImages[index] : 0);

        ulong* waits = stackalloc ulong[2] { glDoneVk, acquireSemaphore };
        uint* waitStages = stackalloc uint[2] { StageTransfer, StageTransfer };
        ulong* signals = stackalloc ulong[2] { vkDoneVk, acquired ? presentSemaphores[index] : 0 };
        nint buffer = commandBuffer;
        VkSubmitInfo submit = new()
        {
            SType = 4,
            WaitSemaphoreCount = acquired ? 2u : 1u,
            PWaitSemaphores = (nint)waits,
            PWaitDstStageMask = (nint)waitStages,
            CommandBufferCount = 1,
            PCommandBuffers = (nint)(&buffer),
            SignalSemaphoreCount = acquired ? 2u : 1u,
            PSignalSemaphores = (nint)signals,
        };
        Check(queueSubmit(queue, 1, &submit, fence), "vkQueueSubmit");
        vkDonePending = true;

        if (!acquired)
        {
            return;
        }

        ulong chain = swapchain;
        VkPresentInfoKHR present = new()
        {
            SType = 1000001001,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = (nint)(signals + 1),
            SwapchainCount = 1,
            PSwapchains = (nint)(&chain),
            PImageIndices = (nint)(&index),
        };
        result = queuePresent(queue, &present);
        if (result is ResultSuboptimal or ResultOutOfDate)
        {
            swapchainStale = true;
        }
        else if (result < 0)
        {
            throw new HdrUnavailableException($"vkQueuePresentKHR failed ({result}).");
        }

        if (parentCommitNeeded)
        {
            // A new subsurface, and a resized window, only take effect when the game's own
            // surface commits, and its EGL swap is the only thing that commits it. Its content
            // is hidden under the opaque subsurface.
            window.Context.SwapBuffers();
            parentCommitNeeded = false;
        }
    }

    public void Resize(int frameWidth, int frameHeight)
    {
        DrainGl();
        _ = deviceWaitIdle(device);

        DestroySharedTexture();
        CreateSharedTexture(frameWidth, frameHeight);
        CreateSwapchain(presentMode);

        int scale = BufferScale(frameWidth, frameHeight);
        overlay!.Resize(frameWidth / scale, frameHeight / scale, scale);
        parentCommitNeeded = true;
    }

    public void Dispose()
    {
        bool haveContext = GlContext.IsCurrent;
        if (haveContext)
        {
            DrainGl();
        }

        if (device != 0)
        {
            _ = ((delegate* unmanaged<nint, int>)DeviceProc("vkDeviceWaitIdle"u8))(device);
        }

        if (haveContext)
        {
            DestroySharedTexture();
            DeleteGlSemaphore(ref glDoneGl);
            DeleteGlSemaphore(ref vkDoneGl);
            // Put vsync back only if it is still what this output set; if the game changed it
            // meanwhile (its own vsync setting), that newer choice stands.
            if (vsyncChanged && window.VSync == VSyncMode.Off)
            {
                window.VSync = previousVSync;
            }

            vsyncChanged = false;
        }

        if (device != 0)
        {
            DestroySwapchain(swapchain);
            swapchain = 0;
            DestroySharedTexture();
            var destroySemaphore = (delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkDestroySemaphore"u8);
            foreach (ulong semaphore in new[] { acquireSemaphore, glDoneVk, vkDoneVk })
            {
                if (semaphore != 0)
                {
                    destroySemaphore(device, semaphore, null);
                }
            }

            acquireSemaphore = glDoneVk = vkDoneVk = 0;
            if (fence != 0)
            {
                ((delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkDestroyFence"u8))(device, fence, null);
                fence = 0;
            }

            if (commandPool != 0)
            {
                ((delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkDestroyCommandPool"u8))(device, commandPool, null);
                commandPool = 0;
            }

            ((delegate* unmanaged<nint, void*, void>)DeviceProc("vkDestroyDevice"u8))(device, null);
            device = 0;
        }

        if (instance != 0)
        {
            if (surface != 0)
            {
                ((delegate* unmanaged<nint, ulong, void*, void>)InstanceProc("vkDestroySurfaceKHR"u8))(instance, surface, null);
                surface = 0;
            }

            ((delegate* unmanaged<nint, void*, void>)InstanceProc("vkDestroyInstance"u8))(instance, null);
            instance = 0;
        }

        overlay?.Dispose();
        overlay = null;

        foreach (nint s in strings)
        {
            Marshal.FreeCoTaskMem(s);
        }

        strings.Clear();
    }

    /// <summary>Waits on the GPU for Vulkan's last read, then for GL to finish, so shared objects can be replaced.</summary>
    private void DrainGl()
    {
        if (vkDonePending)
        {
            gl.WaitSemaphore(vkDoneGl, 0, null, 0, null, null);
            vkDonePending = false;
        }

        GL.Finish();
    }

    private int BufferScale(int frameWidth, int frameHeight)
    {
        GLFW.GetWindowSize(window.WindowPtr, out int logicalWidth, out int logicalHeight);
        if (logicalWidth <= 0 || logicalHeight <= 0 || (logicalWidth == frameWidth && logicalHeight == frameHeight))
        {
            return 1;
        }

        int scale = frameWidth / logicalWidth;
        if (scale >= 1 && frameWidth == logicalWidth * scale && frameHeight == logicalHeight * scale)
        {
            return scale;
        }

        throw new HdrUnavailableException(
            $"Fractional display scaling is not supported yet (window {logicalWidth}x{logicalHeight}, framebuffer {frameWidth}x{frameHeight}).");
    }

    private void CreateInstance()
    {
        HashSet<string> available = EnumerateInstanceExtensions();
        foreach (string name in RequiredInstanceExtensions)
        {
            if (!available.Contains(name))
            {
                throw new HdrUnavailableException($"The Vulkan loader does not offer {name}.");
            }
        }

        nint* names = stackalloc nint[RequiredInstanceExtensions.Length];
        for (int i = 0; i < RequiredInstanceExtensions.Length; i++)
        {
            names[i] = Utf8(RequiredInstanceExtensions[i]);
        }

        VkApplicationInfo app = new() { PApplicationName = Utf8("VintageStoryHDR"), ApiVersion = ApiVersion11 };
        VkInstanceCreateInfo info = new()
        {
            SType = 1,
            PApplicationInfo = (nint)(&app),
            EnabledExtensionCount = (uint)RequiredInstanceExtensions.Length,
            PpEnabledExtensionNames = (nint)names,
        };
        nint created;
        Check(((delegate* unmanaged<VkInstanceCreateInfo*, void*, nint*, int>)InstanceProc("vkCreateInstance"u8))(&info, null, &created), "vkCreateInstance");
        instance = created;
    }

    private void PickPhysicalDevice(Guid glDevice)
    {
        var enumerate = (delegate* unmanaged<nint, uint*, nint*, int>)InstanceProc("vkEnumeratePhysicalDevices"u8);
        var properties2 = (delegate* unmanaged<nint, byte*, void>)InstanceProc("vkGetPhysicalDeviceProperties2"u8);
        uint count = 0;
        Check(enumerate(instance, &count, null), "vkEnumeratePhysicalDevices");
        nint[] devices = new nint[count];
        fixed (nint* p = devices)
        {
            Check(enumerate(instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        byte* properties = stackalloc byte[1024];
        byte* ids = stackalloc byte[64];
        foreach (nint candidate in devices)
        {
            new Span<byte>(properties, 1024).Clear();
            new Span<byte>(ids, 64).Clear();
            *(int*)ids = 1000071004;        // VkPhysicalDeviceIDProperties
            *(int*)properties = 1000059001; // VkPhysicalDeviceProperties2
            *(nint*)(properties + 8) = (nint)ids;
            properties2(candidate, properties);
            if (new Guid(new ReadOnlySpan<byte>(ids + 16, 16)) == glDevice)
            {
                physicalDevice = candidate;
                return;
            }
        }

        throw new HdrUnavailableException(
            "Vulkan has no device matching the GPU OpenGL runs on. On hybrid graphics, run the game on one GPU (e.g. prime-run).");
    }

    private void PickSurfaceFormat()
    {
        var getFormats = (delegate* unmanaged<nint, ulong, uint*, VkSurfaceFormatKHR*, int>)InstanceProc("vkGetPhysicalDeviceSurfaceFormatsKHR"u8);
        uint count = 0;
        Check(getFormats(physicalDevice, surface, &count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        VkSurfaceFormatKHR[] formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* p = formats)
        {
            Check(getFormats(physicalDevice, surface, &count, p), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        }

        foreach (int wanted in new[] { FormatA2B10G10R10, FormatA2R10G10B10 })
        {
            if (Array.Exists(formats, f => f.Format == wanted && f.ColorSpace == ColorSpaceHdr10))
            {
                swapchainFormat = wanted;
                return;
            }
        }

        throw new HdrUnavailableException(
            "The compositor does not offer HDR10 for the game window. Turn on HDR for this display in the system settings, then type .hdr on.");
    }

    private void PickPresentModes()
    {
        var getModes = (delegate* unmanaged<nint, ulong, uint*, int*, int>)InstanceProc("vkGetPhysicalDeviceSurfacePresentModesKHR"u8);
        uint count = 0;
        Check(getModes(physicalDevice, surface, &count, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        int[] modes = new int[count];
        fixed (int* p = modes)
        {
            Check(getModes(physicalDevice, surface, &count, p), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        }

        TearingSupported = Array.IndexOf(modes, PresentModeImmediate) >= 0;
        mailboxSupported = Array.IndexOf(modes, PresentModeMailbox) >= 0;
    }

    private void PickQueueFamily()
    {
        var getFamilies = (delegate* unmanaged<nint, uint*, byte*, void>)InstanceProc("vkGetPhysicalDeviceQueueFamilyProperties"u8);
        var getSupport = (delegate* unmanaged<nint, uint, ulong, uint*, int>)InstanceProc("vkGetPhysicalDeviceSurfaceSupportKHR"u8);
        uint count = 0;
        getFamilies(physicalDevice, &count, null);
        byte[] families = new byte[24 * (int)count]; // sizeof(VkQueueFamilyProperties)
        fixed (byte* p = families)
        {
            getFamilies(physicalDevice, &count, p);
            for (uint i = 0; i < count; i++)
            {
                uint flags = *(uint*)(p + (24 * i));
                uint supported = 0;
                _ = getSupport(physicalDevice, i, surface, &supported);
                if ((flags & 1) != 0 && supported != 0)
                {
                    queueFamily = i;
                    return;
                }
            }
        }

        throw new HdrUnavailableException("No Vulkan graphics queue can present to the game window.");
    }

    private void CreateDevice()
    {
        var enumerate = (delegate* unmanaged<nint, byte*, uint*, byte*, int>)InstanceProc("vkEnumerateDeviceExtensionProperties"u8);
        uint count = 0;
        Check(enumerate(physicalDevice, null, &count, null), "vkEnumerateDeviceExtensionProperties");
        byte[] buffer = new byte[260 * (int)count]; // sizeof(VkExtensionProperties)
        HashSet<string> available = new(StringComparer.Ordinal);
        fixed (byte* p = buffer)
        {
            Check(enumerate(physicalDevice, null, &count, p), "vkEnumerateDeviceExtensionProperties");
            for (int i = 0; i < count; i++)
            {
                available.Add(Marshal.PtrToStringUTF8((nint)(p + (i * 260))) ?? string.Empty);
            }
        }

        List<string> enable = new();
        foreach (string name in RequiredDeviceExtensions)
        {
            if (!available.Contains(name))
            {
                throw new HdrUnavailableException($"The Vulkan driver does not offer {name}.");
            }

            enable.Add(name);
        }

        hdrMetadataSupported = available.Contains("VK_EXT_hdr_metadata");
        if (hdrMetadataSupported)
        {
            enable.Add("VK_EXT_hdr_metadata");
        }

        nint* names = stackalloc nint[enable.Count];
        for (int i = 0; i < enable.Count; i++)
        {
            names[i] = Utf8(enable[i]);
        }

        float priority = 1f;
        VkDeviceQueueCreateInfo queueInfo = new() { SType = 2, QueueFamilyIndex = queueFamily, QueueCount = 1, PQueuePriorities = (nint)(&priority) };
        VkDeviceCreateInfo info = new()
        {
            SType = 3,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = (nint)(&queueInfo),
            EnabledExtensionCount = (uint)enable.Count,
            PpEnabledExtensionNames = (nint)names,
        };
        nint created;
        Check(((delegate* unmanaged<nint, VkDeviceCreateInfo*, void*, nint*, int>)InstanceProc("vkCreateDevice"u8))(physicalDevice, &info, null, &created), "vkCreateDevice");
        device = created;

        nint createdQueue;
        ((delegate* unmanaged<nint, uint, uint, nint*, void>)DeviceProc("vkGetDeviceQueue"u8))(device, queueFamily, 0, &createdQueue);
        queue = createdQueue;

        waitForFences = (delegate* unmanaged<nint, uint, ulong*, uint, ulong, int>)DeviceProc("vkWaitForFences"u8);
        resetFences = (delegate* unmanaged<nint, uint, ulong*, int>)DeviceProc("vkResetFences"u8);
        acquireNextImage = (delegate* unmanaged<nint, ulong, ulong, ulong, ulong, uint*, int>)DeviceProc("vkAcquireNextImageKHR"u8);
        resetCommandBuffer = (delegate* unmanaged<nint, uint, int>)DeviceProc("vkResetCommandBuffer"u8);
        beginCommandBuffer = (delegate* unmanaged<nint, VkCommandBufferBeginInfo*, int>)DeviceProc("vkBeginCommandBuffer"u8);
        endCommandBuffer = (delegate* unmanaged<nint, int>)DeviceProc("vkEndCommandBuffer"u8);
        cmdPipelineBarrier = (delegate* unmanaged<nint, uint, uint, uint, uint, void*, uint, void*, uint, VkImageMemoryBarrier*, void>)DeviceProc("vkCmdPipelineBarrier"u8);
        cmdBlitImage = (delegate* unmanaged<nint, ulong, int, ulong, int, uint, VkImageBlit*, int, void>)DeviceProc("vkCmdBlitImage"u8);
        queueSubmit = (delegate* unmanaged<nint, uint, VkSubmitInfo*, ulong, int>)DeviceProc("vkQueueSubmit"u8);
        queuePresent = (delegate* unmanaged<nint, VkPresentInfoKHR*, int>)DeviceProc("vkQueuePresentKHR"u8);
        deviceWaitIdle = (delegate* unmanaged<nint, int>)DeviceProc("vkDeviceWaitIdle"u8);
    }

    private void CreateFrameObjects()
    {
        VkCommandPoolCreateInfo poolInfo = new() { SType = 39, Flags = 0x2, QueueFamilyIndex = queueFamily }; // RESET_COMMAND_BUFFER
        ulong pool;
        Check(((delegate* unmanaged<nint, VkCommandPoolCreateInfo*, void*, ulong*, int>)DeviceProc("vkCreateCommandPool"u8))(device, &poolInfo, null, &pool), "vkCreateCommandPool");
        commandPool = pool;

        VkCommandBufferAllocateInfo allocateInfo = new() { SType = 40, CommandPool = commandPool, Level = 0, CommandBufferCount = 1 };
        nint buffer;
        Check(((delegate* unmanaged<nint, VkCommandBufferAllocateInfo*, nint*, int>)DeviceProc("vkAllocateCommandBuffers"u8))(device, &allocateInfo, &buffer), "vkAllocateCommandBuffers");
        commandBuffer = buffer;

        VkFlagsCreateInfo fenceInfo = new() { SType = 8, Flags = 1 }; // signalled
        ulong createdFence;
        Check(((delegate* unmanaged<nint, VkFlagsCreateInfo*, void*, ulong*, int>)DeviceProc("vkCreateFence"u8))(device, &fenceInfo, null, &createdFence), "vkCreateFence");
        fence = createdFence;

        acquireSemaphore = CreateSemaphore(exportable: false);

        glDoneVk = CreateSemaphore(exportable: true);
        glDoneGl = ImportSemaphore(glDoneVk);
        vkDoneVk = CreateSemaphore(exportable: true);
        vkDoneGl = ImportSemaphore(vkDoneVk);
    }

    private ulong CreateSemaphore(bool exportable)
    {
        VkHandleTypesInfo export = new() { SType = 1000077000, HandleTypes = HandleTypeOpaqueFd }; // VkExportSemaphoreCreateInfo
        VkFlagsCreateInfo info = new() { SType = 9, PNext = exportable ? (nint)(&export) : 0 };
        ulong semaphore;
        Check(((delegate* unmanaged<nint, VkFlagsCreateInfo*, void*, ulong*, int>)DeviceProc("vkCreateSemaphore"u8))(device, &info, null, &semaphore), "vkCreateSemaphore");
        return semaphore;
    }

    private uint ImportSemaphore(ulong semaphore)
    {
        VkGetFdInfo info = new() { SType = 1000079001, Handle = semaphore, HandleType = HandleTypeOpaqueFd }; // VkSemaphoreGetFdInfoKHR
        int fd;
        Check(((delegate* unmanaged<nint, VkGetFdInfo*, int*, int>)DeviceProc("vkGetSemaphoreFdKHR"u8))(device, &info, &fd), "vkGetSemaphoreFdKHR");

        // A successful import hands the descriptor to GL.
        uint name;
        gl.GenSemaphores(1, &name);
        gl.ImportSemaphoreFd(name, GlExternalObjects.HandleTypeOpaqueFd, fd);
        return name;
    }

    private void CreateSharedTexture(int frameWidth, int frameHeight)
    {
        VkHandleTypesInfo external = new() { SType = 1000072001, HandleTypes = HandleTypeOpaqueFd }; // VkExternalMemoryImageCreateInfo
        VkImageCreateInfo imageInfo = new()
        {
            SType = 14,
            PNext = (nint)(&external),
            ImageType = 1, // 2D
            Format = FormatA2B10G10R10,
            Width = (uint)frameWidth,
            Height = (uint)frameHeight,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = 1,
            Tiling = 0,    // optimal
            Usage = 0x11,  // TRANSFER_SRC | COLOR_ATTACHMENT
            InitialLayout = LayoutUndefined,
        };
        ulong image;
        Check(((delegate* unmanaged<nint, VkImageCreateInfo*, void*, ulong*, int>)DeviceProc("vkCreateImage"u8))(device, &imageInfo, null, &image), "vkCreateImage");
        sharedImage = image;

        VkMemoryRequirements requirements;
        ((delegate* unmanaged<nint, ulong, VkMemoryRequirements*, void>)DeviceProc("vkGetImageMemoryRequirements"u8))(device, sharedImage, &requirements);

        VkMemoryDedicatedAllocateInfo dedicated = new() { SType = 1000127001, Image = sharedImage };
        VkHandleTypesInfo export = new() { SType = 1000072002, PNext = (nint)(&dedicated), HandleTypes = HandleTypeOpaqueFd }; // VkExportMemoryAllocateInfo
        VkMemoryAllocateInfo allocateInfo = new()
        {
            SType = 5,
            PNext = (nint)(&export),
            AllocationSize = requirements.Size,
            MemoryTypeIndex = DeviceLocalMemoryType(requirements.MemoryTypeBits),
        };
        ulong memory;
        Check(((delegate* unmanaged<nint, VkMemoryAllocateInfo*, void*, ulong*, int>)DeviceProc("vkAllocateMemory"u8))(device, &allocateInfo, null, &memory), "vkAllocateMemory");
        sharedMemory = memory;
        Check(((delegate* unmanaged<nint, ulong, ulong, ulong, int>)DeviceProc("vkBindImageMemory"u8))(device, sharedImage, sharedMemory, 0), "vkBindImageMemory");

        VkGetFdInfo fdInfo = new() { SType = 1000074002, Handle = sharedMemory, HandleType = HandleTypeOpaqueFd }; // VkMemoryGetFdInfoKHR
        int fd;
        Check(((delegate* unmanaged<nint, VkGetFdInfo*, int*, int>)DeviceProc("vkGetMemoryFdKHR"u8))(device, &fdInfo, &fd), "vkGetMemoryFdKHR");

        GlErrors.DrainPending();
        uint memoryObject;
        gl.CreateMemoryObjects(1, &memoryObject);
        glMemoryObject = memoryObject;
        int dedicatedFlag = 1;
        gl.MemoryObjectParameteriv(glMemoryObject, GlExternalObjects.DedicatedMemoryObject, &dedicatedFlag);
        gl.ImportMemoryFd(glMemoryObject, requirements.Size, GlExternalObjects.HandleTypeOpaqueFd, fd);

        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        Texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, Texture);
        GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)GlExternalObjects.TextureTiling, (int)GlExternalObjects.OptimalTiling);
        gl.TexStorageMem2D((uint)TextureTarget.Texture2D, 1, GlExternalObjects.Rgb10A2, frameWidth, frameHeight, glMemoryObject, 0);
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);

        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw new HdrUnavailableException($"Importing the Vulkan image into OpenGL failed ({error}).");
        }

        width = frameWidth;
        height = frameHeight;
    }

    private uint DeviceLocalMemoryType(uint allowedTypes)
    {
        byte* properties = stackalloc byte[520]; // sizeof(VkPhysicalDeviceMemoryProperties)
        ((delegate* unmanaged<nint, byte*, void>)InstanceProc("vkGetPhysicalDeviceMemoryProperties"u8))(physicalDevice, properties);
        uint typeCount = *(uint*)properties;
        for (uint i = 0; i < typeCount; i++)
        {
            uint flags = *(uint*)(properties + 4 + (8 * i)); // VkMemoryType.propertyFlags
            if ((allowedTypes & (1u << (int)i)) != 0 && (flags & 1) != 0)
            {
                return i;
            }
        }

        throw new HdrUnavailableException("No device-local Vulkan memory type can hold the shared image.");
    }

    private void DestroySharedTexture()
    {
        if (GlContext.IsCurrent)
        {
            if (Texture != 0)
            {
                GL.DeleteTexture(Texture);
                Texture = 0;
            }

            if (glMemoryObject != 0)
            {
                uint memoryObject = glMemoryObject;
                gl.DeleteMemoryObjects(1, &memoryObject);
                glMemoryObject = 0;
            }
        }

        if (device != 0)
        {
            if (sharedImage != 0)
            {
                ((delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkDestroyImage"u8))(device, sharedImage, null);
                sharedImage = 0;
            }

            if (sharedMemory != 0)
            {
                ((delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkFreeMemory"u8))(device, sharedMemory, null);
                sharedMemory = 0;
            }
        }
    }

    private void DeleteGlSemaphore(ref uint name)
    {
        if (name != 0)
        {
            uint semaphore = name;
            gl.DeleteSemaphores(1, &semaphore);
            name = 0;
        }
    }

    private void CreateSwapchain(int mode)
    {
        _ = deviceWaitIdle(device);

        VkSurfaceCapabilitiesKHR caps;
        Check(((delegate* unmanaged<nint, ulong, VkSurfaceCapabilitiesKHR*, int>)InstanceProc("vkGetPhysicalDeviceSurfaceCapabilitiesKHR"u8))(physicalDevice, surface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        if ((caps.SupportedUsageFlags & 0x2) == 0)
        {
            throw new HdrUnavailableException("Swapchain images cannot be blit targets on this driver.");
        }

        uint imageCount = Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, caps.MaxImageCount);
        }

        ulong old = swapchain;
        VkSwapchainCreateInfoKHR info = new()
        {
            SType = 1000001000,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = swapchainFormat,
            ImageColorSpace = ColorSpaceHdr10,
            ImageWidth = (uint)width,
            ImageHeight = (uint)height,
            ImageArrayLayers = 1,
            ImageUsage = 0x2, // TRANSFER_DST
            PreTransform = 1, // IDENTITY
            CompositeAlpha = (caps.SupportedCompositeAlpha & 1) != 0 ? 1u : caps.SupportedCompositeAlpha & (uint)-(int)caps.SupportedCompositeAlpha,
            PresentMode = mode,
            Clipped = 1,
            OldSwapchain = old,
        };
        ulong created;
        Check(((delegate* unmanaged<nint, VkSwapchainCreateInfoKHR*, void*, ulong*, int>)DeviceProc("vkCreateSwapchainKHR"u8))(device, &info, null, &created), "vkCreateSwapchainKHR");
        DestroySwapchain(old);
        swapchain = created;
        presentMode = mode;
        swapchainStale = false;
        metadataPeak = -1f;

        var getImages = (delegate* unmanaged<nint, ulong, uint*, ulong*, int>)DeviceProc("vkGetSwapchainImagesKHR"u8);
        uint count = 0;
        Check(getImages(device, swapchain, &count, null), "vkGetSwapchainImagesKHR");
        swapchainImages = new ulong[count];
        fixed (ulong* p = swapchainImages)
        {
            Check(getImages(device, swapchain, &count, p), "vkGetSwapchainImagesKHR");
        }

        presentSemaphores = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            presentSemaphores[i] = CreateSemaphore(exportable: false);
        }
    }

    /// <summary>Destroys <paramref name="old"/> and the per-image semaphores that belong to it. The device must be idle.</summary>
    private void DestroySwapchain(ulong old)
    {
        var destroySemaphore = (delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkDestroySemaphore"u8);
        foreach (ulong semaphore in presentSemaphores)
        {
            destroySemaphore(device, semaphore, null);
        }

        presentSemaphores = Array.Empty<ulong>();
        swapchainImages = Array.Empty<ulong>();
        if (old != 0)
        {
            ((delegate* unmanaged<nint, ulong, void*, void>)DeviceProc("vkDestroySwapchainKHR"u8))(device, old, null);
        }
    }

    private void SetHdrMetadata(float peakNits)
    {
        // Mastering display: P3 primaries, D65 -- the widest the gamut stretch reaches.
        VkHdrMetadataEXT metadata = new()
        {
            SType = 1000105000,
            RedX = 0.680f,
            RedY = 0.320f,
            GreenX = 0.265f,
            GreenY = 0.690f,
            BlueX = 0.150f,
            BlueY = 0.060f,
            WhiteX = 0.3127f,
            WhiteY = 0.3290f,
            MaxLuminance = peakNits,
            MinLuminance = 0.005f,
            MaxContentLightLevel = peakNits,
            MaxFrameAverageLightLevel = peakNits * 0.5f,
        };
        ulong chain = swapchain;
        ((delegate* unmanaged<nint, uint, ulong*, VkHdrMetadataEXT*, void>)DeviceProc("vkSetHdrMetadataEXT"u8))(device, 1, &chain, &metadata);
        metadataPeak = peakNits;
    }

    /// <summary>Records this frame's blit, or with no swapchain image only the shared image's ownership round trip.</summary>
    private void Record(ulong target)
    {
        Check(resetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
        VkCommandBufferBeginInfo begin = new() { SType = 42, Flags = 1 }; // ONE_TIME_SUBMIT
        Check(beginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer");

        // GL left the shared image in TRANSFER_SRC; take it from the external (GL) queue.
        VkImageMemoryBarrier* before = stackalloc VkImageMemoryBarrier[2];
        before[0] = Barrier(sharedImage, 0, AccessTransferRead, LayoutTransferSrc, LayoutTransferSrc, QueueFamilyExternal, queueFamily);
        before[1] = Barrier(target, 0, AccessTransferWrite, LayoutUndefined, LayoutTransferDst, QueueFamilyIgnored, QueueFamilyIgnored);
        cmdPipelineBarrier(commandBuffer, StageTransfer, StageTransfer, 0, 0, null, 0, null, target != 0 ? 2u : 1u, before);

        if (target != 0)
        {
            VkImageBlit region = new()
            {
                SrcSubresource = new VkImageSubresourceLayers { AspectMask = 1, LayerCount = 1 },
                SrcX1 = width,
                SrcY1 = height,
                SrcZ1 = 1,
                DstSubresource = new VkImageSubresourceLayers { AspectMask = 1, LayerCount = 1 },
                DstX1 = width,
                DstY1 = height,
                DstZ1 = 1,
            };
            cmdBlitImage(commandBuffer, sharedImage, LayoutTransferSrc, target, LayoutTransferDst, 1, &region, 0); // nearest
        }

        // Hand the shared image back to GL, and the swapchain image to the presentation engine.
        VkImageMemoryBarrier* after = stackalloc VkImageMemoryBarrier[2];
        after[0] = Barrier(sharedImage, AccessTransferRead, 0, LayoutTransferSrc, LayoutTransferSrc, queueFamily, QueueFamilyExternal);
        after[1] = Barrier(target, AccessTransferWrite, 0, LayoutTransferDst, LayoutPresentSrc, QueueFamilyIgnored, QueueFamilyIgnored);
        cmdPipelineBarrier(commandBuffer, StageTransfer, StageBottomOfPipe, 0, 0, null, 0, null, target != 0 ? 2u : 1u, after);

        Check(endCommandBuffer(commandBuffer), "vkEndCommandBuffer");
    }

    private static VkImageMemoryBarrier Barrier(ulong image, uint srcAccess, uint dstAccess, int oldLayout, int newLayout, uint srcFamily, uint dstFamily) => new()
    {
        SType = 45,
        SrcAccessMask = srcAccess,
        DstAccessMask = dstAccess,
        OldLayout = oldLayout,
        NewLayout = newLayout,
        SrcQueueFamilyIndex = srcFamily,
        DstQueueFamilyIndex = dstFamily,
        Image = image,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = 1, LevelCount = 1, LayerCount = 1 },
    };

    private static HashSet<string> EnumerateInstanceExtensions()
    {
        uint count = 0;
        Check(VulkanNative.vkEnumerateInstanceExtensionProperties(null, &count, null), "vkEnumerateInstanceExtensionProperties");
        byte[] buffer = new byte[260 * (int)count];
        HashSet<string> names = new(StringComparer.Ordinal);
        fixed (byte* p = buffer)
        {
            Check(VulkanNative.vkEnumerateInstanceExtensionProperties(null, &count, p), "vkEnumerateInstanceExtensionProperties");
            for (int i = 0; i < count; i++)
            {
                names.Add(Marshal.PtrToStringUTF8((nint)(p + (i * 260))) ?? string.Empty);
            }
        }

        return names;
    }

    private nint InstanceProc(ReadOnlySpan<byte> name)
    {
        nint address;
        fixed (byte* p = name)
        {
            address = VulkanNative.vkGetInstanceProcAddr(instance, p);
        }

        return address != 0 ? address : throw new HdrUnavailableException($"Vulkan does not export {System.Text.Encoding.UTF8.GetString(name)}.");
    }

    private nint DeviceProc(ReadOnlySpan<byte> name)
    {
        if (getDeviceProcAddr == null)
        {
            getDeviceProcAddr = (delegate* unmanaged<nint, byte*, nint>)InstanceProc("vkGetDeviceProcAddr"u8);
        }

        nint address;
        fixed (byte* p = name)
        {
            address = getDeviceProcAddr(device, p);
        }

        return address != 0 ? address : throw new HdrUnavailableException($"Vulkan does not export {System.Text.Encoding.UTF8.GetString(name)}.");
    }

    private nint Utf8(string value)
    {
        nint p = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(p);
        return p;
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new HdrUnavailableException($"{what} failed ({result}).");
        }
    }
}
