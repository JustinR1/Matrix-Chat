using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Loading.Models;

namespace Loading.Services
{
    public interface IChatService
    {
        bool IsConnected { get; }
        ObservableCollection<ChatUser> Users { get; }
        int CurrentPing { get; }

        Task ConnectToServerAsync(string username, string password, bool isReconnect = false);
        Task DisconnectAsync();
        Task SendChatMessage(string message);
        void RequestUserList();
        void LogMessage(string message, System.Windows.Media.Color? color = null);
        void LogWhisper(string message, System.Windows.Media.Color? color = null);
    }
}