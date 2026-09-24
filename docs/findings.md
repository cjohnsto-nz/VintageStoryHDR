# Findings (Vintage Story 1.22.7, decompiled with ilspycmd)

## Presentation path

- `ScreenManager.Render`: clear Default and Primary, scene into Primary, post-processing,
  `RenderFinalComposition` (final.fsh, Luma[10] into Primary), `BlitPrimaryToDefault`, GUI
  drawn straight into framebuffer 0, then `window.SwapBuffers()` in `window_RenderFrame`.
- Primary colour 0 is `GL_RGBA8`. FindBright, GodRays and Luma are already `RGBA16F`, so
  promoting that one texture makes the chain float end to end.
- `final.fsh` clips three times: `min(color, 1)` after godrays, the HSL lightness clamp in
  `ColorGrade`, and the "Limit brightness" block.
- Glow channel (`outGlow.r`): per-surface emissive level. The sky writes 1, which is why
  the emissive boost is weighted by brightness.
- Framebuffer 0 is only ever bound through the `CurrentFrameBuffer` and
  `CurrentFrameBufferKeepVw` setters, reached from `LoadFrameBuffer(Default)`,
  `ClearFrameBuffer(Default)` and the tail of `CreateFramebuffer`. Screenshots
  `glReadPixels` from whatever is bound, so they read the redirect buffer and come out as
  clamped SDR.
- The game reloads all shaders right after `StartModsFully`, so a `ShaderRegistry.LoadShader`
  postfix installed in `StartClientSide` catches `final` before the first in-world frame.

## Dead ends

- **Postfix on `ClearFrameBuffer(EnumFrameBuffer)` as the frame-start hook.** It fired for
  every call site except the one in `ScreenManager.Render`: PGO had inlined it there.
  Frame start now hangs off `window_RenderFrame` (event delegate, not inlinable).
- **Swapchain on the game window itself.** GL has already presented to it; flip-model
  DXGI wants a window nothing else presents to. Hence the disabled `STATIC` child window.
- **Float pixel format for the GL window.** Needs the pixel format chosen before the
  window exists, i.e. a launcher, and is vendor-specific. The DXGI route needs neither.

## Verified on

RTX 3080 Ti, driver 616.92, Windows 11, 3840x2160 HDR: activates, right way up; GUI,
inventory item rendering, chat, escape menu, mouse and keyboard all work. Day-sky banding
fixes and the star boost confirmed by eye on the HDR display (v0.1.0).

RTX 5080 Laptop, driver 615.71.09, CachyOS, KDE Plasma 6.7.5 (Wayland, HDR on, scale 100%),
2560x1600, game on native Wayland: activates as HDR10 via Vulkan on a Wayland subsurface;
brighter highlights, finer texture detail, mouse, resize, F11, options menu and minimise all
work, no FPS loss. Works alongside SheyderMod 1.1.3 (both shader patches still apply) and
Komet v1.2.0-pre.3 (it logs a harmless patch-collision warning on `window_RenderFrame`).
Peak and full-frame luminance come from the compositor's preferred image description
(`wp_color_management_v1`): KWin reported 1200 / 900 nits and 604 nits SDR white, i.e. the
overrides from its display settings rather than the panel's EDID figures. KWin maps the PQ
default reference white (203 nits) to its SDR white, so the HDR10 output is scaled by
203 / SDR white to show the nits it encodes; paper white and UI white default to that SDR
white on Linux, so toggling HDR leaves overall brightness alone. UI colours look very
slightly less saturated with HDR on, most likely KWin's "SDR gamut wideness" (100% here)
stretching the SDR path only. Fractional
display scaling is not supported (falls back to SDR with a message) and has not been tested.

RX 7900 XTX (radeonsi / RADV), Mesa 26.2.3, CachyOS, KDE Plasma (Wayland), game on native
Wayland, seen through a Moonlight stream of a Hermes-KMS HDR virtual output (1920x1080, KDE
reporting 800 nits peak / 200 nits SDR white): activates as HDR10 via Vulkan on a Wayland
subsurface, `.hdr off` / `on` toggles cleanly, no GL errors or fallbacks. Colour accuracy not
judged -- the stream was too soft for that; the physical display has not been tested yet.

## Linux: the game on native Wayland

HDR swapchain colour spaces are only offered for Wayland surfaces; an XWayland window gets
SDR formats only, and `VK_LAYER_hdr_wsi` only hooks `vkCreateWaylandSurfaceKHR`. The game
runs on X11/XWayland by default and needs GLFW's Wayland backend selected before `Main`
(a .NET startup hook), and on Wayland 1.22.7 then crashes intermittently on the loading
screen with `KeyNotFoundException: 'lightPosition'`: the compositor's initial configure
makes `Window_Resize` call `RebuildFrameBuffers()`, whose read of
`ShaderRegistry.SupressShaderAndBufferReloads` runs `ShaderRegistry`'s static constructor
early, publishing an uncompiled `ShaderPrograms.Gui` that the loading screen then uses.
Reported upstream on VintageStory-Issues #9184 (same crash as #8807).
