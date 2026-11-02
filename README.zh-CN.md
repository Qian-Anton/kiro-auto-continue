# kiro-auto-continue

[English](README.md)

人不在电脑前的时候，让 Kiro IDE 里的会话接着跑。只支持 Windows，是一个 PowerShell 脚本，不用重启 Kiro，也不改 Kiro 自己的任何东西。

- **断了的会自动续上。** 有时一轮会自己停掉：页脚只有 `Elapsed time`，没有 `Est. Credits Used`。这时它会发一条续跑消息，里面附上你最后一条任务的原文。哪怕 agent 根本没收到你那条消息（比如「模型流量太大」那种），它也知道该做什么。
- **做到完（可选，按条开）。** 在消息里写上 `【做到完】`（或 `[until-done]`）。agent 中途停下来问你问题、问要不要做下一步时，它会叫 agent 自己拿主意接着做，直到写出 `【完成】`，或者写 `【卡住】` 加原因。
- **你自己点停止的，不碰。**
- **手机上收消息（可选）。** 会话需要你、或者做到完做完了，它往企业微信或飞书的群里发一条短消息。
- **有会话在跑，Windows 就不闲置睡眠。** 半夜不会自己睡过去。
- **在手机上发命令（可选）。** 通过企业微信的智能机器人：问每个会话在干什么、让一个会话接着做，或者往里发一段话。

## 怎么判断

它读 Kiro 自己的会话记录（`%USERPROFILE%\.kiro\sessions\<工作区>\sess_<id>\messages.jsonl`）。每一轮的结尾都有一条 `turn_end`，`stopReason` 写着这一轮是怎么结束的；`error` 的那一轮还记着聊天里显示的错误。光是看，它不切签。

| 这一轮怎么结束的 | 它做什么 |
|---|---|
| `error`（断了，页脚没有积分） | 发续跑消息：「上一轮中断了；记得刚才在做什么就从做到的地方接着做，别从头来；不记得就照下面这条原消息做」。每次发之前分别等 1、5、15 分钟；接连断三回就不管了，日志里记 `NEEDS YOU`。正常做完一轮，次数清零。 |
| `error`，原因是额度用完（`UsageLimitReachedError`、`OverageLimitReachedError`） | 先不发：额度回来以前，重试也是白发。日志记 `NEEDS YOU - out of credits`。等别的会话又正常跑完一轮（说明额度回来了），或者 Kiro 重启以后（比如你重新登录了），把它接着续上，不管隔了多久。 |
| 被 Kiro 重启打断 | 等 Kiro 起来满一分钟，发续跑消息，话里多一句「你开的终端和后台进程可能都没了」。Kiro 关掉时正在跑的轮次，记录里根本没有 `turn_end`；要等你下次往那个会话里打字，Kiro 才补一个写着 `cancelled` 的结尾（其实不是你点的）。所以这两种都算：记录里还开着、但 Kiro 是在它最后一行之后才起来的；Kiro 重启前不久以 `error` 结束的。 |
| `cancelled`（你点了停止） | 什么也不做。这个会话的做到完也一起关掉。 |
| `end_turn`，没开做到完 | 什么也不做。 |
| `end_turn`，开了做到完，没写标记 | 推它接着做。第一次推的时候把规矩讲一遍：要定的事自己按推荐的定，每个决定写进最后的汇报；别问要不要做下一步，直接做；只在写 `【完成】`，或写 `【卡住】` 加原因时才停。每推五回再带一次目标。 |
| `end_turn`，最后两行有 `【完成】` | 第一次先让它对照目标逐条核对一遍；再写 `【完成】` 就停下，记进日志。 |
| `end_turn`，有 `【卡住】 原因` | 停下，日志里记 `NEEDS YOU` 和原因。 |

做到完还会在这几种情况下停：推满 30 回；连着三回推了它都没干活（没调工具、回得很短）；你发了一条不带标记的新消息。

## 怎么打字

只有要发东西时才动界面。先等键盘鼠标三分钟没人动，然后：

1. 按 `session.json` 里的标题找到这个会话的签。
2. 切到那个签，核对面板上最后一条用户消息和记录里的一样。
3. 通过 Windows 界面自动化（UI Automation 和 MSAA）把字填进输入框。不动鼠标，也不按键盘。
4. 点发送，等签上显示 Working，再切回你原来看的那个签。

输入框里有你没发出去的草稿时，它不碰。

## 用法

要求：Windows 10 或 11（自带 Windows PowerShell 5.1），Kiro IDE，聊天面板开着。在 Kiro 1.4.62 上测过。

