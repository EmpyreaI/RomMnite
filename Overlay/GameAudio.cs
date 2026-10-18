using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// The running game's own volume and mute, as Windows' volume mixer has them per program
    /// (Core Audio: every audio session of the game's processes, on every output device), so it
    /// can be turned down or muted from the overlay without touching anything else on the PC.
    ///
    /// Windows keeps a program's volume by its program file, which for an emulated game is the
    /// emulator, shared by all its games. So the setting is kept here per game (GameVolume.json in
    /// the plugin's data) and put on the game's sessions as they appear when it starts: its own
    /// setting, else full volume and not muted, so one game's choice does not follow the
    /// emulator to the next.
    /// </summary>
    internal sealed class GameAudio
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private sealed class Setting
        {
            public float Volume { get; set; } = 1f;
            public bool Muted { get; set; }
        }

        private readonly string _file;
        private readonly object _lock = new object();
        private Dictionary<Guid, Setting> _settings;
        private Func<int, bool> _isGame;
        private Guid _gameId;
        private int _applyRun;

        public GameAudio(string dataFolder)
        {
            _file = Path.Combine(dataFolder, "GameVolume.json");
        }

        /// <summary>
        /// A game has started: its processes are those <paramref name="isGame"/> accepts (by
        /// process id). Its setting is put on its audio sessions as they appear (a game may take
        /// a while to start its sound), for the first two minutes.
        /// </summary>
        public void Start(Guid gameId, Func<int, bool> isGame)
        {
            int run = Interlocked.Increment(ref _applyRun);
            _gameId = gameId;
            _isGame = isGame;
            Setting wanted = Get(gameId);
            new Thread(() =>
            {
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime until = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < until && Volatile.Read(ref _applyRun) == run)
                {
                    try
                    {
                        ForEachSession(isGame, (id, volume) =>
                        {
                            if (!done.Add(id))
                                return;
                            Guid context = Guid.Empty;
                            volume.SetMasterVolume(wanted.Volume, ref context);
                            volume.SetMute(wanted.Muted, ref context);
                        });
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Overlay] Could not set the game's volume: {ex.Message}");
                        return;
                    }
                    Thread.Sleep(1000);
                }
            }) { IsBackground = true, Name = "RomM game volume" }.Start();
        }

        /// <summary>The game has stopped.</summary>
        public void Stop()
        {
            Interlocked.Increment(ref _applyRun);
            _isGame = null;
            _gameId = Guid.Empty;
        }

        /// <summary>The running game's volume (0-1), as last set for it.</summary>
        public float Volume => Get(_gameId).Volume;

        /// <summary>Whether the running game is muted, as last set for it.</summary>
        public bool Muted => Get(_gameId).Muted;

        /// <summary>Sets the running game's volume (0-1), kept for the game.</summary>
        public void SetVolume(float volume)
        {
            volume = Math.Max(0f, Math.Min(1f, volume));
            Update(s => s.Volume = volume);
            Apply((v, context) => v.SetMasterVolume(volume, ref context));
        }

        /// <summary>Mutes or unmutes the running game, kept for the game.</summary>
        public void SetMuted(bool muted)
        {
            Update(s => s.Muted = muted);
            Apply((v, context) => v.SetMute(muted, ref context));
        }

        private delegate void VolumeChange(ISimpleAudioVolume volume, Guid context);

        private void Apply(VolumeChange change)
        {
            Func<int, bool> isGame = _isGame;
            if (isGame == null)
                return;
            try
            {
                ForEachSession(isGame, (id, volume) => change(volume, Guid.Empty));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Overlay] Could not change the game's volume: {ex.Message}");
            }
        }

        // ---- Kept per game ---------------------------------------------------------------------

        private Setting Get(Guid gameId)
        {
            lock (_lock)
            {
                Load();
                return _settings.TryGetValue(gameId, out Setting setting)
                    ? new Setting { Volume = setting.Volume, Muted = setting.Muted }
                    : new Setting();
            }
        }

        private void Update(Action<Setting> change)
        {
            if (_gameId == Guid.Empty)
                return;
            lock (_lock)
            {
                Load();
                if (!_settings.TryGetValue(_gameId, out Setting setting))
                    _settings[_gameId] = setting = new Setting();
                change(setting);
                // Full volume and not muted is the default: not kept.
                if (setting.Volume >= 0.999f && !setting.Muted)
                    _settings.Remove(_gameId);
                try
                {
                    File.WriteAllText(_file, JsonConvert.SerializeObject(_settings));
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Overlay] Could not keep the game's volume: {ex.Message}");
                }
            }
        }

        private void Load()
        {
            if (_settings != null)
                return;
            try
            {
                if (File.Exists(_file))
                    _settings = JsonConvert.DeserializeObject<Dictionary<Guid, Setting>>(File.ReadAllText(_file));
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overlay] Could not read the kept game volumes: {ex.Message}");
            }
            _settings = _settings ?? new Dictionary<Guid, Setting>();
        }

        // ---- Core Audio ------------------------------------------------------------------------

        // Every audio session of the game's processes on every active output device: its
        // instance id (to tell it apart) and its volume control.
        private static void ForEachSession(Func<int, bool> isGame, Action<string, ISimpleAudioVolume> each)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection devices = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out devices));
                Marshal.ThrowExceptionForHR(devices.GetCount(out uint count));
                for (uint d = 0; d < count; d++)
                {
                    IMMDevice device = null;
                    IAudioSessionManager2 manager = null;
                    IAudioSessionEnumerator sessions = null;
                    try
                    {
                        if (devices.Item(d, out device) != 0)
                            continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object activated) != 0)
                            continue;
                        manager = (IAudioSessionManager2)activated;
                        if (manager.GetSessionEnumerator(out sessions) != 0)
                            continue;
                        sessions.GetCount(out int sessionCount);
                        for (int i = 0; i < sessionCount; i++)
                        {
                            IAudioSessionControl control = null;
                            try
                            {
                                if (sessions.GetSession(i, out control) != 0)
                                    continue;
                                var control2 = control as IAudioSessionControl2;
                                if (control2 == null || control2.GetProcessId(out uint pid) != 0 || pid == 0 || !isGame((int)pid))
                                    continue;
                                control2.GetSessionInstanceIdentifier(out string id);
                                if (control is ISimpleAudioVolume volume)
                                    each(id ?? $"{d}/{i}", volume);
                            }
                            finally
                            {
                                Release(control);
                            }
                        }
                    }
                    finally
                    {
                        Release(sessions);
                        Release(manager);
                        Release(device);
                    }
                }
            }
            finally
            {
                Release(devices);
                Release(enumerator);
            }
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
                Marshal.ReleaseComObject(com);
        }

        private const uint DEVICE_STATE_ACTIVE = 0x1;
        private const int CLSCTX_ALL = 0x17;

        private enum EDataFlow { Render = 0, Capture = 1, All = 2 }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            // IAudioSessionManager
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IAudioSessionControl control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out ISimpleAudioVolume volume);
            // IAudioSessionManager2
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            [PreserveSig] int RegisterSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
            [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
