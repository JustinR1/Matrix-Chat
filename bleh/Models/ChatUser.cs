using System;
using System.Windows;
using System.Windows.Media;

namespace Loading.Models
{
    public class ChatUser
    {
        public string Username { get; set; }
        public string Flag { get; set; }
        private readonly string currentUsername;

        public ChatUser(string username, string flag, string currentUsername)
        {
            Username = username;
            Flag = flag;
            this.currentUsername = currentUsername;
        }

        public bool IsOperator => (int.Parse(Flag) & 0x00000002) != 0;
        public bool IsAdmin => (int.Parse(Flag) & 0x00000001) != 0;

        public SolidColorBrush UserColor
        {
            get
            {
                if (IsAdmin)
                    return new SolidColorBrush(Color.FromRgb(0, 0, 255)); // Blue for admins
                else if (IsOperator)
                    return new SolidColorBrush(Color.FromRgb(255, 20, 147)); // Hot pink for operators
                else if (Username == currentUsername)
                    return new SolidColorBrush(Colors.LimeGreen); // Electric yellow for your name
                else
                    return new SolidColorBrush(Colors.LimeGreen); // Default green
            }
        }

        public FontWeight NameWeight => Username == currentUsername ? FontWeights.ExtraBold : FontWeights.Bold;

        public override string ToString() => Username;

        public override bool Equals(object obj)
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