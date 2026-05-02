using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace VolumeMixer
{
    public class UpdateDialog : Form
    {
        private readonly UpdateInfo _info;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

        public UpdateDialog(UpdateInfo info)
        {
            _info = info;
            BuildUI();
        }

        private void BuildUI()
        {
            Text = "Update Available";
            Size = new Size(380, 280);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            MouseDown += Drag;

            var topBar = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Theme.Accent };

            var titleLbl = new Label
            {
                Text = "Update Available",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(20, 22)
            };
            titleLbl.MouseDown += Drag;

            var closeLbl = new Label
            {
                Text = "x",
                Font = new Font("Segoe UI", 13f),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Cursor = Cursors.Hand,
                Location = new Point(Width - 30, 18)
            };
            closeLbl.Click += (s, e) => Close();
            closeLbl.MouseEnter += (s, e) => closeLbl.ForeColor = Theme.Accent;
            closeLbl.MouseLeave += (s, e) => closeLbl.ForeColor = Theme.TextSecondary;
            Resize += (s, e) => closeLbl.Location = new Point(Width - 30, 18);

            var verLbl = new Label
            {
                Text = "Version " + _info.LatestVersion + " is ready to install.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(20, 58)
            };

            string rawNotes = StripMarkdown(_info.ReleaseNotes);
            string notes = string.IsNullOrWhiteSpace(rawNotes)
                ? "No release notes provided."
                : rawNotes.Length > 200
                    ? rawNotes.Substring(0, 197) + "..."
                    : rawNotes;

            var notesBox = new Label
            {
                Text = notes,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(100, 100, 128),
                BackColor = Theme.Background,
                Location = new Point(20, 82),
                Size = new Size(340, 66),
                Padding = new Padding(8),
            };

            // ── Progress bar (hidden until download starts) ───────────────────
            var progressBar = new ProgressBarFlat
            {
                Location = new Point(20, 158),
                Size = new Size(340, 4),
                Visible = false,
                Value = 0
            };

            var progressLbl = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(20, 166),
                Visible = false
            };

            // ── Buttons ───────────────────────────────────────────────────────
            var updateBtn = new AccentButton
            {
                Text = "Update Now",
                Size = new Size(120, 34),
                Location = new Point(20, 226)
            };

            var ignoreBtn = new GhostButton
            {
                Text = "Ignore",
                Size = new Size(80, 34),
                Location = new Point(150, 226)
            };
            ignoreBtn.Click += (s, e) => Close();

            var neverBtn = new GhostButton
            {
                Text = "Never ask again",
                Size = new Size(126, 34),
                Location = new Point(238, 226)
            };
            neverBtn.Click += (s, e) =>
            {
                Updater.SaveSkipVersion(_info.LatestVersion);
                Close();
            };

            updateBtn.Click += (s, e) =>
            {
                updateBtn.Enabled = false;
                updateBtn.Text = "Downloading...";
                ignoreBtn.Enabled = false;
                neverBtn.Enabled = false;
                progressBar.Visible = true;
                progressLbl.Visible = true;
                progressLbl.Text = "Starting download...";

                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    Updater.DownloadAndInstall(
                        _info.LatestVersion,
                        pct => BeginInvoke(new MethodInvoker(() =>
                        {
                            progressBar.Value = pct;
                            progressLbl.Text = "Downloading... " + pct + "%";
                        })),
                        err => BeginInvoke(new MethodInvoker(() =>
                        {
                            updateBtn.Enabled = true;
                            updateBtn.Text = "Update Now";
                            ignoreBtn.Enabled = true;
                            neverBtn.Enabled = true;
                            progressBar.Visible = false;
                            progressLbl.ForeColor = Theme.MutedRed;
                            progressLbl.Text = "Failed - check your connection";
                        }))
                    );
                });
            };

            Controls.Add(topBar);
            Controls.Add(titleLbl);
            Controls.Add(closeLbl);
            Controls.Add(verLbl);
            Controls.Add(notesBox);
            Controls.Add(progressBar);
            Controls.Add(progressLbl);
            Controls.Add(updateBtn);
            Controls.Add(ignoreBtn);
            Controls.Add(neverBtn);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawRectangle(new System.Drawing.Pen(Theme.Border, 1),
                new Rectangle(0, 0, Width - 1, Height - 1));
        }

        private void Drag(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
        }

        private static string StripMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder();
            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("### ")) line = line.Substring(4);
                else if (line.StartsWith("## ")) line = line.Substring(3);
                else if (line.StartsWith("# ")) line = line.Substring(2);
                if (line.StartsWith("- ")) line = "• " + line.Substring(2);
                line = line.Replace("**", "").Replace("__", "").Replace("`", "");
                sb.AppendLine(line);
            }
            return sb.ToString().Trim();
        }
    }

    // ── Thin flat progress bar styled to match the theme ─────────────────────
    public class ProgressBarFlat : Control
    {
        private int _value;
        public int Value
        {
            get => _value;
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public ProgressBarFlat()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.Border), 0, 0, Width, Height);
            if (_value > 0)
            {
                int fillW = (int)(Width * (_value / 100f));
                using (var brush = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, fillW), Height),
                    Theme.AccentGradTop, Theme.AccentGradBot,
                    LinearGradientMode.Horizontal))
                    g.FillRectangle(brush, 0, 0, fillW, Height);
            }
        }
    }
}