using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GrannyCraft
{
    /// <summary>WebSocket client to the Minecraft mod (127.0.0.1:25600). Reconnects forever; never blocks Unity.</summary>
    internal static class Link
    {
        public static readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();
        private static readonly ConcurrentQueue<string> Outgoing = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        /// <summary>Latest camera message: only the newest one matters, so it is never queued.</summary>
        private static string _latestCam;
        public static volatile bool Connected;
        public static int Port = 25600;
        private static Thread _thread;

        public static void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "GrannyCraft link" };
            _thread.Start();
        }

        public static void Send(string json)
        {
            if (!Connected) return;
            Outgoing.Enqueue(json);
            Wake.Set();
        }

        public static void SendCam(string json)
        {
            if (!Connected) return;
            Interlocked.Exchange(ref _latestCam, json);
            Wake.Set();
        }

        private static void Run()
        {
            while (true)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), CancellationToken.None).GetAwaiter().GetResult();
                    while (Outgoing.TryDequeue(out _)) { }
                    Connected = true;
                    Incoming.Enqueue("{\"t\":\"_connected\"}");
                    var recv = Task.Run(() => ReceiveLoop(ws));
                    while (ws.State == WebSocketState.Open && !recv.IsCompleted)
                    {
                        Wake.WaitOne(50);
                        string cam = Interlocked.Exchange(ref _latestCam, null);
                        if (cam != null) SendNow(ws, cam);
                        while (Outgoing.TryDequeue(out var m)) SendNow(ws, m);
                    }
                }
                catch (Exception)
                {
                }
                if (Connected) Incoming.Enqueue("{\"t\":\"_disconnected\"}");
                Connected = false;
                Thread.Sleep(1000);
            }
        }

        private static void SendNow(ClientWebSocket ws, string m)
        {
            var bytes = Encoding.UTF8.GetBytes(m);
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static void ReceiveLoop(ClientWebSocket ws)
        {
            var buf = new byte[1 << 16];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).GetAwaiter().GetResult();
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (r.EndOfMessage)
                {
                    Incoming.Enqueue(sb.ToString());
                    sb.Clear();
                }
            }
        }
    }
}
