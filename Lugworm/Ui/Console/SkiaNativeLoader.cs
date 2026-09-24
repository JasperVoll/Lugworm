using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lugworm.Ui.Console
{
    /// <summary>Loads <c>libSkiaSharp</c> from the plugin's OWN folder.</summary>
    /// <remarks>
    /// <para>Shipping the native next to the assembly is not enough inside Rhino.
    /// Grasshopper loads a <c>.gha</c> into its own load context, but a
    /// <c>[DllImport("libSkiaSharp")]</c> issued from <c>SkiaSharp.dll</c> still resolves
    /// through the ordinary OS search order - the process directory (Rhino's own folder),
    /// System32, then <c>PATH</c>. The directory the plugin happens to live in is not on
    /// that list, so the native is invisible and every Skia call fails with
    /// <c>DllNotFoundException</c>.</para>
    /// <para>This also explains the stray pickup seen during the spike, where the process
    /// loaded a foreign <c>libSkiaSharp.dll</c> from Git's <c>mingw64\bin</c>: resolution
    /// was already falling through to <c>PATH</c>. Binding the native explicitly closes
    /// both problems - the right library is found, and no unrelated one can win.</para>
    /// <para>Must run before ANY SkiaSharp type is touched, because the failure happens at
    /// the first P/Invoke.</para>
    /// </remarks>
    internal static class SkiaNativeLoader
    {
        const string NativeName = "libSkiaSharp";

        static readonly object Gate = new object();
        static bool _attempted;
        static string? _failure;

        /// <summary>Null when the native is available, otherwise why it is not - including
        /// where we looked, because "missing or unusable" on its own is not actionable.</summary>
        internal static string? Failure { get { lock (Gate) { Ensure(); return _failure; } } }

        internal static void Ensure()
        {
            lock (Gate)
            {
                if (_attempted) return;
                _attempted = true;
                try { _failure = Bind(); }
                catch (Exception ex) { _failure = ex.Message; }
            }
        }

        static string? Bind()
        {
            var candidates = Candidates();
            if (candidates.Count == 0) return "could not determine the plugin folder";

            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;

                // Check the PE machine type BEFORE loading. A 32-bit native in a 64-bit
                // process throws BadImageFormatException from deep inside the first Skia
                // call, which reads as "Skia is broken" rather than "the build copied the
                // wrong architecture" - which is exactly the mistake that shipped once,
                // when all three RID copies were selected by file name alone.
                string? machine = PeMachine(path);
                string expected = IntPtr.Size == 8 ? "x64" : "x86";
                if (machine != null && machine != expected)
                    return path + " is " + machine + " but this process is " + expected
                         + " - the build selected the wrong native architecture";

                if (TryLoad(path, out string? error))
                {
                    Register(path);
                    return null;
                }
                // Present but unloadable: a truncated copy, or a missing VC++ runtime.
                return "found " + path + " but it would not load: " + error;
            }
            return "libSkiaSharp.dll not found next to the plugin (looked in: "
                 + string.Join(", ", candidates) + ")";
        }

        /// <summary>PE machine type of a file, or null when it cannot be read as one.
        /// arm64 is reported as-is so the message names what was actually found.</summary>
        static string? PeMachine(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (br.ReadUInt16() != 0x5A4D) return null;      // "MZ"
                    fs.Position = 0x3C;
                    int peOffset = br.ReadInt32();
                    if (peOffset <= 0 || peOffset + 6 > fs.Length) return null;
                    fs.Position = peOffset;
                    if (br.ReadUInt32() != 0x00004550) return null;  // "PE\0\0"
                    switch (br.ReadUInt16())
                    {
                        case 0x014C: return "x86";
                        case 0x8664: return "x64";
                        case 0xAA64: return "arm64";
                        default: return null;
                    }
                }
            }
            // A file we cannot read as a PE image has an UNKNOWN architecture, which is
            // exactly what null means to the caller - it is never read as "matches".
            // Deliberately broad: the reads above can fail as IO, sharing, access or
            // end-of-stream faults and every one of them means the same thing here.
            catch (Exception) { return null; }
        }

        /// <summary>Where the build puts the native, in the order the loaders expect it:
        /// the assembly root for the netcore folders, <c>x64\</c> for net48.</summary>
        static List<string> Candidates()
        {
            var dirs = new List<string>();
            try
            {
                string? asmDir = Path.GetDirectoryName(typeof(SkiaNativeLoader).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir)) dirs.Add(asmDir!);
            }
            catch (Exception) { /* single-file or dynamic - fall back below */ }
            try
            {
                string baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir) && !dirs.Contains(baseDir)) dirs.Add(baseDir);
            }
            // best effort: one more candidate folder. A host that will not answer for its
            // base directory just leaves the list shorter, and Ensure() reports "could not
            // determine the plugin folder" when nothing at all was found.
            catch (Exception) { }

            var paths = new List<string>();
            foreach (string dir in dirs)
            {
                paths.Add(Path.Combine(dir, NativeName + ".dll"));
                paths.Add(Path.Combine(dir, "x64", NativeName + ".dll"));
            }
            return paths;
        }

#if NETFRAMEWORK
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryW(string path);

        static bool TryLoad(string path, out string? error)
        {
            IntPtr handle = LoadLibraryW(path);
            if (handle != IntPtr.Zero) { error = null; return true; }
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        /// <summary>On .NET Framework there is no resolver hook, but none is needed: the
        /// module is already in the process under the name the DllImport asks for, so the
        /// loader binds to it instead of searching.</summary>
        static void Register(string path) { }
#else
        static IntPtr _handle;

        static bool TryLoad(string path, out string? error)
        {
            try
            {
                if (NativeLibrary.TryLoad(path, out _handle)) { error = null; return true; }
                error = "TryLoad returned false";
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Point SkiaSharp's own P/Invokes at the module we just loaded. Without
        /// this the runtime would still search the default locations and miss it.</summary>
        static void Register(string path)
        {
            try
            {
                var skia = typeof(SkiaSharp.SKBitmap).Assembly;
                NativeLibrary.SetDllImportResolver(skia, (name, assembly, searchPath) =>
                    string.Equals(name, NativeName, StringComparison.OrdinalIgnoreCase)
                        ? _handle
                        : IntPtr.Zero);   // anything else keeps the default resolution
            }
            catch (InvalidOperationException)
            {
                // A resolver is already registered for that assembly (this ran twice, or
                // another plugin got there first). The module is loaded either way.
            }
        }
#endif
    }
}
