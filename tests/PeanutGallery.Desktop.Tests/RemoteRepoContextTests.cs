using System.Linq;
using System.Text;
using PeanutGallery.Desktop.Services;
using Xunit;

namespace PeanutGallery.Desktop.Tests;

/// <summary>
/// <see cref="RemoteRepoContext.AcceptAsText"/> is the pure decision at the heart of the whole-file
/// context / conventions readers — bytes in, a verdict out — so it is tested directly rather than
/// through a GitHubClient (which has no injectable transport to mock).
/// </summary>
public class RemoteRepoContextTests
{
    [Fact]
    public void Plain_ascii_text_is_accepted_verbatim()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        Assert.Equal("hello world", RemoteRepoContext.AcceptAsText(bytes, maxBytes: 1024));
    }

    [Fact]
    public void Multibyte_utf8_text_is_accepted()
    {
        var bytes = Encoding.UTF8.GetBytes("café 😀"); // "café 😀"
        Assert.Equal("café 😀", RemoteRepoContext.AcceptAsText(bytes, maxBytes: 1024));
    }

    [Fact]
    public void Empty_bytes_are_rejected()
    {
        Assert.Null(RemoteRepoContext.AcceptAsText([], maxBytes: 1024));
    }

    [Fact]
    public void Bytes_over_the_cap_are_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 10));
        Assert.Null(RemoteRepoContext.AcceptAsText(bytes, maxBytes: 9));
    }

    [Fact]
    public void Bytes_at_exactly_the_cap_are_accepted()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 10));
        Assert.NotNull(RemoteRepoContext.AcceptAsText(bytes, maxBytes: 10));
    }

    [Fact]
    public void A_multibyte_file_whose_char_count_is_under_the_cap_but_whose_byte_count_is_over_is_rejected()
    {
        // 10 emoji: each is a surrogate pair (2 UTF-16 chars) encoded as 4 UTF-8 bytes, so this is
        // 20 chars but 40 bytes. A char-length check (the bug the review caught) would accept this
        // at a 32-byte cap (20 <= 32); the byte-length check must reject it (40 > 32).
        var text = string.Concat(Enumerable.Repeat("😀", 10));
        Assert.Equal(20, text.Length);
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(40, bytes.Length);
        Assert.Null(RemoteRepoContext.AcceptAsText(bytes, maxBytes: 32));
    }

    [Fact]
    public void An_explicit_nul_byte_is_rejected_as_binary()
    {
        byte[] bytes = [(byte)'a', 0, (byte)'b'];
        Assert.Null(RemoteRepoContext.AcceptAsText(bytes, maxBytes: 1024));
    }

    [Fact]
    public void A_nul_free_binary_header_is_still_rejected_via_invalid_utf8()
    {
        // A PNG header: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A - no NUL byte, but 0x89 and 0x1A are
        // not valid UTF-8 lead bytes on their own, so decoding must produce a replacement char.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Null(RemoteRepoContext.AcceptAsText(png, maxBytes: 1024));
    }
}
