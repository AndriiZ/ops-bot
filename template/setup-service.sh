#!/usr/bin/env bash
# The standard layout of a service on the host (ops-bot/template/setup-service.sh). Copy this file into the project
# without changes and run it as root from setup.sh of the project:
#   setup-service.sh <id> <service-user> <unit-file> <service.env> [deploy.sh]
# The script makes the system user, the directories with their rights, installs bin/deploy.sh, configs/service.env
# and the systemd unit, and adds the service to ops-bot (/opt/ops-bot/configs/services) if ops-bot is on the host.
# The owner of artifacts/ is UPLOADER (default: the user that runs sudo): build.sh --upload copies the artifacts there.
# A second run does not change data/, the releases and the other files in configs/.
set -euo pipefail

ID=${1:?"id сервісу"}
SERVICE_USER=${2:?"користувач сервісу"}
UNIT_FILE=${3:?"файл юніта systemd"}
SERVICE_ENV=${4:?"service.env"}
DEPLOY_SH=${5:-$(dirname "$(readlink -f "$0")")/deploy.sh}
SERVICES_ROOT=${SERVICES_ROOT:-/opt}
UPLOADER=${UPLOADER:-${SUDO_USER:-root}}
ROOT=$SERVICES_ROOT/$ID

[[ $EUID -eq 0 ]] || { echo "Запускайте від root (sudo)." >&2; exit 1; }
[[ $ID =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]] || { echo "id: малі латинські літери, цифри й '-', до 32 символів." >&2; exit 1; }
for file in "$UNIT_FILE" "$SERVICE_ENV" "$DEPLOY_SH"; do
    [[ -f $file ]] || { echo "Немає $file." >&2; exit 1; }
done

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$SERVICE_USER"
    echo "Створено користувача $SERVICE_USER."
fi

# ops-bot reads the names of the releases, the link app, artifacts/ and configs/service.env.
# The secrets in configs/ must have the mode 640 (root:<service-user>).
install -d -m 755 -o root -g root "$ROOT" "$ROOT/bin"
install -d -m 755 -o root -g "$SERVICE_USER" "$ROOT/configs"
install -d -m 750 -o "$SERVICE_USER" -g "$SERVICE_USER" "$ROOT/data"
install -d -m 755 -o "$UPLOADER" -g "$(id -gn "$UPLOADER")" "$ROOT/artifacts"
install -m 755 -o root -g root "$DEPLOY_SH" "$ROOT/bin/deploy.sh"
install -m 644 -o root -g root "$SERVICE_ENV" "$ROOT/configs/service.env"

unit=$(basename "$UNIT_FILE")
install -m 644 -o root -g root "$UNIT_FILE" "/etc/systemd/system/$unit"
systemctl daemon-reload
# The unit has ConditionPathExists on app: before the first deploy, it does not start.
systemctl enable "$unit" >/dev/null

if [[ -d $SERVICES_ROOT/ops-bot/configs ]]; then
    services=$SERVICES_ROOT/ops-bot/configs/services
    touch "$services"
    if ! grep -qxF -- "$ID" "$services"; then
        echo "$ID" >> "$services"
        echo "Сервіс $ID додано до ops-bot."
    fi
fi

echo "Каталог сервісу: $ROOT (артефакти завантажує $UPLOADER у $ROOT/artifacts)."
