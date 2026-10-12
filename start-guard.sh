#!/bin/sh
# macOS/Linux counterpart of start-guard.ps1: start the menu bar app (if built) and the gate.
# Exit 0 only when the gate answers with a state other than STARTING; otherwise Claude stays blocked.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
node=$(command -v node || true)
[ -n "$node" ] || { echo 'node not found in PATH' >&2; exit 1; }

# Menu bar app is optional until phase 2 lands; the gate alone already blocks.
app="$dir/ClaudeGuard.app"
if [ -d "$app" ] && ! pgrep -f "$app/Contents/MacOS/" >/dev/null 2>&1; then open -g "$app" || echo 'menu bar app failed to start' >&2; fi

state() { "$node" "$dir/gate-control.cjs" status --quiet 2>/dev/null | "$node" -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{try{console.log(JSON.parse(s).state)}catch{process.exit(1)}})'; }

if s=$(state) && [ -n "$s" ]; then
  [ "$s" = STARTING ] && { echo 'Guard is still starting. Retry shortly.' >&2; exit 1; }
  exit 0
fi

run="$dir/guard-runtime"
mkdir -p "$run"
nohup "$node" "$dir/guard-daemon.cjs" >"$run/daemon.out.log" 2>"$run/daemon.err.log" </dev/null &
pid=$!
i=0
while [ $i -lt 60 ]; do
  sleep 0.2
  if s=$(state) && [ -n "$s" ] && [ "$s" != STARTING ]; then exit 0; fi
  kill -0 "$pid" 2>/dev/null || { echo 'Guard startup failed; inspect guard-runtime/daemon.err.log' >&2; exit 1; }
  i=$((i+1))
done
echo 'Guard startup timed out; Claude must remain blocked.' >&2
exit 1
