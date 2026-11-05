// Per-user pet preferences: window scale, window position and the bubble's vertical offset.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshLitePet
{
    internal sealed class PetSettings
    {
        public double Scale = 1.0;
        public int BubbleOffsetY;
        public int WindowX = int.MinValue;
        public int WindowY = int.MinValue;

        private static string _path;

        /// <summary>
        /// Preference file. The pet may run under a restricted token (the Harness sandbox can
        /// deny parts of the user profile), so several plain-file locations are tried in order;
        /// no directory is ever created.
        /// </summary>
        private static string[] Candidates()
        {
            var candidates = new List<string>();
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrEmpty(home)) candidates.Add(Path.Combine(home, "dsh-lite-pet-settings.json"));
            candidates.Add(Path.Combine(Path.GetTempPath(), "dsh-lite-pet-settings.json"));
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh-lite-pet-settings.json"));
            return candidates.ToArray();
        }

        public static PetSettings Load()
        {
            var settings = new PetSettings();
            if (_path != null) return settings;
            foreach (string candidate in Candidates())
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var node = (Dictionary<string, object>)Json.Parse(File.ReadAllText(candidate, Encoding.UTF8));
                    double scale = Json.Number(node, "scale", 1.0);
                    if (scale < 0.6 || scale > 2.0) scale = 1.0;
                    settings.Scale = scale;
                    settings.BubbleOffsetY = (int)Json.Number(node, "bubbleOffsetY", 0);
                    settings.WindowX = (int)Json.Number(node, "windowX", int.MinValue);
                    settings.WindowY = (int)Json.Number(node, "windowY", int.MinValue);
                    _path = candidate;
                    return settings;
                }
                catch (Exception)
                {
                    // A broken settings file must never keep the pet from starting.
                }
            }
            return settings;
        }

        public void Save()
        {
            string json =
                "{\n  \"scale\": " + Scale.ToString("0.##", CultureInfo.InvariantCulture) +
                ",\n  \"bubbleOffsetY\": " + BubbleOffsetY +
                ",\n  \"windowX\": " + WindowX +
                ",\n  \"windowY\": " + WindowY + "\n}\n";
            var order = new List<string>();
            if (_path != null) order.Add(_path);
            foreach (string candidate in Candidates()) if (!order.Contains(candidate)) order.Add(candidate);
            foreach (string candidate in order)
            {
                try
                {
                    File.WriteAllText(candidate, json, new UTF8Encoding(false));
                    _path = candidate;
                    return;
                }
                catch (Exception)
                {
                    // Try the next location; a missing preference is not fatal.
                }
            }
        }
    }
}
