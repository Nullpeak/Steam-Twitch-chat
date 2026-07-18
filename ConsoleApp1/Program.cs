using DotNetEnv;
using SteamKit2;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;
using System.Diagnostics;
using SteamKit2.Authentication;


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

        private static bool _chatPaused = false;
        private static void UpdateConsoleTitle()
        {
            string status = _chatPaused ? "CHAT PAUSADO" : "CHAT ACTIVO";
            Console.Title = $"Twitch-Steam Bridge | {status}";
        }



        static void Main(string[] args)
        {
            UpdateConsoleTitle();

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
            _steamCallbackManager.Subscribe<SteamFriends.ChatMsgCallback>(OnSteamChatMessage);
            Console.WriteLine("Callback OnSteamChatMessage suscrito correctamente.");


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

            if (!_chatPaused && !string.IsNullOrEmpty(msg))
            {
                // Iniciar el cronómetro
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    SendSteamMessage($"{e.ChatMessage.DisplayName}: {msg}");
                }
                finally
                {
                    // Detener el cronómetro y mostrar el tiempo
                    stopwatch.Stop();
                    Console.WriteLine(
                        $"[Tiempo] Mensaje procesado en {stopwatch.ElapsedMilliseconds} ms " +
                        $"({stopwatch.Elapsed.TotalSeconds:F3} segundos)"
                    );
                }
            }
        }



        // ======================================
        // ENVIAR A STEAM
        // ======================================
        private static void SendSteamMessage(string message)
        {
            if (!_steamLoggedIn || _steamFriends == null)
                return;
            if (_targetSteamId == 0)
            {
                Console.WriteLine("No se ha seleccionado un objetivo de Steam.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
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
            finally
            {
                stopwatch.Stop();
                Console.WriteLine(
                    $"[Tiempo Envío] Mensaje enviado a Steam en {stopwatch.ElapsedMilliseconds} ms " +
                    $"({stopwatch.Elapsed.TotalSeconds:F3} segundos)"
                );
            }
        }

        // Enviar mensaje directo a un SteamID específico (útil para respuestas a quien envía el comando)
        private static void SendSteamMessageTo(ulong steamId, string message)
        {
            if (!_steamLoggedIn || _steamFriends == null)
                return;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var sid = new SteamID(steamId);
                _steamFriends.SendChatMessage(sid, EChatEntryType.ChatMsg, message);
                Console.WriteLine($"[Steam->{steamId}] {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error enviando mensaje: " + ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                Console.WriteLine(
                    $"[Tiempo Envío] Mensaje enviado a Steam en {stopwatch.ElapsedMilliseconds} ms " +
                    $"({stopwatch.Elapsed.TotalSeconds:F3} segundos)"
                );
            }
        }


        // ======================================
        // STEAM CALLBACKS
        // ======================================
        private static async void OnSteamConnected(SteamClient.ConnectedCallback callback)
        {
            Console.WriteLine("Conectado a Steam, iniciando sesión...");
            await DoSteamLoginAsync();
        }

        private static async Task DoSteamLoginAsync()
        {
            try
            {
                var username = Env.GetString("STEAM_USERNAME") ?? "";
                var password = Env.GetString("STEAM_PASSWORD") ?? "";
                var savedRefreshToken = Env.GetString("STEAM_REFRESH_TOKEN") ?? "";

                // Si ya tenemos un refresh token guardado, lo usamos directo (sin pedir 2FA de nuevo)
                if (!string.IsNullOrEmpty(savedRefreshToken))
                {
                    _steamUser?.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = username,
                        AccessToken = savedRefreshToken,
                        ShouldRememberPassword = true,
                    });
                    return;
                }

                // Login "seguro" vía sesión de autenticación (reemplaza al Password directo)
                var authSession = await _steamClient!.Authentication.BeginAuthSessionViaCredentialsAsync(
                    new AuthSessionDetails
                    {
                        Username = username,
                        Password = password,
                        IsPersistentSession = true,
                        Authenticator = new UserConsoleAuthenticator(), // pide el código 2FA/email por consola si hace falta
                    });

                var pollResponse = await authSession.PollingWaitForResultAsync();

                // Guardamos el refresh token para no tener que pasar por 2FA cada vez que arranque el bot
                UpdateEnvFile("STEAM_REFRESH_TOKEN", pollResponse.RefreshToken);

                _steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken,
                    ShouldRememberPassword = true,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error autenticando con Steam: " + ex.Message);
            }
        }

        private static void OnSteamLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                _steamLoggedIn = true;
                Console.WriteLine("Logueado en Steam correctamente.");
                return;
            }

            Console.WriteLine($"Fallo de login. Result: {callback.Result} / ExtendedResult: {callback.ExtendedResult}");

            if (callback.Result == EResult.InvalidPassword || callback.Result == EResult.Expired
                || callback.Result == EResult.AccessDenied)
            {
                Console.WriteLine("El refresh token guardado ya no sirve, se borra y se reintenta con usuario/contraseña.");
                UpdateEnvFile("STEAM_REFRESH_TOKEN", "");
                _ = DoSteamLoginAsync();
            }
        }

        private static void OnSteamDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _steamLoggedIn = false;
            Console.WriteLine("Desconectado de Steam.");
        }

        private static void OnSteamLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            _steamLoggedIn = false;
        }

        private static void SteamLoop(CancellationToken token)
        {
            Console.WriteLine("SteamLoop iniciado. Esperando callbacks...");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _steamCallbackManager?.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error en RunWaitCallbacks: {ex.Message}");
                }
                Thread.Sleep(100);
            }
        }

        private static void OnSteamChatMessage(SteamFriends.ChatMsgCallback callback)
        {
            ulong senderId = callback.ChatterID.ConvertToUInt64();
            string message = callback.Message ?? "";

            Console.WriteLine("=================================");
            Console.WriteLine("[Steam] Mensaje recibido:");
            Console.WriteLine($"Remitente: {senderId}");
            Console.WriteLine($"Mensaje:   {message}");
            Console.WriteLine($"Tipo:      {callback.ChatMsgType}");
            Console.WriteLine("=================================");

            // Solo procesar comandos (mensajes que empiezan con "!")
            if (callback.ChatMsgType == EChatEntryType.ChatMsg && message.StartsWith("!"))
            {
                // Verificar si el remitente tiene permisos (es el target o está en la lista)
                bool allowed = senderId == _targetSteamId || _steamTargets.ContainsValue(senderId);

                if (!allowed)
                {
                    SendSteamMessageTo(senderId, "No tienes permisos para usar comandos.");
                    return;
                }

                // Procesar el comando
                ProcessSteamCommand(callback.ChatterID, message);
            }
            // Ignorar mensajes que no son comandos
        }


        private static void ProcessSteamCommand(SteamID sender, string message)
        {
            var senderId = sender.ConvertToUInt64();

            // Permitir comandos desde la cuenta objetivo actual o cualquier cuenta listada en _steamTargets
            bool allowed = senderId == _targetSteamId;
            if (!allowed)
            {
                SendSteamMessageTo(senderId, "No tienes permisos para usar comandos.");
                return;
            }

            // Procesar comandos (sensible a prefijos)
            message = (message ?? string.Empty).Trim();
            if (message.Length == 0)
                return;

            var parts = message.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            if (cmd == "!pause")
            {
                _chatPaused = true;
                UpdateConsoleTitle();
                SendSteamMessageTo(senderId, "Chat pausado. No se enviarán mensajes a Steam.");
            }
            else if (cmd == "!resume")
            {
                _chatPaused = false;
                UpdateConsoleTitle();
                SendSteamMessageTo(senderId, "Chat reanudado. Los mensajes se enviarán a Steam.");
            }
            else if (cmd == "!steamlist")
            {
                string response = "=== CUENTAS DISPONIBLES ===";
                foreach (var kv in _steamTargets)
                    response += $"\n{kv.Key} → {kv.Value}";
                SendSteamMessageTo(senderId, response);
            }
            else if (cmd == "!set")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    SendSteamMessageTo(senderId, "Uso: !set <alias> - Cambia la cuenta objetivo para enviar mensajes.");
                }
                else
                {
                    if (_steamTargets.TryGetValue(arg, out ulong newId))
                    {
                        _targetSteamId = newId;
                        SendSteamMessageTo(senderId, $"Cuenta objetivo cambiada a: {arg} ({newId})");
                    }
                    else if (ulong.TryParse(arg, out ulong parsedId))
                    {
                        _targetSteamId = parsedId;
                        SendSteamMessageTo(senderId, $"Cuenta objetivo cambiada a: {parsedId}");
                    }
                    else
                    {
                        SendSteamMessageTo(senderId, "Alias no encontrado.");
                    }
                }
            }
            else if (cmd == "!twitch")
            {
                // Subcomando: say <mensaje>
                if (arg.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
                {
                    var toSay = arg.Substring(4).Trim();
                    var channel = Env.GetString("TWITCH_CHANNEL") ?? string.Empty;
                    if (!string.IsNullOrEmpty(toSay) && _twitchClient != null && _twitchClient.IsConnected)
                    {
                        _twitchClient.SendMessage(channel, toSay);
                        SendSteamMessageTo(senderId, "Mensaje enviado al canal de Twitch.");
                    }
                    else
                    {
                        SendSteamMessageTo(senderId, "No se pudo enviar el mensaje a Twitch (¿conectado?).");
                    }
                }
                else
                {
                    SendSteamMessageTo(senderId, "Uso: !twitch say <mensaje>");
                }
            }
            else if (cmd == "!help")
            {
                string helpMessage =
                    "=== COMANDOS DISPONIBLES ===\n" +
                    "!pause      - Pausa el envío de mensajes a Steam.\n" +
                    "!resume     - Reanuda el envío de mensajes a Steam.\n" +
                    "!steamlist  - Lista las cuentas Steam disponibles.\n" +
                    "!set <alias|id> - Cambia la cuenta objetivo para enviar mensajes.\n" +
                    "!twitch say <msg> - Envía un mensaje al canal de Twitch.\n" +
                    "!help       - Muestra esta ayuda.";
                SendSteamMessageTo(senderId, helpMessage);
            }
            else
            {
                SendSteamMessageTo(senderId, $"Comando desconocido. Usa !help para ver la lista de comandos.");
            }
        }


        private static void ConsoleCommandLoop()
        {
            while (true)
            {
                if (Console.KeyAvailable) // Solo lee si hay una tecla disponible (evita bloquear)
                {
                    var input = Console.ReadLine(); // Espera a que presiones Enter
                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    input = input.Trim().ToLower();

                    // Comando: exit
                    if (input == "exit")
                    {
                        Console.WriteLine("Cerrando la aplicación...");
                        _cts?.Cancel();
                        _twitchClient?.Disconnect();
                        _steamClient?.Disconnect();
                        Environment.Exit(0);
                    }
                    // Comando: pause
                    else if (input == "pause")
                    {
                        _chatPaused = true;
                        UpdateConsoleTitle();
                        Console.WriteLine("Chat pausado. No se enviarán mensajes a Steam.");
                    }
                    // Comando: resume
                    else if (input == "resume")
                    {
                        _chatPaused = false;
                        UpdateConsoleTitle();
                        Console.WriteLine("Chat reanudado. Los mensajes se enviarán a Steam.");
                    }
                    // Comando: help
                    else if (input == "help")
                    {
                        Console.WriteLine("\n=== COMANDOS DISPONIBLES ===");
                        Console.WriteLine("exit       - Cierra la aplicación.");
                        Console.WriteLine("help       - Muestra esta ayuda.");
                        Console.WriteLine("title <t>  - Cambia el título de la consola.");
                        Console.WriteLine("steam      - Muestra el menú de selección de cuentas Steam.");
                        Console.WriteLine("steamlist  - Lista las cuentas Steam disponibles.");
                        Console.WriteLine("steamreload- Recarga las cuentas Steam desde el archivo .env.");
                        Console.WriteLine("pause      - Pausa el envío de mensajes a Steam.");
                        Console.WriteLine("resume     - Reanuda el envío de mensajes a Steam.");
                    }
                    // Comando: title
                    else if (input.StartsWith("title "))
                    {
                        var newTitle = input.Substring(6).Trim();
                        if (!string.IsNullOrEmpty(newTitle))
                        {
                            string status = _chatPaused ? " (CHAT PAUSADO)" : " (CHAT ACTIVO)";
                            Console.Title = $"{newTitle}{status}";
                            Console.WriteLine($"Título cambiado a: {newTitle}{status}");
                        }
                        else
                        {
                            Console.WriteLine("Uso: title <nuevo_título>");
                        }
                    }
                    // Comando: steam
                    else if (input == "steam")
                    {
                        ShowSteamSelectionMenu();
                        continue;
                    }
                    // Comando: steamlist
                    else if (input == "steamlist")
                    {
                        Console.WriteLine("=== CUENTAS DISPONIBLES ===");
                        foreach (var kv in _steamTargets)
                            Console.WriteLine($"{kv.Key} → {kv.Value}");
                        continue;
                    }
                    // Comando: steamreload
                    else if (input == "steamreload")
                    {
                        LoadSteamTargetsFromEnv();
                        Console.WriteLine("Cuentas recargadas desde .env");
                        continue;
                    }
                    else
                    {
                        Console.WriteLine("Comando desconocido. Usa 'help' para ver la lista de comandos.");
                    }
                }
                else
                {
                    Thread.Sleep(100); // Pequeña pausa para no saturar la CPU
                }
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