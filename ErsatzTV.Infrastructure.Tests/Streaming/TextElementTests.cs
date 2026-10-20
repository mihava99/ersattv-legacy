using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Streaming.Graphics;
using LanguageExt;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Tests.Streaming;

[TestFixture]
public class TextElementTests
{
    // TextElement re-reads text_file at most once per second
    private static readonly TimeSpan FileCheckWait = TimeSpan.FromMilliseconds(1100);

    private string _folder = null!;
    private string _textFile = null!;

    [SetUp]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"etv-text-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
        _textFile = Path.Combine(_folder, "time.txt");
    }

    [TearDown]
    public void RemoveFolder() => Directory.Delete(_folder, true);

    [Test]
    public async Task Text_file_changes_should_update_the_image()
    {
        File.WriteAllText(_textFile, "12:00");
        using TextElement element = await CreateElement();

        int firstWidth = (await Prepare(element)).Width;
        firstWidth.ShouldBeGreaterThan(0);

        File.WriteAllText(_textFile, "12:00 12:00 12:00");
        await Task.Delay(FileCheckWait);

        (await Prepare(element)).Width.ShouldBeGreaterThan(firstWidth);
    }

    [TestCase("")]
    [TestCase("   \r\n")]
    public async Task Empty_text_file_should_hide_the_text_and_recover(string emptyContent)
    {
        File.WriteAllText(_textFile, "12:00");
        using TextElement element = await CreateElement();
        await Prepare(element);

        // an external writer truncates the file before writing the new time
        File.WriteAllText(_textFile, emptyContent);
        await Task.Delay(FileCheckWait);

        (await TryPrepare(element)).IsNone.ShouldBeTrue();

        // the writer finishes
        File.WriteAllText(_textFile, "12:01");
        await Task.Delay(FileCheckWait);

        SKBitmap after = await Prepare(element);
        after.Handle.ShouldNotBe(IntPtr.Zero);
        after.Width.ShouldBeGreaterThan(0);
        DrawWithoutThrowing(after);
    }

    [Test]
    public async Task Missing_text_file_should_keep_the_last_image_usable()
    {
        File.WriteAllText(_textFile, "12:00");
        using TextElement element = await CreateElement();
        await Prepare(element);

        File.Delete(_textFile);
        await Task.Delay(FileCheckWait);

        SKBitmap bitmap = await Prepare(element);
        bitmap.Handle.ShouldNotBe(IntPtr.Zero);
        DrawWithoutThrowing(bitmap);
    }

    private async Task<TextElement> CreateElement()
    {
        var fonts = new GraphicsEngineFonts(
            new CustomFontMapper(NullLogger<CustomFontMapper>.Instance),
            NullLogger<GraphicsEngineFonts>.Instance);

        var element = new TextElement(
            fonts,
            new TextGraphicsElement
            {
                Text = "00:00",
                TextFile = _textFile,
                BaseStyle = "base",
                Styles = [new StyleDefinition { Name = "base", FontFamily = "Arial", FontSize = 40, TextColor = "#FFFFFF" }]
            },
            NullLogger.Instance);

        var size = new Resolution { Width = 1280, Height = 720 };
        await element.InitializeAsync(
            new GraphicsEngineContext(
                "1",
                new Movie(),
                [],
                [],
                size,
                size,
                new FrameRate("30"),
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(10)),
            CancellationToken.None);

        element.IsFinished.ShouldBeFalse();
        return element;
    }

    private static async Task<SKBitmap> Prepare(TextElement element)
    {
        Option<PreparedElementImage> result = await TryPrepare(element);

        result.IsSome.ShouldBeTrue();
        return result.Match(i => i.Image, () => throw new InvalidOperationException());
    }

    private static async Task<Option<PreparedElementImage>> TryPrepare(TextElement element) =>
        await element.PrepareImage(
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            CancellationToken.None);

    // the graphics engine draws every prepared image the same way; an exception there stops all graphics
    private static void DrawWithoutThrowing(SKBitmap bitmap)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var paint = new SKPaint();
        Should.NotThrow(() => surface.Canvas.DrawBitmap(bitmap, SKPointI.Empty, SKSamplingOptions.Default, paint));
    }
}
