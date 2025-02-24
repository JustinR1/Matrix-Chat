using System;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows; // Ensure this is the Windows namespace
using System.IO;
using Loading.Models;
using System.Linq; // Add this if not already present

namespace Loading.Services
{
    public class ChatService : IChatService
    {
        private TcpClient? client;
        private NetworkStream? stream;
        private StringBuilder messageBuffer = new();
        private readonly Random _random = new();
        private DateTime _lastPingTime;
        private int _currentPing = 0;
        private bool _isAutoReconnectEnabled = true;
        private const int BaseReconnectDelay = 400;
        private const int ReconnectScatter = 150;
        private const string serverAddress = "war.pianka.io";
        private const int serverPort = 6112;

        public bool IsConnected => client?.Connected ?? false;
        public ObservableCollection<ChatUser> Users { get; } = new();
        public int CurrentPing => _currentPing;

        public async Task ConnectToServerAsync(string username, string password, bool isReconnect = false)
        {
            try
            {
                if (isReconnect)
                {
                    await DisconnectAsync();
                }

                client = new TcpClient
                {
                    ReceiveTimeout = 10000,
                    SendTimeout = 10000
                };

                await client.ConnectAsync(serverAddress, serverPort);
                stream = client.GetStream();
                ListenForMessages();

                SendLogin(username, password);
                LogMessage($"* Connected as {username}");
                RequestUserList();

                _ = KeepConnectionAlive();
            }
            catch (Exception ex)
            {
                LogMessage($"* Connection error: {ex.Message}", Colors.Red);

                if (_isAutoReconnectEnabled)
                {
                    await HandleReconnection(username, password);
                }
            }
        }

        private async Task HandleReconnection(string username, string password)
        {
            try
            {
                while (_isAutoReconnectEnabled)
                {
                    int delayMs = BaseReconnectDelay + _random.Next(ReconnectScatter);
                    LogMessage($"* Attempting to reconnect in {delayMs}ms...", Colors.Red);

                    await Task.Delay(delayMs);

                    try
                    {
                        await ConnectToServerAsync(username, password, true);
                        if (client?.Connected == true)
                        {
                            LogMessage("* Successfully reconnected", Colors.Red);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"* Reconnection attempt failed: {ex.Message}", Colors.Red);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"* Reconnection system error: {ex.Message}", Colors.Red);
            }
        }

        private void SendLogin(string username, string password)
        {
            string loginSequence = $"C1\nACCT {username}\nPASS {password}\nLOGIN\n";
            byte[] loginRequest = Encoding.UTF8.GetBytes(loginSequence);
            stream?.Write(loginRequest, 0, loginRequest.Length);
        }

        private void ListenForMessages()
        {
            Task.Run(async () =>
            {
                byte[] buffer = new byte[1024];
                try
                {
                    while (client?.Connected == true && stream != null)
                    {
                        int bytesRead = await stream.ReadAsync(buffer);
                        if (bytesRead > 0)
                        {
                            messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                            while (messageBuffer.ToString().Contains("\n"))
                            {
                                int messageEnd = messageBuffer.ToString().IndexOf("\n");
                                string completeMessage = messageBuffer.ToString()[..messageEnd].Trim();
                                messageBuffer.Remove(0, messageEnd + 1);

                                ProcessMessage(completeMessage);
                            }
                        }
                        else
                        {
                            if (_isAutoReconnectEnabled)
                            {
                                LogMessage("* Connection lost", Colors.Red);
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    LogMessage("* Connection to server lost", Colors.Red);
                }
            });
        }

        private void ProcessMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Handle PING messages silently
            if (message.StartsWith("PING"))
            {
                _lastPingTime = DateTime.Now;
                string[] parts = message.Split(' ');
                if (parts.Length > 1)
                {
                    SendPong(parts[1]);
                }
                return;
            }

            // Handle other message types
            if (message.Contains("WHISPER") || message.Contains("MSG FROM") || message.Contains("MSG TO"))
            {
                ProcessWhisperMessage(message);
            }
            else if (message.Contains("USER TALK"))
            {
                ProcessChatMessage(message);
            }
            else if (message.Contains("USER JOIN") || message.Contains("USER IN"))
            {
                ProcessUserJoin(message);
            }
            else if (message.Contains("USER LEAVE"))
            {
                ProcessUserLeave(message);
            }
            else
            {
                // Handle other system messages
                LogMessage(message);
            }
        }

        private void ProcessWhisperMessage(string message)
        {
            if (message.Contains("FROM"))
            {
                string[] parts = message.Split(new[] { "FROM" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    string[] messageParts = parts[1].Trim().Split(new[] { ' ' }, 2);
                    if (messageParts.Length >= 2)
                    {
                        string fromUser = messageParts[0];
                        string whisperMessage = messageParts[1];
                        LogWhisper($"From {fromUser}: {whisperMessage}");
                    }
                }
            }
            else if (message.Contains("TO") && !message.StartsWith("/msg"))
            {
                string[] parts = message.Split(new[] { "TO" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    string[] messageParts = parts[1].Trim().Split(new[] { ' ' }, 2);
                    if (messageParts.Length >= 2)
                    {
                        string toUser = messageParts[0];
                        string whisperMessage = messageParts[1];
                        LogWhisper($"To {toUser}: {whisperMessage}", Colors.LightCyan);
                    }
                }
            }
        }

        private void ProcessChatMessage(string message)
        {
            string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5)
            {
                string senderUsername;
                string chatMessage;

                if (message.Contains("USER TALK FROM"))
                {
                    senderUsername = parts[3];
                    chatMessage = string.Join(" ", parts[4..]);
                    LogMessage($"{senderUsername}: {chatMessage}");
                }
            }
        }

        private void ProcessUserJoin(string message)
        {
            string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5)
            {
                string flag = parts[2];
                string username = parts[4];

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrWhiteSpace(username) && !username.All(char.IsDigit))
                    {
                        var user = new ChatUser(username, flag, "");
                        if (!Users.Contains(user))
                        {
                            Users.Add(user);
                        }
                    }
                });

                if (message.StartsWith("USER JOIN"))
                {
                    LogMessage($"{username} joined");
                }
            }
        }

