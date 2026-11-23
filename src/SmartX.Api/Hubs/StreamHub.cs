using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Hubs;

/// <summary>
/// The console's live connection to the command stream.
/// <para>
/// Push only: the server sends, the console listens. Everything the console
/// <i>does</i> - issuing a command, recording a search - goes through the REST API,
/// where it is validated and returns a status code. That keeps a single, testable
/// path for every change, and leaves this hub with nothing to secure but a
/// broadcast. The event names are shared constants in
/// <c>SmartX.Contracts.Stream.StreamHubContract</c>.
/// </para>
/// </summary>
public sealed class StreamHub : Hub;
