#!/usr/bin/env bash
#
# deploy-common.sh — the shared parameter block + helpers for kgsm-bot's deploy scripts.
#
# Sourced by BOTH deploy/setup.sh (the one-shot privileged host provisioning) and
# deploy/deploy.sh (the headless code delivery). Every path, unit name and user lives here
# exactly once, so the two entry points can never disagree about what this project installs.
#
# The canonical source of this pattern is tks/scripts/deploy-template/ — see its README for the
# contract. This copy is vendored so a standalone kgsm-bot clone deploys with no umbrella
# checkout present. Keep everything below the PROJECT BLOCK in step with the template.
#
# Not executable on its own.

# This file only DEFINES things; every variable below is consumed by the two scripts that
# source it, which shellcheck cannot see from here.
# shellcheck disable=SC2034

set -euo pipefail

# ── Identity (needed by the project block below) ──────────────────────────────
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The user that owns the install and runs the service. Everything is provisioned FOR this
# user so that day-to-day deploys need no privilege at all.
DEPLOY_USER="${KGSM_DEPLOY_USER:-$(id -un)}"
DEPLOY_GROUP="${KGSM_DEPLOY_GROUP:-$(id -gn)}"

# ── PROJECT BLOCK — the only part that changes per repo ───────────────────────
PROJECT="kgsm-bot"

UNITS=("kgsm-bot.service")
ENABLE_UNITS=("kgsm-bot.service")

PREFIX="/opt/${PROJECT}"

ENV_DIR="/etc/${PROJECT}"
ENV_FILE="${ENV_DIR}/${PROJECT}.env"
ENV_EXAMPLE="${REPO_DIR}/deploy/${PROJECT}.env.example"

HEALTH_TRIES="${HEALTH_TRIES:-30}"

# This project's leaf config descriptor — the JSON declaring its full configurable surface, which
# kgsm-api reads to render the Control Panel's config page for this leaf. setup.sh creates the
# discovery directory; deploy.sh installs the file there unprivileged on every deploy, so the
# descriptor can never be older than the binary it describes. Format: tks/leaf-config-descriptor.md.
# Leave empty for a project that is not a leaf (nothing is installed and nothing is asserted).
LEAF_DESCRIPTOR="${REPO_DIR}/deploy/${PROJECT}.leaf.json"

# The leaf id kgsm-api knows this project by — the descriptor's "id", its filename stem in the
# discovery dir, and the {leaf} segment of the API's config route. Usually the project name minus
# the kgsm- prefix, but NOT always: kgsm-llm ships the leaf "assistant". State it, don't derive it.
LEAF_ID="${PROJECT#kgsm-}"

render_unit() {   # $1 = unit filename
    sed "s/^User=.*/User=${DEPLOY_USER}/; s/^Group=.*/Group=${DEPLOY_GROUP}/" \
        "${REPO_DIR}/deploy/$1"
}

# The bot exposes no health endpoint — it holds an outbound Discord gateway connection. The
# honest probe is therefore "systemd still has it running", and nothing stronger is claimed.
health_probe() {
    systemctl is-active --quiet "$SERVICE"
}
# Anything else one-shot and privileged this project needs provisioned. setup.sh calls it once the
# units are live; deploy.sh never does.
#
# The bot's own state — the guild store — is created by the bot, written by /setup, and lives in the
# unit's StateDirectory, which systemd makes before ExecStart. Hearing and speaking are the kgsm-speech
# leaf's, provisioned by that repo. What is provisioned here is the member wire's web front: the vhost
# the other members reach this bot through, and the pieces that serve the name the cluster's DNS anchor
# gives the chat capability — the bot's keys, the site it generates, its proxy rules (which the vhost
# includes too), the include that loads the site, and the grant to reload the web server after writing
# it.
NGINX_FRAGMENT="${REPO_DIR}/deploy/nginx/${PROJECT}.conf"
TLS_DIR="/var/lib/kgsm/tls/${PROJECT}"
SITES_DIR="/var/lib/kgsm/nginx"
LOCATIONS_SRC="${REPO_DIR}/deploy/nginx/${PROJECT}.locations"
SITES_INCLUDE_SRC="${REPO_DIR}/deploy/nginx/${PROJECT}.sites.conf"
NGINX_RELOAD_TEMPLATE="${REPO_DIR}/deploy/polkit/47-${PROJECT}-nginx-reload.rules.in"
NGINX_RELOAD_DST="/etc/polkit-1/rules.d/47-${PROJECT}-nginx-reload.rules"

