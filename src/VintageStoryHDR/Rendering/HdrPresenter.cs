using System;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageStoryHDR.Interop;

namespace VintageStoryHDR.Rendering;

/// <summary>
/// Owns everything between "the game thinks it is drawing to the window" and "an HDR
/// frame is on screen".
///
/// <list type="bullet">
/// <item>
/// A <b>redirect framebuffer</b> (RGBA16F + depth/stencil) stands in for GL's default
/// framebuffer. The game blits the scene into it and draws the GUI over the top, exactly
/// as it would into the window. Its contents are display-referred and gamma-encoded like
/// vanilla's, except that scene highlights may exceed 1.0 and scene colours outside
/// Rec.709 have negative components.
/// </item>
/// <item>
/// At the end of the frame the redirect texture is decoded to linear light, scaled to
/// paper white, rolled off towards the display's peak and written into the texture of an
/// <see cref="IHdrOutput"/>: as scRGB for DXGI on Windows, as PQ Rec.2020 (HDR10) for
/// Vulkan on Wayland. The output presents it.
/// </item>
/// </list>
///
/// Every method must be called on the render thread with the game's GL context current.
/// </summary>
internal sealed class HdrPresenter : IDisposable
{
    private const string VertexSource = @"#version 330 core
out vec2 uv;
void main() {
	vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
	// GL's row 0 is the bottom of the image; D3D's and Vulkan's is the top.
	uv = vec2(p.x, 1.0 - p.y);
	gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 330 core
uniform sampler2D source;
uniform float gamma;
uniform float paperWhite; // in scRGB units, 1.0 = 80 nits
uniform float peak;       // in scRGB units
uniform int ditherFrame;  // < 0 disables
uniform int encodePq;     // 1: write PQ Rec.2020 (HDR10) instead of scRGB
uniform float pqScale;    // encoded nits per displayed nit, for compositors that rescale PQ
in vec2 uv;
out vec4 outColor;

// SMPTE ST 2084, with 1.0 = 10000 nits.
vec3 pqEncode(vec3 y) {
	vec3 p = pow(clamp(y, 0.0, 1.0), vec3(0.1593017578125));
	return pow((0.8359375 + 18.8515625 * p) / (1.0 + 18.6875 * p), vec3(78.84375));
}
vec3 pqDecode(vec3 e) {
	vec3 p = pow(clamp(e, 0.0, 1.0), vec3(1.0 / 78.84375));
	return pow(max(p - 0.8359375, 0.0) / (18.8515625 - 18.6875 * p), vec3(1.0 / 0.1593017578125));
}
float hash(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

// Linear Rec.709 <-> Rec.2020. The display signal is Rec.2020, so that is the space where
// wide-gamut colours are non-negative and where quantisation actually happens.
const mat3 rec709To2020 = mat3(
	0.6274039, 0.0690973, 0.0163914,
	0.3292830, 0.9195404, 0.0880133,
	0.0433131, 0.0113623, 0.8955953);
const mat3 rec2020To709 = mat3(
	 1.6604910, -0.1245505, -0.0181508,
	-0.5876411,  1.1328999, -0.1005789,
	-0.0728499, -0.0083494,  1.1187297);

void main() {
	// Sign-preserving decode: negative components are colours outside Rec.709.
	vec3 encoded = texture(source, uv).rgb;
	vec3 lin = sign(encoded) * pow(abs(encoded), vec3(gamma)) * paperWhite;

	// Roll the top quarter of the range off towards the display's peak, scaling all
	// three channels together so bright colours keep their hue instead of going white.
	float m = max(lin.r, max(lin.g, lin.b));
	float knee = 0.75 * peak;
	if (m > knee) {
		float range = peak - knee;
		float rolled = knee + range * (1.0 - exp(-(m - knee) / range));
		lin *= rolled / m;
	}

	// The display signal is 10-bit PQ, and the compositor quantises scRGB to it without
	// dithering. Do it here: triangular noise, one code value wide, in the domain it is
	// quantised in.
	vec3 noise = vec3(0.0);
	if (ditherFrame >= 0) {
		vec3 seed = vec3(gl_FragCoord.xy, float(ditherFrame));
		noise = vec3(
			hash(seed) + hash(seed + 17.0),
			hash(seed + 31.0) + hash(seed + 47.0),
			hash(seed + 59.0) + hash(seed + 71.0)) - 1.0;
	}

	if (encodePq != 0) {
		vec3 encodedPq = pqEncode(rec709To2020 * lin * pqScale * (80.0 / 10000.0)) + noise / 1023.0;
		outColor = vec4(clamp(encodedPq, 0.0, 1.0), 1.0);
		return;
	}

	if (ditherFrame >= 0) {
		vec3 pq = pqEncode(rec709To2020 * lin * (80.0 / 10000.0)) + noise / 1023.0;
		lin = rec2020To709 * pqDecode(pq) * (10000.0 / 80.0);
	}

	outColor = vec4(lin, 1.0);
}";

    private readonly Func<(int Width, int Height)> clientSize;
    private readonly Func<int, int, IHdrOutput> createOutput;
    private IHdrOutput? output;

    private int sharedFramebuffer;
    private int redirectTexture;
    private int redirectDepthStencil;
    private int program;
    private int vertexArray;
    private int uniformGamma;
    private int uniformPaperWhite;
    private int uniformPeak;
    private int uniformDitherFrame;
    private int uniformEncodePq;
    private int uniformPqScale;
    private int frameCounter;

    private HdrPresenter(Func<(int Width, int Height)> clientSize, Func<int, int, IHdrOutput> createOutput)
    {
        this.clientSize = clientSize;
        this.createOutput = createOutput;
    }

    /// <summary>The framebuffer object that stands in for framebuffer 0.</summary>
    internal int RedirectFramebuffer { get; private set; }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal DisplayInfo Display => output?.Display ?? default;

    internal bool TearingSupported => output?.TearingSupported ?? false;

    /// <summary>
    /// Builds the whole presentation path for the Win32 window <paramref name="parentWindow"/>.
    /// Throws <see cref="HdrUnavailableException"/> and leaves nothing behind if any part of it
    /// is not available on this machine.
    /// </summary>
    internal static HdrPresenter Create(nint parentWindow) =>
        Create(
            () => NativeMethods.GetClientRect(parentWindow, out NativeMethods.Rect rect)
                ? (rect.Right - rect.Left, rect.Bottom - rect.Top)
                : (0, 0),
            (width, height) => DxgiOutput.Create(parentWindow, width, height));

    /// <summary>As <see cref="Create(nint)"/>, for a game window running on GLFW's Wayland backend.</summary>
    internal static unsafe HdrPresenter CreateWayland(NativeWindow window)
    {
        Window* handle = window.WindowPtr;
        return Create(
            () =>
            {
                GLFW.GetFramebufferSize(handle, out int width, out int height);
                return (width, height);
            },
            (width, height) => WaylandVulkanOutput.Create(window, width, height));
    }

    private static HdrPresenter Create(Func<(int Width, int Height)> clientSize, Func<int, int, IHdrOutput> createOutput)
    {
        HdrPresenter presenter = new(clientSize, createOutput);
        try
        {
            presenter.Initialise();
            return presenter;
        }
        catch
        {
            presenter.Dispose();
            throw;
        }
    }

    private void Initialise()
    {
        (int width, int height) = clientSize();
        if (width <= 0 || height <= 0)
        {
            throw new HdrUnavailableException("The game window has no client area (minimised?).");
        }

        output = createOutput(width, height);

        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int previousFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        int previousRenderbuffer = GL.GetInteger(GetPName.RenderbufferBinding);
        try
        {
            program = BuildProgram();
            uniformGamma = GL.GetUniformLocation(program, "gamma");
            uniformPaperWhite = GL.GetUniformLocation(program, "paperWhite");
            uniformPeak = GL.GetUniformLocation(program, "peak");
            uniformDitherFrame = GL.GetUniformLocation(program, "ditherFrame");
            uniformEncodePq = GL.GetUniformLocation(program, "encodePq");
            uniformPqScale = GL.GetUniformLocation(program, "pqScale");
            GL.ProgramUniform1(program, GL.GetUniformLocation(program, "source"), 0);
            vertexArray = GL.GenVertexArray();

            redirectTexture = GL.GenTexture();
            redirectDepthStencil = GL.GenRenderbuffer();
            RedirectFramebuffer = GL.GenFramebuffer();
            sharedFramebuffer = GL.GenFramebuffer();

            Width = width;
            Height = height;
            AllocateRedirectTargets();
            AttachSharedTexture();
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, previousRenderbuffer);
        }
    }

    /// <summary>
    /// Follows the game window's client size. Call once at the start of a frame, never in
    /// the middle of one. Returns false while the window has no area to present to.
    /// </summary>
    internal bool SyncSize()
    {
        (int width, int height) = clientSize();
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (width == Width && height == Height)
        {
            return true;
        }

        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int previousFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        int previousRenderbuffer = GL.GetInteger(GetPName.RenderbufferBinding);
        try
        {
            output!.Resize(width, height);

            Width = width;
            Height = height;
            AllocateRedirectTargets();
            AttachSharedTexture();
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, previousRenderbuffer);
        }

        return true;
    }

