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
        private const string REG_MIC_HOTKEY = "MicHotkey";
        private const string REG_INPUT_DEVICES = "SelectedInputDevices";
        private const string REG_START_WITH_WINDOWS = "StartWithWindows";
        private const string RUN_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_RUN_NAME = "Volume Mixer";

        // ── State ─────────────────────────────────────────────────────────────
        private Keys _hotkey = Keys.None;
        private Keys _micHotkey = Keys.None;
        private HashSet<string> _selectedInputDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isMuted = false;
        private bool _listeningKey = false;
        private bool _listeningMicKey = false;
        private bool _loading = false;
        private bool _startWithWindows = true;

        // Persisted checked process names — survives app disappearing and reappearing
        private HashSet<string> _checkedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Last known session keys for change detection
        private HashSet<string> _lastSessionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Controls ──────────────────────────────────────────────────────────
        private SessionListPanel _sessionList;
        private MuteToggle _muteToggle;
        private AccentButton _setKeyBtn;
        private AccentButton _setMicKeyBtn;
        private MicStatusIndicator _micStatus;
        private GhostButton _selectAllBtn;
        private GhostButton _clearBtn;
        private Label _emptyHint;
        private Panel _topPanel;
        private Panel _bottomPanel;
        private System.Windows.Forms.Timer _pollTimer;
        private NotifyIcon _trayIcon;
        private bool _reallyClosing = false;
        private Panel _settingsPanel;
        private bool _settingsVisible = false;

        public MainForm()
        {
            BuildUI();
            SetupTrayIcon();
            LoadPreferences();
            SyncSessions(force: true);
            PollMicStatus();
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
            _pollTimer.Tick += (s, e) => { SyncSessions(force: false); PollMicStatus(); };
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

        private void PollMicStatus()
        {
            try
            {
                if (_selectedInputDevices.Count > 0)
                {
                    var devices = AudioManager.GetCaptureDevices();
                    bool allMuted = true;
                    bool anyFound = false;
                    foreach (var d in devices)
                    {
                        if (_selectedInputDevices.Contains(d.Id))
                        {
                            anyFound = true;
                            if (!AudioManager.GetMicMuteForDevice(d.Id)) { allMuted = false; break; }
                        }
                    }
                    _micStatus.SetMuted(anyFound && allMuted);
                }
                else
                {
                    _micStatus.SetMuted(AudioManager.GetMicMute());
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PREFERENCES PERSISTENCE
        // ══════════════════════════════════════════════════════════════════════
        private void SavePreferences()
        {
            if (_loading) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
                {
                    key.SetValue(REG_HOTKEY, (int)_hotkey, RegistryValueKind.DWord);
                    key.SetValue(REG_MIC_HOTKEY, (int)_micHotkey, RegistryValueKind.DWord);
                    key.SetValue(REG_CHECKED, string.Join(",", _checkedApps), RegistryValueKind.String);
                    key.SetValue(REG_INPUT_DEVICES, string.Join("|", _selectedInputDevices), RegistryValueKind.String);
                    key.SetValue(REG_START_WITH_WINDOWS, _startWithWindows ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private void SetStartWithWindows(bool enable)
        {
            try
            {
                using (var runKey = Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, true))
                {
                    if (runKey == null) return;
                    if (enable)
                        runKey.SetValue(APP_RUN_NAME, Application.ExecutablePath);
                    else
                        runKey.DeleteValue(APP_RUN_NAME, false);
                }
            }
            catch { }
        }

        private void LoadPreferences()
        {
            bool startWithWinFound = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    if (key == null) { goto ApplyStartupDefault; }

                    // Read all values first before applying (AssignHotkey calls
                    // SavePreferences which would overwrite not-yet-loaded values)
                    Keys loadedHotkey = Keys.None;
                    Keys loadedMicHotkey = Keys.None;

                    var hotkeyVal = key.GetValue(REG_HOTKEY);
                    if (hotkeyVal != null)
                    {
                        int hkRaw;
                        if (hotkeyVal is int) hkRaw = (int)hotkeyVal;
                        else if (int.TryParse(hotkeyVal.ToString(), out hkRaw)) { }
                        else hkRaw = 0;
                        loadedHotkey = (Keys)hkRaw;
                    }

                    var micHotkeyVal = key.GetValue(REG_MIC_HOTKEY);
                    if (micHotkeyVal != null)
                    {
                        int micRaw;
                        if (micHotkeyVal is int) micRaw = (int)micHotkeyVal;
                        else if (int.TryParse(micHotkeyVal.ToString(), out micRaw)) { }
                        else micRaw = 0;
                        loadedMicHotkey = (Keys)micRaw;
                    }

                    var checkedVal = key.GetValue(REG_CHECKED) as string;
                    if (!string.IsNullOrEmpty(checkedVal))
                        foreach (var name in checkedVal.Split(','))
                            if (!string.IsNullOrWhiteSpace(name))
                                _checkedApps.Add(name.Trim());

                    var inputDevVal = key.GetValue(REG_INPUT_DEVICES) as string;
                    if (!string.IsNullOrEmpty(inputDevVal))
                        foreach (var id in inputDevVal.Split('|'))
                            if (!string.IsNullOrWhiteSpace(id))
                                _selectedInputDevices.Add(id.Trim());

                    var swwVal = key.GetValue(REG_START_WITH_WINDOWS);
                    if (swwVal != null)
                    {
                        int swwRaw;
                        if (swwVal is int) swwRaw = (int)swwVal;
                        else if (int.TryParse(swwVal.ToString(), out swwRaw)) { }
                        else swwRaw = 1;
                        _startWithWindows = swwRaw != 0;
                        startWithWinFound = true;
                    }

                    // Now apply — _loading flag prevents AssignHotkey from triggering SavePreferences
                    _loading = true;
                    if (loadedHotkey != Keys.None) AssignHotkey(loadedHotkey);
                    if (loadedMicHotkey != Keys.None) AssignMicHotkey(loadedMicHotkey);
                    _loading = false;
                }
            }
            catch { }

            ApplyStartupDefault:
            if (!startWithWinFound)
            {
                _startWithWindows = true;
                SetStartWithWindows(true);
                SavePreferences();
            }
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

            var closeBtn = new TitleBarButton("x")
            {
                IsDanger = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            closeBtn.Click += (s, e) => { _reallyClosing = true; Application.Exit(); };

            var minimizeBtn = new TitleBarButton("\u2013")
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            minimizeBtn.Click += (s, e) => HideToTray();

            var settingsBtn = new TitleBarButton("\u2699")
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            settingsBtn.Click += (s, e) => { if (_settingsVisible) HideSettings(); else ShowSettings(); };

            _topPanel.Resize += (s, e) =>
            {
                closeBtn.Location = new Point(_topPanel.Width - 38, 22);
                minimizeBtn.Location = new Point(_topPanel.Width - 72, 22);
                settingsBtn.Location = new Point(_topPanel.Width - 106, 22);
            };
            closeBtn.Location = new Point(392, 22);
            minimizeBtn.Location = new Point(358, 22);
            settingsBtn.Location = new Point(324, 22);

            _topPanel.Controls.Add(titleLbl);
            _topPanel.Controls.Add(subtitleLbl);
            _topPanel.Controls.Add(settingsBtn);
            _topPanel.Controls.Add(minimizeBtn);
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
            _bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 156, BackColor = Theme.Surface };
            _bottomPanel.Paint += (s, e) =>
                e.Graphics.DrawLine(new System.Drawing.Pen(Theme.Border, 1), 0, 0, ((Control)s).Width, 0);

            // Row 1: Two hotkey buttons side by side
            var hotkeyCap = new Label
            {
                Text = "MUTE HOTKEY",
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true
            };

            _setKeyBtn = new AccentButton { Text = "Click to set hotkey", Height = 32 };
            _setKeyBtn.Click += SetKey_Click;

            var micCap = new Label
            {
                Text = "MIC MUTE",
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true
            };

            _setMicKeyBtn = new AccentButton { Text = "Click to set hotkey", Height = 32 };
            _setMicKeyBtn.Click += SetMicKey_Click;

            _micStatus = new MicStatusIndicator();

            // Row 2: Pause/resume toggle on left
            var toggleCap = new Label
            {
                Text = "PAUSE / RESUME",
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = true
            };

            _muteToggle = new MuteToggle();
            _muteToggle.SetState(false, animate: false);
            _muteToggle.Toggled += on => { /* just a setting, no action */ };

            var footerHint = new Label
            {
                Text = "Global hotkeys work even when minimized",
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(55, Theme.TextSecondary),
                BackColor = Color.Transparent,
                AutoSize = true
            };

            var verLabel = new Label
            {
                Text = "v" + Updater.CurrentVersion.ToString(3),
                Font = new Font("Segoe UI", 7f),
                ForeColor = Color.FromArgb(50, Theme.TextSecondary),
                BackColor = Color.Transparent,
                AutoSize = true
            };

            _bottomPanel.Controls.Add(hotkeyCap);
            _bottomPanel.Controls.Add(_setKeyBtn);
            _bottomPanel.Controls.Add(micCap);
            _bottomPanel.Controls.Add(_setMicKeyBtn);
            _bottomPanel.Controls.Add(_micStatus);
            _bottomPanel.Controls.Add(toggleCap);
            _bottomPanel.Controls.Add(_muteToggle);
            _bottomPanel.Controls.Add(footerHint);
            _bottomPanel.Controls.Add(verLabel);

            // Responsive layout — reflows on resize
            Action layoutBottom = () =>
            {
                int w = _bottomPanel.Width;
                int pad = 16;
                int gap = 10;
                int btnWidth = (w - pad * 2 - gap) / 2;

                // Row 1: labels + mic status at far right of mic column
                hotkeyCap.Location = new Point(pad, 18);
                micCap.Location = new Point(pad + btnWidth + gap, 18);
                _micStatus.Location = new Point(pad + btnWidth + gap + btnWidth - _micStatus.Width, 16);

                // Row 1: buttons (extra gap from labels)
                _setKeyBtn.SetBounds(pad, 46, btnWidth, 34);
                _setMicKeyBtn.SetBounds(pad + btnWidth + gap, 46, btnWidth, 34);

                // Row 2: toggle
                toggleCap.Location = new Point(pad, 98);
                toggleCap.Size = toggleCap.GetPreferredSize(Size.Empty);
                _muteToggle.Location = new Point(pad + toggleCap.Width + 8, 95);

                footerHint.Location = new Point(pad, _bottomPanel.Height - footerHint.Height - 4);
                verLabel.Size = verLabel.GetPreferredSize(Size.Empty);
                verLabel.Location = new Point(w - verLabel.Width - 6, _bottomPanel.Height - verLabel.Height - 4);
            };
            _bottomPanel.Resize += (s, e) => layoutBottom();
            _bottomPanel.HandleCreated += (s, e) => layoutBottom();

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
            _listeningMicKey = false;
            CancelMicKeyListen();
            _listeningKey = true;
            _setKeyBtn.Text = "Press any key...";
            _setKeyBtn.Focus();
        }

        private void SetMicKey_Click(object sender, EventArgs e)
        {
            _listeningKey = false;
            CancelKeyListen();
            _listeningMicKey = true;
            _setMicKeyBtn.Text = "Press any key...";
            _setMicKeyBtn.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (_listeningKey)
            {
                if (key == Keys.Escape) { CancelKeyListen(); return true; }
                if (key != Keys.None) { AssignHotkey(key); return true; }
            }
            if (_listeningMicKey)
            {
                if (key == Keys.Escape) { CancelMicKeyListen(); return true; }
                if (key != Keys.None) { AssignMicHotkey(key); return true; }
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

        private void AssignMicHotkey(Keys key)
        {
            _micHotkey = key;
            _listeningMicKey = false;
            _setMicKeyBtn.Text = "Mic Mute:  " + key.ToString();
            SavePreferences();
        }

        private void CancelKeyListen()
        {
            _listeningKey = false;
            _setKeyBtn.Text = _hotkey == Keys.None ? "Click to set hotkey" : "Hotkey:  " + _hotkey.ToString();
        }

        private void CancelMicKeyListen()
        {
            _listeningMicKey = false;
            _setMicKeyBtn.Text = _micHotkey == Keys.None ? "Click to set hotkey" : "Mic Mute:  " + _micHotkey.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SETTINGS SCREEN
        // ══════════════════════════════════════════════════════════════════════
        private void ShowSettings()
        {
            if (_settingsVisible) return;
            _settingsVisible = true;
            BuildSettingsPanel();
            _settingsPanel.Location = new Point(0, _topPanel.Height);
            _settingsPanel.Size = new Size(Width, Height - _topPanel.Height);
            _settingsPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_settingsPanel);
            _settingsPanel.BringToFront();
        }

        private void HideSettings()
        {
            if (!_settingsVisible) return;
            _settingsVisible = false;
            if (_settingsPanel != null)
            {
                Controls.Remove(_settingsPanel);
                _settingsPanel.Dispose();
                _settingsPanel = null;
            }
        }

        private void BuildSettingsPanel()
        {
            _settingsPanel = new Panel { BackColor = Theme.Background };

            // ── Header ──────────────────────────────────────────────────────
            var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Theme.Background };
            header.Paint += (s, e) =>
                e.Graphics.DrawLine(new System.Drawing.Pen(Theme.Border, 1),
                    0, ((Control)s).Height - 1, ((Control)s).Width, ((Control)s).Height - 1);

            var backBtn = new GhostButton { Text = "\u2190  Back", Size = new Size(76, 28), Location = new Point(14, 11) };
            backBtn.Click += (s, e) => HideSettings();

            var titleLbl = new Label
            {
                Text = "INPUT DEVICES",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(98, 16)
            };

            var selectAllBtn = new GhostButton { Text = "Select All", Size = new Size(80, 28) };
            var clearBtn = new GhostButton { Text = "Clear", Size = new Size(56, 28) };

            Action layoutHeader = () =>
            {
                selectAllBtn.Location = new Point(header.Width - 80 - 56 - 14 - 6, 11);
                clearBtn.Location = new Point(header.Width - 56 - 14, 11);
            };
            header.Resize += (s, e) => layoutHeader();
            header.HandleCreated += (s, e) => layoutHeader();

            header.Controls.Add(backBtn);
            header.Controls.Add(titleLbl);
            header.Controls.Add(selectAllBtn);
            header.Controls.Add(clearBtn);

            // ── Description ─────────────────────────────────────────────────
            var descPanel = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Background };
            var descLbl = new Label
            {
                Text = "Select which input devices the mic mute hotkey controls.",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 16, 0)
            };
            descPanel.Controls.Add(descLbl);

            // ── Device list ─────────────────────────────────────────────────
            var deviceList = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                AutoScroll = true
            };

            var devices = AudioManager.GetCaptureDevices();

            selectAllBtn.Click += (s, e) =>
            {
                foreach (Control c in deviceList.Controls)
                {
                    var row = c as InputDeviceRow;
                    if (row != null && !row.IsChecked)
                    {
                        row.IsChecked = true;
                        _selectedInputDevices.Add(row.DeviceId);
                        row.Invalidate();
                    }
                }
                SavePreferences();
            };

            clearBtn.Click += (s, e) =>
            {
                foreach (Control c in deviceList.Controls)
                {
                    var row = c as InputDeviceRow;
                    if (row != null && row.IsChecked)
                    {
                        row.IsChecked = false;
                        row.Invalidate();
                    }
                }
                _selectedInputDevices.Clear();
                SavePreferences();
            };

            // Add rows in reverse order (Dock.Top stacks from top)
            for (int i = devices.Count - 1; i >= 0; i--)
            {
                var d = devices[i];
                var row = new InputDeviceRow(d.Id, d.Name, _selectedInputDevices.Contains(d.Id));
                row.CheckedChanged += r =>
                {
                    if (r.IsChecked) _selectedInputDevices.Add(r.DeviceId);
                    else _selectedInputDevices.Remove(r.DeviceId);
                    SavePreferences();
                };
                deviceList.Controls.Add(row);
            }

            if (devices.Count == 0)
            {
                var noDevLbl = new Label
                {
                    Text = "No input devices found.",
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = Theme.TextSecondary,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Surface
                };
                deviceList.Controls.Add(noDevLbl);
            }

            // ── Start with Windows ──────────────────────────────────────────────
            var startupPanel = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Background };
            startupPanel.Paint += (s, e) =>
                e.Graphics.DrawLine(new System.Drawing.Pen(Theme.Border, 1),
                    0, ((Control)s).Height - 1, ((Control)s).Width, ((Control)s).Height - 1);

            var startupChk = new CheckBox
            {
                Text = "Start with Windows",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                Checked = _startWithWindows,
                AutoSize = true,
                Location = new Point(16, 12)
            };
            startupChk.CheckedChanged += (s, e) =>
            {
                _startWithWindows = startupChk.Checked;
                SetStartWithWindows(_startWithWindows);
                SavePreferences();
            };
            startupPanel.Controls.Add(startupChk);

            _settingsPanel.Controls.Add(deviceList);
            _settingsPanel.Controls.Add(startupPanel);
            _settingsPanel.Controls.Add(descPanel);
            _settingsPanel.Controls.Add(header);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SYSTEM TRAY
        // ══════════════════════════════════════════════════════════════════════
        private void SetupTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.BackColor = Theme.Surface;
            menu.ForeColor = Theme.TextPrimary;
            menu.Font = new Font("Segoe UI", 9f);

            var showItem = new ToolStripMenuItem("Show Volume Mixer");
            showItem.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            showItem.Click += (s, e) => ShowFromTray();

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => { _reallyClosing = true; Application.Exit(); };

            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            // Load the icon from the exe itself — guarantees the real app icon
            // is used in the tray, not the WinForms default placeholder.
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); }
            catch { appIcon = SystemIcons.Application; }

            // Apply to the form too so the taskbar/alt-tab entry also uses it
            this.Icon = appIcon;

            _trayIcon = new NotifyIcon
            {
                Text = "Volume Mixer",
                Icon = appIcon,
                ContextMenuStrip = menu,
                Visible = false
            };

            _trayIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        private void HideToTray()
        {
            _trayIcon.Visible = true;
            Hide();
            _trayIcon.ShowBalloonTip(1500, "Volume Mixer", "Running in the background. Double-click to open.", ToolTipIcon.None);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            _trayIcon.Visible = false;
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
            if (n >= 0 && wp == (IntPtr)WM_KEYDOWN && !_listeningKey && !_listeningMicKey)
            {
                int vk = Marshal.ReadInt32(lp);
                if (_hotkey != Keys.None && (Keys)vk == (_hotkey & Keys.KeyCode))
                    BeginInvoke(new MethodInvoker(ToggleMute));
                if (_micHotkey != Keys.None && (Keys)vk == (_micHotkey & Keys.KeyCode))
                    BeginInvoke(new MethodInvoker(ToggleMicMute));
            }
            return CallNextHookEx(_hookHandle, n, wp, lp);
        }

        private void ToggleMicMute()
        {
            if (_selectedInputDevices.Count > 0)
            {
                var devices = AudioManager.GetCaptureDevices();
                var selected = new List<CaptureDeviceInfo>();
                foreach (var d in devices)
                    if (_selectedInputDevices.Contains(d.Id)) selected.Add(d);

                if (selected.Count > 0)
                {
                    bool anyLive = false;
                    foreach (var d in selected)
                        if (!AudioManager.GetMicMuteForDevice(d.Id)) { anyLive = true; break; }
                    foreach (var d in selected)
                        AudioManager.SetMicMuteForDevice(d.Id, anyLive);
                }
                else
                {
                    AudioManager.ToggleMicMute();
                }
            }
            else
            {
                AudioManager.ToggleMicMute();
            }
            PollMicStatus();
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
            if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _pollTimer?.Stop();
            _trayIcon?.Dispose();
            if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
            base.OnFormClosing(e);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x00020000; return cp; }
        }
    }
}