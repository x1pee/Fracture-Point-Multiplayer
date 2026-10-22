using System.Net.Sockets;
using System.Text.Json;

namespace FractureTogether;

// Fixed English copy and stable support IDs. Never display remote text or exception
// messages: both may contain credentials, addresses or arbitrary markup.
public sealed record ConnectionIssue(string Code,string Title,string Hint,string Phase="",string Versions="");

public sealed class ConnectionException : IOException
{
    public ConnectionIssue Issue {get;}
    public ConnectionException(ConnectionIssue issue):base(issue.Code+": "+issue.Title){Issue=issue;}
}

public static class ConnectionErrors
{
    public static ConnectionIssue Get(string code,string phase="",string versions="")
    {
        var (title,hint)=code switch {
            "MP-001" => ("Different mod versions","Install the same mod archive on every computer, then restart the game."),
            "MP-002" => ("Different game versions","Update the game on every computer to the same supported version."),
            "MP-003" => ("Incorrect lobby password","Check the password with the host. Passwords are case-sensitive."),
            "MP-004" => ("Lobby password required","Enter the host's password, then try again."),
            "MP-005" => ("Lobby is full","This lobby already has four players. Ask the host to free a slot."),
            "MP-006" => ("Lobby is closed","Ask the host to return home and reopen the lobby before joining."),
            "MP-007" => ("Host name not found","Check the host address. For Radmin VPN, use the host's VPN IP."),
            "MP-008" => ("Connection refused","Check the IP and port. The host must create a lobby before you join."),
            "MP-009" => ("Host did not respond","Check that the host is online, your VPN is connected and the firewall allows the game."),
            "MP-010" => ("Network is unreachable","Check your network or VPN connection, then try again."),
            "MP-011" => ("Connection was closed","The host or network closed the connection. Check with the host, then reconnect at home."),
            "MP-012" => ("Lobby handshake timed out","The connection opened, but the host did not finish joining in time. Retry when the host has finished loading."),
            "MP-013" => ("Incompatible server response","Check the IP and port and make sure everyone uses the same Fracture Point Multiplayer archive."),
            "MP-014" => ("Invalid host address or port","Enter an IP address or host name without a URL, and use a port from 1024 to 65535."),
            "MP-015" => ("Port is already in use","Close another lobby using this port, or choose a different port in the mod configuration."),
            "MP-016" => ("Network access was denied","Check that your firewall or security software allows the game to use the network."),
            "MP-017" => ("Network queue overloaded","Too many reliable messages are waiting. Reconnect; if it repeats, collect the multiplayer logs."),
            "MP-018" => ("Invalid network data","The peer sent an invalid message. Use the same mod archive; collect logs if it repeats."),
            "MP-020" => ("Return home before connecting","Start or reconnect from your home before entering a shared run."),
            "MP-021" => ("Invalid lobby password length","Use 1-64 characters for a protected lobby. Leave it empty when joining an open lobby."),
            "MP-022" => ("Personal save storage unavailable","The session could not protect your progress. Collect the multiplayer logs before retrying."),
            "MP-023" => ("Connection stopped responding","Network traffic could not be exchanged in time. Check the host and VPN; collect logs if it repeats."),
            _ => ("Network error","Try again. If the problem repeats, run Collect-Multiplayer-Logs.cmd and include this error code.")
        };
        if(!Known(code))code="MP-019";
        return new(code,title,hint,phase,versions);
    }
    public static bool Known(string? code)=>code is "MP-001" or "MP-002" or "MP-003" or "MP-004" or "MP-005" or "MP-006" or "MP-007" or "MP-008" or "MP-009" or "MP-010" or "MP-011" or "MP-012" or "MP-013" or "MP-014" or "MP-015" or "MP-016" or "MP-017" or "MP-018" or "MP-019" or "MP-020" or "MP-021" or "MP-022" or "MP-023";
    public static ConnectionIssue? Compatibility(string local,string remote,string phase="handshake")
    {
        if(local==remote)return null;
        var own=local.Split(':');var peer=remote.Split(':');
        if(own.Length<2||peer.Length<2||!peer[0].StartsWith("FT/",StringComparison.Ordinal))return Get("MP-013",phase);
        var code=own[0]!=peer[0]?"MP-001":own[1]!=peer[1]?"MP-002":"MP-001";
        return Get(code,phase,"You: "+Identity(local)+"\nHost: "+Identity(remote));
    }
    static string Identity(string value)
    {
        var parts=value.Split(':');
        string Safe(string text,int limit)=>new(text.Where(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '/' or '.' or '-' or '_').Take(limit).ToArray());
        return Safe(parts[0],16)+(parts.Length>1?" · game "+Safe(parts[1],8):"")+(parts.Length>2?" · mod "+Safe(parts[2],8):"");
    }
    public static ConnectionIssue From(Exception error,string phase)
    {
        for(Exception? cause=error;cause!=null;cause=cause.InnerException){
            if(cause is ConnectionException failure)return failure.Issue;
            if(cause is SocketException socket)return Get(socket.SocketErrorCode switch {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain=>"MP-007",
                SocketError.ConnectionRefused=>"MP-008",
                SocketError.TimedOut=>phase=="connect"?"MP-009":"MP-023",
                SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.HostUnreachable=>"MP-010",
                SocketError.AddressAlreadyInUse=>"MP-015",
                SocketError.AccessDenied=>"MP-016",
                SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown=>"MP-011",
                SocketError.InvalidArgument or SocketError.AddressNotAvailable=>"MP-014",
                _=>"MP-019"
            },phase);
        }
        if(error is TimeoutException or OperationCanceledException)return Get(phase=="connect"?"MP-009":phase=="handshake"?"MP-012":"MP-023",phase);
        if(error is ArgumentException or FormatException)return Get("MP-014",phase);
        if(error is EndOfStreamException or ObjectDisposedException)return Get("MP-011",phase);
        if(error is JsonException)return Get("MP-018",phase);
        if(error is IOException)return Get("MP-018",phase);
        return Get("MP-019",phase);
    }
}