    /// <summary>
    /// Encodes the redirect framebuffer for the output and presents it. Replaces the GL buffer
    /// swap. Leaves the GL state it touched as it found it.
    /// </summary>
    internal void Present(HdrConfig config, bool vsync)
    {
        if (output is null || output.Texture == 0)
        {
            return;
        }

        // Encoded 1.0 in the redirect buffer is GUI white. The scene was already scaled
        // relative to that by the final shader -- if it is patched; if not, scene and GUI
        // cannot be told apart and both sit at the scene's level, as they always did.
        config.DisplaySdrWhiteNits = Display.SdrWhiteNits;
        float whiteNits = HdrRuntime.FinalShaderPatched ? config.EffectiveUiNits : config.EffectivePaperWhiteNits;
        float brightestWhite = Math.Max(whiteNits, config.EffectivePaperWhiteNits);

        float peakNits = config.PeakNits > 0f ? config.PeakNits : Display.MaxNits;
        if (!(peakNits >= brightestWhite))
        {
            // No usable figure from the display (SDR, or a driver that reports 0).
            peakNits = Math.Max(brightestWhite, 1000f);
        }

        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        int previousVertexArray = GL.GetInteger(GetPName.VertexArrayBinding);
        int previousActiveTexture = GL.GetInteger(GetPName.ActiveTexture);
        bool blend = GL.IsEnabled(EnableCap.Blend);
        bool depthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool cullFace = GL.IsEnabled(EnableCap.CullFace);
        bool scissorTest = GL.IsEnabled(EnableCap.ScissorTest);
        bool stencilTest = GL.IsEnabled(EnableCap.StencilTest);

        GL.ActiveTexture(TextureUnit.Texture0);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);

