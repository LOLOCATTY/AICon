using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AICon
{
    /// <summary>
    /// Thin wrapper over JavaScriptSerializer (ships with .NET Framework — no NuGet needed
    /// inside Revit) plus tolerant accessors for the loosely-typed values it produces.
    /// </summary>
    internal static class Json
    {
        // Generous but bounded — the default MaxJsonLength (~4 MB) is too small for a big run_code
        // payload or a large batch call, but int.MaxValue removes the serializer's one built-in guard
        // against an oversized request body entirely. 64 MB comfortably covers any real tool call.
        private const int MaxJsonLength = 64 * 1024 * 1024;

        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxJsonLength };
        }

        public static string Serialize(object value)
        {
            return NewSerializer().Serialize(value);
        }

        public static Dictionary<string, object> DeserializeObject(string json)
        {
            return NewSerializer().Deserialize<Dictionary<string, object>>(json);
        }

        public static string GetString(Dictionary<string, object> dict, string key, string fallback = null)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        public static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { }
            }
            return null;
        }

        public static int? GetInt(Dictionary<string, object> dict, string key)
        {
            double? d = GetDouble(dict, key);
            return d.HasValue ? (int?)(int)Math.Round(d.Value) : null;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v))
                return v as Dictionary<string, object>;
            return null;
        }

        /// <summary>JSON arrays may come back as object[] or ArrayList depending on nesting; normalize.</summary>
        public static List<object> GetList(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict == null || !dict.TryGetValue(key, out v) || v == null) return null;
            return ToList(v);
        }

        public static List<object> ToList(object value)
        {
            if (value == null) return null;
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string) return null;
            var list = new List<object>();
            foreach (object item in enumerable) list.Add(item);
            return list;
        }

        public static double ToDouble(object value)
        {
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new InvalidOperationException("Expected a number, got '" + value + "'.");
            }
        }

        public static int ToInt(object value)
        {
            return (int)Math.Round(ToDouble(value));
        }
    }
}

