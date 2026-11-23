using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;
using SmartX.Contracts.Stream;

namespace SmartX.Client.Services;

/// <summary>
/// The console's live model of the command stream, kept up to date by SignalR
/// whether or not the Command Stream page is open.
/// <para>
/// <b>Why it outlives the page.</b> A Blazor page is disposed the moment the
/// operator navigates away, and everything it held goes with it. If the device
/// grid, the filters, the event feed and the command stacks lived in the page,
/// every visit to the Sensors page and back would reset the filters and show an
/// empty feed until the next round trip. Held here instead, they keep receiving
/// pushes in the background, so returning to the stream is instant and shows
/// what happened while the operator was elsewhere.
/// </para>
/// <para>
/// Devices are kept in a <see cref="Dictionary{TKey,TValue}"/> by MAC address - the
/// same O(1) shape the gateway uses - so a push that changes three devices updates
/// three entries rather than searching the grid.
/// </para>
/// </summary>
public sealed class CommandStreamState : IDisposable
{
    private const int MaxEvents = 80;
    private const int MaxPackets = 40;
    private const int MaxCriticals = 4;

    private readonly SmartXApiClient _api;
    private readonly StreamConnection _stream;
    private CancellationTokenSource? _filterDebounce;
    private Task? _loading;

    /// <summary>Subscribes to the stream.</summary>
    public CommandStreamState(SmartXApiClient api, StreamConnection stream)
    {
        _api = api;
        _stream = stream;

        _stream.DevicesChanged += OnDevicesChanged;
        _stream.PacketsProcessed += OnPacketsProcessed;
        _stream.EventsLogged += OnEventsLogged;
        _stream.CriticalAlert += OnCriticalAlert;
        _stream.QueueStats += OnQueueStats;
        _stream.CommandsChanged += OnCommandsChanged;
        _stream.SuggestionsChanged += OnSuggestionsChanged;
        _stream.Reconnected += OnReconnected;
    }

    /// <summary>Raised whenever anything here changes.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------
    // View state the operator chose - survives navigation.
    // ------------------------------------------------------------------

    /// <summary>The device filter in force.</summary>
    public StreamFilter Filter { get; private set; } = StreamFilter.None;

    /// <summary>The device whose timeline is showing.</summary>
    public string? SelectedMac { get; set; }

    /// <summary>How much timeline to show, in minutes.</summary>
    public int TimelineMinutes { get; set; } = 60;

    /// <summary>The History page's filters.</summary>
    public HistoryFilter History { get; set; } = new();

    // ------------------------------------------------------------------
    // Live model.
    // ------------------------------------------------------------------

    /// <summary>Whether the first snapshot has loaded.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Why the last load failed, if it did.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Every device seen, by MAC.</summary>
    public Dictionary<string, DeviceLiveResponse> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The devices the current filter matched, in the gateway's order.</summary>
    public List<string> Visible { get; private set; } = [];

    /// <summary>How the gateway evaluated the current filter.</summary>
    public DeviceFilterResponse? FilterInfo { get; private set; }

    public QueueStatsResponse? Queue { get; private set; }

    public CommandHistoryResponse Commands { get; private set; } = new();

    public InsightsResponse Insights { get; private set; } = new();

    /// <summary>History entries, newest first.</summary>
    public List<StreamEventResponse> Events { get; } = [];

    /// <summary>Processed packets, newest first.</summary>
    public List<ProcessedPacketResponse> Packets { get; } = [];

    /// <summary>New critical alerts not yet dismissed, newest first.</summary>
    public List<CriticalAlertResponse> Criticals { get; } = [];

    /// <summary>The visible devices, live.</summary>
    public IEnumerable<DeviceLiveResponse> VisibleDevices
        => Visible.Where(Devices.ContainsKey).Select(mac => Devices[mac]);

    /// <summary>Loads the first snapshot, once. Later calls return at once.</summary>
    public Task EnsureLoadedAsync() => Loaded ? Task.CompletedTask : _loading ??= LoadAsync();

    /// <summary>Reloads everything from the gateway.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var result = await _api.GetStreamSnapshotAsync(Filter);

            if (result.Value is not { } snapshot)
            {
                LoadError = result.ErrorMessage;
                return;
            }

            LoadError = null;
            Queue = snapshot.Queue;
            Commands = snapshot.Commands;
            Insights = snapshot.Insights;
            ApplyFilterResult(snapshot.Devices);

            Events.Clear();
            Events.AddRange(snapshot.Events);

            Packets.Clear();
            Packets.AddRange(Enumerable.Reverse(snapshot.RecentPackets));

