#!/usr/bin/env bash
#
# Build + deploy kgsm-bot in one go.
#
#   ./deploy/deploy.sh
#
# Publishes the bot as YOU (the invoking, service-owning user) and uses sudo ONLY for the steps
# that touch systemd / root-owned paths. Run it as your normal user; it prompts for your sudo
# password when it reaches the privileged steps.
#
#   * the binary is a FRAMEWORK-DEPENDENT single file — the host must have the .NET 10 runtime
#     (checked up front); the runtime is NOT bundled, keeping the artifact small,
#   * the binary tree is synced in place into /opt/kgsm-bot (stale files pruned),
#   * the systemd unit is (re)installed with User=/Group= rewritten to the invoking user (this
#     host has no dedicated 'kgsm' user), only when it changed,
#   * the env file /etc/kgsm-bot/kgsm-bot.env is created from the template only if absent and is
#     NEVER overwritten — your Discord token survives a redeploy.
#
# kgsm-bot ProjectReferences its siblings (kgsm-llm, kgsm-lib) by relative path, so this requires
# the full tks workspace (umbrella checkout) to be present — checked below.
#
# Automation hook (no password ever in this script): the privileged calls go through "$SUDO".
# Non-interactive: SUDO='sudo -A' SUDO_ASKPASS=/path/to/askpass ./deploy/deploy.sh
#
set -euo pipefail

# ── Paths / config ────────────────────────────────────────────────────────────
PREFIX="/opt/kgsm-bot"
UNIT_DST="/etc/systemd/system/kgsm-bot.service"
ENV_DIR="/etc/kgsm-bot"
ENV_FILE="$ENV_DIR/kgsm-bot.env"
SERVICE="kgsm-bot"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKSPACE="$(cd "$REPO_DIR/.." && pwd)"
PROJECT="$REPO_DIR/src/KGSM.Bot.Discord/KGSM.Bot.Discord.csproj"
UNIT_SRC="$REPO_DIR/deploy/kgsm-bot.service"
ENV_EXAMPLE="$REPO_DIR/deploy/kgsm-bot.env.example"
PUBLISH_DIR="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"
SVC_USER="${KGSM_BOT_USER:-$(id -un)}"
SVC_GROUP="${KGSM_BOT_GROUP:-$(id -gn)}"
SUDO="${SUDO:-sudo}"

# ── Helpers ─────────────────────────────────────────────────────────────────
log() { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
err() { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap — attempting to bring it back up..."
        $SUDO systemctl start "$SERVICE" \
            && err "restarted ${SERVICE} (running the PREVIOUS build)." \
            || err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the service user. It builds as you and sudo's only the systemd steps."
    exit 1
fi
[[ -f "$PROJECT" ]] || { err "project not found: $PROJECT"; exit 1; }

# Umbrella checkout: the bot builds from sibling sources via relative ProjectReference.
for sib in kgsm-llm kgsm-lib; do
    [[ -d "$WORKSPACE/$sib" ]] || { err "sibling repo missing: $WORKSPACE/$sib"; err "clone the full tks workspace (umbrella checkout) so $sib is present."; exit 1; }
done

# Framework-dependent: the host needs the .NET 10 runtime (we don't bundle it).
if ! dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.NETCore.App 10\.'; then
    err "the .NET 10 runtime is not installed (need 'Microsoft.NETCore.App 10.x'). Check: dotnet --list-runtimes"
    exit 1
fi

# ── 1. Build (as the invoking user — fail fast before any service disruption) ──
log "publishing framework-dependent single-file (${RID}) → ${PUBLISH_DIR}"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" --no-self-contained -o "$PUBLISH_DIR"

# ── 2. Privileged prep (no service disruption yet) ────────────────────────────
# Install dir: create owned by the service user, or take ownership if a prior root-era install
# left it root-owned — so a redeploy just works.
if [[ ! -d "$PREFIX" ]]; then
    log "creating ${PREFIX} (owned by ${SVC_USER})"
    $SUDO install -d -m 0755 -o "$SVC_USER" -g "$SVC_GROUP" "$PREFIX"
elif [[ ! -w "$PREFIX" ]]; then
    log "taking ownership of ${PREFIX} (was not writable by $(id -un))"
    $SUDO chown -R "$SVC_USER:$SVC_GROUP" "$PREFIX"
fi

# Env file: create from template only if absent — never clobber the live token.
ENV_FRESH=0
if [[ ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} from template — you MUST edit it and set Discord__Token"
    $SUDO install -d -m 0755 "$ENV_DIR"
    $SUDO install -m 0640 -g "$SVC_GROUP" "$ENV_EXAMPLE" "$ENV_FILE"
    ENV_FRESH=1
fi

# Unit: render with the service user substituted in, install only if it changed.
TMP_UNIT="$(mktemp)"
sed "s/^User=.*/User=${SVC_USER}/; s/^Group=.*/Group=${SVC_GROUP}/" "$UNIT_SRC" > "$TMP_UNIT"
UNIT_CHANGED=0
if ! cmp -s "$TMP_UNIT" "$UNIT_DST"; then
    log "installing systemd unit → ${UNIT_DST} (User=${SVC_USER})"
    $SUDO install -m 0644 "$TMP_UNIT" "$UNIT_DST"
    UNIT_CHANGED=1
fi
rm -f "$TMP_UNIT"

# ── 3. The swap (stop → sync the tree → start) ─────────────────────────────────
log "stopping ${SERVICE}"
$SUDO systemctl stop "$SERVICE" 2>/dev/null || true
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUBLISH_DIR/" "$PREFIX/"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    $SUDO systemctl daemon-reload
fi

log "enabling + starting ${SERVICE}"
$SUDO systemctl enable --now "$SERVICE" >/dev/null 2>&1 || $SUDO systemctl start "$SERVICE"
STOPPED=0

# ── 4. Verify (the bot has no /health endpoint — assert it stayed active) ──────
if systemctl is-active --quiet "$SERVICE"; then
    if [[ "$ENV_FRESH" -eq 1 ]] || $SUDO grep -q 'YOUR_DISCORD_BOT_TOKEN_HERE' "$ENV_FILE" 2>/dev/null; then
        err "${SERVICE} is running but ${ENV_FILE} still has the PLACEHOLDER token — it cannot log in to Discord."
        err "edit ${ENV_FILE} (set Discord__Token=<your token>), then: sudo systemctl restart ${SERVICE}"
    else
        log "kgsm-bot is running ✓"
    fi
    systemctl --no-pager --lines=6 status "$SERVICE" 2>/dev/null | tail -n 6 || true
else
    err "${SERVICE} did not stay active. Recent logs:"
    $SUDO journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
