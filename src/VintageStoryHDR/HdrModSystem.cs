using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VintageStoryHDR.Patches;
using VintageStoryHDR.Platform;
using VintageStoryHDR.Rendering;

namespace VintageStoryHDR;

/// <summary>
/// Client-only mod that presents the game in HDR on Windows, and on Linux under Wayland. See README.md for how the
/// pipeline fits together and docs/findings.md for the game internals behind it.
/// </summary>
public sealed class HdrModSystem : ModSystem
{
    private const string ConfigFile = "hdr.json";

    /// <summary>The config file of 0.2.0 and earlier, when the mod id was vshdr. Read once, if the new one does not exist yet.</summary>
    private const string LegacyConfigFile = "vshdr.json";

    private const string HarmonyId = "net.johnstone.hdr";

    private Harmony? harmony;
    private ICoreClientAPI? capi;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        HdrRuntime.Log = Mod.Logger;
        LoadConfig(api);
        RegisterCommand(api);

        if (!OperatingSystem.IsWindows() && !WaylandVulkanOutput.GameIsOnWayland())
        {
            HdrRuntime.InactiveReason = OperatingSystem.IsLinux()
                ? "on Linux, HDR output needs the game running on native Wayland, not X11 or XWayland."
                : "HDR output is only implemented for Windows and for Linux on Wayland.";
            Mod.Logger.Notification("{0} Vanilla presentation left untouched.", HdrRuntime.InactiveReason);
            return;
        }

        IReadOnlyList<string> problems = HdrPatchTargets.Verify();
        if (problems.Count > 0)
        {
            Mod.Logger.Warning(
                "Not taking over presentation: {0} of the game internals this mod hooks have moved. " +
                "Vanilla presentation is left in charge.",
                problems.Count);
            foreach (string problem in problems)
            {
                Mod.Logger.Warning("  - {0}", problem);
            }

            HdrRuntime.InactiveReason = "this game version moved internals the mod hooks; see client-main.log.";
            return;
        }

        harmony = new Harmony(HarmonyId);
        FinalShaderPatch.Apply(harmony);
        PresentationPatches.Apply(harmony);

