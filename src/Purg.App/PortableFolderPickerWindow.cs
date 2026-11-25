using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Purg.Platform;

namespace Purg.App;

internal sealed class PortableFolderPickerWindow : Window
{
    private readonly IReadOnlyList<PortableDeviceInfo> _devices;
    private readonly ComboBox _devicePicker = new();
    private readonly ListBox _folderList = new();
    private readonly TextBlock _pathText = new();
    private readonly TextBlock _statusText = new();
    private readonly Button _upButton = new();
    private readonly Button _openButton = new();
    private readonly Button _chooseButton = new();
    private readonly HashSet<Guid> _rootIds = new();
    private Guid? _rootId;
    private string _currentPath = string.Empty;
    private bool _loading;

    public PortableFolderPickerWindow(IReadOnlyList<PortableDeviceInfo> devices)
    {
        _devices = devices;
        Title = MainWindow.Text("PortablePickerTitle");
        Width = 560;
        Height = 500;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x13, 0x12));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE7));
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new TextBlock
        {
            Text = MainWindow.Text("PortablePickerHeading"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        root.Children.Add(heading);

        _devicePicker.Margin = new Thickness(0, 0, 0, 12);
        _devicePicker.DisplayMemberPath = nameof(PortableDeviceInfo.Name);
        _devicePicker.Cursor = Cursors.Hand;
        _devicePicker.ItemsSource = _devices;
        _devicePicker.SelectionChanged += DevicePicker_SelectionChanged;
        Grid.SetRow(_devicePicker, 1);
        root.Children.Add(_devicePicker);

        var navigation = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _upButton.Content = MainWindow.Text("PortablePickerUp");
        _upButton.Padding = new Thickness(12, 4, 12, 4);
        _upButton.Margin = new Thickness(0, 0, 10, 0);
        _upButton.Cursor = Cursors.Hand;
        _upButton.Click += UpButton_Click;
        DockPanel.SetDock(_upButton, Dock.Left);
        navigation.Children.Add(_upButton);
        _openButton.Content = MainWindow.Text("PortablePickerOpen");
        _openButton.Padding = new Thickness(12, 4, 12, 4);
        _openButton.Margin = new Thickness(0, 0, 10, 0);
        _openButton.Cursor = Cursors.Hand;
        _openButton.IsEnabled = false;
        _openButton.Click += OpenButton_Click;
        DockPanel.SetDock(_openButton, Dock.Left);
        navigation.Children.Add(_openButton);
        _pathText.VerticalAlignment = VerticalAlignment.Center;
        _pathText.Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA1, 0x98));
        _pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        navigation.Children.Add(_pathText);
        Grid.SetRow(navigation, 2);
        root.Children.Add(navigation);

        _folderList.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x18, 0x16));
        _folderList.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x29, 0x26));
        _folderList.DisplayMemberPath = nameof(PortableFolderInfo.Name);
        _folderList.Cursor = Cursors.Hand;
        _folderList.SelectionChanged += (_, _) =>
            _openButton.IsEnabled = !_loading && _folderList.SelectedItem is PortableFolderInfo;
        _folderList.MouseDoubleClick += FolderList_MouseDoubleClick;
        Grid.SetRow(_folderList, 3);
        root.Children.Add(_folderList);

        _statusText.Margin = new Thickness(0, 10, 0, 12);
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8B, 0x84));
        Grid.SetRow(_statusText, 4);
        root.Children.Add(_statusText);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelButton = new Button
        {
            Content = MainWindow.Text("PortablePickerCancel"),
            Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand
        };
        cancelButton.Click += (_, _) => DialogResult = false;
        _chooseButton.Content = MainWindow.Text("PortablePickerChoose");
        _chooseButton.Padding = new Thickness(14, 7, 14, 7);
        _chooseButton.Margin = new Thickness(10, 0, 0, 0);
        _chooseButton.Background = new SolidColorBrush(Color.FromRgb(0xD4, 0x6A, 0x52));
        _chooseButton.Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x13, 0x12));
        _chooseButton.Cursor = Cursors.Hand;
        _chooseButton.IsEnabled = false;
        _chooseButton.Click += ChooseButton_Click;
        footer.Children.Add(cancelButton);
        footer.Children.Add(_chooseButton);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        Loaded += (_, _) =>
        {
            if (_devices.Count > 0)
            {
                _devicePicker.SelectedIndex = 0;
            }
            else
            {
                _statusText.Text = MainWindow.Text("PortablePickerNoDevices");
            }
        };
        Closed += (_, _) =>
        {
            foreach (var id in _rootIds)
            {
                PortableDeviceStorage.ReleaseRoot(id);
            }
        };
    }

    public Guid? SelectedRootId { get; private set; }

    public string SelectedDeviceName { get; private set; } = string.Empty;

    public string SelectedFolderPath { get; private set; } = string.Empty;

    private async void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_devicePicker.SelectedItem is not PortableDeviceInfo device)
        {
            return;
        }

        try
        {
            _loading = true;
            _devicePicker.IsEnabled = false;
            _chooseButton.IsEnabled = false;
            _statusText.Text = MainWindow.Text("PortablePickerConnecting");
            _rootId = await PortableDeviceStorage.RegisterDeviceRootAsync(device.Id);
            _rootIds.Add(_rootId.Value);
            _currentPath = string.Empty;
            await RefreshFoldersAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidOperationException or
                                           System.Runtime.InteropServices.COMException)
        {
            _rootId = null;
            _folderList.ItemsSource = null;
            _openButton.IsEnabled = false;
            _statusText.Text = MainWindow.Text("PortablePickerDeviceUnavailable");
        }
        finally
        {
            _loading = false;
            _devicePicker.IsEnabled = true;
            _chooseButton.IsEnabled = _rootId.HasValue && !string.IsNullOrEmpty(_currentPath);
        }
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _rootId is null || string.IsNullOrEmpty(_currentPath))
        {
            return;
        }

        var separator = _currentPath.LastIndexOf('/');
        _currentPath = separator < 0 ? string.Empty : _currentPath[..separator];
        await RefreshFoldersAsync();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _folderList.SelectedItem is not PortableFolderInfo folder)
        {
            return;
        }

        _currentPath = folder.RelativePath;
        await RefreshFoldersAsync();
    }

    private async void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await OpenButton_ClickAsync();
    }

    private async Task OpenButton_ClickAsync()
    {
        if (_loading || _folderList.SelectedItem is not PortableFolderInfo folder)
        {
            return;
        }

        _currentPath = folder.RelativePath;
        await RefreshFoldersAsync();
    }

    private async Task RefreshFoldersAsync()
    {
        if (_rootId is null)
        {
            return;
        }

        try
        {
            _loading = true;
            _chooseButton.IsEnabled = false;
            _upButton.IsEnabled = !string.IsNullOrEmpty(_currentPath);
            _pathText.Text = string.IsNullOrEmpty(_currentPath)
                ? MainWindow.Text("PortablePickerDeviceStorage")
                : DisplayNameSanitizer.Sanitize(_currentPath);
            _statusText.Text = MainWindow.Text("PortablePickerLoading");
            _folderList.ItemsSource = await PortableDeviceStorage.GetFoldersAsync(_rootId.Value, _currentPath);
            _statusText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidOperationException or
                                           System.Runtime.InteropServices.COMException)
        {
            _folderList.ItemsSource = null;
            _statusText.Text = MainWindow.Text("PortablePickerFolderUnavailable");
        }
        finally
        {
            _loading = false;
            _chooseButton.IsEnabled = _rootId.HasValue && !string.IsNullOrEmpty(_currentPath);
        }
    }

    private async void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rootId is null || string.IsNullOrEmpty(_currentPath) ||
            _devicePicker.SelectedItem is not PortableDeviceInfo device)
        {
            return;
        }

        try
        {
            _loading = true;
            _chooseButton.IsEnabled = false;
            var selectedFolder = await PortableDeviceStorage.GetFolderAsync(_rootId.Value, _currentPath);
            var selectedRootId = PortableDeviceStorage.RegisterRoot(selectedFolder);
            _rootIds.Remove(_rootId.Value);
            PortableDeviceStorage.ReleaseRoot(_rootId.Value);
            SelectedRootId = selectedRootId;
            SelectedDeviceName = device.Name;
            SelectedFolderPath = _currentPath;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidOperationException or
                                           System.Runtime.InteropServices.COMException)
        {
            _statusText.Text = MainWindow.Text("PortablePickerFolderMissing");
            _loading = false;
            _chooseButton.IsEnabled = _rootId.HasValue && !string.IsNullOrEmpty(_currentPath);
        }
    }
}
