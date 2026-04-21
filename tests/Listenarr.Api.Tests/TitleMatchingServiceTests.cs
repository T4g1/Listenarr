using Xunit;
using Listenarr.Domain.Utils;

namespace Listenarr.Api.Tests
{
    public class TitleMatchingServiceTests
    {
        [Theory]
        [InlineData("The Great Book [Edition] (2020) - 320kbps", "The Great Book")]
        [InlineData("Some_Title-v0.flac", "Some Title")]
        [InlineData("An Audiobook - Unabridged", "An Audiobook")]
        public void NormalizeTitle_RemovesNoise(string input, string expectedStart)
        {
            var norm = TitleUtils.NormalizeTitle(input);
            Assert.False(string.IsNullOrWhiteSpace(norm));
            Assert.Contains(expectedStart.Split(' ')[0], norm, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("The Great Book", "The Great Book", true)]
        [InlineData("The Great Book - 320kbps", "The Great Book", true)]
        [InlineData("The Great Book (unabridged)", "Great Book", true)]
        [InlineData("Completely Different Title", "Another Title", false)]
        public void AreTitlesSimilar_BasicCases(string a, string b, bool expected)
        {
            var result = TitleUtils.AreTitlesSimilar(a, b);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void IsMatchingTitle_EmptyInputs_ReturnsFalse()
        {
            Assert.False(TitleUtils.IsMatchingTitle("", "some"));
            Assert.False(TitleUtils.IsMatchingTitle("some", ""));
            Assert.False(TitleUtils.IsMatchingTitle("", ""));
        }

        [Fact]
        public void AreTitlesSimilar_LongPrefixMatch_ReturnsTrue()
        {
            var a = "A very long audiobook title that contains lots of words and metadata tags";
            var b = "A very long audiobook title that contains lots of words";
            Assert.True(TitleUtils.AreTitlesSimilar(a, b));
        }
    }
}
