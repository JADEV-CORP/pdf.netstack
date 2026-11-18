using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// The proof of a lossy image change bound to the check the optimizer made when it wrote the
/// image, and the pre-screen that keeps images that cannot gain from being decoded at all.
/// </summary>
[TestClass]
public sealed class ImageProofTests
{
    private const long Limit = 64 << 20;

    private static byte[] Deflate(byte[] data) => StreamCodec.Deflate(data, CompressionLevel.Optimal);

    private static string Raw(Raster r) =>
        $"/Type/XObject/Subtype/Image/Width {r.Width}/Height {r.Height}/ColorSpace{SyntheticImages.ColorSpace(r.Channels)}/BitsPerComponent 8/Filter/FlateDecode";

    /// <summary>One page drawing one image, object 10, at the given size in points.</summary>
    private static byte[] Page(string dictionary, byte[] data, double width, double height)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, dictionary, data);
        b.SetStream(4, "", System.Text.Encoding.ASCII.GetBytes(Compat.Format("q {0} 0 0 {1} 0 0 cm /Im0 Do Q", width, height)));
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R>>>>/Contents 4 0 R>>");
        return b.Build();
    }

    /// <summary>What the recompressor records for a file, run as the optimizer runs it.</summary>
    private static RecompressionCounts Recompress(byte[] pdf, JpegSettings? jpeg, ImageSettings? images)
    {
        PdfFile file = PdfFile.Open(pdf, Limit);
        ObjectGraph graph = ObjectGraph.Collect(file, false, false, CancellationToken.None);
        return StreamRecompressor.Run(file, graph, RecompressionScope.All with { Prove = true }, CompressionLevel.Optimal, false, true, true, true, jpeg, images, Limit, 1, CancellationToken.None);
    }

    private static string? Verify(byte[] source, byte[] output, ImageAllowance allowance) =>
        EquivalenceVerifier.Verify(PdfFile.Open(source, Limit), PdfFile.Open(output, Limit), new VerificationRules { Images = allowance }, Limit, 1, CancellationToken.None, out _);

    [TestMethod]
    public void ResampledImageIsProvenByTheCheckMadeWhenItWasWritten()
    {
        // 1200 by 900 pixels on 2 by 1.5 inches: 600 dpi, reduced to 150.
        Raster photo = SyntheticImages.Photo(1200, 900, 3);
        byte[] source = Page(Raw(photo), Deflate(photo.Samples), 144, 108);
        RecompressionCounts counts = Recompress(source, null, new ImageSettings(150, false, null, 0.9, false));
        CollectionAssert.AreEqual(new[] { 10 }, counts.ResampledObjects.ToArray());
        Assert.HasCount(1, counts.ImageProofs);

        // The key binds the images as the proof decodes them, not how their samples are compressed.
        Raster reduced = Resampler.Downsample(photo, 300, 225);
        byte[] output = Page(Raw(reduced), Deflate(reduced.Samples), 144, 108);
        var allowance = new ImageAllowance(static _ => false, static n => n == 10, 0.9) { Proofs = counts.ImageProofs };
        Assert.IsNull(Verify(source, output, allowance));

        // One sample off: the full check would let it through, the recorded check does not cover it.
        byte[] changed = (byte[])reduced.Samples.Clone();
        changed[1000]++;
        byte[] tampered = Page(Raw(reduced), Deflate(changed), 144, 108);
        Assert.IsNull(Verify(source, tampered, allowance with { Proofs = null }));
        StringAssert.Contains(Verify(source, tampered, allowance), "matches no check made when it was written");

        // Nor does a check made under another gate, or no recorded check at all.
        StringAssert.Contains(Verify(source, output, allowance with { MinSsim = 0.95 }), "matches no check");
        StringAssert.Contains(Verify(source, output, allowance with { Proofs = [] }), "matches no check");

        // What no lossy change could be is refused before any key is looked at.
        Raster wider = Resampler.Downsample(photo, 400, 225);
        StringAssert.Contains(Verify(source, Page(Raw(wider), Deflate(wider.Samples), 144, 108), allowance), "aspect ratio");
    }

    [TestMethod]
    public void RequantizedJpegIsProvenByTheCheckMadeWhenItWasWritten()
    {
        byte[] jpeg = JpegCodecTests.Fixture("photo-q95.jpg");
        string dictionary = "/Type/XObject/Subtype/Image/Width 320/Height 240/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode";
        byte[] source = Page(dictionary, jpeg, 320, 240);
        RecompressionCounts counts = Recompress(source, new JpegSettings(60, false, 0.5), null);
        Assert.AreEqual(1, counts.JpegRequantized);
        Assert.HasCount(1, counts.ImageProofs);

        byte[] written = JpegTranscoder.TryTranscode(jpeg, 60, null, 3, Limit, out bool requantized)!;
        Assert.IsTrue(requantized);
        var allowance = new ImageAllowance(static n => n == 10, static _ => false, 0.5) { Proofs = counts.ImageProofs };
        Assert.IsNull(Verify(source, Page(dictionary, written, 320, 240), allowance));

        // Requantized as exactly, at another quality: the coefficients pass and so would the gate,
        // but it is not what was checked.
        byte[] other = JpegTranscoder.TryTranscode(jpeg, 55, null, 3, Limit, out _)!;
        Assert.IsNull(Verify(source, Page(dictionary, other, 320, 240), allowance with { Proofs = null }));
        StringAssert.Contains(Verify(source, Page(dictionary, other, 320, 240), allowance), "matches no check");
    }

    [TestMethod]
    public void MinJpegBytesIsAFloor()
    {
        foreach (int channels in new[] { 1, 3, 4 })
        {
            foreach ((int width, int height) in new[] { (8, 8), (17, 9), (640, 480), (333, 1001) })
            {
                var flat = new Raster(width, height, channels, Enumerable.Repeat((byte)200, width * height * channels).ToArray());
                foreach (Raster image in new[] { flat, SyntheticImages.Photo(width, height, channels) })
                {
                    foreach (int quality in new[] { 5, 50, 75, 95 })
                    {
                        bool subsample = channels == 3 && quality < 90;
                        using JpegImage coded = JpegEncoder.Encode(image, quality, subsample);
                        foreach (bool progressive in new[] { false, true })
                        {
                            byte[] data = JpegWriter.TryWriteSmallest(coded, 1024, progressive)!;
                            Assert.IsGreaterThanOrEqualTo(LossyImages.MinJpegBytes(width, height, channels, subsample), data.Length, $"{channels} channels, {width} by {height}, quality {quality}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>The lossy plan for the first image of a page, under the given settings.</summary>
    private static ImagePlan? Plan(byte[] pdf, ImageSettings settings, PlacedSize? placed = null)
    {
        PdfFile file = PdfFile.Open(pdf, Limit);
        var stream = (PdfStream)file.GetObject(10);
        List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, file.Resolve);
        return LossyImages.PlanFor(file, stream.Dictionary, filters, settings, true, placed, stream.EncodedLength + 20, _ => null);
    }

    [TestMethod]
    public void ImagesThatCannotGainAreNeverDecoded()
    {
        // The data is not a JPEG 2000 image at all: a plan would decode it, so none is made where
        // the stored size is already under the fewest bytes a JPEG of that size takes.
        var balanced = new ImageSettings(null, true, 75, 0.95, true) { Quantize = true };
        string jpx = "/Type/XObject/Subtype/Image/Width 2000/Height 2000/ColorSpace/DeviceRGB/Filter/JPXDecode";
        long floor = LossyImages.MinJpegBytes(2000, 2000, 3, subsampled: true);
        Assert.IsNull(Plan(Page(jpx, new byte[(int)(floor * 11 / 10)], 612, 612), balanced));
        Assert.IsNotNull(Plan(Page(jpx, new byte[(int)(floor * 12 / 10)], 612, 612), balanced), "a JPEG might still be 10% smaller");

        // At quality 90 and above chroma keeps its resolution, and the floor rises with it.
        Assert.IsNull(Plan(Page(jpx, new byte[(int)(floor * 12 / 10)], 612, 612), balanced with { Quality = 95 }));

        // A downsampled image may still win as samples, whatever a JPEG would take.
        var lossy = balanced with { MaxDpi = 150 };
        Assert.IsNotNull(Plan(Page(jpx, new byte[(int)floor / 2], 72, 72), lossy, new PlacedSize(1, 1)));

        // Too small to be worth judging, and a JPEG that would not shrink when downsampled.
        string gray = "/Type/XObject/Subtype/Image/Width 100/Height 100/ColorSpace/DeviceGray/BitsPerComponent 8";
        Assert.IsNull(Plan(Page(gray, new byte[200], 100, 100), balanced));
        Assert.IsNotNull(Plan(Page(gray, new byte[10_000], 100, 100), balanced));
        string dct = "/Type/XObject/Subtype/Image/Width 2000/Height 2000/ColorSpace/DeviceGray/BitsPerComponent 8/Filter/DCTDecode";
        Assert.AreEqual(90, LossyImages.MinJpegBytes(150, 150, 1, subsampled: true));
        Assert.IsNull(Plan(Page(dct, new byte[70], 72, 72), lossy, new PlacedSize(1, 1)));
        Assert.IsNotNull(Plan(Page(dct, new byte[200_000], 72, 72), lossy, new PlacedSize(1, 1)));
    }

    [TestMethod]
    public void JpegTwoThousandDecodesStraightToSamples()
    {
        // The same samples as the planes of the whole image give, for every fixture the lossy pass reads.
        foreach (string name in new[] { "rgb-53.jp2", "rgb-97.jp2", "rgb-97-tiles.jp2", "rgb-offset.jp2", "rgb-lrcp.jp2", "gray-53.jp2", "gray-97.j2k", "gray-12bit.j2k", "palette.jp2" })
        {
            byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpx", name));
            Jpeg2000Image image = Jpeg2000Decoder.Decode(data, Limit);
            int channels = image.Channels.Count;
            if (image.ColorSpace == 18 || image.Channels.Any(c => c.Opacity))
            {
                Assert.IsNull(Jpeg2000Decoder.DecodeRaster(data, image.Width, image.Height, channels, Limit), name);
                continue;
            }

            Raster expected = image.ToRaster(channels, scale: true);
            Raster? raster = Jpeg2000Decoder.DecodeRaster(data, image.Width, image.Height, channels, Limit);
            Assert.IsNotNull(raster, name);
            CollectionAssert.AreEqual(expected.Samples, raster.Samples, name);

            // Another size or channel count than the dictionary's is not decoded.
            Assert.IsNull(Jpeg2000Decoder.DecodeRaster(data, image.Width + 1, image.Height, channels, Limit), name);
            Assert.IsNull(Jpeg2000Decoder.DecodeRaster(data, image.Width, image.Height, channels + 1, Limit), name);
        }
    }

    [TestMethod]
    public void OneWorkspaceServesEveryImage()
    {
        // Buffers grown and filled by one image, then another of another size, coding and tiling,
        // in either order, decode each the same as fresh ones.
        string[] names = ["rgb-97-tiles.jp2", "gray-53.jp2", "rgb-53.jp2", "palette.jp2", "rgb-offset.jp2", "gray-97.j2k", "rgb-97.jp2"];
        var fresh = new Dictionary<string, byte[]>();
        foreach (string name in names)
        {
            byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpx", name));
            Jpeg2000Image image = Jpeg2000Decoder.Decode(data, Limit);
            fresh[name] = Jpeg2000Decoder.DecodeRaster(data, image.Width, image.Height, image.Channels.Count, Limit)!.Samples;
        }

        foreach (IEnumerable<string> order in new IEnumerable<string>[] { names, Enumerable.Reverse(names) })
        {
            var workspace = new Jpeg2000Workspace();
            foreach (string name in order)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpx", name));
                Jpeg2000Image image = Jpeg2000Decoder.Decode(data, Limit);
                CollectionAssert.AreEqual(fresh[name], Jpeg2000Decoder.DecodeRaster(data, image.Width, image.Height, image.Channels.Count, Limit, workspace)!.Samples, name);
            }
        }
    }
}
