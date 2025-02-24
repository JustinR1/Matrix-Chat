using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Linq;
using System.Windows.Controls;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace Loading
{
    public partial class MainWindow : Window
    {
        private TcpClient? client;
        private NetworkStream? stream;
        private StringBuilder messageBuffer = new StringBuilder();
        private const string serverAddress = "war.pianka.io";
        private const int serverPort = 6112;
        private string? username;
        private string? password;
        private string homeChannel;  // Non-nullable
        private CancellationTokenSource? _reconnectCancellation;
        private bool _isAutoReconnectEnabled = true;
        private const int BaseReconnectDelay = 400; // Base delay in milliseconds
        private const int ReconnectScatter = 200;   // Maximum random additional delay
        private readonly Random _random = new Random();
        private DateTime _lastPingTime;
        private int _currentPing = 0;
        private string _serverAddress;
        private int _serverPort;
        private int _baseReconnectDelay;
        private int _reconnectScatter;
        private readonly string _configPath;
        private string _commandTrigger;
        private string _masterUsername;
        private bool _isReconnecting = false;  // Flag to track reconnecting status
        private bool isBanAllActive = false;  // Flag to check if the ban all command was activated
        private readonly Queue<string> messageQueue = new Queue<string>();
        private readonly object messageLock = new object();
        private bool isProcessingMessages = false;
        private const int MAX_CHAT_LINES = 500; // Adjust this number as needed
        private int currentChatLines = 0;
        private bool _isRejoining = false;
        private bool isClearingChat = false;
        private string _homeChannel = string.Empty;
        private DateTime _lastDataReceived = DateTime.Now;
        private const int CONNECTION_TIMEOUT_SECONDS = 90; // Adjust as needed
        private bool _isConnectionMonitorRunning = false;
        private System.Windows.Threading.DispatcherTimer _matrixTimer;
        private readonly Random _matrixRandom = new Random();
        private readonly List<MatrixColumn> _matrixColumns = new List<MatrixColumn>();
        private readonly List<MatrixColumn> _matrixColumns2 = new List<MatrixColumn>();
        private const int MatrixColumnCount = 8;
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 50;
        private readonly List<MatrixColumn> _backgroundMatrixColumns = new List<MatrixColumn>();
        private const int BackgroundMatrixColumnCount = 24; // More columns for the background

        public MainWindow()
        {
            InitializeComponent();

            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            LoadConfiguration();
            InitializeMatrixEffect();




            // Ensure non-nullable fields are initialized
            homeChannel = homeChannel ?? "#Home";
            _serverAddress = _serverAddress ?? "war.pianka.io";
            _commandTrigger = _commandTrigger ?? "!";
            _masterUsername = _masterUsername ?? username ?? string.Empty;

            // Ensure username and password are loaded before attempting to connect
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                // Connect to the server asynchronously
                _ = ConnectToServerAsync(username, password);
            }
            else
            {
                LogMessage("Username or password is missing. Please check the configuration.", Colors.Red);
            }
        }

        private class MatrixColumn
        {
            public List<TextBlock> Characters { get; } = new List<TextBlock>();
            public double Speed { get; set; }
            public double Position { get; set; }
            public int Length { get; set; }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CleanupMatrixEffect();
        }

        private void InitializeMatrixEffect()
        {
            // Create columns for regular matrix (keeping for backward compatibility)
            for (int i = 0; i < MatrixColumnCount; i++)
            {
                var column = new MatrixColumn
                {
                    Speed = _matrixRandom.NextDouble() * 4 + 3, // Increased speed from 3+2 to 4+3
                    Position = _matrixRandom.NextDouble() * -150,
                    Length = _matrixRandom.Next(8, 20)
                };
                _matrixColumns.Add(column);
            }

            // Create columns for background matrix (more columns, wider spread)
            for (int i = 0; i < BackgroundMatrixColumnCount; i++)
            {
                var column = new MatrixColumn
                {
                    Speed = _matrixRandom.NextDouble() * 3 + 2,  // Increased speed from 2+1 to 3+2
                    Position = _matrixRandom.NextDouble() * -200,
                    Length = _matrixRandom.Next(8, 30)  // Longer trails for background
                };
                _backgroundMatrixColumns.Add(column);
            }

            // Set up the timer for animation with high priority
            _matrixTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(30) // Changed from 60ms to 40ms for faster animation
            };
            _matrixTimer.Tick += MatrixTimer_Tick;
            _matrixTimer.Start();
        }

        private void MatrixTimer_Tick(object sender, EventArgs e)
        {
            // Use BeginInvoke to avoid blocking the UI thread with low priority 
            // Changed from Background to ContextIdle for smoother animation
            Dispatcher.BeginInvoke(() => {
                // Update the regular matrix (mostly for backward compatibility)
                if (MatrixCanvas.Visibility == Visibility.Visible)
                {
                    UpdateMatrixCanvas(MatrixCanvas, _matrixColumns);
                }

                // Update the background matrix
                if (BackgroundMatrixCanvas != null)
                {
                    UpdateBackgroundMatrixCanvas(BackgroundMatrixCanvas, _backgroundMatrixColumns);
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void UpdateBackgroundMatrixCanvas(Canvas canvas, List<MatrixColumn> columns)
        {
            if (canvas.ActualWidth < 1 || canvas.ActualHeight < 1)
                return; // Don't update if canvas isn't properly sized yet

            // Matrix character set
            char[] matrixChars = "ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ1234567890".ToCharArray();

            // Cache dimensions
            double canvasWidth = canvas.ActualWidth;
            double canvasHeight = canvas.ActualHeight;

            // Cap the maximum number of characters per column
            int maxCharsPerColumn = 30; // Reduced from 40 to 30 for better performance

            // Process columns evenly across the canvas width
            int columnsToProcess = Math.Min(12, columns.Count); // Process more columns per frame
            int step = Math.Max(1, columns.Count / columnsToProcess);

            // Process evenly distributed columns
            for (int i = 0; i < columns.Count; i += step)
            {
                var column = columns[i];
                double x = i * (canvasWidth / columns.Count);

                // Update position with consistent speed
                column.Position += column.Speed;

                // Reset column if it's gone too far
                if (column.Position > canvasHeight + 50)
                {
                    column.Position = _matrixRandom.NextDouble() * -150;
                    column.Speed = _matrixRandom.NextDouble() * 3 + 2;
                    column.Length = _matrixRandom.Next(8, 25); // Slightly reduced max length

                    // Remove all characters in this column
                    foreach (var charBlock in column.Characters)
                    {
                        canvas.Children.Remove(charBlock);
                    }
                    column.Characters.Clear();
                }

                // Add new character at top with more controlled frequency
                if (column.Characters.Count < column.Length &&
                    column.Characters.Count < maxCharsPerColumn &&
                    _matrixRandom.NextDouble() < 0.35) // Increased probability for more consistent character addition
                {
                    TextBlock textBlock = new TextBlock
                    {
                        Text = matrixChars[_matrixRandom.Next(matrixChars.Length)].ToString(),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromArgb(100, 0, 255, 0)),
                        TextAlignment = TextAlignment.Center,
                        Opacity = 0.6
                    };

                    // Make first character brighter
                    if (column.Characters.Count == 0)
                    {
                        textBlock.Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
                        textBlock.Opacity = 0.8;
                    }

                    Canvas.SetLeft(textBlock, x);
                    Canvas.SetTop(textBlock, column.Position - 15);
                    canvas.Children.Add(textBlock);
                    column.Characters.Add(textBlock);
                }

                // Update positions of existing characters with more efficient loop
                for (int j = 0; j < column.Characters.Count; j++)
                {
                    var textBlock = column.Characters[j];
                    double yPos = column.Position - (j * 15);

                    // Set position
                    Canvas.SetTop(textBlock, yPos);

                    // Remove characters that have gone too far down
                    if (yPos > canvasHeight + 30)
                    {
                        canvas.Children.Remove(textBlock);
                        column.Characters.RemoveAt(j);
                        j--;
                    }
                }
            }

            // More efficient cleanup strategy
            if (_matrixRandom.NextDouble() < 0.05 && canvas.Children.Count > 350)
            {
                int elementsToRemove = canvas.Children.Count - 350;
                for (int i = 0; i < Math.Min(elementsToRemove, 25); i++)
                {
                    if (canvas.Children.Count > 0)
                        canvas.Children.RemoveAt(0);
                }
            }
        }

        private void UpdateMatrixCanvas(Canvas canvas, List<MatrixColumn> columns)
        {
            if (canvas.ActualWidth < 1 || canvas.ActualHeight < 1)
                return; // Don't update if canvas isn't properly sized yet

            // Matrix character set (extended for authenticity)
            char[] matrixChars = "ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ1234567890".ToCharArray();

            // Use a cached width to avoid multiple calls to ActualWidth
            double canvasWidth = canvas.ActualWidth;
            double canvasHeight = canvas.ActualHeight;

            // Cap the maximum number of characters per column to improve performance
            int maxCharsPerColumn = 30;

            // Process a limited number of columns per frame if necessary
            int columnsToProcess = columns.Count;
            if (columnsToProcess > 6 && canvas.Children.Count > 200)
            {
                columnsToProcess = 6; // Process fewer columns when there are many elements
            }

            // Update each column
            for (int i = 0; i < columnsToProcess; i++)
            {
                var column = columns[i];
                double x = i * (canvasWidth / columns.Count) + 2;

                // Update position
                column.Position += column.Speed;

                // Reset column if it's gone too far
                if (column.Position > canvasHeight + 50)
                {
                    column.Position = _matrixRandom.NextDouble() * -150;
                    column.Speed = _matrixRandom.NextDouble() * 3 + 2;
                    column.Length = _matrixRandom.Next(8, 20);

                    // Remove all characters in this column
                    foreach (var charBlock in column.Characters)
                    {
                        canvas.Children.Remove(charBlock);
                    }
                    column.Characters.Clear();
                }

                // Add new character at top if needed with throttling
                if (column.Characters.Count < column.Length &&
                    column.Characters.Count < maxCharsPerColumn &&
                    _matrixRandom.NextDouble() < 0.35)
                {
                    TextBlock textBlock = new TextBlock
                    {
                        Text = matrixChars[_matrixRandom.Next(matrixChars.Length)].ToString(),
                        Style = (Style)FindResource("MatrixTextBlock")
                    };

                    // Make first character brighter
                    if (column.Characters.Count == 0)
                    {
                        textBlock.Foreground = new SolidColorBrush(Colors.White);
                        textBlock.Opacity = 1.0;
                    }

                    Canvas.SetLeft(textBlock, x);
                    Canvas.SetTop(textBlock, column.Position - 12);
                    canvas.Children.Add(textBlock);
                    column.Characters.Add(textBlock);
                }

                // Update positions of existing characters
                for (int j = 0; j < column.Characters.Count; j++)
                {
                    var textBlock = column.Characters[j];
                    double yPos = column.Position - (j * 12);

                    // Randomly change some characters (except the first one) with reduced frequency
                    if (j > 0 && _matrixRandom.NextDouble() < 0.03)
                    {
                        textBlock.Text = matrixChars[_matrixRandom.Next(matrixChars.Length)].ToString();
                    }

                    // Set position
                    Canvas.SetTop(textBlock, yPos);

                    // Remove characters that have gone too far down
                    if (yPos > canvasHeight + 30)
                    {
                        canvas.Children.Remove(textBlock);
                        column.Characters.RemoveAt(j);
                        j--;
                    }
                }
            }

            // Occasionally clean up excess elements to prevent memory issues
            if (_matrixRandom.NextDouble() < 0.05 && canvas.Children.Count > 300)
            {
                int elementsToRemove = canvas.Children.Count - 300;
                for (int i = 0; i < Math.Min(elementsToRemove, 20); i++)
                {
                    if (canvas.Children.Count > 0)
                        canvas.Children.RemoveAt(0);
                }
            }
        }


        // Updated cleanup method
        private void CleanupMatrixEffect()
        {
            if (_matrixTimer != null)
            {
                _matrixTimer.Stop();
                _matrixTimer = null;
            }

            if (MatrixCanvas != null && MatrixCanvas.Visibility == Visibility.Visible)
            {
                MatrixCanvas.Children.Clear();
            }

            if (BackgroundMatrixCanvas != null)
            {
                BackgroundMatrixCanvas.Children.Clear();
            }

            if (MatrixCanvas2 != null && MatrixCanvas2.Visibility == Visibility.Visible)
            {
                MatrixCanvas2.Children.Clear();
            }

            _matrixColumns.Clear();
            _matrixColumns2.Clear();
            _backgroundMatrixColumns.Clear();

            // Force garbage collection
            GC.Collect();
        }




        private List<string> safelistPatterns = new List<string>
        {
            "Loading",    // Example: Add exact usernames
            "*-SyN-*",     // Example: Add wildcard patterns
            "*pianka*",
            "islanti",
            "winsock",
            "*glyph*"
        };





        private bool IsUserOnSafelist(string username)
        {
            foreach (var pattern in safelistPatterns)
            {
                if (pattern.Contains("*"))
                {
                    var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                    if (Regex.IsMatch(username, regexPattern, RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                else
                {
                    if (string.Equals(username, pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Add this method to monitor the connection
        private async Task MonitorConnectionAsync()
        {
            _isConnectionMonitorRunning = true;

            while (_isConnectionMonitorRunning && _isAutoReconnectEnabled)
            {
                try
                {
                    if (IsConnected())
                    {
                        TimeSpan timeSinceLastData = DateTime.Now - _lastDataReceived;

                        if (timeSinceLastData.TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
                        {
                            LogMessage("* Connection timeout detected - No data received recently *", Colors.Red);
                            await HandleReconnection();
                            // Reset the timer after initiating reconnection
                            _lastDataReceived = DateTime.Now;
                        }
                    }

                    await Task.Delay(5000); // Check every 5 seconds
                }
                catch (Exception ex)
                {
                    LogMessage($"Connection monitor error: {ex.Message}", Colors.Red);
                    await Task.Delay(5000);
                }
            }

            _isConnectionMonitorRunning = false;
        }

        private async Task ConnectToServerAsync(string username, string password, bool isReconnect = false)
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

                await client.ConnectAsync(_serverAddress, _serverPort);
                stream = client.GetStream();

                // Start the connection monitor if it's not already running
                if (!_isConnectionMonitorRunning)
                {
                    _ = MonitorConnectionAsync();
                }

                _ = ListenForMessages();
                await SendLoginAsync(username, password, homeChannel);
                LogMessage($"* Connected as {username}");
                await RequestUserListAsync();

                _ = KeepConnectionAlive();

                // Reset the last data received timestamp
                _lastDataReceived = DateTime.Now;
            }
            catch (Exception ex)
            {
                LogMessage($"* Connection error: {ex.Message}");

                if (_isAutoReconnectEnabled)
                {
                    await HandleReconnection();
                }
            }
        }

        private async Task SendLoginAsync(string username, string password, string homeChannel)
        {
            string loginSequence = $"C1\nACCT {username}\nPASS {password}\nHOME {homeChannel}\nLOGIN\n";
            byte[] loginRequest = Encoding.UTF8.GetBytes(loginSequence);
            await stream!.WriteAsync(loginRequest);
        }

        private async Task HandleReconnection()
        {
            try
            {
                _reconnectAttempts++;

                // If we've reached max attempts, add longer delays for the next attempts
                if (_reconnectAttempts >= MaxReconnectAttempts)
                {
                    // Reset counter and take a longer break
                    LogMessage($"* Maximum reconnection attempts reached ({MaxReconnectAttempts}). Taking a longer break... *", Colors.Red);
                    await Task.Delay(30000); // 30 second break before retrying
                    _reconnectAttempts = 0;

                    // Temporarily disable ban mode if it was active
                    if (isBanAllActive)
                    {
                        bool wasActive = isBanAllActive;
                        isBanAllActive = false;
                        LogMessage("* Ban mode temporarily disabled during reconnection *", Colors.Yellow);
                    }
                }

                // Calculate backoff delay - longer delays after more failures
                int baseDelay = BaseReconnectDelay;
                int backoffFactor = Math.Min(10, Math.Max(1, _reconnectAttempts / 5));
                int delayMs = baseDelay * backoffFactor + _random.Next(ReconnectScatter);

                LogMessage($"* Reconnection attempt {_reconnectAttempts}: Waiting {delayMs}ms... *", Colors.Red);

                // Clean up existing connection
                client?.Close();
                client = new TcpClient
                {
                    ReceiveTimeout = 10000,
                    SendTimeout = 10000
                };

                try
                {
                    await Task.Delay(delayMs, _reconnectCancellation?.Token ?? CancellationToken.None);

                    // Since we had disconnect issues, add some guard time between attempts
                    await Task.Delay(100);

                    // Connect and login
                    await client.ConnectAsync(serverAddress, serverPort);
                    stream = client.GetStream();

                    // Clear any pending message buffer
                    messageBuffer.Clear();

                    // Send login info
                    await SendLoginAsync(username!, password!, homeChannel);

                    // Start listening and keepalive
                    _ = ListenForMessages();
                    _ = KeepConnectionAlive();

                    if (client.Connected)
                    {
                        // Reset attempts on successful connection
                        _reconnectAttempts = 0;
                        LogMessage("* Successfully reconnected", Colors.Green);

                        // Wait before requesting user list
                        await Task.Delay(1000);
                        await RequestUserListAsync();

                        // Reset connection-related state
                        _isReconnecting = false;

                        // Return to avoid looping again
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    LogMessage("* Reconnection cancelled", Colors.Red);
                    return;
                }
                catch (Exception ex)
                {
                    LogMessage($"* Reconnection attempt {_reconnectAttempts} failed: {ex.Message}", Colors.Red);

                    // If we have connection issues, retry with HandleReconnection again after a short delay
                    if (_isAutoReconnectEnabled)
                    {
                        await Task.Delay(100);
                        await HandleReconnection();
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"* Reconnection system error: {ex.Message}", Colors.Red);
            }
        }

        private async Task ListenForMessages()
        {
            byte[] buffer = new byte[1024];

            try
            {
                while (IsConnected())
                {
                    int bytesRead = await stream!.ReadAsync(buffer);
                    if (bytesRead > 0)
                    {
                        messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                        while (messageBuffer.ToString().Contains("\n"))
                        {
                            int messageEnd = messageBuffer.ToString().IndexOf("\n");
                            string completeMessage = messageBuffer.ToString().Substring(0, messageEnd).Trim();
                            messageBuffer.Remove(0, messageEnd + 1);

                            lock (messageLock)
                            {
                                messageQueue.Enqueue(completeMessage);
                                if (!isProcessingMessages)
                                {
                                    isProcessingMessages = true;
                                    _ = ProcessMessageQueue();
                                }
                            }
                        }
                    }
                    else if (_isAutoReconnectEnabled)
                    {
                        LogMessage("* Connection lost", Colors.Red);
                        await HandleReconnection();
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                // Only log if auto-reconnect is enabled and we're not manually disconnecting
                if (_isAutoReconnectEnabled)
                {
                    LogMessage("* Connection error occurred", Colors.Red);
                    await HandleReconnection();
                }
            }
        }




        private async Task ProcessMessageQueue()
        {
            while (true)
            {
                string? message = null;
                lock (messageLock)
                {
                    if (messageQueue.Count > 0)
                    {
                        message = messageQueue.Dequeue();
                    }
                    else
                    {
                        isProcessingMessages = false;
                        return;
                    }
                }

                if (message != null)
                {
                    await OnMessageReceived(message);
                    await Task.Delay(10);
                }
            }
        }

        private async Task SendPongAsync(string cookie)
        {
            try
            {
                string pongResponse = $"/PONG {cookie}\n";
                byte[] pongRequest = Encoding.UTF8.GetBytes(pongResponse);
                await stream!.WriteAsync(pongRequest);

                TimeSpan pingTime = DateTime.Now - _lastPingTime;
                var pingMs = (int)pingTime.TotalMilliseconds;

                if (pingMs >= 0 && pingMs < 1000)
                {
                    UpdatePingDisplay(pingMs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SendPong: {ex.Message}");
                UpdatePingDisplay(999);
            }
        }

        private void UpdatePingDisplay(int pingMs)
        {
            _currentPing = pingMs;
            Dispatcher.Invoke(() =>
            {
                PingDisplay.Text = $"{_currentPing} ms";
                Color pingColor = _currentPing < 100 ? Colors.LimeGreen :
                                _currentPing < 200 ? Colors.Yellow :
                                Colors.Red;
                PingDisplay.Foreground = new SolidColorBrush(pingColor);
            });
        }

        private async Task UpdateUserList(string username, string flag, bool isJoining)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (UserList?.Items == null) return;

                    // Remove existing entry if present
                    var existing = UserList.Items.Cast<ChatUser>()
                        .FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        UserList.Items.Remove(existing);
                    }

                    if (isJoining)
                    {
                        var user = new ChatUser(username, flag, this.username ?? string.Empty);
                        UserList.Items.Add(user);
                    }

                    UserList.Items.Refresh();
                    UpdateChannelInfo();
                }
                catch (Exception ex)
                {
                    LogMessage($"Error updating user list: {ex.Message}", Colors.Red);
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        private async Task RequestUserListAsync()
        {
            try
            {
                if (!IsConnected())
                {
                    LogMessage("Cannot request user list - not connected", Colors.Red);
                    return;
                }
                
                string whoCommand = $"/who {homeChannel}\n";
                byte[] whoRequest = Encoding.UTF8.GetBytes(whoCommand);
                await stream!.WriteAsync(whoRequest);
        }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}");
            }
        }

        private async Task OnMessageReceived(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            _lastDataReceived = DateTime.Now;


            if (message.Contains("USER UPDATE"))
            {
                var parts = message.Split(new[] { ' ' });
                if (parts.Length >= 7)
                {
                    string newFlag = parts[4];
                    string updatedUsername = parts[6];

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var existingUser = UserList.Items.Cast<ChatUser>()
                            .FirstOrDefault(u => u.Username.Equals(updatedUsername, StringComparison.OrdinalIgnoreCase));

                        if (existingUser != null)
                        {
                            UserList.Items.Remove(existingUser);
                            var updatedUser = new ChatUser(updatedUsername, newFlag, username);
                            UserList.Items.Add(updatedUser);
                            UserList.Items.Refresh();
                        }
                    });
                }
                return;
            }

            if (message.StartsWith("PING"))
            {
                _lastPingTime = DateTime.Now;
                string[] parts = message.Split(' ');
                if (parts.Length > 1)
                {
                    await Task.Delay(1);
                    await SendPongAsync(parts[1]);
                }
                return;
            }

            if (message.StartsWith("MOTD") || message.StartsWith("Server") ||
                message.StartsWith("INIT") || message.Contains("Welcome to"))
            {
                LogMessage(message);
                return;
            }

            if (message.Contains("USER TALK"))
            {
                if (message.Contains($"USER TALK FROM {username}"))
                    return;

                string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    string senderUsername = parts[3];
                    string chatMessage = string.Join(" ", parts.Skip(4));

                    if (senderUsername == _masterUsername)
                    {
                        bool commandHandled = await HandleCommands(chatMessage, senderUsername);
                        if (commandHandled)
                            return;
                    }

                    LogMessage($"{senderUsername}: {chatMessage}");
                }
                return;
            }

            // Handle whisper messages
            if (message.Contains("WHISPER") || message.Contains("MSG FROM") || message.Contains("MSG TO"))
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
                return;
            }

            if (message.StartsWith("CHANNEL JOIN"))
            {
                string channelName = message.Substring("CHANNEL JOIN".Length).Trim();
                if (!string.IsNullOrEmpty(channelName))
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        UserList.Items.Clear();
                        ChannelInfo.Text = channelName;
                        LogMessage($"Joined channel: {channelName}");

                        // Add delay to allow user list to populate
                        await Task.Delay(1000);

                        // Check operator status and disable ban mode if needed
                        if (!IsCurrentUserOperator())
                        {
                            await DisableBanMode("No longer operator after channel join");
                        }
                    });
                }
                return;
            }

            if (message.StartsWith("SERVER INFO"))
            {
                string infoMessage = message.Substring("SERVER INFO".Length).Trim();
                LogMessage(infoMessage);
                return;
            }

            if (message.Contains("That channel does not exist") ||
                            message.Contains("(If you are trying to search for a user, use the /whois command.)"))
            {
                LogMessage(message, Colors.Red);
                return;
            }

            if (message.StartsWith("USER JOIN") || message.Contains("USER IN"))
            {
                string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    string flag = parts[2];
                    string joinedUsername = parts[4];

                    if (message.StartsWith("USER JOIN"))
                    {
                        LogMessage($"{joinedUsername} joined", Colors.Yellow);
                    }

                    try
                    {
                        // Skip numeric usernames and empty names
                        if (!string.IsNullOrWhiteSpace(joinedUsername) && !joinedUsername.All(char.IsDigit))
                        {
                            // Handle new joins - check for ban mode first
                            if (isBanAllActive && !_isRejoining && !IsUserOnSafelist(joinedUsername))
                            {
                                await SendChatResponse($"/ban {joinedUsername}");
                                LogMessage($"* Auto-banned new join: {joinedUsername} *", Colors.Yellow);
                                // Don't add banned users to the UserList
                                return; // Important: Return early to prevent adding to UserList
                            }
                            else  // Only add to list if not banned
                            {
                                await UpdateUserList(joinedUsername, flag, true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error processing join: {ex.Message}", Colors.Red);
                    }
                }
            }
        }

        private async Task ClearBannedUsersFromList()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                // Get all users that aren't on the safelist
                var usersToRemove = UserList.Items.Cast<ChatUser>()
                    .Where(u => !IsUserOnSafelist(u.Username) && u.Username != username)
                    .ToList();

                foreach (var user in usersToRemove)
                {
                    UserList.Items.Remove(user);
                }

                UserList.Items.Refresh();
                UpdateChannelInfo();

                LogMessage($"* Cleared {usersToRemove.Count} banned users from the list *", Colors.Yellow);
            });
        }



        private void UpdateChannelInfo()
        {
            Dispatcher.InvokeAsync(() =>
            {
                string currentChannel = ChannelInfo.Text.Split('(')[0].Trim();
                int userCount = UserList.Items.Count;
                ChannelInfo.Text = $"{currentChannel} ({userCount})";
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            await ConnectToServerAsync(username!, password!);
        }

        private async void UserInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                await SendChatMessage();
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            ShowHelpWindow();
        }


        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendChatMessage();
        }

        private async Task SendChatMessage()
        {
            string userMessage = UserInput.Text.Trim();
            if (!string.IsNullOrEmpty(userMessage))
            {
                try
                {
                    if (username != null && await HandleCommands(userMessage, username))
                    {
                        UserInput.Clear();
                        return;
                    }

                    // Send the message directly without any special flags
                    await SendChatResponse(userMessage);
                    UserInput.Clear();
                }
                catch (Exception ex)
                {
                    LogMessage($"Error sending message: {ex.Message}");
                }
            }
        }

        private async Task ReconnectToServerAsync()
        {
            try
            {
                // Forcibly disconnect first
                if (IsConnected())
                {
                    await DisconnectSilentlyAsync(); // This ensures a clean, silent second disconnect
                }

                // Reset reconnecting flags
                _isReconnecting = false;

                // Attempt to reconnect
                LogMessage("* Attempting to reconnect...", Colors.Yellow);
                await ConnectToServerAsync(username!, password!, true);
            }
            catch (Exception ex)
            {
                LogMessage($"ERROR: Could not reconnect to server. {ex.Message}", Colors.Red);

                // Ensure auto-reconnect is re-enabled if it fails
                _isAutoReconnectEnabled = true;
                _isReconnecting = false;
            }
        }

        private async Task DisconnectSilentlyAsync()
        {
            try
            {
                _isAutoReconnectEnabled = false;
                _isReconnecting = false;
                _isRejoining = false;
                isBanAllActive = false;

                if (_reconnectCancellation != null)
                {
                    _reconnectCancellation.Cancel();
                    _reconnectCancellation.Dispose();
                    _reconnectCancellation = null;
                }

                var localStream = stream;
                var localClient = client;

                stream = null;
                client = null;

                if (localStream != null)
                {
                    await localStream.DisposeAsync();
                }

                if (localClient != null)
                {
                    localClient.Close();
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    UserList.Items.Clear();
                    PingDisplay.Text = "-- ms";
                    PingDisplay.Foreground = new SolidColorBrush(Colors.LimeGreen);
                });

                // No log message here
            }
            catch (Exception ex)
            {
                // Silent error handling, or log only to console if needed
                Console.WriteLine($"Silent disconnect error: {ex.Message}");
            }
        }

        private async Task<bool> HandleCommands(string message, string senderUsername)
        {
            if (message.Equals("?trigger", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    string response = $"<Trigger is {_commandTrigger}>";
                    await SendChatResponse(response);
                    return true;
                }
            }

            if (message.Equals($"{_commandTrigger}reset", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    await ResetConnectionState();
                    return true;
                }
            }

            if (message.StartsWith($"{_commandTrigger}say ", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    string content = message.Substring($"{_commandTrigger}say ".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        await SendChatResponse(content);
                        return true;
                    }
                }
            }

            if (message.Equals($"{_commandTrigger}verify", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    await VerifyUserCount();
                    return true;
                }
            }

            if (message.StartsWith($"{_commandTrigger}opme", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    try
                    {
                        _isRejoining = true;
                        LogMessage("* Requesting operator status...", Colors.Yellow);

                        // Clear user list before rejoin
                        await Dispatcher.InvokeAsync(() => UserList.Items.Clear());

                        await SendChatResponse($"/designate {senderUsername}");
                        await Task.Delay(1000); // Longer delay before rejoin

                        LogMessage("* Rejoining channel...", Colors.Yellow);
                        await SendChatResponse("/rejoin");
                        await Task.Delay(2000); // Longer delay after rejoin

                        // Request fresh user list
                        await RequestUserListAsync();
                        return true;
                    }
                    finally
                    {
                        _isRejoining = false;
                    }
                }
            }

            if (message.Equals($"{_commandTrigger}rejoin", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    await SendChatResponse("/rejoin");
                    return true;
                }
            }

            // Command to set the home channel
            if (message.StartsWith($"{_commandTrigger}sethome ", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    string newHomeChannel = message.Substring($"{_commandTrigger}sethome ".Length).Trim();

                    if (!string.IsNullOrWhiteSpace(newHomeChannel))
                    {
                        // Update both variables
                        _homeChannel = newHomeChannel;
                        homeChannel = newHomeChannel;

                        // Save configuration immediately
                        SaveConfiguration();

                        await SendChatResponse($"Home channel set to: {_homeChannel}");

                        // Optionally join the new home channel immediately
                        await SendChatResponse($"/join {_homeChannel}");

                        return true;
                    }
                    else
                    {
                        await SendChatResponse("Please specify a valid channel name.");
                        return true;
                    }
                }
            }

            if (message.Equals($"{_commandTrigger}join home", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    if (!string.IsNullOrWhiteSpace(_homeChannel))
                    {
                        // Use SendChatResponse to send the join command
                        await SendChatResponse($"/join {_homeChannel}");
                        await SendChatResponse($"Joining home channel: {_homeChannel}");
                        return true;
                    }
                    else
                    {
                        await SendChatResponse("No home channel has been set. Use !sethome <channel> first.");
                        return true;
                    }
                }
            }

            if (message.StartsWith($"{_commandTrigger}join ", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    // Extract the channel name (trim to remove any leading/trailing whitespace)
                    string channelToJoin = message.Substring($"{_commandTrigger}join ".Length).Trim();

                    if (!string.IsNullOrWhiteSpace(channelToJoin))
                    {
                        // Send the join command for the specified channel
                        await SendChatResponse($"/join {channelToJoin}");
                        await SendChatResponse($"Joining channel: {channelToJoin}");
                        return true;
                    }
                    else
                    {
                        await SendChatResponse("Please specify a channel name to join.");
                        return true;
                    }
                }
            }



            if (message.Equals($"{_commandTrigger}ban *", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    isBanAllActive = !isBanAllActive;

                    if (isBanAllActive)
                    {
                        LogMessage("* Mass ban mode activated - banning current users and new joins *", Colors.Red);
                        await SendChatResponse("* Mass ban mode is now ACTIVE *");
                        // Set a flag to indicate we're doing the initial mass ban
                        _isRejoining = true;
                        // Use a separate method for mass banning current users
                        _ = PerformMassBanAsync();
                    }
                    else
                    {
                        LogMessage("* Mass ban mode deactivated - new users can join normally *", Colors.Green);
                        await SendChatResponse("* Mass ban mode is now INACTIVE *");
                    }

                    return true;
                }
            }

            //if (message.Equals("?help", StringComparison.OrdinalIgnoreCase))
            //{
            //    if (senderUsername == _masterUsername)
            //    {
            //        // Create an array of help messages
            //        string[] helpMessages = new string[]
            //        {
            //"Available Commands:",
            //$"{_commandTrigger}ban <username> - Bans a specific user",
            //$"{_commandTrigger}ban * - Toggles mass ban mode (bans all users except safelist)",
            //$"{_commandTrigger}unban * - Deactivates mass ban mode",
            //$"{_commandTrigger}banstatus - Shows if mass ban mode is active",
            //$"{_commandTrigger}opme - Requests operator status and rejoins",
            //$"{_commandTrigger}rejoin - Rejoins the current channel",
            //$"{_commandTrigger}say <message> - Makes the bot say something",
            //$"{_commandTrigger}settrigger <new_trigger> - Changes the command trigger",
            //$"{_commandTrigger}reconnect - Forces a reconnection to the server",
            //$"{_commandTrigger}ping - Shows current ping to server",
            //$"{_commandTrigger}quit - Safely shuts down the application",
            //"?trigger - Shows current command trigger",
            //"?help - Shows this help message"
            //        };

            //        // Send messages with a deliberate delay between each message to prevent flood
            //        foreach (var helpMessage in helpMessages)
            //        {
            //            await SendChatResponse(helpMessage);
            //            await Task.Delay(500); // 500ms delay between messages to prevent flood disconnect
            //        }

            //        return true;
            //    }
            //}

            if (message.Equals($"{_commandTrigger}quit", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    LogMessage("* Shutting down application... *", Colors.Yellow);
                    await DisconnectAsync();
                    await Task.Delay(500); // Brief delay to allow clean disconnect
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                    return true;
                }
            }

            if (message.StartsWith($"{_commandTrigger}ban ", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    string usernameToBan = message.Substring($"{_commandTrigger}ban ".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(usernameToBan))
                    {
                        await SendChatResponse($"/ban {usernameToBan}");
                        return true;
                    }
                    else
                    {
                        await SendChatResponse("Please provide a username to ban.");
                        return false;
                    }
                }
            }

            if (message.Equals($"{_commandTrigger}unban *", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    isBanAllActive = false;
                    LogMessage("* Mass ban mode deactivated *", Colors.Green);
                    return true;
                }
            }

            if (message.Equals($"{_commandTrigger}banstatus", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    string status = isBanAllActive ? "ACTIVE" : "INACTIVE";
                    await SendChatResponse($"Mass ban mode is currently: {status}");
                    return true;
                }
            }

            if (message.StartsWith($"{_commandTrigger}settrigger ", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    string newTrigger = message.Substring($"{_commandTrigger}settrigger ".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(newTrigger))
                    {
                        _commandTrigger = newTrigger;
                        await SendChatResponse($"Trigger successfully changed to {newTrigger}.");
                        return true;
                    }
                    else
                    {
                        await SendChatResponse("Please provide a valid trigger after !settrigger.");
                        return false;
                    }
                }
            }

            if (message.Equals($"{_commandTrigger}reconnect", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    if (_isReconnecting)
                        return true;

                    _isReconnecting = true;
                    await ReconnectToServerAsync();
                    _isReconnecting = false;
                    return true;
                }
            }

            if (message.Equals($"{_commandTrigger}ping", StringComparison.OrdinalIgnoreCase))
            {
                if (senderUsername == _masterUsername)
                {
                    int pingTime = await GetPingToServerAsync("war.pianka.io");
                    string pingResponse = pingTime != -1
                        ? $"Ping to war.pianka.io: {pingTime} ms"
                        : "Error: Could not ping the server.";
                    await SendChatResponse(pingResponse);
                    return true;
                }
            }

            return false;
        }



        private async Task PerformMassBanAsync()
        {
            try
            {

                await Task.Delay(2000);

                // Check operator status before starting
                if (!IsCurrentUserOperator())
                {
                    LogMessage("* Not an operator, temporarily disabling mass ban mode until operator status is acquired *", Colors.Red);
                    isBanAllActive = false;
                    return;
                }

                LogMessage("* MASS BAN MODE ACTIVATED *", Colors.Red);

                // Log current user count before starting
                int startingCount = await Dispatcher.InvokeAsync(() => UserList.Items.Count);
                LogMessage($"* Starting mass ban with {startingCount} users in channel *", Colors.Yellow);

                // Initial ban wave - don't clear users first, let the ban process remove them
                int banCount = await ExecuteBanWave();
                LogMessage($"* Banned {banCount} users in first wave *", Colors.Yellow);

                // Check operator status before continuing
                if (!IsCurrentUserOperator())
                {
                    await DisableBanMode("Lost operator status");
                    return;
                }

                // Wait between waves
                await Task.Delay(2000);

                // Request a fresh user list after the first wave to make sure we have everyone
                await RequestUserListAsync();
                await Task.Delay(1000);

                // Second sweep to catch users that joined during first wave
                if (IsCurrentUserOperator())
                {
                    LogMessage("* Performing second sweep for new joins *", Colors.Yellow);
                    int secondBanCount = await ExecuteBanWave();
                    LogMessage($"* Banned {secondBanCount} additional users in second wave *", Colors.Yellow);
                }
                else
                {
                    await DisableBanMode("Lost operator status before second wave");
                    return;
                }

                // Final check of remaining users
                int remainingCount = await Dispatcher.InvokeAsync(() => UserList.Items.Count);
                LogMessage($"* Ban waves complete - {remainingCount} users remain (should be safelist only) *", Colors.Green);

                if (isBanAllActive)
                {
                    LogMessage("* Mass ban complete - New joins will be automatically banned *", Colors.Green);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Mass ban system error: {ex.Message}", Colors.Red);
            }
            finally
            {
                _isReconnecting = false;
                _isRejoining = false;
            }
        }

        private async Task<int> ExecuteBanWave()
        {
            // Check operator status first
            if (!IsCurrentUserOperator())
            {
                await DisableBanMode("Lost operator status");
                return 0;
            }

            // Take snapshot of current users
            var currentUsers = await Dispatcher.InvokeAsync(() =>
                UserList.Items.Cast<ChatUser>()
                    .Where(u => !string.IsNullOrEmpty(u.Username) && !u.Username.All(char.IsDigit))
                    .ToList());

            int totalUsers = currentUsers.Count;
            int bannedCount = 0;

            // Log start of ban wave with user count
            LogMessage($"* Starting ban wave with {totalUsers} users *", Colors.Yellow);

            // Keep track of usernames we're going to ban
            List<string> usernamesToBan = new List<string>();

            // First identify all users to ban
            foreach (var user in currentUsers)
            {
                if (user.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                    continue; // Skip yourself

                if (!IsUserOnSafelist(user.Username))
                {
                    usernamesToBan.Add(user.Username);
                }
            }

            // Now ban them all
            foreach (var usernameToBan in usernamesToBan)
            {
                // Check operator status before each ban attempt
                if (!IsCurrentUserOperator())
                {
                    await DisableBanMode("Lost operator status during ban wave");
                    return bannedCount;
                }

                try
                {
                    await SendChatResponse($"/ban {usernameToBan}");
                    LogMessage($"* Banned: {usernameToBan} ({bannedCount + 1}/{usernamesToBan.Count}) *", Colors.Yellow);
                    bannedCount++;

                    // Remove the user from UserList after banning
                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var userToRemove = UserList.Items.Cast<ChatUser>()
                                .FirstOrDefault(u => u.Username.Equals(usernameToBan, StringComparison.OrdinalIgnoreCase));

                            if (userToRemove != null)
                            {
                                UserList.Items.Remove(userToRemove);
                                UserList.Items.Refresh();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error removing user {usernameToBan} from list: {ex.Message}");
                        }
                    });

                    await Task.Delay(50); // Brief delay between bans
                }
                catch (Exception ex)
                {
                    LogMessage($"Error banning {usernameToBan}: {ex.Message}", Colors.Red);
                }
            }

            // Update the channel info display
            UpdateChannelInfo();

            // Return the count of banned users
            return bannedCount;
        }



        private bool IsCurrentUserOperator()
        {
            try
            {
                var currentUser = UserList.Items.Cast<ChatUser>()
                    .FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                return currentUser?.IsOperator ?? false;
            }
            catch
            {
                return false;
            }
        }

        // Add this method to safely disable ban mode
        private async Task DisableBanMode(string reason)
        {
            if (isBanAllActive)
            {
                isBanAllActive = false;
                LogMessage($"* Mass ban mode automatically deactivated: {reason} *", Colors.Yellow);
                await SendChatResponse("* Mass ban mode has been automatically disabled *");
            }
        }



        private async Task<int> GetPingToServerAsync(string serverAddress)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await client.ConnectAsync(serverAddress, 6112);
                    stopwatch.Stop();
                    return (int)stopwatch.ElapsedMilliseconds;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"ERROR: Could not ping the server. {ex.Message}", Colors.Red);
                return -1;
            }
        }

        private async Task SendChatResponse(string message)
        {
            try
            {
                if (!IsConnected())
                {
                    LogMessage("* Cannot send message - not connected", Colors.Red);
                    return;
                }

                var timeoutTask = Task.Delay(5000);
                var sendTask = Task.Run(async () =>
                {
                    byte[] messageRequest = Encoding.UTF8.GetBytes(message + "\n");
                    if (stream != null)
                    {
                        await stream.WriteAsync(messageRequest);
                    }
                });

                var completedTask = await Task.WhenAny(sendTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("Send operation timed out");
                }

                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                LogMessage($"Error sending response: {ex.Message}", Colors.Red);

                if (ex is ObjectDisposedException || ex is IOException || ex is TimeoutException)
                {
                    if (_isAutoReconnectEnabled && !_isReconnecting)
                    {
                        await HandleReconnection();
                    }
                }
            }
        }

        private async Task KeepConnectionAlive()
        {
            while (IsConnected())
            {
                try
                {
                    byte[] keepAliveMessage = Encoding.UTF8.GetBytes("/NULL\n");
                    await stream!.WriteAsync(keepAliveMessage);
                    await Task.Delay(60000);
                }
                catch (Exception)
                {
                    if (_isAutoReconnectEnabled)
                    {
                        await HandleReconnection();
                        break;
                    }
                }
            }
        }

        private async Task VerifyUserCount()
        {
            try
            {
                int reportedCount = await Dispatcher.InvokeAsync(() => UserList.Items.Count);
                LogMessage($"* User list verification: Currently tracking {reportedCount} users *", Colors.Yellow);

                // If we're in ban mode, no need to request a new list
                if (isBanAllActive)
                {
                    LogMessage("* Ban mode active - Not refreshing user list *", Colors.Yellow);
                    return;
                }

                // Request an updated user list
                await RequestUserListAsync();

                // Wait a moment for the list to update
                await Task.Delay(2000);

                // Check the new count
                int newCount = await Dispatcher.InvokeAsync(() => UserList.Items.Count);
                LogMessage($"* After refresh: Now tracking {newCount} users *", Colors.Yellow);

                // Update channel info
                UpdateChannelInfo();
            }
            catch (Exception ex)
            {
                LogMessage($"Error verifying user count: {ex.Message}", Colors.Red);
            }
        }

        private void LogMessage(string message, Color? color = null)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Trim() == "*")
                return;

            string timeStamp = DateTime.Now.ToString("HH:mm:ss");

            Dispatcher.Invoke(() =>
            {
                currentChatLines++;
                if (currentChatLines > MAX_CHAT_LINES && !isClearingChat)
                {
                    isClearingChat = true;
                    rtbChat.Document.Blocks.Clear();
                    LogMessage("* Chat cleared due to length limit *", Colors.Yellow);
                    currentChatLines = 0;
                    isClearingChat = false;
                }

                var paragraph = new System.Windows.Documents.Paragraph();
                paragraph.Inlines.Add(new System.Windows.Documents.Run($"[{timeStamp}] ")
                {
                    Foreground = new SolidColorBrush(Colors.LimeGreen)
                });

                var messageColor = Colors.LimeGreen;

                if (color.HasValue)
                {
                    messageColor = color.Value;
                }
                else if (message.StartsWith("[Whisper"))
                {
                    messageColor = Color.FromRgb(0, 255, 255);
                }
                else if (message.StartsWith($"{username}:"))
                {
                    messageColor = Color.FromRgb(255, 215, 0);
                }
                else if (message.Contains(":"))
                {
                    messageColor = Color.FromRgb(0, 255, 0);
                }

                paragraph.Inlines.Add(new System.Windows.Documents.Run(message)
                {
                    Foreground = new SolidColorBrush(messageColor)
                });

                paragraph.Inlines.Add(new System.Windows.Documents.Run(Environment.NewLine));
                rtbChat.Document.Blocks.Add(paragraph);
                rtbChat.ScrollToEnd();
            });
        }

        private void LogWhisper(string message, Color? color = null)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string timeStamp = DateTime.Now.ToString("HH:mm:ss");

            Dispatcher.Invoke(() =>
            {
                if (rtbWhispers.Document.Blocks.Count > MAX_CHAT_LINES)
                {
                    rtbWhispers.Document.Blocks.Clear();
                    LogWhisper("* Whispers cleared due to length limit *", Colors.Yellow);
                }

                var paragraph = new System.Windows.Documents.Paragraph();
                paragraph.Inlines.Add(new System.Windows.Documents.Run($"[{timeStamp}] ")
                {
                    Foreground = new SolidColorBrush(Colors.Cyan)
                });

                Color messageColor = color ?? Color.FromRgb(0, 255, 255);

                paragraph.Inlines.Add(new System.Windows.Documents.Run(message)
                {
                    Foreground = new SolidColorBrush(messageColor)
                });

                paragraph.Inlines.Add(new System.Windows.Documents.Run(Environment.NewLine));
                rtbWhispers.Document.Blocks.Add(paragraph);
                rtbWhispers.ScrollToEnd();
            });
        }

        private bool IsConnected()
        {
            try
            {
                return client?.Connected == true && stream != null;
            }
            catch
            {
                return false;
            }
        }


        private async Task DisconnectAsync()
        {
            try
            {
                _isConnectionMonitorRunning = false;
                _isAutoReconnectEnabled = false;
                _isReconnecting = false;
                _isRejoining = false;
                isBanAllActive = false;

                if (_reconnectCancellation != null)
                {
                    _reconnectCancellation.Cancel();
                    _reconnectCancellation.Dispose();
                    _reconnectCancellation = null;
                }

                // Store local reference to prevent null during cleanup
                var localStream = stream;
                var localClient = client;

                // Clear references first
                stream = null;
                client = null;

                // Then clean up
                if (localStream != null)
                {
                    await localStream.DisposeAsync();
                }

                if (localClient != null)
                {
                    localClient.Close();
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    UserList.Items.Clear();
                    PingDisplay.Text = "-- ms";
                    PingDisplay.Foreground = new SolidColorBrush(Colors.LimeGreen);
                });

                LogMessage("* Disconnected by user", Colors.Red);
            }
            catch (Exception ex)
            {
                LogMessage($"* Error during disconnection: {ex.Message}", Colors.Red);
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await DisconnectAsync();
        }

        private void ClearChatButton_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                rtbChat.Document.Blocks.Clear();
                rtbWhispers.Document.Blocks.Clear();
            });
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var profileWindow = new ProfileWindow(
                username ?? "",
                password ?? "",
                homeChannel,
                _serverAddress,
                _serverPort,
                _baseReconnectDelay,
                _reconnectScatter,
                _commandTrigger,
                _masterUsername,
                () =>
                {
                    LoadConfiguration();
                    if (client?.Connected == true)
                    {
                        LogMessage("* Profile settings updated. Please reconnect to apply changes.", Colors.Yellow);
                    }
                });

            profileWindow.Owner = this;
            profileWindow.ShowDialog();
        }

        private void LoadConfiguration()
        {
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Path.GetDirectoryName(_configPath))
                    .AddJsonFile(Path.GetFileName(_configPath), optional: false, reloadOnChange: true)
                    .Build();

                username = configuration["LoginSettings:Username"];
                password = configuration["LoginSettings:Password"];

                // Load home channel and update both variables
                var loadedHomeChannel = configuration["LoginSettings:HomeChannel"];
                if (!string.IsNullOrWhiteSpace(loadedHomeChannel))
                {
                    _homeChannel = loadedHomeChannel;
                    homeChannel = loadedHomeChannel;
                }
                else
                {
                    _homeChannel = "#Home";
                    homeChannel = "#Home";
                }

                // Add null coalescing operators
                username = configuration["LoginSettings:Username"] ?? string.Empty;
                password = configuration["LoginSettings:Password"] ?? string.Empty;
                homeChannel = configuration["LoginSettings:HomeChannel"] ?? "#Home";
                _serverAddress = configuration.GetValue<string>("ConnectionSettings:ServerAddress") ?? "war.pianka.io";
                _serverPort = configuration.GetValue<int>("ConnectionSettings:ServerPort", 6112);
                _baseReconnectDelay = configuration.GetValue<int>("ConnectionSettings:BaseReconnectDelay", 400);
                _reconnectScatter = configuration.GetValue<int>("ConnectionSettings:ReconnectScatter", 200);
                _commandTrigger = configuration.GetValue<string>("CommandSettings:Trigger") ?? "!";
                _masterUsername = configuration.GetValue<string>("CommandSettings:Master") ?? username;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading configuration: {ex.Message}", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                // Read existing configuration
                string configJson = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(configJson);

                // Update the configuration
                if (config == null)
                {
                    config = new Dictionary<string, Dictionary<string, object>>();
                }

                // Ensure LoginSettings exists
                if (!config.ContainsKey("LoginSettings"))
                {
                    config["LoginSettings"] = new Dictionary<string, object>();
                }

                // Update values while preserving existing ones
                config["LoginSettings"]["Username"] = username ?? "";
                config["LoginSettings"]["Password"] = password ?? "";
                config["LoginSettings"]["HomeChannel"] = _homeChannel;

                // Ensure other sections exist and are preserved
                if (!config.ContainsKey("ConnectionSettings"))
                {
                    config["ConnectionSettings"] = new Dictionary<string, object>
                    {
                        ["ServerAddress"] = _serverAddress,
                        ["ServerPort"] = _serverPort,
                        ["BaseReconnectDelay"] = _baseReconnectDelay,
                        ["ReconnectScatter"] = _reconnectScatter
                    };
                }

                if (!config.ContainsKey("CommandSettings"))
                {
                    config["CommandSettings"] = new Dictionary<string, object>
                    {
                        ["Trigger"] = _commandTrigger,
                        ["Master"] = _masterUsername
                    };
                }

                // Serialize with proper formatting
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string updatedJsonString = JsonSerializer.Serialize(config, options);

                // Write to file
                File.WriteAllText(_configPath, updatedJsonString);

                LogMessage("* Configuration saved successfully *", Colors.LimeGreen);
            }
            catch (Exception ex)
            {
                LogMessage($"Error saving configuration: {ex.Message}", Colors.Red);
            }
        }

        private void ShowHelpWindow()
        {
            try
            {
                // Create a new window for help content
                var helpWindow = new Window
                {
                    Title = "Bot Commands",
                    Width = 600,
                    Height = 500,
                    Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                    WindowStyle = WindowStyle.ToolWindow,
                    ResizeMode = ResizeMode.NoResize,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                // Create the main container
                var grid = new Grid();
                helpWindow.Content = grid;

                // Create a RichTextBox to display the command list
                var rtbHelp = new RichTextBox
                {
                    Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                    Foreground = new SolidColorBrush(Colors.LimeGreen),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(10),
                    IsReadOnly = true,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                };

                grid.Children.Add(rtbHelp);

                // Create the paragraph for the title
                var titleParagraph = new System.Windows.Documents.Paragraph();
                titleParagraph.Inlines.Add(new System.Windows.Documents.Run("Available Commands")
                {
                    Foreground = new SolidColorBrush(Colors.LimeGreen),
                    FontWeight = FontWeights.Bold,
                    FontSize = 16
                });
                titleParagraph.TextAlignment = TextAlignment.Center;
                rtbHelp.Document.Blocks.Add(titleParagraph);

                // Add a paragraph for each command group
                AddCommandGroup(rtbHelp, "Basic Commands", new[]
                {
            ($"{_commandTrigger}say <message>", "Makes the bot say a message"),
            ("?trigger", "Shows the current command trigger"),
            ($"{_commandTrigger}ping", "Shows current ping to server")
        });

                AddCommandGroup(rtbHelp, "Channel Commands", new[]
                {
            ($"{_commandTrigger}join <channel>", "Joins a specific channel"),
            ($"{_commandTrigger}join home", "Joins the home channel"),
            ($"{_commandTrigger}sethome <channel>", "Sets a new home channel"),
            ($"{_commandTrigger}rejoin", "Rejoins the current channel")
        });

                AddCommandGroup(rtbHelp, "Moderation Commands", new[]
                {
            ($"{_commandTrigger}ban <username>", "Bans a specific user"),
            ($"{_commandTrigger}ban *", "Toggles mass ban mode (bans all users except safelist)"),
            ($"{_commandTrigger}unban *", "Deactivates mass ban mode"),
            ($"{_commandTrigger}banstatus", "Shows if mass ban mode is active"),
            ($"{_commandTrigger}opme", "Requests operator status and rejoins")
        });

                AddCommandGroup(rtbHelp, "System Commands", new[]
                {
            ($"{_commandTrigger}reconnect", "Forces reconnection to the server"),
            ($"{_commandTrigger}verify", "Verifies the user count"),
            ($"{_commandTrigger}settrigger <new_trigger>", "Changes the command trigger"),
            ($"{_commandTrigger}quit", "Safely shuts down the application")
        });


                // Add a footer paragraph with info about master user
                var footerParagraph = new System.Windows.Documents.Paragraph();
                footerParagraph.Inlines.Add(new System.Windows.Documents.Run($"Commands are available to master user: {_masterUsername}")
                {
                    Foreground = new SolidColorBrush(Colors.Yellow),
                    FontStyle = FontStyles.Italic,
                    FontSize = 12
                });
                footerParagraph.TextAlignment = TextAlignment.Center;
                footerParagraph.Margin = new Thickness(0, 20, 0, 0);
                rtbHelp.Document.Blocks.Add(footerParagraph);

                // Show the window
                helpWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                LogMessage($"Error showing help window: {ex.Message}", Colors.Red);
            }
        }

        private void AddCommandGroup(RichTextBox rtb, string groupName, (string Command, string Description)[] commands)
        {
            // Add group header
            var headerParagraph = new System.Windows.Documents.Paragraph();
            headerParagraph.Margin = new Thickness(0, 15, 0, 5);
            headerParagraph.Inlines.Add(new System.Windows.Documents.Run(groupName)
            {
                Foreground = new SolidColorBrush(Colors.Cyan),
                FontWeight = FontWeights.Bold,
                FontSize = 14
            });
            rtb.Document.Blocks.Add(headerParagraph);

            // Add each command
            foreach (var (command, description) in commands)
            {
                var commandParagraph = new System.Windows.Documents.Paragraph();
                commandParagraph.Margin = new Thickness(20, 3, 5, 3);

                // Add command with proper coloring
                commandParagraph.Inlines.Add(new System.Windows.Documents.Run(command)
                {
                    Foreground = new SolidColorBrush(Colors.Yellow),
                    FontWeight = FontWeights.Bold
                });

                // Add description
                commandParagraph.Inlines.Add(new System.Windows.Documents.Run($" - {description}")
                {
                    Foreground = new SolidColorBrush(Colors.LimeGreen)
                });

                rtb.Document.Blocks.Add(commandParagraph);
            }
        }


        private async Task ResetConnectionState()
        {
            try
            {
                // Log the reset
                LogMessage("* Resetting connection state *", Colors.Yellow);

                // Ensure auto-reconnect is disabled during reset
                _isAutoReconnectEnabled = false;

                // Reset all connection-related state variables
                _isReconnecting = false;
                _reconnectAttempts = 0;

                // If we were in ban mode, temporarily disable it
                bool wasBanActive = isBanAllActive;
                if (isBanAllActive)
                {
                    isBanAllActive = false;
                    LogMessage("* Ban mode temporarily disabled during connection reset *", Colors.Yellow);
                }

                // Clear any pending messages
                lock (messageLock)
                {
                    messageQueue.Clear();
                    isProcessingMessages = false;
                    messageBuffer.Clear();
                }

                // Full disconnect
                var localStream = stream;
                var localClient = client;

                stream = null;
                client = null;

                if (localStream != null)
                {
                    try
                    {
                        await localStream.DisposeAsync();
                    }
                    catch { /* Ignore errors during cleanup */ }
                }

                if (localClient != null)
                {
                    try
                    {
                        localClient.Close();
                    }
                    catch { /* Ignore errors during cleanup */ }
                }

                // Clear UI elements
                await Dispatcher.InvokeAsync(() =>
                {
                    UserList.Items.Clear();
                    PingDisplay.Text = "-- ms";
                    PingDisplay.Foreground = new SolidColorBrush(Colors.LimeGreen);
                });

                // Delay before attempting new connection
                await Task.Delay(5000);

                // Try to connect again
                _isAutoReconnectEnabled = true;
                _reconnectAttempts = 0;
                await ConnectToServerAsync(username!, password!);

                // Restore ban mode if it was active
                if (wasBanActive && IsCurrentUserOperator())
                {
                    LogMessage("* Restoring ban mode after connection reset *", Colors.Yellow);
                    isBanAllActive = true;
                }

                LogMessage("* Connection state reset complete *", Colors.Green);
            }
            catch (Exception ex)
            {
                LogMessage($"* Error during connection reset: {ex.Message} *", Colors.Red);
                // Re-enable auto-reconnect in case of error
                _isAutoReconnectEnabled = true;
            }
        }

        public class ChatUser
        {
            public string Username { get; }
            public string Flag { get; }
            private readonly string currentUsername;

            public ChatUser(string username, string flag, string currentUsername)
            {
                // Add null checks
                Username = username ?? throw new ArgumentNullException(nameof(username));
                Flag = flag ?? throw new ArgumentNullException(nameof(flag));
                this.currentUsername = currentUsername ?? string.Empty;
            }

            public bool IsOperator => (int.Parse(Flag) & 0x00000002) != 0;

            public bool IsAdmin => (int.Parse(Flag) & 0x00000001) != 0;

            public SolidColorBrush UserColor
            {
                get
                {
                    // First priority: Admin gets blue
                    if (IsAdmin)
                        return new SolidColorBrush(Color.FromRgb(0, 0, 255));

                    // Second priority: If this is the current user AND they are an operator, use pink
                    if (Username == currentUsername && IsOperator)
                        return new SolidColorBrush(Color.FromRgb(255, 20, 147));

                    // Third priority: Other operators get pink
                    if (IsOperator)
                        return new SolidColorBrush(Color.FromRgb(255, 20, 147));

                    // Fourth priority: Current user (not admin/op) gets a distinctive gold color
                    if (Username == currentUsername)
                        return new SolidColorBrush(Color.FromRgb(255, 215, 0)); // Gold color

                    // Default: Regular users get lime green
                    return new SolidColorBrush(Colors.LimeGreen);
                }
            }

            public FontWeight NameWeight => Username == currentUsername ? FontWeights.ExtraBold : FontWeights.Bold;

            public override string ToString() => Username;

            public override bool Equals(object? obj)
            {
                if (obj is ChatUser other)
                {
                    return Username == other.Username && Flag == other.Flag;
                }
                return false;
            }

            public override int GetHashCode() => HashCode.Combine(Username, Flag);
        }
    }
}