1. 把这个文件夹下载下来。
2. 先试一下能不能用（会开一个很短的会话，大约花 0.2 积分，测完关掉）：
   `powershell -NoProfile -ExecutionPolicy Bypass -File kiro-auto-continue.ps1 -SelfTest`
3. 双击 `start.cmd`。窗口开着就一直在管；关掉窗口就停。

它做的每件事都记在 `logs\kiro-auto-continue.log`。想看它对最近的会话会怎么办、又不碰任何东西，用 `-Inspect`；只想看它怎么判断、不真打字，用 `-DryRun`；`-Only <字>` 只管标题里带这几个字的会话。

60 分钟里只要有哪个会话（或子代理）写过记录，它就挡住 Windows 的闲置睡眠。屏幕照样会关；合盖、按电源键、开始菜单里点睡眠，照样会睡。用电池时会费电，不想要就把 `keepAwake.enabled` 设成 false。

## 手机上收消息

会话需要你的时候（接连断三回、额度用完、做到完卡住或停了、消息发不出去），还有做到完做完的时候，它能通过群机器人的 webhook 告诉你：

- **企业微信**：进群，右上角「…」→ 消息推送 → 添加，起个名字、保存，复制 webhook 地址（`https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…`）。
- **飞书**：群设置 → 群机器人 → 添加机器人 → 自定义机器人，复制 webhook 地址。开了签名校验的话，把密钥也复制下来。

拿到这个地址的人都能往群里发消息，所以写进不进 git 的 `config.json`：

```json
{ "notify": { "webhook": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=...", "secret": "" } }
```

`kiro-auto-continue.ps1 -TestNotify` 发一条测试消息。`notify.events` 选发哪几种：`needsYou`（需要你）、`done`（做到完做完了）。

## 在手机上给电脑发命令（企业微信）

用企业微信的智能机器人，可以在手机上问电脑每个会话在干什么、让一个会话接着做，或者往会话里发一段话。看门工具自己连到企业微信（`wss://openws.work.weixin.qq.com`，只往外连），不用公网 IP，也不用域名。

1. 企业微信管理后台 →「智能专区」→「智能机器人」，创建一个 API 模式、连接方式选「长连接」的机器人，可见范围只选你自己，复制 Bot ID 和 Secret。
2. 写进 `config.json`（不进 git）：`{ "remote": { "botId": "...", "secret": "..." } }`。`kiro-auto-continue.ps1 -TestRemote` 试一下企业微信认不认；先把看门关掉，一个机器人企业微信只留一条连接。
3. 打开看门。还没有账号配对时，它的窗口和日志里有一个配对码，给机器人发「配对 码」。你的账号会存进 `remote-allow.json`（不进 git），之后它只听这个账号的。

| 你发 | 它做 |
|---|---|
| `状态` | 列出 24 小时里写过记录的会话，新的在前，每个带编号和上一轮怎么结束的。 |
| `续 2` | 让 2 号接着做：断了的，发续跑消息（附上你的任务原文）；别的，发一句「继续」。 |
| `停 2` | 让 2 号停下，和点它的停止键一样。 |
| `2 加一段话` | 把这段话发进 2 号会话，和你在电脑上打字一样。2 号正在跑的话，这段话先排着，等这一轮正常做完再发（断了的先续；你点了停止的，接着排着），发进去了机器人告诉你。 |
| `清 2` | 撤掉排给 2 号、还没发出去的话。 |
| `新 工作区 加一段话` | 在这个工作区的 Kiro 窗口里开一个新会话，发这段话。只开着一个 Kiro 窗口时，可以不写工作区。 |
| `帮助` | 列出这几条。 |

它只认文字；语音也行，企业微信会转成字。每个会话的编号一直不变，群里的通知也带上编号。打字前它等键盘鼠标 15 秒没人动。谁能指挥这个机器人，谁就能让电脑上的 agent 跑命令：Secret 别给别人，机器人只对你自己可见。

## 早上的状态

每天 08:00 它多发一条通知：过去 12 小时里跑过的会话，每个带编号、上一轮怎么结束的、续了几回；再用一句话说这段时间它续了几回、推了几回、几个要你看。发到通知去的地方（`notify.webhook`）；没设群的话，发给配对过的账号。一天一条，只在 08:00 到 11:00 之间发，这段时间一个会话都没跑过就不发。`kiro-auto-continue.ps1 -Report` 现在就出一份、发到群里。`report.time` 改时间，设成 `""` 就关掉。

## 设置

默认值在 `config.default.json`。要改哪项，就在同一个文件夹里建一个 `config.json`，只写要改的几项，它会盖在默认值上面（`config.json` 不进 git）。比如：

```json
{ "idleSeconds": 120, "untilDone": { "maxPushes": 50 }, "language": "zh" }
```

