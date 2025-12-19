using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;

public class DonateXClient
{
    private const String Scope = "openid donations.read donations.subscribe donations.write";
    private String _accessToken;
    private Config _config;
    
    public class Donation
    {
        public String Id { get; set; }
        public String Username { get; set; }
        public String Message { get; set; }
        public String MusicLink { get; set; }
        public Decimal AmountInRub { get; set; }
    }

    public event Action<Donation> OnDonation;

    public DonateXClient()
    {
        _config = JsonLoader.LoadJson<Config>("config.json");
    }

    public async Task InitializeAsync()
    {
        _accessToken = await GetAccessTokenAsync();
    }

    public async Task StartAsync()
    {
        if (String.IsNullOrEmpty(_accessToken))
            throw new Exception("AccessToken не получен. Сначала вызовите InitializeAsync().");

        await SubscribeToDonationsAsync(_accessToken);
    }

    private static (String codeVerifier, String codeChallenge) GeneratePkce()
    {
        var codeVerifierBytes = RandomNumberGenerator.GetBytes(64);
        String codeVerifier = Convert.ToBase64String(codeVerifierBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var sha256 = SHA256.Create();
        byte[] challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        String codeChallenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return (codeVerifier, codeChallenge);
    }

    private async Task<String> GetAccessTokenAsync()
    {
        var (codeVerifier, codeChallenge) = GeneratePkce();

        String authUrl = $"https://donatex.gg/api/connect/authorize?" +
                         $"client_id={_config.DonateX.ClientId}&redirect_uri={_config.DonateX.RedirectUri}&" +
                         $"response_type=code&scope={Uri.EscapeDataString(Scope)}&" +
                         $"code_challenge={codeChallenge}&code_challenge_method=S256";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            Console.WriteLine("Откройте ссылку вручную: " + authUrl);
        }

        var httpListener = new HttpListener();
        httpListener.Prefixes.Add(_config.DonateX.RedirectUri + "/");
        httpListener.Start();

        var context = await httpListener.GetContextAsync();
        var code = context.Request.QueryString["code"];

        Byte[] buffer = Encoding.UTF8.GetBytes(
            "<html><body><script>window.close();</script>Авторизация успешна!</body></html>"
        );
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
        httpListener.Stop();

        if (String.IsNullOrEmpty(code))
            throw new Exception("Код авторизации не получен.");


        var values = new Dictionary<String, String>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _config.DonateX.ClientId,
            ["redirect_uri"] = _config.DonateX.RedirectUri,
            ["code_verifier"] = codeVerifier
        };

        using var httpClient = new HttpClient();
        var content = new FormUrlEncodedContent(values);
        var response = await httpClient.PostAsync("https://donatex.gg/api/connect/token", content);
        var json = await response.Content.ReadAsStringAsync();
        var obj = JObject.Parse(json);
        if (obj["access_token"] == null)
            throw new Exception("Access token не получен: " + json);

        return obj["access_token"].Value<String>();
    }

    private async Task SubscribeToDonationsAsync(String accessToken)
    {
        var hubUrl = $"https://donatex.gg/api/public-donations-hub?access_token={accessToken}";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        connection.On("DonationCreated", async (Object donation) =>
        {
            String jsonString;
            if (donation is String s)
            {
                jsonString = s;
            }
            else
            {
                jsonString = System.Text.Json.JsonSerializer.Serialize(donation);
            }
            var donationData = JObject.Parse(jsonString);
            var summa = donationData["amountInRub"]?.Value<Decimal>() ?? 0;
            
            var donationObj = new Donation
            {
                Id = donationData["id"]?.Value<String>(),
                Username = donationData["username"]?.Value<String>(),
                Message = donationData["message"]?.Value<String>(),
                MusicLink = summa >= _config.DonateX.Summa ? donationData["musicLink"]?.Value<String>() : null,
                AmountInRub = donationData["amountInRub"]?.Value<Decimal>() ?? 0,
            };
            if (OnDonation != null)
                 OnDonation.Invoke(donationObj);
        });
        await connection.StartAsync();
        await Task.Delay(Timeout.Infinite);
    }
}
