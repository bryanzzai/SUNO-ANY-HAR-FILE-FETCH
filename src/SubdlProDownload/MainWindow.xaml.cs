using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using SunoHarFileDownload.Models;
using SunoHarFileDownload.Services;

namespace SunoHarFileDownload;

public partial class MainWindow : Window
{
    private const int MaxDisplayedRows = 100;
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<HarEntryRow> Rows { get; } = [];
    public string ReleaseLabel => $"RAW {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void BrowseHarButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a HAR file",
            Filter = "HAR files (*.har)|*.har|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
            HarFileTextBox.Text = dialog.FileName;
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where downloaded media should be written",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) &&
            Directory.Exists(OutputFolderTextBox.Text))
            dialog.InitialDirectory = OutputFolderTextBox.Text;

        if (dialog.ShowDialog(this) == true)
            OutputFolderTextBox.Text = dialog.FolderName;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var harPath = HarFileTextBox.Text.Trim();

        if (!File.Exists(harPath))
        {
            MessageBox.Show(this, "Choose an existing .har file first.", "HAR file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BeginOperation("Boring through HAR entries…");
        Rows.Clear();
        CountTextBlock.Text = "scanning…";
        DownloadButton.IsEnabled = false;

        try
        {
            var scan = await HarScanner.ScanAsync(harPath, MaxDisplayedRows, _operationCts!.Token);

            foreach (var row in scan.Rows)
                Rows.Add(row);

            CountTextBlock.Text =
                $"HAR: {scan.TotalEntries} | shown: {scan.DisplayedEntries} | media: {scan.MediaCandidates} | recoverable: {scan.RecoverableCandidates} | errors: {scan.ParseProblems}";

            StatusTextBlock.Text = scan.TotalEntries > MaxDisplayedRows
                ? $"Scan complete. Fixed raw window shows first {MaxDisplayedRows} of {scan.TotalEntries} entries in original HAR order."
                : $"Scan complete. Showing all {scan.TotalEntries} HAR entries in original order.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "HAR scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "FATAL HAR SCAN ERROR — full dump shown.";
            MessageBox.Show(
                this,
                ex.ToString(),
                "FATAL HAR SCAN ERROR",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private void DownloadCheckBox_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = Rows.Any(row => row.IsSelected);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = Rows
            .Where(row => row.IsSelected)
            .OrderBy(row => row.EntryIndex)
            .ToArray();

        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Tick one or more rows first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            MessageBox.Show(this, "Choose an output folder first.", "Output folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BeginOperation($"Preparing {selected.Length} selected download(s)…");
        ProgressBar.Maximum = selected.Length;
        ProgressBar.Value = 0;

        try
        {
            Directory.CreateDirectory(outputFolder);

            // The raw grid is never renamed. Roman numbering is calculated only now,
            // at the write boundary, and only for exact same-name + same-extension twins.
            var finalNames = RomanNaming.BuildFinalNames(selected);
            using var downloader = new MediaDownloader();

            var completed = 0;
            var failed = 0;

            foreach (var row in selected)
            {
                _operationCts!.Token.ThrowIfCancellationRequested();
                var finalName = finalNames[row];

                row.DownloadStatus = $"Downloading -> {finalName}";
                StatusTextBlock.Text = $"Downloading HAR entry {row.EntryIndex}: {row.RawFileName} -> {finalName}";

                try
                {
                    await downloader.DownloadAsync(row, outputFolder, finalName, _operationCts.Token);
                    row.DownloadStatus = $"Saved: {finalName}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    row.DownloadStatus = $"FAILED: {ex.GetType().Name}: {ex.Message}";
                }

                completed++;
                ProgressBar.Value = completed;
            }

            StatusTextBlock.Text = failed == 0
                ? $"Download complete. {completed} file(s) written."
                : $"Download pass complete. {completed - failed} saved, {failed} failed. Failed rows remain visible.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "FATAL DOWNLOAD SETUP ERROR — full dump shown.";
            MessageBox.Show(
                this,
                ex.ToString(),
                "FATAL DOWNLOAD SETUP ERROR",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
            DownloadButton.IsEnabled = Rows.Any(row => row.IsSelected);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => _operationCts?.Cancel();

    private void BeginOperation(string status)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        ScanButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
        ProgressBar.Value = 0;
    }

    private void EndOperation()
    {
        ScanButton.IsEnabled = true;
        CancelButton.IsEnabled = false;

        _operationCts?.Dispose();
        _operationCts = null;
    }
}
