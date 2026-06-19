using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Dedicated file logging for the mod, kept OUT of tModLoader's shared client.log so our output is easy
    /// to find and parse. Writes two files next to client.log (in tModLoader-Logs):
    /// <list type="bullet">
    /// <item><c>ManyMoreMobs-debug.log</c> — runtime breadcrumbs + de-duplicated exception reports.</item>
    /// <item><c>ManyMoreMobs-state.log</c> — full state/validation dumps (from <c>/debugnpc</c> and auto hooks).</item>
    /// </list>
    /// Both are truncated at the start of each session. A one-line pointer is still echoed to client.log so
    /// it's discoverable. All writes are locked and never allowed to throw into game code.
    /// </summary>
    public static class MmmLog
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> SeenExceptions = new HashSet<string>();

        private static Mod _mod;
        private static string _debugPath;
        private static string _statePath;

        public static string DebugPath => _debugPath;
        public static string StatePath => _statePath;

        public static void Init(Mod mod)
        {
            _mod = mod;
            try
            {
                string dir;
                try { dir = Path.GetDirectoryName(Logging.LogPath); }
                catch { dir = null; }
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Main.SavePath, "Logs");
                dir = Path.GetFullPath(dir);
                Directory.CreateDirectory(dir);

                _debugPath = Path.Combine(dir, "ManyMoreMobs-debug.log");
                _statePath = Path.Combine(dir, "ManyMoreMobs-state.log");

                File.WriteAllText(_debugPath, $"=== Many More Mobs — debug log — session {Now()} ==={Environment.NewLine}");
                File.WriteAllText(_statePath, $"=== Many More Mobs — state log — session {Now()} ==={Environment.NewLine}");

                mod.Logger.Info($"[MMM] Debug logs: {_debugPath}  |  {_statePath}");
            }
            catch (Exception e)
            {
                _debugPath = _statePath = null;
                mod?.Logger.Warn($"[MMM] Could not create debug log files: {e.Message}");
            }
        }

        private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

        public static void Info(string message) => WriteLine(_debugPath, "INFO", message);

        public static void Warn(string message)
        {
            WriteLine(_debugPath, "WARN", message);
            _mod?.Logger.Warn("[MMM] " + message);
        }

        public static void Error(string message)
        {
            WriteLine(_debugPath, "ERROR", message);
            _mod?.Logger.Error("[MMM] " + message);
        }

        /// <summary>Logs each unique exception (by context + type + top stack frame) once, to avoid spam.</summary>
        public static void Report(Exception ex, string context)
        {
            string signature = context + "|" + ex.GetType().FullName + "|" + FirstFrame(ex);
            bool firstTime;
            lock (Gate) firstTime = SeenExceptions.Add(signature);
            if (!firstTime)
                return;

            WriteLine(_debugPath, "EXCEPTION", $"{context}{Environment.NewLine}{ex}");
            _mod?.Logger.Error($"[MMM] {context}: {ex.GetType().Name}: {ex.Message} (see ManyMoreMobs-debug.log)");
        }

        /// <summary>Writes a titled multi-line block to the state log (used for dumps/validation reports).</summary>
        public static void Dump(string title, string body)
        {
            string block = $"{Environment.NewLine}=== {title} @ {Now()} ==={Environment.NewLine}{body.TrimEnd()}{Environment.NewLine}";
            WriteRaw(_statePath, block);
            _mod?.Logger.Info($"[MMM] {title} -> ManyMoreMobs-state.log");
        }

        private static string FirstFrame(Exception ex)
        {
            string trace = ex.StackTrace;
            if (string.IsNullOrEmpty(trace))
                return "";
            int nl = trace.IndexOf('\n');
            return (nl < 0 ? trace : trace.Substring(0, nl)).Trim();
        }

        private static void WriteLine(string path, string level, string message)
            => WriteRaw(path, $"[{Now()}] [{level}] {message}{Environment.NewLine}");

        private static void WriteRaw(string path, string text)
        {
            if (path == null)
                return;
            try { lock (Gate) File.AppendAllText(path, text); }
            catch { /* logging must never crash the game */ }
        }
    }
}
