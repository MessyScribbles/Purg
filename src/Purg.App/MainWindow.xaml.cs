using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Purg.Decode;
using Purg.Mover;
using Purg.Platform;
using Purg.Scan;
using Purg.Session;
using PhotoImageMetadata = Purg.Decode.ImageMetadata;
using FileMover = Purg.Mover.Mover;

namespace Purg.App;

public partial class MainWindow : Window
{
    private static readonly ResourceManager Strings =
        new("Purg.App.Resources.Strings", typeof(MainWindow).Assembly);
    private readonly ImageDecoder _decoder = new();
    private readonly ImageFrameCache _imageCache = new();
    private readonly SemaphoreSlim _portableIoGate = new(1, 1);
    private readonly TranslateTransform _photoTranslate = new();
    private readonly RotateTransform _photoRotate = new();
    private readonly Dictionary<string, FileIdentity> _markedIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _identityVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _identityTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _identityTaskVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhotoImageMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<FilmstripItem> _filmstrip = new();
    private PhotoSession? _session;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _operationCancellation;
    private string? _folderPath;
    private Guid? _portableRootId;
    private bool _portableMode;
    private DeleteMode _deleteMode = DeleteMode.RecycleBin;
    private DeleteSummary? _deleteSummary;
    private int _folderFileCount;
    private int _filmstripLimit = 9;
    private double _operationProgressFraction;
    private string _sortChoice = "largest";
    private long _viewId;
    private long _animationId;
    private int _scanImagesFound;
    private bool _updatingSort;
    private bool _animationActive;
    private bool _dragging;
    private bool _scanShowingLoading;
    private bool _startupComplete;
    private double _dragStartX;
    private double _dragDx;
    private PhotoEntry? _draggedPhoto;

    public MainWindow()
    {
        InitializeComponent();
        SetScreen(LoadingScreen);
        Loaded += MainWindow_Loaded;
    }

