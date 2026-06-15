#!/usr/bin/env bash
# PiiMaker examples dev harness.
#
# Idempotently provisions every backend in Docker, then launches ONE runtime host with ONE key store.
# Rerunnable: an existing container is (re)started, never recreated; rerun freely.
#
#   ./piimaker.sh --runtime temporal --keystore openbao   # provision what's needed + launch, print the URL
#   ./piimaker.sh                                          # interactive: pick runtime + key store
#   ./piimaker.sh provision-only                           # bring up ALL backends, launch nothing
#   ./piimaker.sh down                                     # stop + remove all dev containers
#
# Runtimes : inproc | temporal | durabletask | restate | elsa | zeebe   (panels on 5001..5006)
# Keystores: inmemory | openbao | ravendb
#
# Notes:
#  - The "restate" runtime also needs `cargo` on PATH (the host builds its Rust shell; the script does not).
#  - The "zeebe" runtime runs Camunda 8 + Elasticsearch (~2 GB RAM) so the v2 REST/Operate read model exists.
#  - The RavenDB key store uses a DEMO-ONLY master key unless PIIMAKER_RAVENDB_KEK (base64, 32 bytes) is set.
#  - Backends bind to 127.0.0.1 only. Containers have no volumes, so `down` wipes their data.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"          # .../soex-workflow
HOSTS="$REPO/examples/PiiMaker/Hosts"

# Overridable image tags (export before running to pin/upgrade).
TEMPORAL_IMG="${TEMPORAL_IMG:-temporalio/temporal:latest}"
DTS_IMG="${DTS_IMG:-mcr.microsoft.com/dts/dts-emulator:latest}"
RESTATE_IMG="${RESTATE_IMG:-ghcr.io/restatedev/restate:latest}"
OPENBAO_IMG="${OPENBAO_IMG:-openbao/openbao:latest}"
RAVENDB_IMG="${RAVENDB_IMG:-ravendb/ravendb:latest}"
CAMUNDA_IMG="${CAMUNDA_IMG:-camunda/camunda:8.8.27}"   # concrete patch (no rolling 8.8 tag); override to bump
ELASTIC_IMG="${ELASTIC_IMG:-docker.elastic.co/elasticsearch/elasticsearch:8.19.5}"   # Camunda 8.8 needs ES 8.19+

declare -A PORT=( [inproc]=5001 [temporal]=5002 [durabletask]=5003 [restate]=5004 [elsa]=5005 [zeebe]=5006 )
declare -A HOSTDIR=( [inproc]=InProc [temporal]=Temporal [durabletask]=DurableTask [restate]=Restate [elsa]=Elsa [zeebe]=Zeebe )

log()  { printf '\033[1;34m[piimaker]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[piimaker]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[piimaker]\033[0m %s\n' "$*" >&2; exit 1; }

# ensure_container NAME -- <docker run args...>   — start if it exists, else create. Idempotent.
ensure_container() {
  local name="$1"; shift
  [[ "${1:-}" == "--" ]] && shift
  if [ -n "$(docker ps -aq -f "name=^${name}$")" ]; then
    if [ -z "$(docker ps -q -f "name=^${name}$")" ]; then
      log "starting existing container $name"; docker start "$name" >/dev/null
    else
      log "container $name already running"
    fi
  else
    log "creating container $name"; docker run -d --name "$name" "$@" >/dev/null
  fi
}

wait_tcp() {  # host port [tries]
  local host="$1" port="$2" tries="${3:-60}"
  log "waiting for tcp $host:$port ..."
  for _ in $(seq 1 "$tries"); do
    (exec 3<>"/dev/tcp/$host/$port") 2>/dev/null && { exec 3>&- 3<&-; log "  $host:$port up"; return 0; }
    sleep 1
  done
  die "timed out waiting for $host:$port"
}

wait_http() { # url [tries]
  local url="$1" tries="${2:-60}" code
  log "waiting for http $url ..."
  for _ in $(seq 1 "$tries"); do
    code="$(curl -fsS -o /dev/null -w '%{http_code}' "$url" 2>/dev/null || true)"
    [[ "$code" =~ ^(200|204|301|302|400|401|403|404)$ ]] && { log "  $url up ($code)"; return 0; }
    sleep 1
  done
  die "timed out waiting for $url"
}