            Loaded = true;
            Changed?.Invoke();
        }
        finally
        {
            _loading = null;
        }
    }

    /// <summary>Applies a new device filter.</summary>
    public async Task SetFilterAsync(StreamFilter filter)
    {
        if (filter == Filter && Loaded)
        {
            return;
        }

        Filter = filter;
        Changed?.Invoke();
        await RefreshFilterAsync();
    }

    /// <summary>Re-runs the current filter on the gateway.</summary>
    public async Task RefreshFilterAsync()
    {
        var result = await _api.FilterDevicesAsync(Filter);
        if (result.Value is { } devices)
        {
            ApplyFilterResult(devices);
            Changed?.Invoke();
        }
    }

    /// <summary>Tells the engine about a search the operator committed.</summary>
    public async Task RecordSearchAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var result = await _api.RecordActivityAsync(new RecordActivityRequest { Kind = "Search", Text = text });
        if (result.Value is { } insights)
        {
            Insights = insights;
            Changed?.Invoke();
        }
    }

    /// <summary>Tells the engine the operator opened a device.</summary>
    public async Task RecordInspectAsync(string mac)
    {
        var result = await _api.RecordActivityAsync(new RecordActivityRequest { Kind = "Inspect", MacAddress = mac });
        if (result.Value is { } insights)
        {
            Insights = insights;
            Changed?.Invoke();
        }
    }

    /// <summary>Issues a command and folds the resulting stacks in straight away.</summary>
    public async Task<ApiResult<CommandOutcomeResponse>> IssueAsync(IssueCommandRequest request)
        => Absorb(await _api.IssueCommandAsync(request));

    public async Task<ApiResult<CommandOutcomeResponse>> UndoAsync() => Absorb(await _api.UndoCommandAsync());

    public async Task<ApiResult<CommandOutcomeResponse>> RedoAsync() => Absorb(await _api.RedoCommandAsync());

    /// <summary>Accepts a suggestion and folds in its consequences.</summary>
    public async Task<ApiResult<SuggestionAcceptedResponse>> AcceptAsync(string key)
    {
        var result = await _api.AcceptSuggestionAsync(key);
        if (result.Value is { } accepted)
        {
            Insights = accepted.Insights;
            if (accepted.Command is { } command)
            {
                Commands = command.History;
            }

            Changed?.Invoke();
        }

        return result;
    }

    /// <summary>Dismisses a suggestion.</summary>
    public async Task DismissAsync(string key)
    {
        var result = await _api.DismissSuggestionAsync(key);
        if (result.Value is { } insights)
        {
            Insights = insights;
            Changed?.Invoke();
        }
    }

    /// <summary>Clears one pinned critical alert.</summary>
    public void DismissCritical(CriticalAlertResponse alert)
    {
        if (Criticals.Remove(alert))
        {
            Changed?.Invoke();
        }
    }

    private ApiResult<CommandOutcomeResponse> Absorb(ApiResult<CommandOutcomeResponse> result)
    {
        if (result.Value is { } outcome)
        {
            Commands = outcome.History;
            Changed?.Invoke();
        }

        return result;
    }

    private void ApplyFilterResult(DeviceFilterResponse result)
    {
        FilterInfo = result;
        Visible = [.. result.Devices.Select(device => device.MacAddress)];

        foreach (var device in result.Devices)
        {
            Devices[device.MacAddress] = device;
        }
    }

    private void OnDevicesChanged(List<DeviceLiveResponse> changed)
    {
        var membershipMayChange = false;

        foreach (var device in changed)
        {
            if (!Devices.TryGetValue(device.MacAddress, out var previous)
                || previous.AlertState != device.AlertState
                || previous.Flagged != device.Flagged)
            {
                // A device that changed alert state or gained a flag can move in the
                // grid or in or out of the filter - worth asking the gateway again.
                membershipMayChange = true;
            }

            Devices[device.MacAddress] = device;
        }

        if (membershipMayChange)
        {
            ScheduleFilterRefresh();
        }

        Changed?.Invoke();
    }

    private void OnPacketsProcessed(List<ProcessedPacketResponse> packets)
    {
        // A batch arrives oldest first; the feed is newest first.
        for (var i = 0; i < packets.Count; i++)
        {
            Packets.Insert(0, packets[i]);
        }

        Trim(Packets, MaxPackets);
        Changed?.Invoke();
    }

    private void OnEventsLogged(List<StreamEventResponse> events)
    {
        foreach (var entry in events)
        {
            Events.Insert(0, entry);
        }

        Trim(Events, MaxEvents);
        Changed?.Invoke();
    }

    private void OnCriticalAlert(CriticalAlertResponse alert)
    {
        Criticals.Insert(0, alert);
        Trim(Criticals, MaxCriticals);
        Changed?.Invoke();
    }

    private void OnQueueStats(QueueStatsResponse stats)
    {
        Queue = stats;
        Changed?.Invoke();
    }

    private void OnCommandsChanged(CommandHistoryResponse history)
    {
        Commands = history;
        Changed?.Invoke();
    }

    private void OnSuggestionsChanged(InsightsResponse insights)
    {
        Insights = insights;
        Changed?.Invoke();
    }

    private void OnReconnected()
    {
        // Pushes sent while the link was down were missed; a snapshot fills the gap.
        if (Loaded)
        {
            _ = LoadAsync();
        }
    }

    private void ScheduleFilterRefresh()
    {
        _filterDebounce?.Cancel();
        _filterDebounce = new CancellationTokenSource();
        var token = _filterDebounce.Token;

        _ = Task.Delay(300, token).ContinueWith(
            async _ => await RefreshFilterAsync(),
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private static void Trim<T>(List<T> list, int max)
    {
        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }
    }

    public void Dispose()
    {
        _stream.DevicesChanged -= OnDevicesChanged;
        _stream.PacketsProcessed -= OnPacketsProcessed;
        _stream.EventsLogged -= OnEventsLogged;
        _stream.CriticalAlert -= OnCriticalAlert;
        _stream.QueueStats -= OnQueueStats;
        _stream.CommandsChanged -= OnCommandsChanged;
        _stream.SuggestionsChanged -= OnSuggestionsChanged;
        _stream.Reconnected -= OnReconnected;
        _filterDebounce?.Cancel();
    }
}

/// <summary>The History page's filters.</summary>
/// <param name="Kind">One event kind, or null for all.</param>
/// <param name="Level">Info, Warning or Critical, or null for all.</param>
/// <param name="Mac">One device, or null for all.</param>
/// <param name="WindowMinutes">How far back, or 0 for everything held.</param>
public sealed record HistoryFilter(string? Kind = null, string? Level = null, string? Mac = null, int WindowMinutes = 60);
