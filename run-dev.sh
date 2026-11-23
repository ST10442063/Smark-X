#!/usr/bin/env bash
#
# Starts the whole Smart-X stack for development on macOS or Linux: the gateway
# API, the virtual device fleet and the Blazor console, with their logs in ./.logs.
#
#   ./run-dev.sh
#
# Ctrl+C stops all three. The emulator runs without hotkeys here; for the S/M/D/F
# keys, stop it and run it in its own terminal instead:
#   dotnet run --project src/SmartX.DeviceEmulator

set -euo pipefail
cd "$(dirname "$0")"
mkdir -p .logs

echo "Building the solution..."
dotnet build SmartX.sln --nologo -v q

pids=()
cleanup() {
    echo
    echo "Stopping..."
    for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
}
trap cleanup EXIT INT TERM

echo "Starting the gateway API on http://localhost:5080 (log: .logs/api.log)"
dotnet run --project src/SmartX.Api --no-build > .logs/api.log 2>&1 &
pids+=($!)

# The emulator provisions itself from the gateway, so wait until the API answers.
for _ in $(seq 1 60); do
    if curl -fs http://localhost:5080/api/health > /dev/null; then break; fi
    sleep 1
done
curl -fs http://localhost:5080/api/health > /dev/null || { echo "The gateway did not start - see .logs/api.log"; exit 1; }

echo "Starting the device emulator (log: .logs/emulator.log)"
dotnet run --project src/SmartX.DeviceEmulator --no-build -- --no-keys > .logs/emulator.log 2>&1 &
pids+=($!)

echo "Starting the operator console on http://localhost:5090 (log: .logs/client.log)"
dotnet run --project src/SmartX.Client --no-build > .logs/client.log 2>&1 &
pids+=($!)

echo
echo "Open http://localhost:5090/stream - Ctrl+C here stops everything."
wait