# ---- per-backend provisioners ---------------------------------------------------------------------
prov_temporal() {
  ensure_container temporal-dev -- -p 127.0.0.1:7233:7233 "$TEMPORAL_IMG" \
    server start-dev --ip 0.0.0.0 --namespace default
  wait_tcp 127.0.0.1 7233
}

prov_dts() {
  ensure_container dts -- -e DTS_TASK_HUB_NAMES="default,soex-tests" -p 127.0.0.1:8080:8080 "$DTS_IMG"
  wait_tcp 127.0.0.1 8080
}

prov_restate() {
  # host 8088 -> container default ingress 8080; admin 9070 straight through.
  ensure_container restate -- -p 127.0.0.1:8088:8080 -p 127.0.0.1:9070:9070 "$RESTATE_IMG"
  wait_tcp 127.0.0.1 9070
  wait_tcp 127.0.0.1 8088
}

prov_openbao() {
  ensure_container openbao-dev -- --cap-add=IPC_LOCK -p 127.0.0.1:8200:8200 "$OPENBAO_IMG" \
    server -dev -dev-root-token-id=root -dev-listen-address=0.0.0.0:8200
  wait_http http://127.0.0.1:8200/v1/sys/health
  # Transit is NOT auto-enabled in dev mode — enable it (idempotent; ignore "already in use").
  log "enabling OpenBao transit mount"
  if ! docker exec -e BAO_ADDR=http://127.0.0.1:8200 -e BAO_TOKEN=root openbao-dev \
        bao secrets enable transit >/dev/null 2>&1; then
    # Fallback if the CLI path differs: mount via the HTTP API (also idempotent enough for a dev box).
    curl -fsS -X POST -H "X-Vault-Token: root" -d '{"type":"transit"}' \
      http://127.0.0.1:8200/v1/sys/mounts/transit >/dev/null 2>&1 || true
  fi
}

prov_ravendb() {
  # Unsecured single-node dev server; remapped to host 8085 (DTS owns 8080).
  ensure_container ravendb -- \
    -e RAVEN_License_Eula_Accepted=true \
    -e RAVEN_Setup_Mode=None \
    -e RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork \
    -e RAVEN_ServerUrl=http://0.0.0.0:8080 \
    -p 127.0.0.1:8085:8080 "$RAVENDB_IMG"
  wait_http http://127.0.0.1:8085/setup/alive
}

prov_zeebe() {
  # Camunda 8.8 orchestration cluster needs secondary storage for the v2 REST/Operate read model the Zeebe
  # host polls (/v2/variables/search): one Elasticsearch + one camunda container. ~2 GB RAM. REST/Operate
  # remapped to 8090, gRPC gateway 26500.
  docker network inspect piimaker-c8 >/dev/null 2>&1 || { log "creating docker network piimaker-c8"; docker network create piimaker-c8 >/dev/null; }
  ensure_container camunda-es -- --network piimaker-c8 \
    -e discovery.type=single-node -e xpack.security.enabled=false -e "ES_JAVA_OPTS=-Xms1g -Xmx1g" \
    -p 127.0.0.1:9200:9200 "$ELASTIC_IMG"
  wait_http http://127.0.0.1:9200 120
  # 8.8 unified config: point secondary storage at ES via camunda.data.secondary-storage.* (the legacy
  # CAMUNDA_DATABASE_URL conflicts with the new property and the broker refuses to start). REST/Operate on 8090.
  ensure_container camunda -- --network piimaker-c8 \
    -e CAMUNDA_DATA_SECONDARYSTORAGE_TYPE=elasticsearch \
    -e CAMUNDA_DATA_SECONDARYSTORAGE_ELASTICSEARCH_URL=http://camunda-es:9200 \
    -e "CAMUNDA_SECURITY_AUTHENTICATION_UNPROTECTED-API=true" \
    -e SERVER_PORT=8090 \
    -p 127.0.0.1:26500:26500 -p 127.0.0.1:8090:8090 "$CAMUNDA_IMG"
  wait_tcp  127.0.0.1 26500 180
  wait_http http://127.0.0.1:8090/v2/topology 180
}

