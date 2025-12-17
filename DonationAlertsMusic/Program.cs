using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class Data
{
    public List<string> PlayerActive = new();
    public List<string> PlayerPassed = new();
}

class Program
{
    static Data data = JsonLoader.LoadJson<Data>("data.json");
    static HttpListener listener;
    static object lockObj = new();

    static List<WebSocket> wsClients = new();

    static async Task Main()
    {
        var daClient = new DonationAlertsClient();
        await daClient.InitializeAsync();

        daClient.OnDonation += async donation =>
        {
            string videoId = AddVideo(donation.Url);
            if (videoId != null)
                await BroadcastState();
        };

        _ = daClient.StartAsync();

        listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:666/");
        listener.Start();

        Console.WriteLine("http://localhost:666/");
        Process.Start(new ProcessStartInfo { FileName = "http://localhost:666/", UseShellExecute = true });

        Console.CancelKeyPress += (_, _) => JsonLoader.SaveJson("data.json", data);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => JsonLoader.SaveJson("data.json", data);

        while (true)
        {
            var ctx = await listener.GetContextAsync();

            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url.AbsolutePath == "/ws")
            {
                _ = HandleWebSocket(ctx);
                continue;
            }

            await HandleHttp(ctx);
        }
    }

    static async Task HandleWebSocket(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;

        lock (wsClients) wsClients.Add(ws);

        await SendState(ws);

        var buffer = new byte[4096];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp))
                    continue;

                var type = typeProp.GetString();

                if (type == "add")
                {
                    if (root.TryGetProperty("videoId", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            lock (lockObj)
                                data.PlayerActive.Add(id);

                            await BroadcastState();
                        }
                    }
                }
                else if (type == "skip")
                {
                    lock (lockObj)
                    {
                        if (data.PlayerActive.Count > 0)
                        {
                            var v = data.PlayerActive[0];
                            data.PlayerActive.RemoveAt(0);
                            data.PlayerPassed.Add(v);
                        }
                    }

                    await BroadcastState();
                }
            }
        }
        catch
        {
        }

        lock (wsClients) wsClients.Remove(ws);
    }


    static async Task SendState(WebSocket ws)
    {
        object payload;
        lock (lockObj)
        {
            payload = new
            {
                type = "state",
                active = data.PlayerActive,
                passed = data.PlayerPassed
            };
        }

        await Send(ws, payload);
    }

    static async Task Broadcast(object obj)
    {
        List<WebSocket> clients;
        lock (wsClients)
        {
            wsClients.RemoveAll(w => w.State != WebSocketState.Open);
            clients = wsClients.ToList();
        }

        foreach (var ws in clients)
            await Send(ws, obj);
    }

    static async Task BroadcastState()
    {
        await Broadcast(new
        {
            type = "state",
            active = data.PlayerActive,
            passed = data.PlayerPassed
        });
    }

    static async Task Send(WebSocket ws, object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var buf = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    static async Task HandleHttp(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        if (req.Url.AbsolutePath == "/add")
        {
            string videoId = AddVideo(req.QueryString["url"]);
            if (videoId != null)
                await Broadcast(new { type = "add", videoId });

            Write(res, videoId != null ? "OK" : "FAILED");
        }
        else if (req.Url.AbsolutePath == "/update")
        {
            if (int.TryParse(req.QueryString["index"], out int index))
            {
                lock (lockObj)
                {
                    if (index >= 0 && index < data.PlayerActive.Count)
                    {
                        var v = data.PlayerActive[index];
                        data.PlayerActive.RemoveAt(index);
                        data.PlayerPassed.Add(v);
                    }
                }

                await BroadcastState();
            }

            Write(res, "OK");
        }
        else
        {
            Write(res, Html);
        }
    }

    static void Write(HttpListenerResponse res, string text)
    {
        var buf = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf);
        res.OutputStream.Close();
    }

    static string AddVideo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        string id = null;
        try
        {
            if (url.Contains("youtube.com"))
                id = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["v"];
            else if (url.Contains("youtu.be"))
                id = new Uri(url).AbsolutePath.Trim('/');
        }
        catch
        {
        }

        if (string.IsNullOrEmpty(id)) return null;

        lock (lockObj)
            data.PlayerActive.Add(id);

        return id;
    }

    static string Html => @"<!DOCTYPE html>
