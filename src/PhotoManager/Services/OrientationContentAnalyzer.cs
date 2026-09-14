using System.Text.RegularExpressions;
using PhotoManager.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PhotoManager.Services;

internal sealed record ContentOrientationProposal(int Orientation, string Reason, int Score);

internal static class OrientationContentAnalyzer
{
    private static readonly Regex CandidateWord = new("[A-Za-z]{4,}", RegexOptions.Compiled);
    private static readonly (BitmapRotation Rotation, int Orientation)[] Candidates =
    [
        (BitmapRotation.None, 1),
        (BitmapRotation.Clockwise90Degrees, 6),
        (BitmapRotation.Clockwise180Degrees, 3),
        (BitmapRotation.Clockwise270Degrees, 8)
    ];

    internal static OcrEngine? TryCreateOcrEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? TryCreateFromTag("en-US")
                ?? TryCreateFromTag("en");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OcrEngine? TryCreateFromTag(string tag)
    {
        try
        {
            var language = new Language(tag);
            return OcrEngine.IsLanguageSupported(language)
                ? OcrEngine.TryCreateFromLanguage(language)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static int ScoreRecognizedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var score = 0;
        foreach (Match match in CandidateWord.Matches(text))
        {
            var word = match.Value;
            if (!IsPlausibleWord(word))
            {
                continue;
            }

            score += word.Length;
        }

        return score;
    }

    internal static string DescribeRotation(int orientation) => orientation switch
    {
        2 => "Flip horizontally so the stored pixels are upright.",
        3 => "Rotate 180 so the stored pixels are upright.",
        4 => "Flip vertically so the stored pixels are upright.",
        5 => "Transpose (mirror and rotate) so the stored pixels are upright.",
        6 => "Rotate 90 clockwise so the stored pixels are upright.",
        7 => "Transverse (mirror and rotate) so the stored pixels are upright.",
        8 => "Rotate 90 counter-clockwise so the stored pixels are upright.",
        _ => "Leave stored pixels unchanged."
    };

    internal static string DescribeRotationShort(int orientation) => orientation switch
    {
        3 => "180° rotation",
        6 => "90° clockwise rotation",
        8 => "90° counter-clockwise rotation",
        _ => $"orientation {orientation} rotation"
    };

    public static async Task EnrichAlreadyUprightItemsAsync(
        IList<OrientationReviewItem> items,
        CancellationToken cancellationToken = default)
    {
        var engine = TryCreateOcrEngine();
        FaceDetector? faces = null;
        try
        {
            faces = await FaceDetector.CreateAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            faces = null;
        }

        if (engine is null && faces is null)
        {
            // Scene scoring still works from decoded pixels.
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(item.Status, "AlreadyUpright", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.DecodeStatus, "renderable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var proposal = await TryProposeAsync(item.Path, engine, faces, cancellationToken);
            if (proposal is null)
            {
                continue;
            }

            item.Status = "Proposed";
            item.ProposedOrientation = proposal.Orientation;
            item.ProposedRotation = DescribeRotation(proposal.Orientation);
            item.Confidence = "Medium";
            item.Reason = proposal.Reason;
        }
    }

    public static async Task<ContentOrientationProposal?> TryProposeAsync(
        string path,
        OcrEngine? engine,
        FaceDetector? faceDetector,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }

        var scored = new List<(int Orientation, int Score, int TextScore, int Faces, SceneScore Scene)>(Candidates.Length);
        foreach (var (rotation, orientation) in Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = await ScoreRotationAsync(bytes, rotation, engine, faceDetector, cancellationToken);
            scored.Add((
                orientation,
                evidence.TextScore + (evidence.Faces * 25),
                evidence.TextScore,
                evidence.Faces,
                evidence.Scene));
        }

        var ocrProposal = TryProposeFromOcr(scored);
        if (ocrProposal is not null)
        {
            return ocrProposal;
        }

        return TryProposeFromScene(scored);
    }

    private static ContentOrientationProposal? TryProposeFromOcr(
        List<(int Orientation, int Score, int TextScore, int Faces, SceneScore Scene)> scored)
    {
        var winner = scored.OrderByDescending(item => item.Score).First();
        var second = scored.OrderByDescending(item => item.Score).Skip(1).First();
        if (winner.Orientation == 1 || winner.Score < 8)
        {
            return null;
        }

        if (second.Score >= 4 && winner.Score < second.Score * 1.5)
        {
            return null;
        }

        var evidence = new List<string>();
        if (winner.TextScore >= 4)
        {
            evidence.Add("readable text");
        }

        if (winner.Faces > 0)
        {
            evidence.Add(winner.Faces == 1 ? "a face" : "faces");
        }

        if (evidence.Count == 0)
        {
            return null;
        }

        return new ContentOrientationProposal(
            winner.Orientation,
            $"EXIF Orientation is Normal, but {string.Join(" and ", evidence)} look upright after a {DescribeRotationShort(winner.Orientation)}.",
            winner.Score);
    }

    private static ContentOrientationProposal? TryProposeFromScene(
        List<(int Orientation, int Score, int TextScore, int Faces, SceneScore Scene)> scored)
    {
        var uprightItem = scored.Single(item => item.Orientation == 1);
        if (uprightItem.TextScore >= 4 || uprightItem.Faces > 0)
        {
            return null;
        }

        var upright = uprightItem.Scene;
        var quarterTurns = scored.Where(item => item.Orientation is 6 or 8).ToArray();

        var skyWinner = quarterTurns.OrderByDescending(item => item.Scene.SkyFraction).First();
        if (skyWinner.Scene.SkyFraction >= 0.18
            && skyWinner.Scene.SkyFraction >= upright.SkyFraction * 2 + 0.08)
        {
            return new ContentOrientationProposal(
                skyWinner.Orientation,
                $"EXIF Orientation is Normal, but sky/bright outdoor looks upright after a {DescribeRotationShort(skyWinner.Orientation)}.",
                (int)Math.Round(skyWinner.Scene.SkyFraction * 100));
        }

        if (upright.DeltaLuma >= 0)
        {
            return null;
        }

        var lumaWinner = quarterTurns.OrderByDescending(item => item.Scene.DeltaLuma).First();
        if (lumaWinner.Scene.DeltaLuma >= 18
            && lumaWinner.Scene.DeltaLuma - upright.DeltaLuma >= 20)
        {
            return new ContentOrientationProposal(
                lumaWinner.Orientation,
                $"EXIF Orientation is Normal, but the scene looks upright after a {DescribeRotationShort(lumaWinner.Orientation)}.",
                (int)Math.Round(lumaWinner.Scene.DeltaLuma));
        }

        return null;
    }

    private static async Task<(int TextScore, int Faces, SceneScore Scene)> ScoreRotationAsync(
        byte[] bytes,
        BitmapRotation rotation,
        OcrEngine? engine,
        FaceDetector? faceDetector,
        CancellationToken cancellationToken)
    {
        try
        {
            using var mem = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(mem))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            mem.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(mem);
            var software = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { Rotation = rotation },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var textScore = 0;
            if (engine is not null)
            {
                var ocr = await engine.RecognizeAsync(software);
                textScore = ScoreRecognizedText(ocr.Text);
            }

            var faces = 0;
            if (faceDetector is not null)
            {
                using var gray = SoftwareBitmap.Convert(software, BitmapPixelFormat.Gray8);
                var detected = await faceDetector.DetectFacesAsync(gray);
                faces = (int)detected.Count;
            }

            var scene = SampleScene(software);
            software.Dispose();
            return (textScore, faces, scene);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (0, 0, default);
        }
    }

