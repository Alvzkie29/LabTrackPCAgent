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
                                // Master PC fallback: set when the server was unreachable and the Master PC checked the login.
    private bool _offline;
    private string _offlineId = string.Empty;
    private string _offlinePassword = string.Empty; // kept only until the attendance is queued

    // Class end: warn a few minutes before, then lock the PC for the next class.
    private const int LabUtcOffsetHours = 8;   // Philippine time, same as the server
    private const int WarningMinutes = 5;
    private TimeOnly? _classEnd;               // end time of the class the student recorded
    private System.Windows.Threading.DispatcherTimer? _classTimer;
    private bool _classWarned;


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
        _offline ? "Attendance saved on the Master PC"
        : alreadyRecorded ? "Attendance already recorded" : "Attendance recorded");
        _endSession.EndRequested += (_, _) => EndSession();
        _endSession.Show();
        StartClassTimer();
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
        StopClassTimer();
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
    // ---------- Class end: warning, then lock ----------

    /// <summary>The end time of the class the student picked, from the options the server (or Master PC) sent.</summary>
    private TimeOnly? FindClassEnd(int sectionId)
    {
        if (_options is null) return null;

        var chosen = _options.Suggested?.SectionId == sectionId
            ? _options.Suggested
            : _options.Alternatives.FirstOrDefault(a => a.SectionId == sectionId);
        if (chosen is null) return null;

        // Works whether EndTime is a TimeOnly or text like "10:00:00".
        return TimeOnly.TryParse(Convert.ToString(chosen.EndTime, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture, out var end) ? end : null;
    }

    private void StartClassTimer()
    {
        StopClassTimer();
        if (_classEnd is null) return;

        // If the class has already ended (a late login), give the same short warning period
        // instead of locking the instant the student gets in.
        var now = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(LabUtcOffsetHours));
        if (now >= _classEnd.Value)
            _classEnd = now.AddMinutes(WarningMinutes);

        _classWarned = false;
        _classTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _classTimer.Tick += (_, _) => CheckClassEnd();
        _classTimer.Start();
        CheckClassEnd();
    }

    private void StopClassTimer()
    {
        _classTimer?.Stop();
        _classTimer = null;
        _classEnd = null;
    }

    private void CheckClassEnd()
    {
        if (_classEnd is null) return;

        var now = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(LabUtcOffsetHours));
        var minutesLeft = (_classEnd.Value.ToTimeSpan() - now.ToTimeSpan()).TotalMinutes;

        if (minutesLeft <= 0)
        {
            // Time is up: lock the PC for the next class, the same as pressing End Session.
            EndSession();
            StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
            StatusText.Text = "The class has ended, so this computer was locked for the next class.";
            return;
        }

        if (minutesLeft <= WarningMinutes && !_classWarned)
        {
            _classWarned = true;
            _endSession?.ShowClassEnding((int)Math.Ceiling(minutesLeft));
        }
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
            if (login.Unreachable)
            {
                await TryMasterPcAsync(studentId, password);
                return;
            }

            ShowLoginError(login.Error ?? "Login failed. Please check your credentials.");
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
    // ---------- Offline: Master PC fallback, then the emergency code ----------

    private async Task TryMasterPcAsync(string studentId, string password)
    {
        if (!_api.HasMasterPc)
        {
            ShowLoginError("Can't reach the server right now. Please wait a moment and try again.");
            ShowEmergencyOption();
            return;
        }

        StatusText.Text = "Server not reachable. Trying the Master PC...";

        var offline = await _api.OfflineOptionsAsync(studentId, password, _config.PcIdentifier);
        if (offline.Unreachable)
        {
            ShowLoginError("Can't reach the server or the Master PC. Please tell the laboratory assistant.");
            ShowEmergencyOption();
            return;
        }
        if (!offline.Success)
        {
            ShowLoginError(offline.Error ?? "Login failed. Please check your credentials.");
            return;
        }

        _offline = true;
        _offlineId = studentId;
        _offlinePassword = password;
        _fullName = offline.Data!.FullName;
        PasswordBox.Clear();

        _options = offline.Data;
        ShowConfirmPanel(_options, _fullName);
    }

    private void ShowEmergencyOption()
    {
        if (string.IsNullOrWhiteSpace(_config.EmergencyCodeHash)) return; // no code set for this PC
        EmergencyCodeBox.Clear();
        EmergencyPanel.Visibility = Visibility.Visible;
    }

    private async void EmergencyButton_Click(object sender, RoutedEventArgs e)
    {
        var typed = EmergencyCodeBox.Password;
        EmergencyCodeBox.Clear();

        if (!EmergencyCodeIsValid(typed))
        {
            // A short pause makes guessing slow.
            EmergencyButton.IsEnabled = false;
            ShowLoginError("That emergency code is not correct.");
            await Task.Delay(3000);
            EmergencyButton.IsEnabled = true;
            return;
        }

        EmergencyPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;

        if (!_lockActive)
        {
            MessageBox.Show(this, "Emergency unlock. No attendance was recorded.",
                "LabTrack", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        _screenLocked = false;
        _kiosk?.Disable();
        Hide();

        _endSession = new EndSessionWindow("Emergency unlock", "Offline · no attendance recorded");
        _endSession.EndRequested += (_, _) => EndSession();
        _endSession.Show();
    }

    /// <summary>Checks a typed code against the stored "salt:hash" (PBKDF2-SHA256, 100,000 rounds).</summary>
    private bool EmergencyCodeIsValid(string typed)
    {
        try
        {
            var parts = (_config.EmergencyCodeHash ?? string.Empty).Split(':');
            if (parts.Length != 2 || string.IsNullOrEmpty(typed)) return false;

            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                typed, salt, 100000, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);

            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false; // the stored value is not a valid hash
        }
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

        var result = _offline
        ? await _api.OfflineConfirmAsync(_offlineId, _offlinePassword, _config.PcIdentifier, sectionId)
        : await _api.ConfirmAsync(_config.PcIdentifier, sectionId);

        if (result.Success)
        {
            _classEnd = FindClassEnd(sectionId);
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
        _offline = false;
        _offlineId = string.Empty;
        _offlinePassword = string.Empty;
        EmergencyPanel.Visibility = Visibility.Collapsed;

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