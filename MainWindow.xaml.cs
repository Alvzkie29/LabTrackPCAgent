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
    private bool _syncingLoginPassword;

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

        _endSession = new EndSessionWindow(_fullName,
            alreadyRecorded ? "Attendance already recorded" : "Attendance recorded");
        _endSession.EndRequested += (_, _) => EndSession();
        _endSession.Show();
    }

    private void UnlockForStaff()
    {
        if (!_lockActive)
        {
            MessageBox.Show(this, "Unlocked for maintenance. No attendance was recorded.",
                "LabTrack", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        _screenLocked = false;
        _kiosk?.Disable();
        Hide();

        // Same End Session bar as a student; pressing it locks the PC again.
        _endSession = new EndSessionWindow(_fullName, "Maintenance · no attendance recorded");
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

    private void LoginPassword_Changed(object sender, RoutedEventArgs e)
    {
        PasswordBox.Tag = PasswordBox.SecurePassword.Length == 0 ? "Empty" : "Filled";
        if (_syncingLoginPassword || RevealedPasswordBox is null) return;

        _syncingLoginPassword = true;
        try
        {
            RevealedPasswordBox.Text = ShowPasswordToggle?.IsChecked == true ? PasswordBox.Password : string.Empty;
        }
        finally { _syncingLoginPassword = false; }
    }

    private void RevealedPassword_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingLoginPassword || PasswordBox is null) return;
        _syncingLoginPassword = true;
        try { PasswordBox.Password = RevealedPasswordBox.Text; }
        finally { _syncingLoginPassword = false; }
    }

    private void PasswordVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordBox is null || RevealedPasswordBox is null) return;
        var reveal = ShowPasswordToggle.IsChecked == true;
        _syncingLoginPassword = true;
        try { RevealedPasswordBox.Text = reveal ? PasswordBox.Password : string.Empty; }
        finally { _syncingLoginPassword = false; }

        PasswordBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        RevealedPasswordBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        var action = reveal ? "Hide password" : "Show password";
        ShowPasswordToggle.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(ShowPasswordToggle, action);
        if (reveal)
        {
            RevealedPasswordBox.Focus();
            RevealedPasswordBox.CaretIndex = RevealedPasswordBox.Text.Length;
        }
        else { PasswordBox.Focus(); }
    }

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
        ShowPasswordToggle.IsChecked = false;
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

        var roles = login.Data!.Roles;

        // Staff unlock the PC for maintenance. No attendance is recorded.
        if (roles.Contains("LabAssistant") || roles.Contains("Custodian") || roles.Contains("Administrator"))
        {
            _fullName = login.Data.FullName;
            PasswordBox.Clear();
            StatusText.Text = "Unlocking for maintenance...";

            var unlock = await _api.StaffUnlockAsync(_config.PcIdentifier);
            if (!unlock.Success)
            {
                _api.ClearSession();
                ShowLoginError(unlock.Unreachable
                    ? "Can't reach the server right now. Please wait a moment and try again."
                    : unlock.Error ?? "Could not unlock this computer.");
                return;
            }

            UnlockForStaff();
            return;
        }

        if (!roles.Contains("Student"))
        {
            _api.ClearSession();
            ShowLoginError("Only student and laboratory staff accounts can log in on lab computers.");
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
            PromptText.Text = "This session matches your assigned schedule.";
            SuggestedLabel.Text = "YOUR ASSIGNED SESSION";
            SuggestedText.Text = options.Suggested!.Display;
            SuggestedEligibilityText.Text = options.Suggested.EligibilityMessage;
            SuggestedEligibilityText.Foreground = options.Suggested.CanConfirm
                ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkGoldenrod;
        }
        else
        {
            PromptText.Text = "No assigned session is available right now.";
            SuggestedLabel.Text = "SESSION AVAILABILITY";
            SuggestedText.Text = options.ScheduleMessage;
            SuggestedEligibilityText.Text = "Ask laboratory staff to check your class list and schedule if this looks incorrect.";
            SuggestedEligibilityText.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
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
        SelectionEligibilityText.Visibility = state;
        SelectionEligibilityText.Text = "Unavailable sessions are marked in the list. Only your assigned session can be recorded.";
    }

    private void SetConfirmControlsEnabled(bool enabled)
    {
        YesButton.IsEnabled = enabled && _options?.Suggested?.CanConfirm == true;
        ChooseOtherButton.IsEnabled = enabled;
        OtherClassesBox.IsEnabled = enabled;
        RecordSelectedButton.IsEnabled = enabled && OtherClassesBox.SelectedItem is SectionOption { CanConfirm: true };
        CancelButton.IsEnabled = enabled;
        RefreshScheduleButton.IsEnabled = enabled;
    }

    private async void YesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_options?.Suggested is null) return;
        await RecordAsync(_options.Suggested);
    }

    private void ChooseOtherButton_Click(object sender, RoutedEventArgs e)
    {
        ChooseOtherButton.Visibility = Visibility.Collapsed;
        SetListVisible(true);
    }

    private async void RefreshScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        SetConfirmControlsEnabled(false);
        await LoadClassesAsync();
    }

    private async void RecordSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (OtherClassesBox.SelectedItem is not SectionOption selected)
        {
            ConfirmStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            ConfirmStatusText.Text = "Please choose your class from the list.";
            return;
        }

        await RecordAsync(selected);
    }

    private void OtherClassesBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectionEligibilityText is null || RecordSelectedButton is null) return;
        var selected = OtherClassesBox.SelectedItem as SectionOption;
        RecordSelectedButton.IsEnabled = OtherClassesBox.IsEnabled && selected?.CanConfirm == true;
        SelectionEligibilityText.Text = selected?.EligibilityLabel
            ?? "Unavailable sessions are marked in the list. Only your assigned session can be recorded.";
    }

    private async Task RecordAsync(SectionOption option)
    {
        if (!option.CanConfirm)
        {
            ConfirmStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            ConfirmStatusText.Text = option.EligibilityMessage;
            return;
        }
        SetConfirmControlsEnabled(false);
        ConfirmStatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        ConfirmStatusText.Text = "Recording your attendance...";

        var result = await _api.ConfirmAsync(_config.PcIdentifier, option.SectionId);

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
        ShowPasswordToggle.IsChecked = false;
        StatusText.Text = string.Empty;
        LoginButton.IsEnabled = true;

        ConfirmPanel.Visibility = Visibility.Collapsed;
        NewPasswordPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }
}
