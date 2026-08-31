using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

public class TokenGenTests
{
    [Fact]
    public void Token_Matches_Official_Format()
    {
        Assert.Matches("^sm_[0-9a-f]{32}$", TokenGen.NewToken());
    }

    [Fact]
    public void Tokens_Are_Unique()
    {
        Assert.NotEqual(TokenGen.NewToken(), TokenGen.NewToken());
    }
}
