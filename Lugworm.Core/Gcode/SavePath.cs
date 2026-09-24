using System;
using System.IO;

namespace Lugworm.Core.Gcode
{
    /// <summary>Universal save-path handling for every component that writes files.
    /// The classic trap: feeding a FOLDER (e.g. "C:\...\Desktop\PRINT JOBS") into a
    /// file-path input makes File.Write* throw "Access to the path ... is denied" -
    /// Windows reports writing to a directory as an access violation, not as
    /// "this is a folder". Resolve() turns a folder into folder\defaultName, creates
    /// missing parent directories, and normalises quotes/whitespace.</summary>
    public static class SavePath
    {
        /// <param name="path">user input: file path, folder path, or folder + trailing slash</param>
        /// <param name="defaultFileName">name (with extension) used when path is a folder</param>
        public static string Resolve(string path, string defaultFileName)
        {
            path = path.Trim().Trim('"');
            if (path.Length == 0) throw new ArgumentException("empty save path");

            bool endsWithSep = path.EndsWith("\\", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal);
            if (endsWithSep || Directory.Exists(path))
                path = Path.Combine(path.TrimEnd('\\', '/'), defaultFileName);
            else if (string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                // extensionless input that isn't an existing file: treat as a folder
                if (!File.Exists(path))
                    path = Path.Combine(path, defaultFileName);
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return path;
        }

        /// <summary>User-readable explanation for a failed write.</summary>
        public static string Explain(Exception ex, string path)
        {
            if (ex is UnauthorizedAccessException)
            {
                if (Directory.Exists(path))
                    return $"'{path}' is a folder, not a file - a file name is appended automatically now; if this persists, the folder is write-protected";
                return $"write access to '{path}' denied - the file may be open in another program, " +
                       "or the folder is protected (OneDrive/Controlled Folder Access): allow Rhino in " +
                       "Windows Security → Ransomware protection, or save elsewhere";
            }
            if (ex is DirectoryNotFoundException) return $"folder of '{path}' does not exist";
            if (ex is IOException io) return $"'{path}': {io.Message} (file locked/open in Excel?)";
            return $"'{path}': {ex.Message}";
        }
    }
}
