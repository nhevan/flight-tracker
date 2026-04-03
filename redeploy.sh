#!/usr/bin/env bash
# redeploy.sh — Pull the latest code and restart FlightTracker on EC2.
#
# Run this directly on the EC2 instance from anywhere in the repo:
#   cd /home/ec2-user/flight-tracker && ./redeploy.sh
#
# For first-time setup use ec2-setup.sh instead.
# For deploying from your local machine use deploy.sh instead.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="/opt/flighttracker/app"
CONFIG="$APP_DIR/appsettings.json"
SERVICE="flighttracker"

step() { echo ""; echo "==> $*"; }

# ── 1. Pull latest code ───────────────────────────────────────────────────────

step "Pulling latest code..."
cd "$REPO_DIR"
git pull

# ── 2. Publish ────────────────────────────────────────────────────────────────

step "Publishing app..."
dotnet publish -c Release -o "$APP_DIR"

# ── 3. Sync nav-data files to publish dir ────────────────────────────────────

NAVDATA_SRC="$REPO_DIR/flightLegDataArinc"
NAVDATA_DST="$APP_DIR/flightLegDataArinc"
mkdir -p "$NAVDATA_DST"

# Sync the ARINC 424 terminal-area data
if [[ -d "$NAVDATA_SRC/arinc_eh" ]]; then
    cp -r "$NAVDATA_SRC/arinc_eh" "$NAVDATA_DST/"
fi

# Symlink the Navigraph SQLite (134 MB — avoid copying on every deploy)
NAVDB_SRC="$NAVDATA_SRC/little_navmap_navigraph.sqlite"
NAVDB_DST="$NAVDATA_DST/little_navmap_navigraph.sqlite"
if [[ -f "$NAVDB_SRC" ]]; then
    ln -sf "$NAVDB_SRC" "$NAVDB_DST"
    echo "   Navigraph SQLite linked: $NAVDB_DST → $NAVDB_SRC"
else
    echo ""
    echo "⚠️  Navigraph SQLite NOT FOUND at: $NAVDB_SRC"
    echo "   All flights will fall back to direct paths (no airway snapping)."
    echo "   Copy it from your local machine with:"
    echo "   scp /path/to/little_navmap_navigraph.sqlite ec2-user@<HOST>:$NAVDB_SRC"
fi

# ── 4. Config sanity check ────────────────────────────────────────────────────

if [[ ! -f "$CONFIG" ]]; then
    echo ""
    echo "⚠️  appsettings.json not found at $CONFIG"
    echo "   The service may fail to start. Run ./ec2-setup.sh to configure."
elif grep -q 'your_client_id\|YOUR_' "$CONFIG" 2>/dev/null; then
    echo ""
    echo "⚠️  appsettings.json still contains placeholder values."
    echo "   Run ./ec2-setup.sh to set up your credentials."
fi

if [[ -f "$CONFIG" ]] && ! grep -q '"Sse"' "$CONFIG" 2>/dev/null; then
    echo ""
    echo "⚠️  appsettings.json is missing the \"Sse\" section — SSE /flight-tracker/events won't auth correctly."
    echo "   Add this to $CONFIG and restart:"
    echo '   "Sse": { "Enabled": true, "BearerToken": "your-secret-token" }'
fi

# ── 5. Update and restart service ────────────────────────────────────────────

step "Updating and restarting flighttracker..."
sudo cp "$REPO_DIR/flighttracker.service" /etc/systemd/system/flighttracker.service
sudo systemctl daemon-reload
sudo systemctl reset-failed "$SERVICE" 2>/dev/null || true
sudo systemctl restart "$SERVICE"

# ── 6. Verify ─────────────────────────────────────────────────────────────────

sleep 2
echo ""
sudo systemctl status "$SERVICE" --no-pager

echo ""
echo "==> Done. Tail the logs with:"
echo "    journalctl -u $SERVICE -f"
