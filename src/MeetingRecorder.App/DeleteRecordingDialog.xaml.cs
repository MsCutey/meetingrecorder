using System.Windows;

namespace MeetingRecorder.App;

public partial class DeleteRecordingDialog : Window
{
    public DeleteRecordingDialog(string fileName)
    {
        InitializeComponent();
        FileNameText.Text = fileName;
    }

    public bool DeleteLocalFile => DeleteLocalFileCheckBox.IsChecked == true;
    public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

    private void DeleteClick(object sender, RoutedEventArgs eventArgs) => DialogResult = true;
    private void CancelClick(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
