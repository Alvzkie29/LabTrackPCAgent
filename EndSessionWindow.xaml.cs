using System.Windows;

namespace LabTrackAgent;

using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

/// <summary>A collapsible session bar. "End Session" locks the PC again.</summary>
public partial class EndSessionWindow : Window
{
    private bool _allowClose;
    private bool _positionInitialized;
    private bool _isDragging;
    private bool _updatingPresentation;
    private bool _isCompact;
    private enum DockSide { None, Left, Right }
    private DockSide _dockSide;
    private const double DockThreshold = 32;
    private const double EdgeMargin = 4;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly System.Drawing.Icon _trayImage;

    public event EventHandler? EndRequested;

    public EndSessionWindow(string fullName, string info)
    {
        InitializeComponent();

        NameText.Text = fullName;
        InfoText.Text = info;

        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/LabTrackAgent;component/Assets/labtrack.ico")).Stream)
            _trayImage = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize);

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Show session", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("End Session...", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            EndButton_Click(this, new RoutedEventArgs());
        }));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayImage,
            Text = "LabTrack - session active",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Dispatcher.Invoke(ShowFromTray);
        };

        Loaded += (_, _) =>
        {
            if (_positionInitialized) return;
            var workArea = GetWorkArea();
            Left = workArea.Left + Math.Max(0, (workArea.Width - ActualWidth) / 2);
            Top = workArea.Top + 8;
            _positionInitialized = true;
        };
        SizeChanged += (_, _) => ClampPosition();

        // Close/X and Alt+F4 hide the bar; only confirmed End Session or Windows shutdown closes it.
        Application.Current.SessionEnding += OnSessionEnding;
        Closed += (_, _) =>
        {
            Application.Current.SessionEnding -= OnSessionEnding;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _trayImage.Dispose();
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => _allowClose = true;

    private Rect GetWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(transform.Transform(new Point(area.Left, area.Top)),
                        transform.Transform(new Point(area.Right, area.Bottom)));
    }

    private void ClampPosition()
    {
        if (!_positionInitialized || _isDragging || _updatingPresentation) return;
        var workArea = GetWorkArea();
        var preferredLeft = _dockSide switch
        {
            DockSide.Left => workArea.Left + EdgeMargin,
            DockSide.Right => workArea.Right - ActualWidth - EdgeMargin,
            _ => Left
        };
        Left = Math.Clamp(preferredLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
    }

    private void SessionBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons keep their normal click actions. The logo and text are drag handles.
        for (var source = e.OriginalSource as DependencyObject; source is not null;)
        {
            if (source is ButtonBase) return;
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source);
        }
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        _isDragging = true;
        try { DragMove(); }
        finally { _isDragging = false; }
        DockToNearestEdge();
        e.Handled = true;
    }

    private void DockToNearestEdge()
    {
        var workArea = GetWorkArea();
        var nearLeft = Left <= workArea.Left + DockThreshold;
        var nearRight = Left + ActualWidth >= workArea.Right - DockThreshold;
        _dockSide = nearLeft && (!nearRight || Math.Abs(Left - workArea.Left) <= Math.Abs(workArea.Right - Left - ActualWidth))
            ? DockSide.Left
            : nearRight ? DockSide.Right : DockSide.None;
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        _updatingPresentation = true;
        try
        {
            var docked = _dockSide != DockSide.None;
            ExpandedPanel.Visibility = !docked && !_isCompact ? Visibility.Visible : Visibility.Collapsed;
            CompactPanel.Visibility = !docked && _isCompact ? Visibility.Visible : Visibility.Collapsed;
            VerticalPanel.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
            Width = docked ? 52 : _isCompact ? 164 : 430;
            UpdateLayout();
        }
        finally { _updatingPresentation = false; }
        ClampPosition();
    }

    private void HideToTrayButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        ClampPosition();
        Activate();
    }

    private void HideSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _isCompact = true;
        UpdatePresentation();
        ShowSessionButton.Focus();
    }

    private void ShowSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _isCompact = false;
        UpdatePresentation();
        HideSessionButton.Focus();
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
