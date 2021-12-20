﻿using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using Microsoft.WindowsAPICodePack.Dialogs;
using ModernWpf;
using ModernWpf.Controls;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static ADB_Explorer.Converters.FileTypeClass;
using static ADB_Explorer.Helpers.VisibilityHelper;
using static ADB_Explorer.Models.AdbExplorerConst;
using static ADB_Explorer.Models.Data;

namespace ADB_Explorer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer ConnectTimer = new();
        private Task listDirTask;
        private Task unknownFoldersTask;
        private DispatcherTimer dirListUpdateTimer;
        private CancellationTokenSource dirListCancelTokenSource;
        private CancellationTokenSource determineFoldersCancelTokenSource;
        private ConcurrentQueue<FileStat> waitingFileStats;
        private ItemsPresenter ExplorerContentPresenter;
        private ScrollViewer ExplorerScroller;

        private SolidColorBrush qrForeground, qrBackground;

        public static MDNS MdnsService { get; set; } = new();

        public bool ListingInProgress { get { return listDirTask is not null; } }

        private double ColumnHeaderHeight
        {
            get
            {
                GetExplorerContentPresenter();
                if (ExplorerContentPresenter is null)
                    return 0;
                
                double height = ExplorerGrid.ActualHeight - ExplorerContentPresenter.ActualHeight - HorizontalScrollBarHeight;
                
                return height;
            }
        }
        private double DataGridContentWidth
        {
            get
            {
                GetExplorerContentPresenter();
                return ExplorerContentPresenter is null ? 0 : ExplorerContentPresenter.ActualWidth;
            }
        }
        private double HorizontalScrollBarHeight => ExplorerScroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible
                    ? SystemParameters.HorizontalScrollBarHeight : 0;

        private readonly List<MenuItem> PathButtons = new();

        /// <summary>
        /// Sets the app resource font family for font icons depending on the OS build
        /// </summary>
        public static void SetIconFont()
        {
            Application.Current.Resources["GlyphFontFamily"] = GetIconFont();
        }

        public static FontFamily GetIconFont() =>
            new(Environment.OSVersion.Version.Build switch
            {
                > 21000 => "Segoe Fluent Icons", // Windows 11
                _ => "Segoe MDL2 Assets",
            });

        private void GetExplorerContentPresenter()
        {
            if (ExplorerContentPresenter is null && VisualTreeHelper.GetChild(ExplorerGrid, 0) is Border border && border.Child is ScrollViewer scroller && scroller.Content is ItemsPresenter presenter)
            {
                ExplorerScroller = scroller;
                ExplorerContentPresenter = presenter;
            }
        }

        private string SelectedFilesTotalSize
        {
            get
            {
                var files = ExplorerGrid.SelectedItems.OfType<FileClass>().ToList();
                if (files.Any(i => i.Type != FileType.File)) return "0";

                ulong totalSize = 0;
                files.ForEach(f => totalSize += f.Size.GetValueOrDefault(0));

                return totalSize.ToSize();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            SetIconFont();

            fileOperationQueue = new(this.Dispatcher);
            LaunchSequence();

            ConnectTimer.Interval = CONNECT_TIMER_INTERVAL;
            ConnectTimer.Tick += ConnectTimer_Tick;
            ConnectTimer.Start();

            UpperProgressBar.DataContext = fileOperationQueue;

            //InputLanguageManager.Current.InputLanguageChanged +=
            //    new InputLanguageEventHandler((sender, e) =>
            //    {
            //        UpdateInputLang();
            //    });
        }

        private void InitializeExplorerContextMenu(MenuType type, object dataContext = null)
        {
            MenuItem pullMenu = FindResource("ContextMenuCopyItem") as MenuItem;
            MenuItem deleteMenu = FindResource("ContextMenuDeleteItem") as MenuItem;
            ExplorerGrid.ContextMenu.Items.Clear();

            switch (type)
            {
                case MenuType.ExplorerItem:
                    pullMenu.IsEnabled = false;
                    if (dataContext is FileClass file && file.Type is FileType.File or FileType.Folder)
                        pullMenu.IsEnabled = true;

                    ExplorerGrid.ContextMenu.Items.Add(pullMenu);
                    ExplorerGrid.ContextMenu.Items.Add(deleteMenu);
                    break;
                case MenuType.EmptySpace:
                    ExplorerGrid.ContextMenu.Items.Add(deleteMenu);
                    break;
                case MenuType.Header:
                    break;
                default:
                    break;
            }
        }

        private void InitializePathContextMenu(FrameworkElement sender)
        {
            if (sender.ContextMenu is null)
                return;

            MenuItem headerMenu = FindResource("PathMenuHeader") as MenuItem;
            MenuItem editMenu = FindResource("PathMenuEdit") as MenuItem;
            MenuItem copyMenu = FindResource("PathMenuCopy") as MenuItem;
            sender.ContextMenu.Items.Clear();

            sender.ContextMenu.Items.Add(headerMenu);
            sender.ContextMenu.Items.Add(new Separator());
            sender.ContextMenu.Items.Add(editMenu);
            sender.ContextMenu.Items.Add(copyMenu);

            sender.ContextMenu.Visibility = Visible(sender.ContextMenu.HasItems);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (listDirTask is not null)
            {
                StopDirectoryList();
                StopDetermineFolders();
                AndroidFileList.RemoveAll();
            }

            ConnectTimer.Stop();
        }

        private void SetTheme(object theme) => SetTheme((ApplicationTheme)theme);

        private void SetTheme(ApplicationTheme theme)
        {
            ThemeManager.Current.ApplicationTheme = theme;

            foreach (string key in ((ResourceDictionary)Application.Current.Resources["DynamicBrushes"]).Keys)
            {
                SetResourceColor(theme, key);
            }

            Storage.StoreEnum(ThemeManager.Current.ApplicationTheme);

            SetQrColors(theme);
        }

        private static void SetResourceColor(ApplicationTheme theme, string resource)
        {
            Application.Current.Resources[resource] = new SolidColorBrush((Color)Application.Current.Resources[$"{theme}{resource}"]);
        }

        private void SetQrColors(ApplicationTheme theme)
        {
            switch (theme)
            {
                case ApplicationTheme.Light:
                    qrBackground = QR_BACKGROUND_LIGHT;
                    qrForeground = QR_FOREGROUND_LIGHT;
                    break;
                case ApplicationTheme.Dark:
                    qrBackground = QR_BACKGROUND_DARK;
                    qrForeground = QR_FOREGROUND_DARK;
                    break;
                default:
                    break;
            }
        }

        private void PathBox_GotFocus(object sender, RoutedEventArgs e)
        {
            FocusPathBox();
        }

        private void FocusPathBox()
        {
            PathStackPanel.Visibility = Visibility.Collapsed;
            PathBox.Text = PathBox.Tag?.ToString();
            PathBox.IsReadOnly = false;
            PathBox.SelectAll();

            //UpdateInputLang();
        }

        private void UnfocusPathBox()
        {
            PathStackPanel.Visibility = Visibility.Visible;
            PathBox.Clear();
            PathBox.IsReadOnly = true;
            FileOperationsSplitView.Focus();
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (ExplorerGrid.IsVisible)
                {
                    if (NavigateToPath(PathBox.Text))
                        return;
                }
                else
                {
                    if (!InitNavigation(PathBox.Text))
                    {
                        DriveViewNav();
                        return;
                    }
                }

                ExplorerGrid.Focus();
            }
            else if (e.Key == Key.Escape)
            {
                UnfocusPathBox();
            }
        }

        private void PathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
        }

        private void DataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && ExplorerGrid.SelectedItems.Count == 1)
                DoubleClick(ExplorerGrid.SelectedItem);
        }

        private void DoubleClick(object source)
        {
            if (source is FileClass file)
            {
                switch (file.Type)
                {
                    case FileType.File:
                        if (PullOnDoubleClickCheckBox.IsChecked == true)
                            CopyFiles(true);
                        break;
                    case FileType.Folder:
                        NavigateToPath(file.Path);
                        break;
                    default:
                        break;
                }
            }
        }

        private void ExplorerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TotalSizeBlock.Text = SelectedFilesTotalSize;

            var items = ExplorerGrid.SelectedItems.Cast<FileClass>();
            PullMenuButton.IsEnabled = items.Any() && items.All(f => f.Type
                is FileType.File
                or FileType.Folder);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            UnfocusPathBox();
        }

        private void LaunchSequence()
        {
            var theme = Storage.RetrieveEnum<ApplicationTheme>();
            SetTheme(theme);
            if (theme == ApplicationTheme.Light)
                LightThemeRadioButton.IsChecked = true;
            else
                DarkThemeRadioButton.IsChecked = true;

            Title = Properties.Resources.AppDisplayName;
            LoadSettings();
            DeviceListSetup();

            TestCurrentOperation();
        }

        private void DeviceListSetup(string selectedAddress = "")
        {
            var init = !Devices.Update(ADBService.GetDevices());

            DevicesList.ItemsSource = Devices.List;
            DevicesList.Items.Refresh();

            if (Devices.Current is null || Devices.Current.IsOpen && Devices.Current.Status != DeviceClass.DeviceStatus.Online)
                ClearDrives();

            if (!Devices.DevicesAvailable())
            {
                Title = $"{Properties.Resources.AppDisplayName} - NO CONNECTED DEVICES";
                ClearExplorer();
                ClearDrives();
                return;
            }
            else
            {
                if (Devices.DevicesAvailable(true))
                    return;

                if (AutoOpenCheckBox.IsChecked != true)
                {
                    Devices.Current?.SetOpen(false);

                    Title = Properties.Resources.AppDisplayName;
                    ClearExplorer();
                    return;
                }

                if (!Devices.SetCurrentDevice(selectedAddress))
                    return;

                if (!ConnectTimer.IsEnabled)
                    DevicesSplitView.IsPaneOpen = false;
            }

            Devices.Current.SetOpen(true);
            CurrentADBDevice = new(Devices.Current.ID);
            if (init)
                InitDevice();
        }

        private void LoadSettings()
        {
            if (Storage.RetrieveValue(Settings.defaultFolder) is string path && !string.IsNullOrEmpty(path))
                DefaultFolderBlock.Text = path;

            if (Storage.RetrieveBool(Settings.pullOnDoubleClick) is bool copy)
                PullOnDoubleClickCheckBox.IsChecked = copy;

            if (Storage.RetrieveBool(Settings.rememberIp) is bool remIp)
                RememberIpCheckBox.IsChecked = remIp;

            RememberPortCheckBox.IsEnabled = (bool)RememberIpCheckBox.IsChecked;

            if (RememberPortCheckBox.IsEnabled
                    && Storage.RetrieveBool(Settings.rememberPort) is bool remPort)
            {
                RememberPortCheckBox.IsChecked = remPort;
            }

            if (Storage.RetrieveBool(Settings.autoOpen) is bool autoOpen)
                AutoOpenCheckBox.IsChecked = autoOpen;

            if (Storage.RetrieveBool(Settings.showExtensions) is bool showExt)
                ShowExtensionsCheckBox.IsChecked = showExt;

            if (Storage.RetrieveBool(Settings.showHiddenItems) is bool showHidden)
                ShowHiddenCheckBox.IsChecked = showHidden;

            bool extendedView = Storage.RetrieveBool(Settings.showExtendedView) is bool val && val;
            FileOpDetailedRadioButton.IsChecked = extendedView;
            FileOpCompactRadioButton.IsChecked = !extendedView;

            CurrentOperationDataGrid.ItemsSource = fileOperationQueue.CurrentOperations;
            PendingOperationsDataGrid.ItemsSource = fileOperationQueue.PendingOperations;
            CompletedOperationsDataGrid.ItemsSource = fileOperationQueue.CompletedOperations;

            InitColumnConfig();
        }

        private void InitColumnConfig()
        {
            foreach (var type in (ColumnType[]) Enum.GetValues(typeof(ColumnType)))
            {
                var possibleColumns = POSSIBLE_COLUMNS.Where(c => c.IsType(type)).Select(c => c.Name).ToList();
                List<string> requestedColumns = new();
                if (Storage.RetrieveValue(type) is List<string> cols)
                    requestedColumns.AddRange(cols);

                var configs = requestedColumns?.Select(item => new ColumnConfig(selected: item, columnList: possibleColumns)).ToList();
                configs.Add(new(selected: "[Add]",columnList: possibleColumns, isExisting: false));

                ColumnConfig columns = new()
                {
                    Title = $"{type} Operation{(type is ColumnType.Current ? "" : "s")}",
                    Items = configs
                };
                ColumnConfigs.Add(columns);
            }

            FileOpSettingsTree.ItemsSource = ColumnConfigs;
        }

        private void InitDevice()
        {
            Title = $"{Properties.Resources.AppDisplayName} - {Devices.Current.Name}";

            RefreshDrives();
            DriveViewNav();
            UpdateAndroidVersion();

            if (Devices.Current.Drives.Count < 1)
            {
                // Shouldn't actually happen
                InitNavigation();
            }
        }

        private void DriveViewNav()
        {
            ClearExplorer();
            ExplorerGrid.Visibility = Visibility.Collapsed;
            DrivesItemRepeater.Visibility = Visibility.Visible;
            PathBox.IsEnabled = true;

            MenuItem button = CreatePathButton(Devices.Current, Devices.Current.Name);
            button.ContextMenu = Resources["PathButtonsMenu"] as ContextMenu;
            AddPathButton(button);
        }

        private void UpdateAndroidVersion()
        {
            string androidVer = CurrentADBDevice.GetAndroidVersion();
            AndroidVersionBlock.Text = $"Android {androidVer}";

            sbyte ver = 0;
            sbyte.TryParse(androidVer.Split('.')[0], out ver);
            UnsupportedAndroidIcon.Visibility = Visible(ver > 0 && ver < MIN_SUPPORTED_ANDROID_VER);
        }

        private static void CombinePrettyNames()
        {
            foreach (var drive in Devices.Current.Drives.Where(d => d.Type != Models.DriveType.Root))
            {
                CurrentPrettyNames.TryAdd(drive.Path, drive.Type == Models.DriveType.External
                    ? drive.ID : drive.PrettyName);
            }
            foreach (var item in SPECIAL_FOLDERS_PRETTY_NAMES)
            {
                CurrentPrettyNames.TryAdd(item.Key, item.Value);
            }
        }

        private bool InitNavigation(string path = "")
        {
            DrivesItemRepeater.Visibility = Visibility.Collapsed;
            ExplorerGrid.Visibility = Visibility.Visible;
            CombinePrettyNames();

            unknownFoldersTask = Task.Run(() => { });
            dirListCancelTokenSource = new CancellationTokenSource();
            determineFoldersCancelTokenSource = new CancellationTokenSource();
            waitingFileStats = new ConcurrentQueue<FileStat>();
            dirListUpdateTimer = new DispatcherTimer
            {
                Interval = DIR_LIST_UPDATE_INTERVAL
            };
            dirListUpdateTimer.Tick += DirListUpdateTimer_Tick;

            ExplorerGrid.ItemsSource = AndroidFileList;
            PushMenuButton.IsEnabled = true;
            HomeButton.IsEnabled = Devices.Current.Drives.Any();
            NavHistory.Reset();

            return string.IsNullOrEmpty(path)
                ? NavigateToPath(DEFAULT_PATH)
                : NavigateToPath(path);
        }

        private void ConnectTimer_Tick(object sender, EventArgs e)
        {
            if (MdnsService.State == MDNS.MdnsState.Disabled)
            {
                // do nothing if amount of devices and their types haven't changed
                if (Devices.DevicesChanged(ADBService.GetDevices()))
                    DeviceListSetup();
            }
        }

        private void MdnsCheck()
        {
            Dispatcher.BeginInvoke(() =>
            {
                Task.Run(() =>
                {
                    return MdnsService.State = ADBService.CheckMDNS()
                        ? MDNS.MdnsState.Running
                        : MDNS.MdnsState.NotRunning;
                });
            });
        }

        private void StartDirectoryList(string path)
        {
            Cursor = Cursors.AppStarting;

            Dispatcher.BeginInvoke(() =>
            {
                StopDirectoryList();
                StopDetermineFolders();

                AndroidFileList.RemoveAll();

                determineFoldersCancelTokenSource = new CancellationTokenSource();
                dirListCancelTokenSource = new CancellationTokenSource();
                waitingFileStats = new ConcurrentQueue<FileStat>();

                listDirTask = Task.Run(() => CurrentADBDevice.ListDirectory(path, ref waitingFileStats, dirListCancelTokenSource.Token));

                if (listDirTask.Wait(DIR_LIST_SYNC_TIMEOUT))
                {
                    StopDirectoryList();
                }
                else
                {
                    DirectoryLoadingProgressBar.Visibility =
                    UnfinishedBlock.Visibility = Visibility.Visible;

                    UpdateDirectoryList();
                    dirListUpdateTimer.Start();
                    listDirTask.ContinueWith((t) => Application.Current?.Dispatcher.BeginInvoke(() => StopDirectoryList()));
                }
            });
        }

        private void UpdateDirectoryList()
        {
            if (listDirTask is null) return;

            bool wasEmpty = (AndroidFileList.Count == 0);

            var newFiles = waitingFileStats.DequeueAllExisting().Select(f => FileClass.GenerateAndroidFile(f)).ToArray();
            AndroidFileList.AddRange(newFiles);
            var unknownFiles = newFiles.Where(f => f.Type == FileType.Unknown);
            StartDetermineFolders(unknownFiles);

            if (wasEmpty && (AndroidFileList.Count > 0))
            {
                ExplorerGrid.ScrollIntoView(ExplorerGrid.Items[0]);
            }
        }

        private void StopDirectoryList()
        {
            if (listDirTask is null) return;

            Cursor = null;
            DirectoryLoadingProgressBar.Visibility =
            UnfinishedBlock.Visibility = Visibility.Collapsed;

            dirListUpdateTimer.Stop();
            dirListCancelTokenSource.Cancel();
            listDirTask.Wait();
            UpdateDirectoryList();
            listDirTask = null;
            dirListCancelTokenSource = null;
        }

        private void StartDetermineFolders(IEnumerable<FileClass> files)
        {
            unknownFoldersTask.ContinueWith((t) =>
            {
                if (determineFoldersCancelTokenSource != null)
                {
                    DetermineFolders(files, determineFoldersCancelTokenSource.Token);
                }
            });
        }

        private static void DetermineFolders(IEnumerable<FileClass> files, CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                else if (CurrentADBDevice.IsDirectory(file.Path))
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => { file.Type = FileType.Folder; });
                }
            }
        }

        private void StopDetermineFolders()
        {
            determineFoldersCancelTokenSource.Cancel();
            unknownFoldersTask.Wait();
            determineFoldersCancelTokenSource = null;
        }

        public bool NavigateToPath(string path, bool bfNavigated = false)
        {
            ExplorerGrid.Focus();
            if (path is null) return false;

            string realPath;
            try
            {
                realPath = CurrentADBDevice.TranslateDevicePath(path);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Navigation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!bfNavigated)
                NavHistory.Navigate(realPath);

            UpdateNavButtons();

            PathBox.Tag =
            CurrentPath = realPath;
            PopulateButtons(realPath);
            ParentPath = CurrentADBDevice.TranslateDeviceParentPath(CurrentPath);

            ParentButton.IsEnabled = CurrentPath != ParentPath;

            StartDirectoryList(realPath);
            return true;
        }

        private void UpdateNavButtons()
        {
            BackButton.IsEnabled = NavHistory.BackAvailable;
            ForwardButton.IsEnabled = NavHistory.ForwardAvailable;
        }

        private void DirListUpdateTimer_Tick(object sender, EventArgs e)
        {
            UpdateDirectoryList();
        }

        private void PopulateButtons(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            var expectedLength = 0.0;
            List<MenuItem> tempButtons = new();
            List<string> pathItems = new();

            // On special cases, cut prefix of the path and replace with a pretty button
            var specialPair = CurrentPrettyNames.FirstOrDefault(kv => path.StartsWith(kv.Key));
            if (specialPair.Key != null)
            {
                MenuItem button = CreatePathButton(specialPair);
                tempButtons.Add(button);
                pathItems.Add(specialPair.Key);
                path = path[specialPair.Key.Length..].TrimStart('/');
                expectedLength = PathButtonLength.ButtonLength(button);
            }

            var dirs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in dirs)
            {
                pathItems.Add(dir);
                var dirPath = string.Join('/', pathItems).Replace("//", "/");
                MenuItem button = CreatePathButton(dirPath, dir);
                tempButtons.Add(button);
                expectedLength += PathButtonLength.ButtonLength(button);
            }

            expectedLength += (tempButtons.Count - 1) * PathButtonLength.ButtonLength(CreatePathArrow());

            int i = 0;
            for (; i < PathButtons.Count && i < tempButtons.Count; i++)
            {
                var oldB = PathButtons[i];
                var newB = tempButtons[i];
                if (oldB.Header.ToString() != newB.Header.ToString() ||
                    oldB.Tag.ToString() != newB.Tag.ToString())
                {
                    break;
                }
            }
            PathButtons.RemoveRange(i, PathButtons.Count - i);
            PathButtons.AddRange(tempButtons.GetRange(i, tempButtons.Count - i));

            // StackPanel's margin is 10, while TextBox's margins is 6, thus the offset is 4x2
            ConsolidateButtons(expectedLength + 8);
        }

        private void ConsolidateButtons(double expectedLength)
        {
            if (expectedLength > PathBox.ActualWidth)
                expectedLength += PathButtonLength.ButtonLength(CreateExcessButton());

            double excessLength = expectedLength - PathBox.ActualWidth;
            List<MenuItem> excessButtons = new();
            PathStackPanel.Children.Clear();

            if (excessLength > -10)
            {
                int i = 0;
                while (excessLength >= -10 && PathButtons.Count - excessButtons.Count > 1)
                {
                    excessButtons.Add(PathButtons[i]);
                    PathButtons[i].ContextMenu = null;
                    excessLength -= PathButtonLength.ButtonLength(PathButtons[i]);

                    i++;
                }

                AddExcessButton(excessButtons);
            }

            foreach (var item in PathButtons.Except(excessButtons))
            {
                if (PathStackPanel.Children.Count > 0)
                    AddPathArrow();

                item.ContextMenu = Resources["PathButtonsMenu"] as ContextMenu;
                AddPathButton(item);
            }
        }

        private MenuItem CreateExcessButton() => new MenuItem()
        {
            Height = 24,
            Header = new FontIcon() { Glyph = "\uE712", FontSize = 16, Style = Resources["GlyphFont"] as Style, ContextMenu = Resources["PathButtonsMenu"] as ContextMenu }
        };

        private void AddExcessButton(List<MenuItem> excessButtons = null)
        {
            if (excessButtons is not null && !excessButtons.Any())
                return;

            var button = CreateExcessButton();
            button.ItemsSource = excessButtons;
            Menu excessMenu = new() { Height = 24 };
            excessMenu.ContextMenuOpening += PathButton_ContextMenuOpening;

            excessMenu.Items.Add(button);
            PathStackPanel.Children.Add(excessMenu);
        }

        private MenuItem CreatePathButton(KeyValuePair<string, string> kv) => CreatePathButton(kv.Key, kv.Value);
        private MenuItem CreatePathButton(object path, string name)
        {
            MenuItem button = new()
            {
                Header = new TextBlock() { Text = name, Margin = new(0, 0, 0, 1) },
                Tag = path,
                Padding = new Thickness(10, 0, 10, 0),
                Height = 24,
            };
            button.Click += PathButton_Click;
            button.ContextMenuOpening += PathButton_ContextMenuOpening;

            return button;
        }

        private void PathButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            InitializePathContextMenu((FrameworkElement)sender);
        }

        private void AddPathButton(MenuItem button)
        {
            Menu menu = new() { Height = 24 };
            ((Menu)button.Parent)?.Items.Clear();
            menu.Items.Add(button);
            PathStackPanel.Children.Add(menu);
        }

        private FontIcon CreatePathArrow() => new()
        {
            Glyph = " \uE970 ",
            FontSize = 8,
            Style = Resources["GlyphFont"] as Style,
            ContextMenu = Resources["PathButtonsMenu"] as ContextMenu
        };

        private void AddPathArrow(bool append = true)
        {
            var arrow = CreatePathArrow();
            arrow.ContextMenuOpening += PathButton_ContextMenuOpening;

            if (append)
                PathStackPanel.Children.Add(arrow);
            else
                PathStackPanel.Children.Insert(0, arrow);
        }

        private void PathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                if (item.Tag is string path and not "")
                    NavigateToPath(path);
                else if (item.Tag is DeviceClass)
                    RefreshDrives(true);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            SettingsSplitView.IsPaneOpen = true;
        }

        private void ParentButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPath(ParentPath);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPath(NavHistory.GoBack(), true);
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPath(NavHistory.GoForward(), true);
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1)
                NavigateToPath(NavHistory.GoBack(), true);
            else if (e.ChangedButton == MouseButton.XButton2)
                NavigateToPath(NavHistory.GoForward(), true);
        }

        private void DataGridRow_KeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key;
            if (key == Key.Enter)
            {
                if (ExplorerGrid.SelectedItems.Count == 1)
                    DoubleClick(ExplorerGrid.SelectedItem);
            }
            else if (key == Key.Back)
            {
                NavigateToPath(NavHistory.GoBack(), true);
            }
            else
                return;

            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (ExplorerGrid.Items.Count < 1) return;

            var key = e.Key;
            if (key == Key.Down)
            {
                if (ExplorerGrid.SelectedItems.Count == 0)
                    ExplorerGrid.SelectedIndex = 0;
                else
                    ExplorerGrid.SelectedIndex++;

                ExplorerGrid.ScrollIntoView(ExplorerGrid.SelectedItem);
            }
            else if (key == Key.Up)
            {
                if (ExplorerGrid.SelectedItems.Count == 0)
                    ExplorerGrid.SelectedItem = ExplorerGrid.Items[^1];
                else if (ExplorerGrid.SelectedIndex > 0)
                    ExplorerGrid.SelectedIndex--;

                ExplorerGrid.ScrollIntoView(ExplorerGrid.SelectedItem);
            }
            else if (key == Key.Enter)
            {
                if (ExplorerGrid.SelectedItems.Count == 1)
                    DoubleClick(ExplorerGrid.SelectedItem);
            }
            else if (key == Key.Back)
            {
                NavigateToPath(NavHistory.GoBack(), true);
            }
            else
                return;

            e.Handled = true;
        }

        //private void UpdateInputLang()
        //{
        //    //InputLangBlock.Text = InputLanguageManager.Current.CurrentInputLanguage.TwoLetterISOLanguageName.ToUpper();
        //}

        private void CopyMenuButton_Click(object sender, RoutedEventArgs e)
        {
            
        }

        private void CopyFiles(bool quick = false)
        {
            int itemsCount = ExplorerGrid.SelectedItems.Count;
            string path;

            if (quick)
            {
                path = DefaultFolderBlock.Text;
            }
            else
            {
                var dialog = new CommonOpenFileDialog()
                {
                    IsFolderPicker = true,
                    Multiselect = false,
                    DefaultDirectory = DefaultFolderBlock.Text,
                    Title = "Select destination for " + (itemsCount > 1 ? "multiple items" : ExplorerGrid.SelectedItem)
                };

                if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                path = dialog.FileName;
            }

            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "Destination Path Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            foreach (FileClass item in ExplorerGrid.SelectedItems)
            {
                fileOperationQueue.AddOperation(new FilePullOperation(Dispatcher, CurrentADBDevice, item.Path, path));
            }
        }

        private void PasteFiles(bool isFolderPicker)
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = isFolderPicker,
                Multiselect = true,
                DefaultDirectory = DefaultFolderBlock.Text,
                Title = $"Select {(isFolderPicker ? "folder" : "file")}s to copy"
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return;

            foreach (var item in dialog.FileNames)
            {
                fileOperationQueue.AddOperation(new FilePushOperation(Dispatcher, CurrentADBDevice, item, CurrentPath));
            }
        }

        private void TestCurrentOperation()
        {
             //fileOperationQueue.AddOperation(InProgressTestOperation.CreateProgressStart(Dispatcher, CurrentADBDevice, "Shalom.exe"));
             //fileOperationQueue.AddOperation(InProgressTestOperation.CreateFileInProgress(Dispatcher, CurrentADBDevice, "Shalom.exe"));
            //fileOperationQueue.AddOperation(InProgressTestOperation.CreateFolderInProgress(Dispatcher, CurrentADBDevice, "Shalom"));
        }

        private void LightThemeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            SetTheme(ApplicationTheme.Light);
        }

        private void DarkThemeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            SetTheme(ApplicationTheme.Dark);
        }

        private void ContextMenuCopyItem_Click(object sender, RoutedEventArgs e)
        {
            CopyFiles();
        }

        private void DataGridRow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var row = sender as DataGridRow;

            if (row.IsSelected == false)
            {
                ExplorerGrid.SelectedItems.Clear();
            }

            if (e.ChangedButton == MouseButton.Right)
            {
                InitializeExplorerContextMenu(e.OriginalSource is Border ? MenuType.EmptySpace : MenuType.ExplorerItem, row.DataContext);
            }

            if (e.OriginalSource is Border)
                return;

            row.IsSelected = true;
        }

        private void ChangeDefaultFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true,
                Multiselect = false
            };
            if (DefaultFolderBlock.Text != "[not set]")
                dialog.DefaultDirectory = DefaultFolderBlock.Text;

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                DefaultFolderBlock.Text = dialog.FileName;
                Storage.StoreValue(Settings.defaultFolder, dialog.FileName);
            }
        }

        private void PullOnDoubleClickCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.pullOnDoubleClick, PullOnDoubleClickCheckBox.IsChecked);
        }

        private void InputLangBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void OpenDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            DevicesSplitView.IsPaneOpen = true;
        }

        private void ConnectNewDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectNewDevice();
        }

        private void ConnectNewDevice()
        {
            string deviceAddress = $"{NewDeviceIpBox.Text}:{NewDevicePortBox.Text}";
            try
            {
                ADBService.ConnectNetworkDevice(deviceAddress);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains($"failed to connect to {deviceAddress}"))
                {
                    // propose pairing

                }
                else
                    MessageBox.Show(ex.Message, "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);

                return;
            }

            if (RememberIpCheckBox.IsChecked == true)
                Storage.StoreValue(Settings.lastIp, NewDeviceIpBox.Text);

            if (RememberPortCheckBox.IsChecked == true)
                Storage.StoreValue(Settings.lastPort, NewDevicePortBox.Text);

            NewDeviceIpBox.Text = "";
            NewDevicePortBox.Text = "";
            NewDevicePanelVisibility(false);
            DeviceListSetup(deviceAddress);
        }

        private void EnableConnectButton()
        {
            ConnectNewDeviceButton.IsEnabled = NewDeviceIpBox.Text is string ip
                && !string.IsNullOrWhiteSpace(ip)
                && ip.Count(c => c == '.') == 3
                && ip.Split('.').Count(i => byte.TryParse(i, out _)) == 4
                && NewDevicePortBox.Text is string port
                && !string.IsNullOrWhiteSpace(port)
                && ushort.TryParse(port, out _);
        }

        private void NewDeviceIpBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            EnableConnectButton();
        }

        private void OpenNewDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            NewDevicePanelVisibility(!NewDevicePanelVisibility());
            Devices.UnselectAll();
            DevicesList.Items.Refresh();

            if (NewDevicePanelVisibility())
            {
                RetrieveIp();

                PairingQrImage.Source = WiFiPairingService.GenerateQrCode(qrBackground, qrForeground);
            }
        }

        private void NewDevicePanelVisibility(bool open)
        {
            if (NewDevicePanel is null)
                return;

            if (open)
            {
                if (NewDevicePanel.Visibility == Visibility.Collapsed)
                    NewDevicePanel.Visibility = Visibility.Visible;

                NewDevicePanel.Tag = "Open";
            }
            else
                NewDevicePanel.Tag = "Closed";
        }

        private bool NewDevicePanelVisibility()
        {
            return NewDevicePanel?.Tag?.ToString() == "Open";
        }

        private void RetrieveIp()
        {
            NewDeviceIpBox.Clear();
            NewDevicePortBox.Clear();

            if (RememberIpCheckBox.IsChecked == true
                && Storage.RetrieveValue(Settings.lastIp) is string lastIp
                && !Devices.List.Find(d => d.ID.Split(':')[0] == lastIp))
            {
                NewDeviceIpBox.Text = lastIp;
                if (RememberPortCheckBox.IsChecked == true
                    && Storage.RetrieveValue(Settings.lastPort) is string lastPort)
                {
                    NewDevicePortBox.Text = lastPort;
                }
            }
        }

        private void RememberIpCheckBox_Click(object sender, RoutedEventArgs e)
        {
            RememberPortCheckBox.IsEnabled = (bool)RememberIpCheckBox.IsChecked;
            if (!RememberPortCheckBox.IsEnabled)
                RememberPortCheckBox.IsChecked = false;

            Storage.StoreValue(Settings.rememberIp, RememberIpCheckBox.IsChecked);
        }

        private void OpenDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DeviceClass device && device.Status != DeviceClass.DeviceStatus.Offline)
            {
                device.SetOpen();
                CurrentADBDevice = new(device.ID);

                ClearExplorer();
                DevicesList.Items.Refresh();
                InitDevice();

                DevicesSplitView.IsPaneOpen = false;
            }
        }

        private void DisconnectDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DeviceClass device)
            {
                RemoveDevice(device);
            }
        }

        private void RemoveDevice(DeviceClass device)
        {
            try
            {
                if (device.Type == DeviceClass.DeviceType.Emulator)
                {
                    ADBService.KillEmulator(device.ID);
                }
                else
                {
                    ADBService.DisconnectNetworkDevice(device.ID);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Disconnection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (device.IsOpen)
            {
                ClearDrives();
                ClearExplorer();
                device.SetOpen(false);
                CurrentADBDevice = null;
            }
            DeviceListSetup();
            DevicesList.Items.Refresh();
        }

        private void ClearExplorer()
        {
            CurrentPrettyNames.Clear();
            AndroidFileList.Clear();
            ExplorerGrid.Items.Refresh();
            PathStackPanel.Children.Clear();
            CurrentPath = null;
            PathBox.Tag = null;
            NavHistory.Reset();
            PushMenuButton.IsEnabled =
            PathBox.IsEnabled =
            NewMenuButton.IsEnabled =
            PullMenuButton.IsEnabled =
            BackButton.IsEnabled =
            ForwardButton.IsEnabled =
            HomeButton.IsEnabled =
            ParentButton.IsEnabled = false;
        }

        private void ClearDrives()
        {
            Devices.Current?.Drives.Clear();
            DrivesItemRepeater.ItemsSource = null;
        }

        private void DevicesSplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
        {
            NewDevicePanelVisibility(false);
            Devices.UnselectAll();
            DevicesList.Items.Refresh();
        }

        private void NewDeviceIpBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ConnectNewDeviceButton.IsEnabled)
                ConnectNewDevice();
        }

        private void RememberPortCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.rememberPort, RememberPortCheckBox.IsChecked);
        }

        private void ExplorerGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            ExplorerGrid.ContextMenu.Visibility = Visible(ExplorerGrid.ContextMenu.HasItems);
        }

        private void ListViewItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ModernWpf.Controls.ListViewItem item && item.DataContext is DeviceClass device && !device.IsSelected)
            {
                device.SetSelected();
                DevicesList.Items.Refresh();
            }
        }

        private void AutoOpenCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.autoOpen, AutoOpenCheckBox.IsChecked);
        }

        private void ExplorerGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var point = e.GetPosition(ExplorerGrid);
            var actualRowWidth = 0.0;
            foreach (var item in ExplorerGrid.Columns)
            {
                actualRowWidth += item.ActualWidth;
            }

            if (point.Y > ExplorerGrid.Items.Count * ExplorerGrid.MinRowHeight
                || point.Y < ColumnHeaderHeight
                || point.X > actualRowWidth
                || point.X > DataGridContentWidth)
            {
                ExplorerGrid.SelectedItems.Clear();

                if (point.Y < ColumnHeaderHeight || point.X > DataGridContentWidth)
                    InitializeExplorerContextMenu(MenuType.Header);
            }
        }

        private void ExplorerGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject DepObject = (DependencyObject)e.OriginalSource;
            ExplorerGrid.ContextMenu.IsOpen = false;
            ExplorerGrid.ContextMenu.Visibility = Visibility.Collapsed;

            while (DepObject is not null and not DataGridColumnHeader)
            {
                DepObject = VisualTreeHelper.GetParent(DepObject);
            }

            if (DepObject is DataGridColumnHeader)
            {
                InitializeExplorerContextMenu(MenuType.Header);
            }
            else if (e.OriginalSource is FrameworkElement element && element.DataContext is FileClass file && ExplorerGrid.SelectedItems.Count > 0)
            {
                InitializeExplorerContextMenu(MenuType.ExplorerItem, file);
            }
            else
                InitializeExplorerContextMenu(MenuType.EmptySpace);
        }

        private void DevicesList_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Devices.UnselectAll();
            DevicesList.Items.Refresh();
        }

        private void DevicesSplitView_PaneOpening(SplitView sender, object args)
        {
            NewDevicePanelVisibility(false);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            PopulateButtons((string)PathBox.Tag);
            ResizeDetailedView();
        }

        private void ResizeDetailedView()
        {
            double windowHeight = WindowState == WindowState.Maximized ? ActualHeight : Height;

            if (DetailedViewSize() is sbyte val && val == -1)
            {
                FileOpDetailedGrid.Height = windowHeight * MIN_PANE_HEIGHT_RATIO;
            }
            else if (val == 1)
            {
                FileOpDetailedGrid.Height = windowHeight * MAX_PANE_HEIGHT_RATIO;
            }
        }

        private void FileOperationsButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileOperationsButton.Tag is null || (FileOperationsButton.Tag is bool tag && !tag))
                FileOperationsButton.Tag = true;
            else
                FileOperationsButton.Tag = false;
        }

        private void PairingCodeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PairNewDeviceButton.IsEnabled = PairingCodeBox.Text.Length == 6 && double.TryParse(PairingCodeBox.Text, out double _);
        }

        private void PairNewDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            ADBService.PairNetworkDevice($"{NewDeviceIpBox.Text}:{NewDevicePortBox.Text}", PairingCodeBox.Text);
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            RefreshDrives();
            DriveViewNav();
        }

        private void RefreshDrives(bool findMmc = false)
        {
            Devices.Current.SetDrives(CurrentADBDevice.GetDrives(), findMmc);
            DrivesItemRepeater.ItemsSource = Devices.Current.Drives;
        }

        private void PushMenuButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            PasteFiles(((MenuItem)sender).Name == nameof(PasteFoldersMenu));
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void DataGridCell_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (e.OriginalSource is DataGridCell && e.TargetRect == Rect.Empty)
            {
                e.Handled = true;
            }
        }

        private void PaneCollapse_Click(object sender, RoutedEventArgs e)
        {
            ((MenuItem)sender).FindAscendant<SplitView>().IsPaneOpen = false;
        }

        private void PathMenuEdit_Click(object sender, RoutedEventArgs e)
        {
            PathBox.Focus();
        }

        private void GridBackgroundBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            UnfocusPathBox();
        }

        private void DriveItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ModernWpf.Controls.ListViewItem item && item.DataContext is Drive drive)
            {
                InitNavigation(drive.Path);
            }
        }

        private void PathMenuCopy_Click(object sender, RoutedEventArgs e)
        {
            object tag = PathBox.Tag;
            Clipboard.SetText(tag is null ? "" : (string)tag);
        }

        private void ShowHiddenCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.showHiddenItems, ShowHiddenCheckBox.IsChecked);
        }

        private void ShowExtensionsCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.showExtensions, ShowExtensionsCheckBox.IsChecked);
        }

        private void PullMenuButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            CopyFiles();
        }

        private void GridSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // -1 + 1 or 1 + -1 (0 + 0 shouldn't happen)
            if (SimplifyNumber(e.VerticalChange) + DetailedViewSize() == 0)
                return;

            if (FileOpDetailedGrid.Height is double.NaN)
                FileOpDetailedGrid.Height = FileOpDetailedGrid.ActualHeight;

            FileOpDetailedGrid.Height -= e.VerticalChange;
        }

        /// <summary>
        /// Reduces the number to 3 possible values
        /// </summary>
        /// <param name="num">The number to evaluate</param>
        /// <returns>-1 if less than 0, 1 if greater than 0, 0 if 0</returns>
        private static sbyte SimplifyNumber(double num) => num switch
        {
            < 0 => -1,
            > 0 => 1,
            _ => 0
        };

        /// <summary>
        /// Compares the size of the detailed file op view to its limits
        /// </summary>
        /// <returns>0 if within limits, 1 if exceeds upper limits, -1 if exceeds lower limits</returns>
        private sbyte DetailedViewSize()
        {
            if (FileOpDetailedGrid.ActualHeight > ActualHeight * MAX_PANE_HEIGHT_RATIO)
                return 1;

            if (ActualHeight == 0 || (FileOpDetailedGrid.ActualHeight < ActualHeight * MIN_PANE_HEIGHT_RATIO && FileOpDetailedGrid.ActualHeight < MIN_PANE_HEIGHT))
                return -1;

            return 0;
        }

        private void FileOpDetailedGrid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ResizeDetailedView();
        }

        private void FileOpDetailedRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.showExtendedView, true);
        }

        private void FileOpCompactRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            Storage.StoreValue(Settings.showExtendedView, false);
        }

        private void ConnectionTypeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (ManualConnectionRadioButton.IsChecked == false
                && NewDevicePanel.Visible()
                && MdnsService.State == MDNS.MdnsState.Disabled)
            {
                MdnsService.State = MDNS.MdnsState.Unchecked;
                MdnsCheck();
            }
        }

        private void FileOperationsSplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
        {
            FileOperationsButton.Tag = false;
        }
    }
}
