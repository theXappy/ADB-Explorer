﻿using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Microsoft.WindowsAPICodePack.Shell;
using ModernWpf;
using ModernWpf.Controls;
using ModernWpf.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
        private Mutex connectTimerMutex = new();
        private ItemsPresenter ExplorerContentPresenter;
        private ScrollViewer ExplorerScroller;
        private ThemeService themeService = new();
        private int clickCount = 0;
        private int firstSelectedRow = -1;
        public DirectoryLister DirectoryLister { get; private set; }
        public static MDNS MdnsService { get; set; } = new();
        public Devices DevicesObject { get; set; } = new();
        public PairingQrClass QrClass { get; set; }

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

        private IEnumerable<FileClass> selectedFiles =>
                            from FileClass item in ExplorerGrid.SelectedItems
                            select item;

        public MainWindow()
        {
            InitializeComponent();

            fileOperationQueue = new(this.Dispatcher);
            LaunchSequence();

            ConnectTimer.Interval = CONNECT_TIMER_INIT;
            ConnectTimer.Tick += ConnectTimer_Tick;

            Settings.PropertyChanged += Settings_PropertyChanged;
            themeService.PropertyChanged += ThemeService_PropertyChanged;
            CommandLog.CollectionChanged += CommandLog_CollectionChanged;

            if (CheckAdbVersion())
            {
                ConnectTimer.Start();
                OpenDevicesButton.IsEnabled = true;
                DevicesSplitView.IsPaneOpen = true;
            }

            UpperProgressBar.DataContext = fileOperationQueue;
            CurrentOperationDataGrid.ItemsSource = fileOperationQueue.Operations;
        }

        private void CommandLog_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is null)
                return;

            foreach (Log item in e.NewItems)
            {
                Dispatcher.Invoke(() =>
                {
                    if (PauseAutoScrollButton.IsChecked == false)
                    {
                        LogTextBox.Text += $"{item}\n";
                        LogTextBox.CaretIndex = LogTextBox.Text.Length;
                        LogTextBox.ScrollToEnd();
                    }
                });
            }
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.Theme) or nameof(AppSettings.ForceFluentStyles):
                    SetTheme(Settings.Theme);
                    break;
                case nameof(AppSettings.EnableMdns):
                    EnableMdns();
                    break;
                case nameof(AppSettings.ShowHiddenItems):
                    FilterHiddenFiles();
                    break;
                case nameof(AppSettings.EnableLog):
                    if (!Settings.EnableLog)
                        ClearLogs();
                    break;
                default:
                    break;
            }
        }

        private void DirectoryLister_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectoryLister.IsProgressVisible))
            {
                DirectoryLoadingProgressBar.Visible(DirectoryLister.IsProgressVisible);
                UnfinishedBlock.Visible(DirectoryLister.IsProgressVisible);
            }
        }

        private void ThemeService_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SetTheme();
            });
        }

        private bool CheckAdbVersion()
        {
            int exitCode = 1;
            string stdout = "";
            try
            {
                exitCode = ADBService.ExecuteCommand("adb", "version", out stdout, out _, Encoding.UTF8);
            }
            catch (Exception) { }

            if (exitCode == 0)
            {
                string version = AdbRegEx.ADB_VERSION.Match(stdout).Groups["version"]?.Value;
                if (new Version(version) < MIN_ADB_VERSION)
                {
                    MissingAdbTextblock.Visibility = Visibility.Collapsed;
                    AdbVersionTooLowTextblock.Visibility = Visibility.Visible;
                }
                else
                    return true;
            }
            SettingsSplitView.IsPaneOpen = true;
            WorkingDirectoriesExpander.IsExpanded = true;
            MissingAdbGrid.Visibility = Visibility.Visible;

            return false;
        }

        private void InitializeExplorerContextMenu(MenuType type, object dataContext = null)
        {
            MenuItem pullMenu = FindResource("ContextMenuPullItem") as MenuItem;
            MenuItem deleteMenu = FindResource("MenuDeleteItem") as MenuItem;
            MenuItem pushMenu = FindResource("ContextMenuPushItem") as MenuItem;
            MenuItem renameMenu = FindResource("ContextMenuRenameItem") as MenuItem;
            MenuItem cutMenu = FindResource("ContextMenuCutItem") as MenuItem;
            MenuItem pasteMenu = FindResource("ContextMenuPasteItem") as MenuItem;
            MenuItem newMenu = FindResource("ContextMenuNewItem") as MenuItem;
            MenuItem copyPath = FindResource("ContextMenuCopyPathItem") as MenuItem;
            copyPath.Resources = FindResource("ContextSubMenuStyles") as ResourceDictionary;
            ExplorerGrid.ContextMenu.Items.Clear();

            switch (type)
            {
                case MenuType.ExplorerItem:
                    ExplorerGrid.ContextMenu.Items.Add(pullMenu);

                    if (ExplorerGrid.SelectedItems.Count == 1 && ((FilePath)ExplorerGrid.SelectedItem).IsDirectory)
                    {
                        ExplorerGrid.ContextMenu.Items.Add(pushMenu);
                    }
                    ExplorerGrid.ContextMenu.Items.Add(new Separator());

                    if (!ExplorerGrid.SelectedItems.Cast<FileClass>().All(file => file.IsCut))
                        ExplorerGrid.ContextMenu.Items.Add(cutMenu);

                    if (PasteEnabled())
                        ExplorerGrid.ContextMenu.Items.Add(pasteMenu);

                    if (ExplorerGrid.SelectedItems.Count == 1)
                    {
                        ExplorerGrid.ContextMenu.Items.Add(renameMenu);
                        ExplorerGrid.ContextMenu.Items.Add(copyPath);
                    }

                    if (ExplorerGrid.ContextMenu.Items[^1] is not Separator)
                        ExplorerGrid.ContextMenu.Items.Add(new Separator());

                    ExplorerGrid.ContextMenu.Items.Add(deleteMenu);

                    bool irregular = DevicesObject.CurrentDevice.Root != AbstractDevice.RootStatus.Enabled
                        && dataContext is FileClass file && file.Type is not (FileType.File or FileType.Folder);

                    foreach (MenuItem item in ExplorerGrid.ContextMenu.Items.OfType<MenuItem>())
                    {
                        if (!item.Equals(copyPath))
                            item.IsEnabled = !irregular;
                    }
                    break;
                case MenuType.EmptySpace:
                    ExplorerGrid.ContextMenu.Items.Add(pushMenu);
                    ExplorerGrid.ContextMenu.Items.Add(new Separator());
                    ExplorerGrid.ContextMenu.Items.Add(newMenu);
                    if (PasteEnabled(true))
                    {
                        ExplorerGrid.ContextMenu.Items.Add(new Separator());
                        ExplorerGrid.ContextMenu.Items.Add(pasteMenu);
                    }
                    break;
                case MenuType.Header:
                    break;
                default:
                    break;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (DirectoryLister is not null)
            {
                DirectoryLister.Stop();
            }

            ConnectTimer.Stop();
            StoreClosingValues();
        }

        private void StoreClosingValues()
        {
            Storage.StoreValue(SystemVals.windowMaximized, WindowState == WindowState.Maximized);

            var detailedVisible = FileOpVisibility() && Settings.ShowExtendedView;
            Storage.StoreValue(SystemVals.detailedVisible, detailedVisible);
            if (detailedVisible)
                Storage.StoreValue(SystemVals.detailedHeight, FileOpDetailedGrid.Height);
        }

        private void SetTheme()
        {
            SetTheme(Settings.Theme is AppTheme.windowsDefault
                ? themeService.WindowsTheme
                : ThemeManager.Current.ApplicationTheme.Value);
        }

        private void SetTheme(AppTheme theme) => SetTheme(AppThemeToActual(theme));

        private static void SetTheme(ApplicationTheme theme)
        {
            ThemeManager.Current.ApplicationTheme = theme;

            foreach (string key in ((ResourceDictionary)Application.Current.Resources["DynamicBrushes"]).Keys)
            {
                SetResourceColor(theme, key);
            }
        }

        private static void SetResourceColor(ApplicationTheme theme, string resource)
        {
            Application.Current.Resources[resource] = new SolidColorBrush((Color)Application.Current.Resources[$"{theme}{resource}"]);
        }

        private void PathBox_GotFocus(object sender, RoutedEventArgs e)
        {
            FocusPathBox();
        }

        private void FocusPathBox()
        {
            PathStackPanel.Visibility = Visibility.Collapsed;
            PathBox.Text = TextHelper.GetAltText(PathBox);
            PathBox.IsReadOnly = false;
            PathBox.SelectAll();
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
                        NavHistory.Navigate(NavHistory.SpecialLocation.DriveView);
                        return;
                    }
                }

                e.Handled = true;
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
                        if (Settings.PullOnDoubleClick)
                            PullFiles(true);
                        break;
                    case FileType.Folder:
                        NavigateToPath(file.FullPath);
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

            bool irregular = DevicesObject.CurrentDevice.Root != AbstractDevice.RootStatus.Enabled
                && items.All(item => item is FileClass file && file.Type is not (FileType.File or FileType.Folder));

            DeleteMenuButton.IsEnabled =
            PullMenuButton.IsEnabled = items.Any() && !irregular;

            var renameEnabled = items.Count() == 1 && !irregular;

            RenameMenuButton.IsEnabled = renameEnabled;
            ExplorerGrid.Columns[1].IsReadOnly = !renameEnabled;

            CutMenuButton.IsEnabled = !ExplorerGrid.SelectedItems.Cast<FileClass>().All(file => file.IsCut) && !irregular;
            PasteMenuButton.IsEnabled = PasteEnabled();

            MoreMenuButton.IsEnabled = items.Count() == 1;
        }

        private bool PasteEnabled(bool ignoreSelected = false)
        {
            if (CutItems.Count < 1)
                return false;

            if (CutItems.Count == 1 && CutItems[0].Relation(CurrentPath) is RelationType.Descendant or RelationType.Self)
                return false;

            var selected = ignoreSelected ? 0 : ExplorerGrid.SelectedItems.Count;
            switch (selected)
            {
                case 0:
                    return CutItems[0].ParentPath != CurrentPath;
                case 1:
                    var item = ExplorerGrid.SelectedItem as FilePath;
                    if (!item.IsDirectory
                        || (CutItems.Count == 1 && CutItems[0].FullPath == item.FullPath)
                        || CutItems[0].ParentPath == item.FullPath)
                        return false;
                    break;
                default:
                    return false;
            }

            return true;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            UnfocusPathBox();
        }

        private void LaunchSequence()
        {
            LoadSettings();
            InitFileOpColumns();

            TestCurrentOperation();
            TestDevices();
        }

        private void DeviceListSetup(IEnumerable<LogicalDevice> devices = null, string selectedAddress = "")
        {
            var init = !DevicesObject.UpdateDevices(devices is null ? ADBService.GetDevices() : devices);

            if (DevicesObject.Current is null || DevicesObject.Current.IsOpen && DevicesObject.CurrentDevice.Status != AbstractDevice.DeviceStatus.Online)
                ClearDrives();

            if (!DevicesObject.DevicesAvailable())
            {
                Title = $"{Properties.Resources.AppDisplayName} - NO CONNECTED DEVICES";
                ClearExplorer();
                NavHistory.Reset();
                ClearDrives();
                return;
            }
            else
            {
                if (DevicesObject.DevicesAvailable(true))
                    return;

                if (!Settings.AutoOpen)
                {
                    DevicesObject.CloseAll();

                    Title = Properties.Resources.AppDisplayName;
                    ClearExplorer();
                    NavHistory.Reset();
                    return;
                }

                if (!DevicesObject.SetCurrentDevice(selectedAddress))
                    return;

                if (!ConnectTimer.IsEnabled)
                    DevicesSplitView.IsPaneOpen = false;
            }

            DevicesObject.SetOpen(DevicesObject.Current, true);
            CurrentADBDevice = new(DevicesObject.CurrentDevice);
            InitLister();
            if (init)
                InitDevice();
        }

        private void InitLister()
        {
            DirectoryLister = new(Dispatcher, CurrentADBDevice);
            DirectoryLister.PropertyChanged += DirectoryLister_PropertyChanged;
        }

        private void LoadSettings()
        {
            Title = $"{Properties.Resources.AppDisplayName} - NO CONNECTED DEVICES";

            if (Settings.EnableMdns)
                QrClass = new();

            SetTheme(Settings.Theme);

            EnableMdns();
        }

        private ApplicationTheme AppThemeToActual(AppTheme appTheme) => appTheme switch
        {
            AppTheme.light => ApplicationTheme.Light,
            AppTheme.dark => ApplicationTheme.Dark,
            AppTheme.windowsDefault => themeService.WindowsTheme,
            _ => throw new NotImplementedException(),
        };

        private IEnumerable<CheckBox> FileOpContextItems
        {
            get
            {
                var items = ((ContextMenu)FindResource("FileOpHeaderContextMenu")).Items;
                return from MenuItem item in ((ContextMenu)FindResource("FileOpHeaderContextMenu")).Items
                       let checkbox = item.Header as CheckBox
                       select checkbox;
            }
        }

        private void InitFileOpColumns()
        {
            var fileOpContext = FindResource("FileOpHeaderContextMenu") as ContextMenu;
            foreach (var item in fileOpContext.Items)
            {
                var checkbox = ((MenuItem)item).Header as CheckBox;
                checkbox.Click += ColumnCheckbox_Click;

                var config = Storage.Retrieve<FileOpColumn>(checkbox.Name);
                if (config is null)
                    continue;

                var column = GetCheckboxColumn(checkbox);
                checkbox.DataContext = config;
                checkbox.IsChecked = config.IsVisible;
                column.Width = config.Width;
                column.Visibility = Visible(config.IsVisible);
                column.DisplayIndex = config.Index;
            }

            EnableContextItems();
        }

        private DataGridColumn GetCheckboxColumn(CheckBox checkBox)
        {
            return checkBox.Name.Split("FileOpContext")[1].Split("CheckBox")[0] switch
            {
                "OpType" => OpTypeColumn,
                "FileName" => FileNameColumn,
                "Progress" => ProgressColumn,
                "Source" => SourceColumn,
                "Dest" => DestColumn,
                _ => throw new NotImplementedException(),
            };
        }

        private string ColumnName(DataGridColumn column)
        {
            if (column == OpTypeColumn) return "OpTypeColumn";
            if (column == FileNameColumn) return "FileNameColumn";
            if (column == ProgressColumn) return "ProgressColumn";
            if (column == SourceColumn) return "SourceColumn";
            if (column == DestColumn) return "DestColumn";

            return "";
        }

        private CheckBox GetColumnCheckbox(DataGridColumn column)
        {
            return FileOpContextItems.Where(cb => cb.Name == $"FileOpContext{ColumnName(column).Split("Column")[0]}CheckBox").First();
        }

        private void EnableContextItems()
        {
            var visibleColumns = FileOpContextItems.Count(cb => cb.IsChecked == true);
            foreach (var checkbox in FileOpContextItems)
            {
                checkbox.IsEnabled = visibleColumns > 1 ? true : checkbox.IsChecked == false;
            }
        }

        private void ColumnCheckbox_Click(object sender, RoutedEventArgs e)
        {
            var checkbox = sender as CheckBox;
            var column = GetCheckboxColumn(checkbox);

            column.Visibility = Visible(checkbox.IsChecked);
            if (checkbox.DataContext is FileOpColumn config)
            {
                config.IsVisible = checkbox.IsChecked;
            }
            else
            {
                checkbox.DataContext = CreateColumnConfig(column);
            }

            Storage.StoreValue(checkbox.Name, checkbox.DataContext);
            EnableContextItems();
        }

        private static FileOpColumn CreateColumnConfig(DataGridColumn column) => new FileOpColumn()
        {
            Index = column.DisplayIndex,
            IsVisible = column.Visibility == Visibility.Visible,
            Width = column.ActualWidth
        };

        private void InitDevice()
        {
            Title = $"{Properties.Resources.AppDisplayName} - {DevicesObject.Current.Name}";

            RefreshDrives();
            DriveViewNav();
            NavHistory.Navigate(NavHistory.SpecialLocation.DriveView);

            CurrentDeviceDetailsPanel.DataContext = DevicesObject.Current;
            DeleteMenuButton.DataContext = DevicesObject.CurrentDevice;

            if (DevicesObject.CurrentDevice.Drives.Count < 1)
            {
                // Shouldn't actually happen
                InitNavigation();
            }

            TestCurrentOperation();
        }

        private void DriveViewNav()
        {
            ClearExplorer(false);
            ExplorerGrid.Visibility = Visibility.Collapsed;
            DrivesItemRepeater.Visibility = Visibility.Visible;
            PathBox.IsEnabled = true;

            MenuItem button = CreatePathButton(DevicesObject.Current, DevicesObject.Current.Name);
            AddPathButton(button);
        }

        private void CombinePrettyNames()
        {
            foreach (var drive in DevicesObject.CurrentDevice.Drives.Where(d => d.Type != Models.DriveType.Root))
            {
                CurrentPrettyNames.TryAdd(drive.Path, drive.Type is Models.DriveType.External
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

            ExplorerGrid.ItemsSource = DirectoryLister.FileList;
            PushMenuButton.IsEnabled = true;
            HomeButton.IsEnabled = DevicesObject.CurrentDevice.Drives.Any();
            UpdateRecycledItemsCount();

            if (path is null)
                return true;

            return string.IsNullOrEmpty(path)
                ? NavigateToPath(DEFAULT_PATH)
                : NavigateToPath(path);
        }

        private void UpdateRecycledItemsCount()
        {
            var countTask = Task.Run(() => CurrentADBDevice.CountRecycle());
            countTask.ContinueWith((t) => Dispatcher.Invoke(() => DevicesObject.CurrentDevice.RecycledItemsCount = t.Result));
        }

        private void ListDevices(IEnumerable<LogicalDevice> devices)
        {
            if (devices is not null && DevicesObject.DevicesChanged(devices))
            {
                DeviceListSetup(devices);

                if (Settings.AutoRoot)
                {
                    foreach (var item in DevicesObject.LogicalDevices.Where(device => device.Root is AbstractDevice.RootStatus.Unchecked))
                    {
                        Task.Run(() => item.EnableRoot(true));
                    }
                }
            }
        }

        private void UpdateDevicesBatInfo(bool devicesVisible)
        {
            if (!Settings.PollBattery)
                return;

            DevicesObject.CurrentDevice?.UpdateBattery();

            if (DateTime.Now - DevicesObject.LastUpdate > BATTERY_UPDATE_INTERVAL || devicesVisible)
            {
                foreach (var item in DevicesObject.LogicalDevices.Where(device => device != DevicesObject.CurrentDevice))
                {
                    item.UpdateBattery();
                }

                DevicesObject.LastUpdate = DateTime.Now;
            }
        }

        private void ListServices(IEnumerable<ServiceDevice> services)
        {
            if (services is not null && DevicesObject.ServicesChanged(services))
            {
                DevicesObject.UpdateServices(services);

                var qrServices = DevicesObject.ServiceDevices.Where(service =>
                    service.MdnsType == ServiceDevice.ServiceType.QrCode
                    && service.ID == QrClass.ServiceName).ToList();

                if (qrServices.Any() && PairService(qrServices.First()))
                    PairingExpander.IsExpanded = false;
            }
        }

        private void ConnectTimer_Tick(object sender, EventArgs e)
        {
            ConnectTimer.Interval = CONNECT_TIMER_INTERVAL;
            var devicesVisible = DevicesSplitView.IsPaneOpen;

            Task.Run(() =>
            {
                if (!connectTimerMutex.WaitOne(0))
                {
                    return;
                }

                if (Settings.PollDevices)
                {
                    RefreshDevices(devicesVisible);
                }

                UpdateDevicesBatInfo(devicesVisible);

                connectTimerMutex.ReleaseMutex();
            });
        }

        private void RefreshDevices(bool devicesVisible)
        {
            Dispatcher.BeginInvoke(new Action<IEnumerable<LogicalDevice>>(ListDevices), ADBService.GetDevices()).Wait();

            if (MdnsService.State == MDNS.MdnsState.Running && devicesVisible)
            {
                Dispatcher.BeginInvoke(new Action<IEnumerable<ServiceDevice>>(ListServices), WiFiPairingService.GetServices()).Wait();
            }
        }

        private static void MdnsCheck()
        {
            Task.Run(() =>
            {
                return MdnsService.State = ADBService.CheckMDNS() ? MDNS.MdnsState.Running : MDNS.MdnsState.NotRunning;
            });
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
                DialogService.ShowMessage(e.Message, "Navigation Error", DialogService.DialogIcon.Critical);
                return false;
            }

            if (!bfNavigated)
                NavHistory.Navigate(realPath);

            UpdateNavButtons();

            TextHelper.SetAltText(PathBox, realPath);
            CurrentPath = realPath;
            PopulateButtons(realPath);
            ParentPath = CurrentADBDevice.TranslateDeviceParentPath(CurrentPath);

            ParentButton.IsEnabled = CurrentPath != ParentPath;
            PasteMenuButton.IsEnabled = PasteEnabled();
            NewMenuButton.IsEnabled = true;

            DirectoryLister.Navigate(realPath);
            if (realPath == RECYCLE_PATH)
            {
                Task.Run(() =>
                {
                    var text = ShellFileOperation.ReadAllText(CurrentADBDevice, RECYCLE_INDEX_PATH);
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    RecycleIndex.AddRange(lines.Select(l => new TrashIndexer(l)));
                });
            }

            FilterHiddenFiles();
            return true;
        }

        private void UpdateNavButtons()
        {
            BackButton.IsEnabled = NavHistory.BackAvailable;
            ForwardButton.IsEnabled = NavHistory.ForwardAvailable;
        }

        private void PopulateButtons(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            var expectedLength = 0.0;
            List<MenuItem> tempButtons = new();
            List<string> pathItems = new();

            var pairs = CurrentPrettyNames.Where(kv => path.StartsWith(kv.Key));
            var specialPair = pairs.Count() > 1 ? pairs.OrderBy(kv => kv.Key.Length).Last() : pairs.First();
            if (specialPair.Key != null)
            {
                MenuItem button = CreatePathButton(specialPair);
                tempButtons.Add(button);
                pathItems.Add(specialPair.Key);
                path = path[specialPair.Key.Length..].TrimStart('/');
                expectedLength = ControlSize.GetWidth(button);
            }

            var dirs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in dirs)
            {
                pathItems.Add(dir);
                var dirPath = string.Join('/', pathItems).Replace("//", "/");
                MenuItem button = CreatePathButton(dirPath, dir);
                tempButtons.Add(button);
                expectedLength += ControlSize.GetWidth(button);
            }

            expectedLength += (tempButtons.Count - 1) * ControlSize.GetWidth(CreatePathArrow());

            int i = 0;
            for (; i < PathButtons.Count && i < tempButtons.Count; i++)
            {
                var oldB = PathButtons[i];
                var newB = tempButtons[i];
                if (oldB.Header.ToString() != newB.Header.ToString() ||
                    TextHelper.GetAltObject(oldB).ToString() != TextHelper.GetAltObject(newB).ToString())
                {
                    break;
                }
            }
            PathButtons.RemoveRange(i, PathButtons.Count - i);
            PathButtons.AddRange(tempButtons.GetRange(i, tempButtons.Count - i));

            ConsolidateButtons(expectedLength);
        }

        private void ConsolidateButtons(double expectedLength)
        {
            if (expectedLength > PathBox.ActualWidth)
                expectedLength += ControlSize.GetWidth(CreateExcessButton());

            double excessLength = expectedLength - PathBox.ActualWidth;
            List<MenuItem> excessButtons = new();
            PathStackPanel.Children.Clear();

            if (excessLength > 0)
            {
                int i = 0;
                while (excessLength >= 0 && PathButtons.Count - excessButtons.Count > 1)
                {
                    excessButtons.Add(PathButtons[i]);
                    PathButtons[i].ContextMenu = null;
                    excessLength -= ControlSize.GetWidth(PathButtons[i]);

                    i++;
                }

                AddExcessButton(excessButtons);
            }

            foreach (var item in PathButtons.Except(excessButtons))
            {
                if (PathStackPanel.Children.Count > 0)
                    AddPathArrow();

                AddPathButton(item);
            }

            if (excessLength > 0)
            {
                PathButtons[^1].Width = PathButtons[^1].ActualWidth - (ControlSize.GetWidth(PathStackPanel) - PathBox.ActualWidth) - 4;
            }
            else
                PathButtons[^1].Width = double.NaN;
        }

        private MenuItem CreateExcessButton()
        {
            var menuItem = new MenuItem()
            {
                Height = 24,
                Header = new FontIcon()
                {
                    Glyph = "\uE712",
                    FontSize = 16,
                    Style = Resources["GlyphFont"] as Style,
                }
            };
            ControlHelper.SetCornerRadius(menuItem, new(3));
            return menuItem;
        }

        private void AddExcessButton(List<MenuItem> excessButtons = null)
        {
            if (excessButtons is not null && !excessButtons.Any())
                return;

            var button = CreateExcessButton();
            button.ItemsSource = excessButtons;
            Menu excessMenu = new() { Height = 24 };

            excessMenu.Items.Add(button);
            PathStackPanel.Children.Add(excessMenu);
        }

        private MenuItem CreatePathButton(KeyValuePair<string, string> kv) => CreatePathButton(kv.Key, kv.Value);
        private MenuItem CreatePathButton(object path, string name)
        {
            MenuItem button = new()
            {
                Header = new TextBlock() { Text = name, Margin = new(0, 0, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(8, 0, 8, 0),
                Height = 24,
            };
            button.Click += PathButton_Click;
            ControlHelper.SetCornerRadius(button, new(3));
            TextHelper.SetAltObject(button, path);

            return button;
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
        };

        private void AddPathArrow(bool append = true)
        {
            var arrow = CreatePathArrow();

            if (append)
                PathStackPanel.Children.Add(arrow);
            else
                PathStackPanel.Children.Insert(0, arrow);
        }

        private void PathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                if (TextHelper.GetAltObject(item) is string path and not "")
                    NavigateToPath(path);
                else if (TextHelper.GetAltObject(item) is LogicalDevice)
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

        private void NavigateToLocation(object location, bool bfNavigated = false)
        {
            if (location is string path)
            {
                if (!ExplorerGrid.Visible())
                    InitNavigation(null);

                NavigateToPath(path, bfNavigated);
            }
            else if (location is NavHistory.SpecialLocation special)
            {
                switch (special)
                {
                    case NavHistory.SpecialLocation.DriveView:
                        UnfocusPathBox();
                        RefreshDrives();
                        DriveViewNav();
                        break;
                    default:
                        throw new NotImplementedException();
                }

                UpdateNavButtons();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToLocation(NavHistory.GoBack(), true);
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToLocation(NavHistory.GoForward(), true);
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            switch (e.ChangedButton)
            {
                case MouseButton.XButton1:
                    if (NavHistory.BackAvailable)
                        NavigateBack();
                    break;
                case MouseButton.XButton2:
                    if (NavHistory.ForwardAvailable)
                        NavigateForward();
                    break;
            }
        }

        private void NavigateForward()
        {
            AnimateControl(ForwardButton);
            NavigateToLocation(NavHistory.GoForward(), true);
        }

        private void NavigateBack()
        {
            AnimateControl(BackButton);
            NavigateToLocation(NavHistory.GoBack(), true);
        }

        private void AnimateControl(Control control)
        {
            StyleHelper.SetActivateAnimation(control, true);
            Task.Delay(400).ContinueWith(_ => Dispatcher.Invoke(() => StyleHelper.SetActivateAnimation(control, false)));
        }

        private void DataGridRow_KeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key;
            if (key == Key.Enter)
            {
                if (CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]).IsEditing)
                    return;

                if (ExplorerGrid.SelectedItems.Count == 1 && ((FilePath)ExplorerGrid.SelectedItem).IsDirectory)
                    DoubleClick(ExplorerGrid.SelectedItem);
            }
            else if (key == Key.Back)
            {
                NavigateBack();
            }
            else if (key == Key.Delete)
            {
                DeleteFiles();
            }
            else if (key == Key.Up)
            {
                if (ExplorerGrid.SelectedIndex > 0)
                {
                    ExplorerGrid.SelectedIndex--;
                    ExplorerGrid.ScrollIntoView(ExplorerGrid.SelectedItem);
                }
            }
            else if (key == Key.Down)
            {
                if (ExplorerGrid.SelectedIndex < ExplorerGrid.Items.Count)
                {
                    ExplorerGrid.SelectedIndex++;
                    ExplorerGrid.ScrollIntoView(ExplorerGrid.SelectedItem);
                }
            }
            else
                return;

            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key;
            if (key == Key.Back)
            {
                NavigateBack();
            }
            else if (key == Key.Delete)
            {
                DeleteFiles();
            }

            bool handle = false;
            if (NavHistory.Current is string)
                handle = ExplorerGridKeyNavigation(key);

            if (handle)
                e.Handled = true;
        }

        private bool ExplorerGridKeyNavigation(Key key)
        {
            if (ExplorerGrid.Items.Count < 1)
                return false;

            switch (key)
            {
                case Key.Down:
                    if (ExplorerGrid.SelectedItems.Count == 0)
                        ExplorerGrid.SelectedIndex = 0;
                    else if (ExplorerGrid.SelectedIndex < ExplorerGrid.Items.Count)
                        ExplorerGrid.SelectedIndex++;

                    ExplorerGrid.ScrollIntoView(ExplorerGrid.SelectedItem);
                    break;
                case Key.Up:
                    if (ExplorerGrid.SelectedItems.Count == 0)
                        ExplorerGrid.SelectedItem = ExplorerGrid.Items[^1];
                    else if (ExplorerGrid.SelectedIndex > 0)
                        ExplorerGrid.SelectedIndex--;

                    ExplorerGrid.ScrollIntoView(ExplorerGrid.SelectedItem);
                    break;
                case Key.Enter:
                    if (ExplorerGrid.SelectedCells.Count < 1 || CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]).IsEditing)
                        return false;

                    if (ExplorerGrid.SelectedItems.Count == 1 && ((FilePath)ExplorerGrid.SelectedItem).IsDirectory)
                        DoubleClick(ExplorerGrid.SelectedItem);
                    break;
                default:
                    return false;
            }

            return true;
        }

        private void CopyMenuButton_Click(object sender, RoutedEventArgs e)
        {

        }

        private void PullFiles(bool quick = false)
        {
            int itemsCount = ExplorerGrid.SelectedItems.Count;
            ShellObject path;

            if (quick)
            {
                path = ShellObject.FromParsingName(Settings.DefaultFolder);
            }
            else
            {
                var dialog = new CommonOpenFileDialog()
                {
                    IsFolderPicker = true,
                    Multiselect = false,
                    DefaultDirectory = Settings.DefaultFolder,
                    Title = "Select destination for " + (itemsCount > 1 ? "multiple items" : ExplorerGrid.SelectedItem)
                };

                if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                path = dialog.FileAsShellObject;
            }

            var dirPath = new FilePath(path);

            if (!Directory.Exists(path.ParsingName))
            {
                try
                {
                    Directory.CreateDirectory(path.ParsingName);
                }
                catch (Exception e)
                {
                    DialogService.ShowMessage(e.Message, "Destination Path Error", DialogService.DialogIcon.Critical);
                    return;
                }
            }

            foreach (FileClass item in ExplorerGrid.SelectedItems)
            {
                fileOperationQueue.AddOperation(new FilePullOperation(Dispatcher, CurrentADBDevice, item, dirPath));
            }
        }

        private void PushItems(bool isFolderPicker, bool isContextMenu)
        {
            FilePath targetPath;
            if (isContextMenu && ExplorerGrid.SelectedItems.Count == 1)
                targetPath = (FilePath)ExplorerGrid.SelectedItem;
            else
                targetPath = new(CurrentPath);

            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = isFolderPicker,
                Multiselect = true,
                DefaultDirectory = Settings.DefaultFolder,
                Title = $"Select {(isFolderPicker ? "folder" : "file")}s to push{(targetPath.FullPath == CurrentPath ? "" : $" into {targetPath.FullName}")}"
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return;

            foreach (var item in dialog.FilesAsShellObject)
            {
                fileOperationQueue.AddOperation(new FilePushOperation(Dispatcher, CurrentADBDevice, new FilePath(item), targetPath));
            }
        }

        private void TestCurrentOperation()
        {
            //fileOperationQueue.Clear();
            //fileOperationQueue.AddOperation(InProgressTestOperation.CreateProgressStart(Dispatcher, CurrentADBDevice, "Shalom.exe"));
            //fileOperationQueue.AddOperation(InProgressTestOperation.CreateFileInProgress(Dispatcher, CurrentADBDevice, "Shalom.exe"));
            //fileOperationQueue.AddOperation(InProgressTestOperation.CreateFolderInProgress(Dispatcher, CurrentADBDevice, "Shalom"));
        }

        private void TestDevices()
        {
            //ConnectTimer.IsEnabled = false;
            //DevicesObject.UpdateServices(new List<ServiceDevice>() { new("sdfsdfdsf_adb-tls-pairing._tcp.", "192.168.1.20", "5555") });
            //DevicesObject.UpdateDevices(new List<LogicalDevice>() { LogicalDevice.New("Test", "test.ID", "offline") });
        }

        private void ContextMenuPullItem_Click(object sender, RoutedEventArgs e)
        {
            PullFiles();
        }

        private void DataGridRow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
                return;

            var row = sender as DataGridRow;

            if (row.IsSelected == false)
            {
                ExplorerGrid.SelectedItems.Clear();
            }

            if (e.OriginalSource is not Border)
                row.IsSelected = true;

            if (e.ChangedButton == MouseButton.Right)
            {
                InitializeExplorerContextMenu(e.OriginalSource is Border ? MenuType.EmptySpace : MenuType.ExplorerItem, row.DataContext);
            }
        }

        private void ChangeDefaultFolderButton_Click(object sender, MouseButtonEventArgs e)
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true,
                Multiselect = false
            };
            if (Settings.DefaultFolder != "")
                dialog.DefaultDirectory = Settings.DefaultFolder;

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                Settings.DefaultFolder = dialog.FileName;
            }
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
                    ManualPairingPanel.IsEnabled = true;
                    ManualPairingPortBox.Clear();
                    ManualPairingCodeBox.Clear();
                }
                else
                    DialogService.ShowMessage(ex.Message, "Connection Error", DialogService.DialogIcon.Critical);

                return;
            }

            if (Settings.RememberIp)
                Settings.LastIp = NewDeviceIpBox.Text;

            if (Settings.RememberPort)
                Settings.LastPort = NewDevicePortBox.Text;

            NewDeviceIpBox.Clear();
            NewDevicePortBox.Clear();
            PairingExpander.IsExpanded = false;
            DeviceListSetup(selectedAddress: deviceAddress);
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

        private void EnablePairButton()
        {
            PairNewDeviceButton.IsEnabled = ManualPairingPortBox.Text is string port
                && !string.IsNullOrWhiteSpace(port)
                && ushort.TryParse(port, out _)
                && ManualPairingCodeBox.Text is string text
                && text.Replace("-", "") is string password
                && !string.IsNullOrWhiteSpace(password)
                && uint.TryParse(password, out _)
                && password.Length == 6;
        }

        private void NewDeviceIpBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBoxSeparation(sender as TextBox, specialChars: '.');
            EnableConnectButton();
        }

        private void RetrieveIp()
        {
            if (!string.IsNullOrWhiteSpace(NewDeviceIpBox.Text) && !string.IsNullOrWhiteSpace(NewDevicePortBox.Text))
                return;

            if (Settings.RememberIp
                && Settings.LastIp is string lastIp
                && !DevicesObject.UIList.Find(d => d.Device.ID.Split(':')[0] == lastIp))
            {
                NewDeviceIpBox.Text = lastIp;
                if (Settings.RememberPort
                    && Settings.LastPort is string lastPort)
                {
                    NewDevicePortBox.Text = lastPort;
                }
            }
        }

        private void OpenDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is UILogicalDevice device && device.Device.Status != AbstractDevice.DeviceStatus.Offline)
            {
                DevicesObject.SetOpen(device);
                CurrentADBDevice = new(device);
                InitLister();
                ClearExplorer();
                NavHistory.Reset();
                InitDevice();

                DevicesSplitView.IsPaneOpen = false;
            }
        }

        private void DisconnectDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is UILogicalDevice device)
            {
                RemoveDevice(device);
            }
        }

        private void RemoveDevice(UILogicalDevice device)
        {
            try
            {
                if (device.Device.Type == AbstractDevice.DeviceType.Emulator)
                {
                    ADBService.KillEmulator(device.Device.ID);
                }
                else
                {
                    ADBService.DisconnectNetworkDevice(device.Device.ID);
                }
            }
            catch (Exception ex)
            {
                DialogService.ShowMessage(ex.Message, "Disconnection Error", DialogService.DialogIcon.Critical);
                return;
            }

            if (device.IsOpen)
            {
                ClearDrives();
                ClearExplorer();
                NavHistory.Reset();
                DevicesObject.SetOpen(device, false);
                CurrentADBDevice = null;
                DirectoryLister = null;
            }
            DeviceListSetup();
        }

        private void ClearExplorer(bool clearDevice = true)
        {
            PathStackPanel.Children.Clear();
            DirectoryLister?.FileList?.Clear();
            PushMenuButton.IsEnabled =
            NewMenuButton.IsEnabled =
            PullMenuButton.IsEnabled =
            DeleteMenuButton.IsEnabled =
            RenameMenuButton.IsEnabled =
            HomeButton.IsEnabled =
            NewMenuButton.IsEnabled =
            PasteMenuButton.IsEnabled =
            MoreMenuButton.IsEnabled =
            ParentButton.IsEnabled = false;

            if (clearDevice)
            {
                CurrentPrettyNames.Clear();
                CurrentPath = null;
                CurrentDeviceDetailsPanel.DataContext = null;
                TextHelper.SetAltText(PathBox, "");
                PathBox.IsEnabled =
                BackButton.IsEnabled =
                ForwardButton.IsEnabled = false;
            }
        }

        private void ClearDrives()
        {
            DevicesObject.CurrentDevice?.Drives.Clear();
            DrivesItemRepeater.ItemsSource = null;
        }

        private void DevicesSplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
        {
            if (TextHelper.GetAltObject(DevicesSplitView) is bool and true)
            {
                args.Cancel = true;
                return;
            }

            PairingExpander.IsExpanded = false;
            DevicesObject.UnselectAll();
        }

        private void NewDeviceIpBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (ConnectNewDeviceButton.Visible() && ConnectNewDeviceButton.IsEnabled)
                    ConnectNewDevice();
                else if (PairNewDeviceButton.Visible() && PairNewDeviceButton.IsEnabled)
                    PairDeviceManual();
            }

        }

        private void ExplorerGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            ExplorerGrid.ContextMenu.Visibility = Visible(ExplorerGrid.ContextMenu.HasItems);
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

        private void DevicesSplitView_PaneOpening(SplitView sender, object args)
        {
            PairingExpander.IsExpanded = false;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            PopulateButtons(TextHelper.GetAltText(PathBox));
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
            FileOpVisibility(null);
        }

        private void FileOpVisibility(bool? value = null)
        {
            if (value is not null)
            {
                RemoteToggle.SetIsTargetVisible(FileOperationsButton, value.Value);
                return;
            }

            RemoteToggle.SetIsTargetVisible(FileOperationsButton, !FileOpVisibility());
        }

        private bool FileOpVisibility()
        {
            return RemoteToggle.GetIsTargetVisible(FileOperationsButton);
        }

        private void PairingCodeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBoxSeparation(sender as TextBox, '-', 6);
            EnablePairButton();
        }

        private void PairNewDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            PairDeviceManual();
        }

        private void PairDeviceManual()
        {
            try
            {
                ADBService.PairNetworkDevice($"{NewDeviceIpBox.Text}:{ManualPairingPortBox.Text}", ManualPairingCodeBox.Text.Replace("-", ""));
            }
            catch (Exception ex)
            {
                DialogService.ShowMessage(ex.Message, "Pairing Error", DialogService.DialogIcon.Critical);
                (FindResource("PairServiceFlyout") as Flyout).Hide();
                return;
            }

            ConnectNewDevice();
            ManualPairingPanel.IsEnabled = false;
            PairingExpander.IsExpanded = false;
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            NavHistory.Navigate(NavHistory.SpecialLocation.DriveView);
            NavigateToLocation(NavHistory.SpecialLocation.DriveView);
        }

        private void RefreshDrives(bool findMmc = false)
        {
            var drives = CurrentADBDevice.GetDrives();
            if (Settings.EnableRecycle)
            {
                UpdateRecycledItemsCount();
                drives.Add(new(path: RECYCLE_PATH));
            }

            DevicesObject.CurrentDevice.SetDrives(drives, findMmc);
            DrivesItemRepeater.ItemsSource = DevicesObject.CurrentDevice.Drives;
        }

        private void PushMenuButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            var menuItem = (MenuItem)sender;

            // The file context menu does not have a name
            PushItems(menuItem.Name == "PushFoldersMenu", string.IsNullOrEmpty(((MenuItem)menuItem.Parent).Name));
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
            ClearSelectedDrives();
        }

        private void DriveItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button item && item.DataContext is Drive drive)
            {
                InitNavigation(drive.Path);
            }
        }

        private void PathMenuCopy_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(TextHelper.GetAltText(PathBox));
        }

        private void FilterHiddenFiles()
        {
            //https://docs.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-group-sort-and-filter-data-in-the-datagrid-control?view=netframeworkdesktop-4.8

            string[] recycleItems = { RECYCLE_PATH, RECYCLE_INDEX_PATH };

            CollectionViewSource.GetDefaultView(ExplorerGrid.ItemsSource).Filter = !Settings.ShowHiddenItems
                ? (new(file => file is FileClass fileClass && !fileClass.IsHidden && !recycleItems.Contains(fileClass.FullPath)))
                : (new(file => !recycleItems.Contains(((FileClass)file).FullPath)));
        }

        private void PullMenuButton_Click(object sender, RoutedEventArgs e)
        {
            UnfocusPathBox();
            PullFiles();
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
            double height = FileOpDetailedGrid.ActualHeight;
            if (height == 0 && FileOpDetailedGrid.Height > 0)
                height = FileOpDetailedGrid.Height;

            if (height > ActualHeight * MAX_PANE_HEIGHT_RATIO)
                return 1;

            if (ActualHeight == 0 || height < ActualHeight * MIN_PANE_HEIGHT_RATIO && height < MIN_PANE_HEIGHT)
                return -1;

            return 0;
        }

        private void FileOpDetailedGrid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ResizeDetailedView();
        }

        private void ConnectionTypeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            ChangeConnectionType();
        }

        private void ChangeConnectionType()
        {
            if (ManualConnectionRadioButton.IsChecked == false
                            && PairingExpander.IsExpanded
                            && MdnsService.State == MDNS.MdnsState.Disabled)
            {
                MdnsService.State = MDNS.MdnsState.Unchecked;
                MdnsCheck();
            }
            else if (ManualConnectionRadioButton.IsChecked == true)
            {
                MdnsService.State = MDNS.MdnsState.Disabled;
                DevicesObject.UIList.RemoveAll(device => device is UIServiceDevice);
                //DevicesList?.Items.Refresh();
            }

            if (QrConnectionRadioButton?.IsChecked == true)
            {
                UpdateQrClass();
            }

            if (ManualPairingPanel is not null)
            {
                ManualPairingPanel.IsEnabled = false;
            }
        }

        private void UpdateQrClass()
        {
            QrClass.Background = QR_BACKGROUND;
            QrClass.Foreground = QR_FOREGROUND;
            PairingQrImage.Source = QrClass.Image;
        }

        /// <summary>
        /// Provides validation and separation of text in a <see cref="TextBox"/>.
        /// </summary>
        /// <param name="textBox">The textbox to be validated</param>
        /// <param name="separator">Text separator. Default is null - no separator</param>
        /// <param name="maxChars">Maximum allowed characters in the text. Default is -1 - no length validation</param>
        /// <param name="numeric">Enable numeric validation. Default is <see langword="true"/></param>
        /// <param name="specialChars">When numeric is enabled - allowed non-numeric chars. Otherwise - forbidden chars</param>
        private static void TextBoxSeparation(TextBox textBox,
                                              char? separator = null,
                                              int maxChars = -1,
                                              bool numeric = true,
                                              params char[] specialChars)
        {
            if (TextHelper.GetIsValidating(textBox))
                return;
            else
                TextHelper.SetIsValidating(textBox, true);

            var caretIndex = textBox.CaretIndex;
            var output = "";
            var numbers = "";
            var deletedChars = 0;
            var text = textBox.Text;

            foreach (var c in text)
            {
                if ((numeric && !char.IsDigit(c) && !specialChars.Contains(c))
                    || (!numeric && specialChars.Contains(c)))
                {
                    if (c != separator)
                        deletedChars++;

                    continue;
                }

                numbers += c;
            }

            if (separator is null)
            {
                output = numbers;
            }
            else
            {
                for (int i = 0; i < numbers.Length; i++)
                {
                    output += $"{(i > 0 ? separator : "")}{numbers[i]}";
                }
            }

            if (deletedChars > 0 && TextHelper.GetAltText(textBox) is string prev && prev.Length > output.Length)
            {
                textBox.Text = prev;
                textBox.CaretIndex = caretIndex - deletedChars;

                TextHelper.SetIsValidating(textBox, false);
                return;
            }

            if (maxChars > -1)
                textBox.MaxLength = separator is null ? maxChars : (maxChars * 2) - 1;

            textBox.Text = output;

            caretIndex -= deletedChars;
            if (separator is not null)
                caretIndex += output.Count(c => c == separator) - text.Count(c => c == separator);

            if (caretIndex < 0)
                caretIndex = 0;

            textBox.CaretIndex = caretIndex;
            TextHelper.SetAltText(textBox, output);
            TextHelper.SetIsValidating(textBox, false);
        }

        private void PairingCodeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBoxSeparation(sender as TextBox, '-', 6);
        }

        private void NewDevicePortBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBoxSeparation(sender as TextBox, maxChars: 5);
            EnableConnectButton();
        }

        private void PairServiceButton_Click(object sender, RoutedEventArgs e)
        {
            PairService((ServiceDevice)DevicesObject.SelectedDevice.Device);
        }

        private bool PairService(ServiceDevice service)
        {
            var code = service.MdnsType == ServiceDevice.ServiceType.QrCode
                ? QrClass.Password
                : service.PairingCode;

            try
            {
                ADBService.PairNetworkDevice(service.ID, code);
            }
            catch (Exception ex)
            {
                DialogService.ShowMessage(ex.Message, "Pairing Error", DialogService.DialogIcon.Critical);
                return false;
            }

            return true;
        }

        private void ManualPairingPortBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBoxSeparation(sender as TextBox, maxChars: 5);
            EnablePairButton();
        }

        private void CancelManualPairing_Click(object sender, RoutedEventArgs e)
        {
            ManualPairingPanel.IsEnabled = false;
        }

        private void PairFlyoutCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (FindResource("PairServiceFlyout") is Flyout flyout)
            {
                flyout.Hide();
                PairingExpander.IsExpanded = false;
                DevicesObject.UnselectAll();
                DevicesObject.ConsolidateDevices();
            }
        }

        private void PairingCodeTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PairService((ServiceDevice)DevicesObject.SelectedDevice.Device);
            }
        }

        private void EnableMdns()
        {
            ADBService.IsMdnsEnabled = Settings.EnableMdns;
            if (Settings.EnableMdns)
            {
                QrClass = new();
            }
            else
            {
                ManualConnectionRadioButton.IsChecked = true;
            }
        }

        private void RemovePending_Click(object sender, RoutedEventArgs e)
        {
            fileOperationQueue.ClearPending();
        }

        private void RemoveCompleted_Click(object sender, RoutedEventArgs e)
        {
            fileOperationQueue.ClearCompleted();
        }

        private void OpenDefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", Settings.DefaultFolder);
        }

        private void RemovePendingAndCompleted_Click(object sender, RoutedEventArgs e)
        {
            fileOperationQueue.Clear();
        }

        private void StopFileOperations_Click(object sender, RoutedEventArgs e)
        {
            if (fileOperationQueue.IsActive)
                fileOperationQueue.Stop();
            else
                fileOperationQueue.Start();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            if (Storage.RetrieveBool(SystemVals.windowMaximized) == true)
                WindowState = WindowState.Maximized;

            if (Storage.RetrieveBool(SystemVals.detailedVisible) is bool and true)
            {
                FileOpVisibility(true);
            }

            if (double.TryParse(Storage.RetrieveValue(SystemVals.detailedHeight)?.ToString(), out double detailedHeight))
            {
                FileOpDetailedGrid.Height = detailedHeight;
                ResizeDetailedView();
            }
        }

        private void RestartAdbButton_Click(object sender, RoutedEventArgs e)
        {
            ADBService.KillAdbServer();
            MdnsService.State = MDNS.MdnsState.Disabled;
            ChangeConnectionType();
        }

        private void ManualAdbPath_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var dialog = new OpenFileDialog()
            {
                Multiselect = false,
                Title = "Select ADB Executable",
                Filter = "ADB Executable|adb.exe",
            };

            if (!string.IsNullOrEmpty(Settings.ManualAdbPath))
            {
                try
                {
                    dialog.InitialDirectory = Directory.GetParent(Settings.ManualAdbPath).FullName;
                }
                catch (Exception) { }
            }

            if (dialog.ShowDialog() == true)
            {
                Settings.ManualAdbPath = dialog.FileName;
            }
        }

        private void PairingExpander_Expanded(object sender, RoutedEventArgs e)
        {
            DevicesObject.UnselectAll();

            if (PairingExpander.IsExpanded)
            {
                RetrieveIp();
            }
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (((MenuItem)(FindResource("DeviceActionsMenu") as Menu).Items[0]).IsSubmenuOpen)
                return;

            DevicesObject.UnselectAll();
        }

        private void EnableDeviceRootToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menu && menu.DataContext is UILogicalDevice device && device.Device is LogicalDevice logical)
            {
                bool rootEnabled = ((LogicalDevice)device.Device).Root is AbstractDevice.RootStatus.Enabled;
                var rootTask = Task.Run(() =>
                {
                    logical.EnableRoot(!rootEnabled);
                });
                rootTask.ContinueWith((t) => Dispatcher.BeginInvoke(() =>
                {
                    if (logical.Root is AbstractDevice.RootStatus.Forbidden)
                        DialogService.ShowMessage("Root access cannot be enabled on selected device.", "Root Access", DialogService.DialogIcon.Critical);
                }));
            }
        }

        private void DriveItem_Click(object sender, RoutedEventArgs e)
        {
            ClearSelectedDrives();

            RepeaterHelper.SetIsSelected(sender as Button, true);
            RepeaterHelper.SetSelectedItems(DrivesItemRepeater, 1);
        }

        private void ClearSelectedDrives()
        {
            foreach (Button drive in DrivesItemRepeater.Children)
            {
                RepeaterHelper.SetIsSelected(drive, false);
            }

            RepeaterHelper.SetSelectedItems(DrivesItemRepeater, 0);
        }

        private void CurrentOperationDetailedDataGrid_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            if (e.Column is not DataGridColumn column)
                return;

            var checkbox = GetColumnCheckbox(column);

            if (checkbox.DataContext is FileOpColumn config)
            {
                config.Index = column.DisplayIndex;
            }
            else
            {
                checkbox.DataContext = CreateColumnConfig(column);
            }

            Storage.StoreValue(checkbox.Name, checkbox.DataContext);
        }

        private void DataGridColumnHeader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (((DataGridColumnHeader)sender).Column is not DataGridColumn column)
                return;

            var checkbox = GetColumnCheckbox(column);

            if (checkbox.DataContext is FileOpColumn config)
            {
                config.Width = e.NewSize.Width;
            }
            else
            {
                checkbox.DataContext = CreateColumnConfig(column);
            }

            Storage.StoreValue(checkbox.Name, checkbox.DataContext);
        }

        private void CollectionViewSource_Filter(object sender, FilterEventArgs e)
        {

        }

        private void ContextMenuDeleteItem_Click(object sender, RoutedEventArgs e)
        {
            DeleteFiles();
        }

        private async void DeleteFiles()
        {
            string deletedString;
            if (selectedFiles.Count() == 1)
                deletedString = DisplayName(selectedFiles.First());
            else
            {
                deletedString = $"{selectedFiles.Count()} ";
                if (selectedFiles.All(item => item.IsDirectory))
                    deletedString += "folders";
                else if (selectedFiles.All(item => !item.IsDirectory))
                    deletedString += "files";
                else
                    deletedString += "items";
            }

            var result = await DialogService.ShowConfirmation(
                $"The following will be deleted:\n{deletedString}",
                "Confirm Delete",
                "Delete",
                checkBoxText: Settings.EnableRecycle ? "Permanently Delete" : "",
                icon: DialogService.DialogIcon.Delete);

            if (result.Item1 is not ContentDialogResult.Primary)
                return;

            if (Settings.EnableRecycle && !result.Item2)
            {
                ShellFileOperation.MoveItems(CurrentADBDevice, selectedFiles, RECYCLE_PATH, CurrentPath, DirectoryLister.FileList, Dispatcher, DevicesObject.CurrentDevice);
            }
            else
            {
                ShellFileOperation.DeleteItems(CurrentADBDevice, selectedFiles, DirectoryLister.FileList, Dispatcher);
            }
        }

        private void RenameFile(string newName, FileClass file)
        {
            var newPath = $"{file.ParentPath}{(file.ParentPath.EndsWith('/') ? "" : "/")}{newName}{(Settings.ShowExtensions ? "" : file.Extension)}";
            if (DirectoryLister.FileList.Any(file => file.FullName == newName))
            {
                DialogService.ShowMessage($"{newPath} already exists", "Rename conflict", DialogService.DialogIcon.Exclamation);
                return;
            }

            try
            {
                ShellFileOperation.RenameItem(CurrentADBDevice, file, newPath);
            }
            catch (Exception e)
            {
                DialogService.ShowMessage(e.Message, "Rename Error", DialogService.DialogIcon.Critical);
                throw;
            }

            file.UpdatePath(newPath);
        }

        private void FileOperationsSplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
        {
            FileOpVisibility(false);
        }

        private void ContextMenuRenameItem_Click(object sender, RoutedEventArgs e)
        {
            var cell = CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]);

            cell.IsEditing = !cell.IsEditing;
        }

        private static FileClass GetFromCell(DataGridCellInfo cell) => CellConverter.GetDataGridCell(cell).DataContext as FileClass;

        private void NameColumnEdit_Loaded(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            TextHelper.SetAltObject(textBox, GetFromCell(ExplorerGrid.SelectedCells[1]));
            textBox.Focus();
        }

        private static string DisplayName(TextBox textBox) => DisplayName(textBox.DataContext as FilePath);
        private static string DisplayName(FilePath file) => Settings.ShowExtensions ? file.FullName : file.NoExtName;

        private void NameColumnEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            Rename(sender as TextBox);
        }

        private void Rename(TextBox textBox)
        {
            FileClass file = TextHelper.GetAltObject(textBox) as FileClass;
            var name = DisplayName(textBox);
            if (file.IsTemp)
            {
                if (string.IsNullOrEmpty(textBox.Text))
                {
                    DirectoryLister.FileList.Remove(file);
                    return;
                }
                try
                {
                    CreateNewItem(file, textBox.Text);
                }
                catch (Exception e)
                {
                    if (e is NotImplementedException)
                        throw;
                }
            }
            else if (!string.IsNullOrEmpty(textBox.Text) && textBox.Text != name)
            {
                try
                {
                    RenameFile(textBox.Text, file);
                }
                catch (Exception)
                { }
            }
        }

        private void NameColumnEdit_KeyDown(object sender, KeyEventArgs e)
        {
            var cell = CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]);
            var textBox = sender as TextBox;

            if (e.Key == Key.Enter)
                e.Handled = true;
            else if (e.Key == Key.Escape)
            {
                var name = DisplayName(textBox);
                if (string.IsNullOrEmpty(name))
                {
                    DirectoryLister.FileList.Remove(ExplorerGrid.SelectedItem as FileClass);
                }
                else
                {
                    textBox.Text = DisplayName(sender as TextBox);
                }
            }
            else
                return;

            if (ExplorerGrid.SelectedCells.Count > 0)
                cell.IsEditing = false;
        }

        private void NameColumnCell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            var cell = sender as DataGridCell;
            if (cell.IsEditing)
                return;

            e.Handled = true;
            clickCount = e.ClickCount;

            if (clickCount > 1)
            {
                DoubleClick(cell.DataContext);
            }
            else
            {
                UnfocusPathBox();
                var row = DataGridRow.GetRowContainingElement(cell);
                var current = row.GetIndex();

                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    ExplorerGrid.SelectedItems.Clear();

                    int firstUnselected = firstSelectedRow, lastUnselected = current + 1;
                    if (current < firstSelectedRow)
                    {
                        firstUnselected = current;
                        lastUnselected = firstSelectedRow + 1;
                    }

                    for (int i = firstUnselected; i < lastUnselected; i++)
                    {
                        ExplorerGrid.SelectedItems.Add(ExplorerGrid.Items[i]);
                    }

                    return;
                }
                else if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    row.IsSelected = !row.IsSelected;
                    return;
                }

                firstSelectedRow = row.GetIndex();

                if (!row.IsSelected)
                {
                    ExplorerGrid.SelectedItems.Clear();
                    row.IsSelected = true;
                }
                else
                {
                    if (ExplorerGrid.SelectedItems.Count > 1)
                    {
                        ExplorerGrid.SelectedItems.Clear();
                        row.IsSelected = true;

                        return;
                    }

                    if (DevicesObject.CurrentDevice.Root != AbstractDevice.RootStatus.Enabled
                        && ((FileClass)cell.DataContext).Type is not (FileType.File or FileType.Folder))
                        return;

                    Task.Delay(DOUBLE_CLICK_TIMEOUT).ContinueWith(t =>
                    {
                        if (clickCount != 1)
                            return;

                        Dispatcher.Invoke(() =>
                        {
                            if (e.LeftButton == MouseButtonState.Released && ExplorerGrid.SelectedItems.Count == 1)
                                cell.IsEditing = true;
                        });
                    });
                }
            }
        }

        private void NameColumnCell_MouseEnter(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DataGridRow.GetRowContainingElement(sender as DataGridCell).IsSelected = true;
        }

        private void NameColumnCell_MouseLeave(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var cell = sender as DataGridCell;
                var row = DataGridRow.GetRowContainingElement(cell);
                var index = row.GetIndex();
                var pos = e.GetPosition(cell).Y;

                if (index == firstSelectedRow)
                    return;

                if ((index > firstSelectedRow && pos < 0) || (index < firstSelectedRow && pos > 0))
                    row.IsSelected = false;
            }
        }

        private void NameColumnEdit_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBoxSeparation(sender as TextBox, numeric: false, specialChars: INVALID_ANDROID_CHARS);
        }

        private void PairingCodeTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            (sender as TextBox).SelectAll();
        }

        private void Drive_GotFocus(object sender, RoutedEventArgs e)
        {
            ClearSelectedDrives();

            RepeaterHelper.SetIsSelected(sender as Button, true);
            RepeaterHelper.SetSelectedItems(DrivesItemRepeater, 1);
        }

        private void ContextMenuCutItem_Click(object sender, RoutedEventArgs e)
        {
            CutFiles(selectedFiles);
        }

        private void CutFiles(IEnumerable<FileClass> items)
        {
            ClearCutFiles();

            foreach (var item in items)
            {
                item.IsCut = true;
            }

            CutItems.AddRange(items);

            CutMenuButton.IsEnabled = false;
            PasteMenuButton.IsEnabled = PasteEnabled();
        }

        private static void ClearCutFiles()
        {
            CutItems.ForEach(f => f.IsCut = false);
            CutItems.Clear();
        }

        private void PasteFiles()
        {
            var targetPath = ExplorerGrid.SelectedItems.Count == 1 ? ((FileClass)ExplorerGrid.SelectedItem).FullPath : CurrentPath;
            var pasteItems = CutItems.Where(f => f.Relation(targetPath) is not (RelationType.Self or RelationType.Descendant));

            ShellFileOperation.MoveItems(CurrentADBDevice, pasteItems, targetPath, CurrentPath, DirectoryLister.FileList, Dispatcher, DevicesObject.CurrentDevice);

            ClearCutFiles();
            PasteMenuButton.IsEnabled = PasteEnabled();
        }

        private void ContextMenuPasteItem_Click(object sender, RoutedEventArgs e)
        {
            PasteFiles();
        }

        private void NewItem(bool isFolder)
        {
            var namePrefix = $"New {(isFolder ? "Folder" : "File")}";
            var index = ExistingIndexes(namePrefix);

            FileClass newItem = new($"{namePrefix}{index}", CurrentPath, isFolder ? FileType.Folder : FileType.File, isTemp: true);
            DirectoryLister.FileList.Insert(0, newItem);

            ExplorerGrid.ScrollIntoView(newItem);
            ExplorerGrid.SelectedItem = newItem;
            var cell = CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]);
            if (cell is not null)
                cell.IsEditing = true;
        }

        private string ExistingIndexes(string namePrefix)
        {
            var existingItems = DirectoryLister.FileList.Where(item => item.FullName.StartsWith(namePrefix));
            var suffixes = existingItems.Select(item => item.FullName[namePrefix.Length..].Trim());
            var indexes = (from i in suffixes
                          where int.TryParse(i, out _)
                          select int.Parse(i)).ToList();
            if (suffixes.Any(s => s == ""))
                indexes.Add(0);

            indexes.Sort();
            if (!indexes.Any() || indexes[0] != 0)
                return "";

            for (int i = 0; i < indexes.Count; i++)
            {
                if (indexes[i] > i)
                    return $" {i}";
            }

            return $" {indexes.Count}";
        }

        private void CreateNewItem(FileClass file, string newName)
        {
            file.UpdatePath($"{CurrentPath}{(CurrentPath == "/" ? "" : "/")}{newName}");

            try
            {
                if (file.Type is FileType.Folder)
                    ShellFileOperation.MakeDir(CurrentADBDevice, file.FullPath);
                else if (file.Type is FileType.File)
                    ShellFileOperation.MakeFile(CurrentADBDevice, file.FullPath);
                else
                    throw new NotImplementedException();
            }
            catch (Exception e)
            {
                DialogService.ShowMessage(e.Message, "Create Error", DialogService.DialogIcon.Critical);
                DirectoryLister.FileList.Remove(file);
                throw;
            }

            file.IsTemp = false;
            file.ModifiedTime = DateTime.Now;
            if (file.Type is FileType.File)
                file.Size = 0;

            var index = DirectoryLister.FileList.IndexOf(file);
            DirectoryLister.FileList.Remove(file);
            DirectoryLister.FileList.Insert(index, file);
            ExplorerGrid.SelectedItem = file;
        }

        private void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            NewItem(true);
        }

        private void NewFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            NewItem(false);
        }

        private void ExplorerGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                e.Handled = true;
                DeleteFiles();
            }
        }

        private void RebootMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            var rebootArg = TextHelper.GetAltText(menu);
            var device = (menu.DataContext as UILogicalDevice).Device as LogicalDevice;
            try
            {
                Task.Run(() => ADBService.AdbDevice.Reboot(device, rebootArg));
            }
            catch (Exception ex)
            {
                DialogService.ShowMessage(ex.Message, "Reboot Error", DialogService.DialogIcon.Critical);
                throw;
            }
        }

        private void MenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            TextHelper.SetAltObject(DevicesSplitView, true);
        }

        private void MenuItem_SubmenuClosed(object sender, RoutedEventArgs e)
        {
            if (!(sender as MenuItem).IsSubmenuOpen)
                TextHelper.SetAltObject(DevicesSplitView, false);
        }

        private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(((FilePath)ExplorerGrid.SelectedItem).FullPath);
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            ClearLogs();
        }

        private void ClearLogs()
        {
            CommandLog.Clear();
            LogTextBox.Clear();
        }

        private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshDevices(true);
        }
    }
}
