﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

        private static string ServerID;
        private static SocketIO SocketIOClient;
        private static Dictionary<string, Socket> socksConnections = new Dictionary<string, Socket>();
        private static Dictionary<string, Queue<byte[]>> upstreamBuffer = new Dictionary<string, Queue<byte[]>>();


        public static void Main(string[] args)
        {
            SocketIOClient = new SocketIO(args[0]);
            SocketIOClient.On("socks", response =>
            {
                SetServerID(response.GetValue<string>());
            });
            SocketIOClient.On("socks_connect", response =>
            {
                SocksConnect(response.GetValue<string>());
            });
            SocketIOClient.On("socks_upstream", response =>
            {
                SocksUpStream(response.GetValue<string>());
            });
            SocketIOClient.ConnectAsync();
            GetSocksRequests();
        }

        private static void SetServerID(string serverIDRequest)
        {
            //{"server_id": "SIQcwzTByp"}
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(serverIDRequest);
            if (data.TryGetValue("server_id", out string serverIdValue))
            {
                ServerID = serverIdValue;
            }
            else
            {
                Environment.Exit(0);
            }
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
            remote.ReceiveTimeout = 5000;
            int rep;
            try
            {
                remote.Connect(request.address, request.port);
                socksConnections.Add(request.client_id, remote);
                upstreamBuffer.Add(request.client_id, new Queue<byte[]>());
                rep = 0;
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
            new Thread(() =>
            {
                Stream(remote, request.client_id);
            }).Start();
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

        private static void GetSocksRequests()
        {
            while (true) {
                Thread.Sleep(100);
                if (ServerID == null) { continue; }
                var data = new
                {
                    server_id = ServerID
                };
                string dataSeralized = JsonSerializer.Serialize(data);
                SocketIOClient.EmitAsync("socks_request_for_data", dataSeralized);
            }
        }
    }
}