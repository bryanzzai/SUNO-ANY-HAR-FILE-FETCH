using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SunoHarFileDownload.Models;
using SunoHarFileDownload.Services;

namespace SunoHarFileDownload;

public partial class MainWindow : Window
{
    private const int MaxDisplayedRows = 100;
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<HarEntryRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    public string ReleaseLabel => $"RAW {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

    public MainWindow()
    {
        InitializeComponent();
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
        DataContext = this;
    }

    private bool FilterRow(object item)
    {
        if (item is not HarEntryRow row) return false;

        var filter = ResultsFilterTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter)) return true;

        return row.ReleaseName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ResultsFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RowsView?.Refresh();
        UpdateVisibleRowCount();
    }

    private void UpdateVisibleRowCount()
    {
        var totalRows = Rows.Count;
        var visibleRows = RowsView?.Cast<object>().Count(item => item is HarEntryRow) ?? totalRows;

        CountTextBlock.Text = string.IsNullOrWhiteSpace(ResultsFilterTextBox?.Text)
            ? $"{totalRows} rows"
            : $"{visibleRows} of {totalRows} rows";
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

        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) && Directory.Exists(OutputFolderTextBox.Text))
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

        BeginOperation("Scanning HAR entries…");
        Rows.Clear();
        RowsView.Refresh();
        CountTextBlock.Text = "scanning…";

        try
        {
            var scan = await HarScanner.ScanAsync(harPath, MaxDisplayedRows, _operationCts!.Token);

            foreach (var row in scan.Rows)
                Rows.Add(row);

            RowsView.Refresh();
            UpdateVisibleRowCount();

            StatusTextBlock.Text = scan.DownloadableEntries > MaxDisplayedRows
                ? $"Scan complete. Found {scan.DownloadableEntries} downloadable audio/video entries; showing first {MaxDisplayedRows}."
                : $"Scan complete. Found {scan.DownloadableEntries} downloadable audio/video entries.";

            if (scan.OpaquePayloadEntries > 0)
                StatusTextBlock.Text += $" {scan.OpaquePayloadEntries} source entr(y/ies) have matching rights records and will be fetched live when selected.";

            if (scan.ParseProblems > 0)
                StatusTextBlock.Text += $" Skipped {scan.ParseProblems} malformed entries.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "HAR scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "FATAL HAR SCAN ERROR";
            MessageBox.Show(this, ex.ToString(), "FATAL HAR SCAN ERROR", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
            DownloadButton.IsEnabled = Rows.Any(row => row.IsSelected);
        }
    }

    private void DownloadCheckBox_Click(object sender, RoutedEventArgs e)
        => DownloadButton.IsEnabled = Rows.Any(row => row.IsSelected);

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = Rows.Where(row => row.IsSelected).OrderBy(row => row.EntryIndex).ToArray();
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

        BeginOperation($"Preparing {selected.Length} selected song(s)…");
        ProgressBar.Maximum = selected.Length;
        ProgressBar.Value = 0;

        try
        {
            Directory.CreateDirectory(outputFolder);
            var finalNames = RomanNaming.BuildFinalNames(selected);
            var pipeline = new SunoWavPipeline();
            var zipPath = await pipeline.CreateZipAsync(
                selected,
                finalNames,
                outputFolder,
                (index, row, phase) =>
                {
                    StatusTextBlock.Text = $"{phase} {index}/{selected.Length}: {row.ReleaseName}";
                    ProgressBar.Value = index - 1;
                },
                _operationCts!.Token);

            ProgressBar.Value = selected.Length;
            StatusTextBlock.Text = $"Complete. {selected.Length} WAV file(s) packed in {Path.GetFileName(zipPath)}.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "FATAL DOWNLOAD ERROR";
            MessageBox.Show(this, ex.ToString(), "FATAL DOWNLOAD ERROR", MessageBoxButton.OK, MessageBoxImage.Error);
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

        BrowseHarButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        BrowseOutputButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
        ProgressBar.Value = 0;
    }

    private void EndOperation()
    {
        BrowseHarButton.IsEnabled = true;
        ScanButton.IsEnabled = true;
        BrowseOutputButton.IsEnabled = true;
        CancelButton.IsEnabled = false;

        _operationCts?.Dispose();
        _operationCts = null;
    }
}
