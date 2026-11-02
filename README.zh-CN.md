# kiro-auto-continue

[English](README.md)

Kiro 的会话有时做到一半就停了：网断了、模型太忙、额度用完了，或者 Kiro 重启了。人要是在睡觉、不在电脑前，任务就一直停在那儿，等你回来。

kiro-auto-continue 在后台盯着 Kiro 的会话。哪个这样停了，它就替你发一条消息让 agent 接着做，消息里附上你原来的要求，agent 知道刚才在做什么。它是 Windows 上的一个 PowerShell 脚本，Kiro 照常用，不用装插件，也不用重启。

## 能做什么

- 一轮断了（网络出错、模型太忙），让 agent 接着做。同一个会话接连断，最多续三回，每回前分别等 1、5、15 分钟。
- 你自己点了停止的，不管。
- Kiro 重启以后，把重启前正在跑的会话接上。
- 额度用完了不重试。等 Kiro 重启，或者别的会话又能正常跑完一轮（说明额度回来了），再接着做。
- 「做到完」模式（要用才开）：agent 不再停下来问要不要继续，一直做下去。
- 手机通知（企业微信或飞书群），还能在手机上通过企业微信机器人给它发命令（要用再设）。
- 有会话在跑的时候，电脑不会自己睡过去。

它只在键盘鼠标三分钟没人动时才打字，打完切回你原来看的签。输入框里你写了一半的话，它不碰。锁着屏也能用。

## 开始用

需要 Windows 10 或 11，以及 Kiro IDE（在 1.4.62 上测过）。

1. 从 [Releases](https://github.com/Qian-Anton/kiro-auto-continue/releases) 下载 zip，解压。
2. 可以先试试它跟你的 Kiro 合不合得来。它会开一个很短的测试会话（大约 0.2 积分），测完关掉：
   `powershell -NoProfile -ExecutionPolicy Bypass -File kiro-auto-continue.ps1 -SelfTest`
3. 双击 `start.cmd`。窗口开着它就一直在管，关掉就停。

它做的每件事都记在 `logs\kiro-auto-continue.log`。

## 做到完

在消息里任何地方写上 `【做到完】`（或 `[until-done]`）。agent 停下来问问题、问要不要做下一步时，它会让 agent 自己拿主意，接着做。agent 头一回说做完了，会让它对照你的要求再核对一遍。

agent 写出 `【完成】`、或者 `【卡住】` 加原因时就停；推满 30 回、或者连着三回推了 agent 都没干活，也会停。你自己发一条不带标记的消息，做到完也就结束了。

做完记得看 agent 最后的汇报：本来该你拿主意的事，是它替你定的。

## 手机上收通知

先给一个群建 webhook：

- 企业微信：进群，右上角「…」→ 消息推送 → 添加，复制 webhook 地址。
- 飞书：群设置 → 群机器人 → 添加机器人 → 自定义机器人，复制 webhook 地址。

在脚本旁边新建 `config.json`，把地址写进去：

```json
{ "notify": { "webhook": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=..." } }
```

飞书机器人开了签名校验的话，把密钥也写上：在 `"webhook"` 旁边加 `"secret": "..."`。

运行 `kiro-auto-continue.ps1 -TestNotify` 发一条试试。之后会话需要你的时候（接连断了三回、额度用完、做到完卡住了），还有做到完做完的时候，群里都会收到消息。每天早上 8 点还有一条过去 12 小时的小结。

地址别给别人，拿到它的人都能往群里发消息。`config.json` 不进 git。

## 在手机上发命令（企业微信）

1. 企业微信管理后台 →「智能专区」→「智能机器人」，建一个 API 模式的机器人，连接方式选「长连接」，可见范围只选你自己，复制 Bot ID 和 Secret。
2. 写进同一个 `config.json`：`{ "notify": { ... }, "remote": { "botId": "...", "secret": "..." } }`
3. 打开 kiro-auto-continue，窗口里会显示一个配对码。给机器人发「配对 码」，之后它只听你的。

然后给机器人发：

| 发 | 结果 |
|---|---|
| `状态` | 列出最近的会话，每个带编号。 |
| `续 2` | 2 号接着做。 |
| `停 2` | 2 号停下。 |
| `2 一段话` | 把这段话发给 2 号。2 号正忙的话先排着，这一轮做完再发，发进去了机器人告诉你。 |
| `清 2` | 撤掉排给 2 号、还没发的话。 |
| `新 项目名 一段话` | 在这个项目的 Kiro 窗口里开一个新会话，发这段话。只开着一个 Kiro 窗口时，可以不写项目名。 |
| `帮助` | 看这张表。 |

英文命令也认（`status`、`continue 2`、`stop 2`、`clear 2`、`new`、`help`），发语音也行。手机上的命令要往 Kiro 里打字时，它会等键盘鼠标 15 秒没人动。没设通知群的话，早上的小结由机器人发给你。

Secret 别外传，机器人只让你自己看得见：能指挥这个机器人的人，就能让你电脑上的 agent 跑命令。

## 设置

默认值都在 `config.default.json`。要改哪项，就在 `config.json` 里只写那一项，比如：

```json
{ "idleSeconds": 120, "untilDone": { "maxPushes": 50 } }
```

常改的几项：

| 设置 | 默认 | 意思 |
|---|---|---|
| `idleSeconds` | 180 | 键盘鼠标多少秒没人动，它才打字。 |
| `resume.maxTries` | 3 | 接连断几回就不续了，改成通知你。 |
| `untilDone.maxPushes` | 30 | 一次做到完最多推几回。 |
| `report.time` | `"08:00"` | 早上的小结几点发，写 `""` 就不发。 |
| `keepAwake.enabled` | `true` | 有会话在跑时不让电脑睡。 |
| `language` | `"auto"` | 它发出去的话用中文（`"zh"`）还是英文（`"en"`）；`"auto"` 看你的要求是中文还是英文。 |

其余的设置见 [docs/how-it-works.md](docs/how-it-works.md#all-settings)（英文）。

## 限制

- 只能在 Windows 上用。
- 它靠的是 Kiro 现在聊天面板的样子，Kiro 一升级可能就不灵了。`-SelfTest` 能看出还行不行。
- 要续的会话，签得开在 Kiro 里。
- 手机上的命令，要它开着、电脑醒着才收得到。

## 其他

它具体是怎么做的、全部命令行选项、怎么跑测试：[docs/how-it-works.md](docs/how-it-works.md)（英文）。

本项目和 Kiro、Amazon Web Services 没有关系。MIT 许可，见 [LICENSE](LICENSE)。
