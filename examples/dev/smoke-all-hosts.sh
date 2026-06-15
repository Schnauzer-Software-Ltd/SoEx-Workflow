#!/usr/bin/env bash
# Deterministic end-to-end smoke for every example host, one runtime at a time:
#   StartOnboarding -> status(keyLive:true) -> erase -> status(keyLive:false)
#
# Two backend-timing facts make a naive "start, then erase once" flaky; this driver gates on the
# actual ready/done signals instead of fixed sleeps, so it never flakes:
#   * Zeebe accepts the first CreateProcessInstance only once the broker has settled after the host's
#     startup BPMN deploy, so we RETRY StartOnboarding until it returns a valid instance id.
#   * The subject is indexed only when the first governed step runs on the worker (~1-2s on Temporal),
#     and erase routes by that index, so we RETRY erase until the key actually shreds.
#
# Usage: examples/dev/smoke-all-hosts.sh [Host ...]   (default: all six)   --no-build to skip the build.
set -u
cd "$(dirname "$0")/../.." || exit 1   # repo root (soex-workflow/)

BUILD=1; HOSTS_ARG=()
for a in "$@"; do case "$a" in --no-build) BUILD=0;; *) HOSTS_ARG+=("$a");; esac; done
declare -A PORT=( [InProc]=5101 [Temporal]=5102 [DurableTask]=5103 [Elsa]=5104 [Restate]=5105 [Zeebe]=5106 )
ORDER=("${HOSTS_ARG[@]:-InProc Temporal DurableTask Elsa Restate Zeebe}"); ORDER=(${ORDER[@]})

[ "$BUILD" = 1 ] && { echo "building examples..."; dotnet build examples/SoEx.Workflow.Examples.sln -v q --nologo >/dev/null || { echo "build failed"; exit 1; }; }

pid_on() { ss -ltnp 2>/dev/null | grep ":$1 " | grep -oP 'pid=\K[0-9]+' | head -1; }
kill_orphan_sidecar() { local p; p=$(pid_on 9081); [ -n "${p:-}" ] && { kill "$p" 2>/dev/null; sleep 1; }; }

PASS=0; FAIL=0; RESULTS=()
for H in "${ORDER[@]}"; do
  P=${PORT[$H]:-}; [ -z "$P" ] && { echo "unknown host: $H"; continue; }
  EMAIL="smoke-$H-$RANDOM@example.com"; LOG="/tmp/piimaker-smoke-$H.log"
  echo "=== $H on :$P ==="
  [ "$H" = "Restate" ] && kill_orphan_sidecar
  (dotnet run --project "examples/PiiMaker/Hosts/$H" --no-build -- "$P" >"$LOG" 2>&1 &)

  # 1) wait for the web endpoint (up to 40s)
  up=0; for i in $(seq 1 20); do curl -sf -m5 "http://localhost:$P/example/host" >/dev/null 2>&1 && { up=1; break; }; sleep 2; done

  # 2) retry StartOnboarding until a valid id (handles the Zeebe broker settle + any slow worker connect; up to ~40s)
  ID=""
  for i in $(seq 1 20); do
    ID=$(curl -s -m12 -X POST "http://localhost:$P/IMembershipEntry/StartOnboarding" \
          -H 'Content-Type: application/json' -d "{\"orgId\":\"org-1\",\"email\":\"$EMAIL\",\"offer\":\"pro\"}" | tr -d '"')
    [[ "$ID" == onboard-* ]] && break; sleep 2
  done
  S1=$(curl -s -m8 "http://localhost:$P/example/status/$ID" 2>/dev/null)

  # 3) retry erase until the key shreds (handles the subject-index lag; up to ~24s)
  shred=""
  if [[ "$ID" == onboard-* ]]; then
    for i in $(seq 1 12); do
      curl -s -m10 -X POST "http://localhost:$P/example/erase" -H 'Content-Type: application/json' -d "{\"subject\":\"$EMAIL\"}" >/dev/null 2>&1
      sleep 2; S2=$(curl -s -m8 "http://localhost:$P/example/status/$ID" 2>/dev/null)
      echo "$S2" | grep -q '"keyLive":false' && { shred="after $i erase attempt(s)"; break; }
    done
  fi

  echo "  id=${ID:-<none>}  start=$S1"
  if [[ "$ID" == onboard-* ]] && echo "$S1" | grep -q '"keyLive":true' && [ -n "$shred" ]; then
    echo "  shredded $shred -> RESULT: PASS"; PASS=$((PASS+1)); RESULTS+=("$H PASS")
  else
    echo "  RESULT: FAIL (web-up=$up, last-status=${S2:-<none>})"; FAIL=$((FAIL+1)); RESULTS+=("$H FAIL"); tail -8 "$LOG" | sed 's/^/  | /'
  fi
  p=$(pid_on "$P"); [ -n "${p:-}" ] && kill "$p" 2>/dev/null
  [ "$H" = "Restate" ] && kill_orphan_sidecar
  sleep 2
done

echo; echo "==================== SMOKE SUMMARY ===================="
for r in "${RESULTS[@]}"; do echo "  $r"; done
echo "  PASS=$PASS FAIL=$FAIL"
[ "$FAIL" = 0 ]
