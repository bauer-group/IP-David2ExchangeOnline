// SPDX-License-Identifier: MIT
using David2Exchange.Source;

namespace David2Exchange.Tests.Source;

public sealed class DavidItemIdentityTests
{
    [Theory]
    [InlineData(@"\\SRV\DAVID\archive\user\1\in\I[0000000000000F93E.001", "IF93E.001")]
    [InlineData(@"\\192.168.0.10\david\archive\user\1\IN\I0F93E.001", "IF93E.001")]
    [InlineData(@"\\srv\david\archive\user\1\out\M[000000000000CDD58.001", "MCDD58.001")]
    public void File_token_ignores_the_packed_and_unpacked_spelling(string path, string expected)
    {
        Assert.Equal(expected, DavidItemIdentity.FileToken(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kein Pfad, sondern Text")]
    public void Non_paths_yield_no_file_token(string? value)
    {
        Assert.Null(DavidItemIdentity.FileToken(value));
    }

    [Fact]
    public void Unknown_file_names_are_kept_verbatim()
    {
        Assert.Equal("ANDERS.TXT", DavidItemIdentity.FileToken(@"\\srv\a\anders.txt"));
    }

    [Theory]
    [InlineData(@"\\s\a\ICD058.001", "ICD058.001")]
    [InlineData(@"\\s\a\I0CD58.001", "ICD58.001")]
    [InlineData(@"\\s\a\I[000000000000CD058.001", "ICD058.001")]
    [InlineData(@"\\s\a\MA0B1234.001", "MA0B1234.001")]
    [InlineData(@"\\s\a\M0AB1234.001", "MAB1234.001")]
    public void Hex_letters_of_the_number_do_not_belong_to_the_prefix(string path, string expected)
    {
        Assert.Equal(expected, DavidItemIdentity.FileToken(path));
    }
}
