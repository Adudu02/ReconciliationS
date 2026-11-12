#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
if (( $# > 1 )) || [[ ${1:-} != '' && ${1:-} != down ]]; then
    echo 'Usage: scripts/demo-lan.sh [down]' >&2
    exit 1
fi

compose=(docker compose)
[[ ! -f .env ]] || compose+=(--env-file .env)
if [[ ${1:-} == down ]]; then
    [[ ! -f .env.demo ]] || compose+=(--env-file .env.demo)
    # Compose validates interpolation even when stopping services.
    export JWT_SIGNING_KEY="${JWT_SIGNING_KEY:-unused-while-stopping-the-demo-stack}"
    "${compose[@]}" down
    exit
fi

LAN_IP=${LAN_IP:-$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i=1;i<NF;i++) if ($i=="src") {print $(i+1); exit}}' || true)}
if [[ ! $LAN_IP =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
    echo 'No usable LAN IPv4 found. Set LAN_IP to the LAN IPv4 address of this laptop.' >&2
    exit 1
fi
IFS=. read -r -a octets <<< "$LAN_IP"
for octet in "${octets[@]}"; do
    if (( 10#$octet > 255 )); then
        echo 'Invalid LAN_IP: use a LAN IPv4 address.' >&2
        exit 1
    fi
done
if (( 10#${octets[0]} == 0 || 10#${octets[0]} == 127 || 10#${octets[0]} >= 224 )); then
    echo 'LAN_IP must be a reachable LAN IPv4 address, not a loopback or multicast address.' >&2
    exit 1
fi

if [[ -z ${JWT_SIGNING_KEY:-} && ! -f .env.demo ]]; then
    demo_key=$(openssl rand -hex 32)
    (umask 077; printf 'JWT_SIGNING_KEY=%s\n' "$demo_key" > .env.demo)
fi
if [[ -f .env.demo ]]; then
    chmod 600 .env.demo
    compose+=(--env-file .env.demo)
fi
# APP_DB_PASSWORD already has a Compose default; do not rotate the database password.
export API_BIND=0.0.0.0
export Session__AllowedOrigins__0="http://$LAN_IP:8080"
"${compose[@]}" up -d --build --wait
printf 'Open on laptop B: http://%s:8080\nHealth: http://%s:8080/health\nNote: if firewalld blocks access, run sudo firewall-cmd --add-port=8080/tcp on laptop A.\n' "$LAN_IP" "$LAN_IP"
