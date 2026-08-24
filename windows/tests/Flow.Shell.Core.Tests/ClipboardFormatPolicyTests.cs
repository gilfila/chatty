using Flow.Shell.Core;
using static Flow.Shell.Core.ClipboardFormatPolicy;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// The image / file / rich-content regression coverage the locked clipboard decision requires:
/// Flow borrows the clipboard to paste, so it must put back everything the user had, and must
/// never write garbage over a format it could not actually carry.
/// </summary>
public sealed class ClipboardFormatPolicyTests
{
    // ---- Content the user would notice losing --------------------------------

    [Theory]
    [InlineData(CF_TEXT)]
    [InlineData(CF_OEMTEXT)]
    [InlineData(CF_UNICODETEXT)]
    public void TextIsPreserved(uint format) => Assert.True(IsPreservable(format));

    [Theory]
    [InlineData(CF_DIB)]
    [InlineData(CF_DIBV5)]
    public void ImagesArePreservedThroughTheirDeviceIndependentForm(uint format) =>
        Assert.True(IsPreservable(format));

    [Fact]
    public void CopiedFilesArePreserved()
    {
        // A copied file selection in Explorer. Losing this silently would be the most surprising
        // thing Flow could do to a clipboard.
        Assert.True(IsPreservable(CF_HDROP));
    }

    [Theory]
    [InlineData(0xC000u)] // first registered format
    [InlineData(0xC04Fu)] // e.g. "HTML Format"
    [InlineData(0xC0A3u)] // e.g. "Rich Text Format"
    [InlineData(0xFFFFu)] // last registered format
    public void RichContentFromRegisteredFormatsIsPreserved(uint format) =>
        Assert.True(IsPreservable(format));

    [Fact]
    public void TheLocaleTagThatMakesPastedTextInterpretCorrectlyIsPreserved() =>
        Assert.True(IsPreservable(CF_LOCALE));

    // ---- Handles that are not global memory ---------------------------------

    [Theory]
    [InlineData(CF_BITMAP)]
    [InlineData(CF_PALETTE)]
    [InlineData(CF_METAFILEPICT)]
    [InlineData(CF_ENHMETAFILE)]
    public void GdiHandlesAreSkippedRatherThanCopied(uint format)
    {
        // GlobalSize on a GDI handle is meaningless. Copying whatever it returns would write
        // garbage back onto the user's clipboard, which is worse than not restoring the format.
        Assert.False(IsPreservable(format));
    }

    [Theory]
    [InlineData(CF_OWNERDISPLAY)]
    [InlineData(CF_DSPTEXT)]
    [InlineData(CF_DSPBITMAP)]
    [InlineData(CF_DSPMETAFILEPICT)]
    [InlineData(CF_DSPENHMETAFILE)]
    public void OwnerPaintedFormatsAreSkipped(uint format) => Assert.False(IsPreservable(format));

    [Theory]
    [InlineData(0x0200u)]
    [InlineData(0x0280u)]
    [InlineData(0x02FFu)]
    public void PrivateAppFormatsAreSkippedBecauseTheOwnerFreesThem(uint format) =>
        Assert.False(IsPreservable(format));

    [Theory]
    [InlineData(0x0300u)]
    [InlineData(0x0380u)]
    [InlineData(0x03FFu)]
    public void GdiObjectRangeFormatsAreSkipped(uint format)
    {
        // The regression this test exists for: 0x0300-0x03FF are GDI object handles, and a policy
        // that only names the predefined GDI formats lets this whole range through as if it were
        // global memory.
        Assert.False(IsPreservable(format));
    }

    [Fact]
    public void FormatZeroIsNotAFormat() => Assert.False(IsPreservable(0));

    // ---- Boundaries ---------------------------------------------------------

    [Fact]
    public void TheRangeBoundariesAreInclusive()
    {
        Assert.False(IsPreservable(CF_PRIVATEFIRST));
        Assert.False(IsPreservable(CF_PRIVATELAST));
        Assert.False(IsPreservable(CF_GDIOBJFIRST));
        Assert.False(IsPreservable(CF_GDIOBJLAST));

        // Immediately outside both ranges is ordinary global memory again.
        Assert.True(IsPreservable(CF_PRIVATEFIRST - 1));
        Assert.True(IsPreservable(CF_GDIOBJLAST + 1));
    }

    [Fact]
    public void EveryFormatIsClassifiedWithoutThrowing()
    {
        // Sweep the whole 16-bit space: the enumerator hands us whatever is on the clipboard, and
        // an unclassified value must not become a copy attempt by default.
        for (uint format = 0; format <= 0xFFFF; format++)
        {
            _ = IsPreservable(format);
        }
    }

    // ---- Completeness -------------------------------------------------------

    [Fact]
    public void AClipboardOfOrdinaryContentSnapshotsCompletely()
    {
        Assert.True(IsCompleteSnapshot(new[] { CF_UNICODETEXT, CF_TEXT, CF_LOCALE, 0xC04Fu }));
    }

    [Fact]
    public void AClipboardCarryingAnUnpreservableFormatIsNotComplete()
    {
        // Word puts CF_BITMAP alongside CF_DIB. The pixels survive, but the snapshot is not a
        // faithful copy of what was there, and the user should be told rather than guessed at.
        Assert.False(IsCompleteSnapshot(new[] { CF_UNICODETEXT, CF_DIB, CF_BITMAP }));
    }

    [Fact]
    public void AnEmptyClipboardIsTriviallyComplete() =>
        Assert.True(IsCompleteSnapshot(Array.Empty<uint>()));
}
