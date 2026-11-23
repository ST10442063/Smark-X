# Build and publish the ingestion gateway.
#
# Built from the repository root so project references resolve:
#   docker build -f src/SmartX.Api/Dockerfile .
# Compose does that for you.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files are copied before the source so the restore layer is cached and
# only re-runs when a dependency actually changes, not on every code edit.
COPY Directory.Build.props ./
COPY SmartX.sln ./
COPY src/SmartX.Core/SmartX.Core.csproj src/SmartX.Core/
COPY src/SmartX.Contracts/SmartX.Contracts.csproj src/SmartX.Contracts/
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj

COPY src/SmartX.Core/ src/SmartX.Core/
COPY src/SmartX.Contracts/ src/SmartX.Contracts/
COPY src/SmartX.Api/ src/SmartX.Api/
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj -c Release -o /app --no-restore

# The runtime image carries no SDK, which keeps it a fraction of the build image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The .NET 10 runtime image ships no HTTP client at all - no wget, no curl - so the
# compose health check had nothing to call and the gateway never reported healthy,
# which in turn stopped the emulator and console from starting. curl is installed
# for that probe alone, as root, before the switch to the app user.
RUN apt-get update     && apt-get install -y --no-install-recommends curl     && rm -rf /var/lib/apt/lists/*

# The upload and key directories are created here, as root, and handed to the app
# user BEFORE the USER switch. This matters: a named volume mounted at a path that
# does not exist in the image is created root-owned, and the non-root process
# would then fail its first upload. Creating them in the image means the volumes
# inherit this ownership instead.
RUN mkdir -p /app/App_Data/attachments /app/App_Data/keys && chown -R app:app /app

# Runs as a non-root user. The base image provides "app" for this.
USER app

COPY --from=build --chown=app:app /app ./

# Encrypted attachment bytes are written here. Mounted as a volume in compose so
# uploads survive a container restart.
ENV Gateway__UploadRoot=/app/App_Data/attachments

# The AES-256 key is generated here on first boot unless Gateway__AttachmentEncryptionKey
# is supplied. It is a separate volume from the attachments on purpose: copying the
# ciphertext volume alone must not also hand over the key that opens it.
ENV Gateway__EncryptionKeyPath=/app/App_Data/keys/attachments.key

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