| 设置 | 默认 | 意思 |
|---|---|---|
| `idleSeconds` | 180 | 键盘鼠标这么多秒没人动，它才打字。 |
| `settleSeconds` | 20 | 一轮结束后等这么久再判断。 |
| `resume.delaysSeconds` | 60, 300, 900 | 断了以后，第一、二、三次续跑前各等多久。 |
| `resume.maxTries` | 3 | 接连断几回就不管了。 |
| `resume.catchUpMinutes` | 60 | 它一开起来，前这么多分钟里断掉的轮次也接着续，所以重开它不会漏掉。 |
| `resume.limitErrors` | `UsageLimitReachedError`、`OverageLimitReachedError` | 哪些错误类型算额度用完：不重试，等额度回来（或者 Kiro 重启）。 |
| `resume.creditWaitHours` | 24 | 它开起来时，前这么多小时里额度用完的会话，也等额度回来再续。 |
| `report.time`、`report.hours`、`report.windowHours` | 08:00、12、3 | 早上的状态几点发（`""` 就不发）、说前多少小时的事、过了点以后最晚多久还发。 |
| `restartWindowMinutes` | 10 | Kiro 重启前这么多分钟以内的 `error` 算被重启打断；Kiro 起来以后这么多分钟内打开它，才会去接重启打断的轮次。 |
| `kiroStartupSeconds` | 60 | Kiro 起来以后先等这么久再往里打字。 |
| `keepAwake.enabled`、`keepAwake.minutes` | true、60 | 这么多分钟里有记录写过，就挡住 Windows 的闲置睡眠。 |
| `notify.webhook`、`notify.secret` | | 企业微信或飞书群机器人的 webhook 地址；飞书开了签名校验时的密钥。空着就不发消息。 |
| `notify.events` | `needsYou`、`done` | 发哪几种消息。 |
| `remote.botId`、`remote.secret` | | 企业微信智能机器人（长连接）的 Bot ID 和 Secret。空着就不接手机上的命令。 |
| `remote.allowFrom` | | 配对的账号以外，还听哪些企业微信账号（userid）的。 |
| `remote.idleSeconds`、`remote.waitSeconds` | 15、60 | 手机上的命令要打字前，等键盘鼠标这么多秒没人动，最多等这么久。 |
| `remote.listHours`、`remote.maxList` | 24、10 | 「状态」列哪些会话。 |
| `untilDone.enabled` | true | 设成 false 就整个关掉做到完。 |
| `untilDone.maxPushes` | 30 | 一次做到完最多推几回。 |
| `language` | auto | 发出去的话用 `zh` 还是 `en`；`auto` 看你的任务里有没有中文。 |
| `words.zh`、`words.en` | | 三个标记和它发的每一句话。别写只有 `=` 或 `-` 的一行：Kiro 会把它上面那行显示成标题。 |

## 限制

- 只能在 Windows 上用：它靠 Windows 界面自动化操作 Kiro 的聊天面板。
- 它靠的是 Kiro 现在的聊天面板和会话记录的样子。Kiro 一升级就可能对不上；`-SelfTest` 和 `-Inspect` 能看出是哪一步不行。
- 它开着的时候结束的、被 Kiro 重启打断的轮次它才管。Kiro 重启时它没开的话，在 Kiro 起来以后 10 分钟内打开它：Kiro 起来前 10 分钟里还写过记录的、被打断的轮次它也会接上。
- 会话的签要开在聊天面板里。两个签标题一样的，它不碰。
- 续跑时只能附上你消息里的文字，图片和附件发不了。
- Kiro 最小化或者压在别的窗口后面时，面板更新得慢（几秒，有时要一分钟），它会等、核对过再打字。锁屏没关系：锁着屏它照样切签、打字、发送。
- 做到完模式下，本该你拿主意的事是 agent 替你定的，记得看它最后的汇报。
- 手机上的命令要看门开着、电脑醒着才收得到。一个小时没有会话在跑，Windows 又会照常闲置睡眠。

本项目和 Kiro、Amazon Web Services 没有关系。

## 测试

`tests\Policy.Tests.ps1`、`tests\Records.Tests.ps1`、`tests\Notify.Tests.ps1`、`tests\Remote.Tests.ps1`、`tests\Report.Tests.ps1` 不用开 Kiro 就能跑，也不会真发消息、连企业微信。`tests\fast.json` 是拿测试会话试用时的设置：等待都很短，你自己点停止也当成断了。一定要配上 `-Only` 或 `-OnlyNew` 用，免得碰到真会话。

## 许可

MIT，见 [LICENSE](LICENSE)。
