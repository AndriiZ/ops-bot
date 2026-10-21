#!/usr/bin/env bash
# The standard deploy of a service (ops-bot/template/deploy.sh). Copy this file into the project without changes.
# The layout of a service (ops-bot/README.md §2):
#   /opt/<id>/app -> v<VERSION>   the active release; systemd starts the service through this link
#   /opt/<id>/v<VERSION>/         the releases: the active release and KEEP_PREVIOUS previous releases
#   /opt/<id>/data/               the data that all releases share
#   /opt/<id>/configs/            the configuration; service.env has the parameters of this script
#   /opt/<id>/artifacts/          the artifacts from build.sh --upload: <id>-<VERSION>.tar.gz and .sha256
#   /opt/<id>/bin/deploy.sh       this script; each new release installs its deploy/bin/* here
# Usage (as root):
#   deploy.sh <artifact.tar.gz>       deploy a new release; if the smoke test fails, go back to the active release
#   deploy.sh --rollback [VERSION]    go back to the previous release, or to the given release
#   deploy.sh --status                show the releases and the active release
# Exit codes: 0 done; 1 error, the service did not change; 2 the smoke test failed and the previous release
# works again; 3 the smoke test failed and the rollback also failed (or there is no previous release).
# For ops-bot, the last line is "@result <code> <detail>" (README.md §4). After a failed smoke test, the journal
# of the service since the switch is between the lines "@journal-begin" and "@journal-end".
set -euo pipefail

ROOT=${SERVICE_ROOT:-$(dirname "$(dirname "$(readlink -f "$0")")")}
ID=$(basename "$ROOT")
APP_LINK=$ROOT/app
ENV_FILE=$ROOT/configs/service.env
LOG_FILE=${LOG_FILE:-/var/log/$ID-deploy.log}
LOCK_FILE=${LOCK_FILE:-/run/lock/$ID-deploy.lock}
VERSION_RE='^[0-9]{1,9}(\.[0-9]{1,9}){1,3}$'

log() { echo "[$(date '+%F %T')] $*"; }
result() { echo "@result $*"; }
# fail <code> <message>: the service did not change.
fail() {
    local code=$1
    shift
    log "ПОМИЛКА: $*"
    result "$code" "$*"
    exit 1
}

[[ $EUID -eq 0 ]] || { echo "Запускайте від root (sudo)." >&2; result not-root "deploy.sh needs root"; exit 1; }
[[ -f $ENV_FILE ]] || { result no-config "$ENV_FILE"; exit 1; }

