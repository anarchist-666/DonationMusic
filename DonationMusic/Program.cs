using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

public class Data
{
    public List<String> PlayerActive = new();
    public List<String> PlayerPassed = new();
}

public class Config
{
    [JsonProperty("Настройка DonationAlerts")]
    public DonationAlerts DonationAlerts = new();

    [JsonProperty("Настройка DonationX")] 
    public DonationX DonationX = new();

    [JsonProperty("WebHook для отправки текущей песни (если надо будет)")]
    public String PlayingWebhookUrl = "";
}

public class DonationAlerts
{
    [JsonProperty("ID приложения")] public String ClientId = "";

    [JsonProperty("Секретный токен приложения")]
    public String ClientSecret = "";

    [JsonProperty("Секретный токен виджета")]
    public String WidgetToken = "";
 
    [JsonProperty("URL для отклика")] 
    public String RedirectUri = "http://localhost:5000/callback/";
    
    [JsonProperty("Минимальный донат на музыку (чтобы API меньше дёргать)")]
    public Int32 Summa = 100;
}

public class DonationX
{
    [JsonProperty("ID клиента")] 
    public String ClientId = "";
    
    [JsonProperty("URL для отклика")] 
    public String RedirectUri = "http://localhost:3000/callback";

    [JsonProperty("Минимальный донат на музыку (чтобы API меньше дёргать)")]
    public Int32 Summa = 100;
}

class Program
{
    private static readonly Object logLock = new();
    private static readonly String logFile = "error.log";
    private static String currentNowPlayingId = null;
    private static readonly HttpClient http = new();
    private static Config _config = JsonLoader.LoadJson<Config>("config.json");
    private static Data data = JsonLoader.LoadJson<Data>("data.json");
    private static HttpListener listener;
    private static Object lockObj = new();

    private static List<WebSocket> wsClients = new();
    
    private static Boolean daConfigured = new[]
    {
        _config.DonationAlerts.WidgetToken,
        _config.DonationAlerts.ClientId,
        _config.DonationAlerts.ClientSecret
    }.All(s => !String.IsNullOrWhiteSpace(s));
    
    private static Boolean dxConfigured = new[]
    {
        _config.DonationX.ClientId,
    }.All(s => !String.IsNullOrWhiteSpace(s));
    
    public static void Log(Exception ex, String context = null)
    {
        try
        {
            lock (logLock)
            {
                File.AppendAllText(
                    logFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n" +
                    (context != null ? $"Context: {context}\n" : "") +
                    ex + "\n\n"
                );
            }
        }
        catch
        {
            // логгер упал
        }
    }

