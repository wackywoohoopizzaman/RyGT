# RyGT Launcher changelog

The launcher (RyGTLauncher.exe, called RyGTS.exe before 0.3.1) is versioned on its own with semantic versioning
(MAJOR.MINOR.PATCH), separately from the game, emulator and release packages.
The version lives in `loader/Version.cs`. To release a change, bump
`LauncherVersion.Value` there and add a section here with the same number;
CI fails when the two disagree.

## 0.3.13 - 2026-11-20

* FEAT-RT-021: GT7 offers per-eye resolution, desktop mirroring, reconstruction, 2× frame
  generation and Reflex with an OpenXR renderer. Stereo cockpit mode uses both eyes for visibility.

## 0.3.12 - 2026-11-20

* FIX-RT-017: Set up accepts stripped SELF files whose section table was removed.

## 0.3.11 - 2026-11-20

* FIX-RT-016: GT7 shows its blue picture in a fresh RyGT package too. launcherackground.png and logo.png
  (GT Sport's, which the package ships) went to both games while the folder had no game exe yet, before
  Set up; they are GT Sport's now unless the folder holds only GT7.exe (a ryGT7 release).

## 0.3.10 - 2026-11-20

* FIX-GTS-526: GT Sport's Graphics page says "PS4 Pro Mode", no longer "PS4 Pro Mode (BROKEN)".

## 0.3.9 - 2026-11-20

* FIX-GTS-491: After a GPU device loss, GT Sport and GT7 are no longer started again by the launcher.
  The interrupted session could not be resumed anyway; the restart hid the crash from the player
  and ran a second session under the first one's launch. The game's setup log
  (`logs\<serial>\setup.log`) now says when the device was lost and which log holds that session
  (its `stdout.log`, renamed `stdout.log.<UTC time>` at the next start).

## 0.3.8 - 2026-11-20

* FIX-RT-015: the packages are RyGT now: nightlies are RyGT-ci-<commit>.zip (RyGTS-ci- before) and the CI
  workflows are "RyGT Release/Diagnostics development build". The updater looks for RyGT-ci- nightlies and reads
  both RyGT- and the older RyGTS- package names, so installed folders still show their version.
  The repository is RyGT-Team/RyGT now (RyGT-Team/RyGTS before): the updater and the CI workflows use the
  new name (the workflows compared github.repository with the old one, so a push built development's tip).

## 0.3.7 - 2026-11-20

* FIX-RT-008: GT7 showed GT Sport's red picture in a shared folder: launcher\background.png (which the
  RyGTS package ships) and launcher\logo.png were used for both games. They are GT Sport's now while
  GTSport.exe is in the folder (a ryGT7-only folder keeps them for GT7); each game's own pictures go in
  launcher\<serial>, and GT7 otherwise shows its built-in blue picture again.
* FIX-RT-009: GT7's About page has GT Sport's notice too, with "Copyright © 2026 RyGT-Team."
* FIX-RT-010: one name and one icon for both games. The home caption no longer ends in "RYGTS FOR WINDOWS"
  or "RYGT7 FOR WINDOWS"; the version line, About, sidebar, folder rows, update notices, the update and
  roll back dialogs, the Changelog title and nightlies ("RyGT nightly") say RyGT. The window keeps the
  RyGT icon for GT7 too (GT7's own icon stays beside the player's ID). GT7's sidebar has Advanced above
  About, as GT Sport's does.
* FIX-RT-011: GT7's home caption and version line name its update ("UPDATE 1.71", "GT7 1.71": the
  VERSION in the game's param.sfo) instead of "PS4".
* FIX-RT-012: GT7 starts with SHADGT_SCRIPTS=0: patches\scripts holds GT Sport's replacement scripts
  (GALLERY.ADC, UI_KIT.ADC...), and in a shared folder the renderer served them to GT7 too.

## 0.3.6 - 2026-11-20

* FIX-RT-007: `--game` (snapshots and `--setup --game`) used the GT7 folder an older launcher saved in
  RyGT-Launcher.ini, so `--setup --game gt7` would have set GT7 up there instead of in the launcher's folder.
  It now uses the launcher's folder like the window does. And GT7 no longer reads GT Sport's
  user\custom_configs\CUSA03220.json when its own CUSA24769.json is missing and GTSport.exe shares the folder
  (the runtime's GameSerial.hpp; only a GT7 folder from before GAME-004 falls back to it).
  The Game page names a former GT7 folder only when it still holds GT7's files or saves.

## 0.3.5 - 2026-11-19

* FEAT-RT-006: RyGTS packages carry GT7's files too (libs\libSceTextToSpeech.prx, patches\CUSA24769.xml, and
  the server: RyGTServer.exe, libzstd.dll, shadNet and its data), so one RyGTS folder runs both games and
  GT7 updates with it. An update never replaces server\shadnet\shadnet.cfg, which holds the keys the
  Online page made. The Game page's GT7 hint names the update as the way to get them.

## 0.3.4 - 2026-11-19

* FEAT-RT-005: GT7 no longer has a folder of its own. Both games use the launcher's folder, since every
  file either game writes there follows its serial; the Game page's GT7 folder field is gone. GT7 is
  ready once that folder has GT7's files (libs\libSceTextToSpeech.prx, patches\CUSA24769.xml). A GT7
  folder an older launcher saved is no longer used; the Game page names it, so its saves and settings
  can be copied over.

## 0.3.3 - 2026-11-19

* FIX-GTS-271 (from rygt7 7dbe4e2, which came after the merged 6178d0b): GT7's online-service keys
  are no longer in RyGTServer or tools/vegas_decode.py. RyGTServer reads them from your own GT7
  eboot.bin, which the Online page passes when it starts hosting; without it, GT7's online service
  stays off. GT Sport is not affected.

## 0.3.2 - 2026-11-18

Brings 0.2.3 and 0.2.4 from development (the merge into FEAT-GTS-291's branch):

* FIX-GTS-294: Set up and Play no longer make the offline livery and decal scripts from the game's
  files. Reading them needed the game volumes' decryption keys, so adcpatch's all and extract
  commands and its GTToolsSharp dependency are removed too. Scripts already in patches\scripts
  are still used; adcpatch can still patch scripts you supply yourself.
* FIX-GTS-293: Set up and Play no longer copy shader programs out of the game's files. That step
  needed the game volumes' decryption keys, so adcpatch's shader commands (shaderindex,
  shadercodes, manifestpack, manifestprep, shaderpack, shaderprep) are removed too. Shaders now
  compile the first time they are needed, and the shader folder keeps them for later starts.

## 0.3.1 - 2026-11-15

* FEAT-GTS-291: The launcher is RyGT Launcher, RyGTLauncher.exe, for both games: the window title
  ("RyGT Launcher - Gran Turismo Sport", "RyGT Launcher - Gran Turismo 7"), the start screen's version
  line, Settings > About and the exe's version information name it. The releases keep their names:
  RyGTS (GT Sport's) and ryGT7 (GT7's). Both packages ship RyGTLauncher.exe instead of RyGTS.exe and
  RyGT7.exe.
* FEAT-GTS-291: Both games may share ONE folder (GTSport.exe and GT7.exe side by side): every name a game
  writes there follows its serial (CUSA03220 GT Sport, CUSA24769 GT7). The settings file is
  `<serial>.RyGT.ini` (CUSA03220.RyGT.ini, CUSA24769.RyGT.ini), read by the launcher and the runtime; GT7's
  RyGT7.ini and the RyGT.ini it mirrored into (with online play's keys) become one CUSA24769.RyGT.ini.
  Set up works in `convert\<serial>`, the game's /temp0 and /data are `temp0\<serial>` and `data\<serial>`
  (GT7's offline catalog `data\CUSA24769\gt7offline`), and the launcher's setup lines go to
  `logs\<serial>\setup.log`, no longer setup.log in the folder. At start and before Set up and Play the
  launcher moves a folder's old names (libc does the same at load for a game started directly), only
  where they are clearly the game's: RyGT.ini is renamed, GT7's two files merged and kept as
  `.moved-<date>`, folders moved into `<name>\<serial>` in three steps that a later start finishes if cut
  short. Nothing is deleted or overwritten; each step is in the setup log. Both packages ship the same
  libs\libSceMbus.prx. GT Sport may use a ryGT7 folder, and GT7's folder may be GT Sport's.
* FEAT-GTS-291: A folder updated by an older launcher still holds RyGTS.exe or RyGT7.exe. At start,
  RyGTLauncher.exe renames an older launcher in its own folder to RyGTS.exe.old or RyGT7.exe.old, only
  when the file's version information names the old launcher (product RyGTS or ryGT7); GTSport.exe,
  GT7.exe and other files are never touched. Players keep one launcher, and a shortcut to the old name
  fails instead of starting an outdated launcher: point it at RyGTLauncher.exe. A failed rename is
  written to the setup log and nothing else changes. Rolling back to a release from before
  RyGTLauncher.exe puts the old launcher back and starts it.
* FEAT-GTS-291: One updater for both games. GT7 uses the same updater as GT Sport (RyGT-Team/RyGTS
  releases on GitHub, Stable, Prerelease and Nightly channels, Changelog, roll back), with one Updates
  section in Settings > About; ryGT7's GitLab updater is gone. It updates the selected game's folder,
  and only from a package that has that game's files (GT Sport's CUSA03220.json, GT7's CUSA24769.xml),
  so one game's runtime is never installed over the other's. A ryGT7 folder from GitLab is shown with
  its version but isn't updated until a RyGTS package for GT7 is installed in it.

## 0.3.0 - 2026-11-15

* FEAT-GTS-290: One launcher for Gran Turismo Sport and Gran Turismo 7. The home view has a Game list;
  picking a game fades the launcher's accent (red GT Sport, blue GT7) and its pictures, and the settings
  become that game's. GT7 brings ryGT7's pages (Display, Graphics, Controls, Account, Online), its Set up
  (Offline mode patch) and its updates (GitLab). Each game keeps its own folder, exe, settings file
  (RyGT.ini, RyGT7.ini), launch environment and logs; the Game page lists both games' folders and files.
  RyGT-Launcher.ini beside RyGTS.exe remembers the game picked last and each game's folder; nothing in a
  GT Sport folder changes. Shared pages: Game, Shaders, About (with Updates), Advanced. `--setup` and
  `--snapshot` take `--game gts|gt7`. See loader/UNIFIED-LAUNCHER.md.

## 0.2.2 - 2026-11-15

* FEAT-GTS-287: Settings > About carries NVIDIA's DLSS attribution, as the NVIDIA RTX SDKs License
  asks of software shipping its NGX loader: the package now includes the DLSS plugin
  (dlss\ryGT_dlss.dll), which contains it. NVIDIA's runtime still comes from the download button.

## 0.2.1 - 2026-11-15

* FIX-GTS-286: A Changelog button beside Update opens the update's release notes as a list (headings,
  bullets and nested bullets, wrapped). Settings > About > Updates shows the whole installed version
  ("Launcher 0.2.1 (nightly 2a662886)"); its closing parenthesis was cut off.
* FIX-GTS-286: The updater found no GitHub release: it looked for tags starting gts- and packages
  named RyGTS-*.zip, and the releases are published as gt-sport-2026-11-15 with
  GTSport-Ryty-shadGT-2026-11-15.zip. It now takes both names, and the package with a .sha256 beside
  it. A build from the same commit as the installed one is not offered again on another channel
  (today's release is the newest nightly). Packages whose BUILD_INFO.txt names the commit
  source_commit are recognised. The update channel is Stable by default now that RyGTS publishes
  releases (it was Nightly while there were none).

## 0.2.0 - 2026-11-14

* FEAT-GTS-284: Shaders from the shared shader manifest. When tools\adcpatch\shader-manifest.bin is
  there (adcpatch manifestpack: the records of shader translations and the pipeline recipes RyGT's
  players have built, no shader code, and the index of where each program is in the game's files), Set
  up, and a start after a newer one is shipped, writes it into the shaders folder and copies each
  recorded program out of this PC's game files (adcpatch manifestprep; about a minute the first time,
  seconds after). The game is then started with shader pre-translation on, so the shaders are translated
  and their pipelines built on the splash screen (shadGT FEAT-GTS-470/471).
* FEAT-GTS-285: "Rebuild translated shaders in the background after a renderer update" is on by default,
  as shader pre-translation is in the renderer now (shadGT FEAT-GTS-478); unchecking it turns it off.

## 0.1.8 - 2026-11-13

* FEAT-GTS-280: Controls has a new HD rumble & adaptive triggers window (it replaces the Trigger strength
  slider, which moved into it). A slider per effect, 0-200% with 0 turning it off: HD rumble overall, engine,
  road and kerbs, wheel slip, gear changes, rev limiter and impacts; trigger strength, brake and accelerator
  resistance, how much the resistance follows speed, braking and revs, ABS (wheel-lock) and wheelspin
  vibration. Changes apply while playing. Each effect has a Test button that plays it on the DualSense
  through tools\ryty-feel-test.exe (from the renderer), game running or not.

## 0.1.7 - 2026-11-13

* FEAT-GTS-277: The Advanced page splits the switches under a header row per prefix: Renderer (SHADGT_),
  Renderer and runtime (SHARED_), Runtime (RYGT_), each over its groups. Its count line names the
  renderer build by its git describe (shadGT FEAT-GTS-417) instead of the file's date, which stays for older builds.
* FEAT-GTS-276: On the Advanced page, a switch that takes only a fixed set of values (kind `choice` in
  the renderer's table, shadGT FEAT-GTS-416) is a dropdown instead of a text box where other values were
  ignored: Default, then each value with its meaning. It is saved as soon as a value is picked.
* FEAT-GTS-275: Switches are named after where they take effect: SHADGT_ for the renderer, RYGT_ for
  the runtime, SHARED_ for both (shadGT FEAT-GTS-415). The launcher sets and lists the new names (the
  Advanced page also finds SHARED_ switches). At start, switches saved under an old name the
  installed build no longer reads are listed in the obsolete-settings question ("Update obsolete
  settings?"): Yes renames them, No keeps them. A profile saved before loads with the new names.
  Family rows show the family's name without "_*".

## 0.1.6 - 2026-11-13

* FEAT-GTS-274: On the Advanced page, switches of a group whose names share their first two words
  (RYGT_PARALLEL_*, RYGT_NATIVE_*, SHADGT_DIAG_*, ...) sit under one family row, closed at first,
  instead of scattered through the list; in each group the families come first, then the other
  switches. The row shows how many switches it holds and how many are
  changed; a click anywhere on it opens or closes it, except on the row's own switch when the
  family is named after one (RYGT_NATIVE), which still flips as usual. Right and Left open and
  close it from the keyboard. Searching or "Only changed" opens the families with a match, and a
  column sort lists every switch flat.

## 0.1.5 - 2026-11-13

* FEAT-GTS-273: Settings > About shows the copyright notice ("Copyright © 2026 RyGT-Team.") and
  "Gran Turismo is a trademark of Sony Interactive Entertainment. This project is independent of
  Sony and Polyphony Digital."

## 0.1.4 - 2026-11-13

* FEAT-GTS-272: The launcher updates itself (from ryGT7's updater), on one of three channels
  (Settings > About > Updates > "Update channel"): Stable, the newest GitHub release; Prerelease,
  the newest release or prerelease; and Nightly (the default while RyGTS has no releases), the
  newest successful build of development. At start ("Check for updates at start", on by default)
  it asks GitHub for the newest build on the channel and, when it is not the installed one, the
  home view offers it with an Update button. Nightlies are downloaded through nightly.link
  (GitHub's own artifact download needs a signed-in account). The package is checked against its
  SHA-256 and every file against its checksum list, and installed over the program files; the
  game, saves, settings, profiles and caches are never touched. The replaced files are kept in
  update\backup-<version> for Roll back.

## 0.1.3 - 2026-11-13

* FEAT-GTS-271: The Advanced page also lists the runtime's switches (libkernel, libc,
  libSceAvPlayer, libSceUserService, libSceNpAuth, ...): it reads every `libs\*.prx`, not only the
  renderer, including names the runtime reads as UTF-16 (`RYGT_USER_NAME`, `RYGT_APP0_LAYERS`).
  Their descriptions come from shadGT's table like the renderer's. The page is built the first
  time it is opened, so the launcher starts as fast as before.

* FEAT-GTS-270: Profiles on the Advanced page. A dropdown of saved profiles with Save and Load
  next to it: Save keeps the current switches and other variables under a name
  (`launcher\profiles\<name>.ini`, the same `env=` lines as `RyGT.ini`), Load replaces them with a
  profile's, to swap between configurations instead of tracking several similar switches by hand.

## 0.1.2 - 2026-11-12

* FEAT-GTS-266: The Advanced page (the renderer's experimental switches) is hidden by default. It
  appears once "Show the Advanced page" is turned on under Logging (`show_advanced=1` in `RyGT.ini`);
  switches set there keep applying when it is hidden again.

## 0.1.1 - 2026-11-12

* New launcher look, ported from ryGT7's launcher: a full-window home view with the
  background pictures (slowly drifting, and fading between `launcherackground*.png`
  when there are several), the title or `launcher\logo.png`, Play and a settings button.
* The settings open on their own view with a sidebar of pages: Game (files, Set up and
  its log), Display, Graphics, Shaders, Controls, Advanced and About (folders, logging,
  and showing the Advanced page). The top tabs and the quick-access panel are gone.
* Settings are full-width rows with switches, drop-downs and sliders under spaced
  capital headings; buttons are outlined, and views change with a short fade.
* Every setting is kept with the same keys and files; only where it is shown changed.
* The native renderer is no longer experimental and is now the default renderer. Installs
  that never picked a renderer switch to it on the next Play; a saved choice of the old
  renderer is kept.
* When the game's config has entries nothing reads any more (so far `GPU.quality_preset`,
  replaced by System memory), the launcher asks on opening whether to remove them, with
  "Don't show again" (`prompt_obsolete` in `RyGT.ini`).
* Set up no longer links the game's files into `app0` (Explorer counted the links as a second
  copy of the game): the game reads them from the game and update folders. Links left by earlier
  versions are removed; copies are kept. The RyGTS folder can be on any drive.

## 0.1.0 - 2026-11-11

* First tracked launcher version.
* The version is in the exe's file properties (File version, Product version),
  in the launcher window, and on the first line of `setup.log`.
* CI packages record it as `launcher_version` in their build information.
