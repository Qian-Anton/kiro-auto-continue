using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KiroAutoContinue
{
    // Commands from your phone through a WeCom smart bot (API mode, long connection). Port of src/Remote.ps1 (in v0.2).
    // Every word you type or read is in config.default.json (words.<lang>.remote).
    internal static class Remote
    {
        public static string NewReqId(string prefix)
        {
            return prefix + "_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static string Frame(string cmd, string reqId, object body)
        {
            var f = new Dictionary<string, object>
            {
                { "cmd", cmd },
                { "headers", new Dictionary<string, object> { { "req_id", reqId } } }
            };
            if (body != null) f["body"] = body;
            return Json.Write(f);
        }

        public static string SubscribeFrame(string reqId, string botId, string secret)
        {
            return Frame("aibot_subscribe", reqId, new Dictionary<string, object> { { "bot_id", botId }, { "secret", secret } });
        }

        public static string PingFrame(string reqId) { return Frame("ping", reqId, null); }

        // A reply to the message whose callback carried reqId: a stream message. finish=false is replaced by the
        // next frame with the same stream id.
        public static string ReplyFrame(string reqId, string streamId, string content, bool finish)
        {
            var body = new Dictionary<string, object>
            {
                { "msgtype", "stream" },
                { "stream", new Dictionary<string, object> { { "id", streamId }, { "finish", finish }, { "content", content } } }
            };
            return Frame("aibot_respond_msg", reqId, body);
        }

        // A message the bot sends on its own (not a reply), to chatid (in a single chat, the user id): markdown.
        public static string PushFrame(string chatid, string content)
        {
            var body = new Dictionary<string, object>
            {
                { "chatid", chatid },
                { "msgtype", "markdown" },
                { "markdown", new Dictionary<string, object> { { "content", content } } }
            };
            return Frame("aibot_send_msg", NewReqId("aibot_send_msg"), body);
        }

        // What you wrote: a text message, a voice transcript, or the text parts of a mixed message; "" for an
        // image or a file. A leading @mention is dropped; full-width digits, spaces and "?" become plain ones.
        public static string GetRemoteText(object body)
        {
            string t = "";
            string msgtype = Json.Str(Json.At(body, "msgtype"));
            if (msgtype == "text") t = Json.Str(Json.At(body, "text", "content"));
            else if (msgtype == "voice") t = Json.Str(Json.At(body, "voice", "content"));
            else if (msgtype == "mixed")
            {
                var parts = new List<string>();
                foreach (var it in Json.Arr(Json.At(body, "mixed", "msg_item")))
                    if (Json.Str(Json.At(it, "msgtype")) == "text")
                    {
                        string c = Json.Str(Json.At(it, "text", "content"));
                        if (c.Length > 0) parts.Add(c);
                    }
                t = string.Join("\n", parts);
            }
            t = Regex.Replace(t, @"^\s*(@\S+\s*)+", "");
            var sb = new StringBuilder();
            foreach (char ch in t)
            {
                int c = ch;
                if (c >= 0xFF10 && c <= 0xFF19) sb.Append((char)(c - 0xFF10 + 0x30));
                else if (c == 0x3000) sb.Append(' ');
                else if (c == 0xFF1F) sb.Append('?');
                else sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        static string Alt(Config cfg, string kind)
        {
            var all = new List<string>();
            var words = Json.Obj(cfg.At("words"));
            if (words != null)
                foreach (var kv in words)
                    foreach (var x in Json.StrArr(Json.At(kv.Value, "remote", "cmd", kind)))
                        if (x.Length > 0) all.Add(x);
            all.Sort((a, b) => b.Length.CompareTo(a.Length));
            return string.Join("|", all.Select(Regex.Escape));
        }

        public sealed class Command { public string Kind = "unknown"; public int N; public string Text = ""; public string Code = ""; }

        // Your message as a command. A send needs a separator (space, colon, or the number word) between the
        // number and the text, and a new session a space or colon after the word, so a message that merely starts
        // with a digit or that word is not typed anywhere.
        public static Command ParseCommand(string text, Config cfg)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return new Command();
            var opt = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            const string end = @"[\s\p{P}]*$";
            string hao = Alt(cfg, "number");
            string suffix = hao.Length > 0 ? "(?:" + hao + ")?" : "";

            foreach (string k in new[] { "status", "help" })
            {
                string a = Alt(cfg, k);
                if (a.Length > 0 && Regex.IsMatch(t, "^(?:" + a + ")" + end, opt)) return new Command { Kind = k };
            }
            string pair = Alt(cfg, "pair");
            if (pair.Length > 0)
            {
                var m = Regex.Match(t, "^(?:" + pair + @")\s*(\d{4,8})" + end, opt);
                if (m.Success) return new Command { Kind = "pair", Code = m.Groups[1].Value };
            }
            foreach (string k in new[] { "continue", "stop", "clear" })
            {
                string a = Alt(cfg, k);
                if (a.Length == 0) continue;
                var m = Regex.Match(t, "^(?:" + a + @")\s*(\d+)?\s*" + suffix + end, opt);
                if (m.Success)
                {
                    int n = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
                    return new Command { Kind = k, N = n };
                }
            }
            string na = Alt(cfg, "new");
            if (na.Length > 0)
            {
                var m = Regex.Match(t, "^(?:" + na + @")(?:\s+|\s*[:\uFF1A]\s*)(\S.*)$", opt);
                if (m.Success) return new Command { Kind = "new", Text = m.Groups[1].Value.Trim() };
            }
            const string sep = @"[\s:,;\u3001\uFF0C\uFF1A\uFF1B]";
            string gap = hao.Length > 0 ? @"(?:\s*(?:" + hao + ")" + sep + "*|" + sep + "+)" : sep + "+";
            string send = Alt(cfg, "send");
            string pre = send.Length > 0 ? "(?:(?:" + send + @")\s*)?" : "";
            var m2 = Regex.Match(t, "^" + pre + @"(?>(\d+))" + gap + @"(\S.*)$", opt);
            if (m2.Success) return new Command { Kind = "send", N = int.Parse(m2.Groups[1].Value), Text = m2.Groups[2].Value.Trim() };
            return new Command();
        }

        public sealed class NewCmd { public string Workspace = ""; public string Text = ""; }

        // The workspace a "new" command names and the message after it.
        public static NewCmd SplitNew(string text, List<string> workspaces)
        {
            var ws = workspaces.Where(w => !string.IsNullOrEmpty(w)).ToList();
            var m = Regex.Match(text, @"^(\S+)(?:\s+(\S.*))?$", RegexOptions.Singleline);
            if (m.Success)
            {
                string word = m.Groups[1].Value;
                string rest = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
                var hit = ws.Where(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count == 0) hit = ws.Where(w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count == 1) return new NewCmd { Workspace = hit[0], Text = rest };
            }
            if (ws.Count == 1) return new NewCmd { Workspace = ws[0], Text = text.Trim() };
            return new NewCmd { Workspace = "", Text = text.Trim() };
        }

        // How a session's last turn stands, for the status reply (rw: words.<lang>.remote).
        public static string TurnState(Turn turn, object rw, DateTime now)
        {
            if (!turn.Ended) return Json.Str(Json.At(rw, "state", "running"));
            if (turn.Restart) return Json.Str(Json.At(rw, "state", "restart"));
            string at = "";
            var end = Records.FromTime(turn.EndedAt);
            if (end != null)
            {
                DateTime l = end.Value.ToLocalTime();
                at = l.Date == now.Date ? l.ToString("HH:mm") : l.ToString("MM-dd HH:mm");
            }
            string k = "done";
            if (turn.Stop == "error" || turn.Unfinished) k = "cut"; else if (turn.Cancelled) k = "stopped";
            return Policy.FormatText(Json.At(rw, "state", k), new Dictionary<string, string> { { "at", at } }).Trim();
        }
    }

    // The connection, on its own background thread so heartbeats go on while the watcher types into Kiro. It uses
    // the system proxy; when connecting fails, every other try goes direct. Never logs the secret.
    internal sealed class RemoteClient
    {
        public readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<string> Outbox = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();
        public volatile string State = "starting";
        public volatile bool Stop, AuthFailed, PingNow;
        public DateTime LastPong = DateTime.MinValue;

        string botId = "", secret = "", url = "";
        Thread thread;

        public void Start(Config cfg)
        {
            botId = Json.Str(cfg.At("remote", "botId"));
            secret = Json.Str(cfg.At("remote", "secret"));
            url = Json.Str(cfg.At("remote", "url"));
            if (url.Length == 0) url = "wss://openws.work.weixin.qq.com";
            thread = new Thread(Loop) { IsBackground = true, Name = "kac-remote" };
            thread.Start();
        }

        public void StopAndWait()
        {
            Stop = true;
            if (thread != null) thread.Join(4000);
        }

        void Say(string s) { Log.Enqueue(s); }

        static void SendFrame(ClientWebSocket sock, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (!sock.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(15000))
                throw new Exception("sending timed out");
        }

        void Loop()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            int failures = 0;
            var drops = new List<DateTime>();
            while (!Stop)
            {
                ClientWebSocket sock = null; bool online = false; string reason = ""; bool direct = (failures % 2) == 1;
                try
                {
                    State = "connecting";
                    sock = new ClientWebSocket();
                    sock.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    if (direct) sock.Options.Proxy = null;
                    if (!sock.ConnectAsync(new Uri(url), CancellationToken.None).Wait(20000)) throw new Exception("connecting timed out");

                    string sub = Remote.NewReqId("aibot_subscribe");
                    SendFrame(sock, Remote.SubscribeFrame(sub, botId, secret));
                    DateTime asked = DateTime.Now, lastPing = DateTime.Now; int missed = 0;
                    var buf = new byte[131072];
                    var seg = new ArraySegment<byte>(buf);
                    var acc = new MemoryStream();
                    var recv = sock.ReceiveAsync(seg, CancellationToken.None);
                    while (!Stop)
                    {
                        if (recv.IsCompleted)
                        {
                            var r = recv.Result;
                            if (r.MessageType == WebSocketMessageType.Close)
                                throw new Exception("WeCom closed the connection (" + sock.CloseStatus + " " + sock.CloseStatusDescription + ")");
                            acc.Write(buf, 0, r.Count);
                            if (r.EndOfMessage)
                            {
                                string raw = Encoding.UTF8.GetString(acc.ToArray()); acc.SetLength(0);
                                object f = null;
                                try { f = Json.Parse(raw); } catch { Say("unreadable frame: " + raw.Substring(0, Math.Min(100, raw.Length))); }
                                if (f != null)
                                {
                                    string cmd = Json.Str(Json.At(f, "cmd"));
                                    string rid = Json.Str(Json.At(f, "headers", "req_id"));
                                    if (cmd == "aibot_msg_callback" || cmd == "aibot_event_callback") Inbox.Enqueue(raw);
                                    else if (rid == sub)
                                    {
                                        int ec = Json.Int(Json.At(f, "errcode"));
                                        if (ec != 0) { AuthFailed = true; throw new Exception("WeCom refused the bot id and secret: errcode " + ec + ", " + Json.Str(Json.At(f, "errmsg"))); }
                                        online = true; failures = 0; AuthFailed = false; State = "online";
                                        Say("online" + (direct ? " (connected directly, not through the system proxy)" : ""));
                                    }
                                    else if (rid.StartsWith("ping")) { missed = 0; LastPong = DateTime.Now; }
                                    else if (Json.At(f, "errcode") != null && Json.Int(Json.At(f, "errcode")) != 0)
                                    {
                                        string what = rid.StartsWith("aibot_send_msg") ? "a message" : "a reply";
                                        Say("WeCom refused " + what + ": errcode " + Json.Int(Json.At(f, "errcode")) + ", " + Json.Str(Json.At(f, "errmsg")));
                                    }
                                    else if (cmd.Length > 0) Say("frame " + cmd);
                                }
                            }
                            recv = sock.ReceiveAsync(seg, CancellationToken.None);
                            continue;
                        }
                        if (!online)
                        {
                            if ((DateTime.Now - asked).TotalSeconds > 20) throw new Exception("no answer to subscribing");
                        }
                        else
                        {
                            string o;
                            while (Outbox.TryDequeue(out o)) SendFrame(sock, o);
                            if (PingNow || (DateTime.Now - lastPing).TotalSeconds >= 30)
                            {
                                if (missed >= 2) throw new Exception("no answer to two heartbeats");
                                PingNow = false;
                                SendFrame(sock, Remote.PingFrame(Remote.NewReqId("ping")));
                                missed++; lastPing = DateTime.Now;
                            }
                        }
                        Thread.Sleep(200);
                    }
                }
                catch (Exception ex)
                {
                    var m = ex; while (m.InnerException != null) m = m.InnerException; reason = m.Message;
                }
                finally
                {
                    if (sock != null) { try { sock.Dispose(); } catch { } }
                    State = "offline";
                }
                if (Stop) break;
                if (online) drops.Add(DateTime.Now); else failures++;
                drops.RemoveAll(d => (DateTime.Now - d).TotalMinutes > 10);
                int wait;
                if (AuthFailed) wait = 600;
                else if (drops.Count >= 3) { wait = 600; drops.Clear(); reason += "; it keeps dropping: is another program connected as this bot? WeCom keeps one connection per bot"; }
                else if (online) wait = 2;
                else wait = (int)Math.Min(60, Math.Pow(2, Math.Min(failures, 6)));
                Say("offline: " + reason + "; trying again in " + wait + " s");
                DateTime until = DateTime.Now.AddSeconds(wait);
                while (!Stop && DateTime.Now < until) Thread.Sleep(500);
            }
            State = "stopped";
        }
    }
}
