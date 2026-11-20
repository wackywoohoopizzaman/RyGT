# RyGTS DLSS plugin

An optional plugin for NVIDIA DLSS reconstruction in RyGTS.
NVIDIA's DLSS SDK can't be linked into the GPL renderer, so DLSS lives in this separate
MIT-licensed DLL. Since FEAT-GTS-287 the RyGTS package ships it, built by CI, as
`dlss\ryGT_dlss.dll`; NVIDIA's runtime is not shipped (see Install).

## Licensing

Two licences apply to the shipped `ryGT_dlss.dll`:

- **This source (`ryGT_dlss.cpp`, `rygt_dlss_api.h`, the tests): MIT**, RyGT-Team's code
  ([LICENSE](LICENSE), shipped in the package as `ryGT_dlss-LICENSE.txt`).
- **NVIDIA's NGX loader (`nvsdk_ngx_d.lib`), linked in as object code:** NVIDIA's, under the
  NVIDIA RTX SDKs License (the SDK's `LICENSE.txt`, shipped as `NVIDIA-RTX-SDKs-LICENSE.txt`). It is
  not MIT or GPL. It may be distributed only as object code inside an application, not on its own,
  and not reverse engineered.

That licence forbids putting the SDK under an open-source licence that would require its source,
derivative works or free redistribution. So NVIDIA's code goes only into this plugin, never into
the GPL renderer or runtime; the renderer loads the plugin at run time through `rygt_dlss_api.h`.
Keep it that way: don't link `nvsdk_ngx*.lib` or include NVIDIA's headers anywhere else. NVIDIA's
runtime DLLs are not shipped either: players download them with the launcher. The licence also asks
for attribution, which the launcher's About page and the README's credits give.

## What it does

Renderer patch `0020` supplies scene color, depth, motion vectors and camera jitter from the same
guest frame, before composing the HUD. The temporal interface requests preset M and requires a
DLSS runtime supporting that preset, such as DLSS 4.5. The DLSS checkbox enables this path in the
serial renderer for recognized SDR scene passes. Unsupported passes retain game rendering.

The game renders at up to 1920x1080. Reconstructing that scene to 3840x2160 has the Performance
ratio. There is no quality selector: changing the NGX profile does not change guest render
resolution. Frame generation remains a separate option.

The version 1 interface remains available to older renderers. It uses placeholder depth and
motion, no jitter and a history reset every frame, so it is a single-frame upscaler. Successful
quality selections are cached until the input/output dimensions or device session change.
Installing a newer runtime alone does not enable temporal reconstruction in an older renderer.

## Temporal interface

Version 2 uses guest frame IDs, input-pixel jitter and current-to-previous motion converted through
the supplied motion scales. Color and motion inputs use `SHADER_READ_ONLY_OPTIMAL`; depth also
accepts `DEPTH_STENCIL_READ_ONLY_OPTIMAL`. Output is a distinct RGBA16F storage image in `GENERAL`.
The caller owns resource lifetimes and barriers, serializes evaluations and finishes recorded GPU
work before changing feature dimensions or creation flags.

Successful consecutive frames retain history. First use, explicit resets, frame gaps, rewinds,
feature recreation and failed evaluations invalidate it. Duplicate presentations are not new
guest frames. `RygtDlss_GetOptimalSettings` returns the supported input range for an explicit
quality mode; inputs outside that range are rejected. Current renderer limitations are recorded
in [technical debt](../../docs/dev/TechnicalDebt.md#functional).

## Stereo frame generation

FEAT-RT-020 adds optional midpoint generation for GT7's stereo output. Each eye supplies its
finished color, prepared depth and motion, seated pose, and frustum tangents. The renderer owns
the images, layout transitions and submission timing. Reset on a discontinuity or recenter.
The two eye histories are independent of desktop reconstruction. The indexed entry accepts
one midpoint (count and index both 1), and the original midpoint entry uses the same path.

## Build

You need clang-cl, Visual Studio's C++ libraries, NVIDIA's DLSS SDK
(<https://github.com/NVIDIA/DLSS>, the `include` and `lib\Windows_x86_64\x64` folders) and the
Vulkan headers (shadGT's `externals\vulkan-headers` by default).

```
set DLSS_SDK=C:\path\to\DLSS
build.cmd C:\path\to\output
```

CI builds it in the Renderer target of `ci/build-windows.ps1` (GT7's package: `ci/rygt7/build-windows.ps1`),
against the SDK files pinned by checksum in `ci/runtime-toolchain.json` (`dlss_sdk`: NVIDIA DLSS SDK
v310.9.16, its headers and `nvsdk_ngx_d.lib`), and the packager places it, alone, in the package's
`dlss` folder.

## Install

The package's `dlss` folder holds `ryGT_dlss.dll` and nothing else. NVIDIA's runtime
(`nvngx_dlss.dll`, and for frame generation `nvngx_dlssg.dll` and Streamline's `sl.*.dll`) comes
from the launcher: download it there, or import your own files with "Add DLSS files...", while the
game is closed. The previous runtime is kept in a timestamped `.bak` file. A plugin you built
yourself goes in the same place, `dlss\ryGT_dlss.dll`.

Tick "NVIDIA DLSS" (available when both files are present), or set `"dlss_enabled": true` in the
`GPU` section of `user\custom_configs\CUSA03220.json`. Changes apply at the next game launch.
The half-rate mirror patch is disabled while temporal DLSS is selected to preserve motion data.

`rygt_dlss_api.h` is the interface between the renderer and the plugin; shadGT's
`ryty_gpu/rygt_dlss_api.h` must stay identical.

## Tests

The mode-cache and temporal-history tests need a C++17 compiler and CMake, without the DLSS SDK
or an RTX GPU:

```
cmake -S plugins/dlss/tests -B build/dlss-tests
cmake --build build/dlss-tests --config Release
ctest --test-dir build/dlss-tests -C Release --output-on-failure
```

They are also registered by the root project when `BUILD_TESTING` is enabled.
