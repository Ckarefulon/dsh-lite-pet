---
name: dsh-pet-dev
description: "本仓库（dsh-pet 宠物插件）的开发总则：把任务边界、插件代码规范、注释规范与提交规范串成一条执行链，一次调用覆盖从改代码到提交的全过程。"
whenToUse: "在本仓库改动任何代码、注释、文档或准备提交时使用：用户说“改这个”“加个功能”“修一下”“提交推送”，或用户手动调用 dsh-pet-dev 时。"
metadata:
  language: "zh"
  category: "development"
disable-model-invocation: true
user-invocable: true
---

# dsh-pet 开发总则（dsh-pet-dev）

## 目标

本仓库唯一的入口 skill。它不重复四条规范的内容，只回答一件事：**这次请求该调用哪几个子 skill、按什么顺序执行**。

四个子 skill 是权威正文；本文件是索引与顺序表。二者冲突时以子 skill 为准。

## 四个子 skill

| 子 skill | 什么时候必须调用 |
| --- | --- |
| `task-guardrails` | 每个请求的第一步，任何工具调用之前 |
| `dsh-plugin-standards` | 要动 host/client 源码、加删文件、改接口、补测试 |
| `dsh-plugin-comment-spec` | 和 `dsh-plugin-standards` 一起调：写代码就要写注释 |
| `git-commit` | 要提交、要写或改写提交信息 |

四个子 skill 都已注册，直接按名字交给 `skill` 工具，不需要去找文件，也不需要读文件。门禁命令在 `dsh-pet/` 子目录下执行。

## 执行链

按顺序走，不要跳步，也不要一次全部调用。

1. **判类型**（`task-guardrails`）：纯对话 / 只读调查 / 需要变更。判定不清时往更保守的一类靠。
2. **先开口再动工具**：说出理解与打算看什么；只读也要先开口。
3. **写操作前停住**：说清「改什么、达到什么效果、怎么验证」，然后用弹窗等回复。预告不等于获批。
4. **动代码前一起调 `dsh-plugin-standards` 与 `dsh-plugin-comment-spec`**：前者定插件类型、文件规模、目录归属、导出面、测试位置，后者定模块头分档、导出符号一行摘要、只写 why。写代码必然带注释，不拆成两步。
5. **收尾跑门禁**（在 `dsh-pet/`）：`npm run typecheck`、`npm run lint`、`npm run format:check`、`npm run check:cycles`、`npm run check:runtime`、`npm run check:openapi`、`npm test`、`npm run build`。没跑过的不要说「已验证」。
6. **提交时调 `git-commit`**：从实际 diff 推断 `type(scope)`，正文写实现方式、行为变化与涉及文件；推送必须有明确指令。

## 调用方式

- 按需调用，别一次全调：纯问答一个都不调；只读调查只调 `task-guardrails`；改代码把 `task-guardrails`、`dsh-plugin-standards`、`dsh-plugin-comment-spec` 三个一起调；提交调 `git-commit`。
- 同一子 skill 在一次任务里调一次即可，不要重复调用，也不要凭记忆复述它的规则。
- 本 skill 只由用户点名触发；四个子 skill 由 AI 按需调用，用户也可以直接点名。

## 冲突与优先级

- **边界优先**：`task-guardrails` 决定「能不能动」，`dsh-plugin-standards` / `dsh-plugin-comment-spec` 决定「怎么写」，`git-commit` 决定「怎么记录」。规范与边界冲突时，先满足边界。
- **规范与代码不一致时报告，不擅自改**：发现规范里的说法与仓库现状冲突，先说明再问，不直接改规范。

## 守则

1. 没有明确授权，不产生任何副作用：不写文件、不装依赖、不动 Git。
2. 不把举例当任务：用户描述问题或吐槽时，先确认理解再动手。
3. 删而不留：确认无引用后删除，不留兼容层、不留旧实现、不留注释掉的代码（除非用户要求）。
4. 一处事实只写一遍：定位算术、常量、类型不复制第二份；规范内容不搬进本文件。
5. 提交信息来自本次 diff，不照抄本 skill 的示例文字。
