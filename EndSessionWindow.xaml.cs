using System.Windows;
using System.Windows.Media;

namespace LabTrackAgent;

/// <summary>
/// The small bar shown while someone is using the PC. "End Session" locks the PC again.
/// It can be hidden down to a small tab so it does not cover the student's work.
/// </summary>
public partial class EndSessionWindow : Window
{
    private bool _allowClose;

    public event EventHandler? EndRequested;

    public EndSessionWindow(string fullName, string info)
    {
        InitializeComponent();

        NameText.Text = fullName;
        InfoText.Text = info;

        // The bar can't be closed with Alt+F4, except when Windows is shutting down or restarting.
        Application.Current.SessionEnding += (_, _) => _allowClose = true;
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
    }

    public void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Called a few minutes before the class ends: brings the bar back if it was hidden and
    /// keeps it visible, so the student sees the warning and saves their work.
    /// </summary>
    public void ShowClassEnding(int minutes)
    {
        SetHidden(false);
        HideButton.IsEnabled = false; // the warning stays on screen

        InfoText.Text = minutes <= 1
            ? "Class ends in 1 minute. Save your work now."
            : $"Class ends in {minutes} minutes. Save your work.";
        InfoText.Foreground = Brushes.Orange;
        InfoText.FontWeight = FontWeights.SemiBold;

        System.Media.SystemSounds.Exclamation.Play();
        Activate();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => SetHidden(true);

    private void ShowButton_Click(object sender, RoutedEventArgs e) => SetHidden(false);

    private void SetHidden(bool hidden)
    {
        FullBar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        ShowButton.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps the bar (or the tab) centered at the top of the screen whenever its size changes.</summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Left = (SystemParameters.WorkArea.Width - ActualWidth) / 2;
        Top = ShowButton.Visibility == Visibility.Visible ? 0 : 8;
    }

    private void EndButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "End your session and lock this computer?",
            "LabTrack", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            EndRequested?.Invoke(this, EventArgs.Empty);
    }
}