setup_project_extras() {
    if [[ ! -d /etc/nginx/conf.d ]]; then
        log "nginx is not installed on this host — the member wire is reached some other way, and the chat capability's name is served by nothing here"
        return 0
    fi

    # Keys live here, readable by this component alone; the web server reads them as root.
    if [[ ! -d /var/lib/kgsm/tls ]]; then
        $SUDO install -d -m 0755 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" /var/lib/kgsm/tls
    fi
    $SUDO install -d -m 0700 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" "$TLS_DIR"

    # Shared by every component on the machine, each writing and including only its own file. Setgid so
    # a file keeps the directory's group whichever component writes it.
    if [[ ! -d "$SITES_DIR" ]]; then
        log "creating ${SITES_DIR} — the sites KGSM components generate"
        $SUDO install -d -m 2775 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" "$SITES_DIR"
    fi

    $SUDO install -D -m 0644 -o root -g root "$LOCATIONS_SRC" "/etc/nginx/kgsm/${PROJECT}.locations"
    # This component's own include, one per component, so two components on one machine never claim
    # the same file.
    $SUDO install -m 0644 -o root -g root "$SITES_INCLUDE_SRC" "/etc/nginx/conf.d/00-${PROJECT}-sites.conf"

    local rendered
    rendered="$(mktemp)"
    sed -e "s|@PROJECT@|${PROJECT}|g" -e "s|@SVC_USER@|${DEPLOY_USER}|g" "$NGINX_RELOAD_TEMPLATE" > "$rendered"
    if ! $SUDO cmp -s "$rendered" "$NGINX_RELOAD_DST" 2>/dev/null; then
        log "installing the web-server reload grant → ${NGINX_RELOAD_DST}"
        $SUDO install -D -m 0644 "$rendered" "$NGINX_RELOAD_DST"
    fi
    rm -f "$rendered"

    # The vhost last: it includes the proxy rules installed above. Validated before reloading, so a bad
    # fragment fails here, loudly, rather than at the next reload for an unrelated reason.
    log "installing the nginx vhost → /etc/nginx/conf.d/$(basename "$NGINX_FRAGMENT")"
    $SUDO install -m 0644 -o root -g root "$NGINX_FRAGMENT" "/etc/nginx/conf.d/$(basename "$NGINX_FRAGMENT")"
    if $SUDO nginx -t > /dev/null 2>&1; then
        $SUDO systemctl reload nginx 2>/dev/null || true
    else
        warn "nginx -t failed after installing the fragment — NOT reloading; run 'sudo nginx -t' to see why"
    fi
}

# ── END PROJECT BLOCK ─────────────────────────────────────────────────────────

# ── Derived paths (do not edit) ───────────────────────────────────────────────
# Where the REAL unit files live: a user-owned directory beside the project's config. systemd
# reaches them through a symlink at /etc/systemd/system/<unit> that setup.sh plants once. This
# is what lets deploy.sh update a unit with no sudo — it writes a file it owns, then asks
# systemd (via the polkit grant) to re-read it.
UNIT_DIR="${ENV_DIR}/systemd"
SYSTEMD_DIR="/etc/systemd/system"

# The polkit grant setup.sh installs: lets DEPLOY_USER drive systemctl for THIS project's units
# with no password and no interactive auth agent.
POLKIT_DST="/etc/polkit-1/rules.d/48-${PROJECT}-deploy.rules"

