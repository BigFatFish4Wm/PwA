# 交接笔记过滤（Coopanion 补丁）

Coopanion 的 handoff 笔记由 Cormini 的 `onHandoff` 机械渲染：每个事件、每条工具回执都进，
按年龄折叠成「前 N 字 + 已折叠」。爬塔时一份笔记 1.5 万字符，最坏 13 万。

这里换掉渲染：阿梦和汐的原话原样留最近几条，工具调用一行一条（工具名、参数摘要、结论），
系统噪声、上一份笔记、blob 正文、一天前没人再引用的回执丢掉。

## 为什么不是 world

World 只有事件、工具、配置和 `onHandoffEnded()` 通知；交接前的 `snapshot` 只交给 Persona
(`Persona.onHandoff`)，World 拿不到，也没有渲染钩子。所以：

- **拦截点**：Coopanion 自己的 Persona 子类 `core/persona.ts` 里的 `onHandoff`（Cortico 的
  扩展点，`vendor/cortico` 不动）。
- **渲染**：`core/handoff-filter.ts`，纯函数，无 I/O。
- **配置**：`core/companion.ts` 新增一个 config group `handoff`，Persona 页可改，下一次交接生效。

## 改的文件

安装目录（`D:\Program Files\coopanion\resources\app`）：

| 文件 | 改动 |
|---|---|
| `core/handoff-filter.ts` | 新增，渲染器 |
| `core/persona.ts` | `onHandoff` 覆盖：写文件、注入苏醒句与笔记、返回空尾；唤醒句改成描述新笔记 |
| `core/companion.ts` | `handoff` 配置组、默认值、把配置读给 Persona |

原文件第一次安装时备份为 `*.bak-handoff`；`install.mjs --revert` 还原。

## 装、验、撤

```powershell
node tools/coopanion-handoff/install.mjs           # 装（或更新）
node tools/coopanion-handoff/smoke.mjs             # 冒烟：跑一次 onHandoff，断言过滤规则
node tools/coopanion-handoff/preview.mjs           # 用当前 session 日志渲染一份预览
node tools/coopanion-handoff/install.mjs --revert  # 撤
```

`preview.mjs` 读 `data/home/companion/data/session-main.jsonl`（原始日志，只读），打印字符数对比和
渲染结果：

```
records       289
snapshot      363925 chars
note          6038 chars     ← 同一份快照，Cormini 原渲染器 11321
```

装完要**完全退出并重开 Coopanion**：Core 子进程启动时才加载这些文件。之后第一次交接会写一份新
笔记到 `home/companion/workspace/handoffs/`，日志里有一行「交接笔记已过滤」（entries / dropped /
chars）。

## 参数

设置 → Persona → 交接笔记（`config.json` 的 `handoff` 段）：

| 键 | 默认 | 作用 |
|---|---|---|
| `maxCharsPerReceipt` | 200 | 一条工具调用（名字 + 参数摘要 + 结论）的上限 |
| `collapseRepeats` | true | 同工具同参数的连续调用合并，注明次数 |
| `keepLastNStatusReads` | 1 | `sts2_view` / 截图 / 窗口列表这类读数，每个工具留最后一次 |
| `keepLastNPartnerLines` | 30 | 阿梦 `[typed]`/`[voice]`/`[answer]` 原话留几条 |
| `keepLastNPetLines` | 10 | 汐 `pet_say` 气泡文本留几条 |
| `maxToolLines` | 40 | 工具行总数上限（规格外补的；否则一次窗口几百条，每条 200 字还是长） |

只影响写进笔记的内容；`session-main.jsonl`、`transcript.jsonl`、工作区都不动。

## 取舍

- `end_turn` 不进笔记：它只说明这一轮结束，会以「下一步：等阿梦说话或下一条事件」体现。
- 笔记是一段（不再分「更早/最近」两段）；苏醒句相应改了。
- 「上次说到 / 正在做 / 下一步」由最后一条原话、最后一条气泡、最后一条非 `end_turn` 调用机械拼出，
  不调用模型。
- 24 小时规则是近似：一条回执超过一天，且后面没有调用提到它的 `call_id` 或首个字符串参数，就丢。
