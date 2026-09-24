using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VintageStoryHDR.Interop;

/// <summary>
/// A Wayland subsurface covering the game window's surface, for a Vulkan swapchain to present
/// to. The game's own surface belongs to GLFW's EGL context, which rules it out as a Vulkan
/// target. The subsurface has an empty input region, so pointer input still reaches the game,
/// and is desynchronised, so it updates without the game's surface committing.
///
/// Requests go through the game's <c>wl_display</c>, but on this object's own event queue, so
/// nothing here is dispatched by GLFW's event loop or disturbs it. Only
/// <c>wl_proxy_marshal_array_flags</c> is used to send requests: the header helpers
/// (<c>wl_surface_commit</c> and friends) are inline functions libwayland does not export.
/// </summary>
internal sealed unsafe partial class WaylandSubsurface : IDisposable
{
    // Request opcodes, from wayland.xml.
    private const uint DisplayGetRegistry = 1;
    private const uint RegistryBind = 0;
    private const uint CompositorCreateSurface = 0;
    private const uint CompositorCreateRegion = 1;
    private const uint SurfaceDestroy = 0;
    private const uint SurfaceSetOpaqueRegion = 4;
    private const uint SurfaceSetInputRegion = 5;
    private const uint SurfaceSetBufferScale = 8;
    private const uint RegionDestroy = 0;
    private const uint RegionAdd = 1;
    private const uint SubcompositorDestroy = 0;
    private const uint SubcompositorGetSubsurface = 1;
    private const uint SubsurfaceDestroy = 0;
    private const uint SubsurfaceSetPosition = 1;
    private const uint SubsurfaceSetDesync = 5;

    private static uint compositorName;
    private static uint compositorVersion;
    private static uint subcompositorName;
    private static uint colorManagerName;

    private nint queue;
    private nint wrapper;
    private nint registry;
    private nint listener;
    private nint compositor;
    private nint subcompositor;
    private nint subsurface;
    private DisplayLuminanceFeedback? luminance;

    private WaylandSubsurface(nint display)
    {
        Display = display;
    }

    internal nint Display { get; }

    /// <summary>The subsurface's own <c>wl_surface</c>.</summary>
    internal nint Surface { get; private set; }

    /// <summary>
    /// Creates the subsurface over <paramref name="parent"/>. It is not shown until the parent
    /// surface next commits.
    /// </summary>
    internal static WaylandSubsurface Create(nint display, nint parent, int logicalWidth, int logicalHeight, int bufferScale)
    {
        WaylandSubsurface created = new(display);
        try
        {
            created.Initialise(parent, logicalWidth, logicalHeight, bufferScale);
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    private void Initialise(nint parent, int logicalWidth, int logicalHeight, int bufferScale)
    {
        queue = Created(Native.wl_display_create_queue(Display), "an event queue");
        wrapper = Created(Native.wl_proxy_create_wrapper(Display), "a display wrapper");
        Native.wl_proxy_set_queue(wrapper, queue);

        Arg* args = stackalloc Arg[4];
        args[0] = default;
        registry = Created(Native.wl_proxy_marshal_array_flags(wrapper, DisplayGetRegistry, Interface("wl_registry_interface"), Native.wl_proxy_get_version(wrapper), 0, args), "the registry");

        listener = (nint)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        ((nint*)listener)[0] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, uint, void>)&OnGlobal;
        ((nint*)listener)[1] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, void>)&OnGlobalRemove;
        compositorName = 0;
        subcompositorName = 0;
        colorManagerName = 0;
        _ = Native.wl_proxy_add_listener(registry, listener, 0);
        if (Native.wl_display_roundtrip_queue(Display, queue) < 0)
        {
            throw new HdrUnavailableException("The Wayland connection failed while listing compositor globals.");
        }

        // wl_surface.set_buffer_scale needs wl_compositor version 3.
        if (compositorName == 0 || compositorVersion < 3 || subcompositorName == 0)
        {
            throw new HdrUnavailableException("The compositor does not offer wl_compositor (version 3 or later) and wl_subcompositor.");
        }

        compositor = Bind(compositorName, "wl_compositor", Math.Min(compositorVersion, 4u));
        subcompositor = Bind(subcompositorName, "wl_subcompositor", 1);

        args[0] = default;
        Surface = Created(Native.wl_proxy_marshal_array_flags(compositor, CompositorCreateSurface, Interface("wl_surface_interface"), Native.wl_proxy_get_version(compositor), 0, args), "a surface");

        // No input region: pointer input falls through to the game's surface underneath.
        nint emptyRegion = CreateRegion(0, 0);
        args[0] = new Arg { Object = emptyRegion };
        _ = Native.wl_proxy_marshal_array_flags(Surface, SurfaceSetInputRegion, 0, Native.wl_proxy_get_version(Surface), 0, args);
        DestroyRegion(emptyRegion);

        args[0] = default;
        args[1] = new Arg { Object = Surface };
        args[2] = new Arg { Object = parent };
        subsurface = Created(Native.wl_proxy_marshal_array_flags(subcompositor, SubcompositorGetSubsurface, Interface("wl_subsurface_interface"), 1, 0, args), "a subsurface");
        args[0] = new Arg { Int = 0 };
        args[1] = new Arg { Int = 0 };
        _ = Native.wl_proxy_marshal_array_flags(subsurface, SubsurfaceSetPosition, 0, 1, 0, args);
        _ = Native.wl_proxy_marshal_array_flags(subsurface, SubsurfaceSetDesync, 0, 1, 0, null);

        Resize(logicalWidth, logicalHeight, bufferScale);

        if (colorManagerName != 0)
        {
            try
            {
                luminance = new DisplayLuminanceFeedback(Display, queue, registry, colorManagerName, parent);
            }
            catch (HdrUnavailableException)
            {
                // The display's luminances are a refinement; without them the presenter
                // falls back to the configured or default figures.
                luminance = null;
            }
        }
    }

