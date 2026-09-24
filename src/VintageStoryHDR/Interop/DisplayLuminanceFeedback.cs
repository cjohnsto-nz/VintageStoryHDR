using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arg = VintageStoryHDR.Interop.WaylandSubsurface.Arg;
using Native = VintageStoryHDR.Interop.WaylandSubsurface.Native;

namespace VintageStoryHDR.Interop;

/// <summary>Luminance of the display a surface is on, as the compositor describes it.</summary>
/// <param name="MinNits">Darkest the display gets.</param>
/// <param name="MaxNits">Brightest the display gets (its peak).</param>
/// <param name="ReferenceNits">Where the compositor puts SDR white.</param>
/// <param name="MaxFrameAverageNits">Brightest full frame the display sustains, or 0 if not given.</param>
internal readonly record struct DisplayLuminance(float MinNits, float MaxNits, float ReferenceNits, float MaxFrameAverageNits);

/// <summary>
/// Asks the compositor, through <c>wp_color_management_v1</c>, which image description it
/// prefers for the game window's surface, and reads the luminances out of it: on KWin that is
/// the display's peak and SDR white, including any overrides set in the display settings.
/// Follows <c>preferred_changed</c>, so moving the window to another display, or changing the
/// display's HDR settings, updates the figures.
///
/// libwayland only ships the core protocol's interface tables, so this protocol's are built
/// here from color-management-v1.xml. Events arrive through one dispatcher rather than a
/// listener table per interface.
/// </summary>
internal sealed unsafe class DisplayLuminanceFeedback : IDisposable
{
    // Opcodes, from color-management-v1.xml. Bound at version 1.
    private const uint ManagerDestroy = 0;
    private const uint ManagerGetSurfaceFeedback = 3;
    private const uint FeedbackDestroy = 0;
    private const uint FeedbackGetPreferred = 1;
    private const uint DescriptionDestroy = 0;
    private const uint DescriptionGetInformation = 1;
    private const uint DescriptionEventFailed = 0;
    private const uint InfoEventDone = 0;
    private const uint InfoEventIccFile = 1;
    private const uint InfoEventLuminances = 6;
    private const uint InfoEventTargetLuminance = 8;
    private const uint InfoEventTargetMaxFall = 10;

    private readonly nint display;
    private GCHandle self;
    private nint manager;
    private nint feedback;
    private nint description;
    private nint info;

    private bool preferredChanged;
    private bool descriptionReady;
    private bool infoDone;

    private float minNits;
    private float maxNits;
    private float referenceNits;
    private float targetMinNits;
    private float targetMaxNits;
    private float maxFallNits;
    private bool haveTarget;