# Read service.env. The file is data, not a shell script: KEY=VALUE, no quotes, no expansion.
declare -A CONF=()
while IFS= read -r line || [[ -n $line ]]; do
    line=${line%$'\r'}
    [[ -z ${line//[[:space:]]/} || $line =~ ^[[:space:]]*# ]] && continue
    [[ $line =~ ^[[:space:]]*([A-Z][A-Z0-9_]*)=(.*)$ ]] || { result bad-config "$line"; exit 1; }
    CONF[${BASH_REMATCH[1]}]=${BASH_REMATCH[2]}
done < "$ENV_FILE"

UNIT=${CONF[UNIT]:-$ID}
SERVICE_USER=${CONF[SERVICE_USER]:-}
ENTRY=${CONF[ENTRY]:-}
HEALTH_URL=${CONF[HEALTH_URL]:-}
VERSION_URL=${CONF[VERSION_URL]:-}
HEALTH_TIMEOUT=${CONF[HEALTH_TIMEOUT]:-60}
KEEP_PREVIOUS=${CONF[KEEP_PREVIOUS]:-2}
read -r -a CONFIG_LINKS <<< "${CONF[CONFIG_LINKS]:-}"
if [[ -z $SERVICE_USER || -z $ENTRY || -z $HEALTH_URL ]]; then
    result bad-config "service.env: SERVICE_USER, ENTRY and HEALTH_URL are necessary"
    exit 1
fi

# Only one deploy of the service at a time.
exec 9>"$LOCK_FILE"
flock -n 9 || fail locked "інший деплой $ID уже виконується"
# All output also goes to LOG_FILE. At exit, wait for tee: the caller must get the full output.
exec > >(tee -a "$LOG_FILE" 9>&-) 2>&1
TEE_PID=$!
trap 'exec >&- 2>&-; wait "$TEE_PID" 2>/dev/null || true' EXIT

# The versions of the releases v<VERSION>, from the oldest to the newest.
releases() {
    local dir name
    for dir in "$ROOT"/v*/; do
        [[ -d $dir ]] || continue
        name=$(basename "$dir")
        if [[ ${name#v} =~ $VERSION_RE ]]; then echo "${name#v}"; fi
    done | sort -V
}

# The version of the active release, or nothing.
active_version() {
    local target
    target=$(readlink -e "$APP_LINK" 2>/dev/null) || return 0
    target=$(basename "$target")
    if [[ ${target#v} =~ $VERSION_RE ]]; then echo "${target#v}"; fi
}

# True if version $1 is newer than version $2.
newer() { [[ $1 != "$2" && $(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1) == "$1" ]]; }

# The newest release below the given version. awk reads all lines: an early exit gives SIGPIPE with pipefail.
previous_of() { releases | awk -v cur="$1" '$0 == cur && !found { found = 1; if (prev != "") print prev } { prev = $0 }'; }

# Make app point to v$1 (a relative link, changed in one step) and restart the service.
switch_to() {
    SWITCHED_AT=$(date +%s)
    ln -sfn "v$1" "$APP_LINK.new"
    mv -Tf "$APP_LINK.new" "$APP_LINK"
    # 9>&-: a child process must not keep the lock.
    systemctl restart "$UNIT" 9>&- || log "systemctl restart $UNIT повернув помилку"
}

# The smoke test: the unit is active, HEALTH_URL gives 2xx, and VERSION_URL shows "version":"<VERSION>".
# SMOKE_PROBLEM gets the cause of a failure.
smoke_test() {
    local version=$1 deadline=$((SECONDS + HEALTH_TIMEOUT)) body
    SMOKE_PROBLEM="немає відповіді"
    while (( SECONDS < deadline )); do
        sleep 2
        if ! systemctl is-active --quiet "$UNIT" 9>&-; then
            SMOKE_PROBLEM="юніт $UNIT не активний"
            continue
        fi
        if ! curl -fsS --max-time 5 -o /dev/null "$HEALTH_URL" 2>/dev/null; then
            SMOKE_PROBLEM="$HEALTH_URL не відповідає 2xx"
            continue
        fi
        if [[ -n $VERSION_URL ]]; then
            body=$(curl -fsS --max-time 5 "$VERSION_URL" 2>/dev/null || true)
            body=${body//[[:space:]]/}
            if [[ $body != *"\"version\":\"$version\""* ]]; then
                SMOKE_PROBLEM="$VERSION_URL не показує версію $version"
                continue
            fi
        fi
        log "Смоктест пройдено: v$version"
        return 0
    done
    log "Смоктест v$version не пройдено за ${HEALTH_TIMEOUT} с: $SMOKE_PROBLEM"
    return 1
}

# The journal of the service since the last switch: the cause of a failed start is usually there.
show_journal() {
    command -v journalctl >/dev/null || return 0
    echo "@journal-begin"
    journalctl --unit "$UNIT" --since "@$SWITCHED_AT" --lines 40 --no-pager --output cat 9>&- 2>/dev/null || true
    echo "@journal-end"
}

# Copy deploy/bin/* of the release into bin/. A new file replaces the old one in one step:
# a running script keeps the old file.
install_bin() {
    local file name
    for file in "$1"/deploy/bin/*; do
        [[ -f $file ]] || continue
        name=$(basename "$file")
        cp "$file" "$ROOT/bin/.$name.new"
        chmod 755 "$ROOT/bin/.$name.new"
        mv -f "$ROOT/bin/.$name.new" "$ROOT/bin/$name"
    done
}

# Keep the active release and KEEP_PREVIOUS releases below it. Remove the older releases and their artifacts.
clean_up() {
    local active=$1 below oldest version file
    mapfile -t below < <(releases | awk -v cur="$active" '$0 == cur { found = 1 } !found { print }')
    if (( ${#below[@]} > KEEP_PREVIOUS )); then
        for version in "${below[@]:0:${#below[@]}-KEEP_PREVIOUS}"; do
            log "Видалення старого релізу v$version"
            rm -rf -- "${ROOT:?}/v$version"
        done
    fi
    oldest=$(releases | head -n 1)
    for file in "$ROOT"/artifacts/"$ID"-*.tar.gz; do
        [[ -f $file ]] || continue
        version=$(basename "$file" .tar.gz)
        version=${version#"$ID"-}
        if [[ $version =~ $VERSION_RE ]] && newer "$oldest" "$version"; then
            rm -f -- "$file" "$file.sha256"
        fi
    done
}

status() {
    local active version
    active=$(active_version)
    echo "Сервіс: $ID (юніт $UNIT: $(systemctl is-active "$UNIT" 9>&- 2>/dev/null || true))"
    echo "Активний реліз: ${active:-немає}"
    for version in $(releases); do
        if [[ $version == "$active" ]]; then echo "  v$version  ← app"; else echo "  v$version"; fi
    done
}

rollback() {
    local current target
    current=$(active_version)
    [[ -n $current ]] || fail no-active "немає активного релізу"
    target=${1:-$(previous_of "$current")}
    [[ -n $target ]] || fail no-previous "немає попереднього релізу"
    [[ $target =~ $VERSION_RE && -d $ROOT/v$target ]] || fail no-release "немає релізу v$target"
    [[ $target != "$current" ]] || fail active "v$target уже активний"
    log "Відкат $ID: v$current → v$target"
    switch_to "$target"
    if smoke_test "$target"; then
        log "Готово: активний v$target"
        result ok "v$target"
        exit 0
    fi
    local problem=$SMOKE_PROBLEM
    show_journal
    log "Реліз v$target не працює, повернення на v$current"
    switch_to "$current"
    if smoke_test "$current"; then
        result target-failed "$problem"
        exit 2
    fi
    log "Реліз v$current теж не працює. Потрібне ручне втручання."
    result both-failed "$problem; $SMOKE_PROBLEM"
    exit 3
}

deploy() {
    local artifact expected actual staging version current new hook name problem
    artifact=$(readlink -f "$1")
    [[ -f $artifact ]] || fail no-artifact "немає файлу $1"
    [[ -f $artifact.sha256 ]] || fail no-checksum "немає $artifact.sha256"
    read -r expected _ < "$artifact.sha256"
    actual=$(sha256sum "$artifact" | cut -d ' ' -f 1)
    [[ $expected == "$actual" ]] || fail checksum "контрольна сума $(basename "$artifact") не збігається"

    # Extract next to the releases, then rename: the release appears in one step.
    staging=$(mktemp -d "$ROOT/.incoming.XXXXXX")
    if ! tar -xzf "$artifact" -C "$staging" --no-same-owner; then
        rm -rf -- "$staging"
        fail extract "архів не розпаковується"
    fi
    version=$(head -n 1 "$staging/VERSION" 2>/dev/null | tr -d '[:space:]' || true)
    if [[ ! $version =~ $VERSION_RE || ! -e $staging/$ENTRY ]]; then
        rm -rf -- "$staging"
        fail not-a-release "архів не схожий на реліз $ID (немає VERSION чи $ENTRY)"
    fi
    current=$(active_version)
    if [[ -d $ROOT/v$version ]]; then
        rm -rf -- "$staging"
        [[ $version == "$current" ]] && fail active "v$version уже активний"
        fail exists "реліз v$version уже є; повернутися до нього: deploy.sh --rollback $version"
    fi
    if [[ -n $current ]] && ! newer "$version" "$current"; then
        rm -rf -- "$staging"
        fail not-newer "v$version не новіший за активний v$current; повернутися до старішого: deploy.sh --rollback"
    fi
    new=$ROOT/v$version
    mv -T "$staging" "$new"
    log "Деплой $ID v$version (активний: ${current:+v}${current:-немає})"

    # The service only reads the release. ops-bot reads VERSION and CHANGES.json of the releases.
    # The secrets are only in configs/: the release gets them through links.
    chown -R root:"$SERVICE_USER" "$new"
    chmod -R u=rwX,go=rX "$new"
    chmod 755 "$new/$ENTRY"
    for name in "${CONFIG_LINKS[@]}"; do
        if [[ ! -e $ROOT/configs/$name ]]; then
            rm -rf -- "$new"
            fail config-missing "немає $ROOT/configs/$name"
        fi
        ln -sfn "$ROOT/configs/$name" "$new/$name"
    done

    # The hook of the project, for example a copy of the database and the migrations. An error stops the deploy.
    hook=$new/deploy/hooks/pre-switch
    if [[ -f $hook ]]; then
        log "Підготовка релізу (pre-switch)"
        if ! SERVICE_ROOT=$ROOT SERVICE_ID=$ID SERVICE_USER=$SERVICE_USER UNIT=$UNIT \
            RELEASE_DIR=$new VERSION=$version ACTIVE_VERSION=$current bash "$hook" 9>&-; then
            rm -rf -- "$new"
            fail hook-failed "pre-switch не вдався; сервіс не змінено"
        fi
    fi

    log "Перемикання app → v$version"
    switch_to "$version"
    if ! smoke_test "$version"; then
        problem=$SMOKE_PROBLEM
        show_journal
        if [[ -z $current ]]; then
            systemctl stop "$UNIT" 9>&- || true
            rm -f -- "$APP_LINK"
            rm -rf -- "$new"
            log "Попереднього релізу немає, сервіс зупинено."
            result first-failed "$problem"
            exit 3
        fi
        log "Відкат на v$current"
        switch_to "$current"
        rm -rf -- "$new"
        if smoke_test "$current"; then
            log "Працює попередній реліз v$current."
            result rolled-back "$problem"
            exit 2
        fi
        log "Реліз v$current після відкату теж не працює. Потрібне ручне втручання."
        result both-failed "$problem; $SMOKE_PROBLEM"
        exit 3
    fi

    install_bin "$new"
    clean_up "$version"
    log "Готово: $ID v$version"
    result ok "v$version"
}

case ${1:-} in
    --rollback) rollback "${2:-}" ;;
    --status) status ;;
    ''|-*) echo "Використання: deploy.sh <артефакт.tar.gz> | --rollback [VERSION] | --status" >&2; result usage "deploy.sh"; exit 1 ;;
    *) deploy "$1" ;;
esac