    private static void Welcome()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("===================================");

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("Программа : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("DonationMusic");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("===================================");

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("Автор : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Anarchist");

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("Сделано с любовью для : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("ZakvielChannel");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("===================================");

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("URL ПЛЕЕРА : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("http://localhost:666");

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("URL НАЗВАНИЯ МУЗЫКИ КОТОРАЯ ИГРАЕТ : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("http://localhost:666/now");
        
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("URL СОЛИЧЕСТВА МУЗЫКИ В ОЧЕРЕДИ : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("http://localhost:666/count");
        
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("ТОКЕН : ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("ХЗ, Я НЕ ДАМ СПАЛИТЬ))");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("===================================");

        Console.ResetColor();
    }

    private static async Task Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (daConfigured)
            {
                var daClient = new DonationAlertsClient();
                await daClient.InitializeAsync();

                daClient.OnDonation += async donation =>
                {
                    String videoId = AddVideo(donation.Url);
                    if (videoId != null)
                        await BroadcastState();
                };

                _ = daClient.StartAsync();
            }

            if (dxConfigured)
            {
                var dxClient = new DonateXClient();
                await dxClient.InitializeAsync();

                dxClient.OnDonation += async donation =>
                {
                    String videoId = AddVideo(donation.MusicLink);
                    if (videoId != null)
                        await BroadcastState();
                };

                _ = dxClient.StartAsync();
            }
            
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:666/");
            listener.Start();

            Welcome();

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
        catch (Exception ex)
        {
            Log(ex, "Main loop");
        }
    }

    private static async Task HandleWebSocket(HttpListenerContext ctx)
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
                        if (!String.IsNullOrEmpty(id))
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
        catch (Exception ex)
        {
            Log(ex, "HandleWebSocket loop");
        }

        lock (wsClients) wsClients.Remove(ws);
    }


    private static async Task SendState(WebSocket ws)
    {
        Object payload;
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

    private static async Task Broadcast(Object obj)
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

    private static async Task BroadcastState()
    {
        String first = null;

        lock (lockObj)
        {
            if (data.PlayerActive.Count > 0)
                first = data.PlayerActive[0];
        }

        if (first != null)
            _ = SendNowPlaying(first);

        await Broadcast(new
        {
            type = "state",
            active = data.PlayerActive,
            passed = data.PlayerPassed
        });
    }


    private static async Task Send(WebSocket ws, Object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var buf = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task HandleHttp(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            if (req.Url.AbsolutePath == "/add")
            {
                String videoId = AddVideo(req.QueryString["url"]);
                if (videoId != null)
                    await Broadcast(new { type = "add", videoId });

                Write(res, videoId != null ? "OK" : "FAILED");
            }
            else if (req.Url.AbsolutePath == "/update")
            {
                if (Int32.TryParse(req.QueryString["index"], out Int32 index))
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
            else if (req.Url.AbsolutePath == "/now")
            {
                Write(res, NowHtml);
            }
            else if (req.Url.AbsolutePath == "/count")
            {
                Write(res, CountHtml);
            }
            else
            {
                Write(res, Html);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "HandleHttp loop");
        }
    }

    private static void Write(HttpListenerResponse res, String text)
    {
        var buf = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf);
        res.OutputStream.Close();
    }

    private static async Task<String?> GetYoutubeTitle(String videoId)
    {
        try
        {
            using var wc = new WebClient();
            var json = await wc.DownloadStringTaskAsync(
                $"https://www.googleapis.com/youtube/v3/videos?part=snippet&id={videoId}&key=AIzaSyDg0Da8M9fGfdFga8CNrZle5ohiYnPDU7o"
            );

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("items")[0]
                .GetProperty("snippet")
                .GetProperty("title")
                .GetString();
        }
        catch
        {
            return videoId;
        }
    }

    private static async Task SendNowPlaying(String videoId)
    {
        if (String.IsNullOrWhiteSpace(_config.PlayingWebhookUrl))
            return;

        if (currentNowPlayingId == videoId)
            return;

        currentNowPlayingId = videoId;

        var title = await GetYoutubeTitle(videoId);
        var youtubeUrl = $"https://youtu.be/{videoId}";

        var payload = new
        {
            title = title,
            url = youtubeUrl
        };

        var json = JsonSerializer.Serialize(payload);

        try
        {
            await http.PostAsync(_config.PlayingWebhookUrl,
                new StringContent(json, Encoding.UTF8, "application/json")
            );
        }
        catch (Exception ex)
        {
            Log(ex, "SendNowPlaying loop");
        }
    }

    private static String AddVideo(String url)
    {
        if (String.IsNullOrWhiteSpace(url)) return null;

        String id = null;
        try
        {
            if (url.Contains("youtube.com"))
                id = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["v"];
            else if (url.Contains("youtu.be"))
                id = new Uri(url).AbsolutePath.Trim('/');
        }
        catch (Exception ex)
        {
            Log(ex, "SendNowPlaying loop");
        }

        if (String.IsNullOrEmpty(id)) return null;

        lock (lockObj)
            data.PlayerActive.Add(id);

        return id;
    }

    private static String Html => @"<!DOCTYPE html>
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

    private static String NowHtml => @"<!DOCTYPE html>
<html lang=""ru"">
<head>
<meta charset=""UTF-8"">
<title>Now Playing</title>
<style>
body{
    margin:0;
    background:#000;
    color:#fff;
    font-family:Arial;
    display:flex;
    justify-content:center;
    align-items:center;
    height:100vh;
}
#title{
    font-size:48px;
    text-align:center;
    padding:20px;
}
</style>
</head>
<body>

<div id=""title"">Загрузка...</div>

<script>
const API_KEY = 'AIzaSyDg0Da8M9fGfdFga8CNrZle5ohiYnPDU7o';
let ws;
let cache = {};

const titleEl = document.getElementById('title');

async function getTitle(id){
    if(cache[id]) return cache[id];

    try{
        const r = await fetch(
            `https://youtube.googleapis.com/youtube/v3/videos?part=snippet&id=${id}&key=${API_KEY}`
        );
        const j = await r.json();
        cache[id] = j.items?.[0]?.snippet?.title || id;
    }catch{
        cache[id] = id;
    }
    return cache[id];
}

function connect() {
    ws = new WebSocket(`ws://${location.host}/ws`);

    ws.onmessage = async e => {
        const msg = JSON.parse(e.data);

        if (msg.type === 'state') {
            if (msg.active && msg.active.length > 0) {
                titleEl.textContent = await getTitle(msg.active[0]);
            } else {
                titleEl.textContent = 'Ничего не играет';
            }
        }
    };

    ws.onclose = () => setTimeout(connect, 2000);
}

connect();
</script>
</body>
</html>";

    private static String CountHtml => @"<!DOCTYPE html>
<html lang=""ru"">
<head>
<meta charset=""UTF-8"">
<title>Queue Count</title>
<style>
body{
    margin:0;
    background:#000;
    color:#fff;
    font-family:Arial;
    display:flex;
    justify-content:center;
    align-items:center;
    height:100vh;
}
#text{
    font-size:64px;
    font-weight:bold;
}
</style>
</head>
<body>

<div id=""text"">ОЧЕРЕДЬ МУЗИКИ: 0</div>

<script>
let ws;
const textEl = document.getElementById('text');

function connect() {
    ws = new WebSocket(`ws://${location.host}/ws`);

    ws.onmessage = e => {
        const msg = JSON.parse(e.data);

        if (msg.type === 'state') {
            const count = msg.active ? msg.active.length : 0;
            textEl.textContent = `ОЧЕРЕДЬ МУЗИКИ: ${count}`;
        }
    };

    ws.onclose = () => setTimeout(connect, 2000);
}

connect();
</script>

</body>
</html>";

}