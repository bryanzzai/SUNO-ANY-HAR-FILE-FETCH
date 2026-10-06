using System.IO;
using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

public static class RomanNaming
{
    public static IReadOnlyDictionary<HarEntryRow, string> BuildFinalNames(IReadOnlyList<HarEntryRow> selectedRows)
    {
        var result = new Dictionary<HarEntryRow, string>();

        foreach (var group in selectedRows
                     .OrderBy(row => row.EntryIndex)
                     .GroupBy(row => row.OutputFileName, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(row => row.EntryIndex).ToArray();

            if (ordered.Length == 1)
            {
                result[ordered[0]] = MakeWindowsSafe(ordered[0].OutputFileName);
                continue;
            }

            for (var index = 0; index < ordered.Length; index++)
            {
                var raw = ordered[index].OutputFileName;
                var extension = Path.GetExtension(raw);
                var stem = extension.Length == 0 ? raw : raw[..^extension.Length];
                var romanized = $"{stem} [ {ToRoman(index + 1)} ]{extension}";
                result[ordered[index]] = MakeWindowsSafe(romanized);
            }
        }

        var collision = result
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Two different raw names collapse to the same Windows filename after sanitation: {collision.Key}");
        }

        return result;
    }

    private static string MakeWindowsSafe(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();

        var safe = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "unnamed-media" : safe;
    }

    private static string ToRoman(int value)
    {
        if (value is < 1 or > 3999)
            throw new ArgumentOutOfRangeException(nameof(value), "Roman numbering supports 1..3999.");

        (int Value, string Symbol)[] map =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];

        var remaining = value;
        var result = new System.Text.StringBuilder();

        foreach (var item in map)
        {
            while (remaining >= item.Value)
            {
                result.Append(item.Symbol);
                remaining -= item.Value;
            }
        }

        return result.ToString();
    }
}
