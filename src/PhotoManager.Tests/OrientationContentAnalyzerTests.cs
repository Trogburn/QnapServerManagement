using System.Drawing;
using System.Drawing.Imaging;
using PhotoManager.Models;
using PhotoManager.Services;
using Xunit;

namespace PhotoManager.Tests;

public sealed class OrientationContentAnalyzerTests
{
    [Fact]
    public void ScoreRecognizedTextCountsRealWordsAndIgnoresOcrGarbage()
    {
        Assert.Equal(0, OrientationContentAnalyzer.ScoreRecognizedText(null));
        Assert.Equal(0, OrientationContentAnalyzer.ScoreRecognizedText("uiilii ••ii_•"));
        Assert.Equal(0, OrientationContentAnalyzer.ScoreRecognizedText("11B"));
        Assert.True(OrientationContentAnalyzer.ScoreRecognizedText("'PLATFORM 93/4") >= 8);
        Assert.True(OrientationContentAnalyzer.ScoreRecognizedText("Village Weaver Animal Facts Conservation") >= 20);
    }

    [Fact]
    public async Task ContentAnalyzerProposesClockwiseRotationForSidewaysText()
    {
        var engine = OrientationContentAnalyzer.TryCreateOcrEngine();
        if (engine is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "orient-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sideways = Path.Combine(root, "sideways-text.jpg");
            var upright = Path.Combine(root, "upright-text.jpg");
            WriteTextJpeg(upright, rotatePixelsClockwise270: false);
            WriteTextJpeg(sideways, rotatePixelsClockwise270: true);

            var items = new List<OrientationReviewItem>
            {
                AlreadyUpright(sideways),
                AlreadyUpright(upright)
            };
            await OrientationContentAnalyzer.EnrichAlreadyUprightItemsAsync(items);

            Assert.Equal("Proposed", items[0].Status);
            Assert.Equal(6, items[0].ProposedOrientation);
            Assert.Equal("Medium", items[0].Confidence);
            Assert.Equal("AlreadyUpright", items[1].Status);
            Assert.Null(items[1].ProposedOrientation);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SkyPixelsMustBeBlueDominantNotWhiteOrGray()
    {
        Assert.False(OrientationContentAnalyzer.IsSkyPixel(255, 255, 255, 255));
        Assert.False(OrientationContentAnalyzer.IsSkyPixel(200, 200, 200, 200));
        Assert.False(OrientationContentAnalyzer.IsSkyPixel(80, 160, 70, 130));
        Assert.True(OrientationContentAnalyzer.IsSkyPixel(135, 206, 235, 188));
    }

    [Fact]
    public async Task SceneAnalyzerDoesNotRotateUprightLabeledChartWithBlueSidePanel()
    {
        if (OrientationContentAnalyzer.TryCreateOcrEngine() is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "orient-chart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "already-upright-chart.jpg");
            WriteUprightChartJpeg(path);
            var items = new List<OrientationReviewItem> { AlreadyUpright(path) };
            await OrientationContentAnalyzer.EnrichAlreadyUprightItemsAsync(items);
            Assert.Equal("AlreadyUpright", items[0].Status);
            Assert.Null(items[0].ProposedOrientation);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SceneAnalyzerProposesClockwiseRotationWhenSkyIsOnTheLeft()
    {
        var root = Path.Combine(Path.GetTempPath(), "orient-sky-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sideways = Path.Combine(root, "sky-left.jpg");
            var upright = Path.Combine(root, "sky-top.jpg");
            WriteSkyJpeg(sideways, skyOnLeft: true);
            WriteSkyJpeg(upright, skyOnLeft: false);

            var items = new List<OrientationReviewItem>
            {
                AlreadyUpright(sideways),
                AlreadyUpright(upright)
            };
            await OrientationContentAnalyzer.EnrichAlreadyUprightItemsAsync(items);

            Assert.Equal("Proposed", items[0].Status);
            Assert.Equal(6, items[0].ProposedOrientation);
            Assert.Equal("AlreadyUpright", items[1].Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RealFinePixSamplesProposeContentRotationWhenPresent()
    {
        const string folder = @"\\TrogQNAP6HDD\PhotoWorkflowTest\RealPhotoSample\Input";
        if (!Directory.Exists(folder))
        {
            return;
        }

        var files = new[]
        {
            "DSCF0178 2.JPG",
            "DSCF0179 2.JPG",
            "DSCF0180 2.JPG",
            "DSCF0180 (2).JPG",
            "DSCF0181 2.JPG",
            "DSCF0181 (3).JPG",
            "DSCF0183 (2).JPG",
            "DSCF0184 (2).JPG",
            "DSCF0185 2.JPG"
        };
        var items = files.Select(name => AlreadyUpright(Path.Combine(folder, name))).ToList();
        await OrientationContentAnalyzer.EnrichAlreadyUprightItemsAsync(items);

        Assert.Equal("Proposed", items[0].Status);
        Assert.Equal(6, items[0].ProposedOrientation);
        Assert.Equal("Proposed", items[1].Status);
        Assert.Equal(6, items[1].ProposedOrientation);
        Assert.Equal("Proposed", items[2].Status);
        Assert.Equal(6, items[2].ProposedOrientation);
        Assert.Equal("AlreadyUpright", items[3].Status);
        Assert.Equal("Proposed", items[4].Status);
        Assert.Equal(6, items[4].ProposedOrientation);
        Assert.Equal("AlreadyUpright", items[5].Status);
        Assert.Equal("AlreadyUpright", items[6].Status);
        Assert.Equal("AlreadyUpright", items[7].Status);
        Assert.Equal("Proposed", items[8].Status);
        Assert.Equal(6, items[8].ProposedOrientation);
    }

    [Fact]
    public async Task LabDummyAlreadyUprightChartsStayUpright()
    {
        const string folder = @"\\TrogQNAP6HDD\PhotoWorkflowTest\WpfAcceptance\Input";
        if (!Directory.Exists(folder))
        {
            return;
        }

        var files = new[]
        {
            "Rotate-AlreadyUpright-Normal.jpg",
            "Rotate-AlreadyUpright-NoTag.jpg"
        };
        var items = files.Select(name => AlreadyUpright(Path.Combine(folder, name))).ToList();
        await OrientationContentAnalyzer.EnrichAlreadyUprightItemsAsync(items);

        Assert.Equal("AlreadyUpright", items[0].Status);
        Assert.Null(items[0].ProposedOrientation);
        Assert.Equal("AlreadyUpright", items[1].Status);
        Assert.Null(items[1].ProposedOrientation);
    }

    private static OrientationReviewItem AlreadyUpright(string path) =>
        new()
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Status = "AlreadyUpright",
            Orientation = 1,
            OrientationLabel = "Normal",
            DecodeStatus = "renderable",
            Confidence = "None",
            Reason = "EXIF Orientation is Normal; stored pixels are already upright."
        };

    private static void WriteTextJpeg(string path, bool rotatePixelsClockwise270)
    {
        using var bitmap = new Bitmap(640, 240);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var font = new Font("Arial", 48, FontStyle.Bold);
            graphics.DrawString("PLATFORM NINE", font, Brushes.Black, 20, 80);
        }

        if (rotatePixelsClockwise270)
        {
            bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
        }

        bitmap.Save(path, ImageFormat.Jpeg);
    }

    private static void WriteSkyJpeg(string path, bool skyOnLeft)
    {
        using var bitmap = new Bitmap(320, 240);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var sky = Color.FromArgb(135, 206, 235);
            var ground = Color.FromArgb(80, 120, 50);
            if (skyOnLeft)
            {
                graphics.Clear(ground);
                graphics.FillRectangle(new SolidBrush(sky), 0, 0, 110, 240);
            }
            else
            {
                graphics.Clear(ground);
                graphics.FillRectangle(new SolidBrush(sky), 0, 0, 320, 90);
            }
        }

        bitmap.Save(path, ImageFormat.Jpeg);
    }

    private static void WriteUprightChartJpeg(string path)
    {
        using var bitmap = new Bitmap(480, 360);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.ForestGreen, 80, 0, 320, 70);
            graphics.FillRectangle(new SolidBrush(Color.FromArgb(70, 160, 230)), 400, 0, 80, 360);
            graphics.FillRectangle(new SolidBrush(Color.FromArgb(160, 50, 50)), 0, 0, 80, 360);
            graphics.FillRectangle(new SolidBrush(Color.FromArgb(230, 140, 40)), 80, 290, 320, 70);
            using var font = new Font("Arial", 28, FontStyle.Bold);
            graphics.DrawString("UP", font, Brushes.White, 200, 18);
            graphics.DrawString("LEFT", font, Brushes.White, 4, 160);
            graphics.DrawString("RIGHT", font, Brushes.White, 402, 160);
            graphics.DrawString("DOWN", font, Brushes.White, 180, 310);
            using var caption = new Font("Arial", 16, FontStyle.Bold);
            graphics.DrawString("Already upright", caption, Brushes.DimGray, 160, 180);
        }

        bitmap.Save(path, ImageFormat.Jpeg);
    }
}
