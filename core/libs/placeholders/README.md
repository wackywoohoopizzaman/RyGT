# Placeholder libraries

GT Sport and GT7 link against PS4 libraries that RyGTS does not implement: NP and PSN dialogs, USB, VR,
camera, trophies, FreeType helpers and others. The converted game executable (`GTSport.exe`, `GT7.exe`) loads
`libs/<library>.prx` for each of them when it starts, and stops with "Failed to load module" if one is
missing. These placeholders provide them.

Each `<library>.nids` lists the functions the game imports from that library, one `<library> <NID>` per
line (a few libraries, such as `libSceMbus`, also answer for functions of other libraries).
`generate.py` turns a list into C source and a module definition. Every function prints
`[stub] <library> <NID>` to stderr the first time it is called and returns 0. An empty list makes a
library that exports only `ryty_stub_none`: the game loads it but calls nothing in it.

The lists were recovered from the placeholder binaries in the former October 25 (GT Sport) and October 31
(GT7) runtime archives, whose source had not been kept. `libMozaSDK-PS4`, `libSceAppContentUtil` and
`libSceVideoRecording` are GT7-only and empty.

FEAT-GTS-291: both games' packages ship the one `libSceMbus.prx` built here, so both games can share one
runtime folder. GT7's package used to take `runtime/gt7`'s catch-all stub instead, which differed in two
ways, both kept now:

- No `libSceAudio3d` NIDs: when `libSceMbus` answered them with stubs GT7 had no sound. They now reach
  the renderer's `libSceAudio3d.prx` (which both packages ship) under GT Sport too.
- `libSceNpManager 8Z2Jc5GvGDI` returns SCE_NP_ERROR_SIGNED_OUT (0x80550006) under GT7 and 0 under GT
  Sport, written `gt7=0x80550006` after the NID: a line may end in `gt7=<value>`, the function's result
  under GT7 (the game is told apart as `core/libs/GameSerial.hpp` does).

Otherwise the list is the same as GT7's stub, export for export.

FIX-RT-003: a line may end in `wait=timeval` instead: the function's first argument points to a PS4
timeval and it sleeps that long (at most 100 ms) before returning 0. `libSceUsbd +wU6CGuZcWk`
(`sceUsbdHandleEventsTimeout`) is one: GT7's `USBEV` thread calls it in a loop, and while it returned at
once that thread spun one core for the whole session.

FIX-RT-018: the wheel module in the CUSA24769 01.00 conversion also imports
`libSceUsbd EkqGLxWC-S0`. Export it from the USB placeholder so the module can load.

To implement one of these libraries for real, add it under `core/libs/prx/` and delete its list here.

`libSceSsl2.prx` is not a placeholder: it is the PS4's second module name for the libSceSsl API, so the build
copies the NID-patched `libSceSsl.prx` to it (`core/libs/CMakeLists.txt`).
