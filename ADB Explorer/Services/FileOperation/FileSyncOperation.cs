﻿using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class FileSyncOperation : FileOperation
{
    private Task<AdbSyncStatsInfo> operationTask;
    private CancellationTokenSource cancelTokenSource;
    private ObservableList<FileOpProgressInfo> progressUpdates;

    public override SyncFile FilePath { get; }

    public override SyncFile AndroidPath => FilePath.PathType is AbstractFile.FilePathType.Android
        ? FilePath
        : TargetPath;

    public DiskUsage AdbProcess { get; private set; } = new(new());

    public string BytesTransferred { get; private set; }

    public int? DiskUsageProgress { get; private set; }

    public FileSyncOperation(
        OperationType operationName,
        SyncFile sourcePath,
        SyncFile targetPath,
        ADBService.AdbDevice adbDevice,
        Dispatcher dispatcher) : base(sourcePath, adbDevice, dispatcher)
    {
        OperationName = operationName;
        FilePath = sourcePath;
        TargetPath = targetPath;

        var procTask = AsyncHelper.WaitUntil(() => !string.IsNullOrEmpty(AdbProcess.Process.StartInfo.Arguments),
                                             TimeSpan.FromSeconds(10),
                                             TimeSpan.FromMilliseconds(100),
                                             new());

        procTask.ContinueWith((t) =>
        {
            if (AdbProcess.Process.StartInfo.FileName == Data.ProgressRedirectionPath)
            {
                var children = ProcessHandling.GetChildProcesses(AdbProcess.Process, false);
                var adbProc = children.FirstOrDefault(proc => proc.ProcessName == AdbExplorerConst.ADB_PROCESS);

                if (adbProc is null)
                    return;

                AdbProcess = new(adbProc);
                AdbProcess.PropertyChanged += AdbProcess_PropertyChanged;
            }
        });
    }

    private void AdbProcess_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var totalBytes = OperationName is OperationType.Push ? AdbProcess.ReadRate : AdbProcess.WriteRate;
        
        BytesTransferred = totalBytes?.ToSize();
        OnPropertyChanged(nameof(BytesTransferred));

        if (!FilePath.IsDirectory && totalBytes is not null) // FilePath.Size is ulong and > 0
        {
            DiskUsageProgress = (int)(100 * (double)totalBytes / FilePath.Size);
            OnPropertyChanged(nameof(DiskUsageProgress));
        }
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        StatusInfo = new InProgSyncProgressViewModel();
        cancelTokenSource = new CancellationTokenSource();

        progressUpdates = new();
        progressUpdates.CollectionChanged += ProgressUpdates_CollectionChanged;

        string cmd = "", arg = "";
        if (OperationName is OperationType.Push)
        {
            cmd = "push";
        }
        else
        {
            cmd = "pull";
            arg = "-a";
        }

        var targetPath = TargetPath.IsDirectory ? TargetPath.ParentPath : TargetPath.FullPath;

        operationTask = Task.Run(() =>
            Device.DoFileSync(cmd, arg, targetPath, FilePath.FullPath, AdbProcess.Process, ref progressUpdates, cancelTokenSource.Token),
            cancelTokenSource.Token);

        operationTask.ContinueWith((t) => progressUpdates.CollectionChanged -= ProgressUpdates_CollectionChanged);

        operationTask.ContinueWith((t) => 
        {
            Status = OperationStatus.Completed;
            if (t.Result is null)
                StatusInfo = Data.Settings.AdbProgressMethod is AppSettings.ProgressMethod.Console
                ? StatusInfo = new CompletedShellProgressViewModel()
                : null;
            else if (t.Result.FilesTransferred + t.Result.FilesSkipped < 1)
                StatusInfo = new CompletedShellProgressViewModel();
            else
                StatusInfo = new CompletedSyncProgressViewModel(t.Result);

        }, TaskContinuationOptions.OnlyOnRanToCompletion);

        operationTask.ContinueWith((t) =>
        {
            Status = OperationStatus.Canceled;
            StatusInfo = new CanceledOpProgressViewModel();
        }, TaskContinuationOptions.OnlyOnCanceled);

        operationTask.ContinueWith((t) =>
        {
            string message = string.IsNullOrEmpty(t.Exception.InnerException.Message)
                ? progressUpdates.OfType<SyncErrorInfo>().Last().Message
                : t.Exception.InnerException.Message;

            Status = OperationStatus.Failed;
            StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void ProgressUpdates_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (Status is not OperationStatus.InProgress)
            return;

        AddUpdates(e.NewItems.Cast<FileOpProgressInfo>());

        if (progressUpdates.LastOrDefault() is AdbSyncProgressInfo currProgress and not null)
        {
            StatusInfo = new InProgSyncProgressViewModel(currProgress);
        }
    }

    public override void Cancel()
    {
        if (Status != OperationStatus.InProgress)
        {
            throw new Exception("Cannot cancel a deactivated operation!");
        }

        cancelTokenSource.Cancel();
    }

    public override void ClearChildren()
    {
        AndroidPath.Children.Clear();
        progressUpdates.Clear();
    }

    public override void AddUpdates(IEnumerable<FileOpProgressInfo> newUpdates)
        => AndroidPath.AddUpdates(newUpdates, this);

    public override void AddUpdates(params FileOpProgressInfo[] newUpdates)
        => AndroidPath.AddUpdates(newUpdates, this);

    public static FileSyncOperation PullFile(SyncFile sourcePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, Dispatcher dispatcher)
        => new(OperationType.Pull, sourcePath, targetPath, adbDevice, dispatcher);

    public static FileSyncOperation PushFile(SyncFile sourcePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, Dispatcher dispatcher)
        => new(OperationType.Push, sourcePath, targetPath, adbDevice, dispatcher);
}
