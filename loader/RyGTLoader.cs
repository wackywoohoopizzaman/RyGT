// RyGT loader: sets up Gran Turismo Sport or Gran Turismo 7 (FEAT-GTS-290: one launcher for both)
// from the player's own dump, firmware modules and fonts, then starts it. Built with build.cmd (C# for .NET Framework 4.x, which Windows includes).
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

[assembly: System.Reflection.AssemblyCompany("RyGT-Team")]
[assembly: System.Reflection.AssemblyCopyright("Copyright © 2026 RyGT-Team")]
[assembly: System.Reflection.AssemblyProduct("RyGT Launcher")]
[assembly: System.Reflection.AssemblyTitle("RyGT Launcher")]
[assembly: System.Reflection.AssemblyDescription("Gran Turismo Sport and Gran Turismo 7 launcher for Windows")]

namespace RyGT
{
    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        // Returns a one-byte BOOLEAN, not a BOOL.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CreateSymbolicLink(string link, string target, int flags);

        [StructLayout(LayoutKind.Sequential)]
        struct ByHandleFileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

        public struct FileIdentity { public uint Volume; public ulong Index; }

        /// The volume and file number of a file (following a link), which hard links of it share.
        public static bool Identify(string path, out FileIdentity identity)
        {
            identity = new FileIdentity();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    ByHandleFileInformation information;
                    if (!GetFileInformationByHandle(stream.SafeFileHandle, out information)) return false;
                    identity.Volume = information.VolumeSerialNumber;
                    identity.Index = ((ulong)information.IndexHigh << 32) | information.IndexLow;
                    return true;
                }
            }
            catch { return false; }
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr window, string appName, string idList);
    }

    /// Wine (Linux, Steam Proton): its kernel32 has wine_get_unix_file_name. Wine before 11 cannot
    /// make Windows links (CreateSymbolicLink does nothing there and mklink /J fails), so under Wine
    /// the game's files and folders are linked with Unix symbolic links, which Wine follows like the
    /// files themselves (and reports as reparse points when they are folders).
    static class Wine
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern IntPtr wine_get_unix_file_name(string dosPath);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true,
                   BestFitMapping = false)]
        static extern IntPtr wine_get_dos_file_name(string unixPath);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string reserved, desktop, title;
            public int x, y, width, height, columns, rows, fill, flags;
            public short show, reserved2;
            public IntPtr reserved3, input, output, error;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr process, thread;
            public int processId, threadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation information);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static readonly bool detected = DetectWine();

        public static bool Detected { get { return detected; } }

        static bool DetectWine()
        {
            try
            {
                IntPtr kernel = GetModuleHandle("kernel32.dll");
                return kernel != IntPtr.Zero && GetProcAddress(kernel, "wine_get_unix_file_name") != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// The Unix path of a Windows path (whose last part need not exist yet).
        public static string UnixPath(string path)
        {
            IntPtr unix = wine_get_unix_file_name(Path.GetFullPath(path));
            if (unix == IntPtr.Zero) throw new Exception("Wine has no Unix path for " + path + ".");
            try
            {
                int length = 0;
                while (Marshal.ReadByte(unix, length) != 0) length++;
                var bytes = new byte[length];
                Marshal.Copy(unix, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { HeapFree(GetProcessHeap(), 0, unix); }
        }

        static string DosPath(string unixPath)
        {
            IntPtr dos = wine_get_dos_file_name(unixPath);
            if (dos == IntPtr.Zero) throw new Exception("Wine has no Windows path for " + unixPath + ".");
            try { return Marshal.PtrToStringUni(dos); }
            finally { HeapFree(GetProcessHeap(), 0, dos); }
        }

        static string Quote(string unixPath)
        {
            return "'" + unixPath.Replace("'", "'\\''") + "'";
        }

        /// Makes each link (key) a Unix symbolic link to its target (value), replacing an earlier
        /// symbolic link but never a real file or folder, with one /bin/sh script. Wine starts Unix
        /// programs detached, so the script's last line writes a file to wait for.
        public static void Link(IEnumerable<KeyValuePair<string, string>> links)
        {
            string name = "rygt-link-" + Guid.NewGuid().ToString("N");
            string script = Path.Combine(Path.GetTempPath(), name + ".sh");
            string done = Path.Combine(Path.GetTempPath(), name + ".done");
            var text = new StringBuilder();
            text.Append("#!/bin/sh\n# Written by the RyGT launcher: links the game's files under Wine.\n");
            text.Append("PATH=/usr/local/bin:/usr/bin:/bin\n");
            text.Append("link() { if [ -L \"$2\" ] || [ ! -e \"$2\" ]; then ln -sfn \"$1\" \"$2\"; fi; }\n");
            foreach (var pair in links)
            {
                // The link's folder exists; its last part may not (Wine still gives the Unix path).
                string folder = UnixPath(Path.GetDirectoryName(Path.GetFullPath(pair.Key)));
                text.Append("link " + Quote(UnixPath(pair.Value)) + " " +
                            Quote(folder.TrimEnd('/') + "/" + Path.GetFileName(pair.Key)) + "\n");
            }
            text.Append("echo done > " + Quote(UnixPath(done)) + "\n");
            File.WriteAllText(script, text.ToString(), new UTF8Encoding(false));
            try
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
                ProcessInformation information;
                var command = new StringBuilder("\"" + DosPath("/bin/sh") + "\" \"" + UnixPath(script) + "\"");
                const uint CreateNoWindow = 0x08000000;
                if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, null,
                                   ref startup, out information))
                    throw new Exception("Could not start /bin/sh to link the game's files (error " +
                                        Marshal.GetLastWin32Error() + ").");
                if (information.thread != IntPtr.Zero) CloseHandle(information.thread);
                if (information.process != IntPtr.Zero) CloseHandle(information.process);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (!File.Exists(done) && DateTime.UtcNow < deadline) Thread.Sleep(50);
                if (!File.Exists(done)) throw new Exception("Linking the game's files with /bin/sh did not finish.");
            }
            finally
            {
                try { File.Delete(script); } catch { }
                try { File.Delete(done); } catch { }
            }
        }
    }

    /// The launcher's look (after the Epic Games launcher): a charcoal window with rounded panels
    /// and cards, white text with grey secondary text, white main buttons and grey rounded ones.
    static class Dark
    {
        public static readonly Color Back = Color.FromArgb(17, 18, 22);       // the window
        public static readonly Color Panel = Color.FromArgb(22, 24, 29);      // the settings pages
        public static readonly Color Sidebar = Color.FromArgb(13, 14, 18);    // the settings' category list
        public static readonly Color Card = Color.FromArgb(28, 30, 36);       // cards on a page
        public static readonly Color Field = Color.FromArgb(32, 35, 42);
        public static readonly Color Button = Color.FromArgb(32, 35, 42);
        public static readonly Color ButtonHover = Color.FromArgb(44, 48, 56);
        public static readonly Color ButtonDown = Color.FromArgb(26, 28, 34);
        public static readonly Color Hover = Color.FromArgb(30, 33, 40);      // list rows under the mouse
        public static readonly Color Line = Color.FromArgb(38, 41, 48);       // between settings rows
        public static readonly Color Disabled = Color.FromArgb(96, 101, 112);
        // FEAT-GTS-290: the accent follows the target game (red GT Sport, blue GT7) and fades between
        // them, so these three are set by LoaderForm.FadeAccent; everything reads them when it paints.
        public static Color Accent = Color.FromArgb(178, 24, 36);
        public static Color AccentHover = Color.FromArgb(206, 40, 52);
        public static Color AccentText = Color.FromArgb(240, 92, 76);
        public static readonly Color Heading = Color.FromArgb(128, 134, 146);
        public static readonly Color Border = Color.FromArgb(44, 47, 55);
        public static readonly Color Text = Color.FromArgb(236, 238, 242);
        public static readonly Color DimText = Color.FromArgb(132, 138, 150);
        public const int Radius = 4;

        static readonly bool HasBahnschrift = FontFamily.Families.Any(f => f.Name == "Bahnschrift SemiBold");
        static readonly Dictionary<float, Font> caps = new Dictionary<float, Font>();

        /// The capitals font of headings and buttons: Bahnschrift SemiBold (Windows 10 and later).
        public static Font Caps(float size)
        {
            Font font;
            if (!caps.TryGetValue(size, out font))
                caps[size] = font = HasBahnschrift ? new Font("Bahnschrift SemiBold", size) : new Font("Segoe UI Semibold", size);
            return font;
        }

        /// The text with a hair space between its letters (spaced capitals).
        public static string Spaced(string text)
        {
            return string.Join(((char)0x200A).ToString(), text.Select(c => c.ToString()).ToArray());
        }

        public static void Apply(Control control)
        {
            // Other controls take their parent's color (a card's, the window's) unless they set one.
            if (control is Form) control.BackColor = Back;
            if (control.Tag as string != "heading") control.ForeColor = Text;
            var button = control as Button;
            if (button != null && !(button is GlowButton)) Round(button);
            var box = control as TextBox;
            if (box != null)
            {
                box.BackColor = Field;
                if (box.BorderStyle == BorderStyle.Fixed3D) box.BorderStyle = BorderStyle.FixedSingle;
                box.HandleCreated += delegate { Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null); };
            }
            var number = control as NumericUpDown;
            if (number != null)
            {
                number.BackColor = Field;
                number.BorderStyle = BorderStyle.None;
                // The up and down buttons, drawn over in the field's colors.
                var spin = number.Controls.Count > 0 ? number.Controls[0] : null;
                if (spin != null && !(spin is TextBoxBase)) spin.Paint += (sender, e) => PaintSpin(spin, e.Graphics);
            }
            var check = control as CheckBox;
            if (check != null && !(check is DarkCheckBox)) check.FlatStyle = FlatStyle.Flat;
            var combo = control as ComboBox;
            if (combo != null) DarkCombo(combo);
            var scroll = control as ScrollableControl;
            if (scroll != null && scroll.AutoScroll)
                scroll.HandleCreated += delegate { Native.SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null); };
            foreach (Control child in control.Controls) Apply(child);
        }

        /// A dark title bar (Windows 10 2004 and later; ignored before).
        public static void TitleBar(Form form)
        {
            int on = 1;
            if (Native.DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundedRect(r, radius))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        /// Copies the part of `picture` being painted, unscaled; `offset` is where the painted control
        /// sits in the picture.
        public static void CopyArea(Bitmap picture, Point offset, Graphics g)
        {
            var area = Rectangle.Intersect(Rectangle.Ceiling(g.ClipBounds), new Rectangle(-offset.X, -offset.Y, picture.Width, picture.Height));
            if (area.Width <= 0 || area.Height <= 0) return;
            var mode = g.CompositingMode;
            var interpolation = g.InterpolationMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(picture, area, new Rectangle(area.X + offset.X, area.Y + offset.Y, area.Width, area.Height), GraphicsUnit.Pixel);
            g.CompositingMode = mode;
            g.InterpolationMode = interpolation;
        }

        /// Paints what lies under `control`: the banner picture, or the nearest opaque parent's color.
        public static void PaintUnder(Control control, Graphics g)
        {
            // See-through cards between the control and what is under them, innermost first.
            var glass = new List<Card>();
            for (var parent = control.Parent; parent != null; parent = parent.Parent)
            {
                var card = parent as Card;
                if (card != null) { card.DrawUnder(control, g); FillGlass(glass, control, g); return; } // PERF-GTS-436
                var backdrop = parent as IBackdrop;
                if (backdrop != null && backdrop.DrawBackdrop(control, g, false)) { FillGlass(glass, control, g); return; }
                if (parent.BackColor.A == 255)
                {
                    using (var back = new SolidBrush(parent.BackColor)) g.FillRectangle(back, control.ClientRectangle);
                    FillGlass(glass, control, g);
                    return;
                }
            }
            g.Clear(Back);
            FillGlass(glass, control, g);
        }

        static void FillGlass(List<Card> glass, Control control, Graphics g)
        {
            for (int i = glass.Count - 1; i >= 0; --i)
            {
                using (var fill = new SolidBrush(glass[i].Fill)) g.FillRectangle(fill, control.ClientRectangle);
            }
        }

        /// Gives the controls that take their parent's color a transparent one, so the window's
        /// picture shows through them (the cards, fields, buttons and lists paint their own).
        public static void SeeThrough(Control control)
        {
            foreach (Control child in control.Controls)
            {
                bool own = child is Card || child is TextBoxBase || child is ListControl || child is ButtonBase ||
                           child is TrackBar || child is UpDownBase || child is DarkBar ||
                           child is Slider || child is ToggleRow ||
                           child is DataGridView; // FIX-GTS-434: cannot be see-through (ArgumentException)
                if (!own && !TypeDescriptor.GetProperties(child)["BackColor"].ShouldSerializeValue(child))
                    child.BackColor = Color.Transparent;
                if (!(child is TextBoxBase) && !(child is ListControl) && !(child is UpDownBase) && !(child is DataGridView)) SeeThrough(child);
            }
        }

        /// Draws a button as a rounded rectangle: white with dark text for the main action (Tag
        /// "accent"), grey otherwise; Tag "nav" is a top bar tab and "nav-on" the open one.
        static void Round(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            if (button.Padding == Padding.Empty) button.Padding = new Padding(18, 7, 18, 7);
            if (button.Font.Name == "Segoe UI" && !(button.Tag is string && (string)button.Tag == "icon")) button.Font = Caps(9f);
            bool hover = false, down = false;
            button.MouseEnter += delegate { hover = true; button.Invalidate(); };
            button.MouseLeave += delegate { hover = false; down = false; button.Invalidate(); };
            button.MouseDown += delegate { down = true; button.Invalidate(); };
            button.MouseUp += delegate { down = false; button.Invalidate(); };
            button.Paint += (sender, e) => PaintButton(button, e.Graphics, hover, down);
        }

        static void PaintButton(Button button, Graphics g, bool hover, bool down)
        {
            PaintUnder(button, g);
            string kind = button.Tag as string;
            bool accent = kind == "accent", play = kind == "play", icon = kind == "icon";
            Color fill = Color.Empty, edge, text = Text;
            if (!button.Enabled)
            {
                fill = play ? Color.FromArgb(90, 20, 22, 27) : Color.Empty;
                edge = Color.FromArgb(52, 55, 62);
                text = Disabled;
            }
            else if (accent)
            {
                fill = down ? Color.FromArgb(200, 203, 210) : hover ? Color.White : Color.FromArgb(232, 234, 238);
                edge = fill;
                text = Back;
            }
            else if (play && (hover || down || button.Focused))
            {
                fill = down ? Color.FromArgb(214, 217, 224) : Color.White;
                edge = fill;
                text = Back;
            }
            else if (play)
            {
                fill = Color.FromArgb(120, 10, 11, 14);
                edge = Color.FromArgb(214, 218, 226);
            }
            else
            {
                fill = down ? Color.FromArgb(10, 255, 255, 255) : hover ? Color.FromArgb(22, 255, 255, 255) : Color.Empty;
                edge = hover ? Color.FromArgb(150, 156, 168) : Color.FromArgb(72, 77, 88);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, button.Width - 1f, button.Height - 1f);
            if (!fill.IsEmpty) FillRounded(g, fill, r, 3);
            using (var path = RoundedRect(r, 3))
            using (var pen = new Pen(edge))
                g.DrawPath(pen, path);
            string label = icon ? button.Text : play ? Spaced(button.Text.ToUpperInvariant()) : button.Text.ToUpperInvariant();
            TextRenderer.DrawText(g, label, button.Font, button.ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        static void PaintSpin(Control spin, Graphics g)
        {
            using (var back = new SolidBrush(Button)) g.FillRectangle(back, spin.ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = spin.Width / 2f, half = spin.Height / 2f;
            using (var pen = new Pen(spin.Enabled ? Color.FromArgb(200, 200, 200) : Color.FromArgb(100, 100, 100), 1.5f))
            {
                g.DrawLines(pen, new[] { new PointF(x - 3.5f, half / 2 + 2), new PointF(x, half / 2 - 1.5f), new PointF(x + 3.5f, half / 2 + 2) });
                g.DrawLines(pen, new[] { new PointF(x - 3.5f, half + half / 2 - 2), new PointF(x, half + half / 2 + 1.5f), new PointF(x + 3.5f, half + half / 2 - 2) });
            }
        }

        /// A drop-down list drawn dark: the closed box and the open list both in the field color.
        static void DarkCombo(ComboBox combo)
        {
            combo.BackColor = Field;
            if (combo.DrawMode != DrawMode.Normal) return;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = 18;
            combo.DrawItem += (sender, e) =>
            {
                bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
                bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
                using (var back = new SolidBrush(selected ? ButtonHover : Field)) e.Graphics.FillRectangle(back, e.Bounds);
                if (e.Index < 0) return;
                var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds,
                                      combo.Enabled ? Text : Color.FromArgb(112, 112, 112),
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            };
            combo.HandleCreated += delegate { Native.SetWindowTheme(combo.Handle, "DarkMode_CFD", null); };
        }
    }

    /// A window that paints a picture behind its controls (the launcher: launcher\background.png).
    interface IBackdrop
    {
        /// The picture as it lies under `control`; `raw` without the darkening. False without one.
        bool DrawBackdrop(Control control, Graphics g, bool raw);
    }

    /// A check box drawn in the loader's colors: a dark rounded square, red with a white tick when checked.
    class DarkCheckBox : CheckBox
    {
        public bool BoxRight; // the box at the row's right edge, the text at its left

        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font);
            return new Size(text.Width + 26, Math.Max(text.Height, 18) + 4);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = 16;
            var box = new RectangleF(BoxRight ? Width - size - 1.5f : 1.5f, (Height - size) / 2 + 0.5f, size - 1, size - 1);
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            Dark.FillRounded(g, !Enabled ? Color.FromArgb(36, 36, 36) : Checked ? (hover ? Dark.AccentHover : Dark.Accent) : Dark.Field, box, 4);
            if (!Checked)
            {
                using (var path = Dark.RoundedRect(box, 4))
                using (var border = new Pen(!Enabled ? Color.FromArgb(58, 58, 58) : hover ? Color.FromArgb(140, 140, 140) : Color.FromArgb(88, 88, 88)))
                    g.DrawPath(border, path);
            }
            else
            {
                using (var tick = new Pen(Enabled ? Color.White : Color.FromArgb(140, 140, 140), 2f))
                {
                    g.DrawLines(tick, new[]
                    {
                        new PointF(box.Left + 3.5f, box.Top + 8f),
                        new PointF(box.Left + 6.5f, box.Top + 11f),
                        new PointF(box.Left + 11.5f, box.Top + 4.5f)
                    });
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Point(BoxRight ? 0 : size + 8, (Height - TextRenderer.MeasureText(Text, Font).Height) / 2),
                                  Enabled ? ForeColor : Color.FromArgb(112, 112, 112), TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    }

    /// A drop-down list with a rounded dark frame and arrow, drawn over the flat combo box's
    /// white border once Windows has painted it (its items are drawn by Dark.Apply).
    class DarkComboBox : ComboBox
    {
        bool hover;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        // Windows redraws parts of the box for these without a full paint; paint the frame again.
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF || !IsHandleCreated) return; // WM_PAINT
            using (var g = Graphics.FromHwnd(Handle))
            {
                var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using (var outside = new Region(ClientRectangle))
                using (var path = Dark.RoundedRect(r, 6))
                {
                    outside.Exclude(path);
                    g.SetClip(outside, CombineMode.Replace);
                    Dark.PaintUnder(this, g);
                    g.ResetClip();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // The flat style's inner white line, then the arrow's area.
                    using (var inner = new Pen(Dark.Field)) g.DrawRectangle(inner, 1, 1, Width - 3, Height - 3);
                    var arrow = new RectangleF(Width - 22, 2, 20, Height - 4);
                    using (var field = new SolidBrush(Dark.Field)) g.FillRectangle(field, arrow);
                    using (var pen = new Pen(!Enabled ? Color.FromArgb(52, 52, 52) : Focused || DroppedDown ? Color.FromArgb(120, 120, 120) :
                                             hover ? Color.FromArgb(96, 96, 96) : Color.FromArgb(62, 62, 62)))
                        g.DrawPath(pen, path);
                    float x = arrow.X + arrow.Width / 2 - 1, y = Height / 2f;
                    using (var pen = new Pen(Enabled ? Color.FromArgb(200, 200, 200) : Color.FromArgb(100, 100, 100), 1.5f))
                        g.DrawLines(pen, new[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) });
                }
            }
        }
    }

    /// A rounded panel with a thin border; the controls on it take its color.
    class Card : TableLayoutPanel
    {
        public float Radius = Dark.Radius;
        public Color Edge = Dark.Border;
        /// The card's color; a translucent one lets the window's picture show through it.
        public readonly Color Fill;

        public Card(Color fill)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Fill = fill;
            BackColor = fill.A == 255 ? fill : Color.Transparent;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        // PERF-GTS-436: WinForms paints a see-through label or row by painting its parent card under
        // it first, so a card was drawn again (picture, glass, rounded border) for every row on it,
        // several times per scroll step. The card is drawn once into this picture, kept until it
        // moves in the window or changes size; the repaints copy it.
        Bitmap look;
        Point lookAt;

        Bitmap Look()
        {
            var form = FindForm();
            var at = form != null ? form.PointToClient(PointToScreen(Point.Empty)) : Point.Empty;
            if (look != null && look.Size == Size && lookAt == at) return look;
            if (look != null) look.Dispose();
            look = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(look))
            {
                Dark.PaintUnder(this, g);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using (var path = Dark.RoundedRect(r, Radius))
                {
                    using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
                    if (!Edge.IsEmpty) using (var pen = new Pen(Edge)) g.DrawPath(pen, path);
                }
            }
            lookAt = at;
            return look;
        }

        /// The card as it lies under `control` (the card itself or a control on it).
        public void DrawUnder(Control control, Graphics g)
        {
            var offset = control == this ? Point.Empty : PointToClient(control.PointToScreen(Point.Empty));
            Dark.CopyArea(Look(), offset, g);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            DrawUnder(this, e.Graphics);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && look != null) { look.Dispose(); look = null; }
            base.Dispose(disposing);
        }
    }

    /// A setting that is on or off: its text on the left, a switch on the right, a line under it.
    class ToggleRow : CheckBox
    {
        bool hover;
        float knob = -1; // 0 off .. 1 on, while it slides
        int version;

        public ToggleRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            AutoSize = false;
            Dock = DockStyle.Fill;
            Height = 40;
            Margin = new Padding(0);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(TextRenderer.MeasureText(Text, Font).Width + 70, 40);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            if (hover && Enabled)
                using (var lit = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillRectangle(lit, ClientRectangle);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - 56, Height - 1), Enabled ? Dark.Text : Dark.Disabled,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(Width - 40, (Height - 1 - 18) / 2f, 36, 18);
            float on = knob < 0 ? (Checked ? 1 : 0) : knob;
            Color trackColor = !Enabled ? Color.FromArgb(36, 39, 46) : Anim.Mix(Color.FromArgb(52, 56, 64), Color.FromArgb(232, 234, 238), on);
            Dark.FillRounded(g, trackColor, track, 9);
            const float size = 12;
            float x = track.X + 3 + (track.Width - size - 6) * on;
            Color knobColor = !Enabled ? Color.FromArgb(70, 74, 84) : Anim.Mix(Color.FromArgb(150, 155, 166), Dark.Back, on);
            using (var fill = new SolidBrush(knobColor)) g.FillEllipse(fill, x, track.Y + 3, size, size);
            using (var line = new Pen(Dark.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (!Visible || !IsHandleCreated) { knob = -1; return; }
            int mine = ++version;
            float from = knob < 0 ? (Checked ? 0 : 1) : knob, to = Checked ? 1 : 0;
            int start = Environment.TickCount;
            Anim.Run(() =>
            {
                if (mine != version || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / 140f);
                knob = from + (to - from) * Anim.Ease(t);
                Invalidate();
                if (t >= 1) knob = -1;
                return t < 1;
            });
        }
    }

    /// A setting with a control (a drop-down, a slider, a button): its name on the left, the control
    /// on the right, a line under it. The name greys out with the control.
    class SettingRow : TableLayoutPanel
    {
        readonly Label label;
        readonly Control control;

        /// The row's line goes elsewhere (under a hint that belongs to the setting).
        public bool NoLine;

        public SettingRow(string text, Control control)
        {
            this.control = control;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ColumnCount = 2;
            RowCount = 1;
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(0, 0, 0, 1);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            label = new Label { Text = text, UseMnemonic = false, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0), BackColor = Color.Transparent, Tag = "heading" };
            Controls.Add(label, 0, 0);
            control.Anchor = AnchorStyles.Right;
            control.Margin = new Padding(12, 0, 0, 0);
            Controls.Add(control, 1, 0);
            control.EnabledChanged += delegate { ShowEnabled(); };
            ShowEnabled();
            // A table sizes its rows from this height (a new one is 100 high).
            Height = GetPreferredSize(Size.Empty).Height;
            control.SizeChanged += delegate { Height = GetPreferredSize(Size.Empty).Height; };
        }

        void ShowEnabled()
        {
            label.ForeColor = control.Enabled ? Dark.Text : Dark.Disabled;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            // From the label and the control (a table offered more height would take all of it).
            var text = label.GetPreferredSize(Size.Empty);
            var right = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
            return new Size(text.Width + control.Margin.Horizontal + right.Width, Math.Max(40, Math.Max(text.Height, right.Height) + 12));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
            if (NoLine) return;
            using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// A thin slider: a line filled up to the value, a round knob, and the value on its right.
    class Slider : Control
    {
        int minimum, maximum = 100, value;
        bool dragging;
        public int TickFrequency = 10, SmallChange = 1, LargeChange = 10;
        /// Shown after the value ("%").
        public string Suffix = "";
        public event EventHandler ValueChanged;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(280, 28);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum { get { return minimum; } set { minimum = value; Value = this.value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Value = this.value; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(minimum, Math.Min(maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        Rectangle Track { get { return new Rectangle(8, Height / 2 - 1, Math.Max(10, Width - 8 - 64), 2); } }

        void SetFromX(int x)
        {
            var track = Track;
            float amount = Math.Max(0, Math.Min(1, (x - track.X) / (float)track.Width));
            Value = minimum + (int)Math.Round(amount * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); dragging = true; Capture = true; SetFromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= SmallChange;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += SmallChange;
            else if (e.KeyCode == Keys.PageDown) Value -= LargeChange;
            else if (e.KeyCode == Keys.PageUp) Value += LargeChange;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = Track;
            float amount = maximum > minimum ? (value - minimum) / (float)(maximum - minimum) : 0;
            float x = track.X + track.Width * amount;
            using (var rest = new SolidBrush(Color.FromArgb(62, 66, 76))) g.FillRectangle(rest, track);
            using (var done = new SolidBrush(Enabled ? Color.FromArgb(220, 223, 230) : Dark.Disabled))
                g.FillRectangle(done, track.X, track.Y, x - track.X, track.Height);
            float knob = Focused ? 14 : 12;
            using (var fill = new SolidBrush(Enabled ? Color.White : Dark.Disabled)) g.FillEllipse(fill, x - knob / 2, track.Y + 1 - knob / 2, knob, knob);
            TextRenderer.DrawText(g, value + Suffix, Dark.Caps(9f), new Rectangle(track.Right + 8, 0, Width - track.Right - 8, Height),
                                  Enabled ? Dark.Text : Dark.Disabled, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// A category in the settings' sidebar: grey text, white with a red bar on its left when open.
    class SidebarItem : Control
    {
        bool active, hover;

        public SidebarItem(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = text;
            Height = 38;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 10.5f);
            TabStop = true;
        }

        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Dark.Sidebar)) g.FillRectangle(back, ClientRectangle);
            if (active)
                using (var bar = new SolidBrush(Dark.Accent)) g.FillRectangle(bar, 16, 6, 3, Height - 12);
            Color text = active ? Color.White : hover ? Color.FromArgb(206, 210, 218) : Dark.DimText;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(30, 0, Width - 34, Height), text,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// The launcher's animations: one timer (about 60 times a second) that calls each running
    /// animation's step until the step says it is done.
    static class Anim
    {
        static readonly List<Func<bool>> running = new List<Func<bool>>();
        static System.Windows.Forms.Timer timer;

        /// Runs `step` every tick while it returns true.
        public static void Run(Func<bool> step)
        {
            running.Add(step);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer { Interval = 15 };
                timer.Tick += delegate
                {
                    for (int i = running.Count - 1; i >= 0; --i)
                    {
                        bool more;
                        try { more = running[i](); } catch { more = false; }
                        if (!more) running.RemoveAt(i);
                    }
                    if (running.Count == 0) timer.Stop();
                };
            }
            if (!timer.Enabled) timer.Start();
        }

        /// Ease-out (fast start, gentle end) of 0..1.
        public static float Ease(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return 1 - (1 - t) * (1 - t) * (1 - t);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// A home view button: outlined, with a light sweeping across it now and then; under the mouse
    /// (or with the keyboard focus) it fills white, and the home view draws a glow around it.
    class GlowButton : Button
    {
        float hot;
        int version;
        bool over, pressing;
        /// The text is a Segoe MDL2 Assets glyph (the settings button), not a word in capitals.
        public bool Glyph;
        static readonly int sweepStart = Environment.TickCount;

        public GlowButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
        }

        /// 0..1: how far it has filled white.
        public float Hot { get { return hot; } }

        void Animate()
        {
            int mine = ++version;
            float from = hot, to = (over || (Focused && ShowFocusCues)) && Enabled ? 1 : 0;
            int start = Environment.TickCount;
            Anim.Run(() =>
            {
                if (mine != version || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / 170f);
                hot = from + (to - from) * Anim.Ease(t);
                Invalidate();
                if (Parent != null) Parent.Invalidate(Rectangle.Inflate(Bounds, 16, 16), false);
                return t < 1;
            });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); over = true; Animate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); over = false; pressing = false; Animate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressing = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressing = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Animate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Animate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Animate(); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float h = Enabled ? hot : 0;
            Color fill = !Enabled ? Color.FromArgb(90, 40, 43, 50)
                       : Anim.Mix(Color.FromArgb(120, 10, 11, 14), pressing ? Color.FromArgb(214, 217, 224) : Color.White, h);
            Color edge = !Enabled ? Color.FromArgb(52, 55, 62) : Anim.Mix(Color.FromArgb(214, 218, 226), Color.White, h);
            Color text = !Enabled ? Dark.Disabled : Anim.Mix(Color.White, Dark.Back, h);
            using (var path = Dark.RoundedRect(r, 3))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                // The sweeping light: 1.1 s across, every 3.5 s, while not filled.
                double phase = ((Environment.TickCount - sweepStart) % 3500) / 1100.0;
                if (Enabled && h < 0.99f && phase < 1)
                {
                    float band = 70, x = r.X - band + (float)phase * (r.Width + band);
                    var bandRect = new RectangleF(x, r.Y, band, r.Height);
                    using (var light = new LinearGradientBrush(bandRect, Color.Transparent, Color.Transparent, 0f))
                    {
                        var blend = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb((int)(70 * (1 - h)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                            Positions = new[] { 0f, 0.5f, 1f }
                        };
                        light.InterpolationColors = blend;
                        var clip = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        g.FillRectangle(light, bandRect);
                        g.Clip = clip;
                    }
                }
                using (var pen = new Pen(edge)) g.DrawPath(pen, path);
            }
            string label = Glyph ? Text : Dark.Spaced(Text.ToUpperInvariant());
            TextRenderer.DrawText(g, label, Font, ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// The launcher's home view, drawn in one piece: the pictures (drifting slowly sideways and
    /// fading from one to the next every 10 seconds), darkened toward the bottom left, the logo
    /// or the title, the caption, the status and the version, with glows around its buttons.
    class HomeView : Control, IBackdrop
    {
        readonly List<Image> pictures;
        readonly GlowButton[] buttons;
        public Image Logo;
        public string Title = "", Accent = "", Caption = "", Status = "", Version = "";
        /// FEAT-GTS-272: the updater's notice (a newer build) at the top right, above its Update button.
        public string Notice = "";
        public GlowButton UpdateButton;
        public GlowButton ChangelogButton; // FIX-GTS-286: left of UpdateButton
        /// GT7: the signed-in online ID and the logo beside it, at the top right; the notice goes under them.
        public string Player = "";
        public Image PlayerLogo;
        int NoticeTop { get { return Player.Length > 0 ? 92 : 30; } }
        const int NoticeWidth = 420;
        /// FEAT-GTS-290: the target game's list at the top left of the home view.
        public Control GameChooser;
        int NoticeHeight
        {
            get
            {
                return Notice.Length == 0 ? 0 : TextRenderer.MeasureText(Notice, Font, new Size(NoticeWidth, 400),
                                                                         TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            }
        }
        readonly Dictionary<int, Bitmap> scaled = new Dictionary<int, Bitmap>();
        Size scaledFor;
        Bitmap overlay, frame;
        int current, next = -1, fadeFrom, shownSince;
        readonly int started = Environment.TickCount;
        readonly System.Windows.Forms.Timer ticker;
        const int ShowMs = 10000, FadeMs = 1400;
        const int Left_ = 56, Bottom_ = 46, Gap = 10;

        public HomeView(List<Image> pictures, params GlowButton[] buttons)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            this.pictures = pictures;
            this.buttons = buttons;
            BackColor = Dark.Back;
            foreach (var button in buttons) Controls.Add(button);
            shownSince = Environment.TickCount;
            ticker = new System.Windows.Forms.Timer { Interval = 33 };
            ticker.Tick += delegate { Tick(); };
            VisibleChanged += delegate { if (Visible) ticker.Start(); else ticker.Stop(); };
            ticker.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ticker.Dispose(); foreach (var b in scaled.Values) b.Dispose(); if (overlay != null) overlay.Dispose(); if (frame != null) frame.Dispose(); if (switchedFrom != null) switchedFrom.Dispose(); }
            base.Dispose(disposing);
        }

        void Tick()
        {
            var form = FindForm();
            if (!Visible || form == null || form.WindowState == FormWindowState.Minimized) return;
            if (pictures.Count > 1 && next < 0 && Environment.TickCount - shownSince > ShowMs)
            {
                next = (current + 1) % pictures.Count;
                fadeFrom = Environment.TickCount;
            }
            Compose();
            // The view and the controls drawn over its picture (Play, the gear, the update button) follow
            // each frame. The game list does not: it paints in two steps (Windows' box, then its dark frame),
            // so repainting it 30 times a second flickered; only its corners show the picture, and those
            // are repainted with it on its own events and when the game is switched (Invalidate(true)).
            Invalidate(false);
            foreach (Control child in Controls)
                if (child != GameChooser) child.Invalidate(true);
        }

        Bitmap Scaled(int index)
        {
            Bitmap cached;
            if (scaled.TryGetValue(index, out cached)) return cached;
            var image = pictures[index];
            // Covering the view with 6% to spare, for the drift.
            float scale = 1.06f * Math.Max(Width / (float)image.Width, Height / (float)image.Height);
            int width = (int)Math.Ceiling(image.Width * scale), height = (int)Math.Ceiling(image.Height * scale);
            cached = new Bitmap(width, height);
            using (var g = Graphics.FromImage(cached))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, width, height);
            }
            scaled[index] = cached;
            return cached;
        }

        Bitmap Overlay()
        {
            if (overlay != null) return overlay;
            overlay = new Bitmap(Width, Height);
            var r = new Rectangle(0, 0, Width, Height);
            using (var g = Graphics.FromImage(overlay))
            {
                using (var dim = new SolidBrush(Color.FromArgb(40, 6, 7, 9))) g.FillRectangle(dim, r);
                using (var left = new LinearGradientBrush(r, Color.FromArgb(215, 8, 9, 12), Color.FromArgb(0, 8, 9, 12), 0f))
                {
                    left.Blend = new Blend { Factors = new[] { 0f, 0.35f, 1f, 1f }, Positions = new[] { 0f, 0.3f, 0.65f, 1f } };
                    g.FillRectangle(left, r);
                }
                using (var bottom = new LinearGradientBrush(r, Color.FromArgb(0, 8, 9, 12), Color.FromArgb(225, 8, 9, 12), 90f))
                {
                    bottom.Blend = new Blend { Factors = new[] { 0f, 0f, 1f }, Positions = new[] { 0f, 0.45f, 1f } };
                    g.FillRectangle(bottom, r);
                }
            }
            return overlay;
        }

        void DrawPicture(Graphics g, int index, float alpha, float drift)
        {
            var image = Scaled(index);
            int x = -(int)Math.Round((image.Width - Width) * drift);
            int y = (Height - image.Height) / 12;
            if (alpha >= 1) { g.DrawImageUnscaled(image, x, y); return; }
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
                g.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        /// This moment's picture (with the darkening) into `frame`.
        void Compose()
        {
            if (Width <= 0 || Height <= 0) return;
            if (scaledFor != Size)
            {
                foreach (var b in scaled.Values) b.Dispose();
                scaled.Clear();
                if (overlay != null) { overlay.Dispose(); overlay = null; }
                if (frame != null) { frame.Dispose(); frame = null; }
                scaledFor = Size;
            }
            if (frame == null) frame = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(frame))
            {
                if (pictures.Count == 0)
                {
                    using (var glow = new LinearGradientBrush(ClientRectangle, Color.FromArgb(14, 15, 19), Anim.Mix(Color.FromArgb(14, 15, 19), Dark.Accent, 0.22f), 0f))
                        g.FillRectangle(glow, ClientRectangle);
                    if (switchedFrom != null) FadeSwitch();
                    return;
                }
                // Side to side and back every 48 seconds, slowest at the ends.
                float drift = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * ((Environment.TickCount - started) / 48000.0)));
                DrawPicture(g, current, 1, drift);
                if (next >= 0)
                {
                    float t = (Environment.TickCount - fadeFrom) / (float)FadeMs;
                    DrawPicture(g, next, Math.Min(1, t), drift);
                    if (t >= 1) { current = next; next = -1; shownSince = Environment.TickCount; }
                }
                g.DrawImageUnscaled(Overlay(), 0, 0);
            }
            if (switchedFrom != null) FadeSwitch();
        }

        /// The old game's picture over the new one, fading out (Compose's last step).
        void FadeSwitch()
        {
            float t = (Environment.TickCount - switchedAt) / (float)SwitchMs;
            if (t >= 1 || switchedFrom.Size != frame.Size)
            {
                switchedFrom.Dispose();
                switchedFrom = null;
                return;
            }
            using (var g = Graphics.FromImage(frame))
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 1 - Anim.Ease(t) });
                g.DrawImage(switchedFrom, new Rectangle(0, 0, frame.Width, frame.Height), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        public bool DrawBackdrop(Control control, Graphics g, bool raw)
        {
            if (frame == null || frame.Size != Size) Compose();
            if (frame == null) return false;
            var at = control == this ? Point.Empty : control.Parent == this ? control.Location : PointToClient(control.PointToScreen(Point.Empty));
            g.DrawImageUnscaled(frame, -at.X, -at.Y);
            return true;
        }

        int ButtonsTop { get { return Height - Bottom_ - 30 - buttons[0].Height; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Left_;
            foreach (var button in buttons)
            {
                button.Location = new Point(x, ButtonsTop);
                x += button.Width + Gap;
            }
            if (UpdateButton != null)
                UpdateButton.Location = new Point(Width - UpdateButton.Width - 30, NoticeTop + NoticeHeight + 14);
            if (UpdateButton != null && ChangelogButton != null)
                ChangelogButton.Location = new Point(UpdateButton.Left - ChangelogButton.Width - 10, UpdateButton.Top);
            if (GameChooser != null) GameChooser.Location = new Point(Left_, ChooserTop + 20);
        }

        const int ChooserTop = 30;

        // FEAT-GTS-290: a change of game cross-fades the old pictures into the new ones.
        Bitmap switchedFrom;
        int switchedAt;
        const int SwitchMs = 320;

        /// The pictures of another game: the view fades from what it shows now to them.
        public void ReplacePictures(List<Image> replacement)
        {
            if (frame != null && Width > 0 && Height > 0)
            {
                if (switchedFrom != null) switchedFrom.Dispose();
                switchedFrom = (Bitmap)frame.Clone();
                switchedAt = Environment.TickCount;
            }
            foreach (var picture in pictures) if (!replacement.Contains(picture)) picture.Dispose();
            pictures.Clear();
            pictures.AddRange(replacement);
            foreach (var b in scaled.Values) b.Dispose();
            scaled.Clear();
            current = 0;
            next = -1;
            shownSince = Environment.TickCount;
            Compose();
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            DrawBackdrop(this, g, false);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // The glow around a button under the mouse, outside the button itself.
            foreach (var button in buttons)
            {
                if (button.Hot <= 0.01f || !button.Enabled) continue;
                var r = new RectangleF(button.Left, button.Top, button.Width, button.Height);
                for (int i = 14; i >= 1; i -= 1)
                {
                    int alpha = (int)(button.Hot * 34 * Math.Pow(1 - i / 15.0, 2));
                    using (var path = Dark.RoundedRect(RectangleF.Inflate(r, i, i), 3 + i))
                    using (var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2f))
                        g.DrawPath(pen, path);
                }
            }
            int top = ButtonsTop;
            // The caption above the buttons, the logo (or title) above it.
            var captionFont = Dark.Caps(8.5f);
            var captionSize = TextRenderer.MeasureText(Caption, captionFont);
            int captionY = top - 22 - captionSize.Height;
            TextRenderer.DrawText(g, Caption, captionFont, new Point(Left_ + 3, captionY), Color.FromArgb(190, 194, 204), TextFormatFlags.NoPrefix);
            if (Logo != null)
            {
                int w = 330, h = w * Logo.Height / Logo.Width;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new Rectangle(Left_, captionY - 12 - h, w, h));
            }
            else
            {
                var font = Dark.Caps(34f);
                var size = TextRenderer.MeasureText(Title + " ", font);
                int y = captionY - size.Height;
                TextRenderer.DrawText(g, Title + " ", font, new Point(Left_, y), Color.White, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Accent, font, new Point(Left_ + size.Width - 8, y), Dark.AccentText, TextFormatFlags.NoPrefix);
            }
            // The status under the buttons, the version at the bottom right.
            TextRenderer.DrawText(g, Status, Font, new Rectangle(Left_ + 2, top + buttons[0].Height + 12, Math.Max(100, Width - Left_ - 260), 40),
                                  Color.FromArgb(200, 204, 212), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            if (Player.Length > 0)
            {
                const int logoSize = 36, pad = 8;
                var nameFont = Dark.Caps(10f);
                var nameSize = TextRenderer.MeasureText(Player, nameFont, Size.Empty, TextFormatFlags.NoPrefix);
                int width = nameSize.Width + logoSize + pad * 3, x = Width - width - 30;
                using (var path = Dark.RoundedRect(new RectangleF(x, 24, width, logoSize + pad * 2), 4))
                using (var shade = new SolidBrush(Color.FromArgb(150, 10, 11, 14)))
                    g.FillPath(shade, path);
                TextRenderer.DrawText(g, Player, nameFont, new Point(x + pad, 24 + pad + (logoSize - nameSize.Height) / 2), Color.White, TextFormatFlags.NoPrefix);
                if (PlayerLogo != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(PlayerLogo, new Rectangle(x + width - pad - logoSize, 24 + pad, logoSize, logoSize));
                }
            }
            if (GameChooser != null)
            {
                TextRenderer.DrawText(g, Dark.Spaced("GAME"), Dark.Caps(8.5f), new Point(Left_ + 1, ChooserTop), Color.FromArgb(190, 194, 204), TextFormatFlags.NoPrefix);
                // A thin accent line under the list: it fades with the game's colour.
                using (var line = new SolidBrush(Dark.Accent))
                    g.FillRectangle(line, GameChooser.Left, GameChooser.Bottom + 3, GameChooser.Width, 2);
            }
            if (Notice.Length > 0)
            {
                var box = new Rectangle(Width - NoticeWidth - 30, NoticeTop, NoticeWidth, NoticeHeight);
                using (var shade = new SolidBrush(Color.FromArgb(150, 10, 11, 14)))
                    g.FillRectangle(shade, Rectangle.Inflate(box, 12, 10));
                TextRenderer.DrawText(g, Notice, Font, box, Color.FromArgb(226, 230, 238),
                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
            }
            var versionFont = Dark.Caps(9f);
            var versionSize = TextRenderer.MeasureText(Version, versionFont);
            TextRenderer.DrawText(g, Version, versionFont, new Point(Width - versionSize.Width - 30, Height - versionSize.Height - 24),
                                  Color.FromArgb(200, 204, 212), TextFormatFlags.NoPrefix);
        }
    }

    /// Covers the window while it changes views: the old view darkens, then the new one appears.
    class Curtain : Control
    {
        Bitmap picture;
        float dark;

        public Curtain()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black;
        }

        public void Show(Bitmap image, float darkness)
        {
            picture = image;
            dark = darkness;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (picture != null) g.DrawImageUnscaled(picture, 0, 0);
            else g.Clear(Dark.Back);
            using (var shade = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(1, dark))), 6, 7, 9)))
                g.FillRectangle(shade, ClientRectangle);
        }
    }

    /// A level meter in the launcher's colors (the live input of the wheel window): a dark rounded
    /// track, filled red up to Value.
    class DarkBar : Control
    {
        int minimum, maximum = 100, value;

        public DarkBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        public int Minimum { get { return minimum; } set { minimum = value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set { if (value != this.value) { this.value = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 0.5f, Height - 0.5f);
            Dark.FillRounded(g, Dark.Field, track, Height / 2f);
            int range = maximum - minimum;
            float amount = range > 0 ? Math.Max(0, Math.Min(1, (value - minimum) / (float)range)) : 0;
            if (amount > 0)
                Dark.FillRounded(g, Dark.Accent, new RectangleF(0, 0, Math.Max(Height, (Width - 0.5f) * amount), Height - 0.5f), Height / 2f);
        }
    }

    /// A game or system module file Set up can't use (missing, not a SELF/ELF, encrypted): the dump is
    /// incomplete or damaged, and Set up shows how to fix it (dump again). File is null when unknown.
    class DumpFileException : Exception
    {
        public readonly string File;
        public DumpFileException(string file, string message) : base(message) { File = file; }
    }

    /// GT Sport's message for the same: its text already says what to do.
    sealed class SetupDumpException : DumpFileException
    {
        public SetupDumpException(string message) : base(null, message) { }
    }

    /// Unwraps an unencrypted (fake-signed) PS4 SELF into a plain ELF.
    static class Self
    {
        public static bool IsElf(byte[] d)
        {
            return d.Length >= 4 && d[0] == 0x7f && d[1] == (byte)'E' && d[2] == (byte)'L' && d[3] == (byte)'F';
        }

        public static byte[] ToElf(byte[] d, string name)
        {
            if (IsElf(d)) return d;
            if (d.Length < 32 || d[0] != 0x4f || d[1] != 0x15 || d[2] != 0x3d || d[3] != 0x1d)
                throw new DumpFileException(name, name + " is neither a SELF nor an ELF file.");
            int count = BitConverter.ToUInt16(d, 24);
            var entries = new Dictionary<long, long[]>();
            for (int i = 0; i < count; i++)
            {
                long props = (long)BitConverter.ToUInt64(d, 32 + i * 32);
                long offset = (long)BitConverter.ToUInt64(d, 40 + i * 32);
                long size = (long)BitConverter.ToUInt64(d, 48 + i * 32);
                if ((props & 0xA) != 0)
                    throw new DumpFileException(name, name + " has encrypted or compressed segments. The dump must be decrypted.");
                if ((props & 0x800) != 0) entries[(props >> 20) & 0xFFF] = new[] { offset, size };
            }
            int elf = 32 + count * 32;
            long phoff = (long)BitConverter.ToUInt64(d, elf + 0x20);
            int phnum = BitConverter.ToUInt16(d, elf + 0x38);
            if (phoff != 0x40) throw new Exception(name + ": unexpected ELF header layout.");
            int headerLength = 0x40 + phnum * 0x38;
            long outSize = headerLength;
            var headers = new List<long[]>();
            for (int i = 0; i < phnum; i++)
            {
                int b = elf + 0x40 + i * 0x38;
                long type = BitConverter.ToUInt32(d, b);
                long offset = (long)BitConverter.ToUInt64(d, b + 8);
                long fileSize = (long)BitConverter.ToUInt64(d, b + 0x20);
                headers.Add(new[] { type, offset, fileSize });
                outSize = Math.Max(outSize, offset + fileSize);
            }
            var output = new byte[outSize];
            Array.Copy(d, elf, output, 0, headerLength);
            long selfSize = (long)BitConverter.ToUInt64(d, 16);
            for (int i = 0; i < headers.Count; i++)
            {
                long type = headers[i][0], offset = headers[i][1], fileSize = headers[i][2];
                if (fileSize == 0) continue;
                long[] entry;
                if (entries.TryGetValue(i, out entry))
                    Array.Copy(d, entry[0], output, offset, Math.Min(entry[1], fileSize));
                else if (type == 0x6fffff01 && selfSize + fileSize <= d.Length)
                    Array.Copy(d, selfSize, output, offset, fileSize); // PT_SCE_VERSION follows the body
            }
            return output;
        }

        // FIX-RT-017: a stripped SELF can retain the original section table coordinates.
        // Only the program headers and segments were reconstructed.
        internal static void OmitMissingSectionTable(byte[] elf, int reconstructedSize)
        {
            ulong offset = BitConverter.ToUInt64(elf, 0x28);
            ulong size = (ulong)reconstructedSize;
            ulong length = (ulong)BitConverter.ToUInt16(elf, 0x3a) * BitConverter.ToUInt16(elf, 0x3c);
            if (offset == 0 || (offset <= size && length <= size - offset)) return;
            Array.Clear(elf, 0x28, 8);
            Array.Clear(elf, 0x3a, 6);
        }

        /// Rewrites PT_SCE_RELRO (0x61000010) as a read-write PT_LOAD so the converter maps it and
        /// allows relocations into it.
        public static byte[] RelroToLoad(byte[] elf)
        {
            long phoff = (long)BitConverter.ToUInt64(elf, 0x20);
            int phnum = BitConverter.ToUInt16(elf, 0x38);
            for (int i = 0; i < phnum; i++)
            {
                int header = (int)phoff + i * 0x38;
                if (BitConverter.ToUInt32(elf, header) != 0x61000010) continue;
                BitConverter.GetBytes(1u).CopyTo(elf, header);     // PT_LOAD
                BitConverter.GetBytes(6u).CopyTo(elf, header + 4); // read, write
            }
            return elf;
        }
    }

    /// Finishes the converted GTSport.exe: the launcher's icon (RyGTLauncher.exe's embedded copy of ryGT.ico),
    /// so Explorer and shortcuts show the game's art instead of a blank program icon, and the Windows
    /// (GUI) subsystem, so a double-click opens only the game window, without a console.
    static class GameExe
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool EndUpdateResource(IntPtr update, bool discard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll")]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        const int RtIcon = 3, RtGroupIcon = 14;
        const ushort GuiSubsystem = 2;

        public static bool IsPrepared(string exe)
        {
            return (Wine.Detected || HasIcon(exe)) && ReadSubsystem(exe) == GuiSubsystem;
        }

        public static void Prepare(string exe)
        {
            // (Not the icon under Wine: it is only for Windows' Explorer, and this keeps Set up from
            // depending on Wine rewriting the resources of a 100 MB program.)
            if (!Wine.Detected && !HasIcon(exe)) ApplyIcon(exe, EmbeddedIcon());
            if (ReadSubsystem(exe) != GuiSubsystem) WriteSubsystem(exe, GuiSubsystem);
        }

        /// FEAT-GTS-290 (from ryGT7): GT7.exe gets the player's own game icon (sce_sys\icon0.png, by default
        /// the one in app0 beside the executable) instead of the launcher's.
        public static void PrepareGt7(string exe, string iconPath = null)
        {
            if (iconPath == null) iconPath = Path.Combine(App0Layout.Top(Path.GetDirectoryName(exe), GameInfo.GT7.TitleId), "sce_sys", "icon0.png");
            if (!Wine.Detected && File.Exists(iconPath) && !HasIcon(exe)) ApplyIcon(exe, IconFromPng(iconPath));
            if (ReadSubsystem(exe) != GuiSubsystem) WriteSubsystem(exe, GuiSubsystem);
        }

        static byte[] EmbeddedIcon()
        {
            using (var stream = typeof(GameExe).Assembly.GetManifestResourceStream("ryGT.ico"))
            {
                if (stream == null) throw new Exception("RyGTLauncher.exe has no embedded icon.");
                var ico = new byte[stream.Length];
                stream.Read(ico, 0, ico.Length);
                return ico;
            }
        }

        /// A .ico of PNG images (16 to 256 pixels) made from a picture.
        static byte[] IconFromPng(string iconPath)
        {
            using (var original = Image.FromFile(iconPath))
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
                var images = new List<byte[]>();
                foreach (int size in sizes)
                    using (var bitmap = new Bitmap(original, size, size))
                    using (var png = new MemoryStream())
                    { bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png); images.Add(png.ToArray()); }
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + sizes.Length * 16;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
                return output.ToArray();
            }
        }

        public static void ApplyVersionInfo(string exe)
        {
            if (Wine.Detected) return;
            if (HasResource(exe, RtVersion) &&
                !String.Equals(FileVersionInfo.GetVersionInfo(exe).CompanyName, "RyGT", StringComparison.Ordinal)) return;
            byte[] data = VersionInfo(new[] {
                new[] { "CompanyName", "RyGT Launcher" },
                new[] { "FileDescription", "Gran Turismo Sport" },
                new[] { "FileVersion", "1.69" },
                new[] { "InternalName", "GTSport" },
                new[] { "OriginalFilename", "GTSport.exe" },
                new[] { "ProductName", "Gran Turismo Sport" },
                new[] { "ProductVersion", "1.69" },
            });
            IntPtr update = BeginUpdateResource(exe, false);
            if (update == IntPtr.Zero) throw new Exception("Could not open " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            bool ok = UpdateResource(update, (IntPtr)RtVersion, (IntPtr)1, 0x0409, data, (uint)data.Length);
            if (!EndUpdateResource(update, !ok) || !ok)
                throw new Exception("Could not write the version information into " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
        }

        const int RtVersion = 16;

        static bool HasResource(string exe, int type)
        {
            IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20);
            if (module == IntPtr.Zero) return false;
            try { return FindResource(module, (IntPtr)1, (IntPtr)type) != IntPtr.Zero; }
            finally { FreeLibrary(module); }
        }

        static byte[] VersionInfo(string[][] strings)
        {
            var fixedInfo = new MemoryStream();
            var w = new BinaryWriter(fixedInfo);
            w.Write(0xFEEF04BDu); w.Write(0x00010000u);
            w.Write(0x00010045u); w.Write(0u);
            w.Write(0x00010045u); w.Write(0u);
            w.Write(0x3Fu); w.Write(0u); w.Write(0x40004u);
            w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u);
            var table = new List<byte[]>();
            foreach (var s in strings)
                table.Add(Block(s[0], Encoding.Unicode.GetBytes(s[1] + "\0"), (s[1].Length + 1), 1, null));
            byte[] stringTable = Block("040904b0", null, 0, 1, table);
            byte[] stringFileInfo = Block("StringFileInfo", null, 0, 1, new List<byte[]> { stringTable });
            byte[] translation = Block("Translation", new byte[] { 0x09, 0x04, 0xb0, 0x04 }, 4, 0, null);
            byte[] varFileInfo = Block("VarFileInfo", null, 0, 1, new List<byte[]> { translation });
            return Block("VS_VERSION_INFO", fixedInfo.ToArray(), 52, 0,
                         new List<byte[]> { stringFileInfo, varFileInfo });
        }

        static byte[] Block(string key, byte[] value, int valueLength, ushort type, List<byte[]> children)
        {
            var block = new MemoryStream();
            var w = new BinaryWriter(block);
            w.Write((ushort)0); w.Write((ushort)valueLength); w.Write(type);
            w.Write(Encoding.Unicode.GetBytes(key + "\0"));
            while (block.Length % 4 != 0) w.Write((byte)0);
            if (value != null) w.Write(value);
            if (children != null)
                foreach (var child in children)
                {
                    while (block.Length % 4 != 0) w.Write((byte)0);
                    w.Write(child);
                }
            byte[] bytes = block.ToArray();
            BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
            return bytes;
        }

        // The subsystem field of the PE optional header: PE header offset at 0x3c, then the "PE\0\0"
        // signature (4 bytes), the file header (20 bytes) and 68 bytes into the optional header.
        static long SubsystemOffset(BinaryReader reader)
        {
            reader.BaseStream.Position = 0x3c;
            return reader.ReadInt32() + 4 + 20 + 68;
        }

        static ushort ReadSubsystem(string exe)
        {
            using (var reader = new BinaryReader(File.OpenRead(exe)))
            {
                reader.BaseStream.Position = SubsystemOffset(reader);
                return reader.ReadUInt16();
            }
        }

        static void WriteSubsystem(string exe, ushort subsystem)
        {
            using (var stream = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
            {
                long offset = SubsystemOffset(new BinaryReader(stream));
                stream.Position = offset;
                stream.Write(BitConverter.GetBytes(subsystem), 0, 2);
            }
        }

        static bool HasIcon(string exe)
        {
            IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20); // as a data file and image resource
            if (module == IntPtr.Zero) return false;
            try { return FindResource(module, (IntPtr)1, (IntPtr)RtGroupIcon) != IntPtr.Zero; }
            finally { FreeLibrary(module); }
        }

        /// Adds every image of the .ico as RT_ICON 1..n and the icon group as RT_GROUP_ICON 1.
        static void ApplyIcon(string exe, byte[] ico)
        {
            int count = BitConverter.ToUInt16(ico, 4);
            IntPtr update = BeginUpdateResource(exe, false);
            if (update == IntPtr.Zero) throw new Exception("Could not open " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            bool ok = false;
            try
            {
                var group = new MemoryStream();
                var writer = new BinaryWriter(group);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    int entry = 6 + 16 * i;
                    int size = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
                    var image = new byte[size];
                    Array.Copy(ico, offset, image, 0, size);
                    if (!UpdateResource(update, (IntPtr)RtIcon, (IntPtr)(i + 1), 0, image, (uint)size))
                        throw new Exception("Could not add the icon (error " + Marshal.GetLastWin32Error() + ").");
                    writer.Write(ico, entry, 12);   // width, height, colors, reserved, planes, bit count, size
                    writer.Write((ushort)(i + 1)); // the RT_ICON id instead of the file offset
                }
                var data = group.ToArray();
                if (!UpdateResource(update, (IntPtr)RtGroupIcon, (IntPtr)1, 0, data, (uint)data.Length))
                    throw new Exception("Could not add the icon group (error " + Marshal.GetLastWin32Error() + ").");
                ok = true;
            }
            finally
            {
                if (!EndUpdateResource(update, !ok) && ok)
                    throw new Exception("Could not write the icon into " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            }
        }
    }

    static class AmdSoftware
    {
        // AMD Software can mark a manually added game hidden again; every start clears that.
        static void Unhide(string list, string exe)
        {
            // AMD Software rewrites the list when it closes; leave it alone while it is open.
            if (Process.GetProcessesByName("RadeonSoftware").Length != 0) return;
            string text = File.ReadAllText(list);
            int at = text.IndexOf(exe.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            int entry = text.LastIndexOf("\"FSRUpgradeAvailable\"", at, StringComparison.Ordinal);
            int hidden = entry < 0 ? -1 : text.IndexOf("\"hidden\"", entry, StringComparison.Ordinal);
            if (hidden < 0) return;
            int value = text.IndexOf('"', text.IndexOf(':', hidden)) + 1;
            if (String.CompareOrdinal(text, value, "TRUE", 0, 4) != 0) return;
            File.Copy(list, list + ".rygt-unhide-backup", true);
            File.WriteAllText(list, text.Substring(0, value) + "FALSE" + text.Substring(value + 4), new UTF8Encoding(false));
        }

        public static void EnsureGameEntry(string root, string exe)
        {
            try
            {
                if (Wine.Detected || Environment.GetEnvironmentVariable("RYGT_AMD_SOFTWARE") == "0") return;
                string list = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "AMD", "CN", "gmdb.blb");
                string script = Path.Combine(root, "tools", "amd", "Add-To-AMD-Software.ps1");
                string marker = Path.Combine(root, "user", "amd-software-entry.txt");
                if (!File.Exists(list) || !File.Exists(script) || !File.Exists(exe)) return;
                Unhide(list, exe);
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == exe) return;
                string escaped = exe.Replace("\\", "\\\\");
                if (File.ReadAllText(list).IndexOf(escaped, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    File.WriteAllText(marker, exe);
                    return;
                }
                var start = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -GameExe \"" + exe + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(30000)) return;
                    if (process.ExitCode == 0) File.WriteAllText(marker, exe);
                    // The game whose exe this is: its logs\<serial> folder.
                    string serial = GameInfo.Of(string.Equals(Path.GetFileName(exe), GameInfo.Of(GameKind.GT7).ExeName,
                                                              StringComparison.OrdinalIgnoreCase) ? GameKind.GT7 : GameKind.GTSport).TitleId;
                    Directory.CreateDirectory(LogFiles.Folder(root, serial));
                    File.AppendAllText(Path.Combine(LogFiles.Folder(root, serial), "setup.log"),
                                       "AMD Software game list: " + output.Trim() + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // A local build record, not an authenticity signature. Written only after setup succeeds.
    static class GameBuildIntegrity
    {
        public static string Hash(string exe)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(exe))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        public static bool Matches(string exe)
        {
            string stamp = exe + ".sha256";
            if (!File.Exists(exe) || !File.Exists(stamp)) return false;
            string recorded = File.ReadAllText(stamp).Trim();
            return recorded.Length == 64 && recorded.All(Uri.IsHexDigit) &&
                   String.Equals(recorded, Hash(exe), StringComparison.OrdinalIgnoreCase);
        }

        public static void RequireStopped(string exe)
        {
            if (!File.Exists(exe)) return;
            try { using (File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException e) { throw new IOException("Close the game before rebuilding its executable.", e); }
        }

        public static void Record(string exe)
        {
            File.WriteAllText(exe + ".sha256", Hash(exe) + Environment.NewLine);
        }

        public static bool Ensure(string exe, Action rebuild)
        {
            if (Matches(exe)) return false;
            RequireStopped(exe);
            rebuild();
            if (!Matches(exe)) throw new IOException("The rebuilt game did not match its recorded SHA-256; it will not be started.");
            return true;
        }
    }

    /// FIX-GTS-286: a release's notes, one entry per heading, paragraph or bullet; headings bold, nested
    /// bullets indented, long entries wrapped. Scrolled by pixels, so it ends where the text ends.
    class ChangelogDialog : Form
    {
        sealed class NotesView : Control
        {
            readonly List<KeyValuePair<int, string>> entries;
            readonly Font heading;
            readonly List<Rectangle> areas = new List<Rectangle>();
            const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left;

            public NotesView(List<KeyValuePair<int, string>> entries, Font font)
            {
                this.entries = entries;
                Font = font;
                heading = new Font(font, FontStyle.Bold);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            static int Indent(int level) { return level == 0 ? 8 : level == 1 ? 24 : 42; }

            /// Lays the entries out for `width`; the height they take.
            public int Layout(int width)
            {
                areas.Clear();
                int y = 6;
                foreach (var entry in entries)
                {
                    if (entry.Key == 0 && y > 6) y += 10;
                    int x = Indent(entry.Key), w = Math.Max(100, width - x - 12);
                    var size = TextRenderer.MeasureText(entry.Value, entry.Key == 0 ? heading : Font, new Size(w, 0), Flags);
                    areas.Add(new Rectangle(x, y, w, size.Height));
                    y += size.Height + 6;
                }
                return y + 4;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                for (int k = 0; k < entries.Count && k < areas.Count; ++k)
                {
                    var area = areas[k];
                    if (!e.ClipRectangle.IntersectsWith(area)) continue;
                    var entry = entries[k];
                    if (entry.Key > 0)
                        TextRenderer.DrawText(e.Graphics, entry.Key == 1 ? "\u2022" : "\u2013", Font, new Point(area.X - 13, area.Y), Dark.DimText, TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(e.Graphics, entry.Value, entry.Key == 0 ? heading : Font, area, entry.Key == 0 ? Color.White : Dark.Text, Flags);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) heading.Dispose();
                base.Dispose(disposing);
            }
        }

        public ChangelogDialog(ReleaseInfo release)
        {
            Text = release.Name.Length > 0 ? release.Name.Replace("RyGTS", "RyGT") : "Changelog"; // FIX-RT-010: one name
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } // the launcher's logo
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(420, 300);
            Font = new Font("Segoe UI", 9.5f);
            Padding = new Padding(12);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Dark.Field };
            var view = new NotesView(release.Changes(), Font) { BackColor = Dark.Field, Location = new Point(0, 0) };
            scroll.Controls.Add(view);
            // The view is as tall as its text, so the panel scrolls exactly to its end.
            Action fit = delegate
            {
                int width = scroll.ClientSize.Width;
                int height = view.Layout(width);
                if (height > scroll.ClientSize.Height && !scroll.VerticalScroll.Visible)
                    height = view.Layout(width - SystemInformation.VerticalScrollBarWidth); // room for the bar
                view.Size = new Size(Math.Max(1, scroll.ClientSize.Width), height);
                view.Invalidate();
            };
            scroll.Resize += delegate { fit(); };
            scroll.MouseEnter += delegate { scroll.Focus(); };
            view.MouseEnter += delegate { scroll.Focus(); };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0), BackColor = Color.Transparent };
            bottom.Controls.Add(close);
            Controls.Add(scroll);
            Controls.Add(bottom);
            CancelButton = close;
            Dark.Apply(this);
            scroll.BackColor = Dark.Field;
            view.BackColor = Dark.Field;
            HandleCreated += delegate { Dark.TitleBar(this); };
            Shown += delegate { fit(); scroll.Focus(); };
        }
    }

    class Settings
    {
        public string Game = "", Update = "", Modules = "", Fonts = "";
        public string StdoutLevel = "Warning";
        public bool HddStreaming = false;
        public bool ShowAdvanced = false; // FEAT-GTS-438: the Advanced page; FEAT-GTS-266: hidden by default
        public bool PromptObsolete = true; // FEAT-GTS-469: offer to remove obsolete config entries
        public List<EnvVar> Env = new List<EnvVar>(); // FEAT-GTS-433
        readonly string path;

        /// FEAT-GTS-290: a null path (a game without its folder yet) keeps the values in memory only.
        public Settings(string path)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim() == "env")
                {
                    var entry = CustomEnvironment.Parse(line.Substring(eq + 1)); // the value as written, not trimmed
                    if (entry != null) Env.Add(entry);
                    continue;
                }
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key == "game") Game = value;
                else if (key == "update") Update = value;
                else if (key == "modules") Modules = value;
                else if (key == "fonts") Fonts = value;
                else if (key == "stdout_level") StdoutLevel = LogFiles.Levels[LogFiles.LevelIndex(value)];
                else if (key == "hdd_streaming") HddStreaming = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (key == "show_advanced") ShowAdvanced = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (key == "prompt_obsolete") PromptObsolete = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// FEAT-GTS-291: each game's settings are <TitleId>.RyGT.ini in its folder (CUSA03220.RyGT.ini for GT
        /// Sport, CUSA24769.RyGT.ini for GT7, where GT Sport had RyGT.ini and GT7 RyGT7.ini plus a RyGT.ini the
        /// runtime read; SharedNames). The runtime reads the same file (game=, update=, stdout_level=), so
        /// GT7's values are no longer mirrored into another one. GT7's has no hdd_streaming or
        /// prompt_obsolete line, as ryGT7's launcher wrote it.
        public bool IsGt7 { get { return path != null && Path.GetFileName(path).StartsWith(GameInfo.GT7.TitleId, StringComparison.OrdinalIgnoreCase); } }

        static readonly string[] OwnKeys = { "game", "update", "modules", "fonts", "stdout_level", "hdd_streaming", "show_advanced", "prompt_obsolete", "env" };

        /// Writes this class's lines, then every other line of the file as it was (online play's keys, which
        /// OnlinePlay keeps in the same file, and anything else).
        public void Save()
        {
            if (path == null) return;
            var lines = new List<string> { "game=" + Game, "update=" + Update, "modules=" + Modules, "fonts=" + Fonts, "stdout_level=" + StdoutLevel };
            if (!IsGt7) lines.Add("hdd_streaming=" + (HddStreaming ? "1" : "0"));
            lines.Add("show_advanced=" + (ShowAdvanced ? "1" : "0"));
            if (!IsGt7) lines.Add("prompt_obsolete=" + (PromptObsolete ? "1" : "0"));
            foreach (var entry in Env) lines.Add("env=" + CustomEnvironment.Format(entry));
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && OwnKeys.Contains(line.Substring(0, eq).Trim())) continue;
                    if (line.Trim().Length == 0) continue;
                    lines.Add(line);
                }
            File.WriteAllLines(path, lines);
        }
    }

    partial class LoaderForm : Form, IBackdrop
    {
        // FEAT-GTS-290: one launcher for GT Sport and GT7. Each game has its folder (its exe, runtime,
        // <TitleId>.RyGT.ini, config, logs and shaders; FEAT-GTS-291: both may be one folder), so `root` and
        // `settings` are the selected game's. The settings pages are built for the selected game and built again when it changes.
        readonly string launcherRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        readonly LauncherState state;
        GameKind target;
        readonly Dictionary<GameKind, Settings> loadedSettings = new Dictionary<GameKind, Settings>();
        readonly Dictionary<GameKind, string> loadedFrom = new Dictionary<GameKind, string>();

        /// The selected game's folder (its GTSport.exe or GT7.exe and everything Set up makes).
        string root { get { return state.RootOf(target); } }

        /// The selected game's <TitleId>.RyGT.ini (CUSA03220.RyGT.ini for GT Sport, CUSA24769.RyGT.ini for GT7).
        Settings settings { get { return SettingsOf(target); } }

        Settings SettingsOf(GameKind game)
        {
            string folder = state.Ready(game) ? state.RootOf(game) : "";
            Settings found;
            string from;
            if (loadedSettings.TryGetValue(game, out found) && loadedFrom.TryGetValue(game, out from) &&
                string.Equals(from, folder, StringComparison.OrdinalIgnoreCase)) return found;
            // A game without its folder yet keeps its paths in memory only (Save does nothing).
            found = new Settings(folder.Length > 0 ? Path.Combine(folder, GameInfo.Of(game).IniName) : null);
            loadedSettings[game] = found;
            loadedFrom[game] = folder;
            return found;
        }

        bool RootReady(GameKind game) { return state.Ready(game); }

        GameInfo info { get { return GameInfo.Of(target); } }

        TextBox log;
        Button setup, addDlss, downloadDlss;
        readonly Button play;
        readonly Label status;
        readonly ToolTip shaderObjectTip = new ToolTip();
        volatile bool busy;
        Action<bool> showAdvanced; // FEAT-GTS-438
        // Home: the picture over the whole window with the title, Play and the settings button.
        // Settings: a sidebar of categories and their pages.
        readonly HomeView home;
        readonly DarkComboBox gameChooser; // FEAT-GTS-290
        readonly GlowButton updateButton; // FEAT-GTS-272
        readonly GlowButton changelogButton; // FIX-GTS-286
        ReleaseInfo pendingUpdate;
        readonly Panel settingsView, pagesHost, sidebar;
        readonly Label sideVersion;
        readonly Curtain curtain;
        readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        readonly Dictionary<string, SidebarItem> pageItems = new Dictionary<string, SidebarItem>();
        string[] pageNames = { "Game" };
        string lastPage = "Game";
        // Pages left out of the sidebar (Advanced when Settings > About turns it off). Control.Visible
        // cannot tell: it is false for every item while the settings view is hidden.
        readonly HashSet<string> hiddenPages = new HashSet<string>();
        // The sidebar's game part (its logo or title and the page list), made again with the pages.
        readonly List<Control> sidebarParts = new List<Control>();
        // Each game's folder, game, update, system modules and fonts fields on the Game page.
        readonly Dictionary<GameKind, TextBox[]> pathBoxes = new Dictionary<GameKind, TextBox[]>();
        bool pagesStale, obsoleteAsked;
        string logCarry = "";

        public LoaderForm() : this(null, null) { }

        /// The tests and --setup --game: a game (and a folder for it) instead of RyGT-Launcher.ini's.
        internal LoaderForm(GameKind? game, string folder)
        {
            state = new LauncherState(launcherRoot);
            if (game.HasValue)
            {
                state.Transient = true;
                state.Last = game.Value;
                if (folder != null) state.SetRoot(game.Value, folder);
            }
            target = state.Last;
            MigrateGt7Serial(); // GAME-004, FEAT-GTS-291: before anything reads either game's settings
            SetAccent(info.Accent, info.AccentHover, info.AccentText);
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(1040, 640);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Dark.Back;
            // The launcher pictures (launcher\background*.png or .jpg in the game's folder, else the
            // game's own pic1.png) fill the home view, one after another when there are several.
            pictures = LoadBackgrounds();
            ResizeRedraw = true;

            settingsView = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false };

            // ---- Home: the pictures, the logo (launcher\logo.png) or the title, Play and the settings button
            play = new GlowButton { Text = "Play", Size = new Size(236, 46), Font = Dark.Caps(11f) };
            play.Click += delegate
            {
                if (pagesStale) BuildPages(); // GT7's online play uses its pages' fields
                if (target == GameKind.GT7) PlayFromSwitch(); else Play();
            };
            var gear = new GlowButton { Text = ((char)0xE713).ToString(), Glyph = true, Size = new Size(46, 46), Font = new Font("Segoe MDL2 Assets", 13f) };
            gear.Click += delegate { ShowSettings(lastPage); };
            home = new HomeView(pictures, (GlowButton)play, gear) { Dock = DockStyle.Fill, Title = "GRAN TURISMO" };
            // FEAT-GTS-290: the target game, at the top left of the home view. Changing it fades the
            // accent (red GT Sport, blue GT7) and the pictures, and makes the settings that game's.
            gameChooser = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 236, FlatStyle = FlatStyle.Flat, AccessibleName = "Game" };
            gameChooser.Items.AddRange(new object[] { GameInfo.GTSport.Name, GameInfo.GT7.Name });
            gameChooser.SelectedIndex = (int)target;
            gameChooser.SelectedIndexChanged += delegate { SwitchGame((GameKind)gameChooser.SelectedIndex); };
            home.GameChooser = gameChooser;
            home.Controls.Add(gameChooser);
            // FEAT-GTS-272: the update button, shown when a newer build is found; leftovers of an update go.
            if (RootReady(GameKind.GTSport)) Updater.CleanUp(state.RootOf(GameKind.GTSport));
            if (RootReady(GameKind.GT7)) Updater.CleanUp(state.RootOf(GameKind.GT7));
            updateButton = new GlowButton { Text = "Update", Size = new Size(230, 40), Font = Dark.Caps(10f), Visible = false };
            updateButton.Click += delegate { InstallUpdate(); };
            home.UpdateButton = updateButton;
            home.Controls.Add(updateButton);
            // FIX-GTS-286: the pending update's release notes, as a list.
            changelogButton = new GlowButton { Text = "Changelog", Size = new Size(150, 40), Font = Dark.Caps(10f), Visible = false };
            changelogButton.Click += delegate
            {
                if (pendingUpdate == null) return;
                using (var dialog = new ChangelogDialog(pendingUpdate)) dialog.ShowDialog(this);
            };
            home.ChangelogButton = changelogButton;
            home.Controls.Add(changelogButton);
            curtain = new Curtain { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(curtain);
            Controls.Add(settingsView);
            Controls.Add(home);
            // The status shows on the home view and on the Game page.
            status = new Label { AutoSize = true };
            status.TextChanged += delegate { home.Status = status.Text; home.Invalidate(false); };

            // ---- Settings: the sidebar
            sidebar = new Panel { Dock = DockStyle.Left, Width = 224, BackColor = Dark.Sidebar };
            pagesHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel };
            settingsView.Controls.Add(pagesHost);
            settingsView.Controls.Add(sidebar);
            var back = new Label
            {
                Text = (char)0x2039 + "   " + Dark.Spaced("BACK"), AutoSize = true, Font = Dark.Caps(9f), ForeColor = Dark.DimText,
                Tag = "heading", Cursor = Cursors.Hand, Location = new Point(28, 30)
            };
            back.Click += delegate { ShowHome(); };
            back.MouseEnter += delegate { back.ForeColor = Dark.Text; };
            back.MouseLeave += delegate { back.ForeColor = Dark.DimText; };
            sidebar.Controls.Add(back);
            sideVersion = new Label { AutoSize = true, ForeColor = Color.FromArgb(96, 102, 114), Tag = "heading" };
            sidebar.Controls.Add(sideVersion);
            sidebar.Layout += delegate { sideVersion.Location = new Point(28, sidebar.ClientSize.Height - sideVersion.Height - 24); };

            ShowGameOnHome(false);
            Dark.Apply(this);
            Dark.SeeThrough(home);
            BuildPages();
            HandleCreated += delegate { Dark.TitleBar(this); };
            Shown += delegate
            {
                ActiveControl = play.Enabled ? (Control)play : gear;
                if (target == GameKind.GTSport) BeginInvoke((MethodInvoker)PromptObsoleteSettings); // once the window is up
                if (RootReady(target) && ReadSetting("Launcher", "check_updates", true)) CheckForUpdate(false); // FEAT-GTS-272
            };
            FormClosing += delegate { CommitPaths(); };
            RefreshState();
            // GT7 (from ryGT7): Play and Set up follow whether GT7.exe is running.
            var stateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            stateTimer.Tick += delegate { if (!busy && target == GameKind.GT7) RefreshState(); };
            stateTimer.Start();
            FormClosed += delegate { stateTimer.Dispose(); };
        }

        // ---- FEAT-GTS-290: the target game

        /// Makes another game the target: the accent and the pictures fade to it (about 300 ms), and its
        /// settings pages are built once the fade is done (or at once when they are needed sooner).
        void SwitchGame(GameKind game)
        {
            if (game == target) return;
            if (busy || preparingServer)
            {
                gameChooser.SelectedIndex = (int)target;
                MessageBox.Show(this, "Wait until " + info.Short + " has finished setting up or starting.");
                return;
            }
            CommitPaths();
            target = game;
            state.Last = game;
            MigrateGt7Serial(); // GAME-004, FEAT-GTS-291
            try { state.Save(); } catch { }
            setupLogPath = null;
            logCarry = "";
            if (gameChooser.SelectedIndex != (int)game) gameChooser.SelectedIndex = (int)game;
            ClearUpdate();
            pagesStale = true;
            FadeAccent(game, delegate
            {
                // After the fade, so building the pages (which reads the GPUs) never stalls it.
                if (pagesStale && !IsDisposed) BuildPages();
            });
            ShowGameOnHome(true);
            RefreshState();
            if (game == GameKind.GTSport && !obsoleteAsked) BeginInvoke((MethodInvoker)PromptObsoleteSettings);
            if (RootReady(game) && ReadSetting("Launcher", "check_updates", true)) CheckForUpdate(false);
        }

        /// GAME-004: renames GT7's files from GT Sport's serial to its own in GT7's folder
        /// (SerialMigration), each move in that folder's setup log. Safe to call often: once done it finds
        /// nothing to rename. GT Sport's folder is never touched.
        void MigrateGt7Serial()
        {
            if (RootReady(GameKind.GT7)) LogFor(GameKind.GT7, SerialMigration.MigrateGt7(state.RootOf(GameKind.GT7)));
            // FEAT-GTS-291: and both games' settings file, convert, temp0 and data take their serial
            // (SharedNames), before anything reads the settings.
            MigrateSharedNames(GameKind.GTSport);
            MigrateSharedNames(GameKind.GT7);
        }

        /// FEAT-GTS-291: moves a game's old shared names in its folder (SharedNames.Migrate), each step in its
        /// setup log. Once done it finds nothing to do.
        void MigrateSharedNames(GameKind game)
        {
            if (!RootReady(game)) return;
            List<string> notes;
            try { notes = SharedNames.Migrate(state.RootOf(game), GameInfo.Of(game)); }
            catch (Exception e) { notes = new List<string> { "Could not give the settings file and folders their serial names: " + e.Message }; }
            LogFor(game, notes);
        }

        /// Lines for a game's setup log: the log view's while it is the selected game, else appended to its
        /// logs\<TitleId>\setup.log.
        void LogFor(GameKind game, List<string> notes)
        {
            if (notes.Count == 0) return;
            if (target == game) { foreach (string note in notes) Log(note); return; }
            AppendSetupLog(state.RootOf(game), GameInfo.Of(game).TitleId, notes.ToArray());
        }

        public void SelectGame(GameKind game)
        {
            if (game != target) SwitchGame(game);
        }

        int accentVersion;
        const int AccentFadeMs = 300;

        /// Fades Dark.Accent, AccentHover and AccentText to the game's over AccentFadeMs on the animation
        /// timer; the home view and sidebar read them as they paint (double buffered, so no flicker).
        void FadeAccent(GameKind game, Action done)
        {
            var to = GameInfo.Of(game);
            Color fromAccent = Dark.Accent, fromHover = Dark.AccentHover, fromText = Dark.AccentText;
            int mine = ++accentVersion, start = Environment.TickCount;
            if (!IsHandleCreated || !Visible)
            {
                SetAccent(to.Accent, to.AccentHover, to.AccentText);
                if (done != null) done();
                return;
            }
            Anim.Run(() =>
            {
                if (mine != accentVersion || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / (float)AccentFadeMs);
                float k = t * t * (3 - 2 * t); // smooth start and end
                SetAccent(Anim.Mix(fromAccent, to.Accent, k), Anim.Mix(fromHover, to.AccentHover, k), Anim.Mix(fromText, to.AccentText, k));
                if (t < 1) return true;
                if (done != null) BeginInvoke(done);
                return false;
            });
        }

        void SetAccent(Color accent, Color hover, Color text)
        {
            Color old = Dark.AccentText;
            Dark.Accent = accent;
            Dark.AccentHover = hover;
            Dark.AccentText = text;
            if (old.ToArgb() != text.ToArgb()) Recolor(this, old, text);
            if (home != null && home.Visible) home.Invalidate(false);
            else if (settingsView != null && settingsView.Visible) settingsView.Invalidate(true);
        }

        /// The labels written in the accent colour (the sidebar's SPORT or 7, the warnings) follow it.
        static void Recolor(Control control, Color old, Color now)
        {
            foreach (Control child in control.Controls)
            {
                if (child.ForeColor.ToArgb() == old.ToArgb()) { child.ForeColor = now; child.Invalidate(); }
                if (child.HasChildren) Recolor(child, old, now);
            }
        }

        /// The home view for the selected game: its title, caption, version, logo and pictures, and the
        /// window's title and icon.
        void ShowGameOnHome(bool fadePictures)
        {
            bool ready = RootReady(target);
            Text = info.WindowTitle;
            // FIX-RT-010: one launcher, one icon: the window takes RyGTLauncher.exe's own for both games.
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Image gameIcon = null;
            if (target == GameKind.GT7 && ready)
            {
                // GT7's own icon (icon0.png) beside the player's online ID once Set up has found it.
                try
                {
                    string iconPath = GameFile("sce_sys\\icon0.png");
                    if (iconPath != null)
                        using (var bitmap = new Bitmap(iconPath)) gameIcon = new Bitmap(bitmap);
                }
                catch { }
            }
            home.Accent = info.TitleAccent;
            home.Logo = LoadLogo(); // the game folder's, else the RY logo built into RyGTLauncher.exe
            home.Player = "";
            home.PlayerLogo = null;
            if (target == GameKind.GT7)
            {
                // FIX-RT-011: GT7's update, the VERSION in the game's param.sfo (01.71 -> UPDATE 1.71; APP_VER
                // stays 01.00 in a merged dump), else PS4.
                string update = null;
                try
                {
                    string sfo = ready ? GameFile("sce_sys\\param.sfo") : null;
                    string value;
                    if (sfo != null && GameLayout.ReadSfo(sfo).TryGetValue("VERSION", out value) && value.Trim().Length > 0)
                        update = value.Trim().TrimStart('0');
                }
                catch { }
                home.Caption = Dark.Spaced("CUSA24769   " + (char)183 + "   " + (update != null ? "UPDATE " + update : "PS4"));
                string release = ready ? Updater.ReleaseVersion(root) : null;
                home.Version = "RyGT Launcher " + LauncherVersion.Value + "  " + (char)183 + "  RyGT" + (release == null ? "" : " " + Updater.Display(release)) +
                               "  " + (char)183 + "  GT7" + (update != null ? " " + update : "") + " " + (char)183 + " CUSA24769";
                try { home.PlayerLogo = gameIcon ?? Icon.ToBitmap(); } catch { }
            }
            else
            {
                string installed = ready ? Updater.InstalledVersion(root) : null;
                home.Caption = Dark.Spaced("CUSA03220   " + (char)183 + "   UPDATE 1.69");
                home.Version = "RyGT Launcher " + LauncherVersion.Value + "  " + (char)183 + "  RyGT" + (installed == null ? "" : " " + Updater.Display(installed)) +
                               "  " + (char)183 + "  GT Sport 1.69 " + (char)183 + " CUSA03220";
            }
            if (fadePictures) home.ReplacePictures(LoadBackgrounds());
            home.PerformLayout();
            home.Invalidate(true);
        }

        /// Builds the settings pages (and the sidebar's list) for the selected game, replacing the
        /// other game's. A game without its folder yet gets only the Game and About pages.
        void BuildPages()
        {
            pagesStale = false;
            if (log != null) logCarry = log.Text;
            pagesHost.SuspendLayout();
            sidebar.SuspendLayout();
            foreach (var old in pagesHost.Controls.Cast<Control>().ToList()) { pagesHost.Controls.Remove(old); old.Dispose(); }
            foreach (var part in sidebarParts) { sidebar.Controls.Remove(part); part.Dispose(); }
            sidebarParts.Clear();
            pages.Clear();
            pageItems.Clear();
            hiddenPages.Clear();
            pathBoxes.Clear();
            log = null;
            setup = addDlss = downloadDlss = null;
            controls = null;
            uiHiresBox = null;
            rendererCombo = null;
            showAdvanced = null;
            bool ready = RootReady(target);
            SwitchWalker.Game = target;
            CustomEnvironment.ExeName = info.ExeName;
            pageNames = !ready ? new[] { "Game", "About" }
                      : target == GameKind.GT7 ? new[] { "Game", "Display", "Graphics", "Shaders", "Controls", "Account", "Online", "Advanced", "About" }
                      : new[] { "Game", "Display", "Graphics", "Shaders", "Controls", "Advanced", "About" };

            // The sidebar: the game's logo, or its title as text.
            var logoImage = LoadLogo();
            if (logoImage != null)
            {
                sidebarParts.Add(new PictureBox
                {
                    Image = logoImage, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(26, 70),
                    Size = new Size(150, Math.Min(64, 150 * logoImage.Height / logoImage.Width))
                });
            }
            else
            {
                var logo = new PictureBox { Size = new Size(34, 34), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(28, 78) };
                try { logo.Image = new Icon(Icon, 48, 48).ToBitmap(); } catch { }
                sidebarParts.Add(logo);
                sidebarParts.Add(new Label { Text = "GRAN TURISMO", AutoSize = true, Font = Dark.Caps(12f), Location = new Point(70, 76) });
                sidebarParts.Add(new Label { Text = info.TitleAccent, AutoSize = true, Font = Dark.Caps(12f), ForeColor = Dark.AccentText, Tag = "heading", Location = new Point(70, 95) });
            }
            sideVersion.Text = info.Product + " for Windows";
            foreach (var name in pageNames)
            {
                string page = name;
                var item = new SidebarItem(page) { Width = 224 };
                item.Click += delegate { ShowSettings(page); };
                sidebarParts.Add(item);
                pageItems[page] = item;
            }
            foreach (var part in sidebarParts) sidebar.Controls.Add(part);
            PlaceSidebarItems();

            BuildGamePage();
            Action afterRowify = null;
            if (ready)
            {
                if (target == GameKind.GT7)
                {
                    EnsureGT7Defaults();
                    var displayPage = NewPage("Display", "Display", "How the game's window looks. Changes apply the next time you play.");
                    var graphicsPage = NewPage("Graphics", "Graphics", "The renderer, upscaling and frame generation. Changes apply the next time you play.");
                    var shadersPage = NewPage("Shaders", "Shaders", "Shaders are built once per car and track; these settings decide when that happens.");
                    afterRowify = BuildGt7Pages(displayPage, graphicsPage);
                    BuildShadersPage(shadersPage);
                }
                else
                {
                    // Display, Graphics and Shaders: saved as they change; the game reads them when it starts.
                    var displayPage = NewPage("Display", "Display", "How the game's window looks and which GPU draws it. Changes apply the next time you play.");
                    var graphicsPage = NewPage("Graphics", "Graphics", "Upscaling, frame generation, sharpening and effects. Changes apply the next time you play.");
                    var shadersPage = NewPage("Shaders", "Shaders", "Shaders are built once per car and track; these settings decide when that happens.");
                    BuildGtsPages(displayPage, graphicsPage);
                    BuildShadersPage(shadersPage);
                }
            }
            BuildAboutPage(ready);
            if (ready) BuildAdvancedPage();

            // The setting helpers build label + drop-down rows; they become full-width rows here.
            foreach (var pair in pages) if (pair.Key != "Advanced") Rowify(pair.Value); // the Advanced page lays out its own
            // GT7: Rowify replaced the Graphics rows' panels: shows and hides them again (the multiplier).
            if (afterRowify != null) afterRowify();
            Dark.Apply(pagesHost);
            foreach (var part in sidebarParts) Dark.Apply(part);
            if (log != null)
            {
                log.BackColor = Dark.Field;
                log.ForeColor = Color.FromArgb(180, 186, 196);
                if (logCarry.Length > 0) log.AppendText(logCarry);
            }
            sidebar.ResumeLayout(true);
            pagesHost.ResumeLayout(true);
            if (!pages.ContainsKey(lastPage) || hiddenPages.Contains(lastPage)) lastPage = "Game";
            if (settingsView.Visible) ShowSettings(lastPage, false);
            RefreshState();
        }

        /// Game: the selected game's folder and files (the game list on the start screen picks it; the
        /// other game's stay saved in its own settings and show when it is picked), Set up and its log.
        void BuildGamePage()
        {
            var gamePage = NewPage("Game", "Game",
                "Your own decrypted " + info.Short + " dump, its update if it is separate, and your PS4 firmware's system modules and fonts. " +
                "Set up converts " + info.Short + " once; Play starts it. Pick the other game on the start screen to see its folders.");
            AddGameFiles(gamePage, target);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
            setup = new Button { Text = "Set up " + info.Short, AutoSize = true, Tag = "accent", Margin = new Padding(0, 0, 10, 0) };
            setup.Click += delegate { StartSetup(); };
            var open = new Button { Text = "Open folder", AutoSize = true, Margin = new Padding(0) };
            open.Click += delegate { OpenFolder(); };
            actions.Controls.Add(setup);
            actions.Controls.Add(open);
            gamePage.Controls.Add(actions);
            var gameStatus = new Label { AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(1, 12, 0, 0), Text = status.Text };
            gamePage.Controls.Add(gameStatus);
            EventHandler follow = delegate { if (!gameStatus.IsDisposed) gameStatus.Text = status.Text; };
            status.TextChanged += follow;
            gameStatus.Disposed += delegate { status.TextChanged -= follow; };
            var logSection = Section(gamePage, "Set up log");
            var logBox = new Card(Dark.Field) { Dock = DockStyle.Fill, Height = 190, Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 8, 4, 8), Margin = new Padding(0, 6, 0, 0) };
            logBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f), Margin = new Padding(0)
            };
            logBox.Controls.Add(log);
            logSection.Controls.Add(logBox);
        }

        /// One game's section on the Game page: its folder, then its dump, update, modules and fonts
        /// (the labels its own launcher had). Saved to that game's <TitleId>.RyGT.ini when left.
        void AddGameFiles(TableLayoutPanel page, GameKind game)
        {
            var gameInfo = GameInfo.Of(game);
            bool ready = RootReady(game);
            var files = Section(page, gameInfo.Name);
            var boxes = new TextBox[5];
            pathBoxes[game] = boxes;
            // FEAT-RT-005: both games' folder is the launcher's own (each game's files follow its serial),
            // so neither has a folder field.
            var s = SettingsOf(game);
            if (game == GameKind.GTSport)
            {
                boxes[1] = AddRow(files, "Game (CUSA03220)", s.Game);
                boxes[2] = AddRow(files, "Update 1.69 (if separate)", s.Update);
                boxes[3] = AddRow(files, "System modules", s.Modules);
                boxes[4] = AddRow(files, "Fonts (optional)", s.Fonts);
            }
            else
            {
                boxes[1] = AddRow(files, "Base game (CUSA24769)", s.Game);
                boxes[2] = AddRow(files, "Update (optional, if separate)", s.Update);
                boxes[3] = AddRow(files, "System modules", s.Modules);
                boxes[4] = AddRow(files, "Fonts", s.Fonts);
            }
            for (int i = 1; i < boxes.Length; i++)
            {
                boxes[i].Enabled = ready;
                boxes[i].Leave += delegate { CommitPaths(game); };
            }
            string former = game == GameKind.GT7 ? state.FormerGt7Root() : null;
            if (!ready)
                files.Controls.Add(Hint(game == GameKind.GT7
                    ? "GT7 uses this launcher's folder, like GT Sport. It doesn't have GT7's files yet (libs\\libSceTextToSpeech.prx, patches\\CUSA24769.xml): update it to a RyGT build that carries them (Settings > About > Updates), or copy them in from the ryGT7 release."
                    : "The launcher's folder can't be used (it must hold converter, libs and tools)."));
            else if (game == GameKind.GTSport)
                files.Controls.Add(Hint("Without PS4 dumped fonts, some in-game UI fonts may not draw or appear correctly."));
            if (former != null)
                files.Controls.Add(Hint("GT7 used to have its own folder, " + former + ". Its saves and settings are still there: " +
                                        "copy user\\savedata\\CUSA24769 and CUSA24769.RyGT.ini into this folder to keep them."));
        }

        /// The Game page's paths into each game's settings (saved when they changed).
        void CommitPaths()
        {
            CommitPaths(GameKind.GTSport);
            CommitPaths(GameKind.GT7);
        }

        void CommitPaths(GameKind game)
        {
            TextBox[] boxes;
            if (!pathBoxes.TryGetValue(game, out boxes) || boxes[1] == null || boxes[1].IsDisposed || !RootReady(game)) return;
            var s = SettingsOf(game);
            string g = boxes[1].Text.Trim(), u = boxes[2].Text.Trim(), m = boxes[3].Text.Trim(), f = boxes[4].Text.Trim();
            if (g == s.Game && u == s.Update && m == s.Modules && f == s.Fonts) return;
            s.Game = g; s.Update = u; s.Modules = m; s.Fonts = f;
            try { s.Save(); } catch { }
        }

        /// Shaders: when they are built (both games), and GT Sport's shader storage and options.
        void BuildShadersPage(TableLayoutPanel shadersPage)
        {
            bool gts = target == GameKind.GTSport;
            TableLayoutPanel shaders;
            if (gts)
            {
                // Shaders: the stored ones are built while the splash screen shows; a new one pauses
                // the game until it is compiled (instead of a stutter and missing objects).
                var storage = Section(shadersPage, "Storage");
                var hddStreaming = new ToggleRow { Text = "HDD streaming read-ahead (experimental)", Checked = settings.HddStreaming };
                hddStreaming.CheckedChanged += delegate {
                    settings.HddStreaming = hddStreaming.Checked;
                    try { settings.Save(); }
                    catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
                };
                storage.Controls.Add(hddStreaming);
                storage.Controls.Add(Hint("For game data on a hard drive. May help sequential loads; restart required."));
                shaders = Section(shadersPage, "Compiling");
                AddOption(shaders, "Precompile shaders on the splash screen", "GPU", "precompile_shaders", true);
                var shaderObjects = AddOption(shaders, "Shader objects (experimental)", "GPU", "shader_objects", false);
                shaderObjectTip.SetToolTip(shaderObjects, "Experimental: may cause rendering issues. Unavailable on AMD GPUs.");
                // FEAT-GTS-445 (from RyGT7's PERF-GT7-111/117 and PERF-GT7-108; RyGT-Team/rygt7#3): shader
                // pre-translation (SHADGT_MANIFEST_RECORD / SHADGT_MANIFEST_PRETRANSLATE) and the compile pack
                // (SHADGT_COMPILE_PACK). FEAT-GTS-285: pre-translation is on unless unchecked (the
                // renderer's default since shadGT FEAT-GTS-478); the compile pack is off unless checked.
                AddOption(shaders, "Rebuild translated shaders in the background after a renderer update",
                          "GPU", "shader_pretranslate", true);
                AddOption(shaders, "Compile the shaders already seen on this PC at start, after a driver update (needs shader objects; may pause races)",
                          "GPU", "compile_pack", false);
            }
            else
            {
                shaders = Section(shadersPage, "Compiling");
                // FEAT-GTS-290: ryGT7 had these two on its Graphics page; the same GPU settings, both off unless
                // checked. PERF-GT7-111/117: shader pre-translation (RYGT_MANIFEST_RECORD / RYGT_MANIFEST_PRETRANSLATE).
                AddOption(shaders, "Rebuild translated shaders in the background after a renderer update (about 30 MB a track)", "GPU", "shader_pretranslate", false);
                // PERF-GT7-108: the compile pack (RYGT_COMPILE_PACK).
                AddOption(shaders, "Compile the shaders already seen on this PC at start, after a driver update (background, pauses in races)", "GPU", "compile_pack", false);
            }
            // What the game does while a new shader compiles during play (GPU.compile_mode): GT Sport
            // slows down by default, GT7 keeps going.
            var mode = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            mode.Items.AddRange(new object[] { "Pause the game", "Slow motion", "Keep going (objects pop in)" });
            mode.SelectedIndex = Math.Max(0, Math.Min(2, ReadIntSetting("GPU", "compile_mode", gts ? 1 : 2)));
            shaders.Controls.Add(new SettingRow("When new shaders compile", mode));
            // How long one pause may go on compiling the shaders of what comes next: longer pauses,
            // fewer of them (GPU.compile_batch_ms).
            var batch = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            var batchValues = new[] { 0, 100, 300, 1000 };
            batch.Items.AddRange(new object[] { "Off (only what is needed)", "Short (up to 0.1 s)", "Medium (up to 0.3 s)", "Long (up to 1 s)" });
            int current = ReadIntSetting("GPU", "compile_batch_ms", 0);
            int index = Array.IndexOf(batchValues, current);
            batch.SelectedIndex = index >= 0 ? index : 0;
            batch.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "compile_batch_ms", batchValues[batch.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            shaders.Controls.Add(new SettingRow("Compile batching (when pausing)", batch));
            batch.Enabled = mode.SelectedIndex == 0;
            mode.SelectedIndexChanged += delegate
            {
                batch.Enabled = mode.SelectedIndex == 0;
                try { WriteSetting("GPU", "compile_mode", mode.SelectedIndex); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
        }

        /// About: what the launcher is, the game's folders, updates, logging and the Advanced page's switch.
        void BuildAboutPage(bool ready)
        {
            bool gt7 = target == GameKind.GT7;
            var aboutPage = NewPage("About", "About", gt7
                ? "RyGT Launcher " + LauncherVersion.Value + " " + (char)183 + " Gran Turismo 7 (CUSA24769)"
                : "RyGT Launcher " + LauncherVersion.Value + " " + (char)183 + " Gran Turismo Sport (CUSA03220) " + (char)183 + " update 1.69");
            aboutPage.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(1, 0, 0, 6),
                // FEAT-GTS-273: the copyright and trademark notice.
                Text = gt7
                    // FIX-RT-009: the same notice as GT Sport's, with the RyGT-Team copyright.
                    ? "RyGT converts your own copy of Gran Turismo 7 to run on Windows. You need your own copy of the game." +
                      Environment.NewLine + Environment.NewLine +
                      "Copyright © 2026 RyGT-Team." + Environment.NewLine +
                      "Gran Turismo is a trademark of Sony Interactive Entertainment. " +
                      "This project is independent of Sony and Polyphony Digital." + Environment.NewLine +
                      // FEAT-GTS-269: the attribution the NVIDIA RTX SDKs License asks for (the DLSS plugin).
                      "This software uses NVIDIA DLSS. NVIDIA, the NVIDIA logo and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and other countries."
                    : "RyGT converts your own copy of Gran Turismo Sport to run on Windows. You need your own copy of the game." +
                      Environment.NewLine + Environment.NewLine +
                      "Copyright © 2026 RyGT-Team." + Environment.NewLine +
                      "Gran Turismo is a trademark of Sony Interactive Entertainment. " +
                      "This project is independent of Sony and Polyphony Digital." + Environment.NewLine +
                      // FEAT-GTS-287: the attribution the NVIDIA RTX SDKs License asks for (the DLSS plugin).
                      "This software uses NVIDIA DLSS. NVIDIA, the NVIDIA logo and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and other countries."
            });
            if (!ready)
            {
                aboutPage.Controls.Add(Hint(info.Name + " has no folder yet: pick it on the Game page."));
                return;
            }
            var folders = Section(aboutPage, "Folders");
            folders.Controls.Add(OpenRow("The " + info.Product + " folder", OpenFolder, "Open folder"));
            folders.Controls.Add(OpenRow("Logs (stdout.log, stderr.log, setup.log)", delegate { OpenSubfolder(Path.Combine("logs", info.TitleId)); }, "Open log folder"));
            folders.Controls.Add(OpenRow("Shaders", delegate { OpenSubfolder("shaders"); }, "Open folder"));
            if (gt7) folders.Controls.Add(OpenRow("Photos (exported from the game's gallery)", delegate { OpenSubfolder("Photos"); }, "Open folder"));

            // ---- About > Updates (FEAT-GTS-272): the check at start, the channel, Check now and Roll back
            var updates = Section(aboutPage, "Updates");
            AddUpdateSettings(updates);

            // ---- About > Logging, and whether the Advanced page shows
            var logging = Section(aboutPage, "Logging");
            var logLevel = new DarkComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            logLevel.Items.AddRange(LogFiles.Levels);
            logLevel.SelectedIndex = LogFiles.LevelIndex(settings.StdoutLevel);
            logLevel.SelectedIndexChanged += delegate
            {
                if (logLevel.SelectedIndex < 0) return;
                settings.StdoutLevel = LogFiles.Levels[logLevel.SelectedIndex];
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the logging level: " + e.Message); }
            };
            logging.Controls.Add(new SettingRow("stdout.log minimum level", logLevel));
            logging.Controls.Add(Hint("The selected level and below, from the next launch. All logs are in the logs folder; previous logs are kept with a timestamp."));
            // FEAT-GTS-438: the Advanced page (experimental renderer switches) only for those who want it.
            var advancedShown = new ToggleRow { Text = "Show the Advanced page (experimental renderer switches)", Checked = settings.ShowAdvanced };
            advancedShown.CheckedChanged += delegate
            {
                settings.ShowAdvanced = advancedShown.Checked;
                if (showAdvanced != null) showAdvanced(advancedShown.Checked);
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            logging.Controls.Add(advancedShown);
            logging.Controls.Add(Hint("Hiding it keeps the switches already set there; they still apply when the game starts."));
        }

        /// FEAT-GTS-437: Advanced, the experimental renderer and runtime switches (RYGT_*, SHADGT_* and
        /// SHARED_* environment variables) with a description on hover, and any other variable for the
        /// game's exe. The same page for both games, reading the game's own libs\*.prx.
        void BuildAdvancedPage()
        {
            var advancedHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 24) };
            var advancedPage = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            advancedHost.Controls.Add(advancedPage);
            pagesHost.Controls.Add(advancedHost);
            pages["Advanced"] = advancedHost;
            // FEAT-GTS-271: built the first time the page is shown: the walk reads every libs\*.prx (the
            // renderer and the runtime modules), which most players, with the page hidden, never need.
            bool advancedBuilt = false;
            var pageSettings = settings;
            string pageRoot = root;
            var pageGame = target;
            advancedHost.VisibleChanged += delegate
            {
                if (advancedBuilt || !advancedHost.Visible) return;
                advancedBuilt = true;
                SwitchWalker.Game = pageGame;
                CustomEnvironment.ExeName = GameInfo.Of(pageGame).ExeName;
                advancedPage.SuspendLayout();
                AdvancedPage.Build(advancedPage,
                    PageTitle("Advanced", "Experimental renderer switches for testing and diagnosis. They can break rendering, slow the game " +
                                          "or write large logs; hover a switch for what it does. Saved as you change them; used the next time the game starts."),
                    Dark.Card, pageRoot, pageSettings.Env, list =>
                    {
                        pageSettings.Env = list;
                        try { pageSettings.Save(); }
                        catch (Exception e) { MessageBox.Show(this, "Could not save the switches: " + e.Message); }
                    });
                Dark.Apply(advancedPage);
                advancedPage.ResumeLayout(true);
            };
            showAdvanced = delegate (bool on)
            {
                if (on) hiddenPages.Remove("Advanced"); else hiddenPages.Add("Advanced");
                pageItems["Advanced"].Visible = on;
                PlaceSidebarItems();
                if (!on && lastPage == "Advanced") ShowSettings("About", false); // hidden while open: back to About
            };
            showAdvanced(settings.ShowAdvanced);
        }

        /// Shows the settings at one of their pages (Game, Display, Graphics, Shaders, Controls, Advanced, About;
        /// GT7 also Account and Online).
        public void ShowSettings(string page, bool animate = true)
        {
            if (pagesStale) BuildPages();
            if (!pages.ContainsKey(page) || hiddenPages.Contains(page)) page = "Game";
            lastPage = page;
            foreach (var pair in pages) pair.Value.Visible = pair.Key == page;
            foreach (var pair in pageItems) pair.Value.Active = pair.Key == page;
            if (settingsView.Visible) return;
            Fade(home, delegate { home.Visible = false; settingsView.Visible = true; }, settingsView, animate);
        }

        void ClearUpdate()
        {
            pendingUpdate = null;
            home.Notice = "";
            updateButton.Visible = false;
            changelogButton.Visible = false;
            home.PerformLayout();
            home.Invalidate(true);
        }

        /// Asks for the newest build for the selected game's folder in the background; never holds up Play.
        void CheckForUpdate(bool manual)
        {
            if (!RootReady(target))
            {
                if (manual) MessageBox.Show(this, "Pick the " + info.Product + " folder on the Game page first.");
                return;
            }
            CheckForUpdateOfFolder(manual);
        }


        /// FEAT-GTS-272: the update channel (Launcher.update_channel): stable (the newest release), prerelease
        /// (the newest release or pre-release) or nightly (the newest build of development). Stable by default
        /// (FIX-GTS-286: RyGTS publishes releases on GitHub now; it was nightly while there were none).
        string UpdateChannel()
        {
            try
            {
                object section, value;
                var launcher = LoadConfig().TryGetValue("Launcher", out section) ? section as Dictionary<string, object> : null;
                string channel = launcher != null && launcher.TryGetValue("update_channel", out value) ? value as string : null;
                return Array.IndexOf(Updater.Channels, channel) >= 0 ? channel : Updater.Stable;
            }
            catch { return Updater.Stable; }
        }


        static string ChannelName(string channel)
        {
            return channel == Updater.Nightly ? "nightly" : channel == Updater.Prerelease ? "release or prerelease" : "stable release";
        }


        /// Asks GitHub for the newest RyGTS build on the update channel in the background, for the selected
        /// game's folder (FEAT-GTS-291: one updater for both games); never holds up Play.
        void CheckForUpdateOfFolder(bool manual)
        {
            string installed = Updater.InstalledVersion(root);
            if (installed == null)
            {
                if (manual) MessageBox.Show(this, "The " + info.Product + " folder didn't come from a RyGT package (it has no manifest.json or BUILD_INFO.txt), " +
                                           "so the launcher can't update it.");
                return;
            }
            string channel = UpdateChannel();
            GameKind game = target;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseInfo latest = null;
                Exception error = null;
                try { latest = Updater.Latest(channel); }
                catch (Exception e) { error = e; }
                try { BeginInvoke((Action)delegate { ShowUpdate(game, installed, channel, latest, error, manual); }); }
                catch (InvalidOperationException) { } // closed meanwhile
            });
        }


        void ShowUpdate(GameKind game, string installed, string channel, ReleaseInfo latest, Exception error, bool manual)
        {
            if (target != game) return; // FEAT-GTS-290: the game changed meanwhile
            if (channel != UpdateChannel()) return; // the channel changed meanwhile; that check shows its own
            if (latest != null && Updater.IsNewer(latest, installed, root)) // FIX-GTS-286: not the same build again
            {
                pendingUpdate = latest;
                home.Notice = (latest.Artifact ? "A newer RyGT nightly is available: " + Updater.Display(latest.Version)
                                               : "RyGT " + latest.Version + (latest.Prerelease ? " (prerelease)" : "") + " is available") +
                              " (you have " + Updater.Display(installed) + ")." +
                              (latest.Changes().Count > 0 ? "" : Environment.NewLine + latest.Summary(240)); // the notes are behind Changelog
                updateButton.Text = "Update"; // the notice names the version; the button had no room for it
                updateButton.Visible = true;
                changelogButton.Visible = latest.Changes().Count > 0;
                home.PerformLayout();
                home.Invalidate(true);
            }
            else if (manual)
            {
                MessageBox.Show(this, error != null ? "Could not check for updates: " + error.Message
                                    : latest == null && channel == Updater.Nightly ? "No nightly to update to was found (nightlies are kept for 7 days)."
                                    : latest == null ? "No RyGT " + ChannelName(channel) + " was found on GitHub."
                                    : "RyGT " + Updater.Display(installed) + " is the newest " + ChannelName(channel) + ".");
            }
        }


        /// Downloads and installs the pending update, then restarts the launcher.
        void InstallUpdate()
        {
            var release = pendingUpdate;
            if (release == null || busy) return;
            if (MessageBox.Show(this, "Update RyGT to " + Updater.Display(release.Version) + (release.Prerelease ? " (prerelease)" : "") + "?" +
                                Environment.NewLine + Environment.NewLine +
                                "The launcher restarts when it is done. Your game, saves and settings are kept, and you can roll back " +
                                "from Settings > About.", "Update RyGT", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            busy = true;
            updateButton.Enabled = false;
            play.Enabled = false;
            string last = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try
                {
                    Updater.Install(root, info.TitleId, release, (text, fraction) =>
                    {
                        if (text == last) return;
                        last = text;
                        BeginInvoke((Action)delegate { status.Text = text; });
                    });
                }
                catch (Exception e) { error = e; }
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    if (error == null) { Updater.Restart(root); return; }
                    updateButton.Enabled = true;
                    RefreshState();
                    status.Text = "The update failed: " + error.Message;
                    MessageBox.Show(this, "The update failed, and the launcher's files are as they were." + Environment.NewLine + error.Message);
                });
            });
        }


        /// Updates (About, both games): the check at start, the channel, a check now, and a roll back of the
        /// selected game's folder's last update.
        void AddUpdateSettings(TableLayoutPanel panel)
        {
            AddOption(panel, "Check for updates at start", "Launcher", "check_updates", true);
            var channelRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var channelLabel = new Label { Text = "Update channel", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var channelBox = new DarkComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            channelBox.Items.AddRange(new object[] { "Stable", "Prerelease", "Nightly" });
            channelBox.SelectedIndex = Array.IndexOf(Updater.Channels, UpdateChannel());
            channelBox.SelectedIndexChanged += delegate
            {
                try { WriteSetting("Launcher", "update_channel", Updater.Channels[channelBox.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); return; }
                // What was found on the old channel no longer applies.
                ClearUpdate();
                if (ReadSetting("Launcher", "check_updates", true)) CheckForUpdate(false);
            };
            channelRow.Controls.Add(channelLabel);
            channelRow.Controls.Add(channelBox);
            panel.Controls.Add(channelRow);
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            string installed = Updater.InstalledVersion(root);
            var label = new Label
            {
                Text = "Launcher " + LauncherVersion.Value + (installed == null ? "" : " (" + Updater.Display(installed) + ")"),
                AutoSize = true, MinimumSize = new Size(196, 20), BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) // FIX-GTS-286: not cut off
            };
            var check = new Button { Text = "Check now", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            check.Click += delegate { CheckForUpdate(true); };
            var last = Updater.LastUpdate(root);
            var rollBack = new Button { Text = last == null ? "Roll back" : "Roll back to " + Updater.Display(last.Item2), AutoSize = true, Enabled = last != null, Margin = new Padding(0) };
            rollBack.Click += delegate
            {
                if (busy) return;
                if (MessageBox.Show(this, "Put back RyGT " + Updater.Display(last.Item2) + " (as before the update to " + Updater.Display(last.Item3) +
                                    ") and restart the launcher?", "Roll back", MessageBoxButtons.OKCancel) != DialogResult.OK)
                    return;
                try { Updater.RollBack(root); Updater.Restart(root); }
                catch (Exception e) { MessageBox.Show(this, "Could not roll back: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(check);
            row.Controls.Add(rollBack);
            panel.Controls.Add(row);
            panel.Controls.Add(Hint("Stable is the newest release on GitHub, Prerelease the newest release or prerelease, and Nightly " +
                                    "the newest build of development (downloaded through nightly.link; nightlies are kept for 7 days). " +
                                    "No GitHub account is needed. Your game, saves, settings and caches are never touched."));
        }


        void RefreshState()
        {
            if (!RootReady(target))
            {
                play.Enabled = false;
                if (setup != null) setup.Enabled = false;
                if (addDlss != null) addDlss.Enabled = false;
                if (downloadDlss != null) downloadDlss.Enabled = false;
                if (!busy) status.Text = info.Name + " has no folder yet: open the settings (the gear) and pick the " + info.Product + " folder on the Game page.";
                return;
            }
            if (target == GameKind.GT7) RefreshStateGt7(); else RefreshStateGts();
        }

        void Play()
        {
            MigrateApp0();
            if (target == GameKind.GT7) PlayGt7(); else PlayGts();
        }

        void RunSetup()
        {
            MigrateApp0();
            if (target == GameKind.GT7) RunSetupGt7(); else RunSetupGts();
        }

        /// FEAT-GTS-291: finishes a move of app0 into app0\<serial> that was cut short, and moves app0 there
        /// when the game's executable does not load app0\sce_module any more (App0Layout.Migrate). Set up
        /// moves it itself before it writes the new modules. Each step goes to the game's setup log.
        void MigrateApp0()
        {
            if (!RootReady(target)) return;
            MigrateSharedNames(target); // FEAT-GTS-291: the settings file, convert, temp0 and data too
            try { foreach (string note in App0Layout.Migrate(root, info, false)) Log(note); }
            catch (Exception e) { Log("app0: " + e.Message); }
        }

        void StartSetup()
        {
            CommitPaths();
            if (!RootReady(target)) { MessageBox.Show(this, "Pick the " + info.Product + " folder on the Game page first."); return; }
            try { setupLogPath = LogFiles.Prepare(root, "setup.log", info.TitleId); }
            catch (Exception e) { MessageBox.Show(this, "Could not prepare setup log: " + e.Message); return; }
            var game = target;
            busy = true;
            RefreshState();
            if (log != null) log.Clear();
            var thread = new Thread(delegate ()
            {
                bool ok = false;
                Exception failure = null;
                try { RunSetup(); ok = true; }
                catch (Exception e) { failure = e; Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (ok) status.Text = "Set up finished. Press Play.";
                    else
                    {
                        status.Text = "Set up failed; see the log.";
                        if (game == GameKind.GT7) { if (failure is DumpFileException) ShowRedumpHint((DumpFileException)failure); }
                        else ShowSetupDumpHelp(failure);
                    }
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// FIX-RT-008: the folders holding the selected game's launcher pictures, first its own one,
        /// launcher\<serial>, then launcher itself when those pictures are this game's. launcher\background.png
        /// and logo.png are GT Sport's (the RyGT package ships GT Sport's), except in a folder with only
        /// GT7.exe (a ryGT7 release). FIX-RT-016: also before Set up, when the package's folder has no
        /// game exe yet (FIX-RT-008 gave them to GT7 there, so its home view stayed red).
        List<string> PictureFolders()
        {
            var folders = new List<string> { Path.Combine(root, "launcher", info.TitleId) };
            bool gts = File.Exists(Path.Combine(root, GameInfo.GTSport.ExeName)), gt7 = File.Exists(Path.Combine(root, GameInfo.GT7.ExeName));
            if (gt7 && !gts ? target == GameKind.GT7 : target == GameKind.GTSport) folders.Add(Path.Combine(root, "launcher"));
            return folders;
        }

        /// The pictures behind the home view: launcher\<serial>\background*.png or .jpg, else
        /// launcher\background* when they are this game's (PictureFolders; not the .bak ones), else the
        /// picture built into the launcher, else the game's own pic1.png.
        List<Image> LoadBackgrounds()
        {
            var found = new List<Image>();
            if (!RootReady(target))
            {
                // FEAT-GTS-290: before a folder is picked, the game's own picture built into RyGTLauncher.exe.
                var builtIn = EmbeddedPicture(target == GameKind.GT7 ? "gt7-background.png" : "gts-background.png");
                if (builtIn != null) found.Add(builtIn);
                return found;
            }
            foreach (string folder in PictureFolders())
            try
            {
                if (found.Count == 0 && Directory.Exists(folder))
                {
                    var paths = Directory.GetFiles(folder, "background*.*")
                        .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        .Where(p => Path.GetFileName(p).IndexOf("bak", StringComparison.OrdinalIgnoreCase) < 0)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths)
                    {
                        var picture = LoadPicture(path);
                        if (picture != null) found.Add(picture);
                    }
                }
            }
            catch { }
            if (found.Count == 0)
            {
                // FEAT-GTS-290: the game's launcher picture built into RyGTLauncher.exe (ryGT7's for GT7), then
                // the game's own pic1.png.
                var builtIn = EmbeddedPicture(target == GameKind.GT7 ? "gt7-background.png" : "gts-background.png");
                if (builtIn != null) found.Add(builtIn);
            }
            if (found.Count == 0)
            {
                string path = GameFile("sce_sys\\pic1.png");
                var picture = path != null ? LoadPicture(path) : null;
                if (picture != null) found.Add(picture);
            }
            return found;
        }

        /// A picture built into RyGTLauncher.exe (build.cmd's /resource), or null.
        static Image EmbeddedPicture(string name)
        {
            try
            {
                using (var stream = typeof(LoaderForm).Assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    copy.Position = 0;
                    return Image.FromStream(copy);
                }
            }
            catch { return null; }
        }

        string Exe { get { return Path.Combine(root, info.ExeName); } }

        static readonly string[] FirmwareModules = { "libSceFont", "libSceFontFt", "libSceFreeTypeOt", "libSceJson2", "libSceNgs2", "libSceBluetoothHid" };

        // Used when present: the JPEG decoder (Scapes thumbnails and backgrounds) and PNG encoder (photos).
        // Both are plain software modules needing only libkernel and libSceLibcInternal.
        // Other firmware modules GT Sport imports stay RyGTS's stubs in libs: some, such as
        // libSceOttvCapture, need modules nothing provides and make GTSport.exe fail at startup.
        static readonly string[] OptionalFirmwareModules = { "libSceJpegDec", "libScePngEnc" };


        /// The sidebar's page list, top to bottom, leaving out hidden pages (Advanced when it is off).
        void PlaceSidebarItems()
        {
            int y = 150;
            foreach (var name in pageNames)
            {
                var item = pageItems[name];
                item.Location = new Point(0, y);
                if (!hiddenPages.Contains(name)) y += 40;
            }
        }


        /// Shows the home view (the picture, Play).
        public void ShowHome(bool animate = true)
        {
            if (home.Visible) return;
            Fade(settingsView, delegate { settingsView.Visible = false; home.Visible = true; }, home, animate);
        }


        /// For --snapshot: 0 home, 1 the settings (Display), 2 the controls.
        public void ShowPage(int index)
        {
            if (index == 0) ShowHome(false);
            else ShowSettings(index == 2 ? "Controls" : "Display", false);
        }


        bool fading;


        /// Changes views behind the curtain: the old view darkens (120 ms), then the new one
        /// comes up out of the dark (220 ms).
        void Fade(Control from, Action change, Control to, bool animate)
        {
            if (!animate || fading || !IsHandleCreated || !Visible || !from.Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                change();
                return;
            }
            fading = true;
            var before = new Bitmap(ClientSize.Width, ClientSize.Height);
            from.DrawToBitmap(before, new Rectangle(Point.Empty, from.Size));
            curtain.Show(before, 0);
            curtain.Visible = true;
            curtain.BringToFront();
            change();
            Bitmap after = null;
            int start = Environment.TickCount;
            const int Out = 120, In = 220;
            Anim.Run(() =>
            {
                int t = Environment.TickCount - start;
                if (t < Out)
                {
                    curtain.Show(before, t / (float)Out);
                    return true;
                }
                if (after == null)
                {
                    after = new Bitmap(ClientSize.Width, ClientSize.Height);
                    to.DrawToBitmap(after, new Rectangle(Point.Empty, to.Size));
                }
                float k = Anim.Ease((t - Out) / (float)In);
                curtain.Show(after, 1 - k);
                if (k < 1) return true;
                curtain.Visible = false;
                curtain.Show(null, 0);
                before.Dispose();
                after.Dispose();
                fading = false;
                return false;
            });
        }


        /// A settings page: a scrolling column with its title and the line under it.
        TableLayoutPanel NewPage(string name, string title, string subtitle)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 30) };
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.Controls.Add(PageTitle(title, subtitle));
            page.Controls.Add(content);
            pagesHost.Controls.Add(page);
            pages[name] = page;
            return content;
        }


        /// A page's large title and the grey line under it.
        static Control PageTitle(string title, string subtitle)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Light", 22f), Margin = new Padding(0, 0, 0, 2) });
            panel.Controls.Add(new Label { Text = subtitle, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(2, 0, 0, 0) });
            return panel;
        }


        /// A group of settings under a small spaced capital heading with a line under it.
        TableLayoutPanel Section(TableLayoutPanel parent, string heading)
        {
            var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 22, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var label = new Label
            {
                Text = Dark.Spaced(heading.ToUpperInvariant()), AutoSize = false, Dock = DockStyle.Fill, Height = 26, Font = Dark.Caps(8.5f),
                ForeColor = Dark.Heading, Tag = "heading", Margin = new Padding(0), TextAlign = ContentAlignment.TopLeft
            };
            label.Paint += (sender, e) => { using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, label.Height - 1, label.Width, label.Height - 1); };
            panel.Controls.Add(label);
            parent.Controls.Add(panel);
            return panel;
        }


        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 8, 0, 0) };
        }


        /// A row with a button that opens a window or a folder.
        static SettingRow OpenRow(string text, Action action, string button = "Open")
        {
            var open = new Button { Text = button, AutoSize = true };
            open.Click += delegate { action(); };
            return new SettingRow(text, open);
        }


        /// The label + drop-down rows the setting helpers build (in FlowLayoutPanels) become
        /// SettingRows, in the same place.
        static void Rowify(Control root)
        {
            foreach (var child in root.Controls.Cast<Control>().ToList())
            {
                var flow = child as FlowLayoutPanel;
                if (flow != null && flow.Controls.Count == 2 && flow.Controls[0] is Label && flow.Controls[1] is ListControl)
                {
                    var text = flow.Controls[0].Text;
                    var control = flow.Controls[1];
                    control.Width = Math.Max(control.Width, 260);
                    int index = root.Controls.GetChildIndex(flow);
                    flow.Controls.Clear();
                    root.Controls.Remove(flow);
                    var row = new SettingRow(text, control);
                    root.Controls.Add(row);
                    root.Controls.SetChildIndex(row, index);
                    continue;
                }
                Rowify(child);
            }
        }


        /// The logo: launcher\<serial>\logo.png, or launcher\logo.png when it is this game's
        /// (PictureFolders), else the one built into the launcher.
        Image LoadLogo()
        {
            if (RootReady(target))
                foreach (string folder in PictureFolders())
                {
                    string path = Path.Combine(folder, "logo.png");
                    try { if (File.Exists(path)) return Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); }
                    catch { }
                }
            // FEAT-GTS-290: the game's RY logo built into RyGTLauncher.exe (RY GTS, RY GT7).
            return EmbeddedPicture(target == GameKind.GT7 ? "gt7-logo.png" : "gts-logo.png");
        }


        void OpenSubfolder(string name)
        {
            string path = Path.Combine(root, name);
            try { Directory.CreateDirectory(path); } catch { }
            Process.Start("explorer.exe", "\"" + path + "\"");
        }


        void OpenPorts()
        {
            using (var form = new PortsForm(ReadStringSetting("Input", "controller_ports", ""),
                                            ReadStringSetting("General", "player_names", "").Split(','),
                                            (ports, names) =>
                                            {
                                                WriteSetting("Input", "controller_ports", ports);
                                                // Read by the game's user service (libSceUserService).
                                                WriteSetting("General", "player_names",
                                                             string.Join(",", names.Select(n => n.Replace(",", ""))));
                                            }))
            {
                form.ShowDialog(this);
            }
        }


        void OpenFolder()
        {
            Process.Start("explorer.exe", "\"" + root + "\"");
        }


        // ---- The pictures behind the home view

        readonly List<Image> pictures;


        static Image LoadPicture(string path)
        {
            try
            {
                var picture = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                if (picture.Width <= 2560) return picture;
                // A window never needs more; a 4K picture would hold 33 MB of memory.
                using (picture)
                    return new Bitmap(picture, 2560, picture.Height * 2560 / picture.Width);
            }
            catch { return null; }
        }


        /// The home view's picture (for --snapshot and the controls over it).
        public bool DrawBackdrop(Control control, Graphics g, bool raw)
        {
            return home.Visible && home.DrawBackdrop(control, g, raw);
        }


        /// A file setting: its name, the folder in a field, and a Browse button.
        TextBox AddRow(TableLayoutPanel section, string label, string value, Action browsed = null)
        {
            var inner = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var field = new Card(Dark.Field) { AutoSize = false, Size = new Size(520, 30), Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 7, 10, 4), Margin = new Padding(0, 0, 8, 0) };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Margin = new Padding(0) };
            field.Controls.Add(box);
            inner.Controls.Add(field, 0, 0);
            var browse = new Button { Text = "Browse", AutoSize = true, Margin = new Padding(0) };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = label, SelectedPath = box.Text })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { box.Text = dialog.SelectedPath; if (browsed != null) browsed(); }
                }
            };
            inner.Controls.Add(browse, 1, 0);
            section.Controls.Add(new SettingRow(label, inner));
            return box;
        }


        // GAME-004: the selected game's own serial, CUSA03220.json for GT Sport and CUSA24769.json for GT7.
        string ConfigPath { get { return info.ConfigPath(root); } }


        /// A shadGT setting in a section of the GT Sport config (General: show_splash,
        /// show_fps_counter, discord_rpc_enabled; GPU: full_screen, particle_effects, fsr_enabled, fsr3_enabled, rcas_enabled, dlss_enabled,
        /// framegen_enabled, precompile_shaders, compile_freeze).
        bool ReadSetting(string sectionName, string key, bool fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is bool) return (bool)value;
            }
            catch { }
            return fallback;
        }


        int ReadIntSetting(string sectionName, string key, int fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is int) return (int)value;
            }
            catch { }
            return fallback;
        }


        void WriteSetting(string sectionName, string key, object on)
        {
            var config = LoadConfig();
            object found;
            var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
            if (section == null) { section = new Dictionary<string, object>(); config[sectionName] = section; }
            section[key] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config),
                              new UTF8Encoding(false));
        }


        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }


        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);


        static int InstalledRamGb()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status)) return 32;
            return (int)Math.Round(status.TotalPhys / (1024.0 * 1024 * 1024));
        }


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }


        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);


        class Descending<T> : IComparer<T> where T : IComparable<T>
        {
            public int Compare(T a, T b) { return b.CompareTo(a); }
        }


        /// The primary display's modes (resolution, largest first -> refresh rates in Hz, highest
        /// first) and its current mode.
        static SortedDictionary<long, SortedSet<int>> DisplayModes(out int currentWidth, out int currentHeight, out int currentHz)
        {
            var modes = new SortedDictionary<long, SortedSet<int>>(new Descending<long>());
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            for (int i = 0; EnumDisplaySettings(null, i, ref mode); ++i)
            {
                if (mode.BitsPerPel < 24 || mode.PelsWidth < 1280 || mode.PelsHeight < 720) continue;
                long key = ((long)mode.PelsWidth << 32) | mode.PelsHeight;
                SortedSet<int> rates;
                if (!modes.TryGetValue(key, out rates)) modes[key] = rates = new SortedSet<int>(new Descending<int>());
                if (mode.DisplayFrequency > 1) rates.Add((int)mode.DisplayFrequency);
            }
            currentWidth = currentHeight = currentHz = 0;
            if (EnumDisplaySettings(null, -1, ref mode))
            {
                currentWidth = (int)mode.PelsWidth;
                currentHeight = (int)mode.PelsHeight;
                currentHz = (int)mode.DisplayFrequency;
            }
            return modes;
        }


        string ReadStringSetting(string sectionName, string key, string fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is string) return (string)value;
            }
            catch { }
            return fallback;
        }


        static string ModeText(long key)
        {
            return string.Format("{0} x {1}", key >> 32, key & 0xffffffff);
        }


        // Screen: windowed, borderless fullscreen (the desktop's resolution and refresh rate), or
        // exclusive fullscreen at a chosen resolution and refresh rate (GPU.full_screen,
        // full_screen_mode, window_width/height, fullscreen_refresh_rate). The game draws at
        // 1920x1080 and runs at up to 60 fps; FSR or DLSS scale it to the screen, and a higher
        // refresh rate shows frame generation's extra frames.
        void AddScreenSettings(TableLayoutPanel panel)
        {
            int desktopWidth, desktopHeight, desktopHz;
            var modes = DisplayModes(out desktopWidth, out desktopHeight, out desktopHz);

            var screenRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var screen = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            screen.Items.AddRange(new object[] { "Windowed", "Borderless fullscreen", "Exclusive fullscreen" });
            bool full = ReadSetting("GPU", "full_screen", false);
            bool exclusive = ReadStringSetting("GPU", "full_screen_mode", "Windowed") == "Fullscreen";
            screen.SelectedIndex = !full ? 0 : exclusive ? 2 : 1;
            screenRow.Controls.Add(new Label { Text = "Screen", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            screenRow.Controls.Add(screen);
            panel.Controls.Add(screenRow);

            var resolutionRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var resolution = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var keys = modes.Keys.ToList();
            int width = ReadIntSetting("GPU", "window_width", 0), height = ReadIntSetting("GPU", "window_height", 0);
            if (width < 640 || height < 360)
            {
                width = desktopWidth > 0 ? Math.Min(1920, desktopWidth) : 1920;
                height = desktopHeight > 0 ? Math.Min(1080, desktopHeight) : 1080;
            }
            long wanted = ((long)width << 32) | (uint)height;
            if (!keys.Contains(wanted)) keys.Add(wanted);
            foreach (var key in keys) resolution.Items.Add(ModeText(key));
            resolution.SelectedIndex = keys.IndexOf(wanted);
            resolutionRow.Controls.Add(new Label { Text = "Resolution", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            resolutionRow.Controls.Add(resolution);
            panel.Controls.Add(resolutionRow);

            var refreshRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var refresh = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            refreshRow.Controls.Add(new Label { Text = "Refresh rate", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            refreshRow.Controls.Add(refresh);
            panel.Controls.Add(refreshRow);

            bool loading = true;
            Action<int> fillRates = delegate (int wantedHz)
            {
                refresh.Items.Clear();
                refresh.Items.Add(desktopHz > 0 ? string.Format("Desktop ({0} Hz)", desktopHz) : "Desktop");
                SortedSet<int> rates;
                if (modes.TryGetValue(keys[resolution.SelectedIndex], out rates))
                {
                    foreach (int hz in rates) refresh.Items.Add(hz + " Hz");
                }
                int index = refresh.Items.IndexOf(wantedHz + " Hz");
                refresh.SelectedIndex = wantedHz > 0 && index > 0 ? index : 0;
            };
            Action save = delegate
            {
                if (loading) return;
                try
                {
                    WriteSetting("GPU", "full_screen", screen.SelectedIndex != 0);
                    WriteSetting("GPU", "full_screen_mode", screen.SelectedIndex == 2 ? "Fullscreen" : "Borderless");
                    long key = keys[resolution.SelectedIndex];
                    WriteSetting("GPU", "window_width", (int)(key >> 32));
                    WriteSetting("GPU", "window_height", (int)(key & 0xffffffff));
                    WriteSetting("GPU", "fullscreen_refresh_rate",
                                 refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0);
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action enable = delegate
            {
                // Borderless fullscreen always uses the desktop's mode.
                resolution.Enabled = screen.SelectedIndex != 1;
                refresh.Enabled = screen.SelectedIndex == 2;
            };
            // FEAT-GTS-290: GT7's Ultrawide row (GT Sport has its Screen shape row instead).
            if (target == GameKind.GT7)
            {
                // Ultrawide (GPU.aspect_ratio): the camera widens to this shape and the HUD stays at
                // 16:9 in the middle. A preset also picks its resolution (added to the list when the
                // monitor does not report it, e.g. for a borderless window on another PC's screen).
                var wideRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
                var wide = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
                var wideAspects = new List<string> { "16:9", "auto", "2560:1080", "3440:1440", "3840:1600", "5120:1440" };
                var wideSizes = new List<long> { 0, 0, (2560L << 32) | 1080, (3440L << 32) | 1440, (3840L << 32) | 1600, (5120L << 32) | 1440 };
                wide.Items.AddRange(new object[] { "Off (16:9)", "Auto (screen shape)", "21:9 - 2560 x 1080", "21:9 - 3440 x 1440", "24:10 - 3840 x 1600", "32:9 - 5120 x 1440" });
                string storedAspect = ReadStringSetting("GPU", "aspect_ratio", "16:9");
                int wideIndex = wideAspects.IndexOf(storedAspect);
                if (wideIndex < 0)
                {
                    wideAspects.Add(storedAspect);
                    wideSizes.Add(0);
                    wide.Items.Add("Custom (" + storedAspect + ")");
                    wideIndex = wideAspects.Count - 1;
                }
                wide.SelectedIndex = wideIndex;
                wideRow.Controls.Add(new Label { Text = "Ultrawide", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
                wideRow.Controls.Add(wide);
                panel.Controls.Add(wideRow);
                wide.SelectedIndexChanged += delegate
                {
                    if (loading) return;
                    try
                    {
                        WriteSetting("GPU", "aspect_ratio", wideAspects[wide.SelectedIndex]);
                        long size = wideSizes[wide.SelectedIndex];
                        if (size != 0)
                        {
                            if (!keys.Contains(size))
                            {
                                keys.Add(size);
                                resolution.Items.Add(ModeText(size));
                            }
                            resolution.SelectedIndex = keys.IndexOf(size); // saves the resolution too
                        }
                    }
                    catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
                };

            }
            fillRates(ReadIntSetting("GPU", "fullscreen_refresh_rate", 0));
            enable();
            screen.SelectedIndexChanged += delegate { enable(); save(); };
            resolution.SelectedIndexChanged += delegate
            {
                if (loading) return;
                loading = true;
                int hz = refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0;
                fillRates(hz);
                loading = false;
                save();
            };
            refresh.SelectedIndexChanged += delegate { save(); };
            loading = false;
        }

        // System memory (GPU.system_ram_gb): how much the game may use for things it can do
        // without. Picked from installed RAM the first time; the 16 GB profile keeps fewer freed GPU
        // images for reuse. GT Sport: both profiles retain the shader store, startup precompilation and
        // persistent pipeline cache. GT7: with 16 GB the driver's pipeline cache file (it grows past
        // 1 GB and is loaded whole, then copied again when saved) is off. Tokyo rain race, committed
        // memory: 32 GB setting ~11 GB average / 13.5 GB peak, 16 GB setting ~8.6 / 9.0 GB.
        void AddMemorySetting(TableLayoutPanel panel)
        {
            bool gts = target == GameKind.GTSport;
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label { Text = "System memory (RAM)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var ram = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            ram.Items.AddRange(new object[] { "16 GB (minimum)", "32 GB+ (recommended)" });
            int stored = ReadIntSetting("GPU", "system_ram_gb", 0);
            int gb = stored > 0 ? stored : InstalledRamGb();
            ram.SelectedIndex = gb >= 24 ? 1 : 0;
            Action apply = delegate
            {
                bool large = ram.SelectedIndex == 1;
                WriteSetting("GPU", "system_ram_gb", large ? 32 : 16);
                WriteSetting("Vulkan", "pipeline_cache_enabled", gts || large);
                WriteSetting("GPU", "image_recycle_mb", large ? 512 : 128);
            };
            if (stored == 0 || (gts && !ReadSetting("Vulkan", "pipeline_cache_enabled", true)))
            {
                try { apply(); } catch { }
            }
            ram.SelectedIndexChanged += delegate
            {
                try { apply(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(ram);
            panel.Controls.Add(row);
        }

        /// "--game gts|gt7" anywhere in the arguments: that game (taken out of the arguments), or null.
        static GameKind? TakeGameArgument(ref string[] args)
        {
            var list = args.ToList();
            int at = list.IndexOf("--game");
            if (at < 0 || at + 1 >= list.Count) return null;
            var game = GameInfo.Parse(list[at + 1]);
            list.RemoveRange(at, 2);
            args = list.ToArray();
            return game;
        }

        static int RunLauncher(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Portable: the tools' work folders (and anything else asking for a temporary folder) go
            // into the RyGTS folder instead of AppData\Local\Temp; the tools delete theirs when done.
            try
            {
                string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable("TMP", temp);
                Environment.SetEnvironmentVariable("TEMP", temp);
            }
            catch { }
            if (args.Length == 2 && args[0] == "--allow-firewall")
            {
                // GT7: started elevated by the Online page's firewall button.
                try { return OnlinePlay.AddFirewallRules(args[1]); }
                catch { return 1; }
            }
            // FEAT-GTS-290: --game gts|gt7 picks the game for --setup and --snapshot (else the one picked last).
            var forced = TakeGameArgument(ref args);
            if (args.Contains("--setup")) return new LoaderForm(forced, null).SetupWithoutWindow();
            if (args.Length == 2 && args[0] == "--prepare")
            {
                // Gives an already converted GTSport.exe the ryGT icon (GT7.exe its game's icon) and the Windows subsystem.
                try
                {
                    string exe = Path.GetFullPath(args[1]);
                    if (string.Equals(Path.GetFileName(exe), GameInfo.GT7.ExeName, StringComparison.OrdinalIgnoreCase)) GameExe.PrepareGt7(exe);
                    else GameExe.Prepare(exe);
                    return 0;
                }
                catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            }
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--snapshot")
            {
                // Renders the window's contents to a PNG (for checking the layout); a third argument
                // picks the view: home, settings or controls, or a settings page (game, display,
                // graphics, shaders, advanced, about; GT7 also account and online), or GT7's Controls
                // page's keyboard or wheel tab.
                using (var form = new LoaderForm(forced, null))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    if (args.Length == 3)
                    {
                        int known = Array.IndexOf(new[] { "home", "settings", "controls" }, args[2]);
                        int tab = Array.IndexOf(new[] { "keyboard", "wheel" }, args[2]);
                        if (known >= 0) form.ShowPage(known);
                        else if (tab >= 0) { form.ShowPage(2); form.ShowControlsTab(tab + 1); }
                        else form.ShowSettings(char.ToUpperInvariant(args[2][0]) + args[2].Substring(1), false);
                    }
                    Application.DoEvents();
                    // The client area only: each part of the window drawn where it sits.
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(image)) g.Clear(Dark.Back);
                        for (int i = form.Controls.Count - 1; i >= 0; --i)
                        {
                            var part = form.Controls[i];
                            if (part.Visible) part.DrawToBitmap(image, part.Bounds);
                        }
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--snapshot-previews")
            {
                // The controller preview's layouts side by side as a PNG, some controls pressed.
                var kinds = new[] { PadKind.DualShock4, PadKind.DualSense, PadKind.Xbox, PadKind.Generic };
                using (var image = new Bitmap(420 * kinds.Length, 290))
                {
                    for (int i = 0; i < kinds.Length; ++i)
                    {
                        using (var preview = new ControllerPreview
                        {
                            Kind = kinds[i], Connected = i != 3, Caption = kinds[i].ToString(), Size = new Size(420, 290),
                            Cross = true, R1 = true, PadLeft = true, LX = -0.8f, RY = 0.6f, L2 = 0.5f, R2 = 1f
                        })
                        using (var tile = new Bitmap(420, 290))
                        {
                            preview.DrawToBitmap(tile, new Rectangle(0, 0, 420, 290));
                            using (var g = Graphics.FromImage(image)) g.DrawImage(tile, 420 * i, 0);
                        }
                    }
                    image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return 0;
            }
            if (args.Length == 2 && (args[0] == "--snapshot-ports" || args[0] == "--snapshot-bindings" || args[0] == "--snapshot-wheel" ||
                                     args[0] == "--snapshot-obsolete"))
            {
                // The Controller ports, Button bindings, Steering wheel or obsolete settings window as a PNG
                // (nothing is saved).
                string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                using (Form form = args[0] == "--snapshot-ports"
                           ? (Form)new PortsForm("", null, (ports, names) => { })
                           : args[0] == "--snapshot-wheel"
                               ? (Form)new WheelForm(root)
                               : args[0] == "--snapshot-obsolete"
                                   ? (Form)new ObsoleteSettingsDialog(ObsoleteSettings.Find(new Dictionary<string, object>
                                         { { "GPU", new Dictionary<string, object> { { "quality_preset", 2 } } } }))
                                   : new ControlsForm(root, 1, player => { }))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    // The wheel window shows live state from its probe; give it a moment to arrive.
                    var settle = DateTime.Now.AddMilliseconds(args[0] == "--snapshot-wheel" ? 2000 : 200);
                    while (DateTime.Now < settle) { Application.DoEvents(); Thread.Sleep(20); }
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(image)) g.Clear(Dark.Back);
                        for (int i = form.Controls.Count - 1; i >= 0; --i)
                        {
                            var part = form.Controls[i];
                            if (part.Visible) part.DrawToBitmap(image, part.Bounds);
                        }
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            Application.Run(new LoaderForm());
            return 0;
        }


        CheckBox AddOption(TableLayoutPanel panel, string text, string sectionName, string key, bool fallback)
        {
            var box = new ToggleRow { Text = text, Checked = ReadSetting(sectionName, key, fallback) };
            box.CheckedChanged += delegate
            {
                try { WriteSetting(sectionName, key, box.Checked); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            panel.Controls.Add(box);
            return box;
        }


        Dictionary<string, object> LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return new Dictionary<string, object>();
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath));
            return parsed as Dictionary<string, object> ?? new Dictionary<string, object>();
        }


        internal static void InstallDlssLibrary(string source, string directory)
        {
            if (!string.Equals(Path.GetFileName(source), "nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select the NVIDIA Super Resolution file named nvngx_dlss.dll.");
            using (var reader = new BinaryReader(File.OpenRead(source)))
            {
                if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
                    throw new InvalidDataException("The selected file is not a Windows DLL.");
                reader.BaseStream.Position = 0x3c;
                int pe = reader.ReadInt32();
                if (pe < 64 || pe > reader.BaseStream.Length - 26)
                    throw new InvalidDataException("The selected DLL has an invalid PE header.");
                reader.BaseStream.Position = pe;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                    throw new InvalidDataException("DLSS requires a 64-bit Windows DLL.");
                reader.BaseStream.Position = pe + 22;
                if ((reader.ReadUInt16() & 0x2000) == 0 || reader.ReadUInt16() != 0x20b)
                    throw new InvalidDataException("DLSS requires a 64-bit Windows DLL.");
            }
            var version = FileVersionInfo.GetVersionInfo(source);
            if ((version.CompanyName ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0 ||
                (version.FileDescription ?? "").IndexOf("DLSS", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("The selected file is not an NVIDIA DLSS library.");
            string destination = Path.Combine(directory, "nvngx_dlss.dll");
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(directory);
            string temporary = destination + ".install-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, destination + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak");
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }


        readonly object logLock = new object();

        /// Lines for the log from before a game's folder was known (OldLaunchers.Retire at start).
        static readonly List<string> startupNotes = new List<string>();

        string setupLogPath;


        void Log(string line)
        {
            lock (logLock)
            {
                try
                {
                    // FEAT-GTS-290: the selected game's logs\setup.log; none while it has no folder.
                    if (setupLogPath == null && RootReady(target))
                    {
                        setupLogPath = LogFiles.Prepare(root, "setup.log", info.TitleId);
                        File.AppendAllText(setupLogPath, LauncherVersion.Banner + " (" + info.Short + ")" + Environment.NewLine);
                        // FEAT-GTS-291: what happened to an older launcher at start, in the first log written.
                        foreach (string note in startupNotes) File.AppendAllText(setupLogPath, note + Environment.NewLine);
                        startupNotes.Clear();
                    }
                    if (setupLogPath != null) File.AppendAllText(setupLogPath, line + Environment.NewLine);
                }
                catch { }
            }
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLogText), line); return; }
            AppendLogText(line);
        }


        void AppendLogText(string line)
        {
            if (!IsDisposed && log != null && !log.IsDisposed) log.AppendText(line + Environment.NewLine);
        }

        void SetStatus(string text)
        {
            Log("== " + text);
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatusText), text); return; }
            SetStatusText(text);
        }


        void SetStatusText(string text)
        {
            status.Text = text;
        }


        /// RyGTLauncher.exe --setup: the same set up without the window, from the folders in the game's <TitleId>.RyGT.ini.
        public int SetupWithoutWindow()
        {
            try { setupLogPath = LogFiles.Prepare(root, "setup.log", info.TitleId); RunSetup(); return 0; }
            catch (Exception e) { Log("ERROR: " + e.Message); return 1; }
        }


        static void FindFonts(string selected, out string font, out string font2)
        {
            if (!Directory.Exists(selected)) throw new Exception("Pick the fonts folder (the one holding font and font2).");
            if (Directory.Exists(Path.Combine(selected, "font")) && Directory.Exists(Path.Combine(selected, "font2")))
            {
                font = Path.Combine(selected, "font");
                font2 = Path.Combine(selected, "font2");
                return;
            }
            var parent = Directory.GetParent(selected);
            if (parent != null && Path.GetFileName(selected).Equals("font", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(parent.FullName, "font2")))
            {
                font = selected;
                font2 = Path.Combine(parent.FullName, "font2");
                return;
            }
            throw new Exception("The fonts folder needs font and font2 folders inside it.");
        }


        static string FindModule(string folder, string name)
        {
            if (!Directory.Exists(folder)) throw new Exception("Pick the system modules folder.");
            foreach (var extension in new[] { ".sprx", ".prx", ".elf" })
            {
                var hit = Directory.EnumerateFiles(folder, name + extension, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }


        static string FirstExisting(params string[] paths)
        {
            return paths.FirstOrDefault(File.Exists);
        }


        void Unwrap(string source, string destination, OfflinePatch offline = null)
        {
            var bytes = File.ReadAllBytes(source);
            byte[] elf;
            try { elf = Self.RelroToLoad(Self.ToElf(bytes, Path.GetFileName(source))); }
            catch (DumpFileException e) { throw new DumpFileException(source, e.Message); }
            int reconstructedSize = elf.Length;
            if (target == GameKind.GT7 && Path.GetFileName(source).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) && GameLayout.PatchKnownExecutable(elf))
                Log("Applied the verified CUSA24769 1.00 conversion fix.");
            if (offline != null)
            {
                elf = offline.ExtendLayout(elf);
                Log("Extended the executable's segments for the " + OfflinePatch.GroupName + " patch.");
            }
            // The game and offline patch fingerprints use the unmodified ELF header.
            if (!Self.IsElf(bytes)) Self.OmitMissingSectionTable(elf, reconstructedSize);
            File.WriteAllBytes(destination, elf);
            Log("Prepared " + Path.GetFileName(destination) + " from " + source);
        }


        int Run(string exe, string arguments, string directory)
        {
            Log("> " + Path.GetFileName(exe) + " " + arguments);
            var info = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                var lines = new List<string>();
                DataReceivedEventHandler collect = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.StartsWith("FAIL") || e.Data.StartsWith("error") || e.Data.Contains("Exception")) Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines) File.WriteAllLines(Path.Combine(directory, "convert.log"), lines);
                Log("Converter finished with exit code " + process.ExitCode + " (full output in convert\\" + Path.GetFileName(directory) + "\\convert.log).");
                return process.ExitCode;
            }
        }


        /// Whether two paths are the same file on disk (one a hard link of the other, or a link to it).
        static bool SameFile(string a, string b)
        {
            Native.FileIdentity first, second;
            return Native.Identify(a, out first) && Native.Identify(b, out second) &&
                   first.Volume == second.Volume && first.Index == second.Index;
        }


        /// The first of the game's folders that has `name` at its top: RyGTS\app0\<serial> (what Set up made;
        /// app0 in a folder set up before FEAT-GTS-291), the update, then the base game, in the order the game
        /// looks (FEAT-GTS-259).
        string GameFile(string name)
        {
            var folders = new List<string> { App0Layout.Top(root, info.TitleId) };
            if (settings.Update.Length > 0) folders.Add(settings.Update);
            if (settings.Game.Length > 0) folders.Add(settings.Game);
            foreach (var folder in folders)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }


        /// Why a game file can't be used (missing, unreadable or a different size), or null.
        /// Opening it follows a symbolic link, unlike FileInfo.
        static string ReadProblem(string path, long expectedLength)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (expectedLength >= 0 && stream.Length != expectedLength)
                        return "has " + stream.Length + " bytes instead of " + expectedLength;
                }
                return null;
            }
            catch (FileNotFoundException) { return "is missing"; }
            catch (Exception e) { return "can't be read (" + e.Message.TrimEnd('.') + ")"; }
        }


        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }


        // Everything RyGTS makes stays in the RyGTS folder (portable), and the shaders are in plain sight
        // in <RyGT>\shaders (the renderer's cache folder), never in AppData. Older folders kept them in
        // user\cache: a real folder there is moved to shaders once. Builds from 2026-10-18 to 10-20
        // made user\cache a link to one AppData folder shared by every RyGT folder; its shaders are
        // copied instead (the AppData folder stays for other RyGT folders still linked to it).
        static string OldSharedShaderCache
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "RyGT", "shader-cache");
            }
        }


        string ShaderFolder { get { return Path.Combine(root, "shaders"); } }


        bool OldShaderCacheExists()
        {
            return Directory.Exists(Path.Combine(root, "user", "cache"));
        }


        void UsePortableShaderCache()
        {
            string old = Path.Combine(root, "user", "cache");
            try
            {
                var info = new DirectoryInfo(old);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // File by file, skipping what an earlier run (perhaps closed halfway) already
                    // copied, so a second start picks up where the first stopped. A link to a
                    // shared folder that is gone is just removed.
                    if (Directory.Exists(old))
                    {
                        Log("Copying the shaders from the shared AppData folder into the shaders folder (once)...");
                        int copied = 0;
                        CopyShaders(old, ShaderFolder, ref copied);
                        Log("Copied " + copied + " shader files.");
                    }
                    Directory.Delete(old, false); // the link only, not the shared folder
                    Log("Shaders are kept in the shaders folder now. " + OldSharedShaderCache +
                        " can be deleted once no other RyGT folder uses it.");
                }
                else if (info.Exists)
                {
                    Log("Moving the shaders from user\\cache to the shaders folder (once)...");
                    if (!Directory.Exists(ShaderFolder)) Directory.Move(old, ShaderFolder);
                    else { MergeShaderCache(old, ShaderFolder, true); Directory.Delete(old, true); }
                }
                Directory.CreateDirectory(ShaderFolder);
            }
            catch (Exception e)
            {
                Log("Note: shader folder: " + e.Message);
                try { Directory.CreateDirectory(ShaderFolder); } catch { }
            }
        }


        // Copies a shader folder's files that the target doesn't have yet, with progress in the status
        // line. The driver's pipeline cache file (several GB, unused since FIX-165) stays behind.
        void CopyShaders(string source, string target, ref int copied)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string destination = Path.Combine(target, name);
                if (File.Exists(destination)) continue;
                File.Copy(file, destination + ".part", true);
                File.Move(destination + ".part", destination);
                if (++copied % 2000 == 0) SetStatus("Copying your shaders into the shaders folder (once): " + copied + " files...");
            }
            foreach (var dir in Directory.GetDirectories(source))
                CopyShaders(dir, Path.Combine(target, Path.GetFileName(dir)), ref copied);
        }


        static void MergeShaderCache(string source, string target, bool move = true)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    if (move) File.Move(file, destination); else File.Copy(file, destination);
                }
                else if (name.EndsWith(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) &&
                         new FileInfo(file).Length > new FileInfo(destination).Length)
                    File.Copy(file, destination, true);
            }
            foreach (var dir in Directory.GetDirectories(source))
                MergeShaderCache(dir, Path.Combine(target, Path.GetFileName(dir)), move);
        }


        static bool RendererReportedDeviceLoss(string logDirectory)
        {
            string path = Path.Combine(logDirectory, "stdout.log");
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int count = (int)Math.Min(stream.Length, 1024 * 1024);
                    stream.Seek(-count, SeekOrigin.End);
                    byte[] bytes = new byte[count];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    string tail = Encoding.UTF8.GetString(bytes, 0, read);
                    return tail.IndexOf("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           tail.IndexOf("Device lost", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }


        /// FEAT-GTS-291: lines for a game's logs\<serial>\setup.log (it was setup.log in the runtime folder,
        /// which two games sharing the folder would both write).
        static void AppendSetupLog(string root, string serial, params string[] lines)
        {
            try
            {
                string logs = LogFiles.Folder(root, serial);
                Directory.CreateDirectory(logs);
                File.AppendAllLines(Path.Combine(logs, "setup.log"), lines);
            }
            catch { }
        }


        [STAThread]
        static int Main(string[] args)
        {
            // Read-only snapshots and the executable preparation helper do not open the launcher.
            bool helper = args.Length > 0 &&
                (args[0].StartsWith("--snapshot", StringComparison.Ordinal) || args[0] == "--prepare" ||
                 args[0] == "--allow-firewall"); // GT7's elevated firewall helper runs beside the launcher
            if (helper) return RunLauncher(args);

            // Hold the lock for the entire UI/setup lifetime. WaitOne is atomic across launches;
            // an abandoned lock from a crashed launcher can safely be acquired again.
            using (var instance = new Mutex(false, @"Local\RyGT.Launcher.SingleInstance"))
            {
                bool owns;
                try { owns = instance.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns)
                {
                    MessageBox.Show("RyGT Launcher is already running.\n\n" +
                        "Please close the existing launcher, then open RyGT Launcher again.",
                        "RyGT Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                // FEAT-GTS-291: an older launcher (RyGTS.exe, RyGT7.exe) beside this one goes to <name>.old.
                try { startupNotes.AddRange(OldLaunchers.Retire(AppDomain.CurrentDomain.BaseDirectory, Application.ExecutablePath)); }
                catch { }
                try { return RunLauncher(args); }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}
