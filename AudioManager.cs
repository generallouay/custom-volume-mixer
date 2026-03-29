using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VolumeMixer
{
    public class AudioSessionInfo
    {
        public string ProcessName { get; set; }
        public string DisplayName { get; set; }
        public int ProcessId { get; set; }
        public bool IsMuted { get; set; }
        public bool IsActive { get; set; }  // true = audio currently flowing (peak > 0)
        public bool IsCheckedByUser { get; set; }
        public object RawSession { get; set; }

        public string FriendlyName
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayName) && !DisplayName.StartsWith("@"))
                    return DisplayName;
                if (!string.IsNullOrEmpty(ProcessName))
                    return ProcessName;
                return "PID " + ProcessId;
            }
        }

        public override string ToString() { return FriendlyName; }

        public ISimpleAudioVolume GetVolume() => RawSession as ISimpleAudioVolume;
        public IAudioMeterInformation GetMeter() => RawSession as IAudioMeterInformation;
    }

    public class CaptureDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    [ComVisible(true)]
    public class AudioSessionWatcher : IAudioSessionNotification
    {
        public event Action SessionsChanged;

        public int OnSessionCreated(IAudioSessionControl NewSession)
        {
            System.Threading.Timer t = null;
            t = new System.Threading.Timer(_ =>
            {
                t.Dispose();
                SessionsChanged?.Invoke();
            }, null, 500, System.Threading.Timeout.Infinite);
            return 0;
        }
    }

    public static class AudioManager
    {
        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        private static IAudioSessionManager2 _manager;
        private static AudioSessionWatcher _watcher;

        public static event Action SessionsChanged;

        private static readonly HashSet<string> _blocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nvcontainer", "ledkeeper2", "audiodg", "svchost",
            "msedgewebview2", "runtimebroker", "searchhost"
        };

        private static readonly Dictionary<string, string> _nameOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "msedge",  "Microsoft Edge" },
            { "chrome",  "Google Chrome" },
            { "firefox", "Mozilla Firefox" },
            { "brave",   "Brave Browser" },
            { "spotify", "Spotify" },
            { "discord", "Discord" },
            { "teams",   "Microsoft Teams" },
            { "slack",   "Slack" },
            { "zoom",    "Zoom" },
        };

        public static void StartWatching()
        {
            try
            {
                if (_manager == null) _manager = GetManager();
                if (_manager == null) return;
                _watcher = new AudioSessionWatcher();
                _watcher.SessionsChanged += () => SessionsChanged?.Invoke();
                _manager.RegisterSessionNotification(_watcher);
            }
            catch (Exception ex) { Debug.WriteLine("StartWatching error: " + ex.Message); }
        }

        public static void StopWatching()
        {
            try { if (_manager != null && _watcher != null) _manager.UnregisterSessionNotification(_watcher); }
            catch { }
        }

        private static IAudioSessionManager2 GetManager()
        {
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            object enumObj;
            if (Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out enumObj) != 0) return null;
            var enumerator = (IMMDeviceEnumerator)enumObj;
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return null;
            Guid mgr2 = IID_IAudioSessionManager2;
            object mgrObj;
            if (device.Activate(ref mgr2, 1, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) return null;
            return mgrObj as IAudioSessionManager2;
        }

        public static List<AudioSessionInfo> GetAudioSessions()
        {
            var result = new List<AudioSessionInfo>();
            var best = new Dictionary<string, AudioSessionInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var manager = _manager ?? GetManager();
                if (manager == null) return result;

                IAudioSessionEnumerator sessEnum;
                if (manager.GetSessionEnumerator(out sessEnum) != 0 || sessEnum == null) return result;

                int count;
                sessEnum.GetCount(out count);

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        IAudioSessionControl ctrl;
                        if (sessEnum.GetSession(i, out ctrl) != 0 || ctrl == null) continue;

                        var ctrl2 = ctrl as IAudioSessionControl2;
                        if (ctrl2 == null) continue;

                        uint pid;
                        if (ctrl2.GetProcessId(out pid) != 0 || pid == 0) continue;

                        // Skip fully expired sessions
                        int state;
                        ctrl2.GetState(out state);
                        if (state == 2) continue;

                        // Verify process alive
                        string processName = "";
                        string displayName = "";
                        try
                        {
                            var proc = Process.GetProcessById((int)pid);
                            if (proc.HasExited) continue;
                            processName = proc.ProcessName;

                            if (_nameOverrides.ContainsKey(processName))
                                displayName = _nameOverrides[processName];
                            else
                            {
                                ctrl2.GetDisplayName(out displayName);
                                if (string.IsNullOrEmpty(displayName) || displayName.StartsWith("@"))
                                    displayName = !string.IsNullOrEmpty(proc.MainWindowTitle)
                                        ? proc.MainWindowTitle
                                        : proc.ProcessName;
                            }
                        }
                        catch { continue; }

                        if (_blocklist.Contains(processName)) continue;

                        var vol = ctrl as ISimpleAudioVolume;
                        bool muted = false;
                        if (vol != null) vol.GetMute(out muted);

                        // Use peak meter for instant active detection � no Windows debounce lag
                        bool isActive = false;
                        var meter = ctrl as IAudioMeterInformation;
                        if (meter != null)
                        {
                            float peak;
                            if (meter.GetPeakValue(out peak) == 0)
                                isActive = peak > 0.001f;
                        }
                        else
                        {
                            // Fallback to session state if meter not available
                            isActive = (state == 1);
                        }

                        var info = new AudioSessionInfo
                        {
                            ProcessName = processName,
                            DisplayName = displayName,
                            ProcessId = (int)pid,
                            IsMuted = muted,
                            IsActive = isActive,
                            RawSession = ctrl
                        };

                        // Dedup: active wins; higher PID breaks ties
                        if (best.TryGetValue(processName, out var existing))
                        {
                            bool newWins = (isActive && !existing.IsActive) ||
                                          (isActive == existing.IsActive && (int)pid > existing.ProcessId);
                            if (newWins) best[processName] = info;
                        }
                        else
                        {
                            best[processName] = info;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Debug.WriteLine("GetAudioSessions error: " + ex.Message); }

            foreach (var kv in best.Values)
                result.Add(kv);

            return result;
        }

        public static void SetMute(AudioSessionInfo session, bool mute)
        {
            var vol = session.GetVolume();
            if (vol == null) return;
            Guid empty = Guid.Empty;
            vol.SetMute(mute, ref empty);
            session.IsMuted = mute;
        }

        public static bool GetMute(AudioSessionInfo session)
        {
            var vol = session.GetVolume();
            if (vol == null) return session.IsMuted;
            bool m;
            vol.GetMute(out m);
            return m;
        }

        // ── Microphone mute ────────────────────────────────────────────────
        private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        public static bool ToggleMicMute()
        {
            try
            {
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;
                object enumObj;
                if (Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out enumObj) != 0) return false;
                var enumerator = (IMMDeviceEnumerator)enumObj;

                IMMDevice capDevice;
                // dataFlow=1 (eCapture), role=0 (eConsole)
                if (enumerator.GetDefaultAudioEndpoint(1, 0, out capDevice) != 0 || capDevice == null) return false;

                Guid epvId = IID_IAudioEndpointVolume;
                object epvObj;
                if (capDevice.Activate(ref epvId, 1, IntPtr.Zero, out epvObj) != 0 || epvObj == null) return false;

                var epv = (IAudioEndpointVolume)epvObj;
                bool muted;
                epv.GetMute(out muted);
                Guid ctx = Guid.Empty;
                epv.SetMute(!muted, ref ctx);
                return !muted; // returns new mute state
            }
            catch { return false; }
        }

        public static bool GetMicMute()
        {
            try
            {
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;
                object enumObj;
                if (Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out enumObj) != 0) return false;
                var enumerator = (IMMDeviceEnumerator)enumObj;

                IMMDevice capDevice;
                if (enumerator.GetDefaultAudioEndpoint(1, 0, out capDevice) != 0 || capDevice == null) return false;

                Guid epvId = IID_IAudioEndpointVolume;
                object epvObj;
                if (capDevice.Activate(ref epvId, 1, IntPtr.Zero, out epvObj) != 0 || epvObj == null) return false;

                var epv = (IAudioEndpointVolume)epvObj;
                bool muted;
                epv.GetMute(out muted);
                return muted;
            }
            catch { return false; }
        }

        // ── Capture device enumeration ──────────────────────────────────────
        public static List<CaptureDeviceInfo> GetCaptureDevices()
        {
            var result = new List<CaptureDeviceInfo>();
            try
            {
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;
                object enumObj;
                if (Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out enumObj) != 0) return result;
                var enumerator = (IMMDeviceEnumerator)enumObj;

                IMMDeviceCollection devices;
                // dataFlow=1 (eCapture), DEVICE_STATE_ACTIVE=1
                if (enumerator.EnumAudioEndpoints(1, 1, out devices) != 0 || devices == null) return result;

                uint count;
                devices.GetCount(out count);

                for (uint i = 0; i < count; i++)
                {
                    try
                    {
                        IMMDevice device;
                        if (devices.Item(i, out device) != 0 || device == null) continue;

                        string id;
                        device.GetId(out id);

                        string name = id;
                        IPropertyStore store;
                        if (device.OpenPropertyStore(0, out store) == 0 && store != null)
                        {
                            PROPERTYKEY pkey = PropertyKeys.PKEY_Device_FriendlyName;
                            PROPVARIANT pv;
                            if (store.GetValue(ref pkey, out pv) == 0 && pv.vt == 31 && pv.pointerVal != IntPtr.Zero)
                                name = Marshal.PtrToStringUni(pv.pointerVal);
                        }

                        result.Add(new CaptureDeviceInfo { Id = id, Name = name });
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // ── Per-device mic mute ─────────────────────────────────────────────
        public static void SetMicMuteForDevice(string deviceId, bool mute)
        {
            try
            {
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;
                object enumObj;
                if (Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out enumObj) != 0) return;
                var enumerator = (IMMDeviceEnumerator)enumObj;

                IMMDevice device;
                if (enumerator.GetDevice(deviceId, out device) != 0 || device == null) return;

                Guid epvId = IID_IAudioEndpointVolume;
                object epvObj;
                if (device.Activate(ref epvId, 1, IntPtr.Zero, out epvObj) != 0 || epvObj == null) return;

                var epv = (IAudioEndpointVolume)epvObj;
                Guid ctx = Guid.Empty;
                epv.SetMute(mute, ref ctx);
            }
            catch { }
        }

        public static bool GetMicMuteForDevice(string deviceId)
        {
            try
            {
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;
                object enumObj;
                if (Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out enumObj) != 0) return false;
                var enumerator = (IMMDeviceEnumerator)enumObj;

                IMMDevice device;
                if (enumerator.GetDevice(deviceId, out device) != 0 || device == null) return false;

                Guid epvId = IID_IAudioEndpointVolume;
                object epvObj;
                if (device.Activate(ref epvId, 1, IntPtr.Zero, out epvObj) != 0 || epvObj == null) return false;

                var epv = (IAudioEndpointVolume)epvObj;
                bool muted;
                epv.GetMute(out muted);
                return muted;
            }
            catch { return false; }
        }
    }
}