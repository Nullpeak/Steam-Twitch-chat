using DotNetEnv;
using SteamKit2;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
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

        private static Dictionary<string, ulong> _steamTargets = new();
        private static ulong _targetSteamId;

        private const int MaxSteamAccounts = 5;
        private static string _envPath = "";

        private static CancellationTokenSource? _cts;

        static void Main(string[] args)
        {
            Console.WriteLine("Programa iniciado correctamente.");

            _envPath = Path.Combine(AppContext.BaseDirectory, "hola.env");

            if (!File.Exists(_envPath))
            {
                Console.WriteLine("No se encontró hola.env");
                return;
            }

            Env.Load(_envPath);

            // Cargar cuentas Steam
            LoadSteamTargetsFromEnv();
            SelectSteamTarget();

            // =====================
            // TWITCH
            // =====================
            var twitchBotUser = Env.GetString("TWITCH_BOT_USERNAME") ?? "";
            var twitchOauth = Env.GetString("TWITCH_OAUTH_TOKEN") ?? "";
            var twitchChannel = Env.GetString("TWITCH_CHANNEL") ?? "";

            var credentials = new ConnectionCredentials(twitchBotUser, twitchOauth);
            var clientOptions = new ClientOptions();
            var customClient = new WebSocketClient(clientOptions);
            _twitchClient = new TwitchClient(customClient);

            _twitchClient.Initialize(credentials, twitchChannel);
            _twitchClient.OnMessageReceived += OnTwitchMessageReceived;
            _twitchClient.OnConnected += (s, e) => Console.WriteLine("Conectado a Twitch");
            _twitchClient.Connect();

            // =====================
            // STEAM
            // =====================
            _steamClient = new SteamClient();
            _steamCallbackManager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamFriends = _steamClient.GetHandler<SteamFriends>();

            _steamCallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnSteamConnected);
            _steamCallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnSteamLoggedOn);
            _steamCallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnSteamDisconnected);
            _steamCallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnSteamLoggedOff);

            _steamClient.Connect();

            _cts = new CancellationTokenSource();
            Task.Run(() => SteamLoop(_cts.Token));
            Task.Run(() => ConsoleCommandLoop());

            while (true)
                Thread.Sleep(1000);
        }

        // ======================================
        // CARGAR DESDE .ENV
        // ======================================
        private static void LoadSteamTargetsFromEnv()
        {
            _steamTargets.Clear();

            for (int i = 1; i <= MaxSteamAccounts; i++)
            {
                var name = Env.GetString($"STEAM_TARGET_{i}_NAME");
                var idStr = Env.GetString($"STEAM_TARGET_{i}_ID");

                if (!string.IsNullOrWhiteSpace(name) &&
                    ulong.TryParse(idStr, out ulong steamId))
                {
                    _steamTargets[name] = steamId;
                }
            }
        }

        // ======================================
        // MENÚ CON OPCIÓN 0
        // ======================================
        private static void SelectSteamTarget()
        {
            Console.WriteLine("\n=== SELECCIONA USUARIO STEAM ===");

            var keys = new List<string>(_steamTargets.Keys);

            for (int i = 0; i < keys.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {keys[i]} ({_steamTargets[keys[i]]})");
            }

            Console.WriteLine("0.Agregar nueva cuenta");

            Console.Write("Opción: ");
            var input = Console.ReadLine();

            if (input == "0")
            {
                AddSteamTargetToEnv();
                LoadSteamTargetsFromEnv();
                SelectSteamTarget();
                return;
            }

            if (int.TryParse(input, out int selected) &&
                selected > 0 &&
                selected <= keys.Count)
            {
                var selectedKey = keys[selected - 1];
                _targetSteamId = _steamTargets[selectedKey];
                Console.WriteLine($"Ahora enviando mensajes a: {selectedKey}");
            }
            else
            {
                Console.WriteLine("Opción inválida.");
            }
        }

        // ======================================
        // AGREGAR Y GUARDAR EN .ENV
        // ======================================
        private static void AddSteamTargetToEnv()
        {
            if (_steamTargets.Count >= MaxSteamAccounts)
            {
                Console.WriteLine($"Máximo permitido: {MaxSteamAccounts} cuentas modifica el hola.env.");
                return;
            }

            Console.Write("\nAlias: ");
            var alias = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(alias))
            {
                Console.WriteLine("Alias inválido.");
                return;
            }

            Console.Write("SteamID64: ");
            var idInput = Console.ReadLine()?.Trim();

            if (!ulong.TryParse(idInput, out ulong steamId))
            {
                Console.WriteLine("SteamID inválido.");
                return;
            }

            for (int i = 1; i <= MaxSteamAccounts; i++)
            {
                var existingName = Env.GetString($"STEAM_TARGET_{i}_NAME");

                if (string.IsNullOrWhiteSpace(existingName))
                {
                    UpdateEnvFile($"STEAM_TARGET_{i}_NAME", alias);
                    UpdateEnvFile($"STEAM_TARGET_{i}_ID", steamId.ToString());
                    Console.WriteLine("Cuenta guardada en .env");
                    return;
                }
            }
        }

        private static void UpdateEnvFile(string key, string value)
        {
            var lines = File.Exists(_envPath)
                ? new List<string>(File.ReadAllLines(_envPath))
                : new List<string>();

            var prefix = key + "=";
            var index = lines.FindIndex(l => l.StartsWith(prefix));

            var newLine = $"{key}={value}";

            if (index >= 0)
                lines[index] = newLine;
            else
                lines.Add(newLine);

            File.WriteAllLines(_envPath, lines);
            Env.Load(_envPath);
        }

        // ======================================
        // TWITCH
        // ======================================
        private static void OnTwitchMessageReceived(object? sender, OnMessageReceivedArgs e)
        {
            var msg = e.ChatMessage.Message.Trim();
            Console.WriteLine($"[Twitch] {e.ChatMessage.DisplayName}: {msg}");

            if (!string.IsNullOrEmpty(msg))
                SendSteamMessage($"{e.ChatMessage.DisplayName}: {msg}");
        }

        // ======================================
        // ENVIAR A STEAM
        // ======================================
        private static void SendSteamMessage(string message)
        {
            if (!_steamLoggedIn || _steamFriends == null)
                return;

            try
            {
                var steamId = new SteamID(_targetSteamId);
                _steamFriends.SendChatMessage(steamId, EChatEntryType.ChatMsg, message);
                Console.WriteLine($"[Steam->{_targetSteamId}] {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error enviando mensaje: " + ex.Message);
            }
        }

        // ======================================
        // STEAM CALLBACKS
        // ======================================
        private static void OnSteamConnected(SteamClient.ConnectedCallback callback)
        {
            var username = Env.GetString("STEAM_USERNAME") ?? "";
            var password = Env.GetString("STEAM_PASSWORD") ?? "";

            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                Password = password
            });
        }

        private static void OnSteamLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                _steamLoggedIn = true;
                Console.WriteLine("Logueado en Steam correctamente.");
            }
        }

        private static void OnSteamDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _steamLoggedIn = false;
        }

        private static void OnSteamLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            _steamLoggedIn = false;
        }

        private static void SteamLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _steamCallbackManager?.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                Thread.Sleep(100);
            }
        }
        private static void ConsoleCommandLoop()
        {
            while (true)
            {
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                input = input.Trim().ToLower();

                // 🔥 Abrir menú interactivo
                if (input == "steam")
                {
                    ShowSteamSelectionMenu();
                    continue;
                }

                if (input == "steamlist")
                {
                    Console.WriteLine("=== CUENTAS DISPONIBLES ===");
                    foreach (var kv in _steamTargets)
                        Console.WriteLine($"{kv.Key} → {kv.Value}");

                    continue;
                }

                if (input == "steamreload")
                {
                    LoadSteamTargetsFromEnv();
                    Console.WriteLine("Cuentas recargadas desde .env");
                    continue;
                }

                Console.WriteLine("Comando desconocido.");
            }
        }
        private static void ShowSteamSelectionMenu()
        {
            if (_steamTargets.Count == 0)
            {
                Console.WriteLine("No hay cuentas cargadas.");
                return;
            }

            Console.WriteLine("\n=== CAMBIAR CUENTA STEAM ===");

            var keys = new List<string>(_steamTargets.Keys);

            for (int i = 0; i < keys.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {keys[i]} ({_steamTargets[keys[i]]})");
            }

            Console.WriteLine("0. Cancelar");
            Console.Write("Selecciona opción: ");

            var option = Console.ReadLine();

            if (option == "0")
                return;

            if (int.TryParse(option, out int selected) &&
                selected > 0 &&
                selected <= keys.Count)
            {
                var selectedKey = keys[selected - 1];
                _targetSteamId = _steamTargets[selectedKey];

                Console.WriteLine($"Ahora enviando mensajes a: {selectedKey}");
            }
            else
            {
                Console.WriteLine("Opción inválida.");
            }
        }
    }
}