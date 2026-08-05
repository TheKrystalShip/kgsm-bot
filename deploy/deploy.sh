#!/usr/bin/env bash
#
# deploy.sh — build + deploy the kgsm-bot Discord surface. Fully headless: no sudo, no prompts.
#
#   ./deploy/deploy.sh
#
# Assumes deploy/setup.sh has provisioned this host (prefix owned by you, the unit symlinked out
# of a directory you own, polkit grant in place). If it has not, this script says so and stops
# before building.
#
# Publishes a framework-dependent single-file binary as YOU — the host must have the .NET 10
# runtime; it is deliberately not bundled. The bot builds from sibling checkouts via relative
# ProjectReference (kgsm-lib + kgsm-llm), so the full umbrella checkout must be present.
#
# The env file /etc/kgsm-bot/kgsm-bot.env is setup.sh's business and is never touched here — the
# live Discord token survives every redeploy.
#
# Knobs: RID, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

PROJECT_CSPROJ="$REPO_DIR/src/KGSM.Bot.Discord/KGSM.Bot.Discord.csproj"
WORKSPACE="$(cd "$REPO_DIR/.." && pwd)"
RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -f "$PROJECT_CSPROJ" ]] || { err "project not found: $PROJECT_CSPROJ"; exit 1; }

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
dotnet publish "$PROJECT_CSPROJ" -c Release -r "$RID" --no-self-contained -o "$PUBLISH_DIR"

# ── 2. Refresh the unit if it changed (we own the file; systemd reads it via the symlink) ──
install_units_unprivileged

# ── 2b. Publish the leaf config descriptor ────────────────────────────────────
# Before the swap, so the surface kgsm-api reads never lags the binary that implements it.
install_leaf_descriptor

# ── 2c. Publish the command manifest ──────────────────────────────────────────
# The catalog of slash commands the Control Panel lists, in a subdirectory of the same discovery
# tree so it cannot be mistaken for a config descriptor by the scan that reads those. Written by
# the build from the assembly it just produced; installed here, unprivileged, because the parent
# directory is ours. This bot has no listening surface and the list has to be readable while the
# unit is stopped — the same reason the descriptor is a file rather than an endpoint.
install_command_manifest() {
    local src="${REPO_DIR}/deploy/${PROJECT}.commands.json"
    [[ -f "$src" ]] || { warn "no command manifest at ${src} — the Control Panel will list no commands."; return 0; }

    local dir="${LEAF_DESCRIPTOR_DIR}/commands"
    mkdir -p "$dir"

    local dst="${dir}/${LEAF_ID}.json"
    if ! cmp -s "$src" "$dst"; then
        log "command manifest changed → ${dst}"
        install -m 0644 "$src" "$dst"
    fi
}
install_command_manifest

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
sysctl_do stop "$SERVICE" || true
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUBLISH_DIR/" "$PREFIX/"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

log "starting ${SERVICE}"
sysctl_do start "$SERVICE"
STOPPED=0

# ── 4. Verify ──────────────────────────────────────────────────────────────────
# The bot exposes no health endpoint — it holds an outbound Discord gateway connection. The
# honest check is that systemd still has it running after it has had a moment to fail; nothing
# stronger is claimed here. A token or gateway problem shows up in the journal, not in a probe.
log "waiting for ${SERVICE} to settle ..."
if wait_health; then
    log "kgsm-bot is running ✓  (no health endpoint — verify Discord presence for a real check)"
    if grep -q 'YOUR_DISCORD_BOT_TOKEN_HERE' "$ENV_FILE" 2>/dev/null; then
        warn "${ENV_FILE} still holds the placeholder token — set Discord__Token, then:"
        warn "    systemctl restart ${SERVICE}"
    fi
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service did not stay running within ${HEALTH_TRIES}s. Recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
