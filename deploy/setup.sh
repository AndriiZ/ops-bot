#!/usr/bin/env bash
# The first setup of ops-bot on a host (README.md §5). Copy deploy/ and template/ to the host and run as root:
#   scp -r deploy template HOST:ops-bot-setup/
#   ssh -t HOST sudo bash ops-bot-setup/deploy/setup.sh
# The script makes /opt/ops-bot by the standard of services, the configuration with a new webhook secret,
# ops-runner (root) and its units. A second run keeps the configuration and the data.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
template=$(cd "$here/../template" && pwd)
ROOT=/opt/ops-bot

[[ $EUID -eq 0 ]] || { echo "Запускайте від root (sudo)." >&2; exit 1; }

bash "$template/setup-service.sh" ops-bot ops-bot "$here/systemd/ops-bot.service" "$here/service.env" "$template/deploy.sh"

# The bot writes the requests and reads the results; ops-runner (root) reads the requests and writes the results.
install -d -m 750 -o ops-bot -g ops-bot "$ROOT/data/requests" "$ROOT/data/results" "$ROOT/data/state"
install -m 755 -o root -g root "$here/bin/ops-runner.sh" "$ROOT/bin/ops-runner.sh"
# The list of the managed services. Root owns it: the bot cannot add a service for ops-runner.
touch "$ROOT/configs/services"
chmod 644 "$ROOT/configs/services"

config=$ROOT/configs/appsettings.Production.json
if [[ ! -f $config ]]; then
    secret=$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')
    sed "s/GENERATED-BY-SETUP/$secret/" "$here/appsettings.Production.example.json" > "$config"
    chown root:ops-bot "$config"
    chmod 640 "$config"
    echo "Створено $config: заповніть Token, WebhookUrl і AdminIds."
fi

install -m 644 -o root -g root "$here/systemd/ops-runner.path" "$here/systemd/ops-runner.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now ops-runner.path >/dev/null

echo
echo "ops-bot: $ROOT. Далі (README.md §5):"
echo "  1. sudo nano $config — токен від @BotFather, адреса webhook, AdminIds."
echo "  2. Реверс-проксі з TLS: лише POST /telegram/webhook → 127.0.0.1:8095."
echo "  3. На машині власника: deploy/build.sh --upload HOST"
echo "  4. Перший деплой: sudo $ROOT/bin/deploy.sh $ROOT/artifacts/ops-bot-<VERSION>.tar.gz"
echo "Сервіси підключаються своїми setup.sh (template/setup-service.sh додає їх у $ROOT/configs/services)."
