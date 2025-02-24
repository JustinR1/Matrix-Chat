using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Loading.Models;
using Loading.Services;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Threading.Tasks;

namespace Loading.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IChatService _chatService;
        private readonly IConfiguration _configuration;
        private string _inputText = "";
        private string _chatDocumentText = "";  // Changed from FlowDocument to string
        private string _whisperDocumentText = "";  // Changed from FlowDocument to string
        private string _channelInfo = "";
        private string _currentPing = "-- ms";
        private SolidColorBrush _pingColor = new(Colors.LimeGreen);

        public string InputText
        {
            get => _inputText;
            set
            {
                _inputText = value;
                OnPropertyChanged();
            }
        }

        public string ChatDocumentText  // Changed from FlowDocument to string
        {
            get => _chatDocumentText;
            set
            {
                _chatDocumentText = value;
                OnPropertyChanged();
            }
        }

        public string WhisperDocumentText  // Changed from FlowDocument to string
        {
            get => _whisperDocumentText;
            set
            {
                _whisperDocumentText = value;
                OnPropertyChanged();
            }
        }

        public string ChannelInfo
        {
            get => _channelInfo;
            set
            {
                _channelInfo = value;
                OnPropertyChanged();
            }
        }

        public string CurrentPing
        {
            get => _currentPing;
            set
            {
                _currentPing = value;
                OnPropertyChanged();
            }
        }

        public SolidColorBrush PingColor
        {
            get => _pingColor;
            set
            {
                _pingColor = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<ChatUser> Users => _chatService.Users;

        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand SendMessageCommand { get; }
        public ICommand ClearChatCommand { get; }

        public MainViewModel(IChatService chatService, IConfiguration configuration)
        {
            _chatService = chatService;
            _configuration = configuration;

            ConnectCommand = new RelayCommand(async () =>
                await _chatService.ConnectToServerAsync(
                    _configuration["LoginSettings:Username"],
                    _configuration["LoginSettings:Password"]));

            DisconnectCommand = new RelayCommand(async () => await _chatService.DisconnectAsync());
            SendMessageCommand = new RelayCommand(async () => await SendMessage());
            ClearChatCommand = new RelayCommand(() => ClearChat());

            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Users))
                {
                    UpdateChannelInfo();
                }
            };
        }

        private async Task SendMessage()
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;

            await _chatService.SendChatMessage(InputText);
            InputText = string.Empty;
        }

        private void ClearChat()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ChatDocumentText = string.Empty;  // Reset to an empty string
                WhisperDocumentText = string.Empty;  // Reset to an empty string
            });
        }

        private void UpdateChannelInfo()
        {
            ChannelInfo = $"Users: {Users.Count}";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Func<Task> _executeAsync;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Func<Task> executeAsync, Func<bool> canExecute = null)
        {
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _executeAsync = () =>
            {
                execute();
                return Task.CompletedTask;
            };
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }

        public async void Execute(object parameter)
        {
            await _executeAsync();
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
