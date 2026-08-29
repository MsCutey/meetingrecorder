using System.Windows;

namespace MeetingRecorder.App;

public partial class RecordingNoticeWindow : Window
{
    public RecordingNoticeWindow() => InitializeComponent();
    private void AcceptClick(object sender, RoutedEventArgs eventArgs) => DialogResult = true;
    private void DeclineClick(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
