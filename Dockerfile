# Iron Skies dedicated server.
#
# Build the server package first:
#   ./tools/export_builds.sh
# Then:
#   docker build -t iron-skies-server .
#   docker run --rm -p 7777:7777/udp iron-skies-server

FROM mcr.microsoft.com/dotnet/runtime:10.0

WORKDIR /app

COPY build/server/ /app/

EXPOSE 7777/udp

ENTRYPOINT ["./iron-skies-server.x86_64", "--headless", "--", "--server", "--port", "7777"]
