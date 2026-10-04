# Claude Guard

让 Claude（Claude Code 和桌面版）只通过一个**固定节点**上网：出口 IP 验证通过才放行，验证失败、网络异常或出口变化时立即阻断，并冻结或结束 Claude 进程。不修改主 Clash 配置。

## 工作方式

```
Claude Code / 桌面版
   │  HTTPS_PROXY=127.0.0.1:17899（桌面版用 --proxy-server）
   ▼
门卫 guard-daemon.cjs  :17899   ← 出口验证通过前一律拒绝（503）
   │
   ▼
专用 mihomo 核心        :17901   ← 只有一个固定节点，不回退、不直连，只绑定指定网卡
   │
   ▼
固定节点 → 固定出口 IP（ipify 与 Cloudflare 两路交叉核对）
```

| 组件 | 作用 |
|---|---|
| 门卫 `guard-daemon.cjs` | 代理入口 17899，控制口 19099（需令牌）。默认每 30 秒核对一次出口 IP。 |
| 托盘 `ClaudeGuardTray.exe` | 主界面与监控。门卫锁定、断网、校验过期时冻结或结束 Claude；网络地址变化时先自动复检。 |
| 防火墙 `firewall.ps1` | 系统层兜底：受保护的 Claude 程序只能连本机门卫，绕过门卫直连外网会被 Windows 拦截。 |
| IP 历史 `ip-history.cjs` | 记录每次出口 IP 变化，区分主动与非主动，变化频繁时警告。 |

主 Clash（Clash Verge，7897）照常给其他程序用，与本项目无关。

## 日常使用

- **Claude Code**：`~/.claude/settings.json` 已全局设置代理，直接用即可。每次会话开始和每条消息提交前，钩子 `guard-hook.cjs` 会检查门卫，门卫不是 READY 时 Claude Code 不会继续。
- **Claude 桌面版**：用桌面快捷方式「Claude 桌面安全启动」打开，不要从开始菜单直接打开（那样不带代理参数，只能靠防火墙兜底）。
- **主界面**：桌面「Claude Guard」，或双击托盘图标。

### 被锁定了怎么办

1. 看主界面横幅里的原因，以及「最近事件」。
2. 原因排除后（网络恢复、节点正常），点「**复检并恢复**」：先重新验证出口，通过后才恢复被冻结的进程。
3. 「结束已识别的 Claude」只是关闭进程，**不会解锁**。「稍后处理」只隐藏提示，进程保持冻结。
4. 设置里可以选择出现异常时「先冻结，确认后再结束」（默认）还是「直接结束全部 Claude」。

### 更换节点

主界面「更换节点」：读取 Clash 当前订阅（或自选 YAML），先隔离测试，再确认保存。保存后保持锁定，需要手动「复检并恢复」。目前只支持服务器是 IPv4 地址的 ss / vless / vmess / trojan 节点。

## IP 历史与警告

- 「固定出口」卡片底部显示近 24 小时 / 7 天的变化次数，点「IP 历史」查看完整记录。
- 记录类型：开始记录、**主动更换**（通过界面换节点）、**非界面更换**（配置被别的方式修改）、**出口异常**（实测 IP 与预期不符，会同时记下实测 IP）。后两类标红。
- 24 小时内变化 ≥2 次或 7 天内 ≥3 次，横幅显示「IP 变化频繁」，托盘弹一次通知。看到标红记录时，先确认是不是自己换的。
- 文件：`guard-runtime\ip-history.jsonl`。

## 防火墙

在项目目录的 PowerShell 中运行：

| 操作 | 命令 | 需要管理员 |
|---|---|---|
| 查看状态 | `powershell -ExecutionPolicy Bypass -File .\firewall.ps1 status` | 否 |
| 应用 / 重建规则 | `powershell -ExecutionPolicy Bypass -File .\firewall.ps1 apply` | 是（弹 UAC） |
| 撤销（删除规则，并把防火墙恢复为第一次 apply 之前的开关状态） | `powershell -ExecutionPolicy Bypass -File .\firewall.ps1 remove` | 是（弹 UAC） |

