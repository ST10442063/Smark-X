using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;
using SmartX.Contracts.Stream;

namespace SmartX.Client.Services;

/// <summary>How the console's live link to the gateway is doing.</summary>
public enum StreamLinkState
{
    /// <summary>Not started yet, or starting.</summary>
    Connecting,

    /// <summary>Connected; pushes are arriving.</summary>
    Live,

    /// <summary>The link dropped and is being re-established.</summary>
    Reconnecting,

    /// <summary>The gateway cannot be reached; retrying in the background.</summary>
    Offline
}

/// <summary>
/// The console's one SignalR connection to the gateway's stream hub.
/// <para>
/// <b>A singleton, deliberately.</b> In Blazor WebAssembly a singleton lives for
/// the browser tab, while a page is torn down every time the operator navigates
/// away. Holding the connection here means leaving the Command Stream for the
/// Sensors page and coming back neither drops the link nor misses a critical
/// alert pushed in between - the layout shows those wherever the operator is.
/// </para>
/// <para>
/// WebSockets are used directly, skipping the negotiate request: the console and
/// the gateway are different origins, and a WebSocket upgrade needs no CORS
/// preflight. Reconnection backs off and then keeps trying every ten seconds for
/// as long as the tab is open.
/// </para>
/// </summary>
public sealed class StreamConnection : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly ILogger<StreamConnection> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _starting;

    /// <summary>Builds the connection; nothing is sent until <see cref="StartAsync"/>.</summary>
    public StreamConnection(string apiBaseUrl, ILogger<StreamConnection> logger)
    {
        _logger = logger;

        _hub = new HubConnectionBuilder()
            .WithUrl(apiBaseUrl.TrimEnd('/') + StreamHubContract.Path, options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _hub.On<CriticalAlertResponse>(StreamHubContract.CriticalAlert, alert =>
        {
            UnseenCritical++;
            Raise(CriticalAlert, alert);
        });
        _hub.On<List<ProcessedPacketResponse>>(StreamHubContract.PacketsProcessed, packets => Raise(PacketsProcessed, packets));
        _hub.On<QueueStatsResponse>(StreamHubContract.QueueStats, stats =>
        {
            LatestQueue = stats;
            Raise(QueueStats, stats);
        });
        _hub.On<List<DeviceLiveResponse>>(StreamHubContract.DevicesChanged, devices => Raise(DevicesChanged, devices));
        _hub.On<List<StreamEventResponse>>(StreamHubContract.EventsLogged, events => Raise(EventsLogged, events));
        _hub.On<CommandHistoryResponse>(StreamHubContract.CommandsChanged, history => Raise(CommandsChanged, history));
        _hub.On<InsightsResponse>(StreamHubContract.SuggestionsChanged, insights => Raise(SuggestionsChanged, insights));

        _hub.Reconnecting += _ =>
        {
            SetState(StreamLinkState.Reconnecting);
            return Task.CompletedTask;
        };

        _hub.Reconnected += _ =>
        {
            SetState(StreamLinkState.Live);
            Reconnected?.Invoke();
            return Task.CompletedTask;
        };

        _hub.Closed += error =>
        {
            // Automatic reconnect has given up, or the gateway closed the link: go
            // back to the slow retry loop rather than staying dark.
            _logger.LogDebug(error, "Stream hub connection closed.");
            SetState(StreamLinkState.Offline);
            _starting = null;
            _ = StartAsync();
            return Task.CompletedTask;
        };
    }

    /// <summary>Current link state.</summary>
    public StreamLinkState State { get; private set; } = StreamLinkState.Connecting;

    /// <summary>The most recent queue statistics, for the layout's indicator.</summary>
    public QueueStatsResponse? LatestQueue { get; private set; }

    /// <summary>Critical alerts pushed since the operator last looked at the Command Stream.</summary>
    public int UnseenCritical { get; private set; }

    public event Action? StateChanged;

    /// <summary>Raised after an automatic reconnect: anything pushed during the gap was missed, so reload.</summary>
    public event Action? Reconnected;

    public event Action<CriticalAlertResponse>? CriticalAlert;

    public event Action<List<ProcessedPacketResponse>>? PacketsProcessed;

    public event Action<QueueStatsResponse>? QueueStats;

    public event Action<List<DeviceLiveResponse>>? DevicesChanged;

    public event Action<List<StreamEventResponse>>? EventsLogged;

    public event Action<CommandHistoryResponse>? CommandsChanged;

    public event Action<InsightsResponse>? SuggestionsChanged;

    /// <summary>Marks the critical alerts as seen.</summary>
    public void ClearUnseen()
    {
        if (UnseenCritical == 0)
        {
            return;
        }

        UnseenCritical = 0;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Opens the connection, retrying every few seconds while the gateway is down.
    /// Safe to call repeatedly: every caller shares the one attempt in progress.
    /// </summary>
    public Task StartAsync() => _starting ??= ConnectAsync(_lifetime.Token);

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(StreamLinkState.Connecting);
                await _hub.StartAsync(cancellationToken);
                SetState(StreamLinkState.Live);
                Reconnected?.Invoke();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Stream hub not reachable yet.");
                SetState(StreamLinkState.Offline);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private void SetState(StreamLinkState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke();
    }

    private void Raise<T>(Action<T>? handler, T payload)
    {
        try
        {
            handler?.Invoke(payload);
        }
        catch (Exception error)
        {
            // A broken subscriber must not take the connection's receive loop down.
            _logger.LogWarning(error, "A stream subscriber failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _hub.DisposeAsync();
        _lifetime.Dispose();
    }

    /// <summary>Retries at once, then after 2, 5 and 10 seconds, then every 10 seconds indefinitely.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Steps = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => retryContext.PreviousRetryCount < Steps.Length
                ? Steps[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(10);
    }
}
