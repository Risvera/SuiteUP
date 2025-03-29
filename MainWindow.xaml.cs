using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Popups;
using Microsoft.UI.Dispatching; // Add this namespace for DispatcherQueue and DispatcherQueueTimer


namespace SuiteUP
{
    public partial class MainWindow : Window
    {
        private List<string> selectedFolders = new List<string>();
        private CancellationTokenSource backupCancellation;
        private bool isBackupRunning = false;

        // Add these variables for speed calculation and UI updates
        private readonly object speedLock = new object();
        private double currentSpeed = 0;
        private long bytesInCurrentInterval = 0;
        private DispatcherQueue uiDispatcher;
        private DispatcherQueueTimer speedUpdateTimer;

        // Import Windows API to turn off the display
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int SendMessage(int hWnd, int wMsg, int wParam, int lParam);

        // Constants for monitor power state
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_OFF = 2;

        public MainWindow()
        {
            InitializeComponent();
            LoadDrives();
            LoadBackupSettings();
            this.ExtendsContentIntoTitleBar = true;

            // Store dispatcher reference for UI updates
            uiDispatcher = DispatcherQueue.GetForCurrentThread();

            // Create a timer that will update the UI more efficiently
            speedUpdateTimer = uiDispatcher.CreateTimer();
            speedUpdateTimer.Interval = TimeSpan.FromMilliseconds(100);
            speedUpdateTimer.Tick += SpeedUpdateTimer_Tick;
        }

        //Basic UI Functionalities

        private void LoadDrives()
        {
            driveComboBox.Items.Clear();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                driveComboBox.Items.Add($"{drive.Name} ({drive.TotalSize / (1024 * 1024 * 1024)}GB, {drive.AvailableFreeSpace / (1024 * 1024 * 1024)}GB free)");
            }
        }

