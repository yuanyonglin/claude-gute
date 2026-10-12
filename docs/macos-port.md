# macOS 移植方案

状态：第 1 阶段的代码已在 Windows 上写好（见“进度”），尚未在 Mac 上运行过。在 Mac 上由 Claude Code 接手；本文是交接文档，先读完 README.md 和 CLAUDE.md 再读这里。

## 进度

已完成（Windows 上验证过的标 ✓）：

- ✓ 共用代码平台化，`guard-lib.cjs` 新增 `coreExecutable`、`clashVergeDir`、`startGuardCommand`，被 `guard-daemon.cjs`、`node-selection.cjs`、`guard-hook.cjs` 使用；`gate-control.cjs` 提示按平台。测试 `platform.test.cjs`。Windows 行为不变，全部测试通过。
- ✓ `start-guard.sh`：在 Windows Git Bash 下用端口偏移的隔离副本实测过（启动后 READY；已在运行时直接返回 0）。Mac 上未运行。
- `deploy.sh`：只做过 `sh -n` 语法检查。依赖 `lsof`、`pkill`、`open`，未实测。
- `install-launchagent.sh`：生成并加载 `~/Library/LaunchAgents/local.claude-guard.start.plist`。未实测。
- `.gitattributes` 保证 `.sh` 为 LF；`.gitignore` 加了 `mihomo-gate`、`ClaudeGuard.app/`、`macos/ClaudeGuard/.build/`。

未开始：第 2 阶段菜单栏 App（Swift，需在 Mac 上写和编译）、第 3 阶段系统层兜底。

## 在 Mac 上接手：按顺序做

1. **准备文件**（都不进 git）
   - 下载 mihomo 官方 darwin 构建（Apple 芯片用 arm64），放到仓库根目录命名 `mihomo-gate`，`chmod +x mihomo-gate`；首次运行若被 Gatekeeper 拦，`xattr -d com.apple.quarantine mihomo-gate`。
   - `cp guard-policy.example.json guard-policy.json`，把 `interfaceName` 改成 Mac 的网卡（`networksetup -listallhardwareports` 查，Wi-Fi 通常 `en0`），端口可保持默认。
   - `guard-node.json`：从 Windows 机器拷贝（含凭据，走私密渠道，不要进 git）；或先装好 Clash Verge 后用节点选择流程生成。
   - `cp tray-settings.example.json tray-settings.json`（第 2 阶段才用到）。
2. **验证共用代码**：`node --test *.test.cjs` 全部通过。
3. **隔离试跑**（不要用默认端口，避免和正在用的东西冲突）：复制到临时目录，三个端口偏移，跑 `sh start-guard.sh`，`node gate-control.cjs status` 应为 READY；然后 `node gate-control.cjs stop` 并删除临时目录。
4. **正式启用**：在仓库目录 `sh start-guard.sh` → READY 后运行 `sh install-launchagent.sh`（登录自启）。
5. **接入 Claude Code**：在 `~/.claude/settings.json` 合并以下内容（保留已有的其他 env 和钩子；`<repo>` 换成仓库绝对路径，`node` 写 `command -v node` 的绝对路径）。Windows 上用的是同样的配置：

   ```json
   {
     "env": {
       "HTTPS_PROXY": "http://127.0.0.1:17899", "HTTP_PROXY": "http://127.0.0.1:17899",
       "https_proxy": "http://127.0.0.1:17899", "http_proxy": "http://127.0.0.1:17899",
       "ALL_PROXY": "", "all_proxy": "",
       "NO_PROXY": "127.0.0.1,localhost,::1", "no_proxy": "127.0.0.1,localhost,::1"
     },
     "hooks": {
       "SessionStart": [{ "hooks": [{ "type": "command", "command": "/usr/local/bin/node <repo>/guard-hook.cjs", "timeout": 35 }] }],
       "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "/usr/local/bin/node <repo>/guard-hook.cjs", "timeout": 20 }] }]
     }
   }
   ```

   改完新开会话验证：门卫停止时消息被拦住。注意：从这一步起，当前 Claude 会话也经过门卫，停门卫会断开自己。
6. **实测 `deploy.sh`**：告诉用户会断网约 10 秒后再跑；确认日志顺序与 `deploy.ps1` 一致。发现问题直接修脚本。
7. **桌面版**：`open -a Claude --args --proxy-server=http://127.0.0.1:17899`，写成 `launch-desktop.sh`（对应 `launch-desktop.ps1`，启动前先确认门卫 READY）。
8. **第 2 阶段**：见下文，建议目录 `macos/ClaudeGuard/`（Swift Package），构建产物拷成仓库根的 `ClaudeGuard.app`，`start-guard.sh` 和 `deploy.sh` 已经会在它存在时启动它。
9. 每步完成后更新本文“进度”一节并提交。

