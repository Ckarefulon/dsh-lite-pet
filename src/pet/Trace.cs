// Optional per-stage timing trace, enabled with DSH_LITE_PET_TRACE=1.
// It answers "where does the CPU time actually go" without a profiler attached:
// every second the accumulator is written as one line to runtime/pet/trace.log.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DshLitePet
{
    internal static class Trace
    {
        public const int Decode = 0;
        public const int Compose = 1;
        public const int Bubble = 2;
        public const int Scale = 3;
        public const int Present = 4;
        public const int HitBuild = 5;
        public const int StageCount = 6;

        private static readonly string[] Names =
            { "decode", "compose", "bubble", "scale", "present", "hitbuild" };

        private static bool _enabled;
        private static bool _probed;
        private static StreamWriter _writer;
        private static readonly long[] Ticks = new long[StageCount];
        private static readonly int[] Counts = new int[StageCount];
        private static int _frames;
        private static int _hits;
        private static int _ticksRun;
        private static long _windowStart;
        private static readonly StringBuilder Line = new StringBuilder(256);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern ulong GetTickCount64();

        public static bool Enabled
        {
            get
            {
                if (!_probed)
                {
                    _probed = true;
                    string flag = Environment.GetEnvironmentVariable("DSH_LITE_PET_TRACE");
                    bool on = flag != null && flag.Length > 0 && flag != "0";
                    // A sentinel file is easier to toggle than a process environment variable:
                    // the hosted pet is started by the plugin, not by hand.
                    if (!on)
                    {
                        try { on = File.Exists(SentinelPath); } catch (Exception) { }
                    }
                    _enabled = on;
                    if (_enabled) Open();
                }
                return _enabled;
            }
        }

        private static string SentinelPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.on"); }
        }

        private static void Open()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "pet");
                if (!Directory.Exists(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(dir, "trace.log");
                _writer = new StreamWriter(path, false, Encoding.UTF8);
                _writer.AutoFlush = true;
                _windowStart = (long)GetTickCount64();
            }
            catch (Exception)
            {
                _enabled = false;
            }
        }

        public static void Add(int stage, long elapsedTicks)
        {
            if (!_enabled) return;
            Ticks[stage] += elapsedTicks;
            Counts[stage]++;
        }

        public static void Frame() { if (_enabled) _frames++; }

        public static void Hit() { if (_enabled) _hits++; }

        public static void TickRun() { if (_enabled) _ticksRun++; }

        /// <summary>Writes a one-off diagnostic line (used by the rarely-run code paths).</summary>
        public static void Note(string message)
        {
            if (!_enabled) return;
            try { _writer.WriteLine("note: " + message); } catch (Exception) { }
        }

        /// <summary>Called from the message loop: flushes one line per second.</summary>
        public static void MaybeFlush()
        {
            if (!_enabled) return;
            long now = (long)GetTickCount64();
            if (now - _windowStart < 1000) return;
            // Ignore the very first partial second: it has no useful averages.
            if (_windowStart == 0) { _windowStart = now; return; }

            Line.Length = 0;
            Line.Append("t=").Append(now / 1000).Append("s frames=").Append(_frames)
                .Append(" ticks=").Append(_ticksRun).Append(" hits=").Append(_hits);
            for (int i = 0; i < StageCount; i++)
            {
                long ms = Ticks[i] * 1000 / Stopwatch.Frequency;
                Line.Append(' ').Append(Names[i]).Append('=').Append(ms).Append("ms");
                if (Counts[i] > 0) Line.Append('/').Append(Counts[i]);
            }
            if (_frames > 0)
            {
                long total = 0;
                for (int i = 0; i < StageCount; i++) total += Ticks[i];
                Line.Append(" avgFrame=").Append((total * 1000L / Stopwatch.Frequency / _frames)).Append("ms");
            }
            try { _writer.WriteLine(Line.ToString()); } catch (Exception) { }

            for (int i = 0; i < StageCount; i++) { Ticks[i] = 0; Counts[i] = 0; }
            _frames = 0; _hits = 0; _ticksRun = 0;
            _windowStart = now;
        }
    }
}
