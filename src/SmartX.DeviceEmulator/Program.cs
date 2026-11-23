using SmartX.DeviceEmulator;

// Smart-X virtual device fleet.
//
//   dotnet run --project src/SmartX.DeviceEmulator
//   dotnet run --project src/SmartX.DeviceEmulator -- --api http://localhost:5080 --interval 5
//
// Every reading this produces reaches the gateway as an HTTP request to the same
// uplink a physical ESP32 would use, and every command is collected from the
// downlink and acknowledged - nothing is written into the gateway from the inside.

var options = EmulatorOptions.Parse(args);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var http = new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
var emulator = new Emulator(new GatewayClient(http), options);

try
{
    await emulator.RunAsync(cancellation.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C or Q.
}

Console.WriteLine("Device emulator stopped.");
return 0;

/// <summary>Command-line settings.</summary>
internal sealed record EmulatorOptions(
    string ApiBaseUrl,
    TimeSpan Interval,
    bool Interactive,
    TimeSpan? IncidentEvery,
    string GatewayId)
{
    public static EmulatorOptions Parse(string[] args)
    {
        var api = Environment.GetEnvironmentVariable("SMARTX_API") ?? "http://localhost:5080";
        var interval = 5d;
        var interactive = !Console.IsInputRedirected;
        double? incidents = null;
        var gatewayId = "border-router-01";

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");

            switch (args[i])
            {
                case "--api":
                    api = Next();
                    break;
                case "--interval":
                    interval = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--no-keys":
                    interactive = false;
                    break;
                case "--incidents":
                    incidents = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--gateway-id":
                    gatewayId = Next();
                    break;
                case "-h" or "--help":
                    Console.WriteLine(Emulator.Usage);
                    Environment.Exit(0);
                    break;
            }
        }

        return new EmulatorOptions(
            api,
            TimeSpan.FromSeconds(Math.Clamp(interval, 1d, 60d)),
            interactive,
            incidents is > 0 ? TimeSpan.FromMinutes(incidents.Value) : null,
            gatewayId);
    }
}
