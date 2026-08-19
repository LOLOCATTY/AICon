using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace AICon
{
    /// <summary>
    /// Wraps whichever JSON serializer this Revit version's runtime gives us, behind accessors every
    /// tool already relies on. Revit 2023/2024 (net48): JavaScriptSerializer, ships with .NET
    /// Framework, no NuGet needed inside Revit. Revit 2025+ (a future net8.0-windows target — see
    /// ElementIdCompat.cs for why that split exists): System.Web.Script.Serialization does not exist
    /// on .NET 8 at all, so that half is gone; System.Text.Json takes over instead.
    ///
    /// UNLIKE ElementIdCompat's #else branch, this one is NOT Revit-API-dependent — it is pure
    /// JSON/BCL logic — so it WAS verified against real .NET 8 semantics in a throwaway console
    /// harness before shipping (round-tripped nested dict/list/string/number/bool/null shapes and
    /// confirmed they match JavaScriptSerializer's output exactly). What is still unverified is only
    /// that it behaves the same way once actually hosted inside a Revit 2025 process.
    ///
    /// Every method below the #if/#else block is unchanged and shared by both — only the serializer
    /// itself differs.
    /// </summary>
    internal static class Json
    {
#if NETFRAMEWORK
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
#else
        // System.Text.Json does NOT produce a loosely-typed Dictionary<string,object>/List<object>
        // tree the way JavaScriptSerializer does for free — by default it hands back JsonElement,
        // which every one of the ~76 tools' argument parsing (RequireElement, PointFromArg,
        // RoutineExecutor.ResolveValue, ...) does not understand. ObjectToInferredTypesConverter below
        // recursively unwraps JsonElement into plain CLR objects so the shape matches exactly.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new ObjectToInferredTypesConverter() }
        };

        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        public static Dictionary<string, object> DeserializeObject(string json)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, Options);
        }

        private sealed class ObjectToInferredTypesConverter : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using (JsonDocument doc = JsonDocument.ParseValue(ref reader))
                    return Unwrap(doc.RootElement.Clone());
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                JsonSerializer.Serialize(writer, value, value != null ? value.GetType() : typeof(object), options);
            }

            private static object Unwrap(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        var dict = new Dictionary<string, object>();
                        foreach (JsonProperty prop in el.EnumerateObject()) dict[prop.Name] = Unwrap(prop.Value);
                        return dict;
                    case JsonValueKind.Array:
                        var list = new List<object>();
                        foreach (JsonElement item in el.EnumerateArray()) list.Add(Unwrap(item));
                        return list;
                    case JsonValueKind.String: return el.GetString();
                    // Always double, matching how GetInt/ToInt already round a GetDouble() result —
                    // never has to guess whether a number "should" be int/long/double.
                    case JsonValueKind.Number: return el.GetDouble();
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    default: return null;
                }
            }
        }
#endif

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
