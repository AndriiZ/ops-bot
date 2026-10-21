#!/usr/bin/env bash
# The tests of template/deploy.sh and deploy/bin/ops-runner.sh on Linux (for example WSL), as root:
#   sudo bash template/tests/deploy.test.sh
# A stub of systemctl starts "python3 -m http.server" in the www/ directory of the active release.
# The tests use only a temporary directory; they do not change systemd or /opt.
set -uo pipefail

[[ $EUID -eq 0 ]] || { echo "Run as root (the deploy scripts need root)." >&2; exit 1; }
for tool in python3 curl flock sha256sum tar; do
    command -v "$tool" >/dev/null || { echo "Install $tool." >&2; exit 1; }
done

HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
WORK=$(mktemp -d /tmp/ops-deploy-test.XXXXXX)
PORT=$((20000 + RANDOM % 20000))
SERVICE=demo-svc
ROOT=$WORK/opt/$SERVICE
export LOG_FILE=$WORK/deploy.log LOCK_FILE=$WORK/deploy.lock
FAILED=0

cleanup() {
    [[ -f $WORK/server.pid ]] && kill "$(cat "$WORK/server.pid")" 2>/dev/null
    rm -rf -- "$WORK"
}
trap cleanup EXIT

# The stub of systemctl: restart, stop, is-active.
mkdir -p "$WORK/stubs"
cat > "$WORK/stubs/systemctl" <<EOF
#!/usr/bin/env bash
pidfile=$WORK/server.pid
running() { [[ -f \$pidfile ]] && kill -0 "\$(cat "\$pidfile")" 2>/dev/null; }
stop() { running && kill "\$(cat "\$pidfile")"; rm -f "\$pidfile"; sleep 0.3; }
case \$1 in
    restart)
        stop
        www=\$(readlink -f "$ROOT/app")/www
        mkdir -p "$WORK/empty"
        [[ -d \$www ]] || www=$WORK/empty
        setsid python3 -m http.server $PORT --bind 127.0.0.1 --directory "\$www" >/dev/null 2>&1 &
        echo \$! > "\$pidfile"
        ;;
    stop) stop ;;
    is-active)
        if running; then [[ \$2 == --quiet ]] || echo active; exit 0; fi
        [[ \$2 == --quiet ]] || echo inactive
        exit 3
        ;;
esac
EOF
chmod 755 "$WORK/stubs/systemctl"
export PATH=$WORK/stubs:$PATH

new_root() {
    rm -rf -- "$ROOT"
    mkdir -p "$ROOT"/{bin,configs,data,artifacts}
    cp "$REPO/template/deploy.sh" "$ROOT/bin/deploy.sh"
    chmod 755 "$ROOT/bin/deploy.sh"
    cat > "$ROOT/configs/service.env" <<EOF
# The test service.
TITLE=Demo
UNIT=$SERVICE
SERVICE_USER=root
ENTRY=app.dll
HEALTH_URL=http://127.0.0.1:$PORT/health
VERSION_URL=http://127.0.0.1:$PORT/info
HEALTH_TIMEOUT=6
KEEP_PREVIOUS=2
CONFIG_LINKS=app.json
EOF
    echo '{}' > "$ROOT/configs/app.json"
}