    internal DisplayLuminanceFeedback(nint display, nint queue, nint registry, uint managerName, nint surface)
    {
        this.display = display;
        self = GCHandle.Alloc(this);
        try
        {
            manager = WaylandSubsurface.BindGlobal(registry, managerName, "wp_color_manager_v1", Protocol.Manager, 1);
            Listen(manager);

            Arg* request = stackalloc Arg[2];
            request[0] = default;
            request[1] = new Arg { Object = surface };
            feedback = WaylandSubsurface.Created(Native.wl_proxy_marshal_array_flags(manager, ManagerGetSurfaceFeedback, Protocol.Feedback, 1, 0, request), "colour-management feedback");
            Listen(feedback);

            RequestPreferred();

            // Answer the first question before the first frame, so activation already knows the peak.
            for (int i = 0; i < 4 && Current is null && description + info != 0; i++)
            {
                if (Native.wl_display_roundtrip_queue(display, queue) < 0)
                {
                    break;
                }

                _ = Pump();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal DisplayLuminance? Current { get; private set; }

    /// <summary>Moves the exchange along after events were dispatched. Returns true when <see cref="Current"/> changed.</summary>
    internal bool Pump()
    {
        bool changed = false;

        // Publish a finished answer first: a description that became ready in the same
        // dispatch belongs to a newer question, and starting it would replace `info`.
        if (infoDone)
        {
            infoDone = false;
            DestroyInfo();
            DisplayLuminance next = new(
                haveTarget ? targetMinNits : minNits,
                haveTarget ? targetMaxNits : maxNits,
                referenceNits,
                maxFallNits);
            changed = Current != next;
            Current = next;
        }

        if (descriptionReady && description != 0)
        {
            descriptionReady = false;
            Arg* args = stackalloc Arg[1];
            args[0] = default;
            info = Native.wl_proxy_marshal_array_flags(description, DescriptionGetInformation, Protocol.Info, 1, 0, args);
            if (info != 0)
            {
                Listen(info);
            }

            DestroyDescription();
            _ = Native.wl_display_flush(display);
        }

        if (preferredChanged)
        {
            preferredChanged = false;
            RequestPreferred();
        }

        return changed;
    }

    public void Dispose()
    {
        DestroyInfo();
        DestroyDescription();
        if (feedback != 0)
        {
            _ = Native.wl_proxy_marshal_array_flags(feedback, FeedbackDestroy, 0, 1, Native.MarshalFlagDestroy, null);
            feedback = 0;
        }

        if (manager != 0)
        {
            _ = Native.wl_proxy_marshal_array_flags(manager, ManagerDestroy, 0, 1, Native.MarshalFlagDestroy, null);
            manager = 0;
        }

        if (self.IsAllocated)
        {
            self.Free();
        }
    }

    private void RequestPreferred()
    {
        // An answer still in flight describes the previous preference; its proxy is dropped
        // so its remaining events are discarded, and the figures start over.
        DestroyInfo();
        infoDone = false;
        DestroyDescription();
        minNits = maxNits = referenceNits = targetMinNits = targetMaxNits = maxFallNits = 0f;
        haveTarget = false;

        Arg* args = stackalloc Arg[1];
        args[0] = default;
        description = Native.wl_proxy_marshal_array_flags(feedback, FeedbackGetPreferred, Protocol.Description, 1, 0, args);
        if (description != 0)
        {
            Listen(description);
        }

        _ = Native.wl_display_flush(display);
    }

    private void DestroyDescription()
    {
        if (description != 0)
        {
            _ = Native.wl_proxy_marshal_array_flags(description, DescriptionDestroy, 0, 1, Native.MarshalFlagDestroy, null);
            description = 0;
        }

        descriptionReady = false;
    }

    private void DestroyInfo()
    {
        // wp_image_description_info_v1 has no requests; its done event is the destructor.
        if (info != 0)
        {
            Native.wl_proxy_destroy(info);
            info = 0;
        }
    }

    private void Listen(nint proxy) =>
        _ = Native.wl_proxy_add_dispatcher(proxy, (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, Arg*, int>)&Dispatch, GCHandle.ToIntPtr(self), 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Dispatch(nint dispatcherData, nint target, uint opcode, nint message, Arg* args)
    {
        try
        {
            if (GCHandle.FromIntPtr(dispatcherData).Target is DisplayLuminanceFeedback feedback)
            {
                feedback.OnEvent(target, opcode, args);
            }
        }
        catch (Exception)
        {
            // An exception must not unwind into libwayland; the figures just stay as they were.
        }

        return 0;
    }

    private void OnEvent(nint target, uint opcode, Arg* args)
    {
        if (target == feedback)
        {
            preferredChanged = true; // preferred_changed or preferred_changed2
        }
        else if (target == description)
        {
            if (opcode == DescriptionEventFailed)
            {
                DestroyDescription();
            }
            else
            {
                descriptionReady = true; // ready or ready2
            }
        }
        else if (target == info)
        {
            switch (opcode)
            {
                case InfoEventDone:
                    infoDone = true;
                    break;
                case InfoEventIccFile:
                    _ = Native.close(args[0].Int);
                    break;
                case InfoEventLuminances:
                    minNits = args[0].Uint / 10000f;
                    maxNits = args[1].Uint;
                    referenceNits = args[2].Uint;
                    break;
                case InfoEventTargetLuminance:
                    targetMinNits = args[0].Uint / 10000f;
                    targetMaxNits = args[1].Uint;
                    haveTarget = true;
                    break;
                case InfoEventTargetMaxFall:
                    maxFallNits = args[0].Uint;
                    break;
            }
        }
    }

    /// <summary>
    /// <c>wl_interface</c> tables for the four interfaces used, built once and kept for the life
    /// of the process. Every request and event up to the highest opcode is listed with its
    /// exact signature, since libwayland demarshals events by opcode.
    /// </summary>
    private static class Protocol
    {
        internal static readonly nint Manager = Allocate();
        internal static readonly nint Feedback = Allocate();
        internal static readonly nint Description = Allocate();
        internal static readonly nint Info = Allocate();

        static Protocol()
        {
            Fill(
                Manager,
                "wp_color_manager_v1",
                new[]
                {
                    Message("destroy", string.Empty),
                    Message("get_output", "no", 0, 0),
                    Message("get_surface", "no", 0, 0),
                    Message("get_surface_feedback", "no", Feedback, 0),
                    Message("create_icc_creator", "n", 0),
                    Message("create_parametric_creator", "n", 0),
                    Message("create_windows_scrgb", "n", Description),
                },
                new[]
                {
                    Message("supported_intent", "u", 0),
                    Message("supported_feature", "u", 0),
                    Message("supported_tf_named", "u", 0),
                    Message("supported_primaries_named", "u", 0),
                    Message("done", string.Empty),
                });
            Fill(
                Feedback,
                "wp_color_management_surface_feedback_v1",
                new[]
                {
                    Message("destroy", string.Empty),
                    Message("get_preferred", "n", Description),
                    Message("get_preferred_parametric", "n", Description),
                },
                new[]
                {
                    Message("preferred_changed", "u", 0),
                    Message("preferred_changed2", "2uu", 0, 0),
                });
            Fill(
                Description,
                "wp_image_description_v1",
                new[]
                {
                    Message("destroy", string.Empty),
                    Message("get_information", "n", Info),
                },
                new[]
                {
                    Message("failed", "us", 0, 0),
                    Message("ready", "u", 0),
                    Message("ready2", "2uu", 0, 0),
                });
            Fill(
                Info,
                "wp_image_description_info_v1",
                Array.Empty<(nint, nint, nint)>(),
                new[]
                {
                    Message("done", string.Empty),
                    Message("icc_file", "hu", 0, 0),
                    Message("primaries", "iiiiiiii", 0, 0, 0, 0, 0, 0, 0, 0),
                    Message("primaries_named", "u", 0),
                    Message("tf_power", "u", 0),
                    Message("tf_named", "u", 0),
                    Message("luminances", "uuu", 0, 0, 0),
                    Message("target_primaries", "iiiiiiii", 0, 0, 0, 0, 0, 0, 0, 0),
                    Message("target_luminance", "uu", 0, 0),
                    Message("target_max_cll", "u", 0),
                    Message("target_max_fall", "u", 0),
                });
        }

        // struct wl_interface { const char *name; int version; int method_count;
        //   const struct wl_message *methods; int event_count; const struct wl_message *events; }
        private static nint Allocate() => (nint)NativeMemory.AllocZeroed(40);

        private static void Fill(nint target, string name, (nint Name, nint Signature, nint Types)[] methods, (nint Name, nint Signature, nint Types)[] events)
        {
            byte* p = (byte*)target;
            *(nint*)p = Marshal.StringToCoTaskMemUTF8(name);
            *(int*)(p + 8) = 1;
            *(int*)(p + 12) = methods.Length;
            *(nint*)(p + 16) = Messages(methods);
            *(int*)(p + 24) = events.Length;
            *(nint*)(p + 32) = Messages(events);
        }

        // struct wl_message { const char *name; const char *signature; const struct wl_interface **types; }
        private static nint Messages((nint Name, nint Signature, nint Types)[] messages)
        {
            if (messages.Length == 0)
            {
                return 0;
            }

            nint* table = (nint*)NativeMemory.AllocZeroed((nuint)(messages.Length * 3), (nuint)sizeof(nint));
            for (int i = 0; i < messages.Length; i++)
            {
                table[(i * 3) + 0] = messages[i].Name;
                table[(i * 3) + 1] = messages[i].Signature;
                table[(i * 3) + 2] = messages[i].Types;
            }

            return (nint)table;
        }

        private static (nint Name, nint Signature, nint Types) Message(string name, string signature, params nint[] types)
        {
            nint typeTable = 0;
            if (types.Length > 0)
            {
                nint* t = (nint*)NativeMemory.AllocZeroed((nuint)types.Length, (nuint)sizeof(nint));
                for (int i = 0; i < types.Length; i++)
                {
                    t[i] = types[i];
                }

                typeTable = (nint)t;
            }

            return (Marshal.StringToCoTaskMemUTF8(name), Marshal.StringToCoTaskMemUTF8(signature), typeTable);
        }
    }
}