    /// <summary>
    /// What the compositor says about the display the game window is on, or null if it does not
    /// implement the colour-management protocol or has not answered yet.
    /// </summary>
    internal DisplayLuminance? Luminance => luminance?.Current;

    /// <summary>Processes compositor events. Call once per frame. Returns true when <see cref="Luminance"/> changed.</summary>
    internal bool Pump()
    {
        if (Native.wl_display_dispatch_queue_pending(Display, queue) < 0)
        {
            throw new HdrUnavailableException("The Wayland connection failed.");
        }

        return luminance?.Pump() ?? false;
    }

    /// <summary>Opaque region and buffer scale for a new size. Both apply with the swapchain's next present.</summary>
    internal void Resize(int logicalWidth, int logicalHeight, int bufferScale)
    {
        Arg* args = stackalloc Arg[1];
        nint opaque = CreateRegion(logicalWidth, logicalHeight);
        args[0] = new Arg { Object = opaque };
        _ = Native.wl_proxy_marshal_array_flags(Surface, SurfaceSetOpaqueRegion, 0, Native.wl_proxy_get_version(Surface), 0, args);
        DestroyRegion(opaque);

        args[0] = new Arg { Int = bufferScale };
        _ = Native.wl_proxy_marshal_array_flags(Surface, SurfaceSetBufferScale, 0, Native.wl_proxy_get_version(Surface), 0, args);
        _ = Native.wl_display_flush(Display);
    }

    public void Dispose()
    {
        luminance?.Dispose();
        luminance = null;
        Destroy(ref subsurface, SubsurfaceDestroy);
        nint surface = Surface;
        Destroy(ref surface, SurfaceDestroy);
        Surface = 0;
        Destroy(ref subcompositor, SubcompositorDestroy);

        // wl_compositor and wl_registry have no destroy request before version 6 / at all.
        DestroyProxy(ref compositor);
        DestroyProxy(ref registry);

        if (wrapper != 0)
        {
            Native.wl_proxy_wrapper_destroy(wrapper);
            wrapper = 0;
        }

        if (queue != 0)
        {
            _ = Native.wl_display_flush(Display);
            _ = Native.wl_display_dispatch_queue_pending(Display, queue);
            Native.wl_event_queue_destroy(queue);
            queue = 0;
        }

        if (listener != 0)
        {
            NativeMemory.Free((void*)listener);
            listener = 0;
        }
    }

    private nint Bind(uint name, string interfaceName, uint version) =>
        BindGlobal(registry, name, interfaceName, Interface(interfaceName + "_interface"), version);

    private nint CreateRegion(int width, int height)
    {
        Arg* args = stackalloc Arg[4];
        args[0] = default;
        nint region = Created(Native.wl_proxy_marshal_array_flags(compositor, CompositorCreateRegion, Interface("wl_region_interface"), Native.wl_proxy_get_version(compositor), 0, args), "a region");
        if (width > 0 && height > 0)
        {
            args[0] = new Arg { Int = 0 };
            args[1] = new Arg { Int = 0 };
            args[2] = new Arg { Int = width };
            args[3] = new Arg { Int = height };
            _ = Native.wl_proxy_marshal_array_flags(region, RegionAdd, 0, Native.wl_proxy_get_version(region), 0, args);
        }

        return region;
    }

