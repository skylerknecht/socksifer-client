using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Remoting.Contexts;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient;

namespace SocksiferClient
{
    public class Program
    {

        private static SocketIO SocketIOClient;
        private static Dictionary<string, Socket> socksConnections = new Dictionary<string, Socket>();
        private static Dictionary<string, Queue<byte[]>> upstreamBuffer = new Dictionary<string, Queue<byte[]>>();


        public static void Main(string[] args)
        {
            try
            {
                SocketIOClient = new SocketIO(args[0]);         
            }
            catch (Exception ex)
            {
                Console.WriteLine("Example: SocksiferClient.exe http://127.0.0.1:1337/");
                return;
            }

            SocketIOClient.On("ping", response =>
            {
                Ping(response.GetValue<double>());
            });

            SocketIOClient.On("socks_connect", response =>
            {
                new Thread(() => SocksConnect(response.GetValue<string>())).Start();
            });

            SocketIOClient.On("socks_upstream", response =>
            {
                SocksUpStream(response.GetValue<string>());
            });


            SocketIOClient.ConnectAsync();

            while (true)
            {
                Thread.Sleep(1000);
            }
        }

        private static void Ping(double data)
        {
            SocketIOClient.EmitAsync("pong", data);
        }


        private class SocksConnectRequest
        {
            [JsonPropertyName("atype")]
            public int atype { get; set; }

            [JsonPropertyName("address")]
            public string address { get; set; }

            [JsonPropertyName("port")]
            public int port { get; set; }

            [JsonPropertyName("client_id")]
            public string client_id { get; set; }
        }

        private class SocksConnectResults
        {
            [JsonPropertyName("atype")]
            public int atype { get; set; }

            [JsonPropertyName("rep")]
            public int rep { get; set; }

            [JsonPropertyName("bind_addr")]
            public string bind_addr { get; set; }

            [JsonPropertyName("bind_port")]
            public string bind_port { get; set; }

            [JsonPropertyName("client_id")]
            public string client_id { get; set; }
        }

        private static void SocksConnect(string socksConnectRequest)
        {
            //{"atype": 1, "address": "127.0.0.1", "port": 80, "client_id": "SIQcwzTByp"}
            var request = JsonSerializer.Deserialize<SocksConnectRequest>(socksConnectRequest);
            Socket remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            remote.ReceiveTimeout = 100;
            int rep;
            try
            {
                if (request.atype == 1)
                {
                    remote.Connect(request.address, request.port);
                    socksConnections.Add(request.client_id, remote);
                    upstreamBuffer.Add(request.client_id, new Queue<byte[]>());
                    rep = 0;
                }
                else if (request.atype == 3)
                {
                    // IPHostEntry hostEntry = Dns.GetHostEntry(request.address);
                    // var ipAddress = hostEntry.AddressList[0];
                    remote.Connect(request.address, request.port);
                    socksConnections.Add(request.client_id, remote);
                    upstreamBuffer.Add(request.client_id, new Queue<byte[]>());
                    rep = 0;
                }
                else if (request.atype == 4)
                {
                    IPAddress ipAddress = IPAddress.Parse(request.address);
                    remote.Connect(ipAddress, request.port);
                    socksConnections.Add(request.client_id, remote);
                    upstreamBuffer.Add(request.client_id, new Queue<byte[]>());
                    rep = 0;
                }
                else
                {
                    rep = 8;
                }

            }
            catch (SocketException e)
            {
                switch (e.ErrorCode)
                {
                    case (int)SocketError.AccessDenied:
                        rep = 2;
                        break;
                    case (int)SocketError.NetworkUnreachable:
                        rep = 3;
                        break;
                    case (int)SocketError.HostUnreachable:
                        rep = 4;
                        break;
                    case (int)SocketError.ConnectionRefused:
                        rep = 5;
                        break;
                    default:
                        rep = 6;
                        break;
                }
            }

            string bindAddr = (rep != 0) ? null : ((IPEndPoint)remote.LocalEndPoint).Address.ToString();
            string bindPort = (rep != 0) ? null : ((IPEndPoint)remote.LocalEndPoint).Port.ToString();


            var response = JsonSerializer.Serialize(new SocksConnectResults
            {
                atype = request.atype,
                rep = rep,
                bind_addr = bindAddr,
                bind_port = bindPort,
                client_id = request.client_id
            });

            SocketIOClient.EmitAsync("socks_connect_results", response);

            if (rep == 0)
            {
                new Thread(() =>
                {
                    Stream(remote, request.client_id);
                }).Start();
            }
        }

        private class DownstreamResults
        {

            [JsonPropertyName("data")]
            public string data { get; set; }

            [JsonPropertyName("client_id")]
            public string client_id { get; set; }
        }

        private static void Stream(Socket remote, string client_id)
        {
            while (true)
            {
                if (remote.Poll(0, SelectMode.SelectWrite) && upstreamBuffer[client_id].Count > 0)
                {
                    byte[] data = upstreamBuffer[client_id].Dequeue();
                    remote.Send(data);
                }
                if (remote.Poll(0, SelectMode.SelectRead))
                {
                    try
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead = remote.Receive(buffer);

                        if (bytesRead <= 0)
                        {
                            break;
                        }

                        byte[] downstream_data = new byte[bytesRead];
                        Array.Copy(buffer, downstream_data, bytesRead);

                        string socks_downstream_result = JsonSerializer.Serialize(new DownstreamResults
                        {
                            data = Convert.ToBase64String(downstream_data),
                            client_id = client_id,
                        });

                        SocketIOClient.EmitAsync("socks_downstream_results", socks_downstream_result);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private class UpStreamRequest
        {

            [JsonPropertyName("data")]
            public string data { get; set; }

            [JsonPropertyName("client_id")]
            public string client_id { get; set; }
        }

        private static void SocksUpStream(string upStreamRequest)
        {
            UpStreamRequest socksUpstreamRequest = JsonSerializer.Deserialize<UpStreamRequest>(upStreamRequest);
            upstreamBuffer[socksUpstreamRequest.client_id].Enqueue(Convert.FromBase64String(socksUpstreamRequest.data));
        }
    }
}