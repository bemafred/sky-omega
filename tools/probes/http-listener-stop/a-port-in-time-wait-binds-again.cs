#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
// Can a port be bound again while a connection the server closed first is in TIME_WAIT on it? (The managed
// HttpListener binds the port again at Close after Stop.)
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, port);
using (var accepted = await listener.AcceptSocketAsync()) accepted.Close();   // the server closes first
await Task.Delay(100);
client.Close();
await Task.Delay(100);
listener.Stop();
Console.WriteLine("states on the port: " + string.Join(", ", IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
    .Where(c => c.LocalEndPoint.Port == port).Select(c => c.State)));
using var again = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
try { again.Bind(new IPEndPoint(IPAddress.Loopback, port)); again.Listen(); Console.WriteLine($"bound again, ReuseAddress {again.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)}"); }
catch (SocketException ex) { Console.WriteLine($"bind failed: {ex.SocketErrorCode}"); }