    internal readonly record struct SceneScore(double DeltaLuma, double SkyFraction);

    internal static SceneScore SampleScene(SoftwareBitmap software)
    {
        var width = software.PixelWidth;
        var height = software.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return default;
        }

        var length = checked((uint)(4 * width * height));
        var buffer = new Windows.Storage.Streams.Buffer(length);
        software.CopyToBuffer(buffer);
        var pixels = new byte[length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);

        var band = Math.Max(1, height / 5);
        var step = Math.Max(1, Math.Min(width, height) / 160);
        double topLuma = 0, bottomLuma = 0;
        var topCount = 0;
        var bottomCount = 0;
        var topSky = 0;
        for (var y = 0; y < height; y += step)
        {
            var inTop = y < band;
            var inBottom = y >= height - band;
            if (!inTop && !inBottom)
            {
                continue;
            }

            var row = y * width * 4;
            for (var x = 0; x < width; x += step)
            {
                var i = row + x * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var luma = 0.299 * r + 0.587 * g + 0.114 * b;
                if (inTop)
                {
                    topLuma += luma;
                    topCount++;
                    if (IsSkyPixel(r, g, b, luma))
                    {
                        topSky++;
                    }
                }
                else
                {
                    bottomLuma += luma;
                    bottomCount++;
                }
            }
        }

        if (topCount == 0 || bottomCount == 0)
        {
            return default;
        }

        return new SceneScore(topLuma / topCount - bottomLuma / bottomCount, (double)topSky / topCount);
    }

    internal static bool IsSkyPixel(int r, int g, int b, double luma) =>
        luma is > 110 and < 240
        && b > 145
        && b > r + 20
        && b > g + 8;

    private static bool IsPlausibleWord(string word)
    {
        var hasVowel = false;
        var hasConsonant = false;
        var counts = new int[26];
        foreach (var raw in word)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is < 'a' or > 'z')
            {
                continue;
            }

            counts[c - 'a']++;
            if (c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y')
            {
                hasVowel = true;
            }
            else
            {
                hasConsonant = true;
            }
        }

        if (!hasVowel || !hasConsonant)
        {
            return false;
        }

        var mostCommon = counts.Max();
        return mostCommon * 2 <= word.Length;
    }
}
