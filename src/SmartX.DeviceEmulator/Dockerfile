# Build and run the virtual device fleet.
#
# Built from the repository root so the Contracts project reference resolves:
#   docker build -f src/SmartX.DeviceEmulator/Dockerfile .
# Compose does that for you.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY SmartX.sln ./
COPY src/SmartX.Contracts/SmartX.Contracts.csproj src/SmartX.Contracts/
COPY src/SmartX.DeviceEmulator/SmartX.DeviceEmulator.csproj src/SmartX.DeviceEmulator/
RUN dotnet restore src/SmartX.DeviceEmulator/SmartX.DeviceEmulator.csproj

COPY src/SmartX.Contracts/ src/SmartX.Contracts/
COPY src/SmartX.DeviceEmulator/ src/SmartX.DeviceEmulator/
RUN dotnet publish src/SmartX.DeviceEmulator/SmartX.DeviceEmulator.csproj -c Release -o /app --no-restore

# A console app needs only the .NET runtime, not ASP.NET.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
USER app
COPY --from=build --chown=app:app /app ./

# No keyboard in a container, so incidents are scheduled instead: a random power
# spike or moisture crash every few minutes keeps the priority lane exercised.
ENTRYPOINT ["dotnet", "SmartX.DeviceEmulator.dll", "--no-keys"]
CMD ["--api", "http://api:8080", "--incidents", "3"]
