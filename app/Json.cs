using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace KiroAutoContinue
{
    // Small JSON helper over the in-box JavaScriptSerializer (System.Web.Extensions), so the exe needs no
    // extra DLL. Parsing gives Dictionary<string,object> for objects, object[] for arrays, and primitives.
    // The navigation helpers are null-safe: a missing key or a wrong type gives the default, never throws.
    internal static class Json
    {
        private static JavaScriptSerializer Ser()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return Ser().DeserializeObject(text);
        }

        public static string Write(object value)
        {
            return Ser().Serialize(value);
        }

        public static Dictionary<string, object> Obj(object node)
        {
            return node as Dictionary<string, object>;
        }

        // Follow keys down nested objects; null if any step is missing or not an object.
        public static object At(object node, params string[] keys)
        {
            object cur = node;
            foreach (string k in keys)
            {
                var o = cur as Dictionary<string, object>;
                if (o == null || !o.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }

        public static bool Has(object node, string key)
        {
            var o = node as Dictionary<string, object>;
            return o != null && o.ContainsKey(key);
        }

        public static string Str(object node, string fallback = "")
        {
            if (node == null) return fallback;
            if (node is string s) return s;
            return Convert.ToString(node, CultureInfo.InvariantCulture);
        }

        public static int Int(object node, int fallback = 0)
        {
            if (node == null) return fallback;
            try { return Convert.ToInt32(node, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static double Dbl(object node, double fallback = 0)
        {
            if (node == null) return fallback;
            try { return Convert.ToDouble(node, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static bool Bool(object node, bool fallback = false)
        {
            if (node is bool b) return b;
            if (node is string s) return s == "true" || s == "True" || s == "1";
            return fallback;
        }

        // An array as a list of objects (empty if it is not an array).
        public static List<object> Arr(object node)
        {
            var list = new List<object>();
            if (node is object[] a) list.AddRange(a);
            else if (node is System.Collections.IEnumerable e && !(node is string))
                foreach (var x in e) list.Add(x);
            return list;
        }

        // An array of strings.
        public static List<string> StrArr(object node)
        {
            var list = new List<string>();
            foreach (var x in Arr(node)) list.Add(Str(x));
            return list;
        }
    }
}
