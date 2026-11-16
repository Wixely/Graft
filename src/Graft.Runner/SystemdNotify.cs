using System.Net.Sockets;
using System.Text;

namespace Graft.Runner;

/// <summary>
/// Tells systemd about the runner's state over the socket it names in <c>NOTIFY_SOCKET</c>, the same way the forge
/// does. Nothing happens outside Linux or outside a unit that asked for notifications, and a failure to send is
/// ignored: a runner that cannot report readiness is still a runner, and systemd's timeout will say so.
/// </summary>
internal static class SystemdNotify
{
    public static void Send(string message)
    {
        if (!OperatingSystem.IsLinux()) return;
        string? path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path)) return;
        if (path[0] == '@') path = "\0" + path[1..];
        try
        {
            using Socket socket = new(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.SendTo(Encoding.UTF8.GetBytes(message), new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException) { }
    }
}
