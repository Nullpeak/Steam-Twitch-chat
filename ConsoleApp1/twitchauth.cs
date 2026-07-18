using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ConsoleApp1
{
    public class TwitchTokenResponse
    {
        public string access_token { get; set; } = "";
        public string refresh_token { get; set; } = "";
        public int expires_in { get; set; }
        public string[] scope { get; set; } = Array.Empty<string>();
        public string token_type { get; set; } = "";
    }

    static class TwitchAuth
    {
        private const string RedirectUri = "http://localhost:3000/api/callback";
        // Ajusta los scopes según lo que necesite tu bot
        private const string Scopes = "chat:read chat:edit";

        private static readonly HttpClient _http = new HttpClient();

        // ======================================
        // FLUJO INICIAL: abre el navegador y captura el code
        // ======================================
        public static async Task<TwitchTokenResponse> AuthorizeInteractiveAsync(string clientId, string clientSecret)
        {
            string state = Guid.NewGuid().ToString("N");

            string authUrl =
                "https://id.twitch.tv/oauth2/authorize" +
                $"?client_id={clientId}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scopes)}" +
                $"&state={state}";

            using var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:3000/");
            listener.Start();

            Console.WriteLine("Abriendo navegador para autorizar la app de Twitch...");
            Console.WriteLine("Si no se abre solo, copiá esta URL en tu navegador:");
            Console.WriteLine(authUrl);

            try
            {
                Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });
            }
            catch
            {
                // Si falla el auto-open, el usuario ya tiene la URL impresa arriba
            }

            var context = await listener.GetContextAsync();
            var query = context.Request.QueryString;

            string? code = query["code"];
            string? returnedState = query["state"];

            string responseHtml = "<html><body><h2>Listo, ya podés cerrar esta pestaña.</h2></body></html>";
            var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.OutputStream.Close();
            listener.Stop();

            if (string.IsNullOrEmpty(code) || returnedState != state)
                throw new Exception("No se recibió un código de autorización válido.");

            return await ExchangeCodeForTokenAsync(clientId, clientSecret, code);
        }

        private static async Task<TwitchTokenResponse> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUri
            };

            var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Error obteniendo token: {resp.StatusCode} - {body}");

            return JsonSerializer.Deserialize<TwitchTokenResponse>(body)!;
        }

        // ======================================
        // REFRESCAR TOKEN (sin intervención del usuario)
        // ======================================
        public static async Task<TwitchTokenResponse> RefreshTokenAsync(string clientId, string clientSecret, string refreshToken)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            };

            var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Error refrescando token: {resp.StatusCode} - {body}");

            return JsonSerializer.Deserialize<TwitchTokenResponse>(body)!;
        }

        // ======================================
        // VALIDAR TOKEN ACTUAL (Twitch pide validar cada ~1h)
        // ======================================
        public static async Task<bool> ValidateTokenAsync(string accessToken)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            req.Headers.Add("Authorization", $"OAuth {accessToken}");
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
    }
}