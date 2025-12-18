using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class DonationAlertsClient
{
    private readonly HttpClient _http = new();
    private ClientWebSocket _webSocket;
    private Config _config;
    private String _accessToken;

    public class Donation
    {
        public String Username { get; set; }
        public String Message { get; set; }
        public Decimal Amount { get; set; }
        public String Currency { get; set; }
        public String Url { get; set; }
    }
    
    public event Action<Donation> OnDonation;

    public DonationAlertsClient()
    {
        _config = JsonLoader.LoadJson<Config>("config.json");
    }

    public async Task InitializeAsync()
    {
        _accessToken = await GetAccessTokenAsync();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private async Task<String> GetAccessTokenAsync()
    {
        Console.WriteLine("Открываем ссылку для авторизации...");
        String authUrl =
            $"https://www.donationalerts.com/oauth/authorize?client_id={_config.DonationAlerts.ClientId}&redirect_uri={_config.DonationAlerts.RedirectUri}&response_type=code&scope=oauth-user-show%20oauth-donation-subscribe";

        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });
            process.WaitForExit();
        }
        catch
        {
            Console.WriteLine("Откройте ссылку вручную: " + authUrl);
        }

        var httpListener = new HttpListener();
        httpListener.Prefixes.Add(_config.DonationAlerts.RedirectUri);
        httpListener.Start();

        var context = await httpListener.GetContextAsync();
        var code = context.Request.QueryString["code"];

        var buffer =
            Encoding.UTF8.GetBytes("<html><body><script>window.close();</script>Авторизация успешна!</body></html>");
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
        httpListener.Stop();

        if (String.IsNullOrEmpty(code))
            throw new Exception("Код авторизации не получен.");

        var data = new Dictionary<String, String>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _config.DonationAlerts.ClientId,
            ["client_secret"] = _config.DonationAlerts.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = _config.DonationAlerts.RedirectUri
        };

        var response =
            await _http.PostAsync("https://www.donationalerts.com/oauth/token", new FormUrlEncodedContent(data));
        var json = await response.Content.ReadAsStringAsync();
        var obj = JObject.Parse(json);

        return obj["access_token"].Value<String>();
    }

    private async Task<String> GetVideoUrlAsync(String alertId)
    {
        String callback = "jQuery" + new Random().Next(100000, 999999) + "_" +
                          DateTimeOffset.Now.ToUnixTimeMilliseconds();

        String url =
            $"https://www.donationalerts.com/api/getmediadata?callback={callback}&token={_config.DonationAlerts.WidgetToken}";

        var res = await _http.GetStringAsync(url);

        Int32 firstParen = res.IndexOf('(');
        Int32 lastParen = res.LastIndexOf(')');
        if (firstParen == -1 || lastParen == -1)
            throw new Exception("Неверный формат JSONP");

        String jsonText = res.Substring(firstParen + 1, lastParen - firstParen - 1);

        var json = JObject.Parse(jsonText);
        var mediaArray = (JArray)json["media"];

        var mediaItem = mediaArray.FirstOrDefault(m =>
            m["alert_id"]?.Value<String>() == alertId && m["type"]?.Value<String>() == "video");
        if (mediaItem == null) return null;

        String additionalData = mediaItem["additional_data"]?.Value<String>();
        if (String.IsNullOrEmpty(additionalData)) return null;

        var additionalJson = JObject.Parse(additionalData);
        return additionalJson["url"]?.Value<String>();
    }

    public async Task StartAsync()
    {
        try
        {
            var userJson = await _http.GetStringAsync("https://www.donationalerts.com/api/v1/user/oauth");
            var user = JObject.Parse(userJson)["data"];
            String socketToken = user["socket_connection_token"]?.Value<String>();
            Int64 userId = user["id"]?.Value<Int64>() ?? 0;

            if (String.IsNullOrEmpty(socketToken) || userId == 0)
                throw new Exception("Не удалось получить socketToken или userId.");

            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri("wss://centrifugo.donationalerts.com/connection/websocket"),
                CancellationToken.None);
            await SendAsync(new { id = 1, @params = new { token = socketToken } });

            String clientId = null;
            var buffer = new byte[4096];
            while (clientId == null)
            {
                var r = await _webSocket.ReceiveAsync(buffer, CancellationToken.None);
                var obj = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
                clientId = obj["result"]?["client"]?.Value<String>();
            }

            var payload = new { client = clientId, channels = new[] { $"$alerts:donation_{userId}" } };
            var response = await _http.PostAsync(
                "https://www.donationalerts.com/api/v1/centrifuge/subscribe",
                new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
            );

            var subObj = JObject.Parse(await response.Content.ReadAsStringAsync());
            String subscribeToken = subObj["channels"]?[0]?["token"]?.Value<String>();
            await SendAsync(new
            {
                id = 2, method = 1, @params = new { channel = $"$alerts:donation_{userId}", token = subscribeToken }
            });

            buffer = new byte[8192];
            while (_webSocket.State == WebSocketState.Open)
            {
                var r = await _webSocket.ReceiveAsync(buffer, CancellationToken.None);
                var obj = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
                //Console.WriteLine(obj);
                var donationData = obj["result"]?["data"]?["data"];
                if (donationData != null && donationData["name"]?.Value<String>() == "Donations")
                {
                    var summa = donationData["amount"]?.Value<Decimal>() ?? 0;
                    String url;
                    if (donationData["currency"]?.Value<String>() == "RUB")
                    {
                        url = summa >= _config.DonationAlerts.Summa ? await GetVideoUrlAsync(donationData["id"]?.Value<String>()) : null;
                    }
                    else
                    {
                        url = await GetVideoUrlAsync(donationData["id"]?.Value<String>());
                    }
                    OnDonation?.Invoke(new Donation
                    {
                        Username = donationData["username"]?.Value<String>(),
                        Amount = donationData["amount"]?.Value<Decimal>() ?? 0,
                        Currency = donationData["currency"]?.Value<String>(),
                        Message = donationData["message"]?.Value<String>(),
                        Url = url
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log(ex, "StartAsync loop");
        }
    }

    private async Task SendAsync(Object obj)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj));
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}