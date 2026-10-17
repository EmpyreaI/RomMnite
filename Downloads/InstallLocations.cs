using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RomM.Downloads
{
    /// <summary>
    /// Folders games can be installed to besides the default (each platform mapping's own
    /// folder), as Steam's library folders: on another drive, say. A game installed to one goes
    /// to &lt;folder&gt;\&lt;platform folder&gt;\&lt;game&gt;, laid out as under the default. Kept on
    /// disk, in the order added.
    /// </summary>
    public class InstallLocations
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private readonly string _path;
        private readonly object _lock = new object();
        private List<string> _folders;

        public InstallLocations(string path)
        {
            _path = path;
        }

        public List<string> All()
        {
            lock (_lock)
                return Load().ToList();
        }

        public void Add(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;
            lock (_lock)
            {
                string full = Path.GetFullPath(folder).TrimEnd('\\', '/');
                if (Load().Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
                    return;
                _folders.Add(full);
                Save();
            }
        }

        public void Remove(string folder)
        {
            lock (_lock)
            {
                if (Load().RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) > 0)
                    Save();
            }
        }

        private List<string> Load()
        {
            if (_folders != null)
                return _folders;
            try
            {
                _folders = File.Exists(_path) ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(_path)) ?? new List<string>() : new List<string>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Install] Could not read the install locations: {ex.Message}");
                _folders = new List<string>();
            }
            return _folders;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_folders));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Install] Could not keep the install locations: {ex.Message}");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

        /// <summary>
        /// The free space (for this user) and size of the drive or share a folder is on, asked of
        /// its nearest folder that exists (it may not be made yet). False when it cannot be told.
        /// </summary>
        public static bool Space(string folder, out ulong free, out ulong total)
        {
            free = total = 0;
            try
            {
                string probe = string.IsNullOrEmpty(folder) ? null : Path.GetFullPath(folder);
                while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                    probe = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(probe))
                    return false;
                if (!probe.EndsWith("\\"))
                    probe += "\\";
                return GetDiskFreeSpaceEx(probe, out free, out total, out ulong _);
            }
            catch
            {
                return false;
            }
        }
    }
}