    public void ActivateFromSecondInstance()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        var wasTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = wasTopmost;
        Focus();
    }

    internal static string Text(string key) =>
        Strings.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);

    private void SetCopy()
    {
        Title = Text("AppTitle");
        BrandText.Text = Text("Brand");
        StartHeadlineText.Text = Text("StartHeadline");
        StartHelperText.Text = Text("StartHelper");
        BrowseFolderButton.Content = Text("BrowseFolder");
        BrowseUsbButton.Content = Text("BrowseUsb");
        AlphaBuildText.Text = Text("AlphaBuild");
        LoadingLabelText.Text = Text("LoadingLabel");
        MinimizeButton.Content = Text("WindowMinimize");
        MaximizeButton.Content = Text("WindowMaximize");
        CloseButton.Content = Text("WindowClose");
        UndoButton.ToolTip = Text("UndoAction");
        RedoButton.ToolTip = Text("RedoAction");
        ReviewButton.Content = Text("Review");
        ChangeFolderButton.Content = Text("ChangeFolder");
        SortPrefixText.Text = Text("SortPrefix");
        DetailsHeadingText.Text = Text("Details");
        DimensionsLabelText.Text = Text("Dimensions");
        SizeLabelText.Text = Text("Size");
        DateLabelText.Text = Text("Date");
        CameraLabelText.Text = Text("Camera");
        MarkedSummaryLabelText.Text = Text("TotalSize");
        MegabytesUnitText.Text = Text("Megabytes");
        ReviewedLabelText.Text = Text("Reviewed");
        DeleteHintText.Text = Text("DeleteHint");
        KeepHintText.Text = Text("KeepHint");
        ConfirmTitleText.Text = Text("ConfirmTitle");
        ConfirmNoticeText.Text = Text("ConfirmNotice");
        RecycleBinChoiceText.Text = Text("RecycleBinChoice");
        RecycleBinChoiceDescriptionText.Text = Text("RecycleBinChoiceDescription");
        PermanentDeleteChoiceText.Text = Text("PermanentDeleteChoice");
        PermanentDeleteChoiceDescriptionText.Text = Text("PermanentDeleteChoiceDescription");
        PermanentDeleteWarningText.Text = Text("PermanentDeleteWarning");
        ConfirmBackButton.Content = Text("Back");
        ConfirmDeleteButton.Content = Text("ConfirmRecycleBin");
        CancelOperationButton.Content = Text("Cancel");
        OpenRecycleBinButton.Content = Text("OpenRecycleBin");
        BackToFolderButton.Content = Text("BackToFolder");
        ChooseAnotherFolderButton.Content = Text("ChooseAnotherFolder");
        RecycleBinRadioButton.IsChecked = true;
        UpdateOperationProgress(0, 0);

        _updatingSort = true;
        SortComboBox.DisplayMemberPath = nameof(SortOption.Label);
        SortComboBox.Items.Add(CreateSortOption("largest", "SortLargest"));
        SortComboBox.Items.Add(CreateSortOption("newest", "SortNewest"));
        SortComboBox.Items.Add(CreateSortOption("name", "SortName"));
        SortComboBox.SelectedIndex = 0;
        _sortChoice = "largest";
        _updatingSort = false;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var startupTime = Stopwatch.StartNew();
        StartSunGlowAnimation();
        LoadingProgressBar.Value = 8;
        await Task.Delay(30);

        SetCopy();
        LoadingProgressBar.Value = 38;
        await Task.Delay(30);

        FilmstripItems.ItemsSource = _filmstrip;
        PhotoFrame.RenderTransform = new TransformGroup
        {
            Children =
            {
                _photoTranslate,
                _photoRotate
            }
        };
        SetScreen(StartScreen, keepLoadingVisible: true);
        LoadingProgressBar.Value = 72;
        await Task.Delay(20);

        var remaining = Math.Max(0, 900 - startupTime.ElapsedMilliseconds);
        if (remaining > 0)
        {
            LoadingProgressBar.BeginAnimation(
                ProgressBar.ValueProperty,
                new DoubleAnimation(72, 100, TimeSpan.FromMilliseconds(remaining))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                HandoffBehavior.SnapshotAndReplace);
            await Task.Delay(TimeSpan.FromMilliseconds(remaining));
            LoadingProgressBar.BeginAnimation(ProgressBar.ValueProperty, null);
        }

        LoadingProgressBar.Value = 100;
        await Task.Delay(40);
        LoadingScreen.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
        await Task.Delay(180);
        SetScreen(StartScreen);
        _startupComplete = true;
    }

    private void StartSunGlowAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            LoadingSunGlow.BeginAnimation(UIElement.OpacityProperty, null);
            LoadingSunGlow.Opacity = 0;
            return;
        }

        LoadingSunGlow.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 0.22, TimeSpan.FromMilliseconds(2200))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static SortOption CreateSortOption(string key, string labelKey) => new(key, Text(labelKey));

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowFolderPickerAsync();
    }

    private async void BrowseUsbButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseUsbButton.IsEnabled = false;
        Guid selectedRootId;
        string folderName;
        string selectedDeviceName;
        await _portableIoGate.WaitAsync();
        try
        {
            var devices = await PortableDeviceStorage.GetDevicesAsync();
            var picker = new PortableFolderPickerWindow(devices) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedRootId is not { } rootId)
            {
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Text("UsbAndroidConfirmMessage"),
                    picker.SelectedDeviceName),
                Text("UsbAndroidConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                PortableDeviceStorage.ReleaseRoot(rootId);
                return;
            }

            selectedRootId = rootId;
            selectedDeviceName = picker.SelectedDeviceName;
            folderName = string.IsNullOrEmpty(picker.SelectedFolderPath)
                ? DisplayNameSanitizer.Sanitize(picker.SelectedDeviceName)
                : $"{DisplayNameSanitizer.Sanitize(picker.SelectedDeviceName)}/{DisplayNameSanitizer.Sanitize(picker.SelectedFolderPath)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidOperationException or
                                           System.Runtime.InteropServices.COMException)
        {
            ShowFolderError(Text("UsbNoDevices"));
            return;
        }
        finally
        {
            _portableIoGate.Release();
            BrowseUsbButton.IsEnabled = true;
        }

        await LoadPortableFolderAsync(selectedRootId, selectedDeviceName, folderName);
    }

    private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        ReturnToStart();
    }

    private async Task ShowFolderPickerAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Text("FolderDialogTitle"),
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await LoadFolderAsync(dialog.FolderName);
        }
    }

    private async Task LoadFolderAsync(string folder, bool rescanCurrent = false)
    {
        var validation = await Task.Run(() =>
        {
            var valid = FolderValidator.TryValidate(folder, out var path, out var error);
            return (Valid: valid, Path: path, Error: error);
        });
        if (!validation.Valid)
        {
            ShowFolderError(Text(validation.Error));
            return;
        }

        if (_folderPath is not null &&
            string.Equals(_folderPath, validation.Path, StringComparison.OrdinalIgnoreCase) &&
            !rescanCurrent)
        {
            return;
        }

        _scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        BrowseFolderButton.IsEnabled = false;
        BrowseUsbButton.IsEnabled = false;
        ChangeFolderButton.IsEnabled = false;
        StartErrorText.Text = string.Empty;

        try
        {
            _scanImagesFound = 0;
            _scanShowingLoading = false;
            var scanProgress = new Progress<ScanProgress>(value =>
            {
                if (!ReferenceEquals(_scanCancellation, cancellation))
                {
                    return;
                }

                _scanImagesFound = value.ImagesFound;
                if (_scanShowingLoading)
                {
                    LoadingLabelText.Text = string.Format(
                        CultureInfo.CurrentCulture,
                        Text("ScanningFiles"),
                        _scanImagesFound);
                }
            });
            var scanTask = FolderScanner.ScanAsync(validation.Path, cancellation.Token, scanProgress);
            var showLoadingDelay = Task.Delay(150, cancellation.Token);
            Stopwatch? scanLoadingTime = null;
            if (await Task.WhenAny(scanTask, showLoadingDelay) != scanTask &&
                !cancellation.IsCancellationRequested)
            {
                _scanShowingLoading = true;
                scanLoadingTime = Stopwatch.StartNew();
                LoadingProgressBar.Value = 0;
                LoadingProgressBar.IsIndeterminate = true;
                LoadingLabelText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Text("ScanningFiles"),
                    _scanImagesFound);
                SetScreen(LoadingScreen);
                StartSunGlowAnimation();
            }

            var scan = await scanTask;
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _scanImagesFound = scan.Photos.Count;
            if (_scanShowingLoading)
            {
                LoadingLabelText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Text("ScanningFiles"),
                    _scanImagesFound);
                var remainingLoadingTime = Math.Max(0, 250 - (scanLoadingTime?.ElapsedMilliseconds ?? 250));
                if (remainingLoadingTime > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remainingLoadingTime), cancellation.Token);
                }
            }

            var photos = SortForSelection(scan.Photos);
            if (_portableRootId is { } oldPortableRootId)
            {
                PortableDeviceStorage.ReleaseRoot(oldPortableRootId);
                _portableRootId = null;
            }

            _folderPath = validation.Path;
            _session = new PhotoSession(photos);
            _portableMode = false;
            PortableModeBannerText.Visibility = Visibility.Collapsed;
            _folderFileCount = scan.TotalFiles;
            _metadata.Clear();
            _markedIdentities.Clear();
            _identityVersions.Clear();
            _identityTasks.Clear();
            _identityTaskVersions.Clear();
            _deleteSummary = null;
            _imageCache.Clear();
            _viewId = _decoder.BeginView();
            HeaderFolderText.Text = FormatPathForTitleBar(DisplayNameSanitizer.Sanitize(validation.Path));
            HeaderFolderText.ToolTip = DisplayNameSanitizer.Sanitize(validation.Path);
            HeaderCountText.Text = string.Format(CultureInfo.CurrentCulture, Text("ImageCount"), _session.Count);
            ScanBannerText.Text = scan.SkippedCloudFiles + scan.SkippedLinkedFiles > 0
                ? Text("SkippedFiles")
                : string.Empty;
            SetScreen(Workspace);
            UpdateSessionChrome();
            RenderCurrent();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            ShowFolderError(Text("ScanFailed"));
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanShowingLoading = false;
                _scanCancellation = null;
                BrowseFolderButton.IsEnabled = true;
                BrowseUsbButton.IsEnabled = true;
                ChangeFolderButton.IsEnabled = true;
            }
        }
    }

    private async Task LoadPortableFolderAsync(Guid rootId, string deviceName, string folderName)
    {
        _scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        BrowseFolderButton.IsEnabled = false;
        BrowseUsbButton.IsEnabled = false;
        ChangeFolderButton.IsEnabled = false;
        StartErrorText.Text = string.Empty;
        var keepRoot = false;

        try
        {
            _scanImagesFound = 0;
            _scanShowingLoading = false;
            var scanProgress = new Progress<ScanProgress>(value =>
            {
                if (!ReferenceEquals(_scanCancellation, cancellation))
                {
                    return;
                }

                _scanImagesFound = value.ImagesFound;
                if (_scanShowingLoading)
                {
                    LoadingLabelText.Text = string.Format(
                        CultureInfo.CurrentCulture,
                        Text("ScanningFiles"),
                        _scanImagesFound);
                }
            });
            var scanTask = RunPortableIoAsync(
                () => FolderScanner.ScanPortableAsync(rootId, folderName, cancellation.Token, scanProgress),
                cancellation.Token);
            var showLoadingDelay = Task.Delay(150, cancellation.Token);
            Stopwatch? scanLoadingTime = null;
            if (await Task.WhenAny(scanTask, showLoadingDelay) != scanTask &&
                !cancellation.IsCancellationRequested)
            {
                _scanShowingLoading = true;
                scanLoadingTime = Stopwatch.StartNew();
                LoadingProgressBar.Value = 0;
                LoadingProgressBar.IsIndeterminate = true;
                LoadingLabelText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Text("ScanningFiles"),
                    _scanImagesFound);
                SetScreen(LoadingScreen);
                StartSunGlowAnimation();
            }

            var scan = await scanTask;
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _scanImagesFound = scan.Photos.Count;
            if (_scanShowingLoading)
            {
                LoadingLabelText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Text("ScanningFiles"),
                    _scanImagesFound);
                var remainingLoadingTime = Math.Max(0, 250 - (scanLoadingTime?.ElapsedMilliseconds ?? 250));
                if (remainingLoadingTime > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remainingLoadingTime), cancellation.Token);
                }
            }

            _session = new PhotoSession(SortForSelection(scan.Photos));
            _folderPath = $"USB phone / {folderName}";
            _portableRootId = rootId;
            _portableMode = true;
            keepRoot = true;
            _folderFileCount = scan.TotalFiles;
            _metadata.Clear();
            _markedIdentities.Clear();
            _identityVersions.Clear();
            _identityTasks.Clear();
            _identityTaskVersions.Clear();
            _deleteSummary = null;
            _imageCache.Clear();
            _viewId = _decoder.BeginView();
            HeaderFolderText.Text = FormatPathForTitleBar(DisplayNameSanitizer.Sanitize(folderName));
            HeaderFolderText.ToolTip = DisplayNameSanitizer.Sanitize(folderName);
            HeaderCountText.Text = string.Format(CultureInfo.CurrentCulture, Text("ImageCount"), _session.Count);
            ScanBannerText.Text = scan.SkippedUnavailableFiles > 0
                ? Text("SkippedPortableFiles")
                : string.Empty;
            PortableModeBannerText.Text = Text("UsbModeNotice");
            PortableModeBannerText.Visibility = Visibility.Visible;
            SetScreen(Workspace);
            UpdateSessionChrome();
            RenderCurrent();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or
                                           System.Security.SecurityException or
                                           System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ShowFolderError(Text("UsbScanFailed"));
        }
        finally
        {
            if (!keepRoot)
            {
                PortableDeviceStorage.ReleaseRoot(rootId);
            }

            cancellation.Dispose();
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanShowingLoading = false;
                _scanCancellation = null;
                BrowseFolderButton.IsEnabled = true;
                BrowseUsbButton.IsEnabled = true;
                ChangeFolderButton.IsEnabled = true;
            }
        }
    }

    private void ShowFolderError(string message)
    {
        if (_session is null)
        {
            SetScreen(StartScreen);
            StartErrorText.Text = message;
            return;
        }

        SetScreen(Workspace);
        ScanBannerText.Text = message;
    }

    private List<PhotoEntry> SortForSelection(IEnumerable<PhotoEntry> photos)
    {
        return _sortChoice switch
        {
            "newest" =>
                photos.OrderByDescending(photo => photo.LastWriteTimeUtc)
                    .ThenBy(photo => photo.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            "name" =>
                photos.OrderBy(photo => photo.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => photos.OrderByDescending(photo => photo.SizeBytes)
                .ThenBy(photo => photo.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSort || _animationActive || _dragging || SortComboBox.SelectedItem is not SortOption selection)
        {
            return;
        }

        _sortChoice = selection.Key;
        if (_session is null)
        {
            return;
        }

        _session.Reorder(SortForSelection(_session.Photos));
        CancelAnimation();
        _viewId = _decoder.BeginView();
        UpdateSessionChrome();
        RenderCurrent();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_startupComplete || _animationActive || _dragging)
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await LoadFolderAsync(paths[0]);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || _session is null || StartScreen.Visibility == Visibility.Visible)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_animationActive || _dragging)
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is ComboBox)
        {
            return;
        }

        if (ConfirmScreen.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape)
            {
                SetScreen(Workspace);
                e.Handled = true;
            }

            return;
        }

        if (Workspace.Visibility != Visibility.Visible)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((key == Key.Back && modifiers == ModifierKeys.None) ||
            (key == Key.Z && modifiers is ModifierKeys.None or ModifierKeys.Control))
        {
            UndoDecision();
            e.Handled = true;
            return;
        }

        if ((key == Key.Y && modifiers == ModifierKeys.Control) ||
            (key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            RedoDecision();
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.Left:
                StartKeyboardSwipe(PhotoState.Del);
                e.Handled = true;
                break;
            case Key.Right:
                StartKeyboardSwipe(PhotoState.Keep);
                e.Handled = true;
                break;
        }
    }

    private void StartKeyboardSwipe(PhotoState state)
    {
        var photo = _session?.Current;
        if (photo is null || _animationActive)
        {
            return;
        }

        var direction = state == PhotoState.Keep ? 1 : -1;
        var offset = direction * 110d;
        _draggedPhoto = photo;
        _dragDx = offset;
        UpdateDragFeedback(offset);

        if (!SystemParameters.ClientAreaAnimation)
        {
            CompleteSwipe(photo, state);
            return;
        }

        SetUnderCardReveal(0);
        _animationActive = true;
        UpdateSessionChrome();
        var animationId = ++_animationId;
        var duration = TimeSpan.FromMilliseconds(130);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        AnimateUnderCardReveal(1, duration, easing);
        var slide = new DoubleAnimation(_photoTranslate.X, offset, duration) { EasingFunction = easing };
        slide.Completed += (_, _) =>
        {
            if (animationId != _animationId || _session?.Current != photo)
            {
                return;
            }

            _photoTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _photoTranslate.X = offset;
            _photoRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            _photoRotate.Angle = offset / 25d;
            FlyCardOut(photo, state);
        };
        _photoTranslate.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
        _photoRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(_photoRotate.Angle, offset / 25d, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void PhotoFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_session?.Current is null || _animationActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragging = true;
        _draggedPhoto = _session.Current;
        _dragStartX = e.GetPosition(PhotoViewport).X;
        _dragDx = 0;
        PhotoFrame.CaptureMouse();
        UpdateSessionChrome();
        e.Handled = true;
    }

    private void PhotoFrame_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _animationActive || _draggedPhoto is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragDx = e.GetPosition(PhotoViewport).X - _dragStartX;
        _photoTranslate.X = _dragDx;
        _photoRotate.Angle = _dragDx / 25d;
        UpdateDragFeedback(_dragDx);
        UpdateSessionChrome();
    }

    private void PhotoFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _draggedPhoto is null)
        {
            return;
        }

        _dragDx = e.GetPosition(PhotoViewport).X - _dragStartX;
        _dragging = false;
        PhotoFrame.ReleaseMouseCapture();
        if (Math.Abs(_dragDx) >= 110)
        {
            FlyCardOut(_draggedPhoto, _dragDx > 0 ? PhotoState.Keep : PhotoState.Del);
        }
        else
        {
            SpringCardBack();
        }

        e.Handled = true;
    }

    private void UpdateDragFeedback(double dx)
    {
        var progress = Math.Min(1d, Math.Abs(dx) / 110d);
        var keeping = dx > 0;
        var deleting = dx < 0;
        KeepStamp.Opacity = keeping ? progress : 0;
        KeepRing.Opacity = keeping ? progress : 0;
        DeleteStamp.Opacity = deleting ? progress : 0;
        PhotoImage.Opacity = deleting ? 1d - 0.6d * progress : 1d;
        SetUnderCardReveal(progress);

        var hintActive = progress > 0.30d;
        DeleteHintText.Foreground = deleting && hintActive
            ? (Brush)FindResource("TerracottaBrush")
            : (Brush)FindResource("MutedBrush");
        KeepHintText.Foreground = keeping && hintActive
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("MutedBrush");
        PhotoFrame.BorderBrush = deleting
            ? (Brush)FindResource("TerracottaBrush")
            : (Brush)FindResource("BorderBrush");
    }

    private void SetUnderCardReveal(double progress)
    {
        UnderPhotoFrame.Opacity = progress;
        UnderPhotoScale.ScaleX = 0.96 + 0.04 * progress;
        UnderPhotoScale.ScaleY = 0.96 + 0.04 * progress;
    }

    private void AnimateUnderCardReveal(double progress, TimeSpan duration, IEasingFunction easing)
    {
        var targetScale = 0.96 + 0.04 * progress;
        UnderPhotoFrame.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(UnderPhotoFrame.Opacity, progress, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        UnderPhotoScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(UnderPhotoScale.ScaleX, targetScale, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        UnderPhotoScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(UnderPhotoScale.ScaleY, targetScale, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void FlyCardOut(PhotoEntry photo, PhotoState state)
    {
        if (_session?.Current != photo)
        {
            return;
        }

        _dragging = false;
        _animationActive = true;
        UpdateSessionChrome();
        var direction = state == PhotoState.Keep ? 1 : -1;
        if (!SystemParameters.ClientAreaAnimation)
        {
            CompleteSwipe(photo, state);
            return;
        }

        var animationId = ++_animationId;
        var target = direction * (PhotoViewport.ActualWidth + PhotoFrame.ActualWidth);
        var duration = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(_photoTranslate.X, target, duration) { EasingFunction = easing };
        slide.Completed += (_, _) =>
        {
            if (animationId == _animationId)
            {
                CompleteSwipe(photo, state);
            }
        };
        _photoTranslate.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
        PhotoFrame.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(PhotoFrame.Opacity, 0, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void SpringCardBack()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            CancelAnimation();
            return;
        }

        _animationActive = true;
        UpdateSessionChrome();
        var animationId = ++_animationId;
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(_photoTranslate.X, 0, duration) { EasingFunction = easing };
        slide.Completed += (_, _) =>
        {
            if (animationId == _animationId)
            {
                CancelAnimation();
            }
        };
        _photoTranslate.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
        _photoRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(_photoRotate.Angle, 0, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PhotoImage.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(PhotoImage.Opacity, 1, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        KeepRing.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(KeepRing.Opacity, 0, duration), HandoffBehavior.SnapshotAndReplace);
        KeepStamp.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(KeepStamp.Opacity, 0, duration), HandoffBehavior.SnapshotAndReplace);
        DeleteStamp.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(DeleteStamp.Opacity, 0, duration), HandoffBehavior.SnapshotAndReplace);
        AnimateUnderCardReveal(0, duration, easing);
    }

    private void CompleteSwipe(PhotoEntry photo, PhotoState state)
    {
        if (_session?.Current != photo)
        {
            CancelAnimation();
            return;
        }

        ResetSwipeVisuals(updateChrome: false);
        _session.DecideCurrent(state);
        InvalidateIdentity(photo);
        if (state == PhotoState.Del && photo.State == PhotoState.Del)
        {
            EnsureIdentityTask(photo);
        }

        _viewId = _decoder.BeginView();
        UpdateSessionChrome();
        RenderCurrent();
    }

    private void InvalidateIdentity(PhotoEntry photo)
    {
        _identityVersions.TryGetValue(photo.Path, out var version);
        _identityVersions[photo.Path] = version + 1;
        _markedIdentities.Remove(photo.Path);
    }

    private Task EnsureIdentityTask(PhotoEntry photo)
    {
        if (photo.State != PhotoState.Del || _markedIdentities.ContainsKey(photo.Path))
        {
            return Task.CompletedTask;
        }

        _identityVersions.TryGetValue(photo.Path, out var version);
        if (_identityTasks.TryGetValue(photo.Path, out var running) &&
            _identityTaskVersions.GetValueOrDefault(photo.Path) == version &&
            !running.IsCompleted)
        {
            return running;
        }

        var task = HashMarkedPhotoAsync(photo, version);
        _identityTasks[photo.Path] = task;
        _identityTaskVersions[photo.Path] = version;
        return task;
    }

    private async Task HashMarkedPhotoAsync(PhotoEntry photo, long version)
    {
        try
        {
            FileIdentity identity;
            if (photo.PortableRootId is { } rootId && photo.PortableRelativePath is { } relativePath)
            {
                identity = await RunPortableIoAsync(
                    () => Task.Run(() => PortableDeviceStorage.ComputeIdentityAsync(rootId, relativePath)));
            }
            else
            {
                identity = await SampledFileHash.ComputeAsync(photo.Path);
            }
            if (photo.State == PhotoState.Del &&
                _identityVersions.GetValueOrDefault(photo.Path) == version &&
                _session?.Photos.Contains(photo) == true)
            {
                _markedIdentities[photo.Path] = identity;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           CryptographicException or NotSupportedException or ArgumentException or
                                           System.Security.SecurityException or
                                           System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (photo.State == PhotoState.Del &&
                _identityVersions.GetValueOrDefault(photo.Path) == version &&
                _session?.Photos.Contains(photo) == true)
            {
                ScanBannerText.Text = Text("HashFailed");
            }
        }
    }

    private void UndoDecision()
    {
        if (_animationActive || _dragging || _session?.Undo() != true)
        {
            return;
        }

        SynchronizeMarkedIdentities();
        CancelAnimation();
        _viewId = _decoder.BeginView();
        UpdateSessionChrome();
        RenderCurrent();
    }

    private void RedoDecision()
    {
        if (_animationActive || _dragging || _session?.Redo() != true)
        {
            return;
        }

        SynchronizeMarkedIdentities();
        CancelAnimation();
        _viewId = _decoder.BeginView();
        UpdateSessionChrome();
        RenderCurrent();
    }

    private void SynchronizeMarkedIdentities()
    {
        if (_session is null)
        {
            return;
        }

        foreach (var photo in _session.Photos)
        {
            if (photo.State == PhotoState.Del)
            {
                EnsureIdentityTask(photo);
            }
            else if (_markedIdentities.ContainsKey(photo.Path) ||
                     _identityTasks.TryGetValue(photo.Path, out var task) && !task.IsCompleted)
            {
                InvalidateIdentity(photo);
            }
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ActualWidth < 980 ? 250 : 290;
        WorkspaceRailColumn.Width = new GridLength(width);
    }

    private void UpdateOperationProgress(int processed, int total)
    {
        _operationProgressFraction = total == 0 ? 0 : Math.Clamp((double)processed / total, 0, 1);
        OperationProgressFill.Width = OperationProgressTrack.ActualWidth * _operationProgressFraction;
    }

    private void OperationProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        OperationProgressFill.Width = OperationProgressTrack.ActualWidth * _operationProgressFraction;
    }

    private void FilmstripHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visibleCount = Math.Clamp((int)(FilmstripItems.ActualWidth / 66), 1, 9);
        if (visibleCount == _filmstripLimit)
        {
            return;
        }

        _filmstripLimit = visibleCount;
        RefreshFilmstrip();
    }

    private void CancelAnimation()
    {
        _animationId++;
        ResetSwipeVisuals();
    }

    private void ResetSwipeVisuals(bool updateChrome = true)
    {
        _animationActive = false;
        _dragging = false;
        _dragDx = 0;
        _draggedPhoto = null;
        if (PhotoFrame.IsMouseCaptured)
        {
            PhotoFrame.ReleaseMouseCapture();
        }

        _photoTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _photoTranslate.X = 0;
        _photoRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _photoRotate.Angle = 0;
        PhotoFrame.BeginAnimation(UIElement.OpacityProperty, null);
        PhotoFrame.Opacity = 1;
        PhotoImage.BeginAnimation(UIElement.OpacityProperty, null);
        PhotoImage.Opacity = 1;
        KeepRing.BeginAnimation(UIElement.OpacityProperty, null);
        KeepRing.Opacity = 0;
        KeepStamp.BeginAnimation(UIElement.OpacityProperty, null);
        KeepStamp.Opacity = 0;
        DeleteStamp.BeginAnimation(UIElement.OpacityProperty, null);
        DeleteStamp.Opacity = 0;
        UnderPhotoFrame.BeginAnimation(UIElement.OpacityProperty, null);
        UnderPhotoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        UnderPhotoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SetUnderCardReveal(0);
        DeleteHintText.Foreground = (Brush)FindResource("MutedBrush");
        KeepHintText.Foreground = (Brush)FindResource("MutedBrush");
        UpdatePhotoBorder();
        if (updateChrome)
        {
            UpdateSessionChrome();
        }
    }

    private void RenderCurrent()
    {
        if (_session is null)
        {
            return;
        }

        var photo = _session.Current;
        UpdateSessionChrome();
        UnderPhotoFrame.BeginAnimation(UIElement.OpacityProperty, null);
        UnderPhotoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        UnderPhotoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SetUnderCardReveal(0);
        if (photo is null)
        {
            PhotoImage.Source = null;
            UnderPhotoImage.Source = null;
            UnderPhotoFrame.Visibility = Visibility.Collapsed;
            PhotoFrame.Visibility = Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Visible;
            PreviewFallbackText.Text = _session.Count == 0 ? Text("NoPhotos") : Text("AllReviewed");
            InspectorFilenameText.Text = string.Empty;
            InspectorSubfolderText.Text = string.Empty;
            PhotoStatusText.Text = string.Empty;
            PhotoSizeText.Text = string.Empty;
            DimensionsValueText.Text = string.Empty;
            SizeValueText.Text = string.Empty;
            DateValueText.Text = string.Empty;
            CameraRow.Visibility = Visibility.Collapsed;
            RefreshFilmstrip();
            return;
        }

        PhotoFrame.Visibility = Visibility.Visible;
        PreviewFallback.Visibility = Visibility.Collapsed;
        PhotoImage.Source = null;
        PhotoStatusText.Text = photo.State switch
        {
            PhotoState.Del => Text("PhotoStatusMarked"),
            PhotoState.Keep => Text("StatusKept"),
            PhotoState.Gone => Text("PhotoStatusGone"),
            _ => string.Empty
        };
        PhotoStatusText.Foreground = photo.State == PhotoState.Del
            ? (Brush)FindResource("TerracottaBrush")
            : photo.State == PhotoState.Keep ? (Brush)FindResource("MossBrush") : (Brush)FindResource("MutedBrush");
        PhotoStatusPill.Visibility = photo.State == PhotoState.Undecided ? Visibility.Collapsed : Visibility.Visible;
        PhotoSizeText.Text = FormatSize(photo.SizeBytes);
        InspectorFilenameText.Text = photo.DisplayName;
        InspectorSubfolderText.Text = GetRelativeSubfolderPath(photo);
        SizeValueText.Text = FormatSize(photo.SizeBytes);
        DateValueText.Text = (_metadata.TryGetValue(photo.Path, out var photoMetadata) ? photoMetadata.DateTaken : null)
            ?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
            ?? photo.LastWriteTimeUtc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        CameraRow.Visibility = Visibility.Collapsed;
        DimensionsValueText.Text = photoMetadata is null
            ? Text("DimensionsUnavailable")
            : string.Format(CultureInfo.CurrentCulture, Text("DimensionsFormat"), photoMetadata.Width, photoMetadata.Height);
        if (photoMetadata?.CameraModel is { Length: > 0 } camera)
        {
            CameraRow.Visibility = Visibility.Visible;
            CameraValueText.Text = camera;
        }

        UpdatePhotoBorder();
        if (_imageCache.TryGet(CacheKey(photo.Path, ImageDecodeSize.Fit), ImageDecodeSize.Fit, out var cached) &&
            cached is not null)
        {
            DisplayDecoded(new DecodedImage(cached, _metadata.GetValueOrDefault(photo.Path), null));
        }
        else
        {
            _ = LoadPreviewAsync(photo, _viewId);
        }

        var underPhoto = GetUnderPhoto();
        UnderPhotoImage.Source = null;
        UnderPhotoFrame.Visibility = underPhoto is null ? Visibility.Collapsed : Visibility.Visible;
        if (underPhoto is not null)
        {
            if (_imageCache.TryGet(CacheKey(underPhoto.Path, ImageDecodeSize.Fit), ImageDecodeSize.Fit, out var underBitmap) &&
                underBitmap is not null)
            {
                UnderPhotoImage.Source = underBitmap;
            }
            else
            {
                _ = LoadUnderPreviewAsync(underPhoto, _viewId);
            }
        }

        RefreshFilmstrip();
    }

    private PhotoEntry? GetUnderPhoto()
    {
        if (_session is null)
        {
            return null;
        }

        for (var index = _session.CurrentIndex + 1; index < _session.Count; index++)
        {
            if (_session.Photos[index].State != PhotoState.Gone)
            {
                return _session.Photos[index];
            }
        }

        return null;
    }

    private async Task LoadUnderPreviewAsync(PhotoEntry photo, long viewId)
    {
        try
        {
            var result = await DecodePhotoAsync(photo, ImageDecodeSize.Fit, viewId);
            if (viewId != _viewId || GetUnderPhoto() != photo || result.Bitmap is null)
            {
                return;
            }

            _imageCache.Add(CacheKey(photo.Path, ImageDecodeSize.Fit), ImageDecodeSize.Fit, result.Bitmap);
            UnderPhotoImage.Source = result.Bitmap;
            UnderPhotoFrame.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            UnderPhotoFrame.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadPreviewAsync(PhotoEntry photo, long viewId)
    {
        var decoding = DecodePhotoAsync(photo, ImageDecodeSize.Fit, viewId);
        var completed = await Task.WhenAny(decoding, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != decoding)
        {
            if (viewId == _viewId)
            {
                ShowFallback(Text("PreviewPaused"));
            }

            _ = CompleteSlowPreviewAsync(decoding, photo, viewId);
            return;
        }

        try
        {
            var result = await decoding;
            ApplyDecodeResult(result, photo, viewId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (viewId == _viewId && _session?.Current == photo)
            {
                DisplayDecoded(new DecodedImage(null, null, "Unavailable"));
            }
        }
    }

    private async Task CompleteSlowPreviewAsync(
        Task<DecodedImage> decoding,
        PhotoEntry photo,
        long viewId)
    {
        try
        {
            var result = await decoding;
            ApplyDecodeResult(result, photo, viewId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (viewId == _viewId && _session?.Current == photo)
            {
                DisplayDecoded(new DecodedImage(null, null, "Unavailable"));
            }
        }
    }

    private Task<DecodedImage> DecodePhotoAsync(PhotoEntry photo, ImageDecodeSize size, long viewId)
    {
        if (photo.PortableRootId is not { } rootId || photo.PortableRelativePath is not { } relativePath)
        {
            return _decoder.DecodeAsync(photo.Path, size, viewId);
        }

        return DecodePortablePhotoAsync(rootId, relativePath, photo.DisplayName, size, viewId);
    }

    private async Task<DecodedImage> DecodePortablePhotoAsync(
        Guid rootId,
        string relativePath,
        string fileName,
        ImageDecodeSize size,
        long viewId)
    {
        return await RunPortableIoAsync(async () =>
        {
            if (viewId != _viewId)
            {
                throw new OperationCanceledException();
            }

            var bytes = await PortableDeviceStorage.ReadAllBytesAsync(rootId, relativePath);
            return await _decoder.DecodeAsync(bytes, fileName, size, viewId);
        });
    }

    private async Task<T> RunPortableIoAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await _portableIoGate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _portableIoGate.Release();
        }
    }

    private void ApplyDecodeResult(
        DecodedImage result,
        PhotoEntry photo,
        long viewId)
    {
        if (viewId != _viewId || _session?.Current != photo)
        {
            return;
        }

        if (result.Bitmap is not null)
        {
            _imageCache.Add(
                CacheKey(photo.Path, ImageDecodeSize.Fit),
                ImageDecodeSize.Fit,
                result.Bitmap);
        }

        if (result.Metadata is not null)
        {
            StoreMetadata(photo.Path, result.Metadata);
            DimensionsValueText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Text("DimensionsFormat"),
                result.Metadata.Width,
                result.Metadata.Height);
            DateValueText.Text = result.Metadata.DateTaken?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
                ?? photo.LastWriteTimeUtc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
            if (result.Metadata.CameraModel is { Length: > 0 } camera)
            {
                CameraRow.Visibility = Visibility.Visible;
                CameraValueText.Text = camera;
            }
        }

        DisplayDecoded(result);
    }

    private void StoreMetadata(string path, PhotoImageMetadata metadata)
    {
        if (!_metadata.ContainsKey(path) && _metadata.Count == 3)
        {
            _metadata.Remove(_metadata.Keys.First());
        }

        _metadata[path] = metadata;
    }

    private void DisplayDecoded(DecodedImage result)
    {
        if (result.Bitmap is null)
        {
            var fallback = result.UnavailableReason switch
            {
                "TooLarge" => Text("PreviewTooLarge"),
                "CodecMissing" => Text("PreviewCodecMissing"),
                _ => Text("PreviewUnavailable")
            };
            ShowFallback(fallback);
            return;
        }

        PreviewFallback.Visibility = Visibility.Collapsed;
        PhotoFrame.Visibility = Visibility.Visible;
        PhotoImage.Source = result.Bitmap;
    }

    private void ShowFallback(string message)
    {
        PhotoImage.Source = null;
        PhotoFrame.Visibility = Visibility.Visible;
        PreviewFallback.Visibility = Visibility.Visible;
        PreviewFallbackText.Text = message;
    }

    private void RefreshFilmstrip()
    {
        if (_session is null)
        {
            return;
        }

        var count = Math.Min(_filmstripLimit, _session.Count);
        var start = Math.Clamp(_session.CurrentIndex - count / 2, 0, Math.Max(0, _session.Count - count));
        var end = Math.Min(_session.Count - 1, start + count - 1);
        var photos = _session.Photos.Skip(start).Take(end - start + 1).ToArray();
        _imageCache.KeepThumbnailWindow(photos.Select(photo => photo.Path));
        _filmstrip.Clear();
        foreach (var photo in photos)
        {
            _imageCache.TryGet(photo.Path, ImageDecodeSize.Thumbnail, out var bitmap);
            _filmstrip.Add(new FilmstripItem(photo, bitmap, GetStatusBrush(photo), GetDotBrush(photo)));
        }

        _ = LoadThumbnailsAsync(photos, _viewId);
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<PhotoEntry> photos, long viewId)
    {
        foreach (var photo in photos)
        {
            if (viewId != _viewId)
            {
                return;
            }

            if (_imageCache.TryGet(photo.Path, ImageDecodeSize.Thumbnail, out _))
            {
                continue;
            }

            try
            {
                var result = await DecodePhotoAsync(photo, ImageDecodeSize.Thumbnail, viewId);
                if (viewId != _viewId)
                {
                    return;
                }

                if (result.Bitmap is null)
                {
                    continue;
                }

                _imageCache.Add(photo.Path, ImageDecodeSize.Thumbnail, result.Bitmap);
                var item = _filmstrip.FirstOrDefault(entry => entry.Photo == photo);
                item?.SetBitmap(result.Bitmap);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                continue;
            }
        }
    }

    private void UpdateSessionChrome()
    {
        if (_session is null)
        {
            return;
        }

        var current = _session.Current;
        var markedCount = _session.MarkedCount;
        PositionText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Text("Position"),
            Math.Min(_session.CurrentIndex + 1, _session.Count),
            _session.Count);
        HeaderCountText.Text = string.Format(CultureInfo.CurrentCulture, Text("ImageCount"), _session.Count);
        InspectorFilenameText.Text = current?.DisplayName ?? string.Empty;
        InspectorSubfolderText.Text = GetRelativeSubfolderPath(current);

        UndoButton.IsEnabled = _session.CanUndo && !_animationActive && !_dragging;
        RedoButton.IsEnabled = _session.CanRedo && !_animationActive && !_dragging;
        SortComboBox.IsEnabled = !_animationActive && !_dragging;
        ChangeFolderButton.IsEnabled =
            _operationCancellation is null && _scanCancellation is null && !_animationActive && !_dragging;
        ReviewButton.Content = Text("Review");
        ReviewButton.IsEnabled = markedCount > 0 && !_animationActive && !_dragging && _operationCancellation is null;
        var totalBytes = _session.Photos.Sum(photo => photo.SizeBytes);
        MarkedMegabytesText.Text = FormatMegabytes(_session.MarkedBytes);
        TotalMegabytesText.Text = string.Format(CultureInfo.CurrentCulture, Text("OfMegabytes"), FormatMegabytes(totalBytes));
        ReviewedCountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Text("ReviewedFraction"),
            _session.ReviewedCount,
            _session.Count);
        MarkedTallyText.Text = string.Format(CultureInfo.CurrentCulture, Text("MarkedTally"), markedCount);
        if (_session.Count > 0)
        {
            var value = Math.Clamp((double)_session.ReviewedCount / _session.Count, 0, 1);
            ProgressFill.Width = ProgressTrack.ActualWidth * value;
        }
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_session is null || _session.Count == 0)
        {
            ProgressFill.Width = 0;
            return;
        }

        ProgressFill.Width = ProgressTrack.ActualWidth * Math.Clamp((double)_session.ReviewedCount / _session.Count, 0, 1);
    }

    private void UpdatePhotoBorder()
    {
        var brush = _session?.Current?.State switch
        {
            PhotoState.Del => (Brush)FindResource("TerracottaBrush"),
            PhotoState.Keep => (Brush)FindResource("MossBrush"),
            _ => (Brush)FindResource("BorderBrush")
        };
        PhotoFrame.BorderBrush = brush;
    }

    private static string CacheKey(string path, ImageDecodeSize size) =>
        size == ImageDecodeSize.Thumbnail ? path : path + "|" + size;

    private static string FormatSize(long bytes) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Text(bytes >= 1024L * 1024 ? "MegabytesFormat" : "KilobytesFormat"),
            bytes >= 1024L * 1024 ? bytes / (1024d * 1024) : bytes / 1024d);

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.CurrentCulture);

    private static string FormatPathForTitleBar(string path)
    {
        const int maximumCharacters = 32;
        if (path.Length <= maximumCharacters)
        {
            return path;
        }

        var retainedCharacters = maximumCharacters - 3;
        var prefixLength = (retainedCharacters + 1) / 2;
        var suffixLength = retainedCharacters - prefixLength;
        return path[..prefixLength] + "..." + path[^suffixLength..];
    }

    private string GetRelativeSubfolderPath(PhotoEntry? photo)
    {
        if (photo is null || _folderPath is null)
        {
            return string.Empty;
        }

        if (photo.PortableRelativePath is { } portablePath)
        {
            var separator = portablePath.LastIndexOf('/');
            return separator < 0
                ? string.Empty
                : DisplayNameSanitizer.Sanitize(portablePath[..separator]);
        }

        var directory = Path.GetDirectoryName(photo.Path);
        if (directory is null)
        {
            return string.Empty;
        }

        var relativePath = Path.GetRelativePath(_folderPath, directory);
        return relativePath == "."
            ? string.Empty
            : DisplayNameSanitizer.Sanitize(relativePath);
    }

    private void SetScreen(UIElement screen, bool keepLoadingVisible = false)
    {
        StartScreen.Visibility = screen == StartScreen ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Visibility = screen == Workspace ? Visibility.Visible : Visibility.Collapsed;
        ConfirmScreen.Visibility = screen == ConfirmScreen ? Visibility.Visible : Visibility.Collapsed;
        ProcessingScreen.Visibility = screen == ProcessingScreen ? Visibility.Visible : Visibility.Collapsed;
        ResultScreen.Visibility = screen == ResultScreen ? Visibility.Visible : Visibility.Collapsed;
        LoadingScreen.Visibility = screen == LoadingScreen || keepLoadingVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (screen != LoadingScreen && !keepLoadingVisible)
        {
            LoadingScreen.BeginAnimation(UIElement.OpacityProperty, null);
            LoadingScreen.Opacity = 1;
            LoadingSunGlow.BeginAnimation(UIElement.OpacityProperty, null);
            LoadingSunGlow.Opacity = 0;
            LoadingProgressBar.BeginAnimation(ProgressBar.ValueProperty, null);
            LoadingProgressBar.IsIndeterminate = false;
            LoadingProgressBar.Value = 0;
            LoadingLabelText.Text = Text("LoadingLabel");
        }

        ChangeFolderButton.Visibility = screen == StartScreen || screen == LoadingScreen
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChangeFolderButton.IsEnabled = _operationCancellation is null && _scanCancellation is null && !_animationActive && !_dragging;
    }

    private async Task OpenConfirmAsync()
    {
        if (_session is null || _folderPath is null || _session.MarkedCount == 0)
        {
            return;
        }

        var session = _session;
        var folderPath = _folderPath;
        ReviewButton.IsEnabled = false;
        try
        {
            var pendingHashes = session.Photos
                .Where(photo => photo.State == PhotoState.Del)
                .Select(EnsureIdentityTask)
                .ToArray();
            await Task.WhenAll(pendingHashes);
        }
        finally
        {
            ReviewButton.IsEnabled = _session?.MarkedCount > 0;
        }

        if (!ReferenceEquals(_session, session) || session.MarkedCount == 0)
        {
            return;
        }

        _deleteMode = _portableMode ? DeleteMode.Permanent : DeleteMode.RecycleBin;
        RecycleBinRadioButton.Visibility = _portableMode ? Visibility.Collapsed : Visibility.Visible;
        RecycleBinRadioButton.IsChecked = !_portableMode;
        PermanentDeleteRadioButton.IsChecked = _portableMode;
        ConfirmNoticeText.Text = _portableMode
            ? Text("UsbConfirmNotice")
            : Text("ConfirmNotice");
        PortableConfirmNoticeText.Visibility = _portableMode ? Visibility.Visible : Visibility.Collapsed;
        PortableConfirmNoticeText.Text = Text("UsbModeNotice");
        ConfirmCountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Text("ConfirmCount"),
            session.MarkedCount,
            FormatMegabytes(session.MarkedBytes));
        var warnings = new List<string>();
        if (session.MarkedCount * 2 > _folderFileCount)
        {
            warnings.Add(Text("ConfirmLargeSelectionWarning"));
        }

        if (!_portableMode && FileMover.FolderLooksCloudSynced(folderPath))
        {
            warnings.Add(Text("ConfirmCloudWarning"));
        }

        ConfirmWarningText.Text = string.Join(Environment.NewLine, warnings);
        ConfirmWarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _deleteSummary = null;
        UpdateDeleteChoice();
        SetScreen(ConfirmScreen);
    }

    private void DeleteModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_portableMode)
        {
            _deleteMode = DeleteMode.Permanent;
            UpdateDeleteChoice();
            return;
        }

        _deleteMode = sender == PermanentDeleteRadioButton ? DeleteMode.Permanent : DeleteMode.RecycleBin;
        UpdateDeleteChoice();
    }

    private void UpdateDeleteChoice()
    {
        var permanentlyDeleting = _deleteMode == DeleteMode.Permanent;
        PermanentDeleteWarningCard.Visibility = permanentlyDeleting ? Visibility.Visible : Visibility.Collapsed;
        PermanentDeleteRadioButton.Visibility = Visibility.Visible;
        RecycleBinRadioButton.Visibility = _portableMode ? Visibility.Collapsed : Visibility.Visible;
        ConfirmDeleteButton.Content = permanentlyDeleting
            ? string.Format(
                CultureInfo.CurrentCulture,
                Text("ConfirmPermanentDelete"),
                _session?.MarkedCount ?? 0)
            : Text("ConfirmRecycleBin");
        ConfirmDeleteButton.Background = permanentlyDeleting
            ? new SolidColorBrush(Color.FromRgb(0x8C, 0x1F, 0x15))
            : new SolidColorBrush(Color.FromRgb(0xD4, 0x6A, 0x52));
        ConfirmDeleteButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0x6A, 0x52));
    }

    private async Task ExecuteDeleteAsync()
    {
        if (_session is null || _folderPath is null || _operationCancellation is not null)
        {
            return;
        }

        if (_portableMode &&
            MessageBox.Show(
                this,
                Text("UsbFinalConfirmMessage"),
                Text("UsbFinalConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var mode = _deleteMode;
        var folderPath = _folderPath;
        var requests = _session.Photos
            .Where(photo => photo.State == PhotoState.Del)
            .Select(photo => new DeleteRequest(
                photo.Path,
                _markedIdentities.GetValueOrDefault(photo.Path),
                photo.PortableRootId,
                photo.PortableRelativePath))
            .ToArray();
        if (requests.Length == 0)
        {
            SetScreen(Workspace);
            return;
        }

        _viewId = _decoder.BeginView();
        ConfirmDeleteButton.IsEnabled = false;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var cancellation = _operationCancellation;
        OperationTitleText.Text = Text(mode == DeleteMode.RecycleBin ? "SendingToRecycleBinTitle" : "DeletingPermanentlyTitle");
        OperationProgressText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Text(mode == DeleteMode.RecycleBin ? "RecycleProgress" : "PermanentProgress"),
            0,
            requests.Length,
            0,
            0,
            0);
        UpdateOperationProgress(0, requests.Length);
        CancelOperationButton.IsEnabled = true;
        SetScreen(ProcessingScreen);
        var progress = new Progress<DeleteProgress>(value =>
        {
            OperationProgressText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Text(mode == DeleteMode.RecycleBin ? "RecycleProgress" : "PermanentProgress"),
                value.Processed,
                value.Total,
                value.Deleted,
                value.Skipped,
                value.Failed);
            UpdateOperationProgress(value.Processed, value.Total);
        });

        try
        {
            _deleteSummary = await Task.Run(async () =>
            {
                if (_portableMode)
                {
                    return await RunPortableIoAsync(
                        () => Task.FromResult(
                            FileMover.DeletePortableFiles(requests, progress, cancellation.Token)));
                }

                return FileMover.DeleteMarkedFiles(folderPath, mode, requests, progress, cancellation.Token);
            });
            _session.MarkGone(_deleteSummary.DeletedPaths);
            SynchronizeMarkedIdentities();
            UpdateSessionChrome();
            RenderDeleteResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or
                                           System.Security.SecurityException or InvalidOperationException)
        {
            ConfirmWarningText.Text = Text("OperationStartFailed");
            ConfirmWarningText.Visibility = Visibility.Visible;
            SetScreen(ConfirmScreen);
        }
        finally
        {
            CancelOperationButton.IsEnabled = false;
            ConfirmDeleteButton.IsEnabled = true;
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation.Dispose();
                _operationCancellation = null;
                ChangeFolderButton.IsEnabled = true;
            }
        }
    }

    private void RenderDeleteResult()
    {
        if (_deleteSummary is null)
        {
            return;
        }

        var summary = _deleteSummary;
        var recycleBinMode = summary.Mode == DeleteMode.RecycleBin;
        ResultTitleText.Text = summary.WasCancelled
            ? Text("DeletionCancelledTitle")
            : Text("DeletionResultTitle");
        ProcessedCountText.Text = string.Format(CultureInfo.CurrentCulture, Text("ProcessedCount"), summary.Processed);
        CompletedCountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Text(recycleBinMode ? "SentToRecycleBinCount" : "PermanentlyDeletedCount"),
            summary.Deleted);
        SkippedCountText.Text = string.Format(CultureInfo.CurrentCulture, Text("SkippedCount"), summary.Skipped);
        FailedCountText.Text = string.Format(CultureInfo.CurrentCulture, Text("FailedCount"), summary.Failed);
        ResultReasonsItems.ItemsSource = summary.Reasons
            .OrderBy(reason => reason.Key, StringComparer.Ordinal)
            .Select(reason => string.Format(
                CultureInfo.CurrentCulture,
                Text("ReasonCount"),
                Text(reason.Key),
                reason.Value))
            .ToArray();
        ResultActionErrorText.Text = string.Empty;
        OpenRecycleBinButton.Visibility =
            recycleBinMode && summary.Deleted > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetScreen(ResultScreen);
    }

    private void BrowseTo(PhotoEntry photo)
    {
        if (_session is null || _animationActive)
        {
            return;
        }

        _session.MoveTo(FindPhotoIndex(photo));
        CancelAnimation();
        _viewId = _decoder.BeginView();
        RenderCurrent();
    }

    private int FindPhotoIndex(PhotoEntry photo)
    {
        if (_session is null)
        {
            return 0;
        }

        for (var index = 0; index < _session.Photos.Count; index++)
        {
            if (ReferenceEquals(_session.Photos[index], photo))
            {
                return index;
            }
        }

        return _session.CurrentIndex;
    }

    private Brush GetStatusBrush(PhotoEntry photo)
    {
        if (!ReferenceEquals(photo, _session?.Current))
        {
            return (Brush)FindResource("BorderBrush");
        }

        return photo.State switch
        {
            PhotoState.Del => (Brush)FindResource("TerracottaBrush"),
            PhotoState.Keep => (Brush)FindResource("MossBrush"),
            _ => (Brush)FindResource("PrimaryBrush")
        };
    }

    private Brush? GetDotBrush(PhotoEntry photo) => photo.State switch
    {
        PhotoState.Del => (Brush)FindResource("TerracottaBrush"),
        PhotoState.Keep => (Brush)FindResource("MossBrush"),
        _ => null
    };

    private void UndoButton_Click(object sender, RoutedEventArgs e) => UndoDecision();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => RedoDecision();
    private async void ReviewButton_Click(object sender, RoutedEventArgs e) => await OpenConfirmAsync();
    private void ConfirmBackButton_Click(object sender, RoutedEventArgs e) => SetScreen(Workspace);
    private async void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e) => await ExecuteDeleteAsync();
    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
    private void OpenRecycleBinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deleteSummary is not { Mode: DeleteMode.RecycleBin, Deleted: > 0 })
        {
            return;
        }

        try
        {
            FileMover.OpenRecycleBin();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or
                                           System.Security.SecurityException or System.ComponentModel.Win32Exception or
                                           InvalidOperationException)
        {
            ResultActionErrorText.Text = Text("OpenRecycleBinFailed");
        }
    }

    private async void BackToFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folderPath is not null)
        {
            await LoadFolderAsync(_folderPath, rescanCurrent: true);
        }
    }

    private void ChooseAnotherFolderButton_Click(object sender, RoutedEventArgs e) => ReturnToStart();

    private void ReturnToStart()
    {
        CancelAnimation();
        _scanCancellation?.Cancel();
        if (_portableRootId is { } rootId)
        {
            PortableDeviceStorage.ReleaseRoot(rootId);
        }

        _session = null;
        _folderPath = null;
        _portableRootId = null;
        _portableMode = false;
        _folderFileCount = 0;
        _markedIdentities.Clear();
        _identityVersions.Clear();
        _identityTasks.Clear();
        _identityTaskVersions.Clear();
        _metadata.Clear();
        _deleteSummary = null;
        _imageCache.Clear();
        _filmstrip.Clear();
        PhotoImage.Source = null;
        UnderPhotoImage.Source = null;
        HeaderFolderText.Text = string.Empty;
        HeaderFolderText.ToolTip = null;
        HeaderCountText.Text = string.Empty;
        StartErrorText.Text = string.Empty;
        ScanBannerText.Text = string.Empty;
        PortableModeBannerText.Text = string.Empty;
        PortableModeBannerText.Visibility = Visibility.Collapsed;
        PortableConfirmNoticeText.Text = string.Empty;
        PortableConfirmNoticeText.Visibility = Visibility.Collapsed;
        _viewId = _decoder.BeginView();
        SetScreen(StartScreen);
    }
    private void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FilmstripItem item })
        {
            BrowseTo(item.Photo);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            MaximizeButton.Content = Text("WindowMaximize");
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
            MaximizeButton.Content = Text("WindowRestore");
        }
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private sealed class FilmstripItem : System.ComponentModel.INotifyPropertyChanged
    {
        private BitmapSource? _bitmap;

        public FilmstripItem(PhotoEntry photo, BitmapSource? bitmap, Brush borderBrush, Brush? dotBrush)
        {
            Photo = photo;
            _bitmap = bitmap;
            BorderBrush = borderBrush;
            DotBrush = dotBrush;
        }

        public PhotoEntry Photo { get; }
        public Brush BorderBrush { get; }
        public Brush? DotBrush { get; }
        public BitmapSource? Bitmap
        {
            get => _bitmap;
            private set
            {
                _bitmap = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Bitmap)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void SetBitmap(BitmapSource bitmap) => Bitmap = bitmap;
    }

    private sealed record SortOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }
}
