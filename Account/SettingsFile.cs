using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Account
{
    /// <summary>Changes a few values in an emulator's settings file, leaving the rest as it was.</summary>
    internal static class SettingsFile
    {
        private sealed class Text
        {
            public List<string> Lines;
            public string NewLine;
            public bool Bom;
            public bool TrailingNewLine;
        }

        private static Text Read(string path)
        {
            byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : new byte[0];
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string content = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            string newLine = content.Contains("\r\n") ? "\r\n" : content.Contains("\n") ? "\n" : "\r\n";
            var lines = content.Length == 0 ? new List<string>() : content.Replace("\r\n", "\n").Split('\n').ToList();
            bool trailing = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
            if (trailing)
                lines.RemoveAt(lines.Count - 1);
            return new Text { Lines = lines, NewLine = newLine, Bom = bom, TrailingNewLine = trailing || lines.Count == 0 };
        }

        private static void Write(string path, Text text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string content = string.Join(text.NewLine, text.Lines) + (text.TrailingNewLine ? text.NewLine : "");
            File.WriteAllText(path, content, new UTF8Encoding(text.Bom));
        }

        private static string KeyOf(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || trimmed[0] == '[')
                return null;
            int equals = trimmed.IndexOf('=');
            return equals <= 0 ? null : trimmed.Substring(0, equals).Trim();
        }

        /// <summary>
        /// Sets keys in an ini section ("key = value"); a null value removes the key. The section
        /// is added when missing. <paramref name="keepIfOnlyStamp"/> names a key (a timestamp) not
        /// rewritten when nothing else changed, so the file is not touched on every launch.
        /// Whether the file changed.
        /// </summary>
        public static bool SetIni(string path, string section, IDictionary<string, string> values, bool create = false, string keepIfOnlyStamp = null, string separator = " = ")
        {
            if (!File.Exists(path) && !create)
                return false;
            Text text = Read(path);
            List<string> lines = text.Lines;

            int start = lines.FindIndex(l => string.Equals(l.Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (values.Values.All(v => v == null))
                    return false;
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                    lines.Add("");
                lines.Add("[" + section + "]");
                start = lines.Count - 1;
            }
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (end < 0)
                end = lines.Count;

            // What would change, ignoring the timestamp.
            var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            var present = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = start + 1; i < end; i++)
            {
                string key = KeyOf(lines[i]);
                if (key != null && pending.ContainsKey(key) && !present.ContainsKey(key))
                    present[key] = i;
            }
            bool differs = pending.Any(p =>
                !string.Equals(p.Key, keepIfOnlyStamp, StringComparison.OrdinalIgnoreCase)
                && (present.TryGetValue(p.Key, out int line)
                    ? p.Value == null || ValueOf(lines[line]) != p.Value
                    : p.Value != null));
            if (!differs)
                return false;

            var removals = new List<int>();
            foreach (KeyValuePair<string, string> kv in pending)
            {
                if (present.TryGetValue(kv.Key, out int at))
                {
                    if (kv.Value == null)
                        removals.Add(at);
                    else
                        lines[at] = kv.Key + separator + kv.Value;
                }
            }
            // New keys go after the section's last setting.
            int insertAt = end;
            while (insertAt - 1 > start && lines[insertAt - 1].Trim().Length == 0)
                insertAt--;
            foreach (KeyValuePair<string, string> kv in pending.Where(p => p.Value != null && !present.ContainsKey(p.Key)))
                lines.Insert(insertAt++, kv.Key + separator + kv.Value);
            foreach (int at in removals.OrderByDescending(i => i))
                lines.RemoveAt(at);

            Write(path, text);
            return true;
        }

        private static string ValueOf(string line)
        {
            int equals = line.IndexOf('=');
            return equals < 0 ? null : line.Substring(equals + 1).Trim();
        }

        /// <summary>Sets keys in a RetroArch-style config (key = "value", no sections). Whether it changed.</summary>
        public static bool SetCfg(string path, IDictionary<string, string> values)
        {
            Text text = Read(path);
            List<string> lines = text.Lines;
            var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            for (int i = 0; i < lines.Count && pending.Count > 0; i++)
            {
                string key = KeyOf(lines[i]);
                if (key == null || !pending.TryGetValue(key, out string value))
                    continue;
                string wanted = key + " = \"" + value + "\"";
                if (lines[i] != wanted)
                {
                    lines[i] = wanted;
                    changed = true;
                }
                pending.Remove(key);
            }
            foreach (KeyValuePair<string, string> kv in pending)
            {
                lines.Add(kv.Key + " = \"" + kv.Value + "\"");
                changed = true;
            }
            if (changed)
                Write(path, text);
            return changed;
        }
    }
}