    private static void DestroyRegion(nint region) =>
        _ = Native.wl_proxy_marshal_array_flags(region, RegionDestroy, 0, Native.wl_proxy_get_version(region), Native.MarshalFlagDestroy, null);

    private static void Destroy(ref nint proxy, uint destroyOpcode)
    {
        if (proxy != 0)
        {
            _ = Native.wl_proxy_marshal_array_flags(proxy, destroyOpcode, 0, Native.wl_proxy_get_version(proxy), Native.MarshalFlagDestroy, null);
            proxy = 0;
        }
    }

    private static void DestroyProxy(ref nint proxy)
    {
        if (proxy != 0)
        {
            Native.wl_proxy_destroy(proxy);
            proxy = 0;
        }
    }

    private static readonly Lazy<nint> WaylandLibrary = new(() => NativeLibrary.Load(Native.Library));

    private static nint Interface(string symbol) => NativeLibrary.GetExport(WaylandLibrary.Value, symbol);

    /// <summary>
    /// A proxy a request just created. libwayland returns null when it cannot create one
    /// (out of memory, or the connection already failed); passing that on would crash inside
    /// libwayland instead of falling back to vanilla presentation.
    /// </summary>
    internal static nint Created(nint proxy, string what) =>
        proxy != 0 ? proxy : throw new HdrUnavailableException($"The Wayland connection could not create {what}.");

    /// <summary>wl_registry.bind of global <paramref name="name"/>, typed by <paramref name="interfacePointer"/>.</summary>
    internal static nint BindGlobal(nint registry, uint name, string interfaceName, nint interfacePointer, uint version)
    {
        nint utf8Name = Marshal.StringToCoTaskMemUTF8(interfaceName);
        try
        {
            Arg* args = stackalloc Arg[4];
            args[0] = new Arg { Uint = name };
            args[1] = new Arg { Object = utf8Name };
            args[2] = new Arg { Uint = version };
            args[3] = default;
            return Created(Native.wl_proxy_marshal_array_flags(registry, RegistryBind, interfacePointer, version, 0, args), interfaceName);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Name);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGlobal(nint data, nint registry, uint name, byte* interfaceName, uint version)
    {
        string? name8 = Marshal.PtrToStringUTF8((nint)interfaceName);
        if (name8 == "wl_compositor")
        {
            compositorName = name;
            compositorVersion = version;
        }
        else if (name8 == "wl_subcompositor")
        {
            subcompositorName = name;
        }
        else if (name8 == "wp_color_manager_v1")
        {
            colorManagerName = name;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGlobalRemove(nint data, nint registry, uint name)
    {
    }

    /// <summary><c>union wl_argument</c>.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct Arg
    {
        [FieldOffset(0)]
        public int Int;

        [FieldOffset(0)]
        public uint Uint;

        [FieldOffset(0)]
        public nint Object;
    }

    internal static partial class Native
    {
        internal const string Library = "libwayland-client.so.0";
        internal const uint MarshalFlagDestroy = 1;

        [LibraryImport(Library)]
        internal static partial nint wl_display_create_queue(nint display);

        [LibraryImport(Library)]
        internal static partial nint wl_proxy_create_wrapper(nint proxy);

        [LibraryImport(Library)]
        internal static partial void wl_proxy_wrapper_destroy(nint wrapper);

        [LibraryImport(Library)]
        internal static partial void wl_proxy_set_queue(nint proxy, nint queue);

        [LibraryImport(Library)]
        internal static partial nint wl_proxy_marshal_array_flags(nint proxy, uint opcode, nint iface, uint version, uint flags, Arg* args);

        [LibraryImport(Library)]
        internal static partial int wl_proxy_add_listener(nint proxy, nint implementation, nint data);

        [LibraryImport(Library)]
        internal static partial uint wl_proxy_get_version(nint proxy);

        [LibraryImport(Library)]
        internal static partial void wl_proxy_destroy(nint proxy);

        [LibraryImport(Library)]
        internal static partial int wl_display_roundtrip_queue(nint display, nint queue);

        [LibraryImport(Library)]
        internal static partial int wl_display_dispatch_queue_pending(nint display, nint queue);

        [LibraryImport(Library)]
        internal static partial int wl_display_flush(nint display);

        [LibraryImport(Library)]
        internal static partial void wl_event_queue_destroy(nint queue);

        [LibraryImport(Library)]
        internal static partial int wl_proxy_add_dispatcher(nint proxy, nint dispatcher, nint dispatcherData, nint data);

        [LibraryImport("libc.so.6")]
        internal static partial int close(int fd);
    }
}
