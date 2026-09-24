using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VintageStoryHDR.Interop;
using VintageStoryHDR.Platform;
using ErrorCode = OpenTK.Graphics.OpenGL.ErrorCode;

namespace VintageStoryHDR.Rendering;

/// <summary>
/// The live state shared by the patches: whether HDR presentation is in charge right
/// now, and the presenter doing it. Everything here runs on the render thread.
///
/// <para>
/// The takeover is all-or-nothing and reversible at a frame boundary. Any failure, at
/// start-up or in the middle of play, drops back to vanilla presentation and stays there
/// until the player asks again with <c>.hdr on</c>.
/// </para>
/// </summary>
internal static class HdrRuntime
{
    private static readonly float[] OpaqueBlack = { 0f, 0f, 0f, 1f };

    private static HdrPresenter? presenter;
    private static bool gaveUp;
    private static int floatPrimaryTexture;
    private static int smoothSkyTexture;

    internal static HdrConfig Config { get; set; } = new();

    internal static ILogger? Log { get; set; }

    /// <summary>The running game, for the one field read off it. Null outside a world.</summary>
    internal static ClientMain? Game { get; set; }

    /// <summary>Set once the mod has patched the game; cleared on unload so stale hooks do nothing.</summary>
    internal static bool Armed { get; set; }

    /// <summary>True while frames are presented through DXGI instead of the GL buffer swap.</summary>
    internal static bool Active => presenter is not null;

    /// <summary>Whether the final shader currently loaded carries the HDR code path.</summary>
    internal static bool FinalShaderPatched { get; set; }

    /// <summary>Whether the night sky shader currently loaded carries the star boost.</summary>
    internal static bool NightSkyShaderPatched { get; set; }

    internal static HdrPresenter? Presenter => presenter;

    /// <summary>Why HDR is not active, for <c>.hdr</c>. Null while it is, or before the first attempt.</summary>
    internal static string? InactiveReason { get; set; }

    /// <summary>
    /// Attributes GL errors to this mod's hooks. glGetError is a pipeline sync, so it only
    /// runs for the first few hundred frames after arming -- long enough to cover start-up,
    /// the first framebuffer rebuild and the first shader reload. Call <see cref="GlCheckBegin"/>
    /// before a hook's GL work to report errors that were already pending (not ours), and
    /// <see cref="GlCheckEnd"/> after it.
    /// </summary>
    private static int glChecksLeft = 600;

    internal static bool GlCheckBegin()
    {
        if (glChecksLeft <= 0)
        {
            return false;
        }

        glChecksLeft--;
        ErrorCode pending;
        while ((pending = GL.GetError()) != ErrorCode.NoError)
        {
            // Reading an error clears it, so say so: otherwise the game would have reported it.
            Log?.VerboseDebug("GL error {0} was already pending when this mod's hook ran; it was raised by the game or another mod.", pending);
        }

        return true;
    }

