namespace GHPC.CoopFoundation.UI;

internal enum CoopLobbyRole
{
    None,
    Host,
    Client
}

internal enum CoopLobbyStatus
{
    Idle,
    Hosting,
    Joining,
    Connected,
    Error
}

internal sealed class CoopLobbyMenuState
{
    public CoopLobbyRole Role { get; private set; } = CoopLobbyRole.None;

    public CoopLobbyStatus Status { get; private set; } = CoopLobbyStatus.Idle;

    public string EndpointHost { get; private set; } = "127.0.0.1";

    public int EndpointPort { get; private set; } = 27015;

    public bool ReadyLocal { get; private set; }

    public bool ReadyRemote { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public string StatusText { get; private set; } = "Create or join a co-op session.\nBack: ESC";

    public void BeginHost(int port)
    {
        Role = CoopLobbyRole.Host;
        EndpointHost = "0.0.0.0";
        EndpointPort = port;
        Status = CoopLobbyStatus.Hosting;
        LastError = string.Empty;
        StatusText = $"Hosting on UDP {port}...";
    }

    public void BeginJoin(string host, int port)
    {
        Role = CoopLobbyRole.Client;
        EndpointHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        EndpointPort = port <= 0 ? 27015 : port;
        Status = CoopLobbyStatus.Joining;
        LastError = string.Empty;
        StatusText = $"Joining {EndpointHost}:{EndpointPort}...";
    }

    public void MarkConnected(string statusText)
    {
        Status = CoopLobbyStatus.Connected;
        LastError = string.Empty;
        StatusText = statusText;
    }

    public void MarkDisconnected(string reason)
    {
        Status = CoopLobbyStatus.Idle;
        Role = CoopLobbyRole.None;
        ReadyLocal = false;
        ReadyRemote = false;
        LastError = reason ?? string.Empty;
        StatusText = string.IsNullOrWhiteSpace(LastError)
            ? "Create or join a co-op session.\nBack: ESC"
            : $"Disconnected: {LastError}\nBack: ESC";
    }

    public void MarkError(string error)
    {
        Status = CoopLobbyStatus.Error;
        LastError = error ?? "Unknown error";
        StatusText = $"Error: {LastError}\nBack: ESC";
    }

    public void SetReady(bool ready)
    {
        ReadyLocal = ready;
    }
}