# artifact VERSION [broken|wrong-version|hook-fails]
artifact() {
    local version=$1 kind=${2:-} stage=$WORK/stage
    rm -rf -- "$stage"
    mkdir -p "$stage/www" "$stage/deploy/bin" "$stage/deploy/hooks"
    printf '%s\nabc1234\n2026-10-21T10:00:00Z\n' "$version" > "$stage/VERSION"
    echo binary > "$stage/app.dll"
    [[ $kind == broken ]] || echo ok > "$stage/www/health"
    if [[ $kind == wrong-version ]]; then echo '{"version":"0.0.1"}' > "$stage/www/info"; else printf '{ "version": "%s" }' "$version" > "$stage/www/info"; fi
    cp "$REPO/template/deploy.sh" "$stage/deploy/bin/deploy.sh"
    if [[ $kind == hook-fails ]]; then
        echo 'echo "hook: no database"; exit 1' > "$stage/deploy/hooks/pre-switch"
    else
        echo 'echo "hook $VERSION $ACTIVE_VERSION" >> "$SERVICE_ROOT/data/hooks.log"' > "$stage/deploy/hooks/pre-switch"
    fi
    local file=$ROOT/artifacts/$SERVICE-$version.tar.gz
    tar -czf "$file" -C "$stage" .
    (cd "$ROOT/artifacts" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256")
    echo "$file"
}

active() { basename "$(readlink "$ROOT/app" 2>/dev/null)" 2>/dev/null; }
releases() { (cd "$ROOT" && ls -d v*/ 2>/dev/null | tr -d / | sort -V | tr '\n' ' '); }

check() {
    local name=$1 expected=$2 actual=$3
    if [[ $expected == "$actual" ]]; then
        echo "  ok   $name"
    else
        echo "  FAIL $name: expected [$expected], got [$actual]"
        FAILED=1
    fi
}

run_deploy() { "$ROOT/bin/deploy.sh" "$@" > "$WORK/out.txt" 2>&1; echo $?; }
# The code of the last "@result" line of deploy.sh.
last_result() { grep '^@result ' "$WORK/out.txt" | tail -n 1 | cut -d ' ' -f 2; }

echo "deploy.sh"
new_root

check "the first deploy" 0 "$(run_deploy "$(artifact 1.0.0.1)")"
check "app shows the first release" v1.0.0.1 "$(active)"
check "the hook gets the versions" "hook 1.0.0.1 " "$(tail -n 1 "$ROOT/data/hooks.log")"
check "the configuration is a link" "$ROOT/configs/app.json" "$(readlink "$ROOT/v1.0.0.1/app.json")"

for v in 2 3 4; do run_deploy "$(artifact 1.0.0.$v)" >/dev/null; done
check "keep the active release and two previous" "v1.0.0.2 v1.0.0.3 v1.0.0.4 " "$(releases)"
check "remove the artifacts of removed releases" "" "$(ls "$ROOT/artifacts" | grep -F 1.0.0.1)"
check "the hook gets the active version" "hook 1.0.0.4 1.0.0.3" "$(tail -n 1 "$ROOT/data/hooks.log")"

check "a failed smoke test rolls back" 2 "$(run_deploy "$(artifact 1.0.0.5 broken)")"
check "the result of the rollback" rolled-back "$(last_result)"
check "the cause of the failure" 1 "$(grep -c '^@result rolled-back .*/health не відповідає 2xx' "$WORK/out.txt")"
check "the journal follows a failed smoke test" "$(command -v journalctl >/dev/null && echo 2 || echo 0)" "$(grep -c '^@journal-' "$WORK/out.txt")"
check "app shows the previous release" v1.0.0.4 "$(active)"
check "the failed release is removed" "v1.0.0.2 v1.0.0.3 v1.0.0.4 " "$(releases)"
check "the service works after the rollback" ok "$(curl -fsS "http://127.0.0.1:$PORT/health")"

check "a wrong version in VERSION_URL rolls back" 2 "$(run_deploy "$(artifact 1.0.0.6 wrong-version)")"
check "a failed hook stops the deploy" 1 "$(run_deploy "$(artifact 1.0.0.7 hook-fails)")"
check "the result of the failed hook" hook-failed "$(last_result)"
check "the output of the hook is in the log" 1 "$(grep -c 'hook: no database' "$WORK/out.txt")"
check "the service did not change" v1.0.0.4 "$(active)"
check "the release of the failed hook is removed" "v1.0.0.2 v1.0.0.3 v1.0.0.4 " "$(releases)"

check "the active version again is an error" 1 "$(run_deploy "$ROOT/artifacts/$SERVICE-1.0.0.4.tar.gz")"
check "an older version is an error" 1 "$(run_deploy "$(artifact 1.0.0.0)")"
check "the result of an older version" not-newer "$(last_result)"
file=$(artifact 1.0.0.8)
echo "0000  $(basename "$file")" > "$file.sha256"
check "a wrong checksum is an error" 1 "$(run_deploy "$file")"

check "rollback to the previous release" 0 "$(run_deploy --rollback)"
check "the result of the rollback is ok" ok "$(last_result)"
check "app shows v1.0.0.3" v1.0.0.3 "$(active)"
check "rollback to the given release" 0 "$(run_deploy --rollback 1.0.0.4)"
check "app shows v1.0.0.4" v1.0.0.4 "$(active)"
check "rollback to a missing release is an error" 1 "$(run_deploy --rollback 9.9.9)"
run_deploy --status >/dev/null
check "status shows the active release" "  v1.0.0.4  ← app" "$(grep -F '← app' "$WORK/out.txt")"

new_root
check "a failed first deploy stops the service" 3 "$(run_deploy "$(artifact 2.0.0.1 broken)")"
check "the result of a failed first deploy" first-failed "$(last_result)"
check "no app link and no release" ":" "$(active):$(releases)"

echo "ops-runner.sh"
new_root
run_deploy "$(artifact 3.0.0.1)" >/dev/null
BOT=$WORK/opt/ops-bot
mkdir -p "$BOT"/{configs,data/requests,data/results}
echo "$SERVICE" > "$BOT/configs/services"
export BOT_ROOT=$BOT SERVICES_ROOT=$WORK/opt BOT_USER=root
runner=$REPO/deploy/bin/ops-runner.sh

request() { echo "$2" > "$BOT/data/requests/$1.req"; }
request 20261021100000-aaaaaa "deploy $SERVICE 3.0.0.2"
artifact 3.0.0.2 >/dev/null
request 20261021100001-bbbbbb "deploy other-svc 1.0.0.1"
request 20261021100002-cccccc "deploy $SERVICE 1.0.0.1;rm"
request bad-name "rollback $SERVICE -"
bash "$runner" > "$WORK/runner.txt" 2>&1
check "the runner deploys" 0 "$(cat "$BOT/data/results/20261021100000-aaaaaa.done" 2>/dev/null)"
check "the log of the deploy" 1 "$(grep -c 'Готово: demo-svc v3.0.0.2' "$BOT/data/results/20261021100000-aaaaaa.log")"
check "a service that is not allowed" 126 "$(cat "$BOT/data/results/20261021100001-bbbbbb.done" 2>/dev/null)"
check "the result of a rejected request" 1 "$(grep -c '^@result rejected ' "$BOT/data/results/20261021100001-bbbbbb.log")"
check "a wrong version" 126 "$(cat "$BOT/data/results/20261021100002-cccccc.done" 2>/dev/null)"
check "a wrong file name gives no result" "" "$(ls "$BOT/data/results" | grep -F bad-name)"
check "all requests are removed" "" "$(ls "$BOT/data/requests")"
request 20261021100003-dddddd "rollback $SERVICE -"
bash "$runner" > "$WORK/runner.txt" 2>&1
check "the runner rolls back" 0 "$(cat "$BOT/data/results/20261021100003-dddddd.done" 2>/dev/null)"
check "app shows v3.0.0.1" v3.0.0.1 "$(active)"

if (( FAILED )); then
    echo "FAILED"
    echo "--- the last output of deploy.sh:"
    cat "$WORK/out.txt"
    exit 1
fi
echo "All tests passed."
