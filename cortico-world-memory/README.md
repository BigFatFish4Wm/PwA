# memory · 记忆检索（Coopanion World）

把工作区里的 markdown 建成本地索引，**每次重建 system 前缀时自动捞几条相关笔记塞进去**，
再给模型一个 `memory_search` 按需翻旧账。纯本地、零 API 成本、索引可随时删掉重建。

这是 [`memory-retrieval-spec-2026-10-30.md`](../memory-retrieval-spec-2026-10-30.md) 的落地实现，
一开始要解决的三件事：

| 问题 | 这个 World 怎么解 |
|---|---|
| 想不起来去翻：全靠自己猜关键词 grep，猜错就等于没记忆 | 每轮自动检索（不靠模型想起来）+ `memory_search` 兜底 |
| 上下文一压缩就剩省略号：handoff 只保最近一段 | 索引管「以前写过什么」，交接后照样能捞回来 |
| 流水账多、事实少：改主意就再堆一条，新旧打架 | frontmatter `status` + `supersedes`，被推翻的条目自动失效 |

## 装

```powershell
# 1) 链接进 Coopanion 的扩展目录
powershell -ExecutionPolicy Bypass -File tools\link-world.ps1 -Name cortico-world-memory

# 2) 在部署配置里启用（顺带写好默认值）
node tools\enable-memory-world.mjs

# 3) 重启 Coopanion
```

启用后：Coopanion → 世界 → **记忆检索**，两个面板（索引状态 / 试检索）+ 一组配置项。

## 它在前缀里长什么样

World 的环境段（`## memory`）里会有一段：

```
## 相关记忆（自动检索，可能不全）
- [[notes/pwa-run-state.md]] …这局的药水只有敏捷药水，血 47/80，钱 0…（2026-10-30）
- [[PARTNER.md]] 每天 token 上限 5 元…（2026-10-20）
```

- 条数 `worlds.memory.topK`（默认 5），**总注入硬上限 2000 token**；
- 同一批记忆**连续两轮不重复注入**（换了话题才重置）；
- 预算到点就先不注，检索在后台跑完，下一条前缀生效——绝不拖住前缀构建。

> 注入点是 system 前缀（Coopanion 在会话开始、上下文交接、前缀重载时重建它，
> 这也是放 handoff 的位置）。会话中间想立刻翻东西，用 `memory_search` 工具。

## 检索怎么算分

```
FTS5(trigram)  ┐
LIKE 兜底       ├─ RRF 融合 (Σ 1/(60+rank)) ─ × (0.55 + 0.95×覆盖率) ─ × (1 + 0.3×30天引用数) ─ × 时间衰减
本地向量        ┘
```

- **FTS5 trigram**：中文按三字滑窗切，不需要分词器。查询也会拆成三字滑窗再 OR ——
  因为 trigram 只认「逐字出现」，一句自然问句整段匹配几乎不可能。
- **LIKE 兜底**：两字词（`药水`）与小于 3 字的词 trigram 索引不到，单独扫一遍原文。
- **本地向量**：默认是内置的哈希 n-gram 向量（零依赖、CPU 毫秒级）；配了
  `worlds.memory.embedModel`（`model.onnx` + `vocab.txt`）且装了 `onnxruntime-node`
  就换成真 embedding。**不联网、不调任何云端 API。**
- **覆盖率**：命中的二元组按候选池内的 IDF 加权后求比例，压掉 `怎么`/`什么` 这类
  到处都是的词，让 `六级`、`药水` 这种真正区分得开的词说了算。
- **时间衰减**：半衰期 90 天、最低 0.15 —— 旧笔记会淡出，但不会彻底消失。
- **引用计数**：被注入/被搜到过的条目，30 天内每多一次就 × (1+0.3)。

## 事实条目：别让记忆打架

笔记开头写 YAML 头即可：

```yaml
---
updated: 2026-10-30
status: active          # active | unfinished | superseded | archived
supersedes: notes/old-potion-rule.md
tags: [sts2, potion]
---
```

- 检索默认只回 `active` 与 `unfinished`；`archived` 永不返回，`superseded` 要用
  `status: superseded` 才翻得出来。
- 新条目写 `supersedes`，旧条目**自动**变 `superseded` 并记下 `supersededBy`，
  不用手改两份。
- 头部还可以有 `title`（显示标题）、`date`/`last_updated`（都认作 updated）。

## 工具

| 工具 | 干什么 |
|---|---|
| `memory_search` | 检索：`query` / `k` / `status` / `maxAgeDays` / `path` |
| `memory_index` | 增量重建（`reset: true` 从头建） |
| `memory_status` | 引擎、目录、多少篇/多少段、上一轮注入哪几条、上次扫描 |

## 索引

- 位置：`<workspace>/.index/memory.db`（SQLite，WAL）。**随时可以删**，下次扫描从工作区重建，
  工作区里的文件一个不动；面板上的「存储」区块也能一键清。
- 增量：`mtime`/`size` 变了才重读，`sha1` 也变了才重切——只有 mtime 抖动不会白干活。
- 触发：`fs.watch`（写文件 800ms 内）+ 每 `scanSeconds`（默认 30）定时 + 每次检索前
  按需补扫（3 秒节流）。
- 跳过：`.git`、`node_modules`、`blobs`、`log`、`data`、`archive`、`tmp` 与所有点开头目录
  （`worlds.memory.skipDirs` 可改）。
- 分块：按标题/空行切成 300–800 字的段，代码块不切；每段带上文档标题与小节名。
- 规模参考：一个 99 篇 / 6500 段的工作区，首扫约 3 秒、索引约 55MB、单次检索约 50–90ms。

## 兜底

- 没有 `node:sqlite`、索引打不开、目录写不了 → **不报错**，检索退回直接扫文件的 grep
  （面板会显示 `grep 兜底`），对话照常。
- 检索超时 → 这一轮不注入，不阻塞前缀构建。
- 任何异常都只记 warn，绝不从 `envPromptVars()` 里抛——抛了 Core 会把整个 World 卸掉。

## 配置（`worlds.memory.*`）

| 键 | 默认 | 说明 |
|---|---|---|
| `enabled` | false | 由部署开启 |
| `workspace` | `<部署>/workspace` | 索引哪个目录 |
| `indexPath` | `<workspace>/.index/memory.db` | 索引文件 |
| `inject` | true | 关掉后只能手动 `memory_search` |
| `topK` | 5 | 每轮注入几条 |
| `budgetTokens` | 2000 | 注入 token 硬上限 |
| `minCoverage` | 0.12 | 低于它的候选不进前缀 |
| `halfLifeDays` | 90 | 时间衰减半衰期；0 = 不衰减 |
| `refBoost` | 0.3 | 30 天内每次引用的加成 |
| `maxAgeDays` | 0 | 只搜这么多天内改过的 |
| `vector` | auto | `off` 只用全文 |
| `embedModel` | 空 | ONNX 模型目录（可选） |
| `scanSeconds` | 30 | 后台扫描间隔 |
| `skipDirs` | 见上 | 逗号分隔 |
| `queryHint` | 空 | 钉一个当前话题词，拼进自动检索的 query |
| `humanSources` | 空 | 只把这些来源当「人说的话」 |
| `maxChars` | 4000 | 工具回执上限 |
| `chunkMin`/`chunkMax` | 300 / 800 | 分块长度 |
| `maxFileKb` | 2048 | 单篇最大读取量 |

## 许可

MIT。不包含任何第三方二进制；ONNX 是**可选**依赖，装不装都能跑。