        // From here the hooks are live. The presenter itself is built at the start of the
        // next frame, on the render thread, and the game reloads its shaders right after
        // mods have started, which is when the final shader picks up its HDR path.
        HdrRuntime.Game = api.World as ClientMain;
        HdrRuntime.Armed = true;
        Mod.Logger.Notification(
            HdrRuntime.Config.Enabled
                ? "Hooks installed. HDR presentation starts with the next frame."
                : "Hooks installed, but disabled in config. Type .hdr on to enable.");
    }

    public override void Dispose()
    {
        HdrRuntime.Armed = false;

        try
        {
            List<FrameBufferRef>? frameBuffers = null;
            if (ScreenManager.Platform is ClientPlatformWindows platform)
            {
                frameBuffers = HdrPatchTargets.FrameBuffers?.GetValue(platform) as List<FrameBufferRef>;
            }

            HdrRuntime.Shutdown(frameBuffers);
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Error while shutting HDR presentation down: {0}", e.Message);
        }

        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        HdrRuntime.Log = null;
        capi = null;
        base.Dispose();
    }

    private void RegisterCommand(ICoreClientAPI api)
    {
        CommandArgumentParsers parsers = api.ChatCommands.Parsers;
        api.ChatCommands
            .Create("hdr")
            .WithDescription("Native HDR: status and live tuning. .hdr [on|off|paperwhite|ui|peak|emissive|highlight|stars|gamut|gamma|floatscene|smoothsky|dither] [value]")
            .WithArgs(parsers.OptionalWord("setting"), parsers.OptionalFloat("value"))
            .HandleWith(OnCommand);
    }

    private TextCommandResult OnCommand(TextCommandCallingArgs args)
    {
        HdrConfig config = HdrRuntime.Config;
        string? setting = (args[0] as string)?.ToLowerInvariant();
        float? value = args.Parsers[1].IsMissing ? null : (float)args[1];

        switch (setting)
        {
            case null or "":
                return TextCommandResult.Success(Status());
            case "on":
                config.Enabled = true;
                HdrRuntime.Retry();
                break;
            case "off":
                config.Enabled = false;
                break;
            case "paperwhite" when value is not null:
                config.PaperWhiteNits = value.Value;
                break;
            case "ui" when value is not null:
                config.UiNits = value.Value;
                break;
            case "peak" when value is not null:
                config.PeakNits = value.Value;
                break;
            case "emissive" when value is not null:
                config.EmissiveBoost = value.Value;
                break;
            case "highlight" when value is not null:
                config.HighlightBoost = value.Value;
                break;
            case "gamut" when value is not null:
                config.GamutExpansion = value.Value;
                break;
            case "stars" when value is not null:
                config.StarBoost = value.Value;
                break;
            case "gamma" when value is not null:
                config.SdrGamma = value.Value;
                break;
            case "floatscene" when value is not null:
                config.FloatSceneBuffer = value.Value != 0f;
                break;
            case "smoothsky" when value is not null:
                config.SmoothSkyGradient = value.Value != 0f;
                break;
            case "dither" when value is not null:
                config.Dither = value.Value != 0f;
                break;
            default:
                return TextCommandResult.Error(
                    "Usage: .hdr | .hdr on | .hdr off | .hdr paperwhite <nits, 0 = display> | .hdr ui <nits, 0 = paperwhite> | .hdr peak <nits, 0 = display> | " +
                    ".hdr emissive <x> | .hdr highlight <x> | .hdr stars <x> | .hdr gamut <0..1> | .hdr gamma <g> | .hdr floatscene|smoothsky|dither <0|1>");
        }

        config.Sanitise();
        StoreConfig();
        return TextCommandResult.Success(Status());
    }

    private static string Status()
    {
        HdrConfig config = HdrRuntime.Config;
        string state;
        if (HdrRuntime.Presenter is { } presenter)
        {
            state = string.Format(
                CultureInfo.InvariantCulture,
                "active, {0}x{1}, display peak {2:0} nits{3}",
                presenter.Width,
                presenter.Height,
                presenter.Display.MaxNits,
                HdrRuntime.FinalShaderPatched ? string.Empty : ", scene limited to SDR range (final shader not patched)");
        }
        else
        {
            state = "inactive: " + (HdrRuntime.InactiveReason ?? (HdrRuntime.Armed ? "starting" : "hooks not installed, see client-main.log"));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "HDR {0}. paperwhite {1}, ui {11}, peak {2}, emissive {3:0.##}, highlight {4:0.##}, stars {9:0.##}, gamut {10:0.##}, gamma {5:0.##}, floatscene {6}, smoothsky {7}, dither {8}",
            state,
            config.PaperWhiteNits > 0f
                ? config.PaperWhiteNits.ToString("0", CultureInfo.InvariantCulture) + " nits"
                : HdrRuntime.Presenter is null
                    ? "from display (read when HDR is active)"
                    : config.EffectivePaperWhiteNits.ToString("0", CultureInfo.InvariantCulture)
                        + (config.DisplaySdrWhiteNits > 0f ? " nits (from display)" : " nits (default; the display reports no SDR white)"),
            config.PeakNits > 0f ? config.PeakNits.ToString("0", CultureInfo.InvariantCulture) + " nits" : "from display",
            config.EmissiveBoost,
            config.HighlightBoost,
            config.SdrGamma,
            config.FloatSceneBuffer ? 1 : 0,
            config.SmoothSkyGradient ? 1 : 0,
            config.Dither ? 1 : 0,
            config.StarBoost,
            config.GamutExpansion,
            config.UiNits > 0f ? config.UiNits.ToString("0", CultureInfo.InvariantCulture) + " nits" : "as paperwhite");
    }

    private void LoadConfig(ICoreClientAPI api)
    {
        HdrConfig? loaded = null;

        try
        {
            loaded = api.LoadModConfig<HdrConfig>(ConfigFile) ?? api.LoadModConfig<HdrConfig>(LegacyConfigFile);
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Could not read ModConfig/{0}, using defaults: {1}", ConfigFile, e.Message);
        }

        HdrRuntime.Config = loaded ?? new HdrConfig();
        HdrRuntime.Config.Sanitise();
        StoreConfig();
    }

    private void StoreConfig()
    {
        try
        {
            capi?.StoreModConfig(HdrRuntime.Config, ConfigFile);
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Could not write ModConfig/{0}: {1}", ConfigFile, e.Message);
        }
    }
}
