using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewValley;

namespace HeadlessServer
{
    /// <summary>
    /// Keeps the world save slots beside the server deployment instead of the
    /// OS-specific per-user app-data folder the vanilla game hard-codes, so a
    /// server can be moved or shipped to a cloud box (where the service
    /// account's home directory is not where saves should live) as one
    /// self-contained directory tree.
    ///
    /// <see cref="StardewValley.Program.GetSavesFolder"/> in 1.6.15 is a pure
    /// computation over <c>Environment.GetFolderPath(ApplicationData)</c> with
    /// no settable field or cache, and the whole vanilla save pipeline (the
    /// save enumerator, the temp-file rename dance, slot existence probes, the
    /// load enumerator) resolves its paths through that one method. The folder
    /// is therefore redirected with a Harmony prefix detour - the same
    /// mechanism host mods use. When
    /// <see cref="ServerConfig.PathsSection.WorldSavesFolder"/> is empty the
    /// original method runs untouched and behaviour is exactly vanilla.
    /// </summary>
    internal static class HeadlessSaveFolder
    {
        /// <summary>
        /// Absolute folder <c>GetSavesFolder()</c> is pinned to, or null when
        /// the vanilla behaviour should run.
        /// </summary>
        private static string? overrideFolder;

        /// <summary>
        /// The folder the game would use without the detour, captured before
        /// patching. Used by the shadowed-slots guard and by log lines so the
        /// operator can see both locations.
        /// </summary>
        public static string DefaultSavesFolder { get; private set; } = "";

        /// <summary>True when the detour is active.</summary>
        public static bool IsOverrideActive => overrideFolder != null;

        /// <summary>
        /// Applies <see cref="ServerConfig.PathsSection.WorldSavesFolder"/>.
        /// Must run before the first save or load: the vanilla load pipeline
        /// and every helper resolve the folder lazily on each call, so
        /// installing the detour early covers them all.
        /// </summary>
        public static void Install()
        {
            // Capture the un-redirected folder first: the guard below compares
            // candidate slots in both folders, and log lines report both.
            try
            {
                DefaultSavesFolder = StardewValley.Program.GetSavesFolder();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessSave] Could not query the game's default saves folder: {ex.Message}");
            }

            string configured = ServerConfig.Current.Paths.WorldSavesFolder;
            if (string.IsNullOrWhiteSpace(configured))
            {
                Console.WriteLine($"[HeadlessSave] Paths.WorldSavesFolder is empty; using the game's own saves folder: {DefaultSavesFolder}");
                return;
            }

            string folder = Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
            Directory.CreateDirectory(folder);
            overrideFolder = folder;

            MethodInfo? original = typeof(StardewValley.Program).GetMethod(
                "GetSavesFolder", BindingFlags.Public | BindingFlags.Static);
            if (original == null)
            {
                // Never run half-configured: without the detour the game would
                // read and write the default folder while the operator believes
                // saves live beside the server. Fail loudly instead.
                Console.WriteLine("[HeadlessSave] !! StardewValley.Program.GetSavesFolder was not found in the loaded game assembly.");
                Console.WriteLine("[HeadlessSave] !! The saves folder cannot be redirected; refusing to start so no save");
                Console.WriteLine("[HeadlessSave] !! is read from or written to an unintended location.");
                Environment.Exit(1);
            }

            try
            {
                new Harmony("ValleyServer.HeadlessSaveFolder").Patch(
                    original,
                    prefix: new HarmonyMethod(typeof(HeadlessSaveFolder), nameof(GetSavesFolderPrefix)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessSave] !! Installing the saves-folder detour failed: {ex}");
                Console.WriteLine("[HeadlessSave] !! Refusing to start rather than reading and writing the wrong folder.");
                Environment.Exit(1);
            }

            Console.WriteLine($"[HeadlessSave] World saves folder override active: {overrideFolder}");
            Console.WriteLine($"[HeadlessSave] (Without it the game would use: {DefaultSavesFolder})");

            EnsureNoShadowedSlots();
        }

        /// <summary>Harmony prefix for <c>StardewValley.Program.GetSavesFolder</c>.</summary>
        private static bool GetSavesFolderPrefix(ref string __result)
        {
            if (overrideFolder == null)
                return true; // run the original
            __result = overrideFolder;
            return false; // skip the original
        }

        /// <summary>
        /// Refuses to start when the redirected folder holds no slot for the
        /// configured farm name while the game's default folder does. Starting
        /// anyway would build a fresh world that later saves under the same
        /// farm name, silently shadowing the operator's existing progress -
        /// the exact data-loss-shaped surprise this server exists to prevent.
        /// Copying the slot folders across (or pointing
        /// <see cref="ServerConfig.PathsSection.WorldSavesFolder"/> at the
        /// default folder) resolves it.
        /// </summary>
        private static void EnsureNoShadowedSlots()
        {
            string[] redirected = FindCandidateSlots(overrideFolder!);
            if (redirected.Length > 0)
                return;

            string[] defaults = FindCandidateSlots(DefaultSavesFolder);
            if (defaults.Length == 0)
                return; // nothing anywhere: a genuine first run on a fresh box.

            Console.WriteLine("[HeadlessSave] !! The redirected world saves folder has no slot for the configured farm name,");
            Console.WriteLine($"[HeadlessSave] !! but the game's default saves folder has {defaults.Length}:");
            foreach (string name in defaults)
                Console.WriteLine($"[HeadlessSave] !!   {name}");
            Console.WriteLine("[HeadlessSave] !! Starting now would create a fresh world and later save it under the same farm");
            Console.WriteLine("[HeadlessSave] !! name, shadowing the slots above. Refusing to start. Either copy the slot");
            Console.WriteLine($"[HeadlessSave] !! folders into '{overrideFolder}', or set Paths.WorldSavesFolder to ''");
            Console.WriteLine("[HeadlessSave] !! (or to the default folder) in config.json, then start again.");
            Environment.Exit(1);
        }

        /// <summary>
        /// Names the slot folders under <paramref name="savesFolder"/> that
        /// match the configured farm name and hold a main save file, mirroring
        /// the candidate filter the load path uses.
        /// </summary>
        private static string[] FindCandidateSlots(string savesFolder)
        {
            try
            {
                string prefix = SaveGame.FilterFileName(ServerConfig.Current.World.FarmName) + "_";
                return new DirectoryInfo(savesFolder)
                    .GetDirectories(prefix + "*")
                    .Where(d => !d.Name.Contains("_unloadable_", StringComparison.OrdinalIgnoreCase))
                    .Where(d => File.Exists(Path.Combine(d.FullName, d.Name)))
                    .OrderByDescending(d => d.LastWriteTime)
                    .Select(d => d.Name)
                    .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
