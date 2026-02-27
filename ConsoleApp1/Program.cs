using DotNetEnv;
using SteamKit2;
using System;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace ConsoleApp1
{
    static class Program
    {
        private static TwitchClient? _twitchClient;
        private static SteamClient? _steamClient;
        private static CallbackManager? _steamCallbackManager;
        private static SteamUser? _steamUser;
        private static SteamFriends? _steamFriends;
        private static bool _steamLoggedIn = false;
        private static ulong _targetSteamId;
        private static CancellationTokenSource? _cts;

        static void Main(string[] args)
        {
            Console.WriteLine("Programa iniciado correctamente.");
            Console.WriteLine("Directorio actual:");
            Console.WriteLine(Directory.GetCurrentDirectory());
            try
            {
                var envPath = Path.Combine(AppContext.BaseDirectory, "hola.env");
                Env.Load(envPath);

                Console.WriteLine("Buscando .env en:");
                Console.WriteLine(envPath);
                var rawValue = Env.GetString("TARGET_STEAM_ID");

                Console.WriteLine("Valor crudo leído:");
                Console.WriteLine(rawValue == null ? "NULL" : $"'{rawValue}'");
                Console.WriteLine("Longitud: " + (rawValue?.Length ?? 0));

                var targetSteamIdStr = Env.GetString("TARGET_STEAM_ID") ?? string.Empty;
                if (!ulong.TryParse(targetSteamIdStr, out _targetSteamId))
                {
                    Console.WriteLine("Error: El SteamID en el archivo .env no es válido.");
                    return;
                }

                var twitchBotUser = Env.GetString("TWITCH_BOT_USERNAME") ?? string.Empty;
                var twitchOauth = Env.GetString("TWITCH_OAUTH_TOKEN") ?? string.Empty;
                var twitchChannel = Env.GetString("TWITCH_CHANNEL") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(twitchBotUser) || string.IsNullOrWhiteSpace(twitchOauth) || string.IsNullOrWhiteSpace(twitchChannel))
                {
                    Console.WriteLine("Error: Falta configuración de Twitch en .env (usuario, oauth o canal).");
                    return;
                }

                var credentials = new ConnectionCredentials(twitchBotUser, twitchOauth);

                var clientOptions = new ClientOptions();
                var customClient = new WebSocketClient(clientOptions);
                _twitchClient = new TwitchClient(customClient);

                // Initialize before subscribing in some TwitchLib versions; subscribing is safe either way.
                _twitchClient.Initialize(credentials, twitchChannel);

                _twitchClient.OnMessageReceived += OnTwitchMessageReceived;
                _twitchClient.OnConnected += (sender, e) => Console.WriteLine("Conectado al chat de Twitch");
                _twitchClient.OnDisconnected += (sender, e) => Console.WriteLine("Desconectado de Twitch. Intentando reconectar...");

                _twitchClient.Connect();

                // Steam configuration
                _steamClient = new SteamClient();
                _steamCallbackManager = new CallbackManager(_steamClient);
                _steamUser = _steamClient.GetHandler<SteamUser>();
                _steamFriends = _steamClient.GetHandler<SteamFriends>();

                // Ensure subscribing with lambdas to match delegate expectations
                _steamCallbackManager.Subscribe<SteamClient.ConnectedCallback>(cb => OnSteamConnected(cb));
                _steamCallbackManager.Subscribe<SteamClient.DisconnectedCallback>(cb => OnSteamDisconnected(cb));
                _steamCallbackManager.Subscribe<SteamUser.LoggedOnCallback>(cb => OnSteamLoggedOn(cb));
                _steamCallbackManager.Subscribe<SteamUser.LoggedOffCallback>(cb => OnSteamLoggedOff(cb));

                Console.WriteLine("Iniciando sesión en Steam...");
                _steamClient.Connect();

                _cts = new CancellationTokenSource();
                Task.Run(() => SteamLoop(_cts.Token), _cts.Token);

                Console.WriteLine("Presiona Enter para salir...");
                Console.ReadLine();

                // Graceful shutdown
                _cts.Cancel();
                try { _steamClient?.Disconnect(); } catch { }
                try { _twitchClient?.Disconnect(); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al cargar la configuración: {ex.Message}");
                return;
            }
        }

        private static void OnTwitchMessageReceived(object? sender, OnMessageReceivedArgs e)
        {
            if (_twitchClient == null || e?.ChatMessage == null) return;

            string message = e.ChatMessage.Message;
            string username = e.ChatMessage.Username;
            Console.WriteLine($"{username}: {message}");

            if (_steamLoggedIn && _steamFriends != null && _targetSteamId != 0)
            {
                SendSteamMessage($"{username}: {message}");
            }
            else
            {
                Console.WriteLine("Steam no está conectado. Mensaje no enviado.");
            }
        }

        private static void SendSteamMessage(string message)
        {
            if (_targetSteamId == 0 || _steamFriends == null)
            {
                Console.WriteLine("Error: No se ha configurado el SteamID del usuario destino o SteamFriends no está inicializado.");
                return;
            }
            try
            {
                var steamId = new SteamID(_targetSteamId);
                _steamFriends.SendChatMessage(steamId, EChatEntryType.ChatMsg, message);
                Console.WriteLine($"Mensaje enviado a Steam: {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al enviar mensaje a Steam: {ex.Message}");
            }
        }

        private static void SteamLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _steamCallbackManager?.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error en SteamLoop: {ex.Message}");
                }
            }
        }

        // Steam callbacks
        private static void OnSteamConnected(SteamClient.ConnectedCallback callback)
        {
            if (_steamUser == null) return;

            Console.WriteLine("Conectado a Steam.");
            var username = Env.GetString("STEAM_USERNAME") ?? string.Empty;
            var password = Env.GetString("STEAM_PASSWORD") ?? string.Empty;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                Console.WriteLine("Error: Credenciales de Steam no configuradas en .env.");
                return;
            }

            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                Password = password,
            });
        }

        private static void OnSteamDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Desconectado de Steam.");
            _steamLoggedIn = false;
        }

        private static void OnSteamLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine($"Error al iniciar sesión en Steam: {callback.Result}");
                return;
            }
            Console.WriteLine("Steam: Sesión iniciada correctamente.");
            _steamLoggedIn = true;
        }

        private static void OnSteamLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Steam: Sesión cerrada. Intentando reconectar...");
            _steamLoggedIn = false;
            Task.Delay(5000).Wait();
            try
            {
                _steamClient?.Connect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al reconectar a Steam: {ex.Message}");
            }
        }
    }
}