# The polkit rule's CONTENT is a committed file, not a heredoc, so what the host grants can be
# read and reviewed without running anything. Only the deploying user and the unit list cannot be
# known until install time, and those are the template's two placeholders.
POLKIT_TEMPLATE="${REPO_DIR}/deploy/polkit/48-${PROJECT}-deploy.rules.in"

render_polkit_rule() {
    [[ -f "$POLKIT_TEMPLATE" ]] || { err "missing polkit template: ${POLKIT_TEMPLATE}"; return 1; }

    local units_js="" u
    for u in "${UNITS[@]}"; do
        units_js+="        \"${u}\": true,"$'\n'
    done
    units_js="${units_js%$'\n'}"

    local rendered
    rendered="$(< "$POLKIT_TEMPLATE")"
    rendered="${rendered//@PROJECT@/${PROJECT}}"
    rendered="${rendered//@DEPLOY_USER@/${DEPLOY_USER}}"
    rendered="${rendered//@UNITS@/${units_js}}"
    printf '%s\n' "$rendered"
}

SERVICE="${UNITS[0]}"           # the primary unit, e.g. kgsm-api.service
PUBLISH_DIR="${REPO_DIR}/artifacts/publish"

# Where every leaf drops its config descriptor. Shared across projects and scanned by kgsm-api —
# the API holds no list of leaves, so a new leaf becomes configurable by landing a file here.
LEAF_DESCRIPTOR_DIR="${KGSM_LEAF_DESCRIPTOR_DIR:-/var/lib/kgsm/leaves}"
# Where this host declares who may do what — the Discord app, guild, role-lookup token and role map
# every KGSM surface authorizes against. One file, so a person cannot hold different authority on
# different surfaces. Each unit loads it before its own env file; setup.sh seeds it blank.
SHARED_AUTH_FILE="${KGSM_SHARED_AUTH_FILE:-/etc/kgsm/kgsm-auth.env}"

# Privileged-call indirection, used by setup.sh ONLY. deploy.sh never calls this. An automated
# run can set SUDO='sudo -A' + SUDO_ASKPASS=… to provision without an interactive prompt; no
# password is ever stored in the repo.
SUDO="${SUDO:-sudo}"

