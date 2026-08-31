using System.Windows;

namespace MeetingRecorder.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(object dataContext)
    {
        DataContext = dataContext;
        InitializeComponent();
    }
}