    internal static void GlCheckEnd(bool checking, string where)
    {
        if (!checking)
        {
            return;
        }

        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Log?.Warning("GL error {0} raised in {1}. Please report this.", error, where);
        }
    }

    /// <summary>Forgets an earlier failure so the next frame tries again.</summary>
    internal static void Retry()
    {
        gaveUp = false;
        InactiveReason = null;
    }

    /// <summary>
    /// Frame start, before the game has drawn anything. Brings the presenter in line with
    /// the config and the window, then clears the redirect framebuffer the way the game is
    /// about to clear framebuffer 0. Leaves the framebuffer binding as it found it.
    /// </summary>
    internal static void OnFrameStart(ClientPlatformWindows platform, List<FrameBufferRef> frameBuffers)
    {
        if (!Armed)
        {
            return;
        }

        bool checking = GlCheckBegin();
        FrameStart(platform, frameBuffers);
        GlCheckEnd(checking, "frame start");
    }

    private static void FrameStart(ClientPlatformWindows platform, List<FrameBufferRef> frameBuffers)
    {

        bool wanted = Config.Enabled && !gaveUp;
        if (wanted && presenter is null)
        {
            Activate(platform);
        }
        else if (!wanted && presenter is not null)
        {
            Deactivate(Config.Enabled ? InactiveReason : "Disabled in config.");
        }

        EnsurePrimaryFormat(frameBuffers);
        EnsureSkyFiltering();
        if (presenter is null)
        {
            return;
        }

        try
        {
            presenter.SyncSize();

            // glClearBuffer ignores the clear colour state but still honours the write masks.
            int previousFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
            bool depthMask = GL.GetBoolean(GetPName.DepthWritemask);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, presenter.RedirectFramebuffer);
            GL.DepthMask(true);
            GL.ClearBuffer(ClearBuffer.Color, 0, OpaqueBlack);
            GL.ClearBuffer(ClearBufferCombined.DepthStencil, 0, 1f, 0);
            GL.DepthMask(depthMask);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
        }
        catch (HdrUnavailableException e)
        {
            Fail(e.Message);
            EnsurePrimaryFormat(frameBuffers);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    /// <summary>The scene framebuffers were rebuilt, so the scene colour buffer is RGBA8 again.</summary>
    internal static void OnFrameBuffersRebuilt(List<FrameBufferRef> frameBuffers)
    {
        floatPrimaryTexture = 0;
        if (Armed && presenter is not null)
        {
            bool checking = GlCheckBegin();
            EnsurePrimaryFormat(frameBuffers);
            GlCheckEnd(checking, "framebuffer rebuild");
        }
    }

    /// <summary>Frame end. Returns false when the caller should do the vanilla buffer swap instead.</summary>
    internal static bool Present(bool vsync)
    {
        if (!Armed || presenter is null)
        {
            return false;
        }

        try
        {
            bool checking = GlCheckBegin();
            presenter.Present(Config, vsync);
            GlCheckEnd(checking, "present");
            return true;
        }
        catch (HdrUnavailableException e)
        {
            Fail(e.Message);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return false;
        }
    }

    /// <summary>Tears everything down. Safe to call when nothing is active.</summary>
    internal static void Shutdown(List<FrameBufferRef>? frameBuffers)
    {
        Deactivate("Mod unloaded.");
        if (GlContext.IsCurrent)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (frameBuffers is not null)
            {
                EnsurePrimaryFormat(frameBuffers);
            }

            EnsureSkyFiltering();
        }

        Game = null;
        smoothSkyTexture = 0;
        gaveUp = false;
        InactiveReason = null;
        FinalShaderPatched = false;
        NightSkyShaderPatched = false;
    }

    private static void Activate(ClientPlatformWindows platform)
    {
        HdrPresenter? created = null;
        try
        {
            created = OperatingSystem.IsWindows()
                ? HdrPresenter.Create(WindowHandle(platform.window))
                : HdrPresenter.CreateWayland(platform.window);

            if (!created.Display.HdrEnabled && !Config.ForceOnSdrDisplay)
            {
                throw new HdrUnavailableException(
                    OperatingSystem.IsWindows()
                        ? "Windows reports this display as SDR. Turn on \"Use HDR\" in Windows display settings, then type .hdr on."
                        : WaylandVulkanOutput.SdrDisplayMessage);
            }

            presenter = created;
            created = null;
            InactiveReason = null;
            Log?.Notification(
                "HDR presentation active: {0}x{1} {6}, display {2} (peak {3:0} nits, full-frame {4:0} nits), tearing {5}.",
                presenter.Width,
                presenter.Height,
                presenter.Display.HdrEnabled ? "HDR" : "SDR (forced)",
                presenter.Display.MaxNits,
                presenter.Display.MaxFullFrameNits,
                presenter.TearingSupported ? "supported" : "not supported",
                OperatingSystem.IsWindows() ? "scRGB via DXGI" : "HDR10 via Vulkan on a Wayland subsurface");
        }
        catch (Exception e) when (e is HdrUnavailableException or DllNotFoundException or EntryPointNotFoundException)
        {
            created?.Dispose();
            gaveUp = true;
            InactiveReason = e.Message;
            Log?.Warning("HDR presentation not available, vanilla presentation stays in charge: {0}", e.Message);
        }
    }

    private static void Fail(string reason)
    {
        gaveUp = true;
        Log?.Warning("HDR presentation stopped, back to vanilla presentation: {0}", reason);
        Deactivate(reason);
    }

    /// <summary>Drops the presenter. Leaves framebuffer bindings to the caller, who knows where in the frame it is.</summary>
    private static void Deactivate(string? reason)
    {
        if (presenter is null)
        {
            return;
        }

        InactiveReason = reason;
        presenter.Dispose();
        presenter = null;
        Config.DisplaySdrWhiteNits = 0f;

        if (GlContext.IsCurrent)
        {
            FinalShaderUniforms.Disable();
        }
    }

    /// <summary>
    /// Re-specifies the scene colour texture and the bloom blur targets as RGBA16F (or back
    /// to vanilla's RGBA8). Texture names and framebuffer attachments stay as they are, so
    /// nothing in the game needs to know. Sizes are read back from GL because the game does
    /// not record one for every framebuffer.
    /// </summary>
    private static void EnsurePrimaryFormat(List<FrameBufferRef> frameBuffers)
    {
        if (frameBuffers.Count == 0 || frameBuffers[0]?.ColorTextureIds is not { Length: > 0 } colorTextures)
        {
            return;
        }

        int marker = colorTextures[0];
        bool wantFloat = presenter is not null && Config.FloatSceneBuffer;
        bool isFloat = floatPrimaryTexture == marker;
        if (wantFloat == isFloat)
        {
            return;
        }

        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        foreach (int index in HdrPatchTargets.PromotedFrameBuffers)
        {
            if (index >= frameBuffers.Count || frameBuffers[index]?.ColorTextureIds is not { Length: > 0 } targets)
            {
                continue;
            }

            GL.BindTexture(TextureTarget.Texture2D, targets[0]);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int width);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int height);
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            if (wantFloat)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
            }
            else
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            }
        }

        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        floatPrimaryTexture = wantFloat ? marker : 0;
    }

    /// <summary>
    /// Switches sky.png between vanilla's nearest filtering and linear. The texture is
    /// created once per world, so this only touches GL when the wanted state changes.
    /// </summary>
    private static void EnsureSkyFiltering()
    {
        if (Game is null || HdrPatchTargets.SkyTextureId?.GetValue(Game) is not int texture || texture == 0)
        {
            return;
        }

        bool wantSmooth = presenter is not null && Config.SmoothSkyGradient;
        if (wantSmooth == (smoothSkyTexture == texture))
        {
            return;
        }

        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        int filter = wantSmooth ? (int)TextureMagFilter.Linear : (int)TextureMagFilter.Nearest;
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        smoothSkyTexture = wantSmooth ? texture : 0;
    }

    private static unsafe nint WindowHandle(NativeWindow window) => GLFW.GetWin32Window(window.WindowPtr);
}
