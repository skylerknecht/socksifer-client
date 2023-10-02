using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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

        public static void Main(string[] args)
        {
            try
            {
                SocketIOClient = new SocketIO(args[0], new SocketIOOptions
                {
                    ExtraHeaders = new Dictionary<string, string> { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/117.0" } }
                });         
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
                _ = SocksConnect(response.GetValue<string>());
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

        private static async Task SocksConnect(string socksConnectRequest)
        {
            //{"atype": 1, "address": "127.0.0.1", "port": 80, "client_id": "SIQcwzTByp"}
            var request = JsonSerializer.Deserialize<SocksConnectRequest>(socksConnectRequest);
            Socket remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            SocketAsyncEventArgs connectEventArgs = new SocketAsyncEventArgs();
            connectEventArgs.RemoteEndPoint = new DnsEndPoint(request.address, request.port);
            connectEventArgs.Completed += (sender, args) =>
            {
                if (args.SocketError == SocketError.Success)
                {
                    socksConnections.Add(request.client_id, remote);

                    string bindAddr = ((IPEndPoint)remote.LocalEndPoint).Address.ToString();
                    string bindPort = ((IPEndPoint)remote.LocalEndPoint).Port.ToString();


                    var response = JsonSerializer.Serialize(new SocksConnectResults
                    {
                        atype = request.atype,
                        rep = 0,
                        bind_addr = bindAddr,
                        bind_port = bindPort,
                        client_id = request.client_id
                    });

                    SocketIOClient.EmitAsync("socks_connect_results", response);
                    Stream(remote, request.client_id);
                }
                else
                {
                    var response = JsonSerializer.Serialize(new SocksConnectResults
                    {
                        atype = request.atype,
                        rep = 1,
                        bind_addr = null,
                        bind_port = null,
                        client_id = request.client_id
                    });
                    SocketIOClient.EmitAsync("socks_connect_results", response);
                }
            };
            remote.ConnectAsync(connectEventArgs);
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

            byte[] downstream_data = new byte[4096];
            SocketAsyncEventArgs e = new SocketAsyncEventArgs();
            e.SetBuffer(downstream_data, 0, downstream_data.Length);
            e.Completed += (sender2, args2) =>
            {
                if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
                {
                    string socks_downstream_result = JsonSerializer.Serialize(new DownstreamResults
                    {
                        data = Convert.ToBase64String(e.Buffer, 0, e.BytesTransferred),
                        client_id = client_id,
                    });

                    SocketIOClient.EmitAsync("socks_downstream_results", socks_downstream_result);
                    remote.ReceiveAsync(e);
                }
            };
            remote.ReceiveAsync(e);
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
            try
            {
                UpStreamRequest socksUpstreamRequest = JsonSerializer.Deserialize<UpStreamRequest>(upStreamRequest);
                socksConnections[socksUpstreamRequest.client_id].Send(Convert.FromBase64String(socksUpstreamRequest.data));
            } catch (Exception e) { 
                Console.WriteLine(e.ToString());
            }
        }
    }
}