        private void ProcessUserLeave(string message)
        {
            string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                string leftUsername = parts[2];
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var userToRemove = Users.FirstOrDefault(u => u.Username == leftUsername);
                    if (userToRemove != null)
                    {
                        Users.Remove(userToRemove);
                        LogMessage($"{leftUsername} left");
                    }
                });
            }
        }

        public void RequestUserList()
        {
            try
            {
                if (stream != null && client != null && client.Connected)
                {
                    string whoCommand = "/who\n";
                    byte[] whoRequest = Encoding.UTF8.GetBytes(whoCommand);
                    stream.Write(whoRequest, 0, whoRequest.Length);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}");
            }
        }

        private void SendPong(string cookie)
        {
            try
            {
                string pongResponse = $"/PONG {cookie}\n";
                byte[] pongRequest = Encoding.UTF8.GetBytes(pongResponse);
                stream?.Write(pongRequest, 0, pongRequest.Length);

                TimeSpan pingTime = DateTime.Now - _lastPingTime;
                var pingMs = (int)pingTime.TotalMilliseconds;

                if (pingMs >= 0 && pingMs < 1000)
                {
                    _currentPing = pingMs;
                }
            }
            catch (Exception)
            {
                _currentPing = 999;
            }
        }

        public async Task SendChatMessage(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                try
                {
                    byte[] messageRequest = Encoding.UTF8.GetBytes(message + "\n");
                    await stream!.WriteAsync(messageRequest);
                }
                catch (Exception ex)
                {
                    LogMessage($"Error sending message: {ex.Message}");
                }
            }
        }

        private async Task KeepConnectionAlive()
        {
            while (client?.Connected == true)
            {
                try
                {
                    if (stream == null) break;

                    byte[] keepAliveMessage = Encoding.UTF8.GetBytes("/NULL\n");
                    await stream.WriteAsync(keepAliveMessage);
                    await Task.Delay(60000);
                }
                catch
                {
                    if (_isAutoReconnectEnabled)
                    {
                        break;
                    }
                }
            }
        }

        public async Task DisconnectAsync()
        {
            _isAutoReconnectEnabled = false;

            if (stream != null)
            {
                await stream.DisposeAsync();
                stream = null;
            }

            if (client != null)
            {
                client.Close();
                client = null;
            }

            LogMessage("* Disconnected", Colors.Red);
        }

        public void LogMessage(string message, Color? color = null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Implementation will be provided by the ViewModel
            });
        }

        public void LogWhisper(string message, Color? color = null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Implementation will be provided by the ViewModel
            });
        }
    }
}