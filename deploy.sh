#!/usr/bin/env bash
# Deploy FlightTracker to EC2.
#
# Usage:
#   EC2_HOST=user@your-ec2-ip ./deploy.sh
#
# One-time EC2 prerequisites (run manually once — see flighttracker.service):
#   1. Install .NET SDK: https://learn.microsoft.com/dotnet/core/install/linux
#   2. Repo already cloned at: /home/ec2-user/flight-tracker
#   3. Install service: sudo cp /home/ec2-user/flight-tracker/flighttracker.service /etc/systemd/system/
#                       sudo systemctl daemon-reload && sudo systemctl enable flighttracker
#   4. (Optional) seed DB: scp flight_stats.db user@ec2:/opt/flighttracker/data/
set -euo pipefail

EC2_HOST="${EC2_HOST:?Please set EC2_HOST=user@your-ec2-ip}"
REPO_DIR="/home/ec2-user/flight-tracker"
APP_DIR="/opt/flighttracker/app"
DATA_DIR="/opt/flighttracker/data"

# ── Config setup (runs on first deploy or when placeholders are detected) ─────

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

    # Write JSON to a local temp file then SCP — avoids heredoc quoting issues
    # with secrets that may contain special shell characters.
    tmp=$(mktemp /tmp/flighttracker-XXXXXX.json)
    cat > "$tmp" <<JSON
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
    "ClientSecret": "$opensky_secret"
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
    scp "$tmp" "$EC2_HOST:$APP_DIR/appsettings.json"
    rm "$tmp"
    echo ""
    echo "==> Configuration saved to $EC2_HOST:$APP_DIR/appsettings.json"
}

# ── Main ──────────────────────────────────────────────────────────────────────

echo "==> Ensuring directories exist on EC2..."
ssh "$EC2_HOST" "sudo mkdir -p $APP_DIR $DATA_DIR \
    && sudo chown -R \$(whoami): /opt/flighttracker"

# Config guard: create on first deploy, warn if still has example placeholders
if ! ssh "$EC2_HOST" "test -f $APP_DIR/appsettings.json"; then
    echo "==> appsettings.json not found — running first-time setup."
    setup_config
elif ssh "$EC2_HOST" "grep -q 'your_client_id\|YOUR_' $APP_DIR/appsettings.json 2>/dev/null"; then
    echo "⚠️  appsettings.json still contains placeholder values."
    read -r -p "   Re-run configuration setup? [y/N]: " yn_reconfig
    [[ "$yn_reconfig" =~ ^[Yy]$ ]] && setup_config
fi

echo "==> Pulling latest code and republishing on EC2..."
ssh "$EC2_HOST" "
  set -e
  cd $REPO_DIR
  git pull
  dotnet publish -c Release -o $APP_DIR
  sudo systemctl restart flighttracker
"

echo ""
echo "==> Done."
echo "    Logs: ssh $EC2_HOST 'journalctl -u flighttracker -f'"
