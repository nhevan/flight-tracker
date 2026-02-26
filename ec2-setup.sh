#!/usr/bin/env bash
# ec2-setup.sh — One-time first-time setup for FlightTracker on EC2.
#
# Run this directly on the EC2 instance from the repo directory:
#   cd /home/ec2-user/flight-tracker && ./ec2-setup.sh
#
# What it does:
#   1. Creates /opt/flighttracker/{app,data} with correct ownership
#   2. Pulls the latest code
#   3. Publishes the app
#   4. Prompts for config (appsettings.json) if missing or still has placeholders
#   5. Installs and starts the systemd service
#
# For subsequent updates, use deploy.sh from your local machine:
#   EC2_HOST=user@your-ec2-ip ./deploy.sh
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="/opt/flighttracker/app"
DATA_DIR="/opt/flighttracker/data"
SERVICE_FILE="flighttracker.service"
SERVICE_DEST="/etc/systemd/system/$SERVICE_FILE"

step() { echo ""; echo "==> $*"; }

# ── 1. Directories ────────────────────────────────────────────────────────────

step "Creating directories..."
sudo mkdir -p "$APP_DIR" "$DATA_DIR"
sudo chown -R "$(whoami)": /opt/flighttracker
echo "    $APP_DIR"
echo "    $DATA_DIR"

# ── 2. Pull latest code ───────────────────────────────────────────────────────

step "Pulling latest code..."
cd "$REPO_DIR"
git pull

# ── 3. Publish ────────────────────────────────────────────────────────────────

step "Publishing app..."
dotnet publish -c Release -o "$APP_DIR"

# ── 4. Config ─────────────────────────────────────────────────────────────────

config_path="$APP_DIR/appsettings.json"

setup_config() {
    echo ""
    echo "FlightTracker — configuration setup"
    echo "-------------------------------------"

    read -r  -p "Home latitude  (e.g. 51.9836): " home_lat
    read -r  -p "Home longitude (e.g.  4.6312): " home_lon

    echo ""
    echo "OpenSky credentials (opensky-network.org)"
    read -r  -p "  Client ID:     " opensky_id
    read -r -s -p "  Client Secret: " opensky_secret; echo ""

    echo ""
    echo "Telegram (optional)"
    read -r  -p "  Enable? [y/N]: " yn_telegram
    telegram_enabled="false"; bot_token=""; chat_id=""
    if [[ "$yn_telegram" =~ ^[Yy]$ ]]; then
        telegram_enabled="true"
        read -r  -p "  Bot Token: " bot_token
        read -r  -p "  Chat ID:   " chat_id
    fi

    echo ""
    echo "Anthropic AI facts (optional)"
    read -r  -p "  Enable? [y/N]: " yn_anthropic
    anthropic_enabled="false"; anthropic_key=""
    if [[ "$yn_anthropic" =~ ^[Yy]$ ]]; then
        anthropic_enabled="true"
        read -r -s -p "  API Key: " anthropic_key; echo ""
    fi

    echo ""
    echo "Mapbox live maps (optional)"
    read -r  -p "  Enable? [y/N]: " yn_mapbox
    mapbox_enabled="false"; mapbox_token=""
    if [[ "$yn_mapbox" =~ ^[Yy]$ ]]; then
        mapbox_enabled="true"
        read -r -s -p "  Access Token: " mapbox_token; echo ""
    fi

    cat > "$config_path" <<JSON
{
  "DatabasePath": "$DATA_DIR/flight_stats.db",
  "HomeLocation": {
    "Latitude": $home_lat,
    "Longitude": $home_lon,
    "BoundingBoxDegrees": 1.0,
    "VisualRangeKm": 50
  },
  "Polling": { "IntervalSeconds": 30 },
  "OpenSky": {
    "ClientId": "$opensky_id",
    "ClientSecret": "$opensky_secret",
    "BaseUrl": "https://opensky-network.org/api/",
    "TokenUrl": "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token"
  },
  "Telegram": {
    "Enabled": $telegram_enabled,
    "BotToken": "$bot_token",
    "ChatId": "$chat_id",
    "MaxAltitudeMeters": 3000
  },
  "Anthropic": {
    "Enabled": $anthropic_enabled,
    "ApiKey": "$anthropic_key",
    "Model": "claude-haiku-4-5",
    "MaxTokens": 200
  },
  "Mapbox": {
    "Enabled": $mapbox_enabled,
    "AccessToken": "$mapbox_token",
    "Style": "mapbox/dark-v11"
  }
}
JSON
    echo ""
    echo "==> Config saved to $config_path"
}

if [[ ! -f "$config_path" ]]; then
    step "appsettings.json not found — running config setup..."
    setup_config
elif grep -q 'your_client_id\|YOUR_' "$config_path" 2>/dev/null; then
    echo "⚠️  appsettings.json still contains placeholder values."
    read -r -p "   Re-run configuration setup? [y/N]: " yn_reconfig
    [[ "$yn_reconfig" =~ ^[Yy]$ ]] && setup_config
fi

# ── 5. Install systemd service ────────────────────────────────────────────────

step "Installing systemd service..."
sudo cp "$REPO_DIR/$SERVICE_FILE" "$SERVICE_DEST"
sudo systemctl daemon-reload
sudo systemctl enable flighttracker

# ── 6. Start ──────────────────────────────────────────────────────────────────

step "Starting flighttracker..."
sudo systemctl restart flighttracker

echo ""
echo "==> Done. FlightTracker is running."
echo "    Logs:   journalctl -u flighttracker -f"
echo "    Status: sudo systemctl status flighttracker"
