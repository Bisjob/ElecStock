#!/bin/sh
# Container entry point (see ElecStock.Server.csproj). Starts as root like the postgres image: a host folder
# bind-mounted on /app/AppData is created by Docker as root (mode 755), so it is given to the image's "app" user
# (uid/gid 1654) before the server runs as that user.
set -e

if [ "$(id -u)" = "0" ]; then
    mkdir -p /app/AppData
    chown "$APP_UID:$APP_UID" /app/AppData
    # setpriv keeps the environment (connection string…) but not HOME, where ASP.NET keeps its data protection keys
    export HOME=/home/app
    exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups dotnet ElecStock.Server.dll "$@"
fi

# Already started as a non-root user (compose "user:"): the folder must be writable by it.
exec dotnet ElecStock.Server.dll "$@"