## 目标

Mac 上与 Windows 版一致的保护：Claude Code 和 Claude 桌面版只能经固定节点出网，出口验证不通过就阻断，并冻结或结束 Claude 进程。界面采用 macOS 当前官方设计语言（Liquid Glass 玻璃材质）。

不在范围内：修改主 Clash / 其他代理配置；Windows 版行为变化（共用代码改动必须保证 Windows 测试照常通过）。

## 现有代码哪些能复用

| 部分 | 文件 | Mac 上 |
|---|---|---|
| 门卫（代理入口、出口探测、阻断、锁存） | `guard-daemon.cjs`, `guard-lib.cjs` | 直接复用。需改：核心可执行文件名（现写死 `mihomo-gate.exe`），`spawn` 的 `windowsHide` 无害可留 |
| 控制命令 | `gate-control.cjs` | 直接复用；错误提示里的 `start-guard.ps1` 改成按平台提示 |
| 出口两路核验 | `guard-inspect.cjs` | 直接复用 |
| IP 历史 | `ip-history.cjs` | 直接复用 |
| 换节点 | `node-selection.cjs` | 复用；同样改核心文件名；Clash 订阅路径按平台查找 |
| Claude Code 钩子 | `guard-hook.cjs` | 改：SessionStart 时 Windows 调 `start-guard.ps1`，Mac 调 `start-guard.sh` |
| 网卡选择 | `guard-lib.cjs` 的 `pickInterface` | 已用 `os.networkInterfaces()`，跨平台；Mac 的 `guard-policy.json` 里 `interfaceName` 写 `en0` 等 |
| 代理核心 | `mihomo-gate.exe` | 换成 mihomo 官方 darwin-arm64（或 amd64）构建，命名 `mihomo-gate` |
| 托盘 + 主界面 | `ClaudeGuardTray.cs`, `Dashboard.cs`, `NodePicker.cs`, `Ui.cs` | 重写：SwiftUI 菜单栏 App（见下） |
| 启动、部署、打包 | `*.ps1`, `*.cmd` | 重写为 shell 脚本 + launchd |
| 防火墙兜底 | `firewall.ps1` | 无直接对应，见第 3 阶段 |

原则：共用的 `.cjs` 用 `process.platform` 分支，不复制一份 Mac 版。平台相关的只放在脚本和 App 里。

## 架构（Mac）

```
Claude Code ── HTTPS_PROXY=127.0.0.1:17899 ─┐
Claude.app ── open -a Claude --args --proxy-server=... ─┤
                                             ▼
                       guard-daemon.cjs（launchd 用户代理常驻）
                                             ▼
                       mihomo-gate（只含固定节点，绑定 en0）
                                             ▼
                                        固定出口 IP

ClaudeGuard.app（SwiftUI 菜单栏 App）
  ├─ 每秒 GET 127.0.0.1:<adminPort>/status（带 admin-token）
  ├─ 异常时：POST /block + 冻结（SIGSTOP）或结束（SIGTERM→SIGKILL）Claude 进程
  └─ 复检恢复：POST /resume，通过后 SIGCONT
```

## 分阶段

每个阶段单独分支、单独验证、单独合并。

### 第 1 阶段：门卫在 Mac 上跑起来（最关键的保护）

1. 共用代码加平台分支：核心文件名、`guard-hook.cjs` 的启动脚本、错误提示。
2. 新增 `start-guard.sh`（对应 `start-guard.ps1`：已在运行则退出 0；否则后台启动 daemon，等到非 STARTING；超时非 0 退出）。
3. 新增 `deploy.sh`（对应 `deploy.ps1` 的顺序：停 App → `gate-control.cjs stop` → 等端口释放 → 替换 → 启门卫 → 等 READY → 启 App）。
4. launchd：`~/Library/LaunchAgents/<id>.guard.plist` 登录时运行 `start-guard.sh`；不用 `KeepAlive` 自动重启 daemon（崩溃后应保持锁定，由用户复检）。
5. `~/.claude/settings.json` 设 `HTTPS_PROXY` 和钩子（同 Windows）。

验收：`node --test *.test.cjs` 在 Mac 通过；断开节点后 Claude Code 下一条消息被钩子拦住；恢复后 `gate-control.cjs resume` 正常放行。

### 第 2 阶段：菜单栏 App（冻结 / 结束 / 状态）

- 进程识别（对应 `ProcessGuard`）：按可执行文件完整路径匹配，路径列表来自 `tray-settings.json` 的 `paths`。典型路径：
  - Claude Code：npm 全局安装的 `claude`（实际进程是 `node .../@anthropic-ai/claude-code/cli.js`，需按命令行参数匹配）或原生安装 `~/.local/bin/claude`
  - 桌面版：`/Applications/Claude.app/Contents/MacOS/Claude` 及其 `Contents/Frameworks/*Helper*` 子进程
  - 与 Windows 一样记录 pid + 启动时间，防止 pid 复用误伤
