// FEAT-GTS-290: what only Gran Turismo 7 has in the unified launcher: its Display, Graphics and
// Controls pages (ControlsPage.cs), Online and Account (Online.cs), its Set up (GT7Setup.cs, the Offline
// mode patch) and how it starts GT7.exe, all as
// ryGT7's own launcher (rygt7 6178d0b, 0.1.6) had them.
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RyGT
{
    partial class LoaderForm
    {
        /// GT7's Display, Graphics, Controls, Online and Account pages, as ryGT7's launcher had them (the
        /// Game, Shaders, About and Advanced pages are both games'; RyGTLoader.cs). Returns what to run
        /// after Rowify (the Graphics rows it replaced are shown or hidden again).
        Action BuildGt7Pages(TableLayoutPanel displayPage, TableLayoutPanel graphicsPage)
        {
            var display = Section(displayPage, "Screen");
            AddScreenSettings(display);
            AddOption(display, "Show an FPS counter", "General", "show_fps_counter", false);
            AddOption(display, "Show the splash screen while the game starts", "General", "show_splash", false);
            AddOption(display, "Show the game on Discord (Rich Presence)", "General", "discord_rpc_enabled", true);
            AddMemorySetting(display);
            AddVideoMemorySetting(display);

            // ---- Graphics
            var graphics = Section(graphicsPage, "Rendering");
            // Sparks, tire smoke and other effects are always on; an older launcher could have
            // turned them off in the config.
            if (!ReadSetting("GPU", "particle_effects", true))
            {
                try { WriteSetting("GPU", "particle_effects", true); } catch { }
            }
            // Upscaling: one method at a time, scaling the game's 1920x1080 picture. AMD FSR 1 is
            // shadGT's own pass; FSR 3.1 goes through amd_fidelityfx_vk.dll (part of the release);
            // NVIDIA DLSS goes through Streamline (sl.dlss.dll and nvngx_dlss.dll in the dlss folder,
            // which the Download button fetches from NVIDIA's GitHub; DlssDownload.cs), or a
            // self-built ryGT_dlss.dll when Streamline's DLSS plugin is absent. Settings:
            // GPU.fsr_enabled, fsr3_enabled, dlss_enabled, rcas_enabled and upscale_quality (the game
            // gets the last as RYGT_UPSCALE_QUALITY).
            var upscaling = Section(graphicsPage, "Upscaling");
            var rendererImage = ReadRendererImage();
            if (NamesSwitch(rendererImage, "SHADGT_OPENXR_COCKPIT"))
                AddGt7HeadsetSettings(Section(graphicsPage, "OpenXR headset"));
            bool fsr3Installed = File.Exists(Path.Combine(root, "amd_fidelityfx_vk.dll"));
            string dlssDir = Path.Combine(root, "dlss");
            Func<string, DarkComboBox, Label, FlowLayoutPanel> comboRow = (text, combo, note) =>
            {
                var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0), WrapContents = false };
                row.Controls.Add(new Label { Text = text, AutoSize = false, Width = 110, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
                row.Controls.Add(combo);
                return row;
            };
            Func<Label> noteLabel = () => new Label
            {
                AutoSize = true, MaximumSize = new Size(860, 0), BackColor = Color.Transparent, ForeColor = Dark.DimText,
                Tag = "heading", Margin = new Padding(2, 2, 0, 4)
            };
            var method = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            method.Items.AddRange(new object[] { "Off", "AMD FSR 1", "AMD FSR 3.1", "NVIDIA DLSS" });
            var methodNote = noteLabel();
            upscaling.Controls.Add(comboRow("Method", method, methodNote));
            upscaling.Controls.Add(methodNote);
            // FEAT-GT7-180: shown when a configuration saved before has both NVIDIA DLSS and the PS4 Pro
            // 3200 x 1800 picture on (see the PS4 Pro mode section).
            var methodProWarning = noteLabel();
            methodProWarning.ForeColor = Dark.AccentText;
            methodProWarning.Text = "NVIDIA DLSS and the PS4 Pro 3200 x 1800 picture are both on, and they do not go together. " +
                                    "Pick another method here, or in PS4 Pro mode below pick \"PS4 (no Pro mode)\" or " +
                                    "\"PS4 Pro, 1080p TV\", or untick the 3200 x 1800 box.";
            methodProWarning.Visible = false;
            upscaling.Controls.Add(methodProWarning);

            // Quality: the size the upscaler scales to. The game always draws 1920x1080, so a mode
            // picks the output (the picture times 1.5, 1.72, 2 or 3), which is then fitted to the
            // window; Auto scales straight to the window (ryty_upscale.h in the renderer).
            var qualityKeys = new[] { "auto", "native", "quality", "balanced", "performance", "ultra_performance" };
            var qualityScales = new[] { 0.0, 1.0, 1.5, 1.0 / 0.58, 2.0, 3.0 };
            var quality = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            quality.Items.AddRange(new object[] { "Auto (window size)", "Native (anti-aliasing only)", "Quality", "Balanced", "Performance", "Ultra Performance" });
            quality.SelectedIndex = Math.Max(0, Array.IndexOf(qualityKeys, ReadStringSetting("GPU", "upscale_quality", "auto")));
            var qualityNote = noteLabel();
            upscaling.Controls.Add(comboRow("Quality", quality, qualityNote));
            upscaling.Controls.Add(qualityNote);
            var rcas = AddOption(upscaling, "Sharpening (FSR)", "GPU", "rcas_enabled", true);

            // FEAT-GT7-181: the texture detail of the renderer's temporal DLSS (DLSS-GT7-002 there). With DLSS
            // the game still draws 1920x1080 and picks its texture levels for that size, so the renderer reads
            // them finer: log2(render / output) - 1 levels, as NVIDIA's DLSS guide asks. The amount follows the
            // size DLSS scales to: -2 to 3840x2160, about -1.4 to 2560x1440, nothing when the picture is not
            // enlarged (a 1080p window, the Native quality). Some may find it too sharp (fine textures can
            // shimmer in motion): Reduced drops the -1, Off the whole bias. The note gives the levels for the
            // chosen quality. GPU.dlss_texture_detail (full, reduced, off); the game gets it as
            // RYGT_DLSS_TEMPORAL_MIP_BIAS and RYGT_DLSS_TEMPORAL_MIP_EXTRA. The row only shows where the renderer
            // applies the bias: NVIDIA DLSS through Streamline (sl.dlss.dll), no frame generation (temporal DLSS
            // is off with it), and a renderer that reads those switches (an older one would ignore the setting).
            var detailKeys = new[] { "full", "reduced", "off" };
            var detail = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            detail.Items.AddRange(new object[] { "Full (recommended)", "Reduced", "Off" });
            detail.SelectedIndex = Math.Max(0, Array.IndexOf(detailKeys, ReadStringSetting("GPU", "dlss_texture_detail", "full")));
            var detailNote = noteLabel();
            var detailRow = comboRow("Texture detail", detail, detailNote);
            upscaling.Controls.Add(detailRow);
            upscaling.Controls.Add(detailNote);
            bool detailKnown = NamesSwitch(rendererImage, "RYGT_DLSS_TEMPORAL_MIP_EXTRA");

            // NVIDIA files: the Download button, the status of what is there, and Add from a folder.
            var dlssStatus = noteLabel();
            dlssStatus.Margin = new Padding(0, 2, 0, 2);
            var dlssButtons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
            downloadDlss = new Button { Text = "Download NVIDIA DLSS " + DlssDownload.DlssVersion, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            addDlss = new Button { Text = "Add from a folder...", AutoSize = true, Margin = new Padding(0) };
            dlssButtons.Controls.Add(downloadDlss);
            dlssButtons.Controls.Add(addDlss);
            upscaling.Controls.Add(dlssButtons);
            upscaling.Controls.Add(dlssStatus);

            // Frame generation: one at a time, AMD's (any GPU, about 2x) or NVIDIA's (RTX 40 and
            // newer, Windows' hardware-accelerated GPU scheduling on; 2x-6x, the multiplier is
            // GPU.dlssg_frames, which the game gets as RYGT_DLSSG_FRAMES). Settings:
            // GPU.framegen_enabled, dlssg_enabled.
            var frameGen = Section(graphicsPage, "Frame generation");
            var fg = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            fg.Items.AddRange(new object[] { "Off", "AMD FSR 3.1 (any GPU)", "NVIDIA DLSS (RTX 40+)" });
            var fgNote = noteLabel();
            frameGen.Controls.Add(comboRow("Method", fg, fgNote));
            // FEAT-GT7-180: shown, in the warning colour, while NVIDIA DLSS frame generation is the method.
            var fgAdvice = noteLabel();
            fgAdvice.ForeColor = Dark.AccentText;
            fgAdvice.Text = "Not recommended for now: in our tests the game ran smoother without NVIDIA DLSS frame generation.";
            fgAdvice.Visible = false;
            frameGen.Controls.Add(fgAdvice);
            var mult = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            mult.Items.AddRange(new object[] { "2x", "3x (RTX 50)", "4x (RTX 50)", "5x (RTX 50)", "6x (RTX 50)" });
            mult.SelectedIndex = Math.Max(1, Math.Min(5, ReadIntSetting("GPU", "dlssg_frames", 1))) - 1;
            var multRow = comboRow("Multiplier", mult, null);
            frameGen.Controls.Add(multRow);
            frameGen.Controls.Add(fgNote);

            // FIX-GT7-141: HDR output (GPU.hdr_allowed, the renderer's setting) and the display the
            // game is told of (GPU.hdr_display_peak_nits, which libSceSystemService reads when the
            // game starts: GT7 tone maps its scene to that peak). 1000 unless set, as the library
            // takes it; 0: the display as Windows describes it. The box goes from 0 to 300, the
            // lowest peak offered.
            var hdr = Section(graphicsPage, "HDR");
            var hdrOutput = AddOption(hdr, "HDR output", "GPU", "hdr_allowed", false);
            // A value written by hand may be a decimal or "<peak>,<full frame>,<min>": its peak is
            // shown, and the file keeps it until the box is changed.
            int hdrPeakStored = 1000;
            {
                object stored = GpuSetting(root, "hdr_display_peak_nits");
                string text = stored == null || stored is bool ? null
                    : Convert.ToString(stored, System.Globalization.CultureInfo.InvariantCulture).Split(',')[0];
                double peak;
                if (text != null && double.TryParse(text, System.Globalization.NumberStyles.Float,
                                                    System.Globalization.CultureInfo.InvariantCulture, out peak) &&
                    !double.IsNaN(peak) && !double.IsInfinity(peak))
                {
                    // As the library reads it: 0 asks Windows, under 100 is no peak (1000).
                    hdrPeakStored = peak == 0 ? 0 : peak < 100 ? 1000 : (int)Math.Min(10000, Math.Round(peak));
                }
            }
            if (hdrPeakStored > 0 && hdrPeakStored < 300) hdrPeakStored = 300;
            var hdrPeak = new NumericUpDown { Minimum = 0, Maximum = 10000, DecimalPlaces = 0, Increment = 50, Width = 90,
                                              TextAlign = HorizontalAlignment.Right, Value = hdrPeakStored,
                                              Enabled = hdrOutput.Checked };
            hdr.Controls.Add(new SettingRow("Display peak brightness (nits, 0 = from Windows)", hdrPeak));
            hdr.Controls.Add(Hint("Turn on HDR in Windows first; these settings apply at the next game start. 1000 suits most HDR displays: " +
                                  "set your display's real peak if bright areas lose their detail or look dull. 0 takes the peak Windows reports for the display."));
            // The game's interface on an HDR output (GPU.hdr_paper_white_nits, the renderer's
            // FIX-GT7-143: it replaces the 250 nits GT7 gives its menus and HUD). 250 is the game's
            // own and writes nothing until the box is changed.
            var hdrWhite = new NumericUpDown { Minimum = 50, Maximum = 1000, DecimalPlaces = 0, Increment = 10, Width = 90,
                                               TextAlign = HorizontalAlignment.Right,
                                               Value = Math.Max(50, Math.Min(1000, ReadIntSetting("GPU", "hdr_paper_white_nits", 250))),
                                               Enabled = hdrOutput.Checked };
            hdr.Controls.Add(new SettingRow("Interface brightness (nits)", hdrWhite));
            hdr.Controls.Add(Hint("The white of the game's menus and race HUD on an HDR display. 250 is the game's own; lower it if they glare beside the scenery. Applies at the next game start."));
            hdrWhite.ValueChanged += delegate
            {
                try { WriteSetting("GPU", "hdr_paper_white_nits", (int)hdrWhite.Value); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            hdrOutput.CheckedChanged += delegate { hdrPeak.Enabled = hdrWhite.Enabled = hdrOutput.Checked; };
            int hdrPeakShown = hdrPeakStored;
            hdrPeak.ValueChanged += delegate
            {
                int value = (int)hdrPeak.Value;
                if (value > 0 && value < 300)
                {
                    // One step down from 300 is 0 (Windows' data); anything else under 300, typed
                    // or stepped up from 0, is 300.
                    hdrPeak.Value = hdrPeakShown == 300 && value == 300 - (int)hdrPeak.Increment ? 0 : 300;
                    return;
                }
                if (value == hdrPeakShown) return;
                try
                {
                    WriteSetting("GPU", "hdr_display_peak_nits", value);
                    hdrPeakShown = value;
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };

            // NEO-GT7: GT7's own PS4 Pro mode and six of its settings. GPU.ps4_pro_mode: off (PS4), 4k (a Pro on
            // a 4K TV), 1080p (a Pro on a 1080p TV), full (all six) or custom; the boxes are GPU.ps4_pro_checkerboard,
            // ps4_pro_blur, ps4_pro_replays60, ps4_pro_replay_blur, ps4_pro_garage60 and ps4_pro_lod. A mode other
            // than custom ticks the boxes as that console has them, and the boxes pick the mode back: as a console
            // has them, that mode; any other choice, custom. Only the checkerboard box turns the Pro mode on in
            // custom. StartGame turns them into RYGT_NEO, RYGT_NEO_TV and RYGT_NEO_EXTRAS (NEO-GT7-014 in the renderer).
            var pro = Section(graphicsPage, "PS4 Pro mode");
            var proKeys = new[] { "off", "4k", "1080p", "full", "custom" };
            var proMode = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, FlatStyle = FlatStyle.Flat };
            proMode.Items.AddRange(new object[] { "PS4 (no Pro mode)", "PS4 Pro, 4K TV", "PS4 Pro, 1080p TV", "Full (everything on)", "Custom" });
            proMode.SelectedIndex = Math.Max(0, Array.IndexOf(proKeys, ReadStringSetting("GPU", "ps4_pro_mode", "off")));
            var proNote = noteLabel();
            pro.Controls.Add(comboRow("Mode", proMode, proNote));
            pro.Controls.Add(proNote);
            // FEAT-GT7-180: NVIDIA DLSS and the 3200 x 1800 checkerboard picture are not offered together. The
            // game rebuilds that picture from several frames itself, and DLSS on top cost about 6 ms of GPU time
            // a frame on an RTX 4070 (the only GPU it was measured on). Picking one while the other is on is
            // refused with a message; a configuration saved before with both gets a warning in both sections.
            // The rule is this page's: a RYGT_NEO set from the Advanced page's own variables is not checked.
            var proDlssWarning = noteLabel();
            proDlssWarning.ForeColor = Dark.AccentText;
            proDlssWarning.Text = "The 3200 x 1800 picture and NVIDIA DLSS are both on, and they do not go together. " +
                                  "Untick the 3200 x 1800 box, or pick another upscaling method above.";
            proDlssWarning.Visible = false;
            pro.Controls.Add(proDlssWarning);
            int lastPro = proMode.SelectedIndex;
            bool proReverting = false;
            var proBoxKeys = new[] { "ps4_pro_checkerboard", "ps4_pro_blur", "ps4_pro_replays60", "ps4_pro_replay_blur",
                                     "ps4_pro_garage60", "ps4_pro_lod" };
            var proBoxTexts = new[]
            {
                "3200 x 1800 with the game's own checkerboard and its 4K interface",
                "Motion blur in races",
                "Replays and photo mode at 60 fps instead of 30",
                "Extra blur in replays and photo mode",
                "High-quality garage scenes at 60 fps instead of 30",
                "Level of detail kept 1.3 times farther (buildings, car geometry and decals)",
            };
            var proBoxes = new CheckBox[proBoxKeys.Length];
            for (int i = 0; i < proBoxKeys.Length; i++) proBoxes[i] = AddOption(pro, proBoxTexts[i], "GPU", proBoxKeys[i], false);
            pro.Controls.Add(Hint("The game's own PS4 Pro settings. A mode ticks the boxes as that console has them; change a box to make " +
                                  "your own (Custom). Every box costs GPU time, the checkerboard most. Applies at the next game start."));
            // Checked on an NVIDIA RTX 4070 only, where the game alone used about 5 GB of video memory in a race with
            // everything on. With the checkerboard a race can drop below 30 fps. Without the checkerboard, the boxes
            // run in the PS4 mode, except all five together: that is the PS4 Pro 1080p TV mode.
            bool proApplying = false;
            // FEAT-GT7-180: GPU.dlss_enabled can be saved on while the list shows another method (the NVIDIA files
            // are missing). Turning the 3200 x 1800 picture on then switches that setting off, or adding the
            // files later would bring both on without a word.
            Action proDropUnshownDlss = delegate
            {
                if (method.SelectedIndex == 3 || !ReadSetting("GPU", "dlss_enabled", false)) return;
                try { WriteSetting("GPU", "dlss_enabled", false); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action proDlssRefresh = delegate
            {
                bool both = proBoxes[0].Checked && method.SelectedIndex == 3;
                methodProWarning.Visible = both;
                proDlssWarning.Visible = both;
            };
            Action proRefresh = delegate
            {
                proDlssRefresh();
                int p = proMode.SelectedIndex;
                proNote.Text = p == 0 ? "As a PS4: native 1920 x 1080, no box ticked."
                             : p == 1 ? "As a PS4 Pro on a 4K TV: the checkerboard picture, the other settings off."
                             : p == 2 ? "As a PS4 Pro on a 1080p TV (PS4 Pro mode on): native 1920 x 1080, the five other settings on."
                             : p == 3 ? "Everything on: the 4K TV picture with the 1080p TV's settings. The heaviest."
                             : proBoxes[0].Checked ? "Your own choice of the boxes below, in the game's PS4 Pro mode (checkerboard)."
                                                   : "Your own choice of the boxes below, in the PS4 mode (no checkerboard).";
            };
            // The boxes a mode ticks: 4K TV the checkerboard alone, 1080p TV the five others, Full all six.
            Action proApply = delegate
            {
                int p = proMode.SelectedIndex;
                if (p == 4) return;
                proApplying = true;
                for (int i = 0; i < proBoxes.Length; i++)
                    proBoxes[i].Checked = p == 3 || (p == 1 && i == 0) || (p == 2 && i != 0);
                proApplying = false;
            };
            // The mode the boxes make: none PS4, the checkerboard alone 4K TV, the five others 1080p TV, all six
            // Full, anything else Custom (keeping the boxes as they are).
            Func<int> proModeOfBoxes = delegate
            {
                int others = 0;
                for (int i = 1; i < proBoxes.Length; i++) if (proBoxes[i].Checked) others++;
                bool checkerboard = proBoxes[0].Checked;
                if (!checkerboard && others == 0) return 0;
                if (checkerboard && others == 0) return 1;
                if (!checkerboard && others == proBoxes.Length - 1) return 2;
                if (checkerboard && others == proBoxes.Length - 1) return 3;
                return 4;
            };
            foreach (var box in proBoxes)
            {
                var changed = box;
                box.CheckedChanged += delegate
                {
                    if (proApplying) return;
                    if (changed == proBoxes[0] && changed.Checked && method.SelectedIndex == 3)
                    {
                        // FEAT-GT7-180: not with NVIDIA DLSS. The box is unticked before the message (its own
                        // handler has already saved it ticked), and the mode stays as it is: Custom stays Custom.
                        proApplying = true;
                        changed.Checked = false;
                        proApplying = false;
                        MessageBox.Show(this, "The 3200 x 1800 picture cannot be used with NVIDIA DLSS: the game already rebuilds " +
                                              "that picture itself. Pick another upscaling method first.");
                        return;
                    }
                    if (changed == proBoxes[0] && changed.Checked) proDropUnshownDlss();
                    int boxesMode = proModeOfBoxes();
                    if (boxesMode != proMode.SelectedIndex) proMode.SelectedIndex = boxesMode;
                    else proRefresh();
                };
            }
            proMode.SelectedIndexChanged += delegate
            {
                int picked = proMode.SelectedIndex;
                // FEAT-GT7-180: the modes with the checkerboard (4K TV, Full) are refused while NVIDIA DLSS is the
                // upscaling method. Custom keeps the boxes as they are, and the box has its own check.
                if (!proReverting && (picked == 1 || picked == 3) && method.SelectedIndex == 3 && !proBoxes[0].Checked)
                {
                    MessageBox.Show(this, "This mode draws the 3200 x 1800 picture, which cannot be used with NVIDIA DLSS: the game " +
                                          "already rebuilds that picture itself. Pick another upscaling method first.");
                    proReverting = true;
                    proMode.SelectedIndex = lastPro;
                    proReverting = false;
                    return;
                }
                lastPro = picked;
                if (picked == 1 || picked == 3) proDropUnshownDlss();
                try { WriteSetting("GPU", "ps4_pro_mode", proKeys[Math.Max(0, proMode.SelectedIndex)]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
                proApply();
                proRefresh();
            };
            proApply();
            proRefresh();

            bool loading = true;
            Func<string, bool> has = name => File.Exists(Path.Combine(dlssDir, name));
            Func<bool> dlssReady = () => has("nvngx_dlss.dll") && ((has("sl.interposer.dll") && has("sl.common.dll") && has("sl.dlss.dll")) || has("ryGT_dlss.dll"));
            Func<bool> dlssgReady = () => has("sl.interposer.dll") && has("sl.common.dll") && has("sl.dlss_g.dll") && has("nvngx_dlssg.dll");
            Action save = delegate
            {
                if (loading) return;
                try
                {
                    int m = method.SelectedIndex;
                    WriteSetting("GPU", "fsr_enabled", m == 1 || m == 2);
                    if (m == 1 || m == 2) WriteSetting("GPU", "fsr3_enabled", m == 2);
                    WriteSetting("GPU", "dlss_enabled", m == 3);
                    WriteSetting("GPU", "upscale_quality", qualityKeys[Math.Max(0, quality.SelectedIndex)]);
                    WriteSetting("GPU", "framegen_enabled", fg.SelectedIndex == 1);
                    WriteSetting("GPU", "dlssg_enabled", fg.SelectedIndex == 2);
                    if (fg.SelectedIndex == 2) WriteSetting("GPU", "dlssg_frames", mult.SelectedIndex + 1);
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action refresh = delegate
            {
                bool dlssOk = dlssReady(), dlssgOk = dlssgReady();
                int m = method.SelectedIndex;
                methodNote.Text = m == 0 ? "The picture is stretched to the window as it is."
                                : m == 1 ? "Any GPU. Light and simple."
                                : m == 2 ? (fsr3Installed ? "Any GPU. Sharper than FSR 1." : "Needs amd_fidelityfx_vk.dll next to GT7.exe.")
                                : dlssOk ? "RTX cards only. The best image."
                                         : "RTX cards only. Download the NVIDIA files below first.";
                bool scaler = m == 2 || m == 3;
                quality.Enabled = scaler;
                int q = Math.Max(0, quality.SelectedIndex);
                if (!scaler)
                    qualityNote.Text = m == 1 ? "FSR 1 always scales to the window size." : "Pick FSR 3.1 or DLSS to choose a quality.";
                else if (q == 0)
                    qualityNote.Text = "1920 x 1080, scaled to the window size (only when the window is bigger).";
                else
                {
                    int w = (int)Math.Round(1920 * qualityScales[q]), h = (int)Math.Round(1080 * qualityScales[q]);
                    qualityNote.Text = q == 1 ? "1920 x 1080 at its own size, anti-aliased, then fitted to the window."
                                              : "1920 x 1080 -> " + w + " x " + h + ", then fitted to the window. Higher is sharper and costs more GPU.";
                }
                rcas.Visible = m == 1 || m == 2;
                // FEAT-GT7-181 (the row on screen is the list's parent, as for the multiplier below).
                bool detailShown = m == 3 && detailKnown && fg.SelectedIndex == 0 && has("sl.dlss.dll");
                (detail.Parent ?? detailRow).Visible = detailShown;
                detailNote.Visible = detailShown;
                int d = Math.Max(0, detail.SelectedIndex);
                // How many levels finer: how much DLSS enlarges the picture (log2 of the scale), plus one for
                // Full. With Auto the scale is the window's, which the launcher does not know: two examples.
                Func<double, string> levels = value =>
                {
                    string text = value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    return text + (text == "1" ? " level" : " levels");
                };
                double enlarged = q >= 2 ? Math.Log(qualityScales[q], 2) : 0;
                if (d == 2)
                    detailNote.Text = "Textures as the game reads them at 1920 x 1080: the softest, nothing added.";
                else if (q == 1)
                    detailNote.Text = "No effect at the Native quality: DLSS does not enlarge the picture there.";
                else if (d == 0)
                    detailNote.Text = "Textures are read finer, one level for each doubling of the picture by DLSS plus one more, as NVIDIA advises: " +
                                      (q == 0 ? "about 1.4 levels in a 2560 x 1440 window, 2 in a 3840 x 2160 one, none at 1920 x 1080. "
                                              : levels(enlarged + 1) + " at this quality. ") +
                                      "The sharpest; fine textures can shimmer in motion. It comes on top of Texture sharpness: with Full, leave that on Game default.";
                else
                    detailNote.Text = "One level less than Full: " +
                                      (q == 0 ? "about 0.4 levels in a 2560 x 1440 window, 1 in a 3840 x 2160 one, none at 1920 x 1080. "
                                              : levels(enlarged) + " at this quality. ") +
                                      "A little softer, less shimmer. It comes on top of Texture sharpness.";
                fgNote.Text = fg.SelectedIndex == 0 ? "Off: the game's own frame rate."
                            : fg.SelectedIndex == 1 ? (fsr3Installed ? "About 2x the frames on any GPU." : "Needs amd_fidelityfx_vk.dll next to GT7.exe.")
                            : dlssgOk ? "Generated frames between game frames. RTX 40 or newer."
                                      : "RTX 40 or newer. Download the NVIDIA files above first.";
                // The launcher turns the label + list rows into SettingRows (Rowify), so the row on
                // screen is the list's parent, not multRow.
                (mult.Parent ?? multRow).Visible = fg.SelectedIndex == 2;
                fgAdvice.Visible = fg.SelectedIndex == 2;
                var missing = new List<string>();
                if (!has("nvngx_dlss.dll")) missing.Add("nvngx_dlss.dll");
                if (!has("sl.interposer.dll") || !has("sl.common.dll")) missing.Add("Streamline");
                else if (!has("sl.dlss.dll") && !has("ryGT_dlss.dll")) missing.Add("sl.dlss.dll");
                if (!has("sl.dlss_g.dll") && has("sl.interposer.dll")) missing.Add("sl.dlss_g.dll");
                if (!has("nvngx_dlssg.dll")) missing.Add("nvngx_dlssg.dll");
                bool none = !has("nvngx_dlss.dll") && !has("sl.interposer.dll") && !has("nvngx_dlssg.dll");
                dlssStatus.Text = none
                    ? "NVIDIA DLSS files: not installed. RyGT7 does not include them; the button downloads NVIDIA's own signed files from NVIDIA's GitHub."
                    : "NVIDIA DLSS files: " + (has("nvngx_dlss.dll") ? "DLSS " + FileVersion(Path.Combine(dlssDir, "nvngx_dlss.dll")) : "no DLSS") +
                      ", " + (has("sl.interposer.dll") ? "Streamline " + FileVersion(Path.Combine(dlssDir, "sl.interposer.dll")) : "no Streamline") +
                      (missing.Count == 0 ? ". Ready." : ". Missing: " + string.Join(", ", missing) + ".");
                proDlssRefresh(); // FEAT-GT7-180
            };
            Action load = delegate
            {
                loading = true;
                bool dlssOn = ReadSetting("GPU", "dlss_enabled", false) && dlssReady();
                bool fsrOn = ReadSetting("GPU", "fsr_enabled", false);
                method.SelectedIndex = dlssOn ? 3 : !fsrOn ? 0 : ReadSetting("GPU", "fsr3_enabled", true) && fsr3Installed ? 2 : 1;
                bool dlssgOn = ReadSetting("GPU", "dlssg_enabled", false) && dlssgReady();
                fg.SelectedIndex = dlssgOn ? 2 : ReadSetting("GPU", "framegen_enabled", false) && fsr3Installed ? 1 : 0;
                loading = false;
                refresh();
            };
            Action refreshDlss = delegate { load(); };
            int lastMethod = -1, lastFg = -1;
            method.SelectedIndexChanged += delegate
            {
                if (!loading && ((method.SelectedIndex == 3 && !dlssReady()) || (method.SelectedIndex == 2 && !fsr3Installed)))
                {
                    MessageBox.Show(this, method.SelectedIndex == 3
                        ? "DLSS needs NVIDIA's files. Use \"Download NVIDIA DLSS\" first (RTX cards only)."
                        : "FSR 3.1 needs amd_fidelityfx_vk.dll next to GT7.exe.");
                    method.SelectedIndex = Math.Max(0, lastMethod);
                    return;
                }
                // FEAT-GT7-180: not with the PS4 Pro 3200 x 1800 picture (unless a saved configuration already
                // has both: lastMethod is then DLSS, and the warnings say so).
                if (!loading && method.SelectedIndex == 3 && lastMethod != 3 && proBoxes[0].Checked)
                {
                    MessageBox.Show(this, "NVIDIA DLSS cannot be used with the PS4 Pro 3200 x 1800 picture: the game already rebuilds " +
                                          "that picture itself. In PS4 Pro mode, pick \"PS4 (no Pro mode)\" or \"PS4 Pro, 1080p TV\" first, " +
                                          "or untick the 3200 x 1800 box.");
                    method.SelectedIndex = Math.Max(0, lastMethod);
                    return;
                }
                lastMethod = method.SelectedIndex;
                save();
                refresh();
            };
            quality.SelectedIndexChanged += delegate { save(); refresh(); };
            detail.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "dlss_texture_detail", detailKeys[Math.Max(0, detail.SelectedIndex)]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
                refresh();
            };
            fg.SelectedIndexChanged += delegate
            {
                if (!loading && ((fg.SelectedIndex == 2 && !dlssgReady()) || (fg.SelectedIndex == 1 && !fsr3Installed)))
                {
                    MessageBox.Show(this, fg.SelectedIndex == 2
                        ? "DLSS frame generation needs NVIDIA's files. Use \"Download NVIDIA DLSS\" first (RTX 40 or newer)."
                        : "FSR frame generation needs amd_fidelityfx_vk.dll next to GT7.exe.");
                    fg.SelectedIndex = Math.Max(0, lastFg);
                    return;
                }
                lastFg = fg.SelectedIndex;
                save();
                refresh();
            };
            mult.SelectedIndexChanged += delegate { save(); };
            load();
            lastMethod = method.SelectedIndex;
            lastFg = fg.SelectedIndex;
            addDlss.Click += delegate
            {
                // The game keeps the DLSS files open while it runs (#61).
                var games = Process.GetProcessesByName("GT7");
                bool gameRunning = games.Length != 0;
                foreach (var process in games) process.Dispose();
                if (gameRunning)
                {
                    MessageBox.Show(this, "Close GT7 before adding or replacing the DLSS files.");
                    return;
                }
                using (var dialog = new FolderBrowserDialog
                {
                    Description = "Pick the folder with your NVIDIA DLSS files: nvngx_dlss.dll and Streamline's " +
                                  "sl.*.dll for upscaling; for frame generation also nvngx_dlssg.dll and " +
                                  "NvLowLatencyVk.dll (from NVIDIA's DLSS and Streamline SDKs)."
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var names = new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "sl.common.dll",
                                        "sl.dlss.dll", "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "NvLowLatencyVk.dll" };
                    int copied = 0;
                    try
                    {
                        Directory.CreateDirectory(dlssDir);
                        foreach (var name in names)
                        {
                            // Found anywhere under the picked folder (the SDKs keep them in bin\x64).
                            var found = Directory.GetFiles(dialog.SelectedPath, name, SearchOption.AllDirectories);
                            if (found.Length == 0) continue;
                            // The upscaler is checked to be NVIDIA's 64-bit DLSS library, and the one
                            // it replaces is kept as a .bak (#61).
                            if (name == "nvngx_dlss.dll") InstallDlssLibrary(found[0], dlssDir);
                            else File.Copy(found[0], Path.Combine(dlssDir, name), true);
                            ++copied;
                        }
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(this, "Could not copy the DLSS files: " + e.Message);
                    }
                    refreshDlss();
                    MessageBox.Show(this, copied == 0
                        ? "No DLSS files were found in that folder."
                        : "Copied " + copied + " DLSS file(s) into the dlss folder.");
                }
            };
            // Download NVIDIA DLSS: NVIDIA's DLSS and Streamline files from NVIDIA's GitHub, pinned
            // versions checked by SHA-256 and NVIDIA's signature (DlssDownload.cs); no admin rights.
            downloadDlss.Click += delegate
            {
                var games = Process.GetProcessesByName("GT7");
                bool gameRunning = games.Length != 0;
                foreach (var process in games) process.Dispose();
                if (gameRunning)
                {
                    MessageBox.Show(this, "Close GT7 before downloading the DLSS files.");
                    return;
                }
                if (MessageBox.Show(this,
                        "Download NVIDIA DLSS " + DlssDownload.DlssVersion + " and Streamline " + DlssDownload.StreamlineVersion +
                        " (about 70 MB) into the dlss folder?\n\n" +
                        "From: NVIDIA's own GitHub repositories (github.com/NVIDIA/DLSS and github.com/NVIDIA-RTX/Streamline). " +
                        "RyGT7 does not ship these files.\n\n" +
                        "Checks: each file must match a pinned SHA-256 hash and carry NVIDIA Corporation's digital " +
                        "signature, or it is not used.\n\n" +
                        "Needs an NVIDIA RTX card (frame generation: RTX 40 or newer). The files are NVIDIA's, under " +
                        "NVIDIA's licenses (see dlss\\" + DlssDownload.NoticeName + ").", "Download NVIDIA DLSS",
                        MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                // Busy like Set up, so the state timer keeps Play, Set up and both DLSS buttons off
                // while the files in the dlss folder are replaced one by one.
                busy = true;
                RefreshState();
                status.Text = "Downloading the NVIDIA DLSS files...";
                var worker = new Thread(() =>
                {
                    string message;
                    try
                    {
                        int count = DlssDownload.Install(dlssDir, text => BeginInvoke((Action)(() => dlssStatus.Text = text)));
                        message = count == 0 ? "The NVIDIA DLSS files were already up to date."
                                             : "Downloaded and checked " + count + " NVIDIA DLSS file(s).";
                    }
                    catch (Exception e)
                    {
                        message = "Could not download the DLSS files: " + e.Message;
                    }
                    BeginInvoke((Action)(() =>
                    {
                        busy = false;
                        RefreshState();
                        refreshDlss();
                        MessageBox.Show(this, message);
                    }));
                }) { IsBackground = true };
                worker.Start();
            };
            // Race frame rate (GPU.race_fps_lock, the game gets it as SHADGT_FPS_LOCK): an even 30
            // by default; 60 when the PC keeps up. Busy race sections can stay under 60, and frames of
            // 16.7, 33 and 50 ms mixed judder more than an even 30.
            var fpsRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var fpsLock = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            fpsLock.Items.AddRange(new object[] { "30 fps (steady)", "60 fps" });
            fpsRow.Controls.Add(new Label { Text = "Race frame rate", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            fpsRow.Controls.Add(fpsLock);
            graphics.Controls.Add(fpsRow);
            fpsLock.SelectedIndex = ReadIntSetting("GPU", "race_fps_lock", 30) == 60 ? 1 : 0;
            // Texture sharpness (GPU.texture_lod_bias, the game gets it as RYGT_TEXTURE_LOD_BIAS):
            // textures are read at finer detail levels than GT7 asks for. Two levels were far too
            // strong in testing, so the list stops at one.
            var sharpnessValues = new[] { "0", "-0.5", "-1" };
            var sharpness = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            sharpness.Items.AddRange(new object[] { "Game default", "Sharper (half a level)", "Sharpest (one level)" });
            graphics.Controls.Add(new SettingRow("Texture sharpness", sharpness) { NoLine = true });
            // The setting's line goes under its hint.
            var sharpnessHint = Hint("Sharper textures cost some GPU time and can shimmer in the distance.");
            sharpnessHint.AutoSize = false;
            sharpnessHint.MaximumSize = Size.Empty;
            sharpnessHint.Dock = DockStyle.Fill;
            sharpnessHint.Height = 28;
            sharpnessHint.Margin = new Padding(0);
            sharpnessHint.Paint += (sender, e) => { using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, sharpnessHint.Height - 1, sharpnessHint.Width, sharpnessHint.Height - 1); };
            graphics.Controls.Add(sharpnessHint);
            sharpness.SelectedIndex = Math.Max(0, Array.IndexOf(sharpnessValues, ReadStringSetting("GPU", "texture_lod_bias", "0")));
            sharpness.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "texture_lod_bias", sharpnessValues[sharpness.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            // Shader cache size (GPU.driver_cache_gb, GPU.driver_cache_unlimited; the game gets it as
            // RYGT_DRIVER_CACHE_MAX_MB): the renderer's saved GPU shader cache, like NVIDIA's own
            // setting. 0 turns it off; a bigger cache keeps more cars and tracks compiled, and is saved
            // less often as it grows (each save copies it whole).
            // The driver keeps it in RAM, a little more than the file; a file over the limit is not loaded.
            var cacheRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            cacheRow.Controls.Add(new Label { Text = "Shader cache size (GB, 0 = off)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            var cacheSize = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 0, Increment = 1, Width = 70,
                                                Value = Math.Max(0, Math.Min(100, ReadIntSetting("GPU", "driver_cache_gb", 2))) };
            var cacheUnlimited = new DarkCheckBox { Text = "Unlimited", AutoSize = true, Checked = ReadSetting("GPU", "driver_cache_unlimited", false), Margin = new Padding(10, 4, 0, 0) };
            cacheSize.Enabled = !cacheUnlimited.Checked;
            cacheRow.Controls.Add(cacheSize);
            cacheRow.Controls.Add(cacheUnlimited);
            graphics.Controls.Add(cacheRow);
            cacheSize.ValueChanged += delegate
            {
                try { WriteSetting("GPU", "driver_cache_gb", (int)cacheSize.Value); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            cacheUnlimited.CheckedChanged += delegate
            {
                cacheSize.Enabled = !cacheUnlimited.Checked;
                try { WriteSetting("GPU", "driver_cache_unlimited", cacheUnlimited.Checked); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            // FIX-220: AMD GPUs only, off by default: shaders that read vertex values are interpolated
            // through KHR barycentrics (GPU.amd_graphics_fix, the game gets it as
            // RYGT_AMD_SINGLE_SAMPLE_BARYCENTRIC). It fixes some AMD rendering glitches but makes shader
            // loading slower; other GPUs ignore it.
            AddOption(graphics, "AMD graphics fix (AMD GPUs only; slower shader loading)", "GPU", "amd_graphics_fix", false);
            // A tester's RX 6000 series card crashed loading into races with it on (2026-10-30).
            graphics.Controls.Add(Hint("Can crash RX 6000 series GPUs when loading into a race. If the game crashes when a race loads, turn this off."));
            // (FEAT-GTS-290: the shader pre-translation and compile pack boxes are on the Shaders page.)
            fpsLock.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "race_fps_lock", fpsLock.SelectedIndex == 1 ? 60 : 30); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };

            // ---- Controls: the controller, keyboard and wheel, drawn (ControlsPage); the controller
            // ports open from it. It saves as you change things.
            var controlsHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 24) };
            controls = new ControlsPage(root, ReadIntSetting, WriteSetting, OpenPorts) { Dock = DockStyle.Fill };
            controlsHost.Controls.Add(controls);
            pagesHost.Controls.Add(controlsHost);
            pages["Controls"] = controlsHost;

            // ---- Online: hosting, joining a friend (Online.cs)
            BuildOnlinePage();
            // ---- Account: the online account (Online.cs); nothing asks for one at start
            BuildAccountPage();

            return refresh;
        }

        static readonly string[] FirmwareModulesGt7 = { "libSceFont", "libSceFontFt", "libSceFreeTypeOt", "libSceJson2", "libSceNgs2" };

        ControlsPage controls;


        /// For --snapshot: the Controls page's tab (0 controller, 1 keyboard, 2 wheel).
        public void ShowControlsTab(int index)
        {
            if (controls != null) controls.ShowTab(index);
        }


        /// The largest dedicated video memory of the display adapters, in GB (0 when unknown):
        /// the drivers' HardwareInformation.qwMemorySize (64-bit, unlike WMI's AdapterRAM).
        static int InstalledVramGb()
        {
            long best = 0;
            try
            {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (var name in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using (var adapter = cls.OpenSubKey(name))
                            {
                                object size = adapter == null ? null : adapter.GetValue("HardwareInformation.qwMemorySize");
                                if (size is long) best = Math.Max(best, (long)size);
                            }
                        }
                        catch { } // Properties and other keys can't be opened
                    }
                }
            }
            catch { }
            return (int)Math.Round(best / (1024.0 * 1024 * 1024));
        }


        // Video memory (GPU.vram_budget_mb): how much of the graphics card's memory the renderer
        // plans for. Auto (0) uses the driver's budget, as before. A smaller value makes the
        // renderer free cached textures and render targets it hasn't used lately sooner, leaving
        // the rest of the card to other programs (or a card that shares memory).
        void AddVideoMemorySetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label { Text = "Video memory (VRAM)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var vram = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            int card = InstalledVramGb();
            var sizes = new List<int> { 0 };
            foreach (int gb in new[] { 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 48 })
            {
                if (card == 0 ? gb <= 24 : gb <= card) sizes.Add(gb);
            }
            foreach (int gb in sizes)
            {
                vram.Items.Add(gb == 0 ? (card > 0 ? "Auto (" + card + " GB card)" : "Auto")
                                       : gb + " GB" + (gb == card ? " (all)" : ""));
            }
            int stored = ReadIntSetting("GPU", "vram_budget_mb", 0);
            int index = 0;
            for (int i = 1; i < sizes.Count; ++i)
            {
                if (sizes[i] * 1024 <= stored) index = i; // the largest choice not above the stored value
            }
            vram.SelectedIndex = index;
            vram.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "vram_budget_mb", sizes[vram.SelectedIndex] * 1024); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(vram);
            panel.Controls.Add(row);
        }


        // FEAT-RT-021: keep the values saved by the legacy GT7 launcher. The display labels
        // belong to the UI; the renderer receives the keys below at process start.
        DarkComboBox AddHeadsetChoice(TableLayoutPanel panel, string label, string key,
                                      string[] values, string[] labels, string fallback)
        {
            var box = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
            box.Items.AddRange(labels);
            string stored = ReadStringSetting("GPU", key, fallback);
            int selected = Array.FindIndex(values, value => string.Equals(value, stored, StringComparison.OrdinalIgnoreCase));
            box.SelectedIndex = selected >= 0 ? selected : Array.IndexOf(values, fallback);
            box.SelectedIndexChanged += delegate { SaveHeadsetSetting(key, values[box.SelectedIndex]); };
            panel.Controls.Add(new SettingRow(label, box));
            return box;
        }

        void SaveHeadsetSetting(string key, object value)
        {
            try { WriteSetting("GPU", key, value); }
            catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
        }

        void AddGt7HeadsetSettings(TableLayoutPanel panel)
        {
            panel.Controls.Add(Hint("Enable SHARED_OPENXR in Advanced for the seated screen. Enable SHADGT_OPENXR_COCKPIT for native stereo. Restart GT7 after changing headset settings."));
            var percent = new NumericUpDown { Minimum = 25, Maximum = 100, Increment = 1,
                Value = HeadsetPercent(ReadIntSetting("GPU", "openxr_output_percent", 100)) };
            percent.ValueChanged += delegate { SaveHeadsetSetting("openxr_output_percent", (int)percent.Value); };
            panel.Controls.Add(new SettingRow("Per-eye output (% of recommended size)", percent));
            var mirror = AddHeadsetChoice(panel, "Desktop mirror", "openxr_mirror_eye",
                new[] { "left", "right", "both", "off" }, new[] { "Left eye", "Right eye", "Both eyes", "Off" }, "left");
            var framing = AddHeadsetChoice(panel, "Mirror framing", "openxr_mirror_framing",
                new[] { "full", "fill", "inset" }, new[] { "Full eye", "Fill window", "Inset crop" }, "full");
            var method = AddHeadsetChoice(panel, "Reconstruction", "openxr_upscaler",
                new[] { "off", "fsr", "dlss" }, new[] { "Off", "AMD FSR", "NVIDIA DLSS" }, "off");
            var quality = AddHeadsetChoice(panel, "Reconstruction quality", "openxr_upscale_quality",
                new[] { "native", "quality", "balanced", "performance", "ultra_performance" },
                new[] { "Native AA", "Quality", "Balanced", "Performance", "Ultra performance" }, "quality");
            var model = AddHeadsetChoice(panel, "DLSS model", "openxr_dlss_model",
                new[] { "m", "l", "k", "j", "auto" }, new[] { "Model M", "Model L", "Model K", "Model J", "NVIDIA default" }, "m");
            var detail = AddHeadsetChoice(panel, "DLSS texture detail", "openxr_dlss_texture_detail",
                new[] { "full", "reduced", "off" }, new[] { "Full", "Reduced", "Game mip bias" }, "full");
            var frames = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
            frames.Items.AddRange(new[] { "Off", "AMD FidelityFX (2×)", "NVIDIA DLSS (2×)" });
            frames.SelectedIndex = HeadsetGenerator(ReadIntSetting("GPU", "openxr_framegen", 0));
            frames.SelectedIndexChanged += delegate { SaveHeadsetSetting("openxr_framegen", frames.SelectedIndex); };
            panel.Controls.Add(new SettingRow("Frame generation", frames));
            AddHeadsetChoice(panel, "NVIDIA Reflex", "openxr_reflex", new[] { "auto", "off", "on", "boost" },
                new[] { "Auto (NVIDIA frame generation)", "Off", "On", "On + Boost" }, "auto");
            Action update = delegate
            {
                framing.Enabled = mirror.SelectedIndex != 3;
                quality.Enabled = method.SelectedIndex != 0;
                model.Enabled = detail.Enabled = method.SelectedIndex == 2;
            };
            mirror.SelectedIndexChanged += delegate { update(); };
            method.SelectedIndexChanged += delegate { update(); };
            update();
        }

        static int HeadsetPercent(object value)
        {
            return value is int && (int)value >= 25 && (int)value <= 100 ? (int)value : 100;
        }

        static int HeadsetGenerator(object value)
        {
            int index = value is int ? (int)value : 0;
            // Older profiles offered NVIDIA 3x through 6x. Use its tested 2x mode.
            return index >= 0 && index <= 6 ? Math.Min(index, 2) : 0;
        }

        internal static void ApplyGt7HeadsetSettings(ProcessStartInfo start, IDictionary<string, object> gpu)
        {
            Func<string, object> get = key => { object value; return gpu != null && gpu.TryGetValue(key, out value) ? value : null; };
            var env = start.EnvironmentVariables;
            env["SHADGT_OPENXR_OUTPUT_PERCENT"] = HeadsetPercent(get("openxr_output_percent")).ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["SHADGT_OPENXR_MIRROR_EYE"] = get("openxr_mirror_eye") as string ?? "left";
            env["SHADGT_OPENXR_MIRROR_FRAMING"] = get("openxr_mirror_framing") as string ?? "full";
            env["SHADGT_OPENXR_UPSCALER"] = get("openxr_upscaler") as string ?? "off";
            env["SHADGT_OPENXR_UPSCALE_QUALITY"] = get("openxr_upscale_quality") as string ?? "quality";
            env["SHADGT_OPENXR_DLSS_MODEL"] = get("openxr_dlss_model") as string ?? "m";
            env["SHADGT_OPENXR_DLSS_TEXTURE_DETAIL"] = get("openxr_dlss_texture_detail") as string ?? "full";
            env["SHADGT_OPENXR_REFLEX"] = get("openxr_reflex") as string ?? "auto";
            int generator = HeadsetGenerator(get("openxr_framegen"));
            env["SHARED_OPENXR_FRAMEGEN"] = generator == 2 ? "dlss" : generator == 1 ? "1" : "0";
        }

        internal static void ApplyGt7HeadsetVisibility(ProcessStartInfo start)
        {
            if (start.EnvironmentVariables["SHARED_OPENXR"] == "1" && start.EnvironmentVariables["SHADGT_OPENXR_COCKPIT"] == "1")
                start.Arguments = (start.Arguments + " occlusionculling_mode=cpu").TrimStart();
        }

        /// A GPU section setting from the game's settings file, or null.
        static object GpuSetting(string root, string key)
        {
            var gpu = ReadGt7GpuSettings(root);
            object value;
            return gpu != null && gpu.TryGetValue(key, out value) ? value : null;
        }

        static Dictionary<string, object> ReadGt7GpuSettings(string root)
        {
            try
            {
                string path = GameInfo.GT7.ConfigPath(root);
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object gpu;
                return config != null && config.TryGetValue("GPU", out gpu) ? gpu as Dictionary<string, object> : null;
            }
            catch { return null; }
        }


        /// The shader cache limit in MB for RYGT_DRIVER_CACHE_MAX_MB: -1 unlimited, 0 off, 2 GB unless set.
        static long DriverCacheMaxMb(string root)
        {
            if (GpuSetting(root, "driver_cache_unlimited") is bool && (bool)GpuSetting(root, "driver_cache_unlimited")) return -1;
            object gb = GpuSetting(root, "driver_cache_gb");
            int size = gb is int ? Math.Max(0, Math.Min(100, (int)gb)) : 2;
            return size * 1024L;
        }


        /// GPU.race_fps_lock from the game's settings: the race frame rate, 30 unless 60 was chosen.
        static int RaceFpsLock(string root)
        {
            try
            {
                string path = GameInfo.GT7.ConfigPath(root);
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object found, value;
                var gpu = config != null && config.TryGetValue("GPU", out found) ? found as Dictionary<string, object> : null;
                if (gpu != null && gpu.TryGetValue("race_fps_lock", out value) && value is int && (int)value == 60)
                    return 60;
            }
            catch { }
            return 30;
        }


        /// GPU.dlssg_frames from the game's settings: DLSS generated frames a game frame (1-5).
        static int DlssgFrames(string root)
        {
            try
            {
                string path = GameInfo.GT7.ConfigPath(root);
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object found, value;
                var gpu = config != null && config.TryGetValue("GPU", out found) ? found as Dictionary<string, object> : null;
                if (gpu != null && gpu.TryGetValue("dlssg_frames", out value) && value is int)
                    return Math.Max(1, Math.Min(5, (int)value));
            }
            catch { }
            return 1;
        }


        /// A DLL's file version ("310.9.16"), or "?" when it has none.
        static string FileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return info.FileMajorPart + "." + info.FileMinorPart + "." + info.FileBuildPart;
            }
            catch (Exception) { return "?"; }
        }


        void RefreshStateGt7()
        {
            bool ready = File.Exists(Exe) && (RuntimeReadsGameFolders() ? GameFile("contents\\gt.idx") != null
                                                         : File.Exists(Path.Combine(App0Layout.Top(root, info.TitleId), "contents", "gt.idx")));
            bool running = IsGameRunning();
            play.Enabled = ready && !busy && !running;
            if (setup != null) setup.Enabled = !busy && !running;
            if (addDlss != null) addDlss.Enabled = !busy && !running;
            if (downloadDlss != null) downloadDlss.Enabled = !busy && !running;
            if (!busy) status.Text = ready ? "Ready. Press Play." : "Not set up yet: open the settings (the gear), pick your game's folders and press Set up.";
            if (running) status.Text = "GT7 is running. Close the game before running Set up.";
        }


        static bool IsGameRunning()
        {
            var games = Process.GetProcessesByName("GT7");
            bool running = games.Length > 0;
            foreach (var game in games) game.Dispose();
            return running;
        }


        /// Set up couldn't use a game or system module file: the dump needs doing again.
        void ShowRedumpHint(DumpFileException problem)
        {
            MessageBox.Show(this,
                "Set up couldn't use this file:\n" + problem.File + "\n\n" + problem.Message + "\n\n" +
                "Your game dump or system modules (sys_modules) look incomplete or damaged. " +
                "Dump the game and/or the system modules from your PS4 again, then run Set up again.",
                "Set up: redump needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }


        void RunSetupGt7()
        {
            if (IsGameRunning())
                throw new Exception("Close GT7 before running Set up.");
            MigrateGt7Serial(); // GAME-004
            string baseDir = settings.Game, updateDir = settings.Update;
            var layout = new GameLayout(baseDir, updateDir, root);
            settings.Save();
            Log("Gran Turismo 7 (CUSA24769), version " + layout.Version);
            if (layout.Version != "01.00")
                Log("This update is not yet verified. The converter will use its executable without version-specific offsets.");
            foreach (string name in new[] { "libSceVideoOut.prx", "libSceMbus.prx", "libSceTextToSpeech.prx" })
                if (!File.Exists(Path.Combine(root, "libs", name)))
                    throw new Exception("The RyGT runtime is incomplete: libs\\" + name + " is missing. Extract the full release archive first.");

            string fontDir, font2Dir;
            FindFonts(settings.Fonts, out fontDir, out font2Dir);
            var firmware = new Dictionary<string, string>();
            foreach (var name in FirmwareModulesGt7)
            {
                var found = FindModule(settings.Modules, name);
                if (found == null) throw new DumpFileException(name + ".sprx", "System module " + name + " was not found under " + settings.Modules + ".");
                firmware[name] = found;
            }
            foreach (var name in OptionalFirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found != null) firmware[name] = found;
                else Log("Note: " + name + " is not in the system modules folder; some related game features may be unavailable.");
            }

            // The bundled community Offline mode patch (only that group of the file is used).
            string offlinePath = OfflinePatchPath;
            OfflinePatch offline = null;
            if (File.Exists(offlinePath))
            {
                offline = OfflinePatch.Load(offlinePath);
                Log("Found the " + OfflinePatch.GroupName + " patch " + offline.Version + " (" + offline.Writes.Count + " lines) in patches\\" + GameLayout.TitleId + ".xml.");
            }

            // 1. The executable and modules to convert, unwrapped to plain ELF.
            SetStatus("Preparing the game files...");
            string work = info.OwnFolder(root, "convert"); // FEAT-GTS-291: convert\CUSA24769
            string workModules = Path.Combine(work, "sce_module");
            if (Directory.Exists(workModules)) Directory.Delete(workModules, true);
            Directory.CreateDirectory(workModules);
            Unwrap(layout.Get("eboot.bin"), Path.Combine(work, "eboot.elf"), offline);
            foreach (var name in new[] { "libc.prx", "libSceFios2.prx" })
            {
                string source = layout.Get("sce_module\\" + name);
                if (source == null) throw new DumpFileException("sce_module\\" + name, "The game's sce_module\\" + name + " is missing.");
                Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var name in new[] { "libSceSecure.prx", "logiWheel.prx" })
            {
                string source = layout.Get(name);
                if (source != null) Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var pair in firmware) Unwrap(pair.Value, Path.Combine(workModules, pair.Key + ".prx"));

            // 2. Convert.
            SetStatus("Converting the game for Windows (this takes a while)...");
            string converter = Path.Combine(root, "converter", "ryty.exe");
            if (!File.Exists(converter)) throw new Exception("converter\\ryty.exe is missing from the RyGT folder.");
            string convertedRoot = Path.Combine(work, "runtime-" + Guid.NewGuid().ToString("N"));
            string convertedExe = Path.Combine(convertedRoot, "GT7.exe");
            // FEAT-GTS-291: the converted modules go in app0\CUSA24769 (App0Layout), and GT7.exe loads them from there.
            bool perGame = App0Layout.Supported(root);
            string app0Option;
            string app0Target = App0Layout.SetupFolder(root, info.TitleId, perGame, out app0Option);
            string convertedModules = Path.Combine(perGame ? App0Layout.Folder(convertedRoot, info.TitleId) : Path.Combine(convertedRoot, "app0"), "sce_module");
            if (!perGame) Log("Note: this folder's converter or runtime libraries predate app0\\" + info.TitleId + "; the converted modules go in app0 as before.");
            Directory.CreateDirectory(convertedModules);
            int code = Run(converter, "--windows --ps4 --skip-syscall-check --registry" + app0Option + " --report \"" +
                Path.Combine(work, "port-report.json") + "\" \"" + Path.Combine(work, "eboot.elf") + "\" \"" + convertedExe + "\"", work);
            if (code != 0 || !File.Exists(convertedExe)) throw new Exception("The converter failed (exit code " + code + "); the previous game executable was preserved. See convert\\" + info.TitleId + "\\convert.log.");
            if (offline != null)
            {
                int removed = offline.Apply(convertedExe, File.ReadAllBytes(Path.Combine(work, "eboot.elf")));
                // The patch writes its dealership catalog to /data/gt7offline (data\CUSA24769\gt7offline, FEAT-GTS-291).
                Directory.CreateDirectory(Path.Combine(info.OwnFolder(root, "data"), "gt7offline"));
                Log("Applied the " + OfflinePatch.GroupName + " patch to GT7.exe" + (removed > 0 ? " (" + removed + " overlapping relocations disabled)." : "."));
            }
            GameExe.PrepareGt7(convertedExe, layout.Get("sce_sys\\icon0.png"));
            Log("Prepared GT7.exe with the game's icon.");

            // 3. app0: only the converted modules. The game reads its own files from the update, then the
            //    base game folder (FEAT-GTS-251; see core/libs/prx/libc/include/AppOverlay.hpp).
            SetStatus("Linking the game files...");
            // The new GT7.exe loads app0\CUSA24769\sce_module: the folder's older app0 moves there first.
            if (perGame) foreach (string note in App0Layout.Migrate(root, info, true)) Log(note);
            if (RuntimeReadsGameFolders()) UseGt7GameFolders(layout, convertedModules, app0Target);
            else
            {
                Log("These runtime libraries need the game's files linked into app0.");
                LinkGameFiles(layout, convertedModules, app0Target);
            }
            File.Copy(convertedExe, Exe, true);
            string registry = Path.ChangeExtension(convertedExe, ".registry.json");
            if (File.Exists(registry)) File.Copy(registry, Path.ChangeExtension(Exe, ".registry.json"), true);
            // The converter's own GT7.exe (and those of earlier Set ups) stay as GT7.exe.converted: started
            // from convert\runtime-*, GT7 found none of the launcher's settings next to it.
            foreach (string runtimeDir in Directory.GetDirectories(work, "runtime-*"))
            {
                string leftover = Path.Combine(runtimeDir, "GT7.exe");
                if (!File.Exists(leftover)) continue;
                string kept = leftover + ".converted";
                if (File.Exists(kept)) File.Delete(kept);
                File.Move(leftover, kept);
            }

            // 4. Fonts.
            SetStatus("Copying the fonts...");
            CopyDirectory(fontDir, Path.Combine(root, "sys", "common", "font"));
            CopyDirectory(font2Dir, Path.Combine(root, "sys", "common", "font2"));

            // GT7's runtime settings and directories; no GT Sport scripts or shader packs.
            ConfigureGT7();
            UsePortableShaderCache();
            // Which Offline mode patch file GT7.exe was built with, so that Play notices a file added,
            // changed or removed later (adding it alone changes nothing until GT7.exe is rebuilt).
            File.WriteAllText(OfflineStampPath, OfflinePatchState());

            Log("Set up finished.");
        }


        string OfflinePatchPath { get { return Path.Combine(root, "patches", GameLayout.TitleId + ".xml"); } }

        string OfflineStampPath { get { return Path.Combine(GameInfo.GT7.OwnFolder(root, "convert"), "offline-patch.txt"); } }


        // The SHA-256 of patches\CUSA24769.xml, or "none" without one.
        string OfflinePatchState()
        {
            return File.Exists(OfflinePatchPath) ? GameLayout.Sha256(File.ReadAllBytes(OfflinePatchPath)) : "none";
        }


        // Set up must run again when the Offline mode patch file differs from the one GT7.exe was built
        // with. A folder set up before this check has no record: it was built without the file unless
        // the dealership catalog folder exists.
        bool OfflinePatchChanged()
        {
            string state = OfflinePatchState();
            if (File.Exists(OfflineStampPath)) return File.ReadAllText(OfflineStampPath).Trim() != state;
            bool applied = Directory.Exists(Path.Combine(GameInfo.GT7.OwnFolder(root, "data"), "gt7offline"));
            return (state != "none") != applied;
        }


        // With a runtime that can't read the game folders (see RuntimeReadsGameFolders), app0 is linked
        // as before.
        void LinkGameFiles(GameLayout layout, string convertedModules, string app0)
        {
            string stage = Path.Combine(root, "app0.setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var unixLinks = new Dictionary<string, string>();
            int linked = 0;
            foreach (var pair in layout.Files)
            {
                if (pair.Key.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("sce_module\\", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(stage, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (Wine.Detected) unixLinks[target] = pair.Value;
                else if (!Native.CreateHardLink(target, pair.Value, IntPtr.Zero) &&
                         !Native.CreateSymbolicLink(target, pair.Value, 2))
                    throw new Exception("Could not link " + pair.Value + ". Keep RyGT on the game's drive or enable Windows Developer Mode.");
                linked++;
            }
            if (Wine.Detected) Wine.Link(unixLinks);
            foreach (var pair in layout.Files)
            {
                if (pair.Key.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("sce_module\\", StringComparison.OrdinalIgnoreCase)) continue;
                string problem = ReadProblem(Path.Combine(stage, pair.Key), new FileInfo(pair.Value).Length);
                if (problem != null) throw new Exception("Linked " + pair.Key + " " + problem + ".");
            }
            // The converter writes its modules under app0. Copy those small converted files,
            // never the source PRX files, into the new data tree before swapping directories.
            CopyDirectory(convertedModules, Path.Combine(stage, "sce_module"));
            string previous = PreviousApp0(app0);
            if (Directory.Exists(app0)) Directory.Move(app0, previous);
            Directory.CreateDirectory(Path.GetDirectoryName(app0));
            try { Directory.Move(stage, app0); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, app0); throw; }
            Log("Linked " + linked + " game files. Update files override matching base files; other base files are preserved.");
        }


        // FEAT-RT-021: both capability checks inspect the same renderer image while building the page.
        byte[] ReadRendererImage()
        {
            try { return File.ReadAllBytes(Path.Combine(root, SwitchWalker.RendererFile)); }
            catch (IOException) { return new byte[0]; }
            catch (UnauthorizedAccessException) { return new byte[0]; }
        }


        /// Whether these bytes hold the switch's name as a whole NUL-terminated string: not the start of a longer
        /// name, nor the end of one (as SwitchWalker reads names).
        internal static bool NamesSwitch(byte[] data, string name)
        {
            byte[] text = Encoding.ASCII.GetBytes(name);
            for (int i = 0; i + text.Length < data.Length; i++)
            {
                if (data[i] != text[0]) continue;
                int j = 1;
                while (j < text.Length && data[i + j] == text[j]) j++;
                if (j != text.Length || data[i + j] != 0) continue;
                byte before = i > 0 ? data[i - 1] : (byte)0;
                bool nameByte = before == '_' || (before >= '0' && before <= '9') || (before >= 'A' && before <= 'Z') ||
                                (before >= 'a' && before <= 'z');
                if (!nameByte) return true;
            }
            return false;
        }


        /// FEAT-GT7-181: the renderer's variables for a GPU.dlss_texture_detail value. Full (and anything unknown,
        /// as the page shows it) sets nothing: they are the renderer's defaults, and a script's own values stay.
        internal static void DlssDetailVariables(string setting, out string bias, out string extra)
        {
            bias = setting == "off" ? "0" : setting == "reduced" ? "1" : null;
            extra = setting == "reduced" ? "0" : null;
        }


        /// Whether the installed runtime reads /app0 from the game folders itself (FEAT-GTS-251): its
        /// libc exports ListLayeredDirectory. Release packages can pair a new launcher with older
        /// runtime libraries, which only find the game's files when they are linked into app0.
        bool RuntimeReadsGameFolders()
        {
            try
            {
                byte[] data = File.ReadAllBytes(Path.Combine(root, "libs", "libc.prx"));
                byte[] name = Encoding.ASCII.GetBytes("ListLayeredDirectory_nid_no_patch");
                for (int i = 0; i + name.Length <= data.Length; i++)
                {
                    if (data[i] != name[0]) continue;
                    int j = 1;
                    while (j < name.Length && data[i + j] == name[j]) j++;
                    if (j == name.Length) return true;
                }
            }
            catch { }
            return false;
        }


        // FEAT-GTS-251: the game reads /app0 straight from the update and base game folders (CUSA24769.RyGT.ini
        // game= and update=), so nothing is linked into app0 any more: before, every game file was
        // hard-linked there, which Explorer counted as another copy of the game, once more for each
        // app0.previous-* folder an earlier Set up left. Those links are removed too.
        void UseGt7GameFolders(GameLayout layout, string convertedModules, string app0)
        {
            string stage = Path.Combine(root, "app0.setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            // The converter writes its modules under app0. Copy those small converted files,
            // never the source PRX files, into the new app0 before swapping directories.
            CopyDirectory(convertedModules, Path.Combine(stage, "sce_module"));
            string previous = PreviousApp0(app0);
            if (Directory.Exists(app0)) Directory.Move(app0, previous);
            Directory.CreateDirectory(Path.GetDirectoryName(app0));
            try { Directory.Move(stage, app0); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, app0); throw; }
            // app0.previous-* (app0 before FEAT-GTS-291) and app0.<serial>.previous-* (app0\<serial>).
            foreach (string old in Directory.GetDirectories(root, "app0*.previous-*"))
                RemoveGt7GameLinks(old, layout, Log);
            Log("The game reads its files from " + (settings.Update.Length > 0 ? settings.Update + " and " : "") + settings.Game +
                "; update files override matching base files.");
        }


        /// Where the app0 Set up replaces is kept: beside app0 in the runtime folder, never inside app0 (where a
        /// name that is not a serial would count as the layout before FEAT-GTS-291): app0.previous-<time> for
        /// app0, app0.<serial>.previous-<time> for app0\<serial>.
        string PreviousApp0(string app0)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
            string name = string.Equals(Path.GetFullPath(app0).TrimEnd('\\'), Path.GetFullPath(Path.Combine(root, "app0")).TrimEnd('\\'),
                                        StringComparison.OrdinalIgnoreCase) ? "app0" : "app0." + Path.GetFileName(app0);
            return Path.Combine(root, name + ".previous-" + stamp);
        }


        /// Removes from `folder` (an app0 an earlier Set up made) the links to the game's files: a file
        /// only when it is a link or the very same file on disk as the game's, a folder only when it is
        /// a junction or link. Folders left empty go, and so does `folder` itself when only that Set
        /// up's own converted modules are left. Copies and anything else are kept.
        internal static void RemoveGt7GameLinks(string folder, GameLayout layout, Action<string> Log)
        {
            int removed = 0, kept = 0;
            foreach (var pair in layout.Files)
            {
                string old = Path.Combine(folder, pair.Key);
                if (!File.Exists(old)) continue;
                try
                {
                    if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0 || SameFile(old, pair.Value)) { File.Delete(old); removed++; }
                    else kept++;
                }
                catch (Exception e) { kept++; Log("Note: could not remove the old link " + old + " (" + e.Message.TrimEnd('.') + ")."); }
            }
            foreach (string dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    // Deleting a junction or folder link removes the link, never what it points to.
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(dir, false); removed++; }
                    else if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false);
                }
                catch { }
            }
            bool onlyModules = Directory.EnumerateFileSystemEntries(folder)
                .All(entry => Path.GetFileName(entry).Equals("sce_module", StringComparison.OrdinalIgnoreCase));
            if (onlyModules)
            {
                try { Directory.Delete(folder, true); }
                catch (Exception e) { Log("Note: could not remove " + folder + " (" + e.Message.TrimEnd('.') + ")."); }
            }
            if (removed > 0) Log("Removed " + removed + " links to the game's files from " + Path.GetFileName(folder) + ".");
            if (kept > 0) Log("Note: " + Path.GetFileName(folder) + " still has " + kept + " of the game's files that are not links to them; they were left alone.");
        }


        void ConfigureGT7()
        {
            WriteSetting("GPU", "native_renderer", true);
            WriteSetting("GPU", "readbacks_mode", 2);
            if (!LoadConfig().ContainsKey("General")) WriteSetting("General", "show_splash", true);
            // Photos: where the game's gallery Export writes pictures (FEAT-GT7-161), made now so
            // players find it in the ryGT7 folder.
            // GAME-005: GT7's saves are in user/savedata/CUSA24769 (the runtime moves a folder's old _sd and
            // download0 there at the first start; folders made here first, still empty, don't stop that).
            string saves = "user/savedata/" + GameInfo.GT7.TitleId + "/";
            foreach (string folder in new[] { saves + "_sd/local-data", saves + "_sd/sce_sdmemory", saves + "download0/APP_DATA", "temp", "temp0/" + GameInfo.GT7.TitleId, "logs", "patches", "shaders", "Photos" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            for (int i = 1000; i < 1004; i++)
                foreach (string folder in new[] { "savedata", "trophy", "inputs" })
                    Directory.CreateDirectory(Path.Combine(root, "user", "home", i.ToString(), folder));
        }


        void EnsureGT7Defaults()
        {
            DefaultSetting("GPU", "native_renderer", true);
            DefaultSetting("GPU", "readbacks_mode", 2);
            DefaultSetting("GPU", "compile_mode", 2);
            DefaultSetting("GPU", "precompile_shaders", false);
            DefaultSetting("General", "show_splash", true);
            DefaultSetting("General", "player_names", "Player 1,Player 2");
            DefaultSetting("Input", "background_controller_input", true);
        }


        void DefaultSetting(string section, string key, object value)
        {
            var config = LoadConfig();
            object stored;
            var values = config.TryGetValue(section, out stored) ? stored as Dictionary<string, object> : null;
            if (values == null || !values.ContainsKey(key)) WriteSetting(section, key, value);
        }


        void PlayGt7()
        {
            if (IsGameRunning()) { RefreshState(); return; }
            MigrateGt7Serial(); // GAME-004
            if (!File.Exists(Exe)) { MessageBox.Show(this, "Press Set up first."); return; }
            // Without its data index GT7 closes right after the splash screen.
            string dataIndex = RuntimeReadsGameFolders() ? GameFile("contents\\gt.idx") : null;
            string linkedIndex = Path.Combine(App0Layout.Top(root, info.TitleId), "contents", "gt.idx");
            if (dataIndex == null && File.Exists(linkedIndex)) dataIndex = linkedIndex;
            string dataProblem = dataIndex == null ? "is missing" : ReadProblem(dataIndex, -1);
            if (dataProblem != null)
            {
                MessageBox.Show(this, "GT7's data index contents\\gt.idx " + dataProblem + ", so the game can't start.\n\n" +
                                "Check that the game and update folders in the settings still exist, then press Set up again.");
                return;
            }
            // The one-time steps below can take minutes (copying shaders, preparing them), so they run
            // beside the window, which keeps showing their progress.
            busy = true;
            RefreshState();
            var thread = new Thread(delegate ()
            {
                bool prepared = false;
                DumpFileException playDumpProblem = null;
                try
                {
                    if (OfflinePatchChanged())
                    {
                        // patches\CUSA24769.xml was added, changed or removed after Set up: GT7.exe is
                        // rebuilt with it first (the dealerships said "Failed to get sales list" otherwise).
                        SetStatus("The Offline mode patch file changed since Set up; running Set up again...");
                        Log("patches\\" + GameLayout.TitleId + ".xml changed since Set up; running Set up again.");
                        setupLogPath = LogFiles.Prepare(root, "setup.log", info.TitleId);
                        RunSetupGt7();
                    }
                    if (OldShaderCacheExists()) SetStatus("Moving your shaders to the shaders folder (once)...");
                    UsePortableShaderCache();
                    ConfigureGT7();
                    settings.Save();
                    prepared = true;
                }
                catch (DumpFileException e) { Log("ERROR: " + e.Message); playDumpProblem = e; }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (playDumpProblem != null) ShowRedumpHint(playDumpProblem);
                    if (prepared)
                    {
                        StartGameGt7(root);
                        status.Text = "GT7 is starting. Logs: logs\\stdout.log and logs\\stderr.log.";
                    }
                    else status.Text = "Could not prepare GT7. See the log.";
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }


        /// Starts GT7.exe once, with session log rotation; after a GPU device loss it is not started
        /// again (FIX-GTS-491), the setup log says so.
        static void StartGameGt7(string root)
        {
            string exe = Path.Combine(root, "GT7.exe");
            // Folders set up by an earlier launcher (RyGT7.exe) get the icon and the Windows subsystem now.
            try { if (!GameExe.IsPrepared(exe)) GameExe.PrepareGt7(exe); } catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string logDirectory;
                try
                {
                    logDirectory = LogFiles.PrepareRuntimeLogs(root, GameInfo.Of(GameKind.GT7).TitleId);
                }
                catch (Exception e)
                {
                    AppendSetupLog(root, GameInfo.GT7.TitleId, "Could not prepare game logs: " + e.Message);
                    return;
                }

                Process started;
                try
                {
                    var start = new ProcessStartInfo(exe)
                    { WorkingDirectory = root, UseShellExecute = false };
                    start.EnvironmentVariables["RYGT_LOG_DIRECTORY"] = logDirectory;
                    // GAME-004: the renderer and the runtime take the game from the launcher.
                    start.EnvironmentVariables["SHADGT_GAME"] = "gt7";
                    // FIX-RT-012: patches\scripts holds GT Sport's replacement scripts (GTS-005: GALLERY.ADC,
                    // UI_KIT.ADC...) in a folder both games share; the renderer would serve them to GT7 too.
                    start.EnvironmentVariables["SHADGT_SCRIPTS"] = "0";
                    start.EnvironmentVariables["RYTY_INTEROP_BASE"] = "8000000000";
                    // PERF-161: GT7's per-frame memory (0x200000000-0x208000000), which the CPU rewrites
                    // every frame and a few compute passes write too, stays in guest memory: kept in a
                    // renderer region, each CPU write faulted (about 60,000 a second) and was copied in.
                    // FIX-RT-002: GT7's GPU culling memory as one range (0x1020000000-0x1020412000),
                    // gap included: PS4 Pro mode moves visibility data into the gap the base PS4's
                    // three ranges left out. Keep in step with core/libs/prx/libc/src/LaunchDefaults.cpp.
                    start.EnvironmentVariables["RYGT_NATIVE_INPLACE_RANGES"] = "1020000000+412000,200000000+8000000";
                    // FIX-213: GT7 reads back what the GPU writes into its CPU pools from 0xe000000000
                    // up (GT Sport's are above 0xf000000000, the renderer's default); kept in a
                    // renderer region, the CPU saw stale data and culled the whole rear-view mirror.
                    start.EnvironmentVariables["RYGT_NATIVE_INPLACE_FROM"] = "e000000000";
                    start.EnvironmentVariables["SHADGT_FPS_LOCK"] = RaceFpsLock(root).ToString();
                    ApplyGt7HeadsetSettings(start, ReadGt7GpuSettings(root));
                    // Shader cache size and the AMD graphics fix from the Graphics page.
                    start.EnvironmentVariables["RYGT_DRIVER_CACHE_MAX_MB"] = DriverCacheMaxMb(root).ToString();
                    start.EnvironmentVariables["RYGT_AMD_SINGLE_SAMPLE_BARYCENTRIC"] =
                        GpuSetting(root, "amd_graphics_fix") is bool && (bool)GpuSetting(root, "amd_graphics_fix") ? "1" : "0";
                    // PERF-GT7-111/117 from the Graphics page: each shader translation leaves a record
                    // (shaders\<serial>.manifest) and its code (shaders\<serial>.capture, this PC's only);
                    // at start, the ones the shader store lacks (after a renderer update) are translated
                    // again in the background, so the races after an update start as warm ones.
                    // Only when checked (like the compile pack below): a launcher script that
                    // sets them itself keeps its setting.
                    if (GpuSetting(root, "shader_pretranslate") is bool && (bool)GpuSetting(root, "shader_pretranslate"))
                    {
                        start.EnvironmentVariables["RYGT_MANIFEST_RECORD"] = "1";
                        start.EnvironmentVariables["RYGT_MANIFEST_PRETRANSLATE"] = "1";
                    }
                    // PERF-GT7-108 from the Graphics page: the pipelines this PC's driver cache lacks (after
                    // a driver update) are compiled at start from the recipes kept in the compile pack.
                    // Only when checked: Jouer-Compile-Pack.cmd sets it itself before starting this launcher.
                    if (GpuSetting(root, "compile_pack") is bool && (bool)GpuSetting(root, "compile_pack"))
                        start.EnvironmentVariables["RYGT_COMPILE_PACK"] = "1";
                    // DLSS frame multiplier from the Graphics page (generated frames, 1-5).
                    start.EnvironmentVariables["RYGT_DLSSG_FRAMES"] = DlssgFrames(root).ToString();
                    // Upscaling quality from the Graphics page (the size DLSS and FSR 3.1 scale to).
                    start.EnvironmentVariables["RYGT_UPSCALE_QUALITY"] = GpuSetting(root, "upscale_quality") as string ?? "auto";
                    // Texture sharpness from the Graphics page (detail levels added to every sampler's bias).
                    start.EnvironmentVariables["RYGT_TEXTURE_LOD_BIAS"] = GpuSetting(root, "texture_lod_bias") as string ?? "0";
                    // FEAT-GT7-181: DLSS texture detail from the Graphics page. Reduced: the bias without its -1;
                    // Off: no bias; Full sets nothing. A renderer without these switches ignores them.
                    string dlssBias, dlssExtra;
                    DlssDetailVariables(GpuSetting(root, "dlss_texture_detail") as string, out dlssBias, out dlssExtra);
                    if (dlssBias != null) start.EnvironmentVariables["RYGT_DLSS_TEMPORAL_MIP_BIAS"] = dlssBias;
                    if (dlssExtra != null) start.EnvironmentVariables["RYGT_DLSS_TEMPORAL_MIP_EXTRA"] = dlssExtra;
                    // PS4 Pro mode from the Graphics page (NEO-GT7). PS4 (off) sets nothing, so a launcher
                    // script's own values stay. The other modes set or remove all three variables themselves:
                    // RYGT_NEO=1 with the checkerboard (4K TV, Full, Custom with its box), plus RYGT_NEO_TV=1080
                    // for the 1080p TV; RYGT_NEO_EXTRAS lists the five other settings when they differ from what
                    // the game picks by itself (none in the PS4 and 4K TV presets, all five in the 1080p TV one).
                    // The modes other than custom are read as the console has them, not from the boxes.
                    string proSetting = GpuSetting(root, "ps4_pro_mode") as string ?? "off";
                    if (proSetting == "4k" || proSetting == "1080p" || proSetting == "full" || proSetting == "custom")
                    {
                        Func<string, bool> proBox = key => { object on = GpuSetting(root, key); return on is bool && (bool)on; };
                        bool custom = proSetting == "custom";
                        bool tv1080 = proSetting == "1080p";
                        bool neo = tv1080 || (custom ? proBox("ps4_pro_checkerboard") : true);
                        if (neo) start.EnvironmentVariables["RYGT_NEO"] = "1";
                        else start.EnvironmentVariables.Remove("RYGT_NEO");
                        if (tv1080) start.EnvironmentVariables["RYGT_NEO_TV"] = "1080";
                        else start.EnvironmentVariables.Remove("RYGT_NEO_TV");
                        var proOptionsOn = new List<string>();
                        var proOptionNames = new[] { "ps4_pro_blur", "blur", "ps4_pro_replays60", "replays60",
                                                     "ps4_pro_replay_blur", "replayblur", "ps4_pro_garage60", "garage60",
                                                     "ps4_pro_lod", "lod" };
                        for (int i = 0; i < proOptionNames.Length; i += 2)
                        {
                            bool on = custom ? proBox(proOptionNames[i]) : proSetting == "full";
                            if (on) proOptionsOn.Add(proOptionNames[i + 1]);
                        }
                        if (proOptionsOn.Count != 0) start.EnvironmentVariables["RYGT_NEO_EXTRAS"] = string.Join(",", proOptionsOn);
                        else start.EnvironmentVariables.Remove("RYGT_NEO_EXTRAS");
                    }
                    // FEAT-GTS-254: the Advanced page's switches and other variables last, so they win.
                    int customCount = CustomEnvironment.Apply(start, new Settings(GameInfo.GT7.IniPath(root)).Env);
                    if (customCount > 0) AppendSetupLog(root, GameInfo.GT7.TitleId, customCount + " custom environment variable(s) applied.");
                    ApplyGt7HeadsetVisibility(start);
                    started = Process.Start(start);
                }
                catch (Exception e)
                {
                    AppendSetupLog(root, GameInfo.GT7.TitleId, "Could not start GT7: " + e.Message);
                    return;
                }
                if (started == null) return;
                using (Process game = started) game.WaitForExit();

                // FIX-GTS-491: after a GPU device loss the game is not started again. The interrupted
                // session could not be resumed anyway, and the restart hid the crash from the player
                // and ran a second session under the first one's launch (tests mixed the two).
                if (RendererReportedDeviceLoss(logDirectory))
                    AppendSetupLog(root, GameInfo.GT7.TitleId, "Vulkan lost the GPU device (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "); GT7 was closed and is not started again. Its log: " + Path.Combine(logDirectory, "stdout.log") + ", renamed stdout.log.<UTC time> at the next start (the first one after this time).");
            });
        }
    }
}
