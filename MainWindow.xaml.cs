using System.Windows;
using LabTrackAgent.Models;
using LabTrackAgent.Services;

namespace LabTrackAgent;

public partial class MainWindow : Window
{
    private readonly AgentConfig _config;
    private readonly ApiClient _api;
    private readonly bool _lockActive;
    private KioskLock? _kiosk;
    private EndSessionWindow? _endSession;
    private AttendanceOptions? _options;
    private string _fullName = string.Empty;
    private string _loginPassword = string.Empty; // kept only while a password change is pending
    private bool _screenLocked; // true while the lock screen is showing
    private bool _allowClose;   // true only when Windows is shutting down or restarting

    public MainWindow()
    {
        InitializeComponent();

        _config = AgentConfig.Load();
        _api = new ApiClient(_config);

        _lockActive = _config.LockEnabled;
        if (_lockActive)
            EnterLockMode();
        else
            KioskLock.ClearPolicies(); // safety net: undoes a lock left on by a crash
    }

    // ---------- Lock mode ----------

    private void EnterLockMode()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
        ShowInTaskbar = false;

        _kiosk = new KioskLock();
        _screenLocked = true;
        _kiosk.Enable();

        // Alt+F4 and the close button do nothing, except when Windows itself is shutting down or restarting.
        Application.Current.SessionEnding += (_, _) => _allowClose = true;
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };

        // If something steals focus while the screen is locked, take it back.
        Deactivated += (_, _) =>
        {
            if (_screenLocked)
                Dispatcher.InvokeAsync(() => { Activate(); Focus(); });
        };

        Closed += (_, _) => _kiosk?.Disable();
    }

    private void Unlock(bool alreadyRecorded)
    {
        if (!_lockActive)
        {
            // Development mode (lock off): show a message and close.
            MessageBox.Show(this,
                alreadyRecorded
                    ? "Your attendance for this class was already recorded. Welcome back!"
                    : "Your attendance has been recorded. Welcome!",
                "LabTrack", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        // Lock mode: release the PC and show the small "End Session" bar.
        _screenLocked = false;
        _kiosk?.Disable();
        Hide();

        _endSession = new EndSessionWindow(_fullName, alreadyRecorded);
        _endSession.EndRequested += (_, _) => EndSession();
        _endSession.Show();
    }

    private void EndSession()
    {
        _endSession?.CloseForReal();
        _endSession = null;

        ResetToLogin();
        StudentIdBox.Clear();

        _screenLocked = true;
        Show();
        WindowState = WindowState.Maximized;
        Topmost = true;
        Activate();
        _kiosk?.Enable();
    }

    // ---------- Screen 1: login ----------

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var studentId = StudentIdBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(studentId) || string.IsNullOrWhiteSpace(password))
        {
            ShowLoginError("Please enter both your Student ID and password.");
            return;
        }

        LoginButton.IsEnabled = false;
        StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        StatusText.Text = "Signing in...";

        var login = await _api.LoginAsync(studentId, password);
        if (!login.Success)
        {
            ShowLoginError(login.Unreachable
                ? "Can't reach the server right now. Please wait a moment and try again."
                : login.Error ?? "Login failed. Please check your credentials.");
            return;
        }

        if (!login.Data!.Roles.Contains("Student"))
        {
            _api.ClearSession();
            ShowLoginError("Only student accounts can log in on lab computers.");
            return;
        }

        _fullName = login.Data.FullName;
        PasswordBox.Clear(); // don't leave the password in the box

        // Accounts that still have the starting password must set a new one first.
        if (login.Data.MustChangePassword)
        {
            _loginPassword = password; // needed once, to prove who is changing it
            ShowChangePasswordPanel();
            return;
        }

        StatusText.Text = "Finding your class...";
        await LoadClassesAsync();
    }

    private void ShowLoginError(string message)
    {
        LoginButton.IsEnabled = true;
        StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        StatusText.Text = message;
    }

    // ---------- Screen 3: set a new password ----------

    private void ShowChangePasswordPanel()
    {
        ChangeWelcomeText.Text = $"Welcome, {_fullName}";
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        ChangeStatusText.Text = string.Empty;
        SaveNewPasswordButton.IsEnabled = true;
        CancelChangeButton.IsEnabled = true;

        LoginPanel.Visibility = Visibility.Collapsed;
        NewPasswordPanel.Visibility = Visibility.Visible;
    }

    private async void SaveNewPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var newPassword = NewPasswordBox.Password;

        if (newPassword.Length < 6)
        {
            ShowChangeError("Use at least 6 characters.");
            return;
        }
        if (newPassword != ConfirmPasswordBox.Password)
        {
            ShowChangeError("The two passwords don't match.");
            return;
        }
        if (newPassword == _loginPassword)
        {
            ShowChangeError("Choose a password different from your starting password.");
            return;
        }

        SaveNewPasswordButton.IsEnabled = false;
        CancelChangeButton.IsEnabled = false;
        ChangeStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        ChangeStatusText.Text = "Saving your new password...";

        var result = await _api.ChangePasswordAsync(_loginPassword, newPassword);
        if (!result.Success)
        {
            ShowChangeError(result.Unreachable
                ? "Can't reach the server right now. Please try again."
                : result.Error ?? "Could not change the password.");
            return;
        }

        _loginPassword = string.Empty;
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();

        ChangeStatusText.Text = "Finding your class...";
        await LoadClassesAsync();
    }

    private void ShowChangeError(string message)
    {
        SaveNewPasswordButton.IsEnabled = true;
        CancelChangeButton.IsEnabled = true;
        ChangeStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        ChangeStatusText.Text = message;
    }

    private void CancelChangeButton_Click(object sender, RoutedEventArgs e) => ResetToLogin();

    // ---------- Screen 2: confirm the class ----------

    private async Task LoadClassesAsync()
    {
        var options = await _api.GetOptionsAsync(_config.PcIdentifier);
        if (!options.Success)
        {
            ResetToLogin();
            ShowLoginError(options.Unreachable
                ? "Can't reach the server right now. Please wait a moment and try again."
                : options.Error ?? "Could not load your class.");
            return;
        }

        _options = options.Data!;
        ShowConfirmPanel(_options, _fullName);
    }

    private void ShowConfirmPanel(AttendanceOptions options, string fullName)
    {
        WelcomeText.Text = $"Welcome, {fullName}";
        ConfirmStatusText.Text = string.Empty;

        OtherClassesBox.ItemsSource = options.Alternatives;
        OtherClassesBox.DisplayMemberPath = nameof(SectionOption.Display);
        OtherClassesBox.SelectedIndex = -1;

        var hasSuggestion = options.Suggested is not null;
        var hasAlternatives = options.Alternatives.Count > 0;

        if (hasSuggestion)
        {
            PromptText.Text = "Is this your class?";
            SuggestedText.Text = options.Suggested!.Display;
        }
        else if (hasAlternatives)
        {
            PromptText.Text = "No class is running in this laboratory right now.";
            SuggestedText.Text = "Choose your class from the list.";
        }
        else
        {
            PromptText.Text = "Nothing is scheduled.";
            SuggestedText.Text = "No classes are scheduled in this laboratory today. Please ask the laboratory assistant.";
        }

        YesButton.Visibility = hasSuggestion ? Visibility.Visible : Visibility.Collapsed;
        ChooseOtherButton.Visibility = hasSuggestion && hasAlternatives ? Visibility.Visible : Visibility.Collapsed;
        SetListVisible(!hasSuggestion && hasAlternatives);

        SetConfirmControlsEnabled(true);
        LoginPanel.Visibility = Visibility.Collapsed;
        NewPasswordPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void SetListVisible(bool visible)
    {
        var state = visible ? Visibility.Visible : Visibility.Collapsed;
        OtherClassesBox.Visibility = state;
        RecordSelectedButton.Visibility = state;
    }

    private void SetConfirmControlsEnabled(bool enabled)
    {
        YesButton.IsEnabled = enabled;
        ChooseOtherButton.IsEnabled = enabled;
        OtherClassesBox.IsEnabled = enabled;
        RecordSelectedButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    private async void YesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_options?.Suggested is null) return;
        await RecordAsync(_options.Suggested.SectionId);
    }

    private void ChooseOtherButton_Click(object sender, RoutedEventArgs e)
    {
        ChooseOtherButton.Visibility = Visibility.Collapsed;
        SetListVisible(true);
    }

    private async void RecordSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (OtherClassesBox.SelectedItem is not SectionOption selected)
        {
            ConfirmStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            ConfirmStatusText.Text = "Please choose your class from the list.";
            return;
        }

        await RecordAsync(selected.SectionId);
    }

    private async Task RecordAsync(int sectionId)
    {
        SetConfirmControlsEnabled(false);
        ConfirmStatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        ConfirmStatusText.Text = "Recording your attendance...";

        var result = await _api.ConfirmAsync(_config.PcIdentifier, sectionId);

        if (result.Success)
        {
            Unlock(result.Data!.AlreadyRecorded);
            return;
        }

        ConfirmStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        ConfirmStatusText.Text = result.Unreachable
            ? "Can't reach the server right now. Please try again."
            : result.Error ?? "Could not record your attendance.";
        SetConfirmControlsEnabled(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => ResetToLogin();

    private void ResetToLogin()
    {
        _api.ClearSession();
        _options = null;
        _loginPassword = string.Empty;

        PasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        StatusText.Text = string.Empty;
        LoginButton.IsEnabled = true;

        ConfirmPanel.Visibility = Visibility.Collapsed;
        NewPasswordPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }
}