provision_keystore() { case "$1" in openbao) prov_openbao;; ravendb) prov_ravendb;; inmemory) : ;; esac; }
provision_runtime()  { case "$1" in temporal) prov_temporal;; durabletask) prov_dts;; restate) prov_restate;; zeebe) prov_zeebe;; inproc|elsa) : ;; esac; }

export_keystore_env() {
  export PIIMAKER_KEYSTORE="$1"
  case "$1" in
    openbao) export PIIMAKER_OPENBAO_ADDR="http://127.0.0.1:8200" PIIMAKER_OPENBAO_TOKEN="root" PIIMAKER_OPENBAO_MOUNT="transit" ;;
    ravendb) export PIIMAKER_RAVENDB_URL="http://127.0.0.1:8085" PIIMAKER_RAVENDB_DATABASE="PiiMakerKeys" ;;  # KEK = DEMO unless PIIMAKER_RAVENDB_KEK set
  esac
}

do_down() {
  log "stopping + removing PiiMaker dev containers"
  docker rm -f temporal-dev dts restate openbao-dev ravendb camunda camunda-es >/dev/null 2>&1 || true
  docker network rm piimaker-c8 >/dev/null 2>&1 || true
  log "down."
}

menu() { # prompt opt...
  local prompt="$1"; shift; local opts=("$@") i n
  echo "$prompt" >&2
  for i in "${!opts[@]}"; do printf '  %d) %s\n' "$((i+1))" "${opts[$i]}" >&2; done
  read -rp "> " n
  [[ "$n" =~ ^[0-9]+$ ]] && [ "$n" -ge 1 ] && [ "$n" -le "${#opts[@]}" ] || die "invalid choice"
  echo "${opts[$((n-1))]}"
}

# ---- args -----------------------------------------------------------------------------------------
RUNTIME="" KEYSTORE="" MODE="run"
while [ $# -gt 0 ]; do
  case "$1" in
    --runtime)  RUNTIME="${2:-}"; shift 2 ;;
    --keystore) KEYSTORE="${2:-}"; shift 2 ;;
    provision-only) MODE="provision"; shift ;;
    down) MODE="down"; shift ;;
    -h|--help) awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$0"; exit 0 ;;
    *) die "unknown arg '$1' (try --help)" ;;
  esac
done

[ "$MODE" = "down" ] && { do_down; exit 0; }
command -v docker >/dev/null || die "docker not found on PATH"
command -v curl   >/dev/null || die "curl not found on PATH"

if [ "$MODE" = "provision" ]; then
  log "provisioning ALL backends (idempotent)"
  prov_temporal; prov_dts; prov_restate; prov_zeebe; prov_openbao; prov_ravendb
  log "all backends provisioned. Run a host with: $0 --runtime <name> --keystore <name>"
  exit 0
fi

[ -z "$RUNTIME" ]  && RUNTIME="$(menu 'Runtime?' inproc temporal durabletask restate elsa zeebe)"
[ -z "$KEYSTORE" ] && KEYSTORE="$(menu 'Key store?' inmemory openbao ravendb)"
[[ -n "${HOSTDIR[$RUNTIME]:-}" ]] || die "unknown runtime '$RUNTIME'"
[[ "$KEYSTORE" =~ ^(inmemory|openbao|ravendb)$ ]] || die "unknown keystore '$KEYSTORE'"
[ "$RUNTIME" = "restate" ] && ! command -v cargo >/dev/null && warn "the restate host builds a Rust shell — install cargo or it will fail to start."

log "runtime=$RUNTIME  keystore=$KEYSTORE  panel-port=${PORT[$RUNTIME]}"
provision_keystore "$KEYSTORE"
provision_runtime  "$RUNTIME"
export_keystore_env "$KEYSTORE"

PANEL="http://localhost:${PORT[$RUNTIME]}"
log "launching host -> $PANEL"
log "(Ctrl-C stops the host; backends stay up. '$0 down' removes them.)"
exec dotnet run --project "$HOSTS/${HOSTDIR[$RUNTIME]}" -- "${PORT[$RUNTIME]}"
