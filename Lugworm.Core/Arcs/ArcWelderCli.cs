using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Lugworm.Core.Arcs
{
    /// <summary>Optional whole-file G1->G2/G3 post-process via the ArcWelderLib console
    /// exe (ArcWelderConsole.exe, built from Git/ArcWelderLib). If the exe is missing
    /// or fails, returns the input unchanged and says so explicitly. The caller may
    /// choose the built-in fitter before invoking this optional post-process; this
    /// helper never silently changes that choice. No hard dependency.</summary>
    public static class ArcWelderCli
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool Available(string exePath)
            => !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);

        /// <summary>Builds the console argument string. `pathTolPercent` is a true
        /// percentage (5.0 = 5%); ArcWelderConsole expects a decimal fraction in (0,1)
        /// (0.05 = 5%), so it is converted here. Exposed for unit testing.</summary>
        public static string BuildArgs(string srcPath, string dstPath,
            double resolutionMm, double pathTolPercent, double maxRadiusMm,
            bool g90InfluencesExtruder)
        {
            if (!(resolutionMm > 0))
                throw new ArgumentOutOfRangeException(nameof(resolutionMm), "resolution must be > 0 mm");
            double tolFrac = pathTolPercent / 100.0;
            if (!(tolFrac > 0.0) || tolFrac >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(pathTolPercent),
                    $"path tolerance must be in (0,100)%, got {pathTolPercent.ToString(Inv)}%");
            if (!(maxRadiusMm > 0))
                throw new ArgumentOutOfRangeException(nameof(maxRadiusMm), "max radius must be > 0 mm");

            return
                $"--resolution-mm={resolutionMm.ToString(Inv)} " +
                $"--path-tolerance-percent={tolFrac.ToString(Inv)} " +
                $"--max-radius-mm={maxRadiusMm.ToString(Inv)}" +
                (g90InfluencesExtruder ? " --g90-influences-extruder" : "") +
                $" \"{srcPath}\" \"{dstPath}\"";
        }

        /// <summary>Returns (welded text, info, ran). On any problem `ran` is false and
        /// the original text comes back. `pathTolPercent` is a percentage (5.0 = 5%).</summary>
        public static (string text, string info, bool ran) Weld(
            string gcodeText, string exePath,
            double resolutionMm = 0.05, double pathTolPercent = 5.0, double maxRadiusMm = 1.0e6,
            bool g90InfluencesExtruder = false, int timeoutMs = 120000)
        {
            if (!Available(exePath))
                return (gcodeText,
                    "ArcWelder exe not found -> output remains unwelded; disable ArcWelder to use built-in arc fitting",
                    false);

            string src = Path.GetTempFileName();
            string dst = src + ".welded.gcode";
            try
            {
                File.WriteAllText(src, gcodeText);
                string args = BuildArgs(src, dst, resolutionMm, pathTolPercent, maxRadiusMm,
                                        g90InfluencesExtruder);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                string stderr = "";
                int exitCode;
                using (var p = Process.Start(psi))
                {
                    if (p == null) return (gcodeText, "ArcWelder failed to start -> output remains unwelded", false);
                    // Drain both pipes concurrently: sequential ReadToEnd deadlocks when
                    // the child fills the other pipe's buffer first.
                    var soTask = p.StandardOutput.ReadToEndAsync();
                    var seTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        // best effort: the process may have exited between the timeout
                        // check and Kill(), or be unkillable under the current account.
                        try { p.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                        return (gcodeText, "ArcWelder timed out -> output remains unwelded", false);
                    }
                    soTask.Wait(5000);
                    if (seTask.Wait(5000)) stderr = seTask.Result;
                    exitCode = p.ExitCode;
                }
                if (exitCode == 0 && File.Exists(dst))
                {
                    string outText = File.ReadAllText(dst);
                    return (outText, "ArcWelder: welded", true);
                }
                string why = exitCode != 0 ? $"exit code {exitCode}" : "no output file";
                if (stderr.Length > 0) why += "; stderr: " + stderr.Trim();
                return (gcodeText, $"ArcWelder failed ({why}) -> output remains unwelded", false);
            }
            // Broad by contract: Weld() promises the caller "on any problem, the
            // original text comes back with a reason" (see doc comment above) rather
            // than letting a launch failure, a locked temp file, or any other external-
            // process hiccup propagate. Not on a cancellable path (no CancellationToken
            // here), so there is no OperationCanceledException to special-case.
            catch (Exception ex)
            {
                return (gcodeText, "ArcWelder error: " + ex.Message + " -> output remains unwelded", false);
            }
            finally
            {
                // best effort: temp files; a leftover file in %TEMP% is not worth failing the call over.
                try { File.Delete(src); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { File.Delete(dst); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