- 冻结 = `SIGSTOP`，恢复 = `SIGCONT`，结束 = `SIGTERM`，2 秒后仍在则 `SIGKILL`。
- 功能与 Windows 对齐：状态横幅、固定出口两路核验、IP 历史、最近事件、复检并恢复、一键冻结 / 一键结束、设置（先冻结 / 直接结束）、换节点（调用 `node-selection.cjs`）。
- 托盘侧规则同 Windows：状态请求超时连续 3 次才判定门卫不可用；连接被拒、非 READY、校验过期立即锁定；手动冻结在“直接结束”模式下也只冻结（`freezeOnly`）。
- 锁定状态写 `guard-runtime/tray-incident.json`，App 重启后保持锁定。

### 第 3 阶段：系统层兜底（可选，难度最高）

Windows 用防火墙限制 Claude 程序只能连本机门卫。Mac 上没有等价的“按程序”简单方案：

- `pf` 只能按用户（uid）过滤，不能按程序。可行做法是让 Claude 以专用用户运行，体验差。
- 按程序过滤需要 Network Extension（内容过滤器），要 Apple 开发者签名和权限审批。
- 建议：第 1、2 阶段完成后再评估；短期依赖代理设置 + 钩子 + 进程冻结。

## 界面：Liquid Glass

Apple 从 macOS 26（Tahoe）起的设计语言叫 Liquid Glass。实现前在 Mac 上查一次当前 SDK 文档确认 API（本文写于 2026-10，未核实 macOS 27 是否有变化）。

- 技术：SwiftUI，`MenuBarExtra` + `.menuBarExtraStyle(.window)` 做菜单栏弹出面板；主窗口用 `Window`。
- 用新 SDK 编译时，系统控件（按钮、工具栏、侧栏、弹出面板）自动是玻璃材质，不需要手绘。自定义卡片用 `.glassEffect(...)`，多个相邻玻璃元素放进 `GlassEffectContainer`。
- 颜色：状态色只用在图标和标题文字上（绿 = 保护中，红 = 已锁定，橙 = 验证中），卡片保持玻璃底，不要整块填红绿色，否则失去玻璃效果。
- 布局（与 Windows 主界面对应）：
  1. 状态卡：图标 + “保护中 · 固定出口已验证”/“已锁定”，下面标签行（冻结数、上次通过、模式）；锁定时卡片内展开“复检并恢复 / 结束已识别的 Claude / 稍后处理”
  2. 固定出口卡：节点名、预期 IP、ipify / Cloudflare 两行、重新验证、IP 历史
  3. 最近事件列表
  4. 紧急操作：一键冻结、一键结束（`.buttonStyle(.glass)`，红色文字，点后确认对话框，默认按钮是“取消”）
  5. 底栏：启动 Claude Code、启动桌面版、打开日志
- 菜单栏图标：SF Symbols `lock.shield`（正常）/ `lock.shield.fill` 红色（锁定）。
- 遵守“减少透明度”辅助功能设置：系统控件自动处理，自定义玻璃也要检查。

难度：用 SwiftUI 原生控件做，玻璃效果基本是自带的，不难；难点在进程识别和权限，不在界面。

## Mac 上需要的工具

- Xcode（与目标 macOS 匹配的版本），或 Command Line Tools + `swift build`（菜单栏 App 可用 Swift Package 构建，避免 Xcode 工程文件）
- Node.js（与 Windows 相同主版本）
- mihomo darwin 构建

## 交接注意

- 仓库是公开的。不要在代码、测试、文档、提交信息里写真实节点信息（IP、节点名、UUID、密钥、SNI）；示例用 203.0.113.x / 198.51.100.x 和 `Example-A`。`pre-push` 钩子会检查，不要绕过。
- `guard-policy.json`、`guard-node.json`、`tray-settings.json` 是本机配置，已 gitignore。Mac 上从 `*.example.json` 复制后填本机值。
- 门卫运行期间会逐字节比对配置文件，改配置只能通过“更换节点”流程或在停止门卫后进行。
- 如果 Mac 上的 Claude Code 自己也走这个门卫：重启门卫会断开当前会话，部署前先告诉用户。
- 测试真实 daemon / App 时用端口偏移的隔离副本（同 CLAUDE.md 第 3 条），不要碰正在用的那套。
- Windows 的 C# 5 限制与 Mac 无关，但改共用 `.cjs` 后要在 Windows 上跑一次 `node --test *.test.cjs`。
