using System.Security.Principal;

namespace AgentSeat.Windows.Tests;

public sealed class VirtualGamepadIsolationTests
{
    [Fact]
    public void BuildSecurityDescriptorGrantsOnlyLocalSystemAndSeatUsers()
    {
        var descriptor = VirtualGamepadIsolation.BuildSecurityDescriptor(
        [
            new SecurityIdentifier("S-1-5-21-1-2-3-1001"),
            new SecurityIdentifier("S-1-5-21-1-2-3-1000"),
            new SecurityIdentifier("S-1-5-21-1-2-3-1001")
        ]);

        Assert.Equal(
            "D:P(A;;GA;;;SY)(A;;GA;;;S-1-5-21-1-2-3-1000)(A;;GA;;;S-1-5-21-1-2-3-1001)",
            descriptor);
        Assert.True(VirtualGamepadIsolation.IsIsolationDescriptor(descriptor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not sddl")]
    [InlineData("D:(A;;GA;;;SY)(A;;GA;;;S-1-5-21-1-2-3-1000)")]
    [InlineData("D:P(A;;GA;;;S-1-5-21-1-2-3-1000)")]
    [InlineData("D:P(A;;GR;;;SY)")]
    [InlineData("D:P(A;;GA;;;SY)(D;;GA;;;WD)")]
    public void IsIsolationDescriptorRejectsDescriptorsFromOtherTools(string? descriptor)
    {
        Assert.False(VirtualGamepadIsolation.IsIsolationDescriptor(descriptor));
    }
}
