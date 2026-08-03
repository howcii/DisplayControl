using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DisplayControl.Server;

namespace DisplayControl.Server.Services;

public sealed class CaptchaStore
{
    private const int CodeLength = 5;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly string[] TextColors =
    [
        "#6aa84f",
        "#b23a48",
        "#334155",
        "#2563eb",
        "#7c3aed"
    ];

    private static readonly string[] LineColors =
    [
        "#94a3b8",
        "#64748b",
        "#3b82f6",
        "#a855f7",
        "#b45309"
    ];

    private readonly ConcurrentDictionary<string, CaptchaEntry> _entries = new(StringComparer.Ordinal);

    public CaptchaResponse Create()
    {
        Cleanup();
        var id = Guid.NewGuid().ToString("N");
        var code = CreateCode();
        _entries[id] = new CaptchaEntry(code, DateTimeOffset.UtcNow.AddMinutes(5));
        var svg = BuildSvg(code);
        var image = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return new CaptchaResponse(id, image, 300);
    }

    public bool Consume(string? id, string? code)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return _entries.TryRemove(id, out var entry) &&
            entry.ExpiresAtUtc >= DateTimeOffset.UtcNow &&
            string.Equals(entry.Code, NormalizeCode(code), StringComparison.Ordinal);
    }

    private static string CreateCode()
    {
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    private static string NormalizeCode(string code) =>
        code.Trim().Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string BuildSvg(string code)
    {
        var backgroundNoise = new StringBuilder();
        for (var i = 0; i < 16; i++)
        {
            var x = RandomNumberGenerator.GetInt32(8, 156);
            var y = RandomNumberGenerator.GetInt32(7, 48);
            var radius = RandomNumberGenerator.GetInt32(7, 14) / 10.0;
            var opacity = RandomNumberGenerator.GetInt32(9, 19) / 100.0;
            var color = i % 2 == 0 ? "#93c5fd" : "#94a3b8";
            backgroundNoise.AppendLine(FormattableString.Invariant(
                $"""<circle cx="{x}" cy="{y}" r="{radius}" fill="{color}" opacity="{Invariant(opacity)}"/>"""));
        }

        var curves = new StringBuilder();
        for (var i = 0; i < 2; i++)
        {
            var y1 = RandomNumberGenerator.GetInt32(16, 41);
            var c1x = RandomNumberGenerator.GetInt32(30, 56);
            var c1y = RandomNumberGenerator.GetInt32(8, 48);
            var c2x = RandomNumberGenerator.GetInt32(92, 124);
            var c2y = RandomNumberGenerator.GetInt32(8, 48);
            var y2 = RandomNumberGenerator.GetInt32(14, 42);
            var width = RandomNumberGenerator.GetInt32(12, 20) / 10.0;
            var opacity = RandomNumberGenerator.GetInt32(25, 42) / 100.0;
            var color = LineColors[RandomNumberGenerator.GetInt32(LineColors.Length)];
            curves.AppendLine(FormattableString.Invariant(
                $"""<path d="M4 {y1} C{c1x} {c1y}, {c2x} {c2y}, 160 {y2}" fill="none" stroke="{color}" stroke-width="{Invariant(width)}" stroke-linecap="round" opacity="{Invariant(opacity)}"/>"""));
        }

        var hairlines = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var x = RandomNumberGenerator.GetInt32(24, 145);
            var top = RandomNumberGenerator.GetInt32(5, 18);
            var bottom = RandomNumberGenerator.GetInt32(35, 50);
            var sway = RandomNumberGenerator.GetInt32(-10, 11);
            var opacity = RandomNumberGenerator.GetInt32(12, 23) / 100.0;
            hairlines.AppendLine(FormattableString.Invariant(
                $"""<path d="M{x} {top} C{x + sway} 23, {x - sway} 31, {x + RandomNumberGenerator.GetInt32(-4, 5)} {bottom}" fill="none" stroke="#64748b" stroke-width=".8" opacity="{Invariant(opacity)}"/>"""));
        }

        var letters = new StringBuilder();
        for (var i = 0; i < code.Length; i++)
        {
            var x = 17 + i * 30 + RandomNumberGenerator.GetInt32(-3, 4);
            var y = 37 + RandomNumberGenerator.GetInt32(-4, 5);
            var rotate = RandomNumberGenerator.GetInt32(-18, 19);
            var skew = RandomNumberGenerator.GetInt32(-10, 11);
            var scaleX = RandomNumberGenerator.GetInt32(86, 117) / 100.0;
            var scaleY = RandomNumberGenerator.GetInt32(91, 111) / 100.0;
            var size = RandomNumberGenerator.GetInt32(31, 38);
            var fill = TextColors[RandomNumberGenerator.GetInt32(TextColors.Length)];
            var displayChar = RandomNumberGenerator.GetInt32(2) == 0
                ? char.ToLowerInvariant(code[i])
                : code[i];
            var fontFamily = (i % 3) switch
            {
                0 => "Georgia, Times New Roman, SimSun, serif",
                1 => "Trebuchet MS, Segoe UI, Microsoft YaHei UI, sans-serif",
                _ => "Arial Black, Microsoft YaHei UI, sans-serif"
            };
            letters.AppendLine(FormattableString.Invariant($"""
                <g transform="translate({x} {y}) rotate({rotate}) skewX({skew}) scale({Invariant(scaleX)} {Invariant(scaleY)})">
                  <text x="0" y="0" font-family="{fontFamily}" font-size="{size}" font-weight="800" stroke="#ffffff" stroke-width=".55" paint-order="stroke fill" fill="{fill}">{displayChar}</text>
                </g>
                """));
        }

        var warpSeed = RandomNumberGenerator.GetInt32(1, 10_000);
        var warpFrequencyX = RandomNumberGenerator.GetInt32(10, 17) / 1000.0;
        var warpFrequencyY = RandomNumberGenerator.GetInt32(65, 86) / 1000.0;
        var warpScale = RandomNumberGenerator.GetInt32(22, 31) / 10.0;
        var topOffset = RandomNumberGenerator.GetInt32(-3, 0);
        var middleOffset = RandomNumberGenerator.GetInt32(2, 5);
        var bottomOffset = RandomNumberGenerator.GetInt32(-2, 2);

        return FormattableString.Invariant($"""
            <svg xmlns="http://www.w3.org/2000/svg" width="164" height="54" viewBox="0 0 164 54" role="img" aria-label="图形验证码">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="#ffffff"/>
                  <stop offset="52%" stop-color="#f8fbff"/>
                  <stop offset="100%" stop-color="#eaf2ff"/>
                </linearGradient>
                <filter id="captchaWarp" x="-12%" y="-20%" width="124%" height="140%">
                  <feTurbulence type="fractalNoise" baseFrequency="{Invariant(warpFrequencyX)} {Invariant(warpFrequencyY)}" numOctaves="1" seed="{warpSeed}" result="noise"/>
                  <feDisplacementMap in="SourceGraphic" in2="noise" scale="{Invariant(warpScale)}" xChannelSelector="R" yChannelSelector="G"/>
                </filter>
                <clipPath id="captchaTop"><rect x="0" y="0" width="164" height="18.5"/></clipPath>
                <clipPath id="captchaMiddle"><rect x="0" y="20" width="164" height="16"/></clipPath>
                <clipPath id="captchaBottom"><rect x="0" y="37.5" width="164" height="16.5"/></clipPath>
              </defs>
              <rect x="1" y="1" width="162" height="52" rx="11" fill="url(#bg)" stroke="#bfdbfe" stroke-width="1"/>
              {backgroundNoise}
              {hairlines}
              <g filter="url(#captchaWarp)">
                <g clip-path="url(#captchaTop)" transform="translate({topOffset} 0)">{letters}</g>
                <g clip-path="url(#captchaMiddle)" transform="translate({middleOffset} 0)">{letters}</g>
                <g clip-path="url(#captchaBottom)" transform="translate({bottomOffset} 0)">{letters}</g>
              </g>
              <path d="M7 19.3 H157 M7 36.8 H157" fill="none" stroke="#f8fbff" stroke-width="1.7" opacity=".95"/>
              <path d="M7 19.3 H157 M7 36.8 H157" fill="none" stroke="#8fb4df" stroke-width=".55" stroke-dasharray="9 7" opacity=".5"/>
              {curves}
            </svg>
            """);
    }

    private static string Invariant(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAtUtc < now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record CaptchaEntry(string Code, DateTimeOffset ExpiresAtUtc);
}
