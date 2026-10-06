#pragma warning disable SYSLIB0050

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using StardewValley;

namespace HeadlessServer
{
    public class HeadlessContentManager : LocalizedContentManager
    {
        public static string ResolvedContentRoot { get; private set; } = "";

        public HeadlessContentManager(IServiceProvider serviceProvider, string rootDirectory)
            : base(serviceProvider, rootDirectory)
        {
            // Content is not part of the repository. Resolve it from the configured path
            // first, then from the published directory and the common Steam locations.
            // ConfigLoader has already merged the VALLEY_CONTENT_PATH environment
            // variable over config.json, so the configured value is authoritative here.
            string? configuredPath = ServerConfig.Current.Paths.ContentPath;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string?[] possiblePaths = new[]
            {
                configuredPath,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content"),
                Path.Combine(Environment.CurrentDirectory, "Content"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Stardew Valley", "Content"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StardewValley", "Content"),
                Path.Combine(programFiles, "Steam", "steamapps", "common", "Stardew Valley", "Content"),
                Path.Combine(programFilesX86, "Steam", "steamapps", "common", "Stardew Valley", "Content")
            };

            foreach (string? path in possiblePaths)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    _CachedContentRoot = Path.GetFullPath(path);
                    ResolvedContentRoot = _CachedContentRoot;
                    break;
                }
            }

            if (string.IsNullOrEmpty(_CachedContentRoot))
            {
                // Keep the conventional path so the eventual load error names the expected
                // location, but report the actionable configuration knob to the operator.
                _CachedContentRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content"));
                ResolvedContentRoot = _CachedContentRoot;
                Console.WriteLine("[HeadlessContentManager] Content not found. Set Paths.ContentPath in config.json (or VALLEY_CONTENT_PATH), or install Content beside the server.");
            }

            Console.WriteLine($"[HeadlessContentManager] Content path resolved to: {_CachedContentRoot}");
        }

        public override LocalizedContentManager CreateTemporary()
        {
            return new HeadlessContentManager(base.ServiceProvider, base.RootDirectory);
        }

        private string ResolveAssetPath(string assetName)
        {
            string suffix = assetName;
            if (suffix.StartsWith("Content/", StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("Content\\", StringComparison.OrdinalIgnoreCase))
            {
                suffix = suffix.Substring(8);
            }

            // Game asset names are Windows-style even when the server runs on Linux.
            // A backslash is a legal filename character on Unix, so Path.Combine alone
            // would look for e.g. "Content/Data\\Objects" instead of "Data/Objects.xnb".
            suffix = suffix.Replace('\\', Path.DirectorySeparatorChar)
                           .Replace('/', Path.DirectorySeparatorChar)
                           .TrimStart(Path.DirectorySeparatorChar);

            string path = Path.GetFullPath(Path.Combine(_CachedContentRoot, suffix));
            string root = Path.GetFullPath(_CachedContentRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!path.StartsWith(root, pathComparison))
            {
                throw new ContentLoadException($"Asset path escapes the Content directory: '{assetName}'.");
            }

            if (!File.Exists(path) && File.Exists(path + ".xnb"))
            {
                path += ".xnb";
            }

            return path;
        }

        protected override Stream OpenStream(string assetName)
        {
            return File.OpenRead(ResolveAssetPath(assetName));
        }

        private static T CreateHeadlessAsset<T>()
        {
            object asset = FormatterServices.GetUninitializedObject(typeof(T));
            if (asset is Microsoft.Xna.Framework.Graphics.Texture2D texture)
            {
                // Texture2D.Bounds is queried by gameplay objects (e.g. Bush) even
                // when nothing is rendered. Give the inert texture valid dimensions.
                typeof(Microsoft.Xna.Framework.Graphics.Texture2D).GetField("width", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(texture, 1280);
                typeof(Microsoft.Xna.Framework.Graphics.Texture2D).GetField("height", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(texture, 1280);
            }
            return (T)asset;
        }

        private static Microsoft.Xna.Framework.Graphics.SpriteFont? _inertSpriteFont;

        private static Microsoft.Xna.Framework.Graphics.SpriteFont GetInertSpriteFont()
        {
            if (_inertSpriteFont == null)
            {
                // Menus built on the headless server (e.g. ReadyCheckDialog via
                // DedicatedServer.HostSleepInBed -> startSleep) call MeasureString and
                // parseText before anything would ever be rendered; an uninitialized
                // SpriteFont made those constructors throw NullReferenceException.
                // A real font with a single blank 8x16 glyph satisfies every query.
                var glyphBounds = new List<Microsoft.Xna.Framework.Rectangle> { new Microsoft.Xna.Framework.Rectangle(0, 0, 8, 16) };
                var cropping = new List<Microsoft.Xna.Framework.Rectangle> { new Microsoft.Xna.Framework.Rectangle(0, 0, 0, 0) };
                var characters = new List<char> { ' ' };
                var kerning = new List<Microsoft.Xna.Framework.Vector3> { new Microsoft.Xna.Framework.Vector3(0f, 8f, 0f) };
                _inertSpriteFont = new Microsoft.Xna.Framework.Graphics.SpriteFont(
                    CreateHeadlessAsset<Microsoft.Xna.Framework.Graphics.Texture2D>(),
                    glyphBounds,
                    cropping,
                    characters,
                    16,
                    0f,
                    kerning,
                    ' ');
            }
            return _inertSpriteFont;
        }

        public override T Load<T>(string assetName)
        {
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.Texture2D))
            {
                return CreateHeadlessAsset<T>();
            }
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.SpriteFont))
            {
                return (T)(object)GetInertSpriteFont();
            }
            return base.Load<T>(assetName);
        }

        public override T Load<T>(string assetName, LanguageCode language)
        {
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.Texture2D))
            {
                return CreateHeadlessAsset<T>();
            }
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.SpriteFont))
            {
                return (T)(object)GetInertSpriteFont();
            }
            return base.Load<T>(assetName, language);
        }

        public override T LoadImpl<T>(string baseAssetName, string localizedAssetName, LanguageCode languageCode)
        {
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.Texture2D))
            {
                return CreateHeadlessAsset<T>();
            }
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.SpriteFont))
            {
                return (T)(object)GetInertSpriteFont();
            }
            return base.LoadImpl<T>(baseAssetName, localizedAssetName, languageCode);
        }

        public override bool DoesAssetExist<T>(string assetName)
        {
            if (typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.Texture2D) ||
                typeof(T) == typeof(Microsoft.Xna.Framework.Graphics.SpriteFont))
            {
                return true;
            }

            try
            {
                if (base.DoesAssetExist<T>(assetName))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                return File.Exists(ResolveAssetPath(assetName));
            }
            catch (ContentLoadException)
            {
                return false;
            }
        }
    }
}
