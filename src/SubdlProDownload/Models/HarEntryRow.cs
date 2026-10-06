using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SunoHarFileDownload.Models;

public sealed class HarEntryRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _downloadStatus = string.Empty;

    public required int EntryIndex { get; init; }
    public required string Method { get; init; }
    public required int StatusCode { get; init; }
    public required string Classification { get; init; }
    public required string MimeType { get; init; }
    public required string RawFileName { get; init; }
    public required string Url { get; init; }
    public required string SizeText { get; init; }
    public required string RecoveryMode { get; init; }
    public required string Diagnostic { get; init; }
    public required bool IsMediaCandidate { get; init; }
    public required bool CanDownload { get; init; }

    public byte[]? EmbeddedBody { get; init; }
    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        set
        {
            if (_downloadStatus == value) return;
            _downloadStatus = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record HarScanResult(
    int TotalEntries,
    int DisplayedEntries,
    int MediaCandidates,
    int RecoverableCandidates,
    int ParseProblems,
    IReadOnlyList<HarEntryRow> Rows);