        // Method to turn off the display
        private void TurnOffDisplay()
        {
            SendMessage(-1, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_OFF);
        }

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !selectedFolders.Contains(folder.Path))
            {
                selectedFolders.Add(folder.Path);
                folderListBox.Items.Add(new ListBoxItem
                {
                    Content = folder.Path,
                    Tag = folder.Path
                });
                SaveBackupSettings();
            }
        }

        private void LoadBackupSettings()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            if (localSettings.Values.ContainsKey("BackupDrive"))
            {
                string backupDrive = localSettings.Values["BackupDrive"] as string;
                var matchingDrive = driveComboBox.Items.Cast<string>()
                    .FirstOrDefault(d => d.StartsWith(backupDrive));
                if (matchingDrive != null)
                {
                    driveComboBox.SelectedItem = matchingDrive;
                }
            }
            if (localSettings.Values.ContainsKey("SelectedFolders"))
            {
                string folders = localSettings.Values["SelectedFolders"] as string;
                selectedFolders = folders.Split(';').Where(f => !string.IsNullOrEmpty(f)).ToList();
                foreach (var folder in selectedFolders)
                {
                    folderListBox.Items.Add(new ListBoxItem
                    {
                        Content = folder,
                        Tag = folder
                    });
                }
            }
        }

        private void CleanupOldBackups(string backupRoot)
        {
            try
            {
                var backupFolders = Directory.GetDirectories(backupRoot);
                foreach (var folder in backupFolders)
                {
                    var versions = Directory.GetDirectories(folder)
                        .Select(d => new DirectoryInfo(d))
                        .OrderByDescending(d => d.CreationTime)
                        .Skip(5) // Keep last 5 versions
                        .ToList();

                    foreach (var version in versions)
                    {
                        try
                        {
                            version.Delete(true);
                        }
                        catch { /* Log error */ }
                    }
                }
            }
            catch { /* Log error */ }
        }

        private async Task ShowMessageDialog(string message)
        {
            var dialog = new MessageDialog(message);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
            await dialog.ShowAsync();
        }

        private void DeleteFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string folderPath)
            {
                selectedFolders.Remove(folderPath);
                folderListBox.Items.Remove(folderListBox.Items.Cast<ListBoxItem>()
                    .FirstOrDefault(item => item.Tag as string == folderPath));
                SaveBackupSettings();
            }
        }

        private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e)
        {
            LoadDrives();
        }

        private void folderListBox_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement element && element.DataContext is string folderPath)
            {
                var menuFlyout = new MenuFlyout();
                var deleteItem = new MenuFlyoutItem { Text = "Delete" };
                deleteItem.Click += (s, args) =>
                {
                    var button = new Button { Tag = folderPath };
                    DeleteFolderButton_Click(button, null);
                };
                menuFlyout.Items.Add(deleteItem);
                menuFlyout.ShowAt(element);
            }
        }

        private void SaveBackupSettings(string backupDrive = null)
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            if (backupDrive != null)
            {
                localSettings.Values["BackupDrive"] = backupDrive;
            }
            localSettings.Values["SelectedFolders"] = string.Join(";", selectedFolders);
        }

        private void ShowToastNotification(string title, string message)
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }

        //File Transfer Functionalities

        private void SpeedUpdateTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            // Update the UI with the current speed
            UpdateSpeedUI(currentSpeed);
        }

        private void UpdateSpeedUI(double speed)
        {
            transferSpeedTextBlock.Text = $"{speed:F2} MB/s";

            // Scale the indicator (max height at 100MB/s)
            double maxSpeed = 100.0;
            double percentage = Math.Min(speed / maxSpeed, 1.0);
            double height = speedBorder.ActualHeight > 0 ? speedBorder.ActualHeight : 120;

            // Smoothly animate the speed indicator height
            speedIndicatorRect.Height = percentage * height;
        }

        //Basic UI Functionalities
        // (No changes to existing methods...)

        private async void BackupButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset speed display before starting backup
            transferSpeedTextBlock.Text = "0.00 MB/s";
            speedIndicatorRect.Height = 0;
            currentSpeed = 0;
            bytesInCurrentInterval = 0;

            // Check if a backup is already running
            if (isBackupRunning)
            {
                await ShowMessageDialog("A backup is already in progress");
                return;
            }

            // Validation code (no changes)...
            if (driveComboBox.SelectedItem == null)
            {
                await ShowMessageDialog("Please select a backup drive");
                return;
            }

            if (selectedFolders.Count == 0)
            {
                await ShowMessageDialog("Please select at least one folder to backup");
                return;
            }

            // Get the selected drive
            string selectedDrive = driveComboBox.SelectedItem.ToString().Split(' ')[0];
            SaveBackupSettings(selectedDrive);

            // Create backup folder if it doesn't exist
            string backupRootPath = Path.Combine(selectedDrive, "SuiteUP_Backups");
            Directory.CreateDirectory(backupRootPath);

            // Create a timestamp for this backup
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

            // Setup UI for backup process
            isBackupRunning = true;
            backupCancellation = new CancellationTokenSource();
            backupButton.Content = "Cancel Backup";
            backupProgressBar.Value = 0;
            statusTextBlock.Text = "Preparing...";
            progressTextBlock.Text = "0%";
            currentFileTextBlock.Text = "";
            elapsedTimeTextBlock.Text = "Time elapsed: 0:00:00";

            // Start the speed update timer
            speedUpdateTimer.Start();

            // Start the stopwatch to measure elapsed time
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            // Start a timer to update the elapsed time display
            var elapsedTimer = new Timer(_ =>
            {
                uiDispatcher.TryEnqueue(() =>
                {
                    TimeSpan elapsed = stopwatch.Elapsed;
                    elapsedTimeTextBlock.Text = $"Time elapsed: {elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                });
            }, null, 0, 1000);

            try
            {
                // Count total files for progress calculation
                statusTextBlock.Text = "Counting files...";
                int totalFiles = 0;
                int processedFiles = 0;

                // Count files in all selected folders
                await Task.Run(() =>
                {
                    foreach (var folder in selectedFolders)
                    {
                        if (Directory.Exists(folder))
                        {
                            totalFiles += Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length;
                        }
                    }
                });

                // If no files found
                if (totalFiles == 0)
                {
                    uiDispatcher.TryEnqueue(() =>
                    {
                        backupProgressBar.Value = 100;
                        statusTextBlock.Text = "No files to backup";
                    });
                    return;
                }

                // Process each selected folder
                foreach (var sourceFolder in selectedFolders)
                {
                    if (!Directory.Exists(sourceFolder) || backupCancellation.Token.IsCancellationRequested)
                        continue;

                    // Create folder name from source folder path
                    string folderName = new DirectoryInfo(sourceFolder).Name;
                    string backupFolderPath = Path.Combine(backupRootPath, folderName);

                    // Create timestamp folder for this backup
                    string timestampFolderPath = Path.Combine(backupFolderPath, timestamp);
                    Directory.CreateDirectory(timestampFolderPath);

                    // Copy files with progress reporting
                    await Task.Run(() =>
                    {
                        CopyDirectory(sourceFolder, timestampFolderPath, (file, bytesCopied) =>
                        {
                            processedFiles++;

                            // Update bytes copied in this interval (for speed calculation)
                            Interlocked.Add(ref bytesInCurrentInterval, bytesCopied);

                            int progressPercentage = (int)((double)processedFiles / totalFiles * 100);

                            // Only update progress and file name, not the speed indicator (that's handled by the timer)
                            uiDispatcher.TryEnqueue(() =>
                            {
                                backupProgressBar.Value = progressPercentage;
                                progressTextBlock.Text = $"{progressPercentage}% ({processedFiles}/{totalFiles})";
                                currentFileTextBlock.Text = $"Copying: {Path.GetFileName(file)}";
                                statusTextBlock.Text = "Backing up...";
                            });

                            return !backupCancellation.Token.IsCancellationRequested;
                        });
                    }, backupCancellation.Token);

                    // Clean up old backups (keep only 5 most recent)
                    await Task.Run(() => CleanupOldBackups(backupRootPath), backupCancellation.Token);

                    // Update UI on completion
                    uiDispatcher.TryEnqueue(() =>
                    {
                        backupProgressBar.Value = 100;
                        statusTextBlock.Text = "Backup completed successfully";
                        progressTextBlock.Text = "100%";
                        currentFileTextBlock.Text = "";
                        ShowToastNotification("Backup Complete", "Your files have been backed up successfully");
                    });
                }
            }
            catch (OperationCanceledException)
            {
                uiDispatcher.TryEnqueue(() =>
                {
                    statusTextBlock.Text = "Backup cancelled";
                });
            }
            catch (Exception ex)
            {
                uiDispatcher.TryEnqueue(() =>
                {
                    statusTextBlock.Text = "Error during backup";
                    ShowToastNotification("Backup Error", ex.Message);
                });
            }
            finally
            {
                // Cleanup
                elapsedTimer.Dispose();
                stopwatch.Stop();
                speedUpdateTimer.Stop();

                uiDispatcher.TryEnqueue(() =>
                {
                    backupButton.Content = "Start Backup";
                    isBackupRunning = false;

                    // Handle shutdown or display off if requested
                    if (shutdownAfterBackupCheckBox.IsChecked.GetValueOrDefault() && statusTextBlock.Text == "Backup completed successfully")
                    {
                        Process.Start("shutdown", "/s /t 60 /c \"Shutdown scheduled by SuiteUP after backup completion\"");
                        ShowToastNotification("Shutdown Scheduled", "Your PC will shutdown in 60 seconds");
                    }
                    else if (turnOffDisplayCheckBox.IsChecked.GetValueOrDefault() && statusTextBlock.Text == "Backup completed successfully")
                    {
                        TurnOffDisplay();
                    }
                });
            }
        }

        // Helper method to copy directory with progress reporting and speed calculation
        private void CopyDirectory(string sourceDir, string destDir, Func<string, long, bool> progressCallback)
        {
            // Create all directories
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, destDir));
            }

            // Variables for speed calculation
            Stopwatch speedTimer = new Stopwatch();
            speedTimer.Start();

            // Periodic speed calculation timer (more frequent than UI updates)
            using var speedCalculationTimer = new Timer(_ =>
            {
                // Calculate MB/s (divide bytes by elapsed seconds)
                double elapsedSeconds = speedTimer.Elapsed.TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    // Read the current interval bytes and reset the counter
                    long bytes = Interlocked.Exchange(ref bytesInCurrentInterval, 0);

                    // Calculate current speed in MB/s
                    lock (speedLock)
                    {
                        // Use exponential moving average for smoother updates
                        double instantSpeed = bytes / (1024 * 1024) / (elapsedSeconds);
                        currentSpeed = currentSpeed * 0.7 + instantSpeed * 0.3;
                    }

                    // Reset the timer for the next interval
                    speedTimer.Restart();
                }
            }, null, 0, 200); // Calculate speed every 200ms

            // Copy all files
            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string newFilePath = filePath.Replace(sourceDir, destDir);

                // Create directory if it doesn't exist (for safety)
                Directory.CreateDirectory(Path.GetDirectoryName(newFilePath));

                // Get file size for speed calculation
                var fileInfo = new FileInfo(filePath);
                long fileSize = fileInfo.Length;

                // Use File.Copy for native Windows copy behavior
                File.Copy(filePath, newFilePath, true);

                // Report progress with bytes copied
                if (!progressCallback(filePath, fileSize))
                    return; // Stop if cancellation requested
            }

            speedTimer.Stop();
        }

        // Testing function for the speed bar
        private void TestSpeedBar()
        {
            double speed = 0;
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += (s, e) =>
            {
                // Simulate changing speed from 0 to 100 MB/s
                speed += 1;
                if (speed > 100)
                {
                    timer.Stop();
                    return;
                }

                UpdateSpeedUI(speed);
            };
            timer.Start();
        }
    }
}