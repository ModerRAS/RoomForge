namespace AudioOptimizer.IO;

using System.Globalization;
using AudioOptimizer.Core;

/// <summary>
/// Why a calibration file could not be turned into a payload. <see cref="LineNumber"/> is 1-based;
/// <c>0</c> means the problem is not about one line (no rows at all, no identity, no absolute sensitivity).
/// <see cref="Line"/> carries the offending text, so a message can quote it instead of paraphrasing it.
/// </summary>
public sealed record CalibrationParseError(int LineNumber, string Line, string Reason);

/// <summary>
/// The outcome of a parse: either a payload or a structured error naming the line. Never an exception from inside
/// the row loop — the caller has to show the user which line is wrong, and a caller that has to catch an
/// exception to find that out will eventually catch too much.
/// </summary>
public sealed record CalibrationParseResult(MicrophoneCalibration? Calibration, CalibrationParseError? Error)
{
    public bool Succeeded => Calibration is not null;

    public string Message => Error is null
        ? $"Parsed '{Calibration!.Identity}' with {Calibration.Points.Count} calibration point(s)."
        : Error.LineNumber > 0 ? $"Line {Error.LineNumber}: {Error.Reason}" : Error.Reason;
}

/// <summary>
/// Reads a miniDSP/UMIK-1 style calibration file: whitespace- or tab-separated <c>frequency dB</c> rows, with
/// optional blank lines, <c>*</c>/<c>#</c> comment lines, quoted metadata lines and an optional
/// <c>Sensitivity =</c> line. Row order is not trusted — the payload sorts. What the format allows is tolerated;
/// what it does not is refused <b>with the line that says so</b>, never skipped.
/// <para>
/// The absolute reference is deliberately not taken from a <c>Sens Factor</c> line: a real UMIK-1 file's value is
/// a hundredth of a dB, which is a shape factor, not an SPL-per-full-scale reference. Treating it as one would
/// turn "SPL" into a claim the file does not support, so such a file needs the sensitivity supplied explicitly.
/// </para>
/// </summary>
public static class CalibrationFileParser
{
    /// <summary>
    /// Parses <paramref name="text"/>. <paramref name="sensitivityDbSplPerFullScale"/> wins over a sensitivity
    /// line in the file when both are present; without either, the result is an error rather than a payload,
    /// because a payload without an absolute reference is exactly the "SPL" this type exists to prevent.
    /// </summary>
    /// <param name="correctionColumn">
    /// 0-based column holding the correction, defaulting to the second column. A miniDSP 3-column file is
    /// <c>frequency 0-degree 90-degree</c>; the default takes the 0-degree column, matching REW's default.
    /// </param>
    public static CalibrationParseResult Parse(
        string text,
        double? sensitivityDbSplPerFullScale = null,
        string? identity = null,
        int correctionColumn = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (correctionColumn < 1)
            throw new ArgumentOutOfRangeException(nameof(correctionColumn), correctionColumn, "The correction column is 0-based and cannot be the frequency column (0).");

        var rows = new List<(int Line, CalibrationPoint Point)>();
        double? fileSensitivity = null;
        string serial = "";

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('*')) continue;

            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!TryParseNumber(tokens[0], out double frequency))
            {
                // Metadata. Recognised by name rather than by position, because the headers real files carry
                // ("Auto-generated 90-degree calibration file", a quoted Sens Factor/SERNO line) have no fixed
                // order and are quoted in some writers and bare in others.
                bool metadata = false;
                if (line.Contains("sensitivity", StringComparison.OrdinalIgnoreCase) && TryFirstNumber(line, out double sensitivity))
                {
                    fileSensitivity = sensitivity;
                    metadata = true;
                }

                if (TrySerial(line, out string found))
                {
                    serial = found;
                    metadata = true;
                }

                if (line.Contains("sens factor", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("calibration file", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith('"'))
                {
                    metadata = true;
                }

                if (!metadata)
                    return Fail(lineNumber, line, "expected a '<frequency> <dB>' row or a recognised header line");

                continue;
            }

            if (!double.IsFinite(frequency) || frequency <= 0.0)
                return Fail(lineNumber, line, $"the frequency must be greater than 0 Hz, not '{tokens[0]}'");
            if (tokens.Length <= correctionColumn || !TryParseNumber(tokens[correctionColumn], out double correction))
                return Fail(lineNumber, line, $"a data row needs a correction in column {correctionColumn}");

            rows.Add((lineNumber, new CalibrationPoint(frequency, correction)));
        }

        if (rows.Count == 0)
            return Fail(0, "", "the file contains no '<frequency> <dB>' rows, so there is nothing to correct with");

        // Sorted here only so a duplicate can be reported against the line that repeats it; the payload sorts too
        // and is the single authority on ordering.
        rows.Sort((left, right) => left.Point.FrequencyHz.CompareTo(right.Point.FrequencyHz));
        for (int i = 1; i < rows.Count; i++)
            if (rows[i].Point.FrequencyHz == rows[i - 1].Point.FrequencyHz)
                return Fail(rows[i].Line, lines[rows[i].Line - 1].Trim(), $"two rows give a correction for {rows[i].Point.FrequencyHz} Hz, so the correction there is ambiguous");

        string name = string.IsNullOrWhiteSpace(identity) ? serial : identity;
        if (string.IsNullOrWhiteSpace(name))
            return Fail(0, "", "the file names no microphone (no 'SERNO:') and no identity was supplied");

        double? effectiveSensitivity = sensitivityDbSplPerFullScale ?? fileSensitivity;
        if (effectiveSensitivity is not { } sensitivityValue)
            return Fail(0, "", "no absolute sensitivity: the file has no 'Sensitivity =' line (a 'Sens Factor' line is a shape factor, not an SPL reference) and none was supplied");

        return new CalibrationParseResult(
            new MicrophoneCalibration(name, sensitivityValue, [.. rows.Select(row => row.Point)]),
            null);
    }

    private static CalibrationParseResult Fail(int lineNumber, string line, string reason)
        => new(null, new CalibrationParseError(lineNumber, line, reason));

    private static bool TryParseNumber(string token, out double value)
        => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>The first numeric run in a line, so <c>"Sensitivity = -30.5 dBFS"</c> yields −30.5 and not the label's text.</summary>
    private static bool TryFirstNumber(string text, out double value)
    {
        value = 0.0;
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsAsciiDigit(text[i]) || text[i] is '-' or '+' or '.')
            {
                int start = i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '-' or '+')) i++;

                string candidate = text[start..i].TrimEnd('.');
                if (candidate.Length > 0 && TryParseNumber(candidate, out value)) return true;
                continue;
            }

            i++;
        }

        return false;
    }

    /// <summary>The digits after a <c>SERNO:</c> token — the file's own attribution of the microphone.</summary>
    private static bool TrySerial(string line, out string serial)
    {
        serial = "";
        int at = line.IndexOf("SERNO", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        int i = at + 5;
        while (i < line.Length && !char.IsAsciiDigit(line[i])) i++;
        int start = i;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == start) return false;

        serial = line[start..i];
        return true;
    }
}
