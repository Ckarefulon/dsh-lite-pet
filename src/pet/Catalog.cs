// Animation catalog loaded from runtime/media/index.json (produced by tools/build-media.mjs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshLitePet
{
    internal sealed class CatalogEntry
    {
        public string Id;
        public string Name;
        public string Group;
        public string Kind;
        public string ColorPath;
        public string AlphaPath;
        public int Ms;
        public int Fps;

        public bool IsLoopingKind()
        {
            return Kind == "idle" || Kind == "work-thinking" || Kind == "work-busy" || Kind == "work-waiting";
        }
    }

    internal sealed class Catalog
    {
        public int Width = 640;
        public int Height = 360;
        public readonly List<CatalogEntry> Animations = new List<CatalogEntry>();

        public static Catalog Load(string mediaDir)
        {
            string path = Path.Combine(mediaDir, "index.json");
            string json = File.ReadAllText(path, Encoding.UTF8);
            var root = (Dictionary<string, object>)Json.Parse(json);
            var catalog = new Catalog();
            catalog.Width = (int)Json.Number(root, "width", 640);
            catalog.Height = (int)Json.Number(root, "height", 360);
            var list = (List<object>)root["animations"];
            foreach (object item in list)
            {
                var node = (Dictionary<string, object>)item;
                var entry = new CatalogEntry
                {
                    Id = Json.String(node, "id"),
                    Name = Json.String(node, "name"),
                    Group = Json.String(node, "group"),
                    Kind = Json.String(node, "kind"),
                    Ms = (int)Json.Number(node, "ms", 10000),
                    Fps = (int)Json.Number(node, "fps", 24),
                };
                entry.ColorPath = Path.Combine(mediaDir, Json.String(node, "color"));
                entry.AlphaPath = Path.Combine(mediaDir, Json.String(node, "alpha"));
                catalog.Animations.Add(entry);
            }
            return catalog;
        }

        public CatalogEntry First(string kind)
        {
            foreach (CatalogEntry entry in Animations) if (entry.Kind == kind) return entry;
            return null;
        }

        public List<CatalogEntry> ByKind(string kind)
        {
            var result = new List<CatalogEntry>();
            foreach (CatalogEntry entry in Animations) if (entry.Kind == kind) result.Add(entry);
            return result;
        }

        public List<string> Groups()
        {
            var seen = new List<string>();
            foreach (CatalogEntry entry in Animations)
                if (!seen.Contains(entry.Group)) seen.Add(entry.Group);
            return seen;
        }

        public List<CatalogEntry> ByGroup(string group)
        {
            var result = new List<CatalogEntry>();
            foreach (CatalogEntry entry in Animations) if (entry.Group == group) result.Add(entry);
            return result;
        }

        public CatalogEntry ById(string id)
        {
            foreach (CatalogEntry entry in Animations) if (entry.Id == id) return entry;
            return null;
        }
    }

    /// <summary>Tiny JSON reader — the catalog and the host's IPC lines are the only JSON this process sees.</summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            int index = 0;
            object value = ParseValue(text, ref index);
            return value;
        }

        public static string String(Dictionary<string, object> node, string key)
        {
            object value;
            return node.TryGetValue(key, out value) ? value as string : null;
        }

        public static double Number(Dictionary<string, object> node, string key, double fallback)
        {
            object value;
            if (!node.TryGetValue(key, out value) || value == null) return fallback;
            if (value is double) return (double)value;
            if (value is long) return (long)value;
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> node, string key, bool fallback)
        {
            object value;
            if (!node.TryGetValue(key, out value) || !(value is bool)) return fallback;
            return (bool)value;
        }

        private static object ParseValue(string s, ref int i)
        {
            Skip(s, ref i);
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            Skip(s, ref i);
            if (s[i] == '}') { i++; return result; }
            while (true)
            {
                Skip(s, ref i);
                string key = ParseString(s, ref i);
                Skip(s, ref i);
                if (s[i] != ':') throw new FormatException("json: expected ':' at " + i);
                i++;
                result[key] = ParseValue(s, ref i);
                Skip(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("json: expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            Skip(s, ref i);
            if (s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref i));
                Skip(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("json: expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // "
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                        i += 4;
                        break;
                    default: throw new FormatException("json: bad escape");
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
