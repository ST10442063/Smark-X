# Build and serve the Blazor WebAssembly operator console.
#
# Built from the repository root so the Contracts project reference resolves:
#   docker build -f src/SmartX.Client/Dockerfile .
#
# A WebAssembly app publishes to plain static files - the .NET runtime runs in the
# visitor's browser, not in this container - so the runtime image is nginx rather
# than an ASP.NET image. It needs no .NET at all.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY SmartX.sln ./
COPY src/SmartX.Contracts/SmartX.Contracts.csproj src/SmartX.Contracts/
COPY src/SmartX.Client/SmartX.Client.csproj src/SmartX.Client/
RUN dotnet restore src/SmartX.Client/SmartX.Client.csproj

COPY src/SmartX.Contracts/ src/SmartX.Contracts/
COPY src/SmartX.Client/ src/SmartX.Client/
RUN dotnet publish src/SmartX.Client/SmartX.Client.csproj -c Release -o /app --no-restore

FROM nginx:1.27-alpine AS runtime

COPY src/SmartX.Client/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/wwwroot /usr/share/nginx/html

EXPOSE 80
