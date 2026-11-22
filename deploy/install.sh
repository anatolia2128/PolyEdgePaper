#!/usr/bin/env bash
# Ubuntu sunucuda çalıştır:   bash deploy/install.sh            -> "flow" ve "noflow" örneklerini kurar
#                             bash deploy/install.sh sniper     -> mevcut örneklere dokunmadan "sniper" ekler
#                                                                  (kurulu tüm örnekler yeni kodla yeniden başlatılır)
# Tekrar çalıştırınca: kodu çeker, yeniden derler, servisleri yeniden başlatır (data klasörlerine dokunmaz).
set -euo pipefail

cd "$(dirname "$0")/.."
REPO_DIR="$(pwd)"
RUN_USER="$(id -un)"
APP_DIR="$HOME/polyedge/app"
DATA_ROOT="/var/lib/polyedge"

if [ $# -gt 0 ]; then INSTANCES=("$@"); else INSTANCES=(flow noflow); fi

echo "==> .NET SDK kontrolü"
if ! command -v dotnet >/dev/null 2>&1; then
  sudo apt-get update -y
  sudo apt-get install -y dotnet-sdk-8.0 git
fi
dotnet --version

if [ -d .git ]; then
  echo "==> git pull"
  git pull --ff-only || echo "(git pull atlandı)"
fi

echo "==> Derleniyor"
rm -rf "$HOME/polyedge/app.new"
dotnet publish src/PolyEdge.App -c Release -o "$HOME/polyedge/app.new" --nologo -v q

echo "==> Çalışan servisler durduruluyor"
sudo systemctl stop 'polyedge@*' 2>/dev/null || true
rm -rf "$APP_DIR"
mv "$HOME/polyedge/app.new" "$APP_DIR"

echo "==> Ortam dosyaları ve data klasörleri"
sudo mkdir -p /etc/polyedge "$DATA_ROOT"
sudo chown "$RUN_USER":"$RUN_USER" "$DATA_ROOT"

next_port() {
  local max=5079 f p
  for f in /etc/polyedge/*.env; do
    [ -f "$f" ] || continue
    p=$(grep -E '^ASPNETCORE_URLS=' "$f" | sed -E 's/.*:([0-9]+)$/\1/' || true)
    if [ -n "$p" ] && [ "$p" -gt "$max" ] 2>/dev/null; then max=$p; fi
  done
  echo $((max + 1))
}

for name in "${INSTANCES[@]}"; do
  envfile="/etc/polyedge/$name.env"
  if [ ! -f "$envfile" ]; then
    port=$(next_port)
    {
      echo "ASPNETCORE_URLS=http://127.0.0.1:$port"
      echo "Bot__DataDirectory=$DATA_ROOT/$name"
      echo "Bot__StartingBalanceUsd=1000"
      [ "$name" = "noflow" ] && echo "Strategy__FlowWeight=0"
      [ "$name" = "sniper" ] && echo "Strategy__Mode=Sniper"
      [ "$name" = "skew" ] && echo "Strategy__Mode=Skew"
      if [ "$name" = "sniperv2" ]; then
        echo "Strategy__Mode=Sniper"
        echo "Strategy__SniperRequireConfirm=true"
        echo "Strategy__SniperUseEv=true"
        echo "Bot__RecordBooks=true"
        echo "Feeds__BookPollMs=500"
      fi
    } | sudo tee "$envfile" >/dev/null
    echo "   $envfile oluşturuldu (port $port)"
  else
    echo "   $envfile zaten var, dokunulmadı"
  fi
  mkdir -p "$DATA_ROOT/$name"
done

# Yeni binary'yi kullanması için kurulu TÜM örnekler yeniden başlatılır (yalnızca argümandakiler değil).
ALL=()
for f in /etc/polyedge/*.env; do ALL+=("$(basename "$f" .env)"); done

echo "==> systemd servisi"
sudo tee /etc/systemd/system/polyedge@.service >/dev/null <<UNIT
[Unit]
Description=PolyEdge Paper Bot (%i)
After=network-online.target
Wants=network-online.target

[Service]
User=$RUN_USER
WorkingDirectory=$APP_DIR
EnvironmentFile=/etc/polyedge/%i.env
ExecStart=$(command -v dotnet) $APP_DIR/PolyEdge.App.dll
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
for name in "${ALL[@]}"; do
  sudo systemctl enable "polyedge@$name" >/dev/null 2>&1
  sudo systemctl restart "polyedge@$name"
done

sleep 4
echo
for name in "${ALL[@]}"; do
  printf "polyedge@%-8s : " "$name"; systemctl is-active "polyedge@$name" || true
done
cat <<MSG

Log izlemek için:  sudo journalctl -u polyedge@${ALL[0]} -f
Veri klasörleri :  $DATA_ROOT/<ornek>/   (state.json, trades.csv, features.csv, windows.csv)
Panel (SSH tüneli ile): ssh -L 5080:localhost:5080 -L 5081:localhost:5081 ubuntu@SUNUCU-IP
MSG
