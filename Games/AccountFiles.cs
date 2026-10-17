using Playnite.SDK;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RomM.Games
{
    /// <summary>
    /// Where an account's own files are kept (its collections, shelves, sync state): a folder
    /// per account, named from its server and user name.
    /// </summary>
    public static class AccountFiles
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public static string Folder(string dataFolder, string accountKey)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(accountKey ?? ""));
                var name = new StringBuilder();
                for (int i = 0; i < 8; i++)
                    name.Append(hash[i].ToString("x2"));
                return Path.Combine(dataFolder, "Accounts", name.ToString());
            }
        }

        /// <summary>
        /// The first account to have files takes the ones kept before accounts had their own
        /// (moved, so no second account takes them too).
        /// </summary>
        public static void Adopt(string path, string legacyPath)
        {
            try
            {
                if (string.IsNullOrEmpty(legacyPath) || File.Exists(path) || !File.Exists(legacyPath))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Move(legacyPath, path);
                s_log.Info($"[Accounts] {Path.GetFileName(legacyPath)} now belongs to the signed-in account.");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not move {legacyPath} to the account: {ex.Message}");
            }
        }
    }
}
