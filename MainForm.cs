using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VolumeMixer
{
    public class MainForm : Form
    {
        // ── Win32 ────────────────────────────────────────────────────────────
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc cb, IntPtr hmod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hk, int n, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int w, int l);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

        private delegate IntPtr LowLevelKeyboardProc(int n, IntPtr wp, IntPtr lp);
        private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100;
        private IntPtr _hookHandle = IntPtr.Zero;
        private LowLevelKeyboardProc _hookProc;

        private const string REG_KEY = @"Software\VolumeMixer";
        private const string REG_HOTKEY = "Hotkey";
        private const string REG_CHECKED = "CheckedApps";

        // ── State ─────────────────────────────────────────────────────────────
        private Keys _hotkey = Keys.None;
        private bool _isMuted = false;
        private bool _listeningKey = false;

        // Persisted checked process names — survives app disappearing and reappearing
        private HashSet<string> _checkedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Last known session keys for change detection
        private HashSet<string> _lastSessionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Controls ──────────────────────────────────────────────────────────
        private SessionListPanel _sessionList;
        private MuteToggle _muteToggle;
        private AccentButton _setKeyBtn;
        private GhostButton _selectAllBtn;
        private GhostButton _clearBtn;
        private Label _emptyHint;
        private Panel _topPanel;
        private Panel _bottomPanel;
        private System.Windows.Forms.Timer _pollTimer;

        public MainForm()
        {
            BuildUI();
            LoadPreferences();
            SyncSessions(force: true);
            InstallHook();
            StartPolling();
            CheckForUpdateAsync();
        }

        private void CheckForUpdateAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var info = Updater.CheckForUpdate();
                if (info == null) return;
                BeginInvoke(new MethodInvoker(() =>
                {
                    using (var dlg = new UpdateDialog(info))
                        dlg.ShowDialog(this);
                }));
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POLLING
        // ══════════════════════════════════════════════════════════════════════
        private void StartPolling()
        {
            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 1000;
            _pollTimer.Tick += (s, e) => SyncSessions(force: false);
            _pollTimer.Start();
        }

        private void SyncSessions(bool force)
        {
            if (InvokeRequired) { Invoke(new MethodInvoker(() => SyncSessions(force))); return; }

            List<AudioSessionInfo> sessions;
            try { sessions = AudioManager.GetAudioSessions(); }
            catch { sessions = new List<AudioSessionInfo>(); }

            var newKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions) newKeys.Add(s.ProcessName);

            bool muteChanged = false;
            foreach (var s in sessions)
            {
                var existing = _sessionList.GetSession(s.ProcessName);
                if (existing != null && (existing.IsMuted != s.IsMuted || existing.IsActive != s.IsActive))
                    muteChanged = true;
            }

            bool changed = force || muteChanged || !newKeys.SetEquals(_lastSessionKeys);
            if (!changed) return;

            _lastSessionKeys = newKeys;

            // Restore checked state from memory for any session in the list
            foreach (var s in sessions)
                s.IsCheckedByUser = _checkedApps.Contains(s.ProcessName);

            _sessionList.Sync(sessions);

            bool empty = sessions.Count == 0;
            _emptyHint.Visible = empty;
            if (empty) _emptyHint.BringToFront();
            else _sessionList.BringToFront();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PREFERENCES PERSISTENCE
        // ══════════════════════════════════════════════════════════════════════
        private void SavePreferences()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
                {
                    key.SetValue(REG_HOTKEY, (int)_hotkey, RegistryValueKind.DWord);
                    key.SetValue(REG_CHECKED, string.Join(",", _checkedApps), RegistryValueKind.String);
                }
            }
            catch { }
        }

        private void LoadPreferences()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    if (key == null) return;

                    var hotkeyVal = key.GetValue(REG_HOTKEY);
                    if (hotkeyVal != null)
                    {
                        var loaded = (Keys)(int)hotkeyVal;
                        if (loaded != Keys.None) AssignHotkey(loaded);
                    }

                    var checkedVal = key.GetValue(REG_CHECKED) as string;
                    if (!string.IsNullOrEmpty(checkedVal))
                        foreach (var name in checkedVal.Split(','))
                            if (!string.IsNullOrWhiteSpace(name))
                                _checkedApps.Add(name.Trim());
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BUILD UI
        // ══════════════════════════════════════════════════════════════════════
        private void BuildUI()
        {
            Text = "Volume Mixer";
            Size = new Size(430, 600);
            MinimumSize = new Size(360, 480);
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.5f);
            MouseDown += DragForm;

            // ── TOP BAR ───────────────────────────────────────────────────────
            _topPanel = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Theme.Surface };
            _topPanel.MouseDown += DragForm;
            _topPanel.Paint += (s, e) =>
            {
                var c = (Control)s;
                e.Graphics.FillRectangle(new SolidBrush(Theme.Accent), 0, 0, c.Width, 3);
                e.Graphics.DrawLine(new System.Drawing.Pen(Theme.Border, 1), 0, c.Height - 1, c.Width, c.Height - 1);
            };

            var titleLbl = new Label
            {
                Text = "Volume Mixer",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(18, 14)
            };
            titleLbl.MouseDown += DragForm;

            var subtitleLbl = new Label
            {
                Text = "Select apps  \u00b7  set a hotkey  \u00b7  toggle mute",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(20, 44)
            };
            subtitleLbl.MouseDown += DragForm;

            var closeBtn = new Button
            {
                Text = "x",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Size = new Size(30, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Theme.TextSecondary,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                UseVisualStyleBackColor = false
            };
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.Click += (s, e) => Application.Exit();
            closeBtn.MouseEnter += (s, e) => closeBtn.ForeColor = Theme.Accent;
            closeBtn.MouseLeave += (s, e) => closeBtn.ForeColor = Theme.TextSecondary;
            _topPanel.Resize += (s, e) => closeBtn.Location = new Point(_topPanel.Width - 38, 22);
            closeBtn.Location = new Point(392, 22);

            _topPanel.Controls.Add(titleLbl);
            _topPanel.Controls.Add(subtitleLbl);
            _topPanel.Controls.Add(closeBtn);

            // ── TOOLBAR ───────────────────────────────────────────────────────
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Background };

            _selectAllBtn = new GhostButton { Text = "Select All", Size = new Size(80, 26), Location = new Point(14, 8) };
            _selectAllBtn.Click += (s, e) =>
            {
                _sessionList.SetAllChecked(true);
                _checkedApps = new HashSet<string>(_sessionList.GetCheckedProcessNames(), StringComparer.OrdinalIgnoreCase);
                SavePreferences();
            };

            _clearBtn = new GhostButton { Text = "Clear", Size = new Size(56, 26), Location = new Point(100, 8) };
            _clearBtn.Click += (s, e) =>
            {
                _sessionList.SetAllChecked(false);
                _checkedApps.Clear();
                SavePreferences();
            };

            toolbar.Controls.Add(_selectAllBtn);
            toolbar.Controls.Add(_clearBtn);

            // ── LIST ──────────────────────────────────────────────────────────
            var listPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };

            _sessionList = new SessionListPanel { Dock = DockStyle.Fill };
            _sessionList.ItemCheckedChanged += (procName, isChecked) =>
            {
                if (isChecked) _checkedApps.Add(procName);
                else _checkedApps.Remove(procName);
                SavePreferences();
            };

            _emptyHint = new Label
            {
                Text = "No active audio sessions found.\nPlay audio in an app first.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Theme.TextSecondary,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                Visible = false
            };

            listPanel.Controls.Add(_sessionList);
            listPanel.Controls.Add(_emptyHint);

            // ── BOTTOM PANEL ──────────────────────────────────────────────────
            _bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 100, BackColor = Theme.Surface };
            _bottomPanel.Paint += (s, e) =>
                e.Graphics.DrawLine(new System.Drawing.Pen(Theme.Border, 1), 0, 0, ((Control)s).Width, 0);

            var hotkeyCap = new Label
            {
                Text = "HOTKEY",
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(16, 10)
            };

            _setKeyBtn = new AccentButton { Text = "Click to set hotkey", Size = new Size(176, 36), Location = new Point(16, 26) };
            _setKeyBtn.Click += SetKey_Click;

            // Label above the pill, both anchored to the right
            var toggleCap = new Label
            {
                Text = "PAUSE/RESUME",
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _muteToggle = new MuteToggle { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _muteToggle.SetState(false, animate: false);
            _muteToggle.Toggled += on => { /* just a setting, no action */ };

            // Version label — below the pill, right-aligned
            var verLabel = new Label
            {
                Text = "v" + Updater.CurrentVersion.ToString(3),
                Font = new Font("Segoe UI", 7f),
                ForeColor = Color.FromArgb(50, Theme.TextSecondary),
                BackColor = Color.Transparent,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            var footerHint = new Label
            {
                Text = "Global hotkey works even when minimized",
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(55, Theme.TextSecondary),
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(16, 80),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            _bottomPanel.Controls.Add(hotkeyCap);
            _bottomPanel.Controls.Add(_setKeyBtn);
            _bottomPanel.Controls.Add(toggleCap);
            _bottomPanel.Controls.Add(_muteToggle);
            _bottomPanel.Controls.Add(verLabel);
            _bottomPanel.Controls.Add(footerHint);

            // Position the toggle + label flush right, updating on resize
            Action layoutRight = () =>
            {
                int rightEdge = _bottomPanel.Width - 16;
                toggleCap.Size = toggleCap.GetPreferredSize(Size.Empty);
                verLabel.Size = verLabel.GetPreferredSize(Size.Empty);
                int blockLeft = rightEdge - Math.Max(_muteToggle.Width, Math.Max(toggleCap.Width, verLabel.Width));
                toggleCap.Location = new Point(blockLeft, 16);
                _muteToggle.Location = new Point(blockLeft, 36);
                verLabel.Location = new Point(_bottomPanel.Width - verLabel.Width - 4, _bottomPanel.Height - verLabel.Height - 3);
            };
            _bottomPanel.Resize += (s, e) => layoutRight();
            _bottomPanel.HandleCreated += (s, e) => layoutRight();

            // ── ASSEMBLE ──────────────────────────────────────────────────────
            var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
            center.Controls.Add(listPanel);
            center.Controls.Add(toolbar);

            Controls.Add(center);
            Controls.Add(_topPanel);
            Controls.Add(_bottomPanel);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  MUTE TOGGLE
        // ══════════════════════════════════════════════════════════════════════
        private void ToggleMute()
        {
            var checkedNames = _sessionList.GetCheckedProcessNames();
            if (checkedNames.Count == 0) return;

            var sessions = new List<AudioSessionInfo>();
            foreach (var name in checkedNames)
            {
                var s = _sessionList.GetSession(name);
                if (s != null) sessions.Add(s);
            }

            bool anyLive = false;
            foreach (var s in sessions)
                if (!AudioManager.GetMute(s)) { anyLive = true; break; }

            _isMuted = anyLive;
            foreach (var s in sessions) AudioManager.SetMute(s, _isMuted);

            if (_muteToggle.IsOn) SendMediaPlayPause();
            SyncSessions(force: true);
        }

        private void SendMediaPlayPause()
        {
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 2, UIntPtr.Zero); // key-up
        }

        // ══════════════════════════════════════════════════════════════════════
        //  HOTKEY SETUP
        // ══════════════════════════════════════════════════════════════════════
        private void SetKey_Click(object sender, EventArgs e)
        {
            _listeningKey = true;
            _setKeyBtn.Text = "Press any key...";
            _setKeyBtn.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_listeningKey)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key == Keys.Escape) { CancelKeyListen(); return true; }
                if (key != Keys.None) { AssignHotkey(key); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void AssignHotkey(Keys key)
        {
            _hotkey = key;
            _listeningKey = false;
            _setKeyBtn.Text = "Hotkey:  " + key.ToString();
            SavePreferences();
        }

        private void CancelKeyListen()
        {
            _listeningKey = false;
            _setKeyBtn.Text = _hotkey == Keys.None ? "Click to set hotkey" : "Hotkey:  " + _hotkey.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GLOBAL KEYBOARD HOOK
        // ══════════════════════════════════════════════════════════════════════
        private void InstallHook()
        {
            _hookProc = HookCallback;
            try
            {
                var p = System.Diagnostics.Process.GetCurrentProcess();
                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                    GetModuleHandle(p.MainModule.ModuleName), 0);
            }
            catch { }
        }

        private IntPtr HookCallback(int n, IntPtr wp, IntPtr lp)
        {
            if (n >= 0 && wp == (IntPtr)WM_KEYDOWN && !_listeningKey && _hotkey != Keys.None)
            {
                int vk = Marshal.ReadInt32(lp);
                if ((Keys)vk == (_hotkey & Keys.KeyCode))
                    BeginInvoke(new MethodInvoker(ToggleMute));
            }
            return CallNextHookEx(_hookHandle, n, wp, lp);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void DragForm(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawRectangle(new System.Drawing.Pen(Theme.Border, 1), new Rectangle(0, 0, Width - 1, Height - 1));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _pollTimer?.Stop();
            if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
            base.OnFormClosing(e);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x00020000; return cp; }
        }
    }
}