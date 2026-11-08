using System.Windows;

namespace LabTrackAgent;

/// <summary>The small bar shown while a student is using the PC. "End Session" locks the PC again.</summary>
public partial class EndSessionWindow : Window
{
    private bool _allowClose;

    public event EventHandler? EndRequested;

    public EndSessionWindow(string fullName, bool alreadyRecorded)
    {
        InitializeComponent();

        NameText.Text = fullName;
        InfoText.Text = alreadyRecorded ? "Attendance already recorded" : "Attendance recorded";

        // Top center of the screen.
        Left = (SystemParameters.WorkArea.Width - Width) / 2;
        Top = 8;

        // The bar can't be closed with Alt+F4, except when Windows is shutting down or restarting.
        Application.Current.SessionEnding += (_, _) => _allowClose = true;
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
    }

    public void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    private void EndButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "End your session and lock this computer?",
            "LabTrack", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            EndRequested?.Invoke(this, EventArgs.Empty);
    }
}