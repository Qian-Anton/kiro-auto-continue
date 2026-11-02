using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace KiroAutoContinue
{
    // Short notices to your phone through a WeCom group bot's webhook (qyapi.weixin.qq.com: in a group, the
    // "..." menu > Message push > Add). Anyone holding the webhook can post to that group, so it lives in the
    // user's config, out of git and out of the log. Port of src/Notify.ps1, WeCom only. Needs Config.
    internal static class Notify
    {
        public static bool IsWeCom(string url)
        {
            return url.StartsWith("https://qyapi.weixin.qq.com/", StringComparison.OrdinalIgnoreCase);
        }

        public static string Body(string text)
        {
            var o = new Dictionary<string, object>
            {
                { "msgtype", "text" },
                { "text", new Dictionary<string, object> { { "content", text } } }
            };
            return Json.Write(o);
        }

        // At most maxBytes of text in UTF-8 (WeCom allows 2048 for one text message), cut at a whole character.
        public static string LimitUtf8(string text, int maxBytes)
        {
            var enc = Encoding.UTF8;
            if (enc.GetByteCount(text) <= maxBytes) return text;
            int n = text.Length;
            while (n > 0 && enc.GetByteCount(text.Substring(0, n)) > maxBytes - 3) n--;
            if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
            return text.Substring(0, n) + "...";
        }

        // The notice: the headline for code (words.<lang>.notice), the session, the detail, the time.
        public static string Format(Config cfg, string code, string name, string detail, DateTime? when = null)
        {
            DateTime w0 = when ?? DateTime.Now;
            object w = cfg.Words(cfg.UiLang());
            string head = Json.Str(Json.At(w, "notice", code)); if (head.Length == 0) head = code;
            var lines = new List<string> { Json.Str(Json.At(w, "notice", "prefix")) + head };
            if (!string.IsNullOrEmpty(name)) lines.Add(name);
            if (!string.IsNullOrEmpty(detail)) lines.Add(detail);
            lines.Add(w0.ToString("MM-dd HH:mm"));
            return string.Join("\n", lines);
        }

        // Post text to notify.webhook. "" once it went out, otherwise why not (never the webhook itself).
        public static string Send(Config cfg, string text)
        {
            string url = Json.Str(cfg.At("notify", "webhook"));
            if (url.Length == 0) return "no webhook in notify.webhook";
            if (!IsWeCom(url)) return "notify.webhook is not a WeCom webhook";
            text = LimitUtf8(text, 2000);
            byte[] data = Encoding.UTF8.GetBytes(Body(text));
            int timeout = Json.Int(cfg.At("notify", "timeoutSeconds")); if (timeout <= 0) timeout = 10;
            string resp;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Timeout = timeout * 1000;
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var r = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) resp = rd.ReadToEnd();
            }
            catch (Exception e) { return "could not reach the webhook: " + e.Message; }

            var jr = Json.Obj(Json.Parse(resp));
            if (jr != null && jr.ContainsKey("errcode"))
            {
                int code = Json.Int(jr["errcode"]);
                if (code != 0)
                {
                    string msg = jr.ContainsKey("errmsg") ? Json.Str(jr["errmsg"]) : "";
                    return "the webhook answered " + code + ": " + msg;
                }
            }
            return "";
        }
    }
}
