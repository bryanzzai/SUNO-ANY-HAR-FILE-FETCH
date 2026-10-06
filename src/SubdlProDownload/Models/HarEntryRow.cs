using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SunoHarFileDownload.Models;

public sealed class HarEntryRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public required int EntryIndex { get; init; }
    public required string ReleaseName { get; init; }
    public required string RawFileName { get; init; }
    public string OutputFileName { get; init; } = string.Empty;
    public string ContentId { get; init; } = string.Empty;
    public required string Url { get; init; }
    public required string PayloadStatus { get; init; }
    public bool IsOpaquePayload { get; init; }

    public byte[]? EmbeddedBody { get; init; }
    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public HarRequestRecipe? RightsRequest { get; init; }

    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record HarScanResult(int TotalEntries, int DownloadableEntries, int DisplayedEntries, int OpaquePayloadEntries, int ParseProblems, IReadOnlyList<HarEntryRow> Rows);
public sealed record HarRequestRecipe(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body);
public sealed record MangoLicense(string Key, string Iv, string? GuestToken);
