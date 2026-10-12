#!/bin/sh
# macOS counterpart of deploy.ps1: restart the gate (and the menu bar app, if built) with the code in this folder.
# Claude loses its connection for ~10 s. Order: stop app, stop gate, start gate, wait READY, start app last,
# so the app never sees a blocked gate during the switch and never freezes Claude.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
run="$dir/guard-runtime"
log="$run/deploy.log"
mkdir -p "$run"
node=$(command -v node) || { echo 'node not found in PATH' >&2; exit 1; }
policy="$dir/guard-policy.json"
ports=$("$node" -e 'const p=require(process.argv[1]);console.log([p.adminPort,p.proxyPort,p.corePort].join(" "))' "$policy")
app="$dir/ClaudeGuard.app"

say() { line="$(date '+%Y-%m-%d %H:%M:%S') $1"; echo "$line" >>"$log"; echo "$line"; }
fail() { say "FAILED: $1"; exit 1; }
listening() { for p in $ports; do lsof -nP -iTCP:"$p" -sTCP:LISTEN >/dev/null 2>&1 && return 0; done; return 1; }
state() { "$node" "$dir/gate-control.cjs" status --quiet 2>/dev/null | "$node" -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{try{console.log(JSON.parse(s).state)}catch{process.exit(1)}})' || true; }

say 'deploy started'
for f in "$dir"/*.cjs; do "$node" --check "$f" || fail "syntax error in $f"; done
[ -x "$dir/mihomo-gate" ] || fail 'mihomo-gate missing or not executable'
say 'checked'

if [ -d "$app" ]; then pkill -f "$app/Contents/MacOS/" || true; sleep 0.5; say 'old app stopped'; fi

"$node" "$dir/gate-control.cjs" stop >/dev/null 2>&1 || true
i=0; while [ $i -lt 50 ] && listening; do sleep 0.2; i=$((i+1)); done
listening && fail 'old gate or dedicated core did not exit'
say 'old gate stopped'

nohup "$node" "$dir/guard-daemon.cjs" >"$run/daemon.out.log" 2>"$run/daemon.err.log" </dev/null &
s=''; i=0
while [ $i -lt 120 ]; do sleep 0.25; s=$(state); [ -n "$s" ] && [ "$s" != STARTING ] && break; i=$((i+1)); done
[ -n "$s" ] || fail 'new gate did not respond; see guard-runtime/daemon.err.log'
say "new gate started: $s"

# Stopping the gate latches a block on purpose; resume re-verifies the fixed exit before opening.
if [ "$s" != READY ]; then "$node" "$dir/gate-control.cjs" resume >/dev/null 2>&1 || true; s=$(state); fi
[ "$s" = READY ] || fail "re-verification failed; the gate stays blocked ($s)"
say 'gate READY'

if [ -d "$app" ]; then open -g "$app"; say 'app started'; fi
say 'deploy complete'
