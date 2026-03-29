using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VolumeMixer
{
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(18, 18, 24);
        public static readonly Color Surface = Color.FromArgb(26, 26, 36);
        public static readonly Color SurfaceHover = Color.FromArgb(36, 36, 50);
        public static readonly Color Border = Color.FromArgb(52, 52, 72);
        public static readonly Color Accent = Color.FromArgb(232, 109, 40);
        public static readonly Color AccentHover = Color.FromArgb(252, 128, 55);
        public static readonly Color AccentPressed = Color.FromArgb(180, 80, 20);
        public static readonly Color AccentGradTop = Color.FromArgb(245, 130, 55);
        public static readonly Color AccentGradBot = Color.FromArgb(200, 85, 20);
        public static readonly Color TextPrimary = Color.FromArgb(238, 238, 246);
        public static readonly Color TextSecondary = Color.FromArgb(120, 120, 148);
        public static readonly Color MutedRed = Color.FromArgb(210, 65, 65);
        public static readonly Color CheckedBg = Color.FromArgb(45, 35, 28);
        public static readonly Color ActiveDot = Color.FromArgb(72, 199, 116);
        public static readonly Color IdleDot = Color.FromArgb(80, 80, 100);
    }

    // ── Single animated session row ───────────────────────────────────────────
    public class SessionRow : Panel
    {
        // Set to false to hide the streaming indicator entirely
        public static bool ShowStreamingIndicator = true;

        public AudioSessionInfo Session { get; private set; }
        public bool IsChecked { get; set; }

        public event Action<SessionRow> CheckedChanged;

        private bool _hover;
        private System.Windows.Forms.Timer _animTimer;
        private int _animOffset;
        private int _step;

        private const int ItemHeight = 44;
        private const int AnimSteps = 10;
        private const int DotRadius = 5;
        private const int DotMarginRight = 10;  // gap from right edge

        public SessionRow(AudioSessionInfo session, bool isChecked)
        {
            Session = session;
            IsChecked = isChecked;
            Height = ItemHeight;
            Dock = DockStyle.Top;
            BackColor = Theme.Surface;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;

            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    IsChecked = !IsChecked;
                    Invalidate();
                    CheckedChanged?.Invoke(this);
                }
            };

            _animOffset = 300;
            _step = 0;
            StartSlideIn();
        }

        private void StartSlideIn()
        {
            if (_animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); }
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 14;
            _animTimer.Tick += (s, e) =>
            {
                _step++;
                float t = (float)_step / AnimSteps;
                float eased = 1f - (1f - t) * (1f - t);
                _animOffset = (int)((1f - eased) * 300);
                if (_step >= AnimSteps) { _animTimer.Stop(); _animOffset = 0; }
                Invalidate();
            };
            _animTimer.Start();
        }

        public void AnimateOut(Action onDone)
        {
            _step = 0;
            if (_animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); }
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 14;
            _animTimer.Tick += (s, e) =>
            {
                _step++;
                float t = (float)_step / AnimSteps;
                float eased = t * t;
                _animOffset = -(int)(eased * 300);
                Invalidate();
                if (_step >= AnimSteps) { _animTimer.Stop(); onDone?.Invoke(); }
            };
            _animTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(new Rectangle(0, 0, Width, Height));
            g.TranslateTransform(_animOffset, 0);

            bool chk = IsChecked;
            Color bg = chk ? Theme.CheckedBg : (_hover ? Theme.SurfaceHover : Theme.Surface);
            g.FillRectangle(new SolidBrush(bg), 0, 0, Width, Height);

            if (chk)
                g.FillRectangle(new SolidBrush(Theme.Accent), 0, 4, 3, Height - 8);

            // Circle checkbox
            int cx = 22, cy = Height / 2, cr = 9;
            var cRect = new Rectangle(cx - cr, cy - cr, cr * 2, cr * 2);
            if (chk)
            {
                g.FillEllipse(new SolidBrush(Theme.Accent), cRect);
                using (Pen p2 = new Pen(Color.White, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(p2, new PointF[] {
                        new PointF(cx - 4f, cy + 0.5f),
                        new PointF(cx - 1f, cy + 3.5f),
                        new PointF(cx + 4.5f, cy - 3f) });
            }
            else
                g.DrawEllipse(new Pen(Theme.Border, 1.5f), cRect);

            // ── Right-side layout (built right to left, all anchored to far right) ──

            // 1. Streaming dot — always at the very right with fixed margin
            int rightCursor = Width - DotMarginRight;
            if (ShowStreamingIndicator)
            {
                int dx = rightCursor - DotRadius * 2;
                int dy = cy - DotRadius;
                Color dotColor = Session.IsActive ? Theme.ActiveDot : Theme.IdleDot;
                g.FillEllipse(new SolidBrush(dotColor), dx, dy, DotRadius * 2, DotRadius * 2);
                if (Session.IsActive)
                    g.DrawEllipse(new Pen(Color.FromArgb(60, Theme.ActiveDot), 1.5f),
                        dx - 2, dy - 2, DotRadius * 2 + 4, DotRadius * 2 + 4);
                rightCursor = dx - 6;
            }

            // 2. MUTED badge — left of the dot
            if (Session.IsMuted)
            {
                Font badgeFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
                string badge = "MUTED";
                SizeF bs = g.MeasureString(badge, badgeFont);
                int bw = (int)bs.Width + 10, bh = 18;
                int bx = rightCursor - bw, by = cy - bh / 2;
                using (GraphicsPath bp = Pill(new Rectangle(bx, by, bw, bh)))
                    g.FillPath(new SolidBrush(Color.FromArgb(60, 210, 65, 65)), bp);
                StringFormat sf2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(badge, badgeFont, new SolidBrush(Theme.MutedRed), new RectangleF(bx, by, bw, bh), sf2);
                rightCursor = bx - 6;
            }

            // 3. App name — fills remaining space
            int tx = cx + cr + 12;
            var tsf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            Color tc = chk ? Theme.TextPrimary : Theme.TextSecondary;
            using (Font itemFont = new Font("Segoe UI", 9.5f, FontStyle.Regular))
                g.DrawString(Session.FriendlyName, itemFont, new SolidBrush(tc),
                    new Rectangle(tx, 0, rightCursor - tx, Height), tsf);

            g.DrawLine(new Pen(Color.FromArgb(30, Theme.Border), 1), 10, Height - 1, Width - 10, Height - 1);
        }

        private static GraphicsPath Pill(Rectangle r)
        {
            var p = new GraphicsPath(); int d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure(); return p;
        }
    }

    // ── Animated session list ─────────────────────────────────────────────────
    public class SessionListPanel : Panel
    {
        private readonly Dictionary<string, SessionRow> _rows =
            new Dictionary<string, SessionRow>(StringComparer.OrdinalIgnoreCase);

        public event Action<string, bool> ItemCheckedChanged;

        public SessionListPanel()
        {
            BackColor = Theme.Surface;
            AutoScroll = true;
            DoubleBuffered = true;
        }

        public void Sync(List<AudioSessionInfo> sessions)
        {
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions) incoming.Add(s.ProcessName);

            var toRemove = new List<string>();
            foreach (var key in _rows.Keys)
                if (!incoming.Contains(key)) toRemove.Add(key);

            foreach (var key in toRemove)
            {
                var row = _rows[key];
                _rows.Remove(key);
                row.AnimateOut(() =>
                {
                    try
                    {
                        if (row.Parent != null)
                            Invoke(new MethodInvoker(() => { Controls.Remove(row); row.Dispose(); }));
                    }
                    catch { }
                });
            }

            foreach (var sess in sessions)
            {
                if (_rows.TryGetValue(sess.ProcessName, out var existing))
                {
                    existing.Session.IsMuted = sess.IsMuted;
                    existing.Session.IsActive = sess.IsActive;
                    existing.Invalidate();
                    continue;
                }

                var row = new SessionRow(sess, sess.IsCheckedByUser);
                row.CheckedChanged += r => ItemCheckedChanged?.Invoke(r.Session.ProcessName, r.IsChecked);
                _rows[sess.ProcessName] = row;
                Controls.Add(row);
                row.BringToFront();
            }

            for (int i = sessions.Count - 1; i >= 0; i--)
                if (_rows.TryGetValue(sessions[i].ProcessName, out var r))
                    r.BringToFront();
        }

        public List<string> GetCheckedProcessNames()
        {
            var list = new List<string>();
            foreach (var kv in _rows)
                if (kv.Value.IsChecked) list.Add(kv.Key);
            return list;
        }

        public AudioSessionInfo GetSession(string processName)
            => _rows.TryGetValue(processName, out var row) ? row.Session : null;

        public void SetAllChecked(bool value)
        {
            foreach (var row in _rows.Values) { row.IsChecked = value; row.Invalidate(); }
        }

        public bool HasItems => _rows.Count > 0;
    }

    // ─── Orange gradient accent button ───────────────────────────────────────
    public class AccentButton : Button
    {
        private bool _hover, _down;

        public AccentButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            Cursor = Cursors.Hand;
            Height = 38;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);

            Rectangle fillRect = new Rectangle(1, 1, Width - 2, Height - 2);
            using (GraphicsPath path = RoundRect(fillRect, 9))
            {
                if (_down)
                {
                    g.FillPath(new SolidBrush(Theme.AccentPressed), path);
                }
                else
                {
                    Color top = _hover ? Theme.AccentHover : Theme.AccentGradTop;
                    Color bot = _hover ? Theme.AccentPressed : Theme.AccentGradBot;
                    using (var brush = new LinearGradientBrush(fillRect, top, bot, LinearGradientMode.Vertical))
                        g.FillPath(brush, path);
                    using (GraphicsPath tl = RoundRect(new Rectangle(fillRect.X + 1, fillRect.Y + 1, fillRect.Width - 2, fillRect.Height / 2), 8))
                        g.FillPath(new SolidBrush(Color.FromArgb(30, Color.White)), tl);
                }
            }

            StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, Font, Brushes.White, new RectangleF(0, 0, Width, Height), sf);
        }

        private static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    // ─── Animated pill toggle (mute switch) ──────────────────────────────────
    public class MuteToggle : Control
    {
        public bool IsOn { get; private set; }
        public event Action<bool> Toggled;

        // Animation
        private float _thumbX;
        private float _startX;
        private float _targetX;
        private int _animStep;
        private const int AnimSteps = 16;
        private System.Windows.Forms.Timer _anim;

        // Dimensions
        private const int W = 52, H = 28, R = 14;   // total size, corner radius
        private const int ThumbR = 11;               // thumb radius

        public MuteToggle()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(W, H);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;

            _thumbX = R;
            _startX = R;
            _targetX = R;
            _animStep = AnimSteps;

            _anim = new System.Windows.Forms.Timer { Interval = 10 };
            _anim.Tick += (s, e) =>
            {
                _animStep++;
                if (_animStep >= AnimSteps) { _thumbX = _targetX; _anim.Stop(); }
                else
                {
                    float t = (float)_animStep / AnimSteps;
                    // Smooth ease in-out (cubic)
                    float eased = t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
                    _thumbX = _startX + (_targetX - _startX) * eased;
                }
                Invalidate();
            };

            MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) Toggle();
            };
        }

        public void SetState(bool on, bool animate = true)
        {
            IsOn = on;
            _startX = _thumbX;
            _targetX = on ? W - R : R;
            if (animate) { _animStep = 0; _anim.Stop(); _anim.Start(); }
            else { _thumbX = _targetX; _startX = _targetX; _animStep = AnimSteps; Invalidate(); }
        }

        public void Toggle() { SetState(!IsOn); Toggled?.Invoke(IsOn); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Track
            Color trackOn = Theme.Accent;
            Color trackOff = Color.FromArgb(55, 55, 72);
            float blend = (_thumbX - R) / (float)(W - 2 * R);
            blend = Math.Max(0, Math.Min(1, blend));
            Color track = Blend(trackOff, trackOn, blend);

            using (GraphicsPath tp = TrackPath())
                g.FillPath(new SolidBrush(track), tp);

            // Thumb shadow
            int tx = (int)_thumbX, ty = H / 2;
            g.FillEllipse(new SolidBrush(Color.FromArgb(40, 0, 0, 0)),
                tx - ThumbR + 1, ty - ThumbR + 2, ThumbR * 2, ThumbR * 2);

            // Thumb
            using (var tb = new LinearGradientBrush(
                new Rectangle(tx - ThumbR, ty - ThumbR, ThumbR * 2, ThumbR * 2),
                Color.FromArgb(255, 255, 255), Color.FromArgb(220, 220, 220),
                LinearGradientMode.Vertical))
                g.FillEllipse(tb, tx - ThumbR, ty - ThumbR, ThumbR * 2, ThumbR * 2);
        }

        private GraphicsPath TrackPath()
        {
            var p = new GraphicsPath();
            int d = H;
            p.AddArc(0, 0, d, d, 90, 180);
            p.AddArc(W - d, 0, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        private static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim?.Dispose();
            base.Dispose(disposing);
        }
    }

    // ─── Title bar icon button (close / minimize) ─────────────────────────────
    public class TitleBarButton : Control
    {
        public string Symbol { get; set; }
        public bool IsDanger { get; set; } = false;  // true = red bg on hover (close)

        private bool _hover, _down;

        public TitleBarButton(string symbol)
        {
            Symbol = symbol;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(30, 30);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;

            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; _down = false; Invalidate(); };
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } };
            MouseUp += (s, e) => { _down = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);

            if (_hover || _down)
            {
                Color fill = IsDanger
                    ? Color.FromArgb(_down ? 180 : 140, 210, 45, 45)
                    : Color.FromArgb(_down ? 60 : 38, Theme.Accent);
                int pad = 3;
                var rect = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);
                int radius = 6;
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    int d = radius * 2;
                    path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                    path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                    path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();
                    g.FillPath(new SolidBrush(fill), path);
                }
            }

            Color fg = _hover
                ? (IsDanger ? Color.White : Theme.Accent)
                : Theme.TextSecondary;

            float fontSize = 10f;
            FontStyle fontStyle = FontStyle.Bold;
            if (Symbol == "\u2013") { fontSize = 13f; fontStyle = FontStyle.Regular; }
            else if (Symbol == "\u2699") { fontSize = 14f; fontStyle = FontStyle.Regular; }

            using (var sf = new System.Drawing.StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var f = new Font("Segoe UI", fontSize, fontStyle))
                g.DrawString(Symbol, f, new SolidBrush(fg),
                    new RectangleF(0, 0, Width, Height), sf);
        }
    }

    // ─── Ghost outline button ─────────────────────────────────────────────────
    public class GhostButton : Button
    {
        private bool _hover;

        public GhostButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            ForeColor = Theme.TextSecondary;
            Font = new Font("Segoe UI", 8.5f);
            Cursor = Cursors.Hand;
            Height = 28;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);

            Rectangle rect = new Rectangle(1, 1, Width - 2, Height - 2);
            using (GraphicsPath path = RoundRect(rect, 7))
            {
                if (_hover) g.FillPath(new SolidBrush(Theme.SurfaceHover), path);
                g.DrawPath(new Pen(_hover ? Theme.Border : Color.FromArgb(65, Theme.Border), 1f), path);
            }

            StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, Font, new SolidBrush(_hover ? Theme.TextPrimary : Theme.TextSecondary),
                new RectangleF(0, 0, Width, Height), sf);
        }

        private static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    // ─── Input device row (settings screen) ────────────────────────────────
    public class InputDeviceRow : Panel
    {
        public string DeviceId { get; private set; }
        public string DeviceName { get; private set; }
        public bool IsChecked { get; set; }
        public event Action<InputDeviceRow> CheckedChanged;

        private bool _hover;
        private const int ItemHeight = 48;

        public InputDeviceRow(string deviceId, string deviceName, bool isChecked)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            IsChecked = isChecked;
            Height = ItemHeight;
            Dock = DockStyle.Top;
            BackColor = Theme.Surface;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;

            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    IsChecked = !IsChecked;
                    Invalidate();
                    CheckedChanged?.Invoke(this);
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool chk = IsChecked;
            Color bg = chk ? Theme.CheckedBg : (_hover ? Theme.SurfaceHover : Theme.Surface);
            g.FillRectangle(new SolidBrush(bg), 0, 0, Width, Height);

            if (chk)
                g.FillRectangle(new SolidBrush(Theme.Accent), 0, 4, 3, Height - 8);

            // Circle checkbox
            int cx = 22, cy = Height / 2, cr = 9;
            var cRect = new Rectangle(cx - cr, cy - cr, cr * 2, cr * 2);
            if (chk)
            {
                g.FillEllipse(new SolidBrush(Theme.Accent), cRect);
                using (var p2 = new Pen(Color.White, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(p2, new PointF[] {
                        new PointF(cx - 4f, cy + 0.5f),
                        new PointF(cx - 1f, cy + 3.5f),
                        new PointF(cx + 4.5f, cy - 3f) });
            }
            else
                g.DrawEllipse(new Pen(Theme.Border, 1.5f), cRect);

            // Device name
            int tx = cx + cr + 14;
            var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            Color tc = chk ? Theme.TextPrimary : Theme.TextSecondary;
            using (var itemFont = new Font("Segoe UI", 9.5f, FontStyle.Regular))
                g.DrawString(DeviceName, itemFont, new SolidBrush(tc),
                    new Rectangle(tx, 0, Width - tx - 16, Height), sf);

            // Bottom separator
            g.DrawLine(new Pen(Color.FromArgb(30, Theme.Border), 1), 10, Height - 1, Width - 10, Height - 1);
        }
    }

    // ─── Mic status indicator (dot + LIVE / MUTED pill) ────────────────────
    public class MicStatusIndicator : Control
    {
        private bool _muted;

        public MicStatusIndicator()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(70, 20);
            BackColor = Color.Transparent;
        }

        public void SetMuted(bool muted)
        {
            if (_muted == muted) return;
            _muted = muted;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color dot = _muted ? Theme.MutedRed : Theme.ActiveDot;
            Color pillBg = Color.FromArgb(50, dot);
            string text = _muted ? "MUTED" : "LIVE";

            Font f = new Font("Segoe UI", 7f, FontStyle.Bold);
            SizeF ts = g.MeasureString(text, f);
            int dotR = 4;
            int pillW = dotR * 2 + 6 + (int)ts.Width + 16;
            int pillH = Height;

            // Pill background
            using (var path = new GraphicsPath())
            {
                int d = pillH;
                var rect = new Rectangle(0, 0, pillW, pillH);
                path.AddArc(rect.X, rect.Y, d, d, 90, 180);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(new SolidBrush(pillBg), path);
            }

            // Dot
            int dy = pillH / 2 - dotR;
            g.FillEllipse(new SolidBrush(dot), 8, dy, dotR * 2, dotR * 2);
            if (!_muted)
                g.DrawEllipse(new Pen(Color.FromArgb(60, dot), 1.2f), 6, dy - 2, dotR * 2 + 4, dotR * 2 + 4);

            // Text
            var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, new SolidBrush(dot), 8 + dotR * 2 + 4, pillH / 2f, sf);
        }
    }
}