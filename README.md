# dsh-lite-pet

轻量的 **DSH 系统级桌面桌宠**。不套 Electron、不嵌 Chromium：桌宠是一个约 1 MB 的原生 Windows 进程（系统自带 .NET Framework 4.8 + Media Foundation 解码），盯着 DSH 的会话事件在桌面上动画。

素材（动画、立绘）来自 [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet)，由 `git subtree` + GitHub Actions 每日同步；**代码与上游无关**。

## 功能

- **点击桌宠或气泡** 打开 / 聚焦 DSH 窗口。
- **右键菜单**：动作（按分类换动画）/ 调整大小（小·标准·大·特大）/ 查余额 / 关闭桌宠（会记住关闭状态）。
- **自动播放**：待机之外随机播放素材库里的非工作动画（点击回应、碎碎念、余额、季节小动作……共 90+ 段）。
- **工作状态动画**：思考 / 忙碌 / 等待确认 / 完成 各自对应动画。
- **气泡**：仅工作时显示 Agent 最新一段正文；**等待回答或批准**时最左侧亮黄色圆点；回合结束显示「“会话名”已完成」+ 绿色圆点，不自动消失，直到下一次工作。
- **查余额**：气泡显示「余额 ¥x（赠金 ¥y）」并播放对应余额动画。
- **拖拽**：按住宠物移动位置，按住气泡上下拖动调气泡距离，都会记住。
- **逐像素透明 + 点击穿透**：透明像素、以及宠物实体边缘内侧 6 px 的环形区域都不吃点击，直接落到下层窗口。
- **程序内只有一个开关**：设置 → 通用 → 桌面桌宠。

## 环境要求

- Windows 10/11（依赖系统自带的 .NET Framework 4.x 与 Media Foundation，不需要额外安装运行时）。
- DSH Desktop：点击桌宠时通过 `dsh://open` 聚焦主窗口；未注册该协议时退化为打开 Web GUI 地址。

## 安装

在 DSH 的插件管理里安装 `dsh-lite-pet`（或在 profile 里 `dsh plugin install dsh-lite-pet`），然后到 **设置 → 通用 → 桌面桌宠** 打开开关。

## 实现

| 路径 | 作用 |
| --- | --- |
| `index.js` | 宿主半边：`session/event` → 桌宠状态机、余额查询、`/api/dsh-lite-pet/state` 与 `/enabled` 两个 JSON 端点 |
| `client/index.js` | 浏览器半边：注册 `settings.general.item` 一行开关（唯一程序内入口） |
| `src/pet/` | 桌宠进程（C#，`csc.exe` 编译）：分层窗口、Media Foundation 解码、合成、气泡、原生菜单 |
| `tools/build-media.mjs` | WebM → 彩色 H.264 + 灰度 alpha H.264，并生成 `index.json` 目录 |
| `tools/build-pet.mjs` | 用系统自带 `csc.exe` 编译 `src/pet/*.cs` |
| `runtime/` | 构建产物（不入库；发布时随 npm 包分发） |

### 为什么转码

桌宠窗口是 layered window，必须逐像素 alpha。Windows 的 Media Foundation 不能解码 WebM/VP9 的 alpha 旁路流，所以构建时把每段动画转成**两路 H.264**：彩色轨 + 灰度 alpha 轨；运行时用 Source Reader 解出两帧后自行预乘合成。代价是构建依赖 ffmpeg，收益是运行时没有 Chromium。

### 资源占用（本机实测，1080p / 125% 缩放）

- 内存：约 75–90 MB（Media Foundation 解码器占大头）。
- CPU：待机 12 fps 播放约 13% 单核；工作/动作 24 fps 约 20–26% 单核。
- 磁盘：`runtime/media` 约 65 MB（105 段动画，640×360，彩色 CRF 23）。

## 开发

```bash
# 1) 素材子树（CI 里由 Actions 首次自动添加；本地也可以手动跑）
git subtree add --prefix=vendor/upstream https://github.com/PC2005-cloud/dsh-pet.git main --squash

# 2) 构建
npm run build         # 转码素材 + 编译桌宠
npm run test:pet      # 冒烟：启动 → ready → 切状态 → quit（需要交互式桌面）
```

## 发布

- 打 `v*` tag（或手动触发）运行 `.github/workflows/publish.yml`：装 ffmpeg → 转码 → 编译 → `npm publish` → 创建 GitHub Release。
- 发布走 **npm Trusted Publishing（OIDC）**，仓库里不需要任何 token secret。需要在 npmjs.com 上给 `dsh-lite-pet` 配置 Trusted Publisher：仓库 `Ckarefulon/dsh-lite-pet`、工作流文件 `publish.yml`。
- `.github/workflows/sync-assets.yml` 每天 `git subtree pull` 上游素材，有变更即提交并推送。

## 许可

MIT。动画素材来自上游仓库，版权与署名见 [上游仓库](https://github.com/PC2005-cloud/dsh-pet)。


