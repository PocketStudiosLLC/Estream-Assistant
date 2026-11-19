namespace EstreamAssistant.Core.Upc;

/// <summary>Why a particular lookup candidate was tried.</summary>
public enum UpcCandidateKind
{
    Exact,
    WithoutLastDigit,
    WithCheckDigitAppended,
}

public sealed record UpcCandidate(long Upc, UpcCandidateKind Kind)
{
    /// <summary>Plain-language note shown on the item card when a fallback matched.</summary>
    public string? Note => Kind switch
    {
        UpcCandidateKind.WithoutLastDigit =>
            "Matched without the last digit (eStream has this code stored without its check digit).",
        UpcCandidateKind.WithCheckDigitAppended =>
            "Matched after adding the check digit (the scanned code was missing it).",
        _ => null,
    };
}

/// <summary>
/// UPC normalization and check-digit fallbacks (Plan.txt sec. 11).
/// Inventory.BarCode is a float, so leading zeros are lost; ~2% of stored codes
/// have no check digit. Lookup order: exact, without last digit, with check digit appended.
/// TODO(store PC): compare edge cases against Get-EsItem.ps1, which is the reference.
/// </summary>
public static class UpcNormalizer
{
    public const int MaxDigits = 18; // decimal(18,0) in the reference query

    /// <summary>Strips non-digits and leading zeros. Returns "" when nothing is left.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (var c in raw)
            if (c is >= '0' and <= '9') buf[n++] = c;
        var digits = new string(buf[..n]).TrimStart('0');
        return digits;
    }

    /// <summary>True when the text is digits only (ignoring spaces and dashes).</summary>
    public static bool LooksLikeUpc(string? raw, int minDigits = 1)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        int digits = 0;
        foreach (var c in raw.Trim())
        {
            if (c is >= '0' and <= '9') digits++;
            else if (c is not (' ' or '-')) return false;
        }
        return digits >= minDigits;
    }

    /// <summary>
    /// UPC/EAN mod-10 check digit for a payload (the code WITHOUT its check digit).
    /// From the right, weights 3,1,3,1...
    /// </summary>
    public static int ComputeCheckDigit(string payload)
    {
        if (payload.Length == 0) throw new ArgumentException("Payload is empty.", nameof(payload));
        int sum = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            char c = payload[payload.Length - 1 - i];
            if (c is < '0' or > '9') throw new ArgumentException("Payload must be digits only.", nameof(payload));
            sum += (c - '0') * (i % 2 == 0 ? 3 : 1);
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>True when the last digit is a valid check digit for the rest.</summary>
    public static bool HasValidCheckDigit(string digits)
    {
        if (digits.Length < 2) return false;
        return ComputeCheckDigit(digits[..^1]) == digits[^1] - '0';
    }

    /// <summary>
    /// Candidates to try, in order: exact, without last digit, with check digit appended.
    /// Duplicates and values that do not fit are skipped.
    /// </summary>
    public static IReadOnlyList<UpcCandidate> Candidates(string? raw)
    {
        var digits = Normalize(raw);
        var result = new List<UpcCandidate>(3);
        if (digits.Length == 0) return result;

        void Add(string d, UpcCandidateKind kind)
        {
            d = d.TrimStart('0');
            if (d.Length == 0 || d.Length > MaxDigits) return;
            var value = long.Parse(d);
            if (result.Exists(r => r.Upc == value)) return;
            result.Add(new UpcCandidate(value, kind));
        }

        Add(digits, UpcCandidateKind.Exact);
        if (digits.Length > 1) Add(digits[..^1], UpcCandidateKind.WithoutLastDigit);
        Add(digits + ComputeCheckDigit(digits), UpcCandidateKind.WithCheckDigitAppended);
        return result;
    }
}