# ── Output helpers ────────────────────────────────────────────────────────────
log()  { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m** %s\033[0m\n' "$*" >&2; }
err()  { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

# ── Shared preflight ──────────────────────────────────────────────────────────

# Refuse to run as root. Both entry points build/publish as the invoking user so the source
# tree never gains root-owned obj/bin, and setup.sh templates the grants with a real user.
refuse_root() {
    if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
        err "do NOT run this as root — run it as the service-owning user."
        err "setup.sh sudo's the few steps that need it; deploy.sh needs no privilege at all."
        exit 1
    fi
}

# The contract deploy.sh enforces before it touches anything: this host has been provisioned.
# A missing piece means setup.sh has not run (or has been undone) — say so and stop, rather
# than half-deploying or blocking on a password prompt that will never be answered.
require_setup() {
    local u problem=0

    [[ -d "$PREFIX" && -w "$PREFIX" ]] || {
        err "install prefix ${PREFIX} is missing or not writable by $(id -un)."; problem=1; }
    [[ -d "$UNIT_DIR" && -w "$UNIT_DIR" ]] || {
        err "unit directory ${UNIT_DIR} is missing or not writable by $(id -un)."; problem=1; }

    for u in "${UNITS[@]}"; do
        if [[ ! -L "${SYSTEMD_DIR}/${u}" ]]; then
            err "${SYSTEMD_DIR}/${u} is not a symlink into ${UNIT_DIR}."; problem=1
        elif [[ "$(readlink -f "${SYSTEMD_DIR}/${u}")" != "${UNIT_DIR}/${u}" ]]; then
            err "${SYSTEMD_DIR}/${u} points at $(readlink "${SYSTEMD_DIR}/${u}"), not ${UNIT_DIR}/${u}."
            problem=1
        fi
    done

    if [[ "$problem" -ne 0 ]]; then
        err ""
        err "this host is not provisioned for headless deploys of ${PROJECT}."
        err "run ONCE (it will ask for your sudo password):   ${REPO_DIR}/deploy/setup.sh"
        exit 1
    fi
}

# systemctl, unprivileged, via the polkit grant setup.sh installed. A denial here means the
# grant is missing — surface that as the actionable thing it is instead of a raw polkit error.
sysctl_do() {   # $@ = systemctl arguments
    # --no-ask-password: this path must fail fast rather than block on a prompt nobody will answer.
    if ! systemctl --no-ask-password "$@"; then
        err "systemctl $* was refused."
        err "the polkit grant for ${DEPLOY_USER} is missing or does not cover this unit."
        err "re-run: ${REPO_DIR}/deploy/setup.sh"
        return 1
    fi
}

# Poll health_probe until it passes. Used inside an `if`, so a failing probe never trips ERR.
wait_health() {
    local i
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        health_probe && return 0
        sleep 1
    done
    return 1
}

# Write the rendered units into UNIT_DIR (which we own — no privilege). Sets UNIT_CHANGED=1
# when any unit's content actually changed, so the caller can daemon-reload only when needed.
UNIT_CHANGED=0
install_units_unprivileged() {
    local u tmp
    UNIT_CHANGED=0
    for u in "${UNITS[@]}"; do
        tmp="$(mktemp)"
        render_unit "$u" > "$tmp"
        if ! cmp -s "$tmp" "${UNIT_DIR}/${u}"; then
            log "unit changed → ${UNIT_DIR}/${u}"
            install -m 0644 "$tmp" "${UNIT_DIR}/${u}"
            UNIT_CHANGED=1
        fi
        rm -f "$tmp"
    done
}

# Install this project's leaf config descriptor into the shared discovery directory. Unprivileged:
# the directory is owned by DEPLOY_USER (setup.sh created it), so this is a plain file write.
#
# A project with no descriptor file is simply not a leaf — nothing is installed and nothing fails.
# When the file IS present the descriptor is validated before it lands, because kgsm-api skips a
# malformed one silently: catching it here is the difference between "the panel has no page for
# this leaf" and knowing why.
install_leaf_descriptor() {
    [[ -n "${LEAF_DESCRIPTOR:-}" && -f "$LEAF_DESCRIPTOR" ]] || return 0

    local dst="${LEAF_DESCRIPTOR_DIR}/${LEAF_ID}.json"

    # Validate what we can before it lands: it must parse, and its "id" must be the id this
    # project deploys under — a mismatch would install the file under a name kgsm-api then reads
    # back as a different leaf.
    if command -v python3 >/dev/null 2>&1; then
        if ! python3 - "$LEAF_DESCRIPTOR" "$LEAF_ID" <<'PY'
import json, sys
path, want = sys.argv[1], sys.argv[2]
try:
    d = json.load(open(path))
except Exception as e:
    sys.exit(f"{path} is not valid JSON: {e}")
if d.get("id") != want:
    sys.exit(f"{path} declares id={d.get('id')!r}, but this project deploys leaf id {want!r}.")
PY
        then
            err "refusing to install the leaf descriptor — kgsm-api would skip it and the"
            err "Control Panel would show no configuration for ${PROJECT}."
            return 1
        fi
    fi

    if [[ ! -d "$LEAF_DESCRIPTOR_DIR" ]]; then
        err "leaf descriptor directory ${LEAF_DESCRIPTOR_DIR} is missing."
        err "run ONCE (it will ask for your sudo password):   ${REPO_DIR}/deploy/setup.sh"
        return 1
    fi

    if ! cmp -s "$LEAF_DESCRIPTOR" "$dst"; then
        log "leaf descriptor changed → ${dst}"
        install -m 0644 "$LEAF_DESCRIPTOR" "$dst"
    fi
}
