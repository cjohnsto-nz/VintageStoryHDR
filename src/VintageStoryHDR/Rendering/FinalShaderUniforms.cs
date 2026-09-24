using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace VintageStoryHDR.Rendering;

/// <summary>
/// Feeds the uniforms <see cref="FinalShaderPatcher"/> and <see cref="NightSkyShaderPatcher"/>
/// added to the game's shaders.
/// They are written with glProgramUniform, which needs no program bound and so cannot
/// disturb the game's own shader bookkeeping. Locations are cached per program object;
/// a shader reload produces a new one.
/// </summary>
internal static class FinalShaderUniforms
{
    private static int cachedProgram;
    private static int locationEnabled = -1;
    private static int locationEmissiveBoost = -1;
    private static int locationHighlightBoost = -1;
    private static int locationGamma = -1;
    private static int locationGamut = -1;
    private static int locationSceneScale = -1;

    private static int cachedNightSkyProgram;
    private static int locationStarBoost = -1;
    private static int locationStarGamma = -1;

    /// <summary>Call right before the final composition pass draws.</summary>
    internal static void Apply()
    {
        ApplyNightSky(HdrRuntime.Active && HdrRuntime.Config.FloatSceneBuffer ? HdrRuntime.Config.StarBoost : 0f);
        if (!Resolve())
        {
            return;
        }

        HdrConfig config = HdrRuntime.Config;
        GL.ProgramUniform1(cachedProgram, locationEnabled, HdrRuntime.Active ? 1 : 0);
        GL.ProgramUniform1(cachedProgram, locationEmissiveBoost, config.EmissiveBoost);
        GL.ProgramUniform1(cachedProgram, locationHighlightBoost, config.HighlightBoost);
        GL.ProgramUniform1(cachedProgram, locationGamma, config.SdrGamma);
        GL.ProgramUniform1(cachedProgram, locationGamut, config.GamutExpansion);
        GL.ProgramUniform1(cachedProgram, locationSceneScale, config.EffectivePaperWhiteNits / config.EffectiveUiNits);
    }

    /// <summary>Puts the final shader back on its vanilla path. The value lives in the program object, so it has to be cleared explicitly.</summary>
    internal static void Disable()
    {
        ApplyNightSky(0f);
        if (Resolve())
        {
            GL.ProgramUniform1(cachedProgram, locationEnabled, 0);
        }
    }

    // Set once a frame from the final pass's hook; the night sky is drawn long before it,
    // so a change lands one frame late, which nobody can see.
    private static void ApplyNightSky(float starBoost)
    {
        int program = ShaderPrograms.Nightsky?.ProgramId ?? 0;
        if (program == 0 || !HdrRuntime.NightSkyShaderPatched)
        {
            return;
        }

        if (program != cachedNightSkyProgram)
        {
            cachedNightSkyProgram = program;
            locationStarBoost = GL.GetUniformLocation(program, NightSkyShaderPatcher.UniformStarBoost);
            locationStarGamma = GL.GetUniformLocation(program, NightSkyShaderPatcher.UniformGamma);
        }

        if (locationStarBoost >= 0)
        {
            GL.ProgramUniform1(program, locationStarBoost, starBoost);
            GL.ProgramUniform1(program, locationStarGamma, HdrRuntime.Config.SdrGamma);
        }
    }

    private static bool Resolve()
    {
        int program = ShaderPrograms.Final?.ProgramId ?? 0;
        if (program == 0 || !HdrRuntime.FinalShaderPatched)
        {
            return false;
        }

        if (program != cachedProgram)
        {
            cachedProgram = program;
            locationEnabled = GL.GetUniformLocation(program, FinalShaderPatcher.UniformEnabled);
            locationEmissiveBoost = GL.GetUniformLocation(program, FinalShaderPatcher.UniformEmissiveBoost);
            locationHighlightBoost = GL.GetUniformLocation(program, FinalShaderPatcher.UniformHighlightBoost);
            locationGamma = GL.GetUniformLocation(program, FinalShaderPatcher.UniformGamma);
            locationGamut = GL.GetUniformLocation(program, FinalShaderPatcher.UniformGamut);
            locationSceneScale = GL.GetUniformLocation(program, FinalShaderPatcher.UniformSceneScale);
        }

        return locationEnabled >= 0;
    }
}
