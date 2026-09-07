using Identity.Domain;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Unit tests for the <see cref="User"/> profile-mutation domain methods added
/// for tenant-portal User Management (<see cref="User.UpdateName"/> and
/// <see cref="User.ChangeEmail"/>).
/// </summary>
public class UserProfileMutationTests
{
    private static User NewUser() =>
        User.Create(Guid.CreateVersion7(), "alice@example.com", "hash", "Alice", "Adams");

    [Fact]
    public void UpdateName_TrimsAndReportsChange()
    {
        var user = NewUser();
        var before = user.UpdatedAtUtc;

        var changed = user.UpdateName("  Alicia  ", "  Adams  ");

        Assert.True(changed);
        Assert.Equal("Alicia", user.FirstName);
        Assert.Equal("Adams", user.LastName);
        Assert.True(user.UpdatedAtUtc >= before);
    }

    [Fact]
    public void UpdateName_IsNoOp_WhenUnchanged()
    {
        var user = NewUser();
        Assert.False(user.UpdateName("Alice", "Adams"));
    }

    [Fact]
    public void UpdateName_Rejects_BlankParts()
    {
        var user = NewUser();
        Assert.Throws<ArgumentException>(() => user.UpdateName("Alice", "  "));
    }

    [Fact]
    public void ChangeEmail_LowercasesTrims_AndBumpsSessionVersion()
    {
        var user = NewUser();
        var version = user.SessionVersion;

        var changed = user.ChangeEmail("  NEW@Example.COM ");

        Assert.True(changed);
        Assert.Equal("new@example.com", user.Email);
        Assert.Equal(version + 1, user.SessionVersion);
    }

    [Fact]
    public void ChangeEmail_IsNoOp_WhenUnchanged()
    {
        var user = NewUser();
        var version = user.SessionVersion;

        Assert.False(user.ChangeEmail("Alice@Example.com"));
        Assert.Equal(version, user.SessionVersion);
    }
}
