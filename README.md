# Native HDR for Vintage Story

Real HDR output for Vintage Story on Windows and on Linux (Wayland). Torches, lava, forges, the sun, lightning
and the stars go above paper white instead of clipping at it; the GUI stays where you put
it. It is an ordinary client mod: no launcher, injector, wrapper DLL or ReShade.

This is not Auto HDR or an inverse tone-mapping filter over the finished image. The mod
keeps the game's scene in floating point, stops the final shader from clipping, and uses
the game's own emissive channel to decide what is a light source.

## Requirements

**Windows**

- Windows 10 1709 or later, with **Use HDR** turned on for the display (Settings > System > Display).
- An HDR display, and a GPU driver that implements `WGL_NV_DX_interop2`. Current NVIDIA,
  AMD and Intel drivers all do. Developed and tested on an NVIDIA RTX 3080 Ti.

**Linux**

- A Wayland session whose compositor implements `wp_color_management_v1` with HDR, with HDR
  turned on for the display. Tested on KDE Plasma 6.7.
- The game running as a **native Wayland** window. By default it runs through XWayland,
  where no HDR output exists; [vintagestory-wayland](https://github.com/Seralth/vintagestory-wayland)
  starts it on Wayland and also fixes the loading-screen crash the game hits there.
- Vulkan and OpenGL drivers with `VK_EXT_swapchain_colorspace`, `VK_KHR_external_memory_fd`,
  `VK_KHR_external_semaphore_fd`, `GL_EXT_memory_object_fd` and `GL_EXT_semaphore_fd`.
  Tested on an NVIDIA RTX 5080 laptop (driver 615) and an AMD RX 7900 XTX (Mesa 26.2).

Both need Vintage Story 1.22. Client-side only. Works in single player and on any server;
the server does not need it. On macOS, and on Linux under X11/XWayland, the mod loads, says
so once in the log, and does nothing.

## Install

Download `hdr-x.y.z.zip` from [Releases](../../releases) and put it, still zipped, in
`%APPDATA%\VintagestoryData\Mods` (Linux: `~/.config/VintagestoryData/Mods`). Join a world. HDR starts with the first in-world frame;
the main menu stays SDR because the game does not load mods there.

Type `.hdr` in chat to check it is active.

Upgrading from 0.2.0 or earlier: the mod id changed from `vshdr` to `hdr`, so delete the old
`vshdr-*.zip` from your Mods folder -- otherwise both copies load. Your settings carry over.

## Tuning

Everything is live and saved to `ModConfig/hdr.json`.

| Command | Meaning | Default |
| --- | --- | --- |
| `.hdr` | Status | |
| `.hdr on` / `.hdr off` | Toggle, or retry after a failure | on |
| `.hdr paperwhite <nits>` | Luminance of SDR white in the scene: a fully lit white block; 0 = the system's SDR white where it reports one (Linux), else 300 | 300 (Linux: 0) |
| `.hdr ui <nits>` | Luminance of white in the GUI; 0 = same as `paperwhite` | 400 (Linux: 0) |
| `.hdr peak <nits>` | Brightest output; 0 = what the display reports | 0 |
| `.hdr emissive <x>` | Boost for emissive surfaces: torches, lava, sun, lightning | 10 |
| `.hdr highlight <x>` | Boost for non-emissive highlights that were about to clip | 0.75 |
| `.hdr gamut <0..1>` | Push vivid scene colours from Rec.709 towards P3; neutrals and the GUI do not move | 0.5 |
| `.hdr stars <x>` | Boost for the night sky's stars | 4 |
| `.hdr gamma <g>` | SDR decoding gamma | 2.2 |
| `.hdr floatscene <0 or 1>` | RGBA16F scene buffer and bloom chain | 1 |
| `.hdr smoothsky <0 or 1>` | Linear filtering on the sky gradient texture | 1 |
| `.hdr dither <0 or 1>` | One-code-value dither at 10-bit PQ | 1 |

Start with `paperwhite` for the overall scene brightness, then `ui` if you want the HUD,
inventory and chat dimmer (or brighter) than the world, then `emissive` to taste. If your display reports an implausible peak (the activation line in
`client-main.log` shows what it reported), set `.hdr peak` to its real figure so the
highlight roll-off lands in the right place.

## Troubleshooting

`.hdr` says why when it is inactive, and the same reason is in `client-main.log` on a line
starting `[hdr]`.

- **"Windows reports this display as SDR"** -- turn on Use HDR for the monitor the game is
  on, then `.hdr on`.
- **"wglDXOpenDeviceNV failed"** -- OpenGL and Direct3D are on different GPUs, typical of
  hybrid-graphics laptops. Set Vintage Story to the high-performance GPU in Windows
  graphics settings.
- **"on Linux, HDR output needs the game running on native Wayland"** -- start it through
  [vintagestory-wayland](https://github.com/Seralth/vintagestory-wayland).
- **"The compositor does not offer HDR10 for the game window"** -- turn on HDR for the
  display in the system settings, then `.hdr on`.
- **"Vulkan has no device matching the GPU OpenGL runs on"** -- hybrid graphics: run the
  game on one GPU (e.g. `prime-run`).
- **"game internals this mod hooks have moved"** -- a game update changed something the
  mod depends on. The game runs normally in SDR; check for a mod update.
- Whatever goes wrong, the mod falls back to the game's normal presentation rather than
  leaving you with a black screen.

## How it works

OpenGL on Windows cannot ask for an HDR surface, but DXGI can, and `WGL_NV_DX_interop2`
lets GL render into a D3D11 texture.

1. **Float scene.** The scene colour buffer and the bloom blur chain are RGBA8 in vanilla;
   they are re-specified as RGBA16F so values above 1.0 survive to the final pass.
2. **Shaders.** `final.fsh` and `nightsky.fsh` are patched at load, at named anchors. The
   final pass stops clipping, grades without flattening headroom, and expands highlights
   in linear light, weighted by the game's glow channel. It also widens the gamut of vivid
   colours towards P3, weighted by saturation and luminance-preserving; colours outside
   Rec.709 are carried as negative components, which float buffers and scRGB allow. Everything added is behind
   uniforms that default to off, where the shaders compute exactly what vanilla does.
3. **Redirected default framebuffer.** The game blits the scene to framebuffer 0 and draws
   the GUI over it. Framebuffer 0 is replaced by an RGBA16F framebuffer holding the same
   display-referred, gamma-encoded signal as vanilla -- so the GUI blends identically and
   screenshots still work -- except that scene highlights may exceed 1.0.
4. **Present.** At the end of the frame that buffer is decoded, scaled to paper white,
   rolled off towards the display's peak, dithered, and written as scRGB into a D3D11
   texture shared with GL. A flip-model DXGI swapchain on a disabled child window (input
   still goes to the game) presents it, replacing the GL buffer swap.

**On Linux**, EGL on Wayland cannot ask for an HDR surface either, but a Vulkan swapchain can,
and the compositor honours its colour space. Step 4 then writes PQ-encoded Rec.2020 into a GL
texture backed by Vulkan memory (`GL_EXT_memory_object_fd`), hands it over with shared
semaphores (`GL_EXT_semaphore_fd`), and Vulkan blits it into an HDR10 swapchain on a Wayland
subsurface covering the game window -- input-transparent and desynchronised, the Wayland
counterpart of the disabled child window. The display's peak and SDR white come from the
compositor's colour-management feedback and follow the window across displays. KWin maps
the PQ default reference white (203 nits) to its own SDR white, so the output is scaled by
203 / SDR white to show the nits it encodes.

Vanilla's 8-bit output hides some 8-bit sources that HDR exposes. Two are fixed along the
way: the sky gradient texture is sampled with nearest filtering in vanilla, and Windows
converts scRGB to the display's 10-bit signal without dithering.

[docs/findings.md](docs/findings.md) has the game internals this rests on, and the dead ends.

## Limitations

- Windows, and Linux on native Wayland. The main menu is SDR. Toggling Windows HDR mid-session needs `.hdr off` then `.hdr on`.
- Linux: fractional display scaling is not supported yet (the mod falls back to SDR and says so).
- Adaptive vsync is treated as vsync on. Vsync off uses tearing presents where supported.
- Gamut expansion is a stylistic stretch towards P3: the game's art is authored in sRGB, so there is no "true" wide-gamut colour to recover. `.hdr gamut 0` keeps everything inside Rec.709.
- Screenshots are SDR, and capture the scene at its brightness relative to the GUI: with `ui` below `paperwhite` the scene in a screenshot is brighter than vanilla and its highlights clip.
- Mods that replace `final.fsh` wholesale, or bind framebuffer 0 with raw GL calls, will not mix.
- Overlays that hook OpenGL's buffer swap will not see frames; ones that hook DXGI (Windows) or the Vulkan swapchain (Linux) will.

## Build

Needs the .NET 10 SDK, PowerShell 7 and an installed copy of the game (found in
`%APPDATA%\Vintagestory`, or wherever `VINTAGE_STORY` points).

```powershell
pwsh ./package.ps1            # tests, Release build, artifacts/hdr-<version>.zip
pwsh ./deploy.ps1 -StopGame   # build and install straight into the game's Mods folder
```

The tests include two that drive the real DXGI runtime and a real OpenGL context, so they
need a GPU and a desktop session.

On Linux, without PowerShell, build the mod directly and zip the `mod` folder's contents:

```sh
VINTAGE_STORY=/path/to/game dotnet build src/VintageStoryHDR/VintageStoryHDR.csproj -c Release
cd src/VintageStoryHDR/bin/Release/net10.0/mod && zip ../hdr-linux.zip *
```

The tests target Windows: `HdrConfigTests` expects the Windows defaults, and the
presentation tests need DXGI.
