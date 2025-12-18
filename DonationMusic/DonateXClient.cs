using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;

class DonateXClient
{
    private const string ClientId = "BgNwbsf3Rfq02AASI5ChMg";
    private const string RedirectUri = "http://localhost:5000/callback";
    private const string Scope = "openid donations.read donations.subscribe";

    static async Task Main(string[] args)
    {
        string accessToken = await GetAccessTokenAsync();
        Console.WriteLine("AccessToken получен!");

        await SubscribeToDonationsAsync(accessToken);
    }

    static (string codeVerifier, string codeChallenge) GeneratePkce()
    {
        var codeVerifierBytes = RandomNumberGenerator.GetBytes(64);
        string codeVerifier = Convert.ToBase64String(codeVerifierBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var sha256 = SHA256.Create();
        byte[] challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        string codeChallenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return (codeVerifier, codeChallenge);
    }

    static async Task<string> GetAccessTokenAsync()
    {
        var (codeVerifier, codeChallenge) = GeneratePkce();

        string authUrl = $"https://donatex.gg/api/connect/authorize?" +
                         $"client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
                         $"response_type=code&scope={Uri.EscapeDataString(Scope)}&" +
                         $"code_challenge={codeChallenge}&code_challenge_method=S256";

        Console.WriteLine("Откройте ссылку в браузере для авторизации:\n" + authUrl);

        var httpListener = new HttpListener();
        httpListener.Prefixes.Add(RedirectUri.EndsWith("/") ? RedirectUri : RedirectUri + "/");
        httpListener.Start();

        var context = await httpListener.GetContextAsync();
        string code = context.Request.QueryString["code"];

        byte[] buffer = Encoding.UTF8.GetBytes("<html><body><script>window.close();</script>Авторизация успешна!</body></html>");
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
        httpListener.Stop();

        if (string.IsNullOrEmpty(code))
            throw new Exception("Код авторизации не получен.");

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier
        };

        using var httpClient = new HttpClient();
        var content = new FormUrlEncodedContent(values);
        var response = await httpClient.PostAsync("https://donatex.gg/api/connect/token", content);
        var json = await response.Content.ReadAsStringAsync();
        var obj = JObject.Parse(json);

        return obj["access_token"]?.Value<string>();
    }

    static async Task SubscribeToDonationsAsync(string accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"https://donatex.gg/api/public-donations-hub?access_token={accessToken}")
            .WithAutomaticReconnect()
            .Build();

        connection.On<JObject>("DonationCreated", donation =>
        {
            string username = donation["username"]?.ToString();
            string message = donation["message"]?.ToString();
            string currency = donation["currency"]?.ToString();
            decimal amount = donation["amount"]?.Value<decimal>() ?? 0;
            string musicLink = donation["musicLink"]?.ToString();

            Console.WriteLine($"Новый донат от {username}: {amount} {currency}");
            Console.WriteLine($"Сообщение: {message}");
            if (!string.IsNullOrEmpty(musicLink))
                Console.WriteLine($"Музыка: {musicLink}");
            Console.WriteLine("-------------------------");
        });

        await connection.StartAsync();
        Console.WriteLine("Подписка на донаты активна. Ожидание новых донатов...");
        await Task.Delay(-1);
    }
}
