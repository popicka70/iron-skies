# Dedicated server

## Build

```bash
./tools/export_builds.sh
```

Outputs:

```
build/linux/iron-skies.x86_64              client (launcher)
build/windows/iron-skies.exe               client (launcher)
build/server/iron-skies-server.x86_64      dedicated server
```

The server package needs the **.NET 10 runtime** installed; the Docker image below
includes it.

## Run the exported server

```bash
cd build/server
./iron-skies-server.x86_64 --headless -- --server --port 7777
```

User arguments go after `--` (`OS.GetCmdlineUserArgs`); the launcher dispatches to the
multiplayer server scene and never shows the menu. The server listens on **UDP 7777**.

Clients connect with the launcher's *Join Server* screen or:

```bash
godot --path game scenes/Multiplayer.tscn -- --connect <server-ip> --port 7777
```

## Docker

```bash
docker build -t iron-skies-server .
docker run -d --name iron-skies -p 7777:7777/udp iron-skies-server
docker logs -f iron-skies
```

The image is based on `mcr.microsoft.com/dotnet/runtime:10.0`, includes the server
package and runs the launcher in `--server` mode. Only UDP 7777 is exposed.

`docker logs` can lag because Godot's stdout is block-buffered when not attached to a
TTY; the server itself is unaffected.

## Host & Play

The launcher's **Host & Play** starts `iron-skies-server.x86_64 --headless -- --server
--hosted --port <port>` as a subprocess and connects to it. The `--hosted` flag makes the
server exit after 8 seconds without players. The manager kills the process when the game
quits.

## Configuration

- Port: `--port <n>` (default 7777).
- Player cap: 64.
- Spawns: four named positions plus a circle for additional players.
- Persistence, server browser, accounts and moderation are not implemented yet.
