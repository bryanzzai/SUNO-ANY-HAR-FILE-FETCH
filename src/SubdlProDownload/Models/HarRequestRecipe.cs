using System.Text;

namespace SunoHarFileDownload.Models;

/// <summary>
/// A replayable HTTP request captured in a HAR. Volatile browser-only headers are
/// deliberately rebuilt when the request is sent.
/// </summary>
public sealed record HarRequestRecipe(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);
