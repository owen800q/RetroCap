using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace RetroCap.Services
{
    /// <summary>Notification-area icon with an SAP-coloured context menu.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;
        private readonly Forms.ToolStripItem _record;

        public TrayIcon(Action capture, Action record, Action open, Action exit)
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
            var menu = new Forms.ContextMenuStrip
            {
                Renderer = new SapRenderer(),
                Font = new Font("Microsoft Sans Serif", 8.25f),
                ShowImageMargin = false,
            };
            menu.Items.Add("Capture        Ctrl+Alt+A", null, (_, _) => capture());
            _record = menu.Items.Add("Record Screen  Ctrl+Alt+Z", null, (_, _) => record());
            menu.Items.Add("Open RetroCap", null, (_, _) => open());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => exit());

            _icon = new Forms.NotifyIcon
            {
                Icon = new Icon(stream, new System.Drawing.Size(16, 16)),
                Text = "RetroCap - Ctrl+Alt+A to capture",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
            _icon.DoubleClick += (_, _) => capture();
        }

        /// <summary>Switches the menu and tooltip between "record" and "stop recording".</summary>
        public void SetRecording(bool recording)
        {
            _record.Text = recording ? "Stop Recording Ctrl+Alt+Z" : "Record Screen  Ctrl+Alt+Z";
            _icon.Text = recording ? "RetroCap - Recording (Ctrl+Alt+Z to stop)" : "RetroCap - Ctrl+Alt+A to capture";
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }

        private sealed class SapRenderer : Forms.ToolStripProfessionalRenderer
        {
            public SapRenderer() : base(new SapColors()) => RoundedEdges = false;

            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? Color.White : Color.Black;
                base.OnRenderItemText(e);
            }
        }

        private sealed class SapColors : Forms.ProfessionalColorTable
        {
            private static readonly Color Panel = Color.FromArgb(0xEA, 0xF1, 0xF8);
            private static readonly Color Select = Color.FromArgb(0x2A, 0x6F, 0xB8);
            public override Color ToolStripDropDownBackground => Panel;
            public override Color ImageMarginGradientBegin => Panel;
            public override Color ImageMarginGradientMiddle => Panel;
            public override Color ImageMarginGradientEnd => Panel;
            public override Color MenuBorder => Color.Black;
            public override Color MenuItemBorder => Select;
            public override Color MenuItemSelected => Select;
            public override Color MenuItemSelectedGradientBegin => Select;
            public override Color MenuItemSelectedGradientEnd => Select;
            public override Color SeparatorDark => Color.FromArgb(0x6E, 0x7A, 0x89);
            public override Color SeparatorLight => Color.White;
        }
    }
}
