using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Loading.ViewModels;

namespace Loading.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ChatDocumentText))
            {
                ChatRichTextBox.Document.Blocks.Clear();
                ChatRichTextBox.Document.Blocks.Add(new Paragraph(new Run((sender as MainViewModel)?.ChatDocumentText)));
            }
            else if (e.PropertyName == nameof(MainViewModel.WhisperDocumentText))
            {
                WhisperRichTextBox.Document.Blocks.Clear();
                WhisperRichTextBox.Document.Blocks.Add(new Paragraph(new Run((sender as MainViewModel)?.WhisperDocumentText)));
            }
        }
    }
}


