#!/usr/bin/env bash
# Publica el game server en /opt/astrion/gs desde lo que haya en `main`.
#   ssh root@servidor 'bash -s' < deploy/deploy.sh
#
# Ojo con el orden: el .csproj compila el protocolo desde el repo hermano
# (../../../mex-orbit-protocol), asi que ese repo tiene que estar clonado al
# lado y actualizado ANTES de publicar. Si no, se compila contra un wire viejo
# y el fallo aparece como mensajes que el cliente no entiende.
set -euo pipefail
RAIZ=/home/astrion/mex-orbit-v1
cd "$RAIZ/mex-orbit-protocol" && git fetch -q origin main && git reset -q --hard origin/main
echo "protocolo: $(git log --oneline -1)"
cd "$RAIZ/mex-orbit-game-server"
git fetch -q origin main && git reset -q --hard origin/main
echo "gs commit: $(git log --oneline -1)"
dotnet publish src/MexOrbit.GameServer/MexOrbit.GameServer.csproj -c Release -o /opt/astrion/gs --nologo -v q
systemctl restart astrion-gs.service
sleep 3
systemctl is-active --quiet astrion-gs.service || { journalctl -u astrion-gs -n 30 --no-pager; exit 1; }
curl -fsS http://127.0.0.1:5210/health && echo && echo "GS OK"
