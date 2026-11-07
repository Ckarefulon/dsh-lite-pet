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

- Windows 10/11（依赖系统自带的 .NET Framework 4.x，不需要额外安装运行时）。
- DSH Desktop：点击桌宠时通过 `dsh://open` 聚焦主窗口；未注册该协议时退化为打开 Web GUI 地址。

## 安装

在 DSH 的插件管理里安装 `dsh-lite-pet`（或在 profile 里 `dsh plugin install dsh-lite-pet`），然后到 **设置 → 通用 → 桌面桌宠** 打开开关。

第一次打开开关时，插件会在后台从 GitHub Release 拉取**完整动作素材包**（约 280 MB），缓存到 `~/.dsh/dsh-lite-pet-media/`，桌宠下次重启就换用全套 113 段动画。拉取失败不影响使用：包内自带的 21 段核心动画（待机 / 工作四态 / 余额六档 / 点击回应 / 睡觉）已经能完整跑起来，只是少了那些随机小动作。

## 实现

| 路径 | 作用 |
| --- | --- |
| `index.js` | 宿主半边：`session/event` → 桌宠状态机、余额查询、完整素材包下载与缓存、`/api/dsh-lite-pet/state` 与 `/enabled` 两个 JSON 端点 |
| `client/index.js` | 浏览器半边：注册 `settings.general.item` 一行开关（唯一程序内入口） |
| `src/pet/` | 桌宠进程（C#，`csc.exe` 编译）：分层窗口、libwebp 解码、合成、气泡、原生菜单 |
| `tools/build-media.mjs` | 分类 WebP 素材并生成 `index.json`（`--tier core` 默认，`--tier full` 出全套） |
| `tools/pack-media.mjs` | 把全套装成 `runtime/dsh-lite-pet-media-full.zip` 作为 Release 资产 |
| `tools/build-pet.mjs` | 用系统自带 `csc.exe` 编译 `src/pet/*.cs` |

### 素材：动图 WebP，直接在 libwebp 里解

每段动画就是一个**动画 WebP**（带 alpha），放在 `vendor/webp/`（114 段，由上游 WebM 母版用 `ffmpeg -c:v libwebp_anim` 转出）。`build-media.mjs` 只做分类和生成 `index.json`，运行时用随包的 `libwebp`（约 390 KB，BSD 许可）逐帧解码。

解码请求的是 `MODE_bgrA`——libwebp 直接吐出**预乘 BGRA**，正好是分层窗口要的格式，所以渲染路径里没有逐像素预乘；而且对预乘值做插值在数学上才是正确的缩放方式，边缘不会发灰。

### 两档分发

| 档位 | 内容 | 体积 | 分发方式 |
| --- | --- | --- | --- |
| core | 21 段（idle / 工作四态 / 余额六档 / 点击五段 / 睡觉三段） | 48 MB | npm 包内 |
| full | 113 段（再加 87 段随机小动作、5 段碎碎念） | 281 MB | GitHub Release 资产，首次启用时下载缓存 |

### 资源占用（本机实测，1080p / 125% 缩放，0.75× 窗口）

- 内存：约 25–35 MB（只加载当前这一段的 WebP 数据）。
- CPU：待机约 7.7% 单核，工作状态约 9.1%；每帧解码 2.2 ms、融合渲染约 1.1 ms。
- 磁盘：核心集 48 MB；全套缓存 281 MB。

### 性能取舍（实测数据）

| 方案 | 素材体积 | 解码 CPU |
| --- | --- | --- |
| 动图 WebP（现在） | 281 MB | 3.3% 单核 |
| H.264 堆叠软解（0.1.4） | 62 MB | 8.6% 单核 |

桌宠是常驻进程，CPU 是无时无刻都在付的成本，磁盘是一次性的下载，所以选了 WebP。GPU 硬解那条路在本机不成立：`IMFDXGIDeviceManager::ResetDevice` 恒返回 `E_HANDLE`（连裸 vtable 调用也一样），详见 `docs/gpu-decode-plan.md`。

## 开发

```bash
# 1) 素材子树（CI 里由 Actions 首次自动添加；本地也可以手动跑）
git subtree add --prefix=vendor/upstream https://github.com/PC2005-cloud/dsh-pet.git main --squash

# 2) 构建
npm run build            # 核心素材 + 编译桌宠
npm run build:media:full # 全套素材并打包成 Release 资产
npm run test:pet         # 冒烟：启动 → ready → 切状态 → quit（需要交互式桌面）
npm run test:zip         # 校验素材包的写入与解压
```

## 发布

- 打 `v*` tag（或手动触发）运行 `.github/workflows/publish.yml`：构建核心素材 → 编译桌宠 → 打全套素材包 → `npm publish` → 创建 GitHub Release 并附上素材包。
- 发布走 **npm Trusted Publishing（OIDC）**，仓库里不需要任何 token secret。需要在 npmjs.com 上给 `dsh-lite-pet` 配置 Trusted Publisher：仓库 `Ckarefulon/dsh-lite-pet`、工作流文件 `publish.yml`。
- `.github/workflows/sync-assets.yml` 每天 `git subtree pull` 上游素材，有变更即提交并推送。


## 许可

MIT。动画素材来自上游仓库，版权与署名见 [上游仓库](https://github.com/PC2005-cloud/dsh-pet)。


