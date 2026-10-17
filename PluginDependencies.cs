using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RomM
{
    /// <summary>
    /// The extension's own copies of the libraries Playnite also ships (SharpCompress,
    /// System.Memory, ...). Playnite's Toolbox leaves every file named like one of Playnite's
    /// own out of the top of the .pext (its ExtensionsRefIgnoreList.txt), so the extension
    /// would get Playnite's copies, of other versions, which .NET refuses ("Could not load file
    /// or assembly 'SharpCompress, Version=0.36.0.0'"). The build also puts every dependency in
    /// lib\ (RomMnite.csproj, CopyDependenciesToLib), which the Toolbox keeps; when .NET cannot find
    /// a library, it is loaded from there.
    /// </summary>
    internal static class PluginDependencies
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered)
                return;
            s_registered = true;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var wanted = new AssemblyName(args.Name);
                string folder = Path.GetDirectoryName(typeof(PluginDependencies).Assembly.Location);
                if (string.IsNullOrEmpty(folder))
                    return null;
                string file = Path.Combine(folder, "lib", wanted.Name + ".dll");
                if (!File.Exists(file))
                    return null;

                AssemblyName offered = AssemblyName.GetAssemblyName(file);
                if (wanted.Version != null && offered.Version != null && offered.Version < wanted.Version)
                    return null;

                // Already loaded from there (another request for the same library).
                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                {
                    try { return !a.IsDynamic && string.Equals(a.Location, file, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
                return loaded ?? Assembly.LoadFrom(file);
            }
            catch
            {
                return null;
            }
        }
    }
}