`status` 结果：`OK` 正常；`MISSING` / `STALE` 需要重新运行 `apply`；`OFF` 表示防火墙被关闭，规则不生效。`apply` 结束前会自检：同样的规则必须拦得住直连，同时经过门卫仍能拿到固定出口 IP。

## 后续注意事项

1. **Claude 桌面版更新后**，安装路径里的版本号会变：运行一次 `firewall.ps1 status`，出现 `MISSING` / `STALE` 就运行 `apply`。托盘的冻结名单会自动适配新版本，不用改。
2. **门卫运行时不要改 `guard-policy.json` / `guard-node.json`**：门卫每次核对都会比对这两个文件，一改就锁定。换节点用界面；手动改配置要先关闭 Claude，运行 `node gate-control.cjs stop`，改完再运行 `deploy.ps1` 或 `start-guard.ps1`。手动改出口 IP 会被记为「非界面更换」。
3. **换网卡**（比如改用网线）：在 `guard-policy.json` 里把 `interfaceName` 写成列表，例如 `["WLAN", "以太网"]`，按顺序选第一个有 IPv4 的网卡；改完按第 2 条的方式重启门卫。不要写入 Mihomo、vEthernet 这类虚拟网卡。
4. **部署新版本**：关闭所有 Claude，在项目目录运行 `powershell -ExecutionPolicy Bypass -File .\deploy.ps1`。它会先编译，再停托盘、停门卫、替换程序、启动新门卫并复检，最后启动托盘。期间 Claude 会断网约 10 秒。记录在 `guard-runtime\deploy.log`。
5. **防火墙开启后，入站默认拦截**：如果别的设备访问这台电脑上的服务出了问题，先想到这个原因。
6. **不走门卫的流量**：浏览器访问 claude.ai，以及没设代理的程序，都走主 Clash，不受本项目保护。
7. **UAC**：第一次运行 `firewall.ps1 apply` 时没有看到 UAC 弹窗，原因还没查明。如果 `apply` / `remove` 不弹窗就直接完成，请检查「更改用户账户控制设置」的滑块，以及有没有会自动批准提权的软件。
8. **凭据**：`guard-node.json` 里有节点 UUID 和 REALITY 密钥，已被 `.gitignore` 排除。不要提交，也不要发给别人。新环境从 `*.example.json` 复制后填写。
9. **Node.js** 默认用 `C:\Program Files\nodejs\node.exe`，找不到时从 PATH 里找。`~/.claude/settings.json` 里的钩子写的是固定路径，换了 Node 安装位置时要一起改。

## 日志与文件

| 文件 | 内容 |
|---|---|
| `guard-runtime\events.jsonl` | 门卫事件：启动、锁定、恢复、重试、连接、IP 变化 |
| `guard-runtime\daemon.err.log` | 门卫启动失败的原因 |
| `guard-runtime\tray.log` | 托盘操作与错误 |
| `guard-runtime\ip-history.jsonl` | 出口 IP 历史 |
| `guard-runtime\firewall.log` / `deploy.log` | 防火墙与部署记录 |
| `backups\` | 换节点前的配置备份 |

命令行查看状态：`node gate-control.cjs status`（另有 `check`、`resume`、`block`、`stop`）。

## 开发

- 测试：`node --test guard.test.cjs node-selection.test.cjs ip-history.test.cjs`
- 编译托盘：`.\build-tray.ps1`，生成 `ClaudeGuardTray.next.exe`。编译器是 .NET Framework 自带的 csc，只支持 C# 5。
- 托盘自检：`ClaudeGuardTray.next.exe --self-test <输出文件>`
- 界面截图：`ClaudeGuardTray.next.exe --ui-preview <png>`，会额外生成 `-blocked.png` 和 `-picker.png`。
- `--ui-test`、`--recovery-test`、`--node-ui-test` 会按当前目录的真实配置启动托盘，**只能在端口错开的隔离副本里运行**，详见 `CLAUDE.md`。
