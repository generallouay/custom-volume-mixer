using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VolumeMixer
{
    public enum OverlayCorner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }

    public class OverlayForm : Form
    {
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        const uint ULW_ALPHA = 2;

        private const int OW = 96, OH = 48;
        private const string GlyphMic   = "";   // Segoe MDL2 Assets: Microphone  (U+E720)
        private const string GlyphAudio = ""; // Segoe MDL2 Assets: Headphone   (U+E7F6)

        private bool _audioMuted, _micMuted;
        private OverlayCorner _corner = OverlayCorner.BottomRight;
        private int _monitorIndex, _offsetX = 16, _offsetY = 16;
        private int _posX, _posY;
        private readonly Timer _topmostTimer;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Size = new Size(OW, OH);
            _topmostTimer = new Timer { Interval = 500 };
            _topmostTimer.Tick += (s, e) => ReassertTopmost();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = unchecked((int)0x80000000); // WS_POPUP
                cp.ExStyle |= 0x00000008   // WS_EX_TOPMOST
                           |  0x00080000   // WS_EX_LAYERED
                           |  0x00000020   // WS_EX_TRANSPARENT
                           |  0x08000000   // WS_EX_NOACTIVATE
                           |  0x00000080;  // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ReassertTopmost();
            RepositionOverlay();
            _topmostTimer.Start();
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated) BeginInvoke(new MethodInvoker(RepositionOverlay));
        }

        public void SetPosition(OverlayCorner corner, int monitorIndex, int offsetX, int offsetY)
        {
            _corner = corner;
            _monitorIndex = monitorIndex;
            _offsetX = offsetX;
            _offsetY = offsetY;
            if (IsHandleCreated) RepositionOverlay();
        }

        public void UpdateState(bool audioMuted, bool micMuted)
        {
            if (_audioMuted == audioMuted && _micMuted == micMuted) return;
            _audioMuted = audioMuted;
            _micMuted = micMuted;
            if (IsHandleCreated) Redraw();
        }

        private void RepositionOverlay()
        {
            var screens = Screen.AllScreens;
            int idx = Math.Max(0, Math.Min(_monitorIndex, screens.Length - 1));
            var b = screens[idx].Bounds;
            switch (_corner)
            {
                case OverlayCorner.TopLeft:    _posX = b.Left + _offsetX;        _posY = b.Top + _offsetY;         break;
                case OverlayCorner.TopRight:   _posX = b.Right - OW - _offsetX;  _posY = b.Top + _offsetY;         break;
                case OverlayCorner.BottomLeft: _posX = b.Left + _offsetX;        _posY = b.Bottom - OH - _offsetY; break;
                default:                       _posX = b.Right - OW - _offsetX;  _posY = b.Bottom - OH - _offsetY; break;
            }
            SetWindowPos(Handle, HWND_TOPMOST, _posX, _posY, OW, OH, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Redraw();
        }

        public void Redraw()
        {
            if (!IsHandleCreated) return;
            using (var bmp = new Bitmap(OW, OH, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                using (var path = RoundedRect(0, 0, OW, OH, OH / 2))
                using (var fill = new SolidBrush(Color.FromArgb(210, 18, 18, 26)))
                    g.FillPath(fill, path);
                using (var path = RoundedRect(0, 0, OW - 1, OH - 1, OH / 2))
                using (var border = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                    g.DrawPath(border, path);

                int sz = 28, gap = 8;
                int ix = (OW - sz * 2 - gap) / 2;
                int iy = (OH - sz) / 2;

                DrawIcon(g, new Rectangle(ix, iy, sz, sz), GlyphMic, _micMuted);
                DrawIcon(g, new Rectangle(ix + sz + gap, iy, sz, sz), GlyphAudio, _audioMuted);

                PushBitmap(bmp);
            }
        }

        private static void DrawIcon(Graphics g, Rectangle r, string glyph, bool muted)
        {
            Color iconColor = muted ? Color.FromArgb(200, 65, 65) : Color.FromArgb(72, 199, 116);
            using (var font = new Font("Segoe MDL2 Assets", 17f))
            using (var brush = new SolidBrush(iconColor))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, font, brush, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
            }
            if (muted)
            {
                using (var pen = new Pen(Color.FromArgb(210, 255, 255, 255), 2.2f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, r.Right - 3, r.Y + 3, r.X + 3, r.Bottom - 3);
            }
        }

        private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            var p = new GraphicsPath();
            p.AddArc(x, y, r * 2, r * 2, 180, 90);
            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void PushBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBmp);

            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            var size = new SIZE { cx = OW, cy = OH };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = _posX, y = _posY };

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);

            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        private void ReassertTopmost()
        {
            if (!IsHandleCreated) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _topmostTimer?.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            base.OnFormClosing(e);
        }
    }
}