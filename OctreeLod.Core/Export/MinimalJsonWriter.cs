using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OctreeLod.Core.Export;

// Minimal recursive JSON writer scoped to tileset.json's known, fixed shape
// (objects, arrays, numbers, strings, bools) — deliberately not a
// general-purpose serializer; see design notes on avoiding a
// System.Text.Json dependency in this netstandard2.0 library. Public so a
// live tileset builder (e.g. OctreeLod.Server) serializes the exact same
// JSON shape/number formatting as the file-based exporter, instead of a
// second serialization path that could subtly diverge from it.
public static class MinimalJsonWriter
{
    public static string Write(object value)
    {
        var sb = new StringBuilder();
        WriteValue(value, sb);
        return sb.ToString();
    }

    private static void WriteValue(object value, StringBuilder sb)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                WriteString(s, sb);
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case IDictionary<string, object> obj:
                WriteObject(obj, sb);
                break;
            case IEnumerable<object> arr:
                WriteArray(arr, sb);
                break;
            default:
                throw new NotSupportedException($"Unsupported JSON value type: {value.GetType()}");
        }
    }

    private static void WriteObject(IDictionary<string, object> obj, StringBuilder sb)
    {
        sb.Append('{');
        bool first = true;
        foreach (var kvp in obj)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(kvp.Key, sb);
            sb.Append(':');
            WriteValue(kvp.Value, sb);
        }
        sb.Append('}');
    }

    private static void WriteArray(IEnumerable<object> arr, StringBuilder sb)
    {
        sb.Append('[');
        bool first = true;
        foreach (var item in arr)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteValue(item, sb);
        }
        sb.Append(']');
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
