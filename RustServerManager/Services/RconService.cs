using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Websocket.Client;

namespace RustServerManager.Services
{
    public class RconService
    {
        private WebsocketClient? _client;
        private Timer? _keepAliveTimer;

        public event Action<string>? OnLogMessage;
        public event Action<string>? OnPlayerListReceived;

        public async Task ConnectAsync(string ip, int port, string password)
        {
            // Если старое соединение висит, убиваем его
            _client?.Dispose();
            _keepAliveTimer?.Dispose();

            var url = new Uri($"ws://{ip}:{port}/{password}");
            _client = new WebsocketClient(url);

            // Включаем авто-переподключение, чтобы при обрыве связи программа сама её восстанавливала
            _client.IsReconnectionEnabled = true;
            _client.ReconnectTimeout = TimeSpan.FromSeconds(1);

            _client.MessageReceived.Subscribe(msg =>
            {
                if (string.IsNullOrWhiteSpace(msg.Text)) return;

                try
                {
                    var json = JsonConvert.DeserializeObject<dynamic>(msg.Text);
                    if (json == null) return;

                    string? message = json.Message?.ToString();
                    int? identifier = json.Identifier;

                    // Игнорируем ответы на наши системные пинги
                    if (identifier == 99) return;

                    if (identifier == 2)
                        OnPlayerListReceived?.Invoke(message ?? "");
                    else
                        OnLogMessage?.Invoke(message ?? "");
                }
                catch
                {
                    OnLogMessage?.Invoke(msg.Text ?? "");
                }
            });

            // 👇 МЫ ПОЛНОСТЬЮ УБРАЛИ ЛОГИ О ПЕРЕПОДКЛЮЧЕНИИ
            _client.ReconnectionHappened.Subscribe(_ => { });
            _client.DisconnectionHappened.Subscribe(_ => { });

            try
            {
                await _client.Start();
                // Пишем в лог только один раз при успешном коннекте
                OnLogMessage?.Invoke("[Система] Успешно подключено к RCON!");
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"[ОШИБКА] Не удалось подключиться: {ex.Message}");
            }

            // Таймер для поддержания связи (отправляет безобидный запрос)
            _keepAliveTimer = new Timer(_ =>
            {
                if (_client != null && _client.IsRunning)
                {
                    var payload = new { Identifier = 99, Message = "server.info", Name = "WebRcon" };
                    _client.Send(JsonConvert.SerializeObject(payload));
                }
            }, null!, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        public void SendCommand(string command)
        {
            SendCommand(1, command);
        }

        public void RequestPlayerList()
        {
            SendCommand(2, "playerlist");
        }

        private void SendCommand(int id, string command)
        {
            if (_client == null || !_client.IsRunning) return;

            var payload = new { Identifier = id, Message = command, Name = "WebRcon" };
            _client.Send(JsonConvert.SerializeObject(payload));
        }

        public void Disconnect()
        {
            _keepAliveTimer?.Dispose();
            _client?.Dispose();
        }
    }
}
