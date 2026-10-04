# Working on Claude Guard

Read README.md for what the project does. This file covers what an agent must not get wrong here.

## Your own connection runs through this gate

`~/.claude/settings.json` sets `HTTPS_PROXY=http://127.0.0.1:17899` and the tray's freeze list contains the npm `claude.exe`. The project folder is the live install, so:

- Stopping or restarting the gate cuts your API connection. If the tray then sees the gate down, it suspends your process and the session hangs.
- The gate byte-compares `guard-policy.json` and `guard-node.json` on every check. Any write to them, including a git checkout or line-ending change, blocks the gate. They are gitignored for this reason; never stage them.
- `guard-hook.cjs` and `gate-control.cjs` run on every prompt submit, and `start-guard.ps1` runs at session start and at login. A syntax error in them blocks every new Claude session. Run `node --check` on every changed `.cjs` before it lands in this folder.

## How to change code

1. Develop in a `git worktree` outside this folder, on a feature branch.
2. Verify there: `node --test *.test.cjs`, `.\build-tray.ps1`, `--self-test`, `--ui-preview`.
3. Anything that starts a real Tray (`--ui-test`, `--recovery-test`, `--node-ui-test`) or a real daemon needs an isolated copy: a scratch folder with the `.cjs` files, `vendor/`, `mihomo-gate.exe`, `guard-node.json`, a `guard-policy.json` with all three ports offset (e.g. 27899/27901/29099), and `tray-settings.json` with `"paths": []` and the offset admin port. Stop that daemon with `gate-control.cjs stop` and delete the folder afterwards; it holds credentials.
4. Merge into `main` here, then `git worktree remove` before `git branch -d`.
5. Code changes to the daemon or tray take effect only after `deploy.ps1`. It cuts the connection for about 10 s, so tell the user first and run it only with their go-ahead. Run it as a single command: it starts the tray only after the new gate is READY.

Never edit the live JSON files, stop the gate, or kill/restart the tray outside `deploy.ps1`. Administrator actions (`firewall.ps1 apply|remove`) show a UAC prompt; tell the user before running them.

## Pitfalls seen in this repo

- The tray is compiled by .NET Framework `csc`, C# 5 only: no `?.`, `$"..."`, `is T x`, or expression-bodied members.
- Shell heredocs on this machine halve backslashes. Write files that contain `\n` or Windows paths with the file-writing tool, not via bash heredoc or inline Python.
- PowerShell scripts here are run by Windows PowerShell 5.1, which reads BOM-less UTF-8 as ANSI. Keep `.ps1` text ASCII.
- `events.jsonl` is appended by the daemon; a failed log write there blocks the guard. Readers must open it with `FileShare.ReadWrite | FileShare.Delete`.
- The firewall rules and the tray's process matching treat only the Store version segment (`Claude_<version>_x64__pzs8sxrjxfjjc`) as variable. Keep the publisher id pinned.
- Commit messages: short Chinese description of the behavior change.
