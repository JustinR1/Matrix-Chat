using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;

namespace Loading
{
    public partial class ProfileWindow : Window
    {
        private readonly string _configPath;
        private readonly Action _onSettingsUpdated;

        public ProfileWindow(string username, string password, string channel,
                            string serverAddress, int serverPort,
                            int baseDelay, int scatter,
                            string trigger, string master,
                            Action onSettingsUpdated)
        {
            InitializeComponent();

            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            _onSettingsUpdated = onSettingsUpdated;

            // Set current values
            UsernameTextBox.Text = username;
            PasswordBox.Password = password;
            ChannelTextBox.Text = channel;
            ServerAddressTextBox.Text = serverAddress;
            PortNumberTextBox.Text = serverPort.ToString();
            DelayTextBox.Text = baseDelay.ToString();
            ScatterTextBox.Text = scatter.ToString();
            TriggerTextBox.Text = trigger;
            MasterTextBox.Text = master;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate numeric inputs
                if (!int.TryParse(PortNumberTextBox.Text, out int port) ||
                    !int.TryParse(DelayTextBox.Text, out int delay) ||
                    !int.TryParse(ScatterTextBox.Text, out int scatter))
                {
                    StatusMessage.Text = "Please enter valid numbers for Port, Delay, and Scatter values.";
                    StatusMessage.Foreground = Brushes.Red;
                    return;
                }

                var jsonObject = new
                {
                    LoginSettings = new
                    {
                        Username = UsernameTextBox.Text,
                        Password = PasswordBox.Password,
                        HomeChannel = ChannelTextBox.Text
                    },
                    ConnectionSettings = new
                    {
                        ServerAddress = ServerAddressTextBox.Text,
                        ServerPort = port,
                        BaseReconnectDelay = delay,
                        ReconnectScatter = scatter
                    },
                    CommandSettings = new
                    {
                        Trigger = TriggerTextBox.Text,
                        Master = MasterTextBox.Text
                    }
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string newJsonString = JsonSerializer.Serialize(jsonObject, options);
                await File.WriteAllTextAsync(_configPath, newJsonString);

                StatusMessage.Text = "Settings saved successfully!";
                StatusMessage.Foreground = Brushes.Green;

                _onSettingsUpdated?.Invoke();

                await Task.Delay(1000);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                StatusMessage.Text = $"Error saving settings: {ex.Message}";
                StatusMessage.Foreground = Brushes.Red;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}