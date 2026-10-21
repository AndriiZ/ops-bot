#!/usr/bin/env bash
# Run the deploy requests of ops-bot (README.md §4). The unit ops-runner.path starts this script
# (through ops-runner.service) when a file data/requests/*.req exists.
# The bot has no root rights: it only writes the request files. Thus this script examines each request strictly.
# A request is one line: "deploy <service> <VERSION>" or "rollback <service> <VERSION|->".
# The result for the bot: data/results/<id>.log (the output of deploy.sh) and data/results/<id>.done (the exit code).
# Exit code 126: the request is not correct; deploy.sh did not run.
set -uo pipefail

BOT_ROOT=${BOT_ROOT:-/opt/ops-bot}
SERVICES_ROOT=${SERVICES_ROOT:-/opt}
BOT_USER=${BOT_USER:-ops-bot}
MAX_AGE=${MAX_AGE:-900}
TIMEOUT=${TIMEOUT:-30m}
REQUESTS=$BOT_ROOT/data/requests
RESULTS=$BOT_ROOT/data/results
ALLOWED=$BOT_ROOT/configs/services
VERSION_RE='^[0-9]{1,9}(\.[0-9]{1,9}){1,3}$'
SERVICE_RE='^[a-z0-9][a-z0-9-]{0,31}$'

[[ $EUID -eq 0 ]] || { echo "Запускайте від root." >&2; exit 1; }
mkdir -p "$RESULTS"

# Write the exit code. The .done file comes last: the bot reads the result when .done exists.
finish() {
    local id=$1 code=$2
    echo "$code" > "$RESULTS/.$id.done"
    chown "$BOT_USER": "$RESULTS/$id.log" "$RESULTS/.$id.done"
    mv -f "$RESULTS/.$id.done" "$RESULTS/$id.done"
}

reject() {
    local id=$1
    shift
    printf 'Запит відхилено: %s\n@result rejected %s\n' "$*" "$*" >> "$RESULTS/$id.log"
    finish "$id" 126
}

run_one() {
    local request=$1 id age line action service version extra root script owner mode
    id=$(basename "$request" .req)

    # Accept only a regular file with a correct name. Do not follow a symbolic link and do not show the content.
    if [[ ! $id =~ ^[0-9]{14}-[0-9a-f]{6}$ || -L $request || ! -f $request ]]; then
        rm -rf -- "$request"
        echo "Пропущено неправильний файл запиту: $(basename "$request")" >&2
        return
    fi
    age=$(( $(date +%s) - $(stat -c %Y "$request") ))
    line=$(head -c 200 "$request" | head -n 1)
    rm -f -- "$request"
    : > "$RESULTS/$id.log"
    read -r action service version extra <<< "$line"

    if (( age > MAX_AGE )); then reject "$id" "запит застарів ($age с)"; return; fi
    if [[ -n ${extra:-} ]]; then reject "$id" "зайві слова в запиті"; return; fi
    if [[ ! ${service:-} =~ $SERVICE_RE ]] || ! grep -qxF -- "$service" "$ALLOWED" 2>/dev/null; then
        reject "$id" "сервісу немає в $ALLOWED"
        return
    fi
    root=$SERVICES_ROOT/$service
    script=$root/bin/deploy.sh
    # deploy.sh runs as root: only root can change it.
    if [[ ! -f $script || -L $script ]]; then reject "$id" "немає $script"; return; fi
    read -r owner mode <<< "$(stat -c '%u %a' "$script")"
    if [[ $owner != 0 || $(( 8#$mode & 8#022 )) != 0 ]]; then reject "$id" "$script може змінювати не лише root"; return; fi

    local args=()
    case ${action:-} in
        deploy)
            [[ ${version:-} =~ $VERSION_RE ]] || { reject "$id" "неправильна версія"; return; }
            args=("$root/artifacts/$service-$version.tar.gz")
            ;;
        rollback)
            if [[ ${version:-} == - ]]; then
                args=(--rollback)
            elif [[ ${version:-} =~ $VERSION_RE ]]; then
                args=(--rollback "$version")
            else
                reject "$id" "неправильна версія"
                return
            fi
            ;;
        *) reject "$id" "невідома дія"; return ;;
    esac

    echo "$line ($id)"
    timeout --kill-after=1m "$TIMEOUT" "$script" "${args[@]}" >> "$RESULTS/$id.log" 2>&1
    local code=$?
    # 124: timeout stopped deploy.sh; 137: timeout killed it.
    if (( code == 124 || code == 137 )); then
        echo "@result timeout deploy.sh не завершився за $TIMEOUT" >> "$RESULTS/$id.log"
    fi
    echo "Код завершення: $code"
    finish "$id" "$code"
}

shopt -s nullglob
for request in "$REQUESTS"/*.req; do
    run_one "$request"
done
