using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KiroAutoContinue
{
    // Report.cs: counting what the watcher did from its log, and when the morning status is due. The cases of
    // tests\Report.Tests.ps1, one for one.
    internal static class ReportTests
    {
        static int Get(Dictionary<string, int> t, string k) { int v; return t.TryGetValue(k, out v) ? v : 0; }

        public static void Run()
        {
            var now = new DateTime(2026, 11, 3, 8, 0, 0);
            var log = new[]
            {
                "11-02 19:00:00  ws | old : interrupted [error]; sent resume",
                "11-02 21:10:05  ws | a : interrupted [error, failed]; sent resume",
                "11-02 21:40:00  ws | a : cut short by a Kiro restart [no turn_end]; sent resume (screen locked)",
                "11-02 22:00:00  ws | b : until-done: stopped without a mark, push 2/30; sent push",
                "11-02 22:30:00  ws | b : a message from your phone was waiting [end_turn, success]; sent queued",
                "11-03 01:00:00  ws | c : NEEDS YOU - out of credits [error (OverageLimitReachedError)]; it continues once the credits are back or Kiro restarts",
                "11-03 02:00:00  ws | b : DONE - until-done: done after 3 pushes",
                "11-03 03:00:00  ws | a : sent from your phone: [x]",
                "11-03 03:00:01  remote: zhangsan sent status",
                "not a log line"
            };
            var c = Report.LogCounts(log, now.AddHours(-12), now);
            T.Check("continues in the window: " + Get(c.Resumes, "ws | a"), () => Get(c.Resumes, "ws | a") == 2 && !c.Resumes.ContainsKey("ws | old"));
            T.Check("pushes: until-done and waiting messages", () => Get(c.Pushes, "ws | b") == 2);
            T.Check("needs you", () => c.Needs.ContainsKey("ws | c") && c.Needs.Count == 1);
            T.Check("until-done finished", () => c.Done.ContainsKey("ws | b"));
            T.Check("totals", () => Report.Total(c.Resumes) == 2 && Report.Total(c.Pushes) == 2);
            c = Report.LogCounts(new[] { "12-31 23:30:00  ws | a : x; sent resume" }, new DateTime(2026, 12, 31, 20, 0, 0), new DateTime(2027, 1, 1, 8, 0, 0));
            T.Check("a line from the last day of the year before", () => Get(c.Resumes, "ws | a") == 1);
            c = Report.LogCounts(new string[0], now.AddHours(-12), now);
            T.Check("no lines: nothing", () => Report.Total(c.Resumes) == 0 && c.Needs.Count == 0);

            T.Check("08:00 today", () => Report.ReportDue("08:00", now) == new DateTime(2026, 11, 3, 8, 0, 0));
            T.Check("7:30", () => Report.ReportDue("7:30", now) == new DateTime(2026, 11, 3, 7, 30, 0));
            T.Check("empty: off", () => Report.ReportDue("", now) == null);
            T.Check("not a time: off", () => Report.ReportDue("25:00", now) == null && Report.ReportDue("eight", now) == null);
        }
    }

    // Notify.cs: the webhook, the JSON body, the notice text. Sends nothing. The cases of tests\Notify.Tests.ps1
    // that apply: the exe sends to WeCom only, so the two about Feishu's body and signature are not here, and a
    // Feishu or Lark webhook must be refused instead.
    internal static class NotifyTests
    {
        public static void Run()
        {
            var cfg = T.Defaults();
            T.Check("WeCom webhook", () => Notify.IsWeCom("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abc"));
            T.Check("Feishu webhook: not WeCom (the exe sends to WeCom only)", () => !Notify.IsWeCom("https://open.feishu.cn/open-apis/bot/v2/hook/abc"));
            T.Check("Lark webhook: not WeCom", () => !Notify.IsWeCom("https://open.larksuite.com/open-apis/bot/v2/hook/abc"));
            T.Check("another URL: none", () => !Notify.IsWeCom("https://example.com/hook"));
            T.Check("plain http: none", () => !Notify.IsWeCom("http://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abc"));

            string zh = T.U(0x9700, 0x8981, 0x4F60);
            object j = Json.Parse(Notify.Body("line 1\n" + zh));
            T.Check("WeCom body: a text message, Chinese and line break kept", () => Json.Str(Json.At(j, "msgtype")) == "text" && Json.Str(Json.At(j, "text", "content")) == "line 1\n" + zh);

            var at = new DateTime(2026, 11, 2, 13, 45, 0);
            cfg.Root["language"] = "zh";
            string n = Notify.Format(cfg, "outOfCredits", "my_project | x", "out of credits [error]", at);
            var lines = n.Split('\n');
            string head = Json.Str(cfg.At("words", "zh", "notice", "prefix")) + Json.Str(cfg.At("words", "zh", "notice", "outOfCredits"));
            T.Check("notice: headline, session, detail, time: " + n.Replace("\n", " / "), () => lines.Length == 4 && lines[0] == head
                && lines[1] == "my_project | x" && lines[2] == "out of credits [error]" && lines[3] == "11-02 13:45");
            cfg.Root["language"] = "en";
            n = Notify.Format(cfg, "done", "ws | t", "", at);
            string enHead = Json.Str(cfg.At("words", "en", "notice", "prefix")) + Json.Str(cfg.At("words", "en", "notice", "done"));
            T.Check("English notice, empty detail left out: " + n.Replace("\n", " / "), () => n.Split('\n').Length == 3 && n.StartsWith(enHead) && enHead.StartsWith("[kiro-auto-continue] Until-done"));
            T.Check("unknown code: the code itself", () => Notify.Format(cfg, "whatever", "", "", at).Contains("whatever"));
            string zh10 = new string((char)0x4E2D, 10);
            T.Check("a short text is left alone", () => Notify.LimitUtf8("abc", 2000) == "abc");
            string cut = Notify.LimitUtf8(zh10, 20); int nb = Encoding.UTF8.GetByteCount(cut);
            T.Check("cut to at most 20 bytes, at a whole character: " + nb, () => nb <= 20 && cut.EndsWith("...") && cut.StartsWith(new string((char)0x4E2D, 5)));
            T.SetPath(cfg, "", "notify", "webhook");
            T.Check("no webhook: not sent, says why", () => Notify.Send(cfg, "x").StartsWith("no webhook"));
            T.SetPath(cfg, "https://example.com/hook", "notify", "webhook");
            T.Check("not a WeCom webhook: not sent", () => Notify.Send(cfg, "x").Contains("not a WeCom webhook"));
            T.SetPath(cfg, "https://open.feishu.cn/open-apis/bot/v2/hook/abc", "notify", "webhook");
            T.Check("a Feishu webhook: not sent either", () => Notify.Send(cfg, "x").Contains("not a WeCom webhook"));
        }
    }
}
