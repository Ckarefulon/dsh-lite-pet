# dsh-lite-pet

轻量的 **DSH 系统级桌面桌宠**：不走 Electron、不嵌 Chromium，桌宠是一个约 1 MB 的原生 Windows 进程（.NET Framework 自带运行时 + Media Foundation 解码），盯着 DSH 的会话事件在桌面上动画。

素材（动画、立绘风格）来自 [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet)，通过 `git subtree` 同步；代码与上游无关。

## 功能

- **点击桌宠（或气泡）** 打开 DSH 窗口；**右键菜单**：动作 / 查余额 / 关闭桌宠。
- **自动播放**：待机动画之外，随机播放素材库里的非工作动画（点击回应、碎碎念、余额、季节小动作……）。
- **工作状态动画**：思考 / 忙碌 / 等待确认 / 完成 / 出错 各自对应动画。
- **气泡**：仅工作时显示 Agent 最新一段正文；等待回答或批准时最左侧亮 **黄色圆点**；回合结束后显示「“会话名”已完成」+ **绿色圆点**（不自动消失，直到下一次工作）。
- **查余额**：气泡显示余额 + 播放对应余额动画。
- **逐像素透明 + 点击穿透**：透明像素与宠物边缘内侧 4 px 的环形区域都不吃点击，直接落到下层窗口。
- **设置里只有一个开关**：设置 → 通用 → 桌面桌宠。

## 环境要求

- Windows 10/11（桌宠进程依赖 .NET Framework 4.x 与 Media Foundation，两者都是系统自带）。
- DSH Desktop（点桌宠时用 `dsh://open` 聚焦窗口；没有注册该协议时退化为打开 Web GUI 地址）。

## 安装

```bash
dsh plugin install dsh-lite-pet
```

或在 DSH 的插件管理里安装本包。安装后到 设置 → 通用 打开「桌面桌宠」。

## 开发

```bash
git subtree add --prefix=vendor/upstream https://github.com/PC2005-cloud/dsh-pet.git main --squash
npm run build          # 转码素材 + 编译桌宠进程
npm run test:pet       # 无窗口冒烟测试（可选）
```

- `tools/build-media.mjs`：WebM → 彩色 H.264 + 灰度 alpha H.264（Media Foundation 无法直接解码 VP9 alpha，这一步是必需的）。
- `tools/build-pet.mjs`：用 Windows 自带的 `csc.exe` 编译 `src/pet/*.cs`。
- 素材同步：`.github/workflows/sync-assets.yml` 每天 `git subtree pull` 上游，变更即提交。

## 结构

| 路径 | 作用 |
| --- | --- |
| `index.js` | 宿主半边：会话事件 → 桌宠状态、余额查询、`/api/dsh-lite-pet/*` 两个 JSON 端点 |
| `client/index.js` | 浏览器半边：设置 → 通用 里的开关行 |
| `src/pet/` | 桌宠进程（C#）：分层窗口、Media Foundation 解码、合成、气泡、原生菜单 |
| `tools/` | 素材转码、编译、冒烟测试、发布前检查 |
| `runtime/` | 构建产物（不入库，CI 构建后进 npm 包） |

## 许可

MIT。动画素材来自上游仓库，署名见上游仓库。
