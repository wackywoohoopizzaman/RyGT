// FEAT-GTS-290: checks of the launcher for both games: RyGT-Launcher.ini (the game picked last and each
// game's folder), each game's settings file read and written as its own launcher did, the accent
// colours, and the pages built for each game and after switching. Built with test-launcher.cmd.
// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RyGT
{
    static class UnifiedLauncherTests
    {
        static int checks;
        static void Check(bool value, string message)
        { checks++; if (!value) throw new Exception(message); }

        static string Folder(string root, string name)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        static void StateChecks(string root)
        {
            // A RyGTS folder: GT Sport is the default. FEAT-RT-005: both games' folder is the launcher's,
            // and GT7 is ready there once GT7's files are in it.
            string gts = Folder(root, "RyGTS");
            var state = new LauncherState(gts);
            Check(state.Last == GameKind.GTSport, "A RyGTS folder starts with GT Sport");
            Check(state.RootOf(GameKind.GTSport) == gts && state.RootOf(GameKind.GT7) == gts, "Both games' folder is the launcher's");
            Check(state.Ready(GameKind.GTSport) && !state.Ready(GameKind.GT7), "Only GT Sport is ready without GT7's files");
            Check(state.FormerGt7Root() == null, "No former GT7 folder");
            Check(!File.Exists(Path.Combine(gts, LauncherState.FileName)), "Reading writes nothing");
            Directory.CreateDirectory(Path.Combine(gts, "libs"));
            File.WriteAllText(Path.Combine(gts, "libs", "libSceTextToSpeech.prx"), "");
            Check(new LauncherState(gts).Ready(GameKind.GT7), "GT7 is ready once its stubs are in the launcher's folder");
            File.Delete(Path.Combine(gts, "libs", "libSceTextToSpeech.prx"));
            // A GT7 folder an older launcher saved (gt7_root): ignored, shown as the former folder; other lines survive.
            string gt7 = Folder(root, "ryGT7");
            File.WriteAllText(Path.Combine(gt7, "RyGT7.ini"), "game=\n");
            File.WriteAllText(Path.Combine(gts, LauncherState.FileName), "# a comment\nfuture=1\ngt7_root=" + gt7 + "\n");
            state = new LauncherState(gts);
            state.SetRoot(GameKind.GT7, gts + "\\other");
            Check(state.RootOf(GameKind.GT7) == gts && state.FormerGt7Root() == gt7, "A saved GT7 folder is ignored and named as the former one");
            state.Last = GameKind.GT7;
            state.Save();
            var text = File.ReadAllText(Path.Combine(gts, LauncherState.FileName));
            Check(text.Contains("game=gt7") && text.Contains("gt7_root=" + gt7) && text.Contains("gts_root=\r\n"), "Saved: game, the old GT7 folder kept as it was");
            Check(text.Contains("# a comment") && text.Contains("future=1"), "Unknown lines kept");
            var again = new LauncherState(gts);
            Check(again.Last == GameKind.GT7 && again.RootOf(GameKind.GT7) == gts, "Read back");
            // GT Sport's folder is the launcher's own and can't be changed outside a transient session.
            again.SetRoot(GameKind.GTSport, gt7);
            Check(again.Setting(GameKind.GTSport) == "" && again.RootOf(GameKind.GTSport) == gts && again.Ready(GameKind.GTSport),
                  "GT Sport's folder stays the launcher's");
            File.WriteAllText(Path.Combine(gts, LauncherState.FileName), "game=gts\ngts_root=" + gt7 + "\n");
            Check(new LauncherState(gts).RootOf(GameKind.GTSport) == gts, "A saved GT Sport folder is ignored");
            // FIX-RT-007: --game (snapshots, --setup --game) is transient, and it ignores saved folders too.
            File.WriteAllText(Path.Combine(gts, LauncherState.FileName), "game=gt7\ngts_root=" + gt7 + "\ngt7_root=" + gt7 + "\n");
            var forced = new LauncherState(gts) { Transient = true };
            Check(forced.RootOf(GameKind.GT7) == gts && forced.RootOf(GameKind.GTSport) == gts && forced.FormerGt7Root() == gt7,
                  "--game ignores the saved folders and still names the former GT7 folder");
            string empty = Folder(root, "emptyGT7");
            File.WriteAllText(Path.Combine(gts, LauncherState.FileName), "gt7_root=" + empty + "\n");
            Check(new LauncherState(gts).FormerGt7Root() == null, "An empty former GT7 folder is not named");
            // A transient session may still point GT Sport elsewhere. FEAT-GTS-291: GT Sport may use the
            // ryGT7 folder too, every name either game writes there follows its serial.
            var pointed = new LauncherState(gts) { Transient = true };
            pointed.SetRoot(GameKind.GTSport, gt7);
            Check(pointed.Ready(GameKind.GTSport), "GT Sport in a ryGT7 folder is ready");
            // The launcher inside a ryGT7 folder: GT7 is the default, and both games' folder is the launcher's.
            var inside = new LauncherState(gt7);
            Check(inside.Last == GameKind.GT7 && inside.RootOf(GameKind.GT7) == gt7 && inside.RootOf(GameKind.GTSport) == gt7 &&
                  inside.Ready(GameKind.GT7) && inside.Ready(GameKind.GTSport), "A ryGT7 folder starts with GT7, and GT Sport may share it");
            // One folder for both, as set: GT7's folder set to GT Sport's (the launcher's), both ready.
            var shared = new LauncherState(gts) { Transient = true };
            File.WriteAllText(Path.Combine(gts, "GTSport.exe"), "MZ");
            File.WriteAllText(Path.Combine(gts, "GT7.exe"), "MZ");
            shared.SetRoot(GameKind.GTSport, gts); // the RyGT-Launcher.ini above names another one
            shared.SetRoot(GameKind.GT7, gts);
            Check(shared.RootOf(GameKind.GT7) == gts && shared.RootOf(GameKind.GTSport) == gts &&
                  shared.Ready(GameKind.GT7) && shared.Ready(GameKind.GTSport), "Both games in one folder are ready");
            File.Delete(Path.Combine(gts, "GTSport.exe"));
            File.Delete(Path.Combine(gts, "GT7.exe"));
            // Transient (tests, --setup --game): never written.
            var transient = new LauncherState(root) { Transient = true };
            transient.Save();
            Check(!File.Exists(Path.Combine(root, LauncherState.FileName)), "Transient state is not saved");
            Check(GameInfo.Parse("GTS") == GameKind.GTSport && GameInfo.Parse("gt7") == GameKind.GT7 && GameInfo.Parse("gt6") == null, "--game values");
        }

        static void SettingsChecks(string root)
        {
            var launch = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false, Arguments = "profile=wheel" };
            launch.EnvironmentVariables.Remove("SHARED_OPENXR");
            launch.EnvironmentVariables.Remove("SHADGT_OPENXR_COCKPIT");
            LoaderForm.ApplyGt7HeadsetSettings(launch, null);
            Check(launch.EnvironmentVariables["SHADGT_OPENXR_OUTPUT_PERCENT"] == "100" &&
                  launch.EnvironmentVariables["SHARED_OPENXR_FRAMEGEN"] == "0", "Safe headset defaults");
            LoaderForm.ApplyGt7HeadsetVisibility(launch);
            Check(launch.Arguments == "profile=wheel", "Flat launch preserves the game arguments");
            launch.EnvironmentVariables["SHARED_OPENXR"] = "1";
            LoaderForm.ApplyGt7HeadsetVisibility(launch);
            Check(launch.Arguments == "profile=wheel", "Seated screen preserves ordinary culling");
            launch.EnvironmentVariables["SHADGT_OPENXR_COCKPIT"] = "1";
            LoaderForm.ApplyGt7HeadsetVisibility(launch);
            Check(launch.Arguments == "profile=wheel occlusionculling_mode=cpu", "Both cockpit eyes participate in visibility");
            LoaderForm.ApplyGt7HeadsetSettings(launch, new Dictionary<string, object>
                { { "openxr_framegen", 6 }, { "openxr_output_percent", 85 }, { "openxr_mirror_eye", "right" } });
            Check(launch.EnvironmentVariables["SHARED_OPENXR_FRAMEGEN"] == "dlss" &&
                  launch.EnvironmentVariables["SHADGT_OPENXR_OUTPUT_PERCENT"] == "85" &&
                  launch.EnvironmentVariables["SHADGT_OPENXR_MIRROR_EYE"] == "right", "Existing VR profiles retain their provider and output");
            // FEAT-GTS-291: each game's settings file is named after its serial.
            Check(GameInfo.GTSport.IniPath(@"X:\g") == @"X:\g\CUSA03220.RyGT.ini" && GameInfo.GT7.IniPath(@"X:\g") == @"X:\g\CUSA24769.RyGT.ini",
                  "<serial>.RyGT.ini");
            // GT Sport: its settings (RyGT.ini as RyGTS 0.2.2 wrote it, renamed) come back byte for byte.
            string gts = Folder(root, "settings-gts");
            string ini = GameInfo.GTSport.IniPath(gts);
            string old = "game=D:\\GTS\\CUSA03220\r\nupdate=D:\\GTS\\CUSA03220-patch\r\nmodules=D:\\fw\\sys_modules\r\nfonts=\r\n" +
                         "stdout_level=Info\r\nhdd_streaming=1\r\nshow_advanced=1\r\nprompt_obsolete=0\r\nenv=1|SHADGT_PARALLEL_WORKERS=4\r\n";
            File.WriteAllText(ini, old);
            var settings = new Settings(ini);
            Check(settings.Game == "D:\\GTS\\CUSA03220" && settings.HddStreaming && settings.ShowAdvanced && !settings.PromptObsolete &&
                  settings.Env.Count == 1 && settings.StdoutLevel == "Info", "GT Sport's RyGT.ini read");
            settings.Save();
            Check(File.ReadAllText(ini) == old, "GT Sport's settings written as before");
            Check(!settings.IsGt7, "CUSA03220.RyGT.ini is GT Sport's");
            // GT7: RyGT7.ini as ryGT7 0.1.6 wrote it, merged with online play's keys from RyGT.ini.
            string gt7 = Folder(root, "settings-gt7");
            string ini7 = GameInfo.GT7.IniPath(gt7);
            string old7 = "game=E:\\GT7\\CUSA24769\r\nupdate=\r\nmodules=E:\\fw\r\nfonts=E:\\fw\\fonts\r\nstdout_level=Warning\r\nshow_advanced=0\r\nenv=0|RYGT_NEO=1\r\n";
            File.WriteAllText(ini7, old7 + "online=1\r\nonline_id=Racer\r\n");
            var settings7 = new Settings(ini7);
            Check(settings7.IsGt7 && settings7.Game == "E:\\GT7\\CUSA24769" && settings7.Env.Count == 1, "GT7's settings read");
            settings7.Save();
            Check(File.ReadAllText(ini7) == old7 + "online=1\r\nonline_id=Racer\r\n", "GT7's written as before (no GT Sport lines), online play's keys kept");
            Check(!File.Exists(Path.Combine(gt7, "RyGT.ini")), "No copy of GT7's values in RyGT.ini any more");
            settings7.StdoutLevel = "Error";
            settings7.Save();
            OnlinePlay.WriteIni(gt7, new Dictionary<string, string> { { "online_id", "Other" } });
            string both = File.ReadAllText(ini7);
            Check(both.Contains("stdout_level=Error") && both.Contains("online_id=Other") && !both.Contains("online_id=Racer") && both.Contains("online=1"),
                  "The launcher's and online play's settings share GT7's file");
            // A game without a folder keeps its values in memory and writes nothing.
            var none = new Settings(null);
            none.Game = "x";
            none.Save();
            Check(none.Game == "x", "A folderless game's settings stay in memory");
        }

        static void AccentChecks()
        {
            Color red = GameInfo.GTSport.Accent, blue = GameInfo.GT7.Accent;
            Check(red.R > red.B * 3 && GameInfo.GTSport.AccentText.R > GameInfo.GTSport.AccentText.B, "GT Sport is red");
            Check(blue.B > blue.R * 3 && GameInfo.GT7.AccentText.B > GameInfo.GT7.AccentText.R, "GT7 is blue");
            var half = Anim.Mix(red, blue, 0.5f);
            Check(half.R == (red.R + blue.R) / 2 && Math.Abs(half.B - (red.B + blue.B) / 2) <= 1, "The fade passes half way");
            Check(Anim.Mix(red, blue, 0) == Color.FromArgb(red.ToArgb()) && Anim.Mix(red, blue, 1).ToArgb() == blue.ToArgb(), "The fade ends on the colours");
        }

        static List<string> Pages(LoaderForm form)
        {
            var pages = (Dictionary<string, System.Windows.Forms.Panel>)typeof(LoaderForm)
                .GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            return pages.Keys.ToList();
        }

        static void FormChecks(string root)
        {
            string gts = Folder(root, "form-gts"), gt7 = Folder(root, "form-gt7");
            File.WriteAllText(Path.Combine(gt7, "RyGT7.ini"), "");
            using (var form = new LoaderForm(GameKind.GTSport, gts))
            {
                var pages = Pages(form);
                Check(pages.Contains("Display") && pages.Contains("Graphics") && pages.Contains("Shaders") && pages.Contains("Controls") &&
                      pages.Contains("Advanced") && !pages.Contains("Online") && !pages.Contains("Account"), "GT Sport's pages");
                Check(Dark.Accent.ToArgb() == GameInfo.GTSport.Accent.ToArgb(), "GT Sport's accent");
                Check(form.Text == GameInfo.GTSport.WindowTitle, "GT Sport's window title");
                // GT7 has no folder here: only the Game and About pages, and Play is off.
                form.SelectGame(GameKind.GT7);
                pages = Pages(form);
                Check(pages.SequenceEqual(new[] { "Game", "About" }), "A folderless GT7 gets the Game and About pages");
                Check(Dark.Accent.ToArgb() == GameInfo.GT7.Accent.ToArgb(), "GT7's accent after the switch");
            }
            using (var form = new LoaderForm(GameKind.GT7, gt7))
            {
                var pages = Pages(form);
                Check(pages.Contains("Online") && pages.Contains("Account") && pages.Contains("Controls") && pages.Contains("Graphics"), "GT7's pages");
                // ryGT7's defaults are written into GT7's own config, as ryGT7's launcher did.
                // GAME-004: GT7's config follows its own serial.
                string config = File.ReadAllText(Path.Combine(gt7, "user", "custom_configs", "CUSA24769.json"));
                Check(!File.Exists(Path.Combine(gt7, "user", "custom_configs", "CUSA03220.json")), "GT7 never writes GT Sport's config name");
                Check(config.Contains("\"readbacks_mode\":2") && config.Contains("\"native_renderer\":true"), "GT7's defaults written");
                Check(!File.Exists(Path.Combine(gts, "user", "custom_configs", "CUSA03220.json")) ||
                      !File.ReadAllText(Path.Combine(gts, "user", "custom_configs", "CUSA03220.json")).Contains("readbacks_mode"), "GT Sport's config untouched by GT7");
                Check(form.Text == GameInfo.GT7.WindowTitle, "GT7's window title");
            }
        }

        /// A small program whose version information names product as its ProductName.
        static void FakeLauncher(string path, string product)
        {
            var options = new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = true, OutputAssembly = path };
            var result = new Microsoft.CSharp.CSharpCodeProvider().CompileAssemblyFromSource(options,
                "[assembly: System.Reflection.AssemblyProduct(\"" + product + "\")] static class P { static void Main() { } }");
            if (result.Errors.HasErrors) throw new Exception("Could not build the test launcher " + path);
        }

        /// FEAT-GTS-291: older launchers beside RyGTLauncher.exe go to <name>.old; nothing else does.
        static void OldLauncherChecks(string root)
        {
            string folder = Folder(root, "old-launchers");
            FakeLauncher(Path.Combine(folder, "RyGTS.exe"), "RyGTS");
            FakeLauncher(Path.Combine(folder, "RyGT7.exe"), "ryGT7");
            FakeLauncher(Path.Combine(folder, "GTSport.exe"), "RyGTS"); // a game, even if it claimed the product
            string self = Path.Combine(folder, Updater.LauncherExe);
            File.WriteAllText(self, "");
            var notes = OldLaunchers.Retire(folder, self);
            Check(notes.Count == 2 && notes.All(n => n.StartsWith("Renamed")), "Two older launchers renamed: " + string.Join(" | ", notes));
            Check(!File.Exists(Path.Combine(folder, "RyGTS.exe")) && File.Exists(Path.Combine(folder, "RyGTS.exe.old")), "RyGTS.exe became RyGTS.exe.old");
            Check(!File.Exists(Path.Combine(folder, "RyGT7.exe")) && File.Exists(Path.Combine(folder, "RyGT7.exe.old")), "RyGT7.exe became RyGT7.exe.old");
            Check(File.Exists(Path.Combine(folder, "GTSport.exe")) && File.Exists(self), "The game and the launcher stay");
            Check(LauncherState.LooksLikeGt7(folder), "A ryGT7 folder whose RyGT7.exe was renamed is still one");
            // Not a launcher: a RyGTS.exe without that product (another program, or this launcher under the old name).
            string other = Folder(root, "not-launchers");
            FakeLauncher(Path.Combine(other, "RyGTS.exe"), "RyGT Launcher");
            File.WriteAllText(Path.Combine(other, "RyGT7.exe"), "not a program");
            Check(OldLaunchers.Retire(other, Path.Combine(other, Updater.LauncherExe)).Count == 0, "Neither is renamed");
            Check(File.Exists(Path.Combine(other, "RyGTS.exe")) && File.Exists(Path.Combine(other, "RyGT7.exe")), "Only the old launchers are renamed");
            // The running launcher itself is never renamed.
            string running = Folder(root, "running");
            FakeLauncher(Path.Combine(running, "RyGTS.exe"), "RyGTS");
            Check(OldLaunchers.Retire(running, Path.Combine(running, "RyGTS.exe")).Count == 0 && File.Exists(Path.Combine(running, "RyGTS.exe")), "Not the running launcher");
            // A roll back to a release from before RyGTLauncher.exe puts the old launcher back.
            File.Delete(self);
            Check(OldLaunchers.PutBack(folder) == Path.Combine(folder, "RyGTS.exe") && File.Exists(Path.Combine(folder, "RyGTS.exe")), "Put back after a roll back");
            // One updater: a package only updates the folder of a game it has files for.
            var gtsPackage = new[] { "RyGTLauncher.exe", "libs/libc.prx", "user/custom_configs/CUSA03220.json" };
            var gt7Package = new[] { "RyGTLauncher.exe", "libs/libc.prx", "patches/CUSA24769.xml" };
            Check(Updater.PackageIsFor(gtsPackage, GameInfo.GTSport.TitleId) && !Updater.PackageIsFor(gtsPackage, GameInfo.GT7.TitleId), "GT Sport's package is GT Sport's");
            Check(Updater.PackageIsFor(gt7Package, GameInfo.GT7.TitleId) && !Updater.PackageIsFor(gt7Package, GameInfo.GTSport.TitleId), "GT7's package is GT7's");
            Check(Updater.PackageIsFor(gtsPackage.Concat(gt7Package), GameInfo.GT7.TitleId), "A package for both games updates either");
        }

        static void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        /// GAME-004: in a GT7 folder the old serial's files take GT7's own (CUSA24769); never overwriting,
        /// never in a GT Sport folder, and a second run does nothing.
        static void SerialChecks(string root)
        {
            Check(GameInfo.GTSport.ConfigPath(@"X:\g") == @"X:\g\user\custom_configs\CUSA03220.json" &&
                  GameInfo.GT7.ConfigPath(@"X:\g") == @"X:\g\user\custom_configs\CUSA24769.json", "Each game's config is named after its serial");
            string gt7 = Folder(root, "serial-gt7");
            File.WriteAllText(Path.Combine(gt7, "RyGT7.ini"), "game=\n");
            string configs = Path.Combine(gt7, "user", "custom_configs"), shaders = Path.Combine(gt7, "shaders");
            string inputs = Path.Combine(gt7, "user", "input_config");
            Put(Path.Combine(configs, "CUSA03220.json"), "{\"GPU\":{\"race_fps_lock\":60}}");
            Put(Path.Combine(shaders, "CUSA03220-amd.vkpipelinecache"), "cache");
            Put(Path.Combine(shaders, "CUSA03220.manifest", "a.bin"), "manifest");
            Put(Path.Combine(shaders, "CUSA03220-direct-sets.v3", "b.bin"), "sets");
            Put(Path.Combine(shaders, "CUSA03220-nvidia.vkpipelinecache"), "old");
            Put(Path.Combine(shaders, "CUSA24769-nvidia.vkpipelinecache"), "new"); // already there: kept
            Put(Path.Combine(shaders, "shader-manifest.txt"), "other");
            Put(Path.Combine(inputs, "CUSA03220.ini"), "bindings");
            Put(Path.Combine(inputs, "default.ini"), "default");
            var notes = SerialMigration.MigrateGt7(gt7);
            Check(notes.Count == 5 && notes.All(n => n.Contains("renamed")), "Five renamed: " + string.Join(" | ", notes));
            Check(File.ReadAllText(Path.Combine(configs, "CUSA24769.json")).Contains("race_fps_lock") && !File.Exists(Path.Combine(configs, "CUSA03220.json")),
                  "The config takes GT7's serial");
            Check(File.Exists(Path.Combine(shaders, "CUSA24769-amd.vkpipelinecache")) && !File.Exists(Path.Combine(shaders, "CUSA03220-amd.vkpipelinecache")), "A pipeline cache renamed");
            Check(File.Exists(Path.Combine(shaders, "CUSA24769.manifest", "a.bin")) && !Directory.Exists(Path.Combine(shaders, "CUSA03220.manifest")), "The manifest folder renamed");
            Check(File.Exists(Path.Combine(shaders, "CUSA24769-direct-sets.v3", "b.bin")), "The direct sets renamed");
            Check(File.ReadAllText(Path.Combine(shaders, "CUSA24769-nvidia.vkpipelinecache")) == "new" &&
                  File.ReadAllText(Path.Combine(shaders, "CUSA03220-nvidia.vkpipelinecache")) == "old", "An existing new name is never overwritten");
            Check(File.Exists(Path.Combine(shaders, "shader-manifest.txt")) && File.Exists(Path.Combine(inputs, "default.ini")), "Other files stay");
            Check(File.Exists(Path.Combine(inputs, "CUSA24769.ini")) && !File.Exists(Path.Combine(inputs, "CUSA03220.ini")), "The input config renamed");
            Check(SerialMigration.MigrateGt7(gt7).Count == 0, "A second run does nothing");
            // The new config already there: the old one stays, untouched.
            Put(Path.Combine(configs, "CUSA03220.json"), "{}");
            Check(SerialMigration.MigrateGt7(gt7).Count == 0 && File.ReadAllText(Path.Combine(configs, "CUSA24769.json")).Contains("race_fps_lock") &&
                  File.ReadAllText(Path.Combine(configs, "CUSA03220.json")) == "{}", "The new config is never overwritten");
            // A GT Sport folder is never touched.
            string gts = Folder(root, "serial-gts");
            Put(Path.Combine(gts, "user", "custom_configs", "CUSA03220.json"), "{}");
            Put(Path.Combine(gts, "shaders", "CUSA03220.manifest", "a.bin"), "manifest");
            Check(SerialMigration.MigrateGt7(gts).Count == 0 && File.Exists(Path.Combine(gts, "user", "custom_configs", "CUSA03220.json")) &&
                  Directory.Exists(Path.Combine(gts, "shaders", "CUSA03220.manifest")) && !File.Exists(Path.Combine(gts, "user", "custom_configs", "CUSA24769.json")),
                  "GT Sport's folder untouched");
            // The launcher renames when GT7's folder is opened, before its pages read the settings, and logs it.
            string opened = Folder(root, "serial-open");
            File.WriteAllText(Path.Combine(opened, "RyGT7.ini"), "game=\n");
            Put(Path.Combine(opened, "user", "custom_configs", "CUSA03220.json"), "{\"GPU\":{\"race_fps_lock\":60}}");
            using (var form = new LoaderForm(GameKind.GT7, opened)) { }
            string migrated = Path.Combine(opened, "user", "custom_configs", "CUSA24769.json");
            Check(File.Exists(migrated) && File.ReadAllText(migrated).Contains("\"race_fps_lock\":60") &&
                  !File.Exists(Path.Combine(opened, "user", "custom_configs", "CUSA03220.json")), "Opening GT7's folder keeps its settings under the new name");
            string log = Path.Combine(LogFiles.Folder(opened, GameInfo.Of(GameKind.GT7).TitleId), "setup.log"); // logs\<serial>
            Check(File.Exists(log) && File.ReadAllText(log).Contains("CUSA03220.json to CUSA24769.json"), "The rename is in the setup log");
        }

        // FEAT-GTS-291: each game's app0 is app0\<serial>; App0Layout.Migrate moves a folder's older app0 there.
        static void App0Checks(string root)
        {
            const string flat = "app0\\sce_module\\libc.prx.guest.prx", perGame = "app0\\CUSA03220\\sce_module\\libc.prx.guest.prx";
            var gts = GameInfo.GTSport;
            Check(App0Layout.Folder("D:\\RyGT", "CUSA03220") == "D:\\RyGT\\app0\\CUSA03220" && App0Layout.IsSerial("CUSA24769") &&
                  !App0Layout.IsSerial("sce_module") && !App0Layout.IsSerial("cusa03220"), "app0\\<serial> names");
            string option;
            Check(App0Layout.SetupFolder("D:\\RyGT", "CUSA24769", true, out option) == "D:\\RyGT\\app0\\CUSA24769" && option == " --app0 app0/CUSA24769",
                  "Set up writes app0\\<serial> and tells the converter");
            Check(App0Layout.SetupFolder("D:\\RyGT", "CUSA24769", false, out option) == "D:\\RyGT\\app0" && option == "",
                  "Set up writes app0 as before with an older converter or runtime");

            // Fresh: nothing to move.
            string fresh = Folder(root, "a0-new");
            Check(App0Layout.Migrate(fresh, gts, false).Count == 0 && !Directory.Exists(Path.Combine(fresh, "app0")), "A fresh folder: nothing done");
            Check(App0Layout.Top(fresh, "CUSA03220") == Path.Combine(fresh, "app0"), "Top is app0 without app0\\<serial>");

            // Existing, with a GTSport.exe converted before --app0: kept until Set up, then moved.
            string old = Folder(root, "a0-old");
            Put(Path.Combine(old, "GTSport.exe"), "MZ " + flat);
            Put(Path.Combine(old, "app0", "sce_module", "libc.prx.guest.prx"), "converted");
            Put(Path.Combine(old, "app0", "gt.idx"), "a real copy");
            Check(App0Layout.LoadsFlatApp0(Path.Combine(old, "GTSport.exe")), "An older exe loads app0\\sce_module");
            Check(App0Layout.Migrate(old, gts, false).Count == 0 && File.Exists(Path.Combine(old, "app0", "sce_module", "libc.prx.guest.prx")),
                  "Play keeps app0 for an exe that loads app0\\sce_module");
            var notes = App0Layout.Migrate(old, gts, true);
            string own = Path.Combine(old, "app0", "CUSA03220");
            Check(notes.Count == 3 && File.ReadAllText(Path.Combine(own, "sce_module", "libc.prx.guest.prx")) == "converted" &&
                  File.ReadAllText(Path.Combine(own, "gt.idx")) == "a real copy" &&
                  Directory.GetFileSystemEntries(Path.Combine(old, "app0")).Length == 1 &&
                  Directory.GetDirectories(old, "app0.moving-*").Length == 0, "Set up moves app0 into app0\\CUSA03220 in three steps");
            Check(App0Layout.Top(old, "CUSA03220") == own && App0Layout.Top(old, "CUSA24769") == Path.Combine(old, "app0"), "Top follows the serial");
            // Idempotent: a second run finds nothing, also with a new exe.
            Put(Path.Combine(old, "GTSport.exe"), "MZ " + perGame);
            Check(!App0Layout.LoadsFlatApp0(Path.Combine(old, "GTSport.exe")), "A new exe loads app0\\<serial>");
            Check(App0Layout.Migrate(old, gts, false).Count == 0 && App0Layout.Migrate(old, gts, true).Count == 0, "A second run does nothing");

            // A new exe beside an older app0 (Set up cut short before it moved it): Play moves it.
            string leftover = Folder(root, "a0-left");
            Put(Path.Combine(leftover, "GTSport.exe"), "MZ " + perGame);
            Put(Path.Combine(leftover, "app0", "sce_module", "libc.prx.guest.prx"), "converted");
            Check(App0Layout.Migrate(leftover, gts, false).Count == 3 &&
                  File.Exists(Path.Combine(leftover, "app0", "CUSA03220", "sce_module", "libc.prx.guest.prx")), "Play moves app0 for a new exe");

            // Interrupted after step 1 (app0 renamed, no new app0) and after step 2 (new, empty app0).
            foreach (bool afterStep2 in new[] { false, true })
            {
                string cut = Folder(root, "a0-cut" + (afterStep2 ? "2" : "1"));
                Put(Path.Combine(cut, "GTSport.exe"), "MZ " + perGame);
                Put(Path.Combine(cut, "app0.moving-CUSA03220-1", "sce_module", "libc.prx.guest.prx"), "converted");
                if (afterStep2) Directory.CreateDirectory(Path.Combine(cut, "app0"));
                notes = App0Layout.Migrate(cut, gts, false);
                Check(notes.Count == (afterStep2 ? 1 : 2) && File.Exists(Path.Combine(cut, "app0", "CUSA03220", "sce_module", "libc.prx.guest.prx")) &&
                      Directory.GetDirectories(cut, "app0.moving-*").Length == 0, "An interrupted move is finished" + (afterStep2 ? " after step 2" : " after step 1"));
                Check(App0Layout.Migrate(cut, gts, false).Count == 0, "And not done twice");
            }
            // An interrupted move whose app0\<serial> exists meanwhile: nothing overwritten, both kept.
            string clash = Folder(root, "a0-clash");
            Put(Path.Combine(clash, "app0.moving-CUSA03220-1", "gt.idx"), "older");
            Put(Path.Combine(clash, "app0", "CUSA03220", "gt.idx"), "newer");
            notes = App0Layout.Migrate(clash, gts, true);
            Check(notes.Count == 1 && notes[0].Contains("already exists") && File.ReadAllText(Path.Combine(clash, "app0", "CUSA03220", "gt.idx")) == "newer" &&
                  File.ReadAllText(Path.Combine(clash, "app0.moving-CUSA03220-1", "gt.idx")) == "older", "Never overwritten");
            // Both exes: whose app0 it is isn't clear, so nothing moves.
            string both = Folder(root, "a0-both");
            Put(Path.Combine(both, "GTSport.exe"), "MZ " + perGame);
            Put(Path.Combine(both, "GT7.exe"), "MZ app0\\CUSA24769\\sce_module\\");
            Put(Path.Combine(both, "app0", "sce_module", "libc.prx.guest.prx"), "converted");
            notes = App0Layout.Migrate(both, gts, true);
            Check(notes.Count == 1 && notes[0].Contains("both") && File.Exists(Path.Combine(both, "app0", "sce_module", "libc.prx.guest.prx")) &&
                  !Directory.Exists(Path.Combine(both, "app0", "CUSA03220")), "Both exes: app0 left as it is");
            // The other game's folder: GT7's app0 is never moved for GT Sport.
            string other = Folder(root, "a0-gt7");
            Put(Path.Combine(other, "GT7.exe"), "MZ");
            Put(Path.Combine(other, "app0", "sce_module", "libc.prx.guest.prx"), "converted");
            Check(App0Layout.Migrate(other, gts, true).Count == 0 && File.Exists(Path.Combine(other, "app0", "sce_module", "libc.prx.guest.prx")),
                  "Another game's folder untouched");
            // GT7 moves into its own serial, and an app0 holding another game's serial folder beside older files is left alone.
            Check(App0Layout.Migrate(other, GameInfo.GT7, true).Count == 3 &&
                  File.Exists(Path.Combine(other, "app0", "CUSA24769", "sce_module", "libc.prx.guest.prx")), "GT7's app0 goes to app0\\CUSA24769");
            Put(Path.Combine(other, "app0", "sce_module", "stray.prx"), "older");
            File.Delete(Path.Combine(other, "GT7.exe"));
            notes = App0Layout.Migrate(other, gts, true);
            Check(notes.Count == 1 && File.Exists(Path.Combine(other, "app0", "sce_module", "stray.prx")) && !Directory.Exists(Path.Combine(other, "app0", "CUSA03220")),
                  "Another game's app0\\<serial> beside older files: left alone");

            // The launcher moves it before Play, with each step in the game's setup log.
            string played = Folder(root, "a0-play");
            Put(Path.Combine(played, "GTSport.exe"), "MZ " + perGame);
            Put(Path.Combine(played, "app0", "sce_module", "libc.prx.guest.prx"), "converted");
            using (var form = new LoaderForm(GameKind.GTSport, played))
                typeof(LoaderForm).GetMethod("MigrateApp0", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
            string log = Path.Combine(LogFiles.Folder(played, "CUSA03220"), "setup.log");
            Check(Directory.Exists(Path.Combine(played, "app0", "CUSA03220", "sce_module")) && File.Exists(log) &&
                  File.ReadAllText(log).Contains("step 3 of 3"), "Play's move is in logs\\CUSA03220\\setup.log");
        }

        static List<string> Moves(string folder, GameInfo game) { return SharedNames.Migrate(folder, game); }

        /// FEAT-GTS-291: the settings file, convert, temp0 and data take the game's serial (SharedNames).
        static void SharedNameChecks(string root)
        {
            var gts = GameInfo.GTSport;
            var gt7 = GameInfo.GT7;
            Check(gts.OwnFolder(@"X:\g", "convert") == @"X:\g\convert\CUSA03220" && gt7.OwnFolder(@"X:\g", "data") == @"X:\g\data\CUSA24769",
                  "<name>\\<serial>");
            // A RyGTS folder: RyGT.ini renamed byte for byte, convert, temp0 and data moved in three steps each.
            string sport = Folder(root, "sn-gts");
            Put(Path.Combine(sport, "GTSport.exe"), "MZ");
            string ini = "game=D:\\GTS\\CUSA03220\r\nupdate=\r\nstdout_level=Info\r\n";
            Put(Path.Combine(sport, "RyGT.ini"), ini);
            Put(Path.Combine(sport, "convert", "eboot.elf"), "elf");
            Put(Path.Combine(sport, "convert", "sce_module", "libc.prx"), "module");
            Put(Path.Combine(sport, "temp0", "a.tmp"), "tmp");
            var notes = Moves(sport, gts);
            Check(notes.Count == 7, "One rename and two moves of three steps: " + string.Join(" | ", notes));
            Check(File.ReadAllText(gts.IniPath(sport)) == ini && !File.Exists(Path.Combine(sport, "RyGT.ini")), "RyGT.ini is CUSA03220.RyGT.ini");
            Check(File.ReadAllText(Path.Combine(sport, "convert", "CUSA03220", "eboot.elf")) == "elf" &&
                  File.Exists(Path.Combine(sport, "convert", "CUSA03220", "sce_module", "libc.prx")) &&
                  Directory.GetFileSystemEntries(Path.Combine(sport, "convert")).Length == 1, "convert is convert\\CUSA03220");
            Check(File.Exists(Path.Combine(sport, "temp0", "CUSA03220", "a.tmp")) && !Directory.Exists(Path.Combine(sport, "data")), "temp0 moved, no data made");
            Check(Directory.GetDirectories(sport, "*.moving-*").Length == 0, "nothing left on the way");
            Check(Moves(sport, gts).Count == 0, "A second run does nothing");
            Check(Moves(sport, gt7).Count == 0 && !File.Exists(gt7.IniPath(sport)), "GT7 never takes GT Sport's files");

            // A ryGT7 folder: RyGT7.ini and the RyGT.ini beside it (online play) become CUSA24769.RyGT.ini.
            string seven = Folder(root, "sn-gt7");
            Put(Path.Combine(seven, "GT7.exe"), "MZ");
            Put(Path.Combine(seven, "RyGT7.ini"), "game=E:\\GT7\r\nstdout_level=Warning\r\nenv=1|RYGT_NEO=1\r\n");
            Put(Path.Combine(seven, "RyGT.ini"), "online=1\r\naccount_key=k\r\nstdout_level=Warning\r\ngame=E:\\GT7\r\nupdate=\r\n");
            Put(Path.Combine(seven, "data", "gt7offline", "catalog.bin"), "cars");
            Put(Path.Combine(seven, "convert", "offline-patch.txt"), "none");
            notes = Moves(seven, gt7);
            string merged = File.ReadAllText(gt7.IniPath(seven));
            Check(merged == "game=E:\\GT7\r\nstdout_level=Warning\r\nenv=1|RYGT_NEO=1\r\nonline=1\r\naccount_key=k\r\nupdate=\r\n",
                  "RyGT7.ini's lines, then RyGT.ini's other keys: " + merged);
            Check(!File.Exists(Path.Combine(seven, "RyGT7.ini")) && !File.Exists(Path.Combine(seven, "RyGT.ini")) &&
                  Directory.GetFiles(seven, "RyGT7.ini.moved-*").Length == 1 && Directory.GetFiles(seven, "RyGT.ini.moved-*").Length == 1,
                  "Both old files kept as .moved-<date>");
            Check(File.ReadAllText(Path.Combine(seven, "data", "CUSA24769", "gt7offline", "catalog.bin")) == "cars" &&
                  File.Exists(Path.Combine(seven, "convert", "CUSA24769", "offline-patch.txt")), "data and convert moved into CUSA24769");
            Check(LauncherState.LooksLikeGt7(seven), "Still a ryGT7 folder after its RyGT7.ini was renamed");
            Check(new Settings(gt7.IniPath(seven)).Env.Count == 1 && OnlinePlay.Enabled(seven), "GT7's settings and online play read from the new file");
            Check(Moves(seven, gt7).Count == 0, "And not done twice");
            // A newer file that lacks a key of an old one: the old one stays.
            Put(Path.Combine(seven, "RyGT.ini"), "account_pass=p\r\n");
            notes = Moves(seven, gt7);
            Check(notes.Count == 1 && File.Exists(Path.Combine(seven, "RyGT.ini")) && !File.ReadAllText(gt7.IniPath(seven)).Contains("account_pass"),
                  "Nothing dropped, nothing overwritten");
            File.Delete(Path.Combine(seven, "RyGT.ini"));

            // GT Sport in a ryGT7 folder before GT7.exe exists: RyGT.ini there is GT7's, never taken.
            string fresh7 = Folder(root, "sn-gt7-fresh");
            Put(Path.Combine(fresh7, "RyGT7.ini"), "game=\r\n");
            Put(Path.Combine(fresh7, "RyGT.ini"), "online=0\r\n");
            Put(Path.Combine(fresh7, "convert", "eboot.elf"), "gt7");
            Check(Moves(fresh7, gts).Count == 0 && File.Exists(Path.Combine(fresh7, "RyGT.ini")) && File.Exists(Path.Combine(fresh7, "convert", "eboot.elf")),
                  "GT Sport leaves a ryGT7 folder's old files alone");

            // Interrupted after step 1 and step 2, and a move whose target exists meanwhile.
            foreach (bool afterStep2 in new[] { false, true })
            {
                string cut = Folder(root, "sn-cut" + (afterStep2 ? "2" : "1"));
                Put(Path.Combine(cut, "GTSport.exe"), "MZ");
                Put(Path.Combine(cut, "convert.moving-CUSA03220-1", "eboot.elf"), "elf");
                if (afterStep2) Directory.CreateDirectory(Path.Combine(cut, "convert"));
                notes = Moves(cut, gts);
                Check(notes.Count == (afterStep2 ? 1 : 2) && File.Exists(Path.Combine(cut, "convert", "CUSA03220", "eboot.elf")) &&
                      Directory.GetDirectories(cut, "convert.moving-*").Length == 0, "An interrupted move is finished" + (afterStep2 ? " after step 2" : " after step 1"));
                Check(Moves(cut, gts).Count == 0, "And not twice");
            }
            string clash = Folder(root, "sn-clash");
            Put(Path.Combine(clash, "GTSport.exe"), "MZ");
            Put(Path.Combine(clash, "temp0.moving-CUSA03220-1", "a.tmp"), "older");
            Put(Path.Combine(clash, "temp0", "CUSA03220", "a.tmp"), "newer");
            notes = Moves(clash, gts);
            Check(notes.Count == 1 && notes[0].Contains("already exists") && File.ReadAllText(Path.Combine(clash, "temp0", "CUSA03220", "a.tmp")) == "newer" &&
                  File.ReadAllText(Path.Combine(clash, "temp0.moving-CUSA03220-1", "a.tmp")) == "older", "Never overwritten");

            // The launcher moves them when the game's folder is opened, before the pages read the settings,
            // and the top-level setup.log is now the game's logs\<serial>\setup.log.
            string opened = Folder(root, "sn-open");
            Put(Path.Combine(opened, "GT7.exe"), "MZ");
            Put(Path.Combine(opened, "RyGT7.ini"), "game=E:\\GT7\\CUSA24769\r\n");
            Put(Path.Combine(opened, "data", "gt7offline", "catalog.bin"), "cars");
            using (var form = new LoaderForm(GameKind.GT7, opened)) { }
            Check(new Settings(gt7.IniPath(opened)).Game == "E:\\GT7\\CUSA24769" && File.Exists(Path.Combine(opened, "data", "CUSA24769", "gt7offline", "catalog.bin")),
                  "Opening GT7's folder moves its settings and data");
            string log = Path.Combine(LogFiles.Folder(opened, gt7.TitleId), "setup.log");
            Check(File.Exists(log) && File.ReadAllText(log).Contains("CUSA24769.RyGT.ini") && File.ReadAllText(log).Contains("data\\CUSA24769") &&
                  !File.Exists(Path.Combine(opened, "setup.log")), "Each step is in logs\\CUSA24769\\setup.log");
            typeof(LoaderForm).GetMethod("AppendSetupLog", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { opened, gt7.TitleId, new[] { "a line" } });
            Check(File.ReadAllText(log).Contains("a line") && !File.Exists(Path.Combine(opened, "setup.log")), "AppendSetupLog writes logs\\<serial>\\setup.log");
        }

        /// FEAT-GTS-291: ONE folder for both games: GTSport.exe and GT7.exe side by side, each game's files apart.
        static void OneFolderChecks(string root)
        {
            var gts = GameInfo.GTSport;
            var gt7 = GameInfo.GT7;
            string folder = Folder(root, "one-folder");
            Put(Path.Combine(folder, "GTSport.exe"), "MZ");
            Put(Path.Combine(folder, "GT7.exe"), "MZ");
            Put(Path.Combine(folder, "patches", "CUSA24769.xml"), "<patch/>");
            // An old RyGT.ini beside both exes: whose it is isn't clear, so it stays as it is.
            Put(Path.Combine(folder, "RyGT.ini"), "game=?\r\n");
            Put(Path.Combine(folder, "data", "gt7offline", "catalog.bin"), "cars");
            Check(Moves(folder, gts).Count == 0 && Moves(folder, gt7).Count == 0 && File.Exists(Path.Combine(folder, "RyGT.ini")) &&
                  Directory.Exists(Path.Combine(folder, "data", "gt7offline")), "Old files beside both exes: left as they are");
            File.Delete(Path.Combine(folder, "RyGT.ini"));
            Directory.Delete(Path.Combine(folder, "data"), true);

            var state = new LauncherState(folder) { Transient = true };
            state.SetRoot(GameKind.GT7, folder);
            Check(state.RootOf(GameKind.GTSport) == folder && state.RootOf(GameKind.GT7) == folder &&
                  state.Ready(GameKind.GTSport) && state.Ready(GameKind.GT7), "Both games ready in one folder");

            // Every name either game writes is its own.
            var paths = new List<string[]>
            {
                new[] { gts.IniPath(folder), gt7.IniPath(folder) },
                new[] { gts.ConfigPath(folder), gt7.ConfigPath(folder) },
                new[] { gts.OwnFolder(folder, "convert"), gt7.OwnFolder(folder, "convert") },
                new[] { gts.OwnFolder(folder, "temp0"), gt7.OwnFolder(folder, "temp0") },
                new[] { gts.OwnFolder(folder, "data"), gt7.OwnFolder(folder, "data") },
                new[] { LogFiles.Folder(folder, gts.TitleId), LogFiles.Folder(folder, gt7.TitleId) },
                new[] { App0Layout.Folder(folder, gts.TitleId), App0Layout.Folder(folder, gt7.TitleId) },
                new[] { Path.Combine(folder, "user", "savedata", gts.TitleId), Path.Combine(folder, "user", "savedata", gt7.TitleId) },
            };
            Check(paths.All(p => !string.Equals(p[0], p[1], StringComparison.OrdinalIgnoreCase)), "Each game's settings, config, convert, temp0, data, logs, app0 and saves apart");

            // Each game's settings, written through the launcher, stay in its own file.
            var sportSettings = new Settings(gts.IniPath(folder)) { Game = "D:\\GTS\\CUSA03220", StdoutLevel = "Info" };
            sportSettings.Save();
            var sevenSettings = new Settings(gt7.IniPath(folder)) { Game = "E:\\GT7\\CUSA24769", StdoutLevel = "Error" };
            sevenSettings.Save();
            OnlinePlay.WriteIni(folder, new Dictionary<string, string> { { "online", "1" } });
            Check(new Settings(gts.IniPath(folder)).Game == "D:\\GTS\\CUSA03220" && new Settings(gts.IniPath(folder)).StdoutLevel == "Info" &&
                  !File.ReadAllText(gts.IniPath(folder)).Contains("online"), "GT Sport's settings untouched by GT7's and online play's");
            Check(new Settings(gt7.IniPath(folder)).Game == "E:\\GT7\\CUSA24769" && OnlinePlay.Enabled(folder), "GT7's settings and online play in GT7's file");
            Check(!File.Exists(Path.Combine(folder, "RyGT.ini")) && !File.Exists(Path.Combine(folder, "RyGT7.ini")), "No shared settings file");

            // Both games' launcher pages on the one folder: each writes only its own files, and GT Sport's
            // CUSA03220 files are never taken for GT7's old names (GAME-004's rename is for ryGT7 folders only).
            Put(Path.Combine(folder, "shaders", "CUSA03220-amd.vkpipelinecache"), "gt sport");
            Put(Path.Combine(folder, "user", "input_config", "CUSA03220.ini"), "gt sport");
            Check(SerialMigration.MigrateGt7(folder).Count == 0, "No GAME-004 rename in a folder with GTSport.exe");
            using (var form = new LoaderForm(GameKind.GT7, folder)) { }
            using (var form = new LoaderForm(GameKind.GTSport, folder)) { }
            Check(File.Exists(gt7.ConfigPath(folder)) && File.ReadAllText(gt7.ConfigPath(folder)).Contains("\"readbacks_mode\":2") &&
                  (!File.Exists(gts.ConfigPath(folder)) || !File.ReadAllText(gts.ConfigPath(folder)).Contains("readbacks_mode")),
                  "GT7's defaults in CUSA24769.json, GT Sport's config untouched");
            Check(new Settings(gts.IniPath(folder)).Game == "D:\\GTS\\CUSA03220" && new Settings(gt7.IniPath(folder)).Game == "E:\\GT7\\CUSA24769",
                  "Opening both games keeps each one's settings");
            Check(File.ReadAllText(Path.Combine(folder, "shaders", "CUSA03220-amd.vkpipelinecache")) == "gt sport" &&
                  !File.Exists(Path.Combine(folder, "shaders", "CUSA24769-amd.vkpipelinecache")) &&
                  File.Exists(Path.Combine(folder, "user", "input_config", "CUSA03220.ini")), "GT Sport's shaders and input config stay GT Sport's");
            // GT Sport's settings in a ryGT7 folder without GTSport.exe yet: GAME-004 leaves its CUSA03220 files too.
            string gt7Only = Folder(root, "one-folder-gt7");
            Put(Path.Combine(gt7Only, "GT7.exe"), "MZ");
            Put(gt7.IniPath(gt7Only), "game=\r\n");
            Put(gts.IniPath(gt7Only), "game=D:\\GTS\r\n");
            Put(Path.Combine(gt7Only, "shaders", "CUSA03220.manifest", "a.bin"), "gt sport");
            Check(SerialMigration.MigrateGt7(gt7Only).Count == 0 && Directory.Exists(Path.Combine(gt7Only, "shaders", "CUSA03220.manifest")),
                  "GT Sport's settings in GT7's folder: no GAME-004 rename");
            Check(!Directory.Exists(Path.Combine(folder, "convert")) || Directory.GetFileSystemEntries(Path.Combine(folder, "convert")).All(e => App0Layout.IsSerial(Path.GetFileName(e))),
                  "Nothing written to convert outside a game's own folder");
        }

        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "RyGT-unified-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                StateChecks(root);
                SettingsChecks(root);
                AccentChecks();
                FormChecks(root);
                OldLauncherChecks(root);
                SerialChecks(root);
                App0Checks(root);
                SharedNameChecks(root);
                OneFolderChecks(root);
            }
            catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
            finally { try { Directory.Delete(root, true); } catch { } }
            Console.WriteLine("PASS: " + checks + " unified launcher checks");
            return 0;
        }
    }
}
