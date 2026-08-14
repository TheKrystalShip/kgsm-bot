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
# The speech model the voice surface recognises with. Not in the install prefix, because deploy.sh
# syncs that with rsync --delete and would take a 488MB file on every deploy; the state directory is
# the bot's own and survives. Pinned by digest — it is loaded into the bot's own process, and "the
# download finished" is not the same claim as "this is the model".
VOICE_MODEL_NAME="ggml-small.en.bin"
VOICE_MODEL_DIR="/var/lib/${PROJECT}/models"
VOICE_MODEL_URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/${VOICE_MODEL_NAME}"
VOICE_MODEL_SHA256="c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"

# The synthesis model the bot answers out loud with. Fetched here rather than left to the library,
# which downloads it into the working directory — that is the install prefix, which the deploy syncs
# with rsync --delete, so it would be re-fetched on every deploy forever.
SPEECH_MODEL_NAME="kokoro.onnx"
SPEECH_MODEL_URL="https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/${SPEECH_MODEL_NAME}"
SPEECH_MODEL_SHA256="0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05"

# Anything else one-shot and privileged this project needs provisioned. setup.sh calls it once
# the units are live; deploy.sh never does. Keep it idempotent — setup.sh is re-runnable. Use
# "$SUDO" for privileged steps.
#
# The guild store needs nothing here. /var/lib/kgsm-bot is the unit's StateDirectory=kgsm-bot, which
# systemd creates owned by User= before ExecStart — so this function runs after the units are live
# and writes into a directory it already owns, with no privilege. The store FILE is never created
# here either: the bot creates it, /setup writes it, and losing it loses every channel a server's
# history is in.
#
# What is provisioned is the two voice models — one to hear with, one to speak with — because the
# alternative is a host that switches voice on and silently does neither until somebody puts files in
# place by hand. Together they are most of a gigabyte for a surface that ships off;
# KGSM_VOICE_MODEL=0 skips both, and re-running this script after setting it to 1 fetches them.
setup_project_extras() {
    [[ "${KGSM_VOICE_MODEL:-1}" == "0" ]] && {
        log "skipping the voice models (KGSM_VOICE_MODEL=0) — the bot will neither understand nor speak"
        return 0
    }

    install -d -m 0755 "$VOICE_MODEL_DIR" 2>/dev/null ||
        $SUDO install -d -m 0755 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" "$VOICE_MODEL_DIR"

    fetch_model "hearing"  "$VOICE_MODEL_NAME"  "$VOICE_MODEL_URL"  "$VOICE_MODEL_SHA256"  "488MB"
    fetch_model "speaking" "$SPEECH_MODEL_NAME" "$SPEECH_MODEL_URL" "$SPEECH_MODEL_SHA256" "325MB"
}

# One model: present and correct is a no-op, present and wrong is replaced, absent is fetched.
# $1 = what it is for, $2 = filename, $3 = url, $4 = digest, $5 = human size
fetch_model() {
    local what="$1" name="$2" url="$3" digest="$4" size="$5"
    local model="${VOICE_MODEL_DIR}/${name}"

    if [[ -f "$model" ]] && sha256_matches "$model" "$digest"; then
        return 0
    fi

    # A file that is present and wrong is worse than one that is absent: it loads, behaves badly, and
    # reads as a tuning problem. So it goes BEFORE the fetch is attempted rather than after it
    # succeeds — if the download then fails, the bot reports having no model, which is true and
    # actionable, instead of quietly producing nonsense.
    if [[ -f "$model" ]]; then
        warn "${model} does not match its expected digest — discarding it and re-fetching"
        rm -f "$model"
    fi

    log "fetching the ${what} model (~${size}, once) → ${model}"

    # Downloaded beside the target and moved into place only once verified, so an interrupted fetch
    # leaves no half-file for the bot to load.
    local tmp="${model}.partial"
    if ! curl -fL --retry 3 --retry-delay 2 -o "$tmp" "$url"; then
        rm -f "$tmp"
        warn "could not fetch the ${what} model — the voice surface will start without it."
        warn "  fetch it later:  KGSM_VOICE_MODEL=1 ./deploy/setup.sh"
        return 0
    fi

    if ! sha256_matches "$tmp" "$digest"; then
        rm -f "$tmp"
        warn "the downloaded ${what} model does not match its expected digest — discarded."
        return 0
    fi

    mv -f "$tmp" "$model"
    log "${what} model installed ✓"
}

sha256_matches() {   # $1 = file, $2 = expected digest
    [[ "$(sha256sum "$1" | cut -d' ' -f1)" == "$2" ]]
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
