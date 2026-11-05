using System.Globalization;
using System.Text;
using Solvelt.Marketplace.Api.Text;

// Compares the managed normaliser of the application (decision M1-81) with the runtime's ICU-backed string.Normalize(), which the application cannot use because it runs
// with InvariantGlobalization. Run it on a machine that has ICU:   dotnet run -c Release --project tools/NormalizationCheck [samples]
// It checks every code point, then a seeded random sample of strings built from starters, combining marks of every class, Hangul jamo and syllables, and composites.
// Exit code 0: no difference. Run it again whenever the Unicode version of the tables or of the runtime changes (tools/unicode/generate_normalization.py).

if (!"é".Normalize(NormalizationForm.FormC).Equals("é"))
{
    Console.Error.WriteLine("string.Normalize() does nothing here: this process runs in invariant globalization mode or without ICU. Install ICU (libicu) and run again.");
    return 2;
}

var samples = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 2_000_000;
var wrong = 0;
void Report(string kind, string input, string managed, string icu)
{
    if (wrong++ < 10) Console.WriteLine($"DIFFERENT {kind}: input [{Show(input)}] managed [{Show(managed)}] ICU [{Show(icu)}]");
}
static string Show(string s) => string.Join(" ", s.EnumerateRunes().Select(r => r.Value.ToString("X4")));

var refused = new List<int>();
void Compare(string s)
{
    string ic, id;
    try { ic = s.Normalize(NormalizationForm.FormC); id = s.Normalize(NormalizationForm.FormD); }
    catch (ArgumentException)
    {
        // the runtime refuses some input outright (noncharacters, lone surrogates); the application's normaliser must simply leave it alone
        if (UnicodeNormalizer.Nfc(s) != s || UnicodeNormalizer.Nfd(s) != s) Report("refused input changed", s, UnicodeNormalizer.Nfc(s), s);
        if (s.EnumerateRunes().Count() == 1) refused.Add(s.EnumerateRunes().First().Value);
        return;
    }
    var c = UnicodeNormalizer.Nfc(s);
    if (c != ic) Report("NFC", s, c, ic);
    var d = UnicodeNormalizer.Nfd(s);
    if (d != id) Report("NFD", s, d, id);
}

var checkedCodePoints = 0;
for (var cp = 0; cp <= 0x10FFFF; cp++)
{
    if (cp is >= 0xD800 and <= 0xDFFF) continue;
    Compare(char.ConvertFromUtf32(cp));
    checkedCodePoints++;
}
Console.WriteLine($"{checkedCodePoints:N0} code points compared ({refused.Count} of them refused by the runtime itself, left unchanged by ours).");

var rng = new Random(20260105);
var starters = new List<int>();
for (var cp = 0x20; cp < 0x2000; cp++) if (cp is < 0xD800 or > 0xDFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber) starters.Add(cp);
starters.AddRange([0x212B, 0x2126, 0x1F00, 0x1F88, 0x958, 0xF40, 0x1100, 0x1161, 0x11A8, 0xAC00, 0xAC01, 0xD7A3, 0x3042, 0x30AB, 0x4E00, 0x2F800, 0x1D15E]);
var marks = new List<int>();
for (var cp = 0x300; cp < 0x1E000; cp++) if (cp is < 0xD800 or > 0xDFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) marks.Add(cp);
var pool = starters.Concat(marks).Concat(starters.Take(60)).Concat([0x20, 0x41, 0x61, 0x1F600, 0x20000, 0x1100, 0x1161, 0x11A8, 0xAC00, 0xC800, 0xD7A3]).ToArray();
for (var i = 0; i < samples; i++)
{
    var sb = new StringBuilder();
    var length = rng.Next(1, 12);
    for (var k = 0; k < length; k++) sb.Append(char.ConvertFromUtf32(pool[rng.Next(pool.Length)]));
    Compare(sb.ToString());
}
Console.WriteLine($"{samples:N0} random strings compared.");
Console.WriteLine(wrong == 0 ? "NO DIFFERENCE" : $"{wrong:N0} DIFFERENCES");
return wrong == 0 ? 0 : 1;
