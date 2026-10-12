#!/bin/sh
# macOS: run start-guard.sh at login (counterpart of the Windows login shortcut).
# No KeepAlive: a crashed gate must stay down and keep Claude blocked until the user re-verifies.
#   ./install-launchagent.sh           install or update, and load now
#   ./install-launchagent.sh remove    unload and delete
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
label=local.claude-guard.start
plist="$HOME/Library/LaunchAgents/$label.plist"
domain="gui/$(id -u)"

if [ "${1:-}" = remove ]; then
  launchctl bootout "$domain" "$plist" 2>/dev/null || true
  rm -f "$plist"; echo "removed $plist"; exit 0
fi

node=$(command -v node) || { echo 'node not found in PATH' >&2; exit 1; }
mkdir -p "$HOME/Library/LaunchAgents" "$dir/guard-runtime"
# launchd starts with a minimal PATH; pass the directory of the node found now.
cat >"$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>ProgramArguments</key><array><string>/bin/sh</string><string>$dir/start-guard.sh</string></array>
  <key>EnvironmentVariables</key><dict><key>PATH</key><string>$(dirname "$node"):/usr/bin:/bin:/usr/sbin:/sbin</string></dict>
  <key>RunAtLoad</key><true/>
  <key>AbandonProcessGroup</key><true/>
  <key>StandardOutPath</key><string>$dir/guard-runtime/launchagent.log</string>
  <key>StandardErrorPath</key><string>$dir/guard-runtime/launchagent.log</string>
</dict>
</plist>
EOF
plutil -lint "$plist"
launchctl bootout "$domain" "$plist" 2>/dev/null || true
launchctl bootstrap "$domain" "$plist"
echo "installed $plist"
