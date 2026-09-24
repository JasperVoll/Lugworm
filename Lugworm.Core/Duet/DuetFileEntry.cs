using System;
using System.Collections.Generic;

namespace Lugworm.Core.Duet
{
    /// <summary>One entry of an rr_filelist directory listing.</summary>
    public sealed class DuetFileEntry
    {
        public string Name = "";
        public long Size;
        public bool IsDirectory;

        /// <summary>Returns whether a board path is inside a protected firmware
        /// area. This is deliberately pure and canonicalizes before comparing, so
        /// traversal and separator/case variants cannot bypass the guard.</summary>
        public static bool IsProtected(string path)
        {
            string? canonical = Canonicalize(path);
            if (canonical == null) return false;
            return string.Equals(canonical, "0:/sys", StringComparison.OrdinalIgnoreCase)
                || canonical.StartsWith("0:/sys/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(canonical, "0:/firmware", StringComparison.OrdinalIgnoreCase)
                || canonical.StartsWith("0:/firmware/", StringComparison.OrdinalIgnoreCase);
        }

        static string? Canonicalize(string path)
        {
            string p = (path ?? "").Trim().Replace('\\', '/');
            if (p.Length == 0) return null;
            string drive;
            if (p.Length >= 2 && p[1] == ':')
            {
                if (!char.IsDigit(p[0])) return null;
                drive = p.Substring(0, 2);
                p = p.Substring(2);
            }
            else
            {
                drive = "0:";
            }

            var parts = new List<string>();
            foreach (string part in p.TrimStart('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(part);
            }
            return drive + "/" + string.Join("/", parts);
        }

        public override string ToString()
            => IsDirectory ? Name + "/" : $"{Name}  ({Size:#,0} B)";
    }

    /// <summary>Pure path arithmetic for the board's SD paths ("0:/gcodes/x.gcode").
    /// Kept out of the UI so the move-target rules are unit-tested.</summary>
    public static class DuetFilePath
    {
        public static string Normalize(string path)
        {
            string p = (path ?? "").Trim().Replace('\\', '/');
            if (p.Length < 2 || !char.IsDigit(p[0]) || p[1] != ':')
                throw new ArgumentException($"path must be an SD path like 0:/gcodes (got '{path}')");
            if (p.Length == 2) p += "/";
            if (p[2] != '/')
                throw new ArgumentException($"path must use the drive-root form 0:/... (got '{path}')");
            var parts = p.Substring(3).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var clean = new System.Collections.Generic.List<string>(parts.Length);
            foreach (var part in parts)
            {
                if (part == "." || part.Length == 0) continue;
                if (part == "..") throw new ArgumentException("SD paths may not contain '..' traversal");
                clean.Add(part);
            }
            return p.Substring(0, 3) + string.Join("/", clean);
        }

        public static string FileName(string path)
        {
            string p = Normalize(path);
            int i = p.LastIndexOf('/');
            return i < 3 ? "" : p.Substring(i + 1);
        }

        public static string Directory(string path)
        {
            string p = Normalize(path);
            int i = p.LastIndexOf('/');
            return i <= 2 ? p.Substring(0, 3) : p.Substring(0, i);
        }

        public static string Join(string dir, string name)
        {
            string d = Normalize(dir);
            string n = (name ?? "").Trim().Replace('\\', '/').Trim('/');
#if NETFRAMEWORK
            if (n.Length == 0 || n.Contains("/")) throw new ArgumentException("file name is empty or contains a path separator");
#else
            if (n.Length == 0 || n.Contains('/')) throw new ArgumentException("file name is empty or contains a path separator");
#endif
            return Normalize(d.TrimEnd('/') + "/" + n);
        }

        /// <summary>Resolves the user's move/rename input against the current file:
        /// "name.gcode" renames in place, "0:/dir/" moves keeping the name, a full
        /// path is taken verbatim. Throws on empty input, a target equal to the
        /// source, or a target outside an SD drive ("0:/", "1:/").</summary>
        public static string ResolveMoveTarget(string currentPath, string target)
        {
            string current = Normalize(currentPath);
            string t = (target ?? "").Trim().Replace('\\', '/');
            if (t.Length == 0)
                throw new ArgumentException("move target is empty - give a new name, a directory ending in '/', or a full path");
#if NETFRAMEWORK
            bool hasSlash = t.Contains("/");
#else
            bool hasSlash = t.Contains('/');
#endif
            string resolved = t.EndsWith("/", StringComparison.Ordinal) ? Join(Normalize(t), FileName(current))
                            : t.Length >= 2 && char.IsDigit(t[0]) && t[1] == ':' ? Normalize(t)
                            : hasSlash ? throw new ArgumentException("relative move targets may not contain path separators")
                            : Join(Directory(current), t);
            if (string.Equals(resolved, current, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("move target equals the current path - nothing to do");
            return resolved;
        }
    }
}