        output.BeginWrite();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, sharedFramebuffer);
            GL.Viewport(0, 0, Width, Height);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.StencilTest);

            GL.UseProgram(program);
            GL.Uniform1(uniformGamma, config.SdrGamma);
            GL.Uniform1(uniformPaperWhite, whiteNits / 80f);
            GL.Uniform1(uniformPeak, peakNits / 80f);
            frameCounter = (frameCounter + 1) & 0xFF;
            GL.Uniform1(uniformDitherFrame, config.Dither ? frameCounter : -1);
            GL.Uniform1(uniformEncodePq, output.EncodesPq ? 1 : 0);
            GL.Uniform1(uniformPqScale, output.ContentScale);
            GL.BindTexture(TextureTarget.Texture2D, redirectTexture);
            GL.BindVertexArray(vertexArray);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
            GL.BindVertexArray(previousVertexArray);
            GL.UseProgram(previousProgram);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, RedirectFramebuffer);
            Restore(EnableCap.Blend, blend);
            Restore(EnableCap.DepthTest, depthTest);
            Restore(EnableCap.CullFace, cullFace);
            Restore(EnableCap.ScissorTest, scissorTest);
            Restore(EnableCap.StencilTest, stencilTest);

            output.EndWrite();
        }

        output.Present(vsync, peakNits);
    }

    public void Dispose()
    {
        if (GlContext.IsCurrent)
        {
            DeleteIfSet(ref redirectTexture, GL.DeleteTexture);
            DeleteIfSet(ref redirectDepthStencil, GL.DeleteRenderbuffer);
            DeleteIfSet(ref sharedFramebuffer, GL.DeleteFramebuffer);
            int redirect = RedirectFramebuffer;
            DeleteIfSet(ref redirect, GL.DeleteFramebuffer);
            RedirectFramebuffer = 0;
            DeleteIfSet(ref vertexArray, GL.DeleteVertexArray);
            DeleteIfSet(ref program, GL.DeleteProgram);
        }

        output?.Dispose();
        output = null;
    }

    private void AllocateRedirectTargets()
    {
        GL.BindTexture(TextureTarget.Texture2D, redirectTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, Width, Height, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, redirectDepthStencil);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, Width, Height);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RedirectFramebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, redirectTexture, 0);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, redirectDepthStencil);
        RequireComplete("redirect");
    }

    private void AttachSharedTexture()
    {
        IHdrOutput target = output!;
        target.WithTexture(() =>
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, sharedFramebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target.Texture, 0);
            RequireComplete("output");
        });
    }

    private static void RequireComplete(string name)
    {
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            throw new HdrUnavailableException($"The {name} framebuffer is incomplete ({status}).");
        }
    }

    private static int BuildProgram()
    {
        int vertex = Compile(ShaderType.VertexShader, VertexSource);
        int fragment = Compile(ShaderType.FragmentShader, FragmentSource);
        int linked = GL.CreateProgram();
        GL.AttachShader(linked, vertex);
        GL.AttachShader(linked, fragment);
        GL.LinkProgram(linked);
        GL.GetProgram(linked, GetProgramParameterName.LinkStatus, out int ok);
        string log = ok == 0 ? GL.GetProgramInfoLog(linked) : string.Empty;
        GL.DetachShader(linked, vertex);
        GL.DetachShader(linked, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (ok == 0)
        {
            GL.DeleteProgram(linked);
            throw new HdrUnavailableException("The presentation shader failed to link: " + log);
        }

        return linked;
    }

    private static int Compile(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new HdrUnavailableException($"The presentation {type} failed to compile: {log}");
        }

        return shader;
    }

    private static void Restore(EnableCap cap, bool enabled)
    {
        if (enabled)
        {
            GL.Enable(cap);
        }
        else
        {
            GL.Disable(cap);
        }
    }

    private static void DeleteIfSet(ref int name, Action<int> delete)
    {
        if (name != 0)
        {
            delete(name);
            name = 0;
        }
    }
}