<html lang=""ru"">
<head>
<meta charset=""UTF-8"">
<title>DonationAlerts Player</title>
<style>
body{margin:0;font-family:Arial;background:#0e0e0e;color:#fff}
#container{display:flex;height:100vh}
#left{flex:2;padding:10px}
#right{flex:1;padding:10px;border-left:1px solid #333;overflow:auto}
#title{font-size:22px;margin-bottom:6px}
#queueCount{color:#aaa;margin-bottom:10px}
h3{margin:10px 0 5px}
ul{list-style:none;padding:0;margin:0}
li{padding:4px 0;border-bottom:1px solid #222}
#player{width:100%;height:390px;background:#000;margin-bottom:10px}
#controls{display:flex;gap:10px;margin-bottom:10px}
#controls input{flex:1;padding:6px;font-size:16px}
#controls button{padding:6px 12px;font-size:16px;cursor:pointer}
#skipBtn{background-color:#ff4c4c;color:#fff;border:none;border-radius:4px}
#addBtn{background-color:#4caf50;color:#fff;border:none;border-radius:4px}
</style>
</head>
<body>
<div id=""container"">
    <div id=""left"">
        <div id=""title"">Загрузка...</div>
        <div id=""queueCount"">В очереди: 0</div>
        <div id=""player""></div>
        <div id=""controls"">
            <button id=""skipBtn"">Пропустить</button>
            <input type=""text"" id=""newVideo"" placeholder=""Вставьте ссылку YouTube"">
            <button id=""addBtn"">Добавить</button>
        </div>
    </div>
    <div id=""right"">
        <h3>Очередь</h3>
        <ul id=""activeList""></ul>
        <h3>Проиграно</h3>
        <ul id=""passedList""></ul>
    </div>
</div>

<script src=""https://www.youtube.com/iframe_api""></script>
<script>
const API_KEY = 'AIzaSyDg0Da8M9fGfdFga8CNrZle5ohiYnPDU7o';

let player;
let ws;

let domReady = false;
let stateReady = false;
let playerReady = false;

let active = [];
let passed = [];
let titles = {};
let index = 0;

let currentVideoId = null;

const title = document.getElementById('title');
const activeList = document.getElementById('activeList');
const passedList = document.getElementById('passedList');
const queueCount = document.getElementById('queueCount');
const skipBtn = document.getElementById('skipBtn');
const addBtn = document.getElementById('addBtn');
const newVideo = document.getElementById('newVideo');

let playerPromiseResolve;
const playerReadyPromise = new Promise(resolve => playerPromiseResolve = resolve);

document.addEventListener('DOMContentLoaded', () => {
    domReady = true;
    connectWS();
});

function connectWS() {
    ws = new WebSocket(`ws://${location.host}/ws`);

    ws.onmessage = async e => {
        const msg = JSON.parse(e.data);

        if (msg.type === 'state') {
            active = msg.active || [];
            passed = msg.passed || [];
            index = 0;
            stateReady = true;
            await tryRender();
        }
    };

    ws.onclose = () => setTimeout(connectWS, 2000);
}

function onYouTubeIframeAPIReady() {
    player = new YT.Player('player', {
        height: '390',
        width: '640',
        videoId: '',
        events: {
            onReady: () => {
                playerReady = true;
                playerPromiseResolve();
                tryRender();
            },
            onStateChange: e => {
                if (e.data === YT.PlayerState.ENDED) {
                    currentVideoId = null;
                    ws.send(JSON.stringify({ type: 'skip' }));
                }
            }
        }
    });
}

async function tryRender() {
    if (!domReady || !stateReady) return;

    if (!playerReady && window.YT && YT.Player && !player) {
        onYouTubeIframeAPIReady();
        return;
    }

    await render();
    await playerReadyPromise;

    if (active.length > 0) play(0);
}

function play(i) {
    if (!player || !active[i]) return;

    const nextId = active[i];

    if (currentVideoId === nextId) return;

    currentVideoId = nextId;
    player.loadVideoById(nextId);
}

async function render() {
    activeList.innerHTML = '';
    passedList.innerHTML = '';

    for (const v of active) {
        const li = document.createElement('li');
        li.textContent = await getTitle(v);
        activeList.appendChild(li);
    }

    for (const v of passed) {
        const li = document.createElement('li');
        li.textContent = await getTitle(v);
        passedList.appendChild(li);
    }

    title.textContent = active[0]
        ? await getTitle(active[0])
        : 'Очередь пуста';

    queueCount.textContent = `В очереди: ${active.length}`;
}

async function getTitle(id) {
    if (titles[id]) return titles[id];

    try {
        const r = await fetch(
            `https://youtube.googleapis.com/youtube/v3/videos?part=snippet&id=${id}&key=${API_KEY}`
        );
        const j = await r.json();
        titles[id] = j.items?.[0]?.snippet?.title || id;
    } catch {
        titles[id] = id;
    }

    return titles[id];
}

/* ===== КНОПКИ ===== */

skipBtn.addEventListener('click', () => {
    currentVideoId = null;
    ws.send(JSON.stringify({ type: 'skip' }));
});

addBtn.addEventListener('click', addVideo);

newVideo.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
        addVideo();
        e.preventDefault();
    }
});

function addVideo() {
    const url = newVideo.value.trim();
    if (!url) return;

    const videoId = extractVideoId(url);
    if (!videoId) return;

    ws.send(JSON.stringify({ type: 'add', videoId }));
    newVideo.value = '';
}

function extractVideoId(url) {
    const m = url.match(/(?:v=|youtu\.be\/)([a-zA-Z0-9_-]{11})/);
    return m ? m[1] : null;
}
</script>
</body>
</html>
";
}