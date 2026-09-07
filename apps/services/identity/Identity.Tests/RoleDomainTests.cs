using Identity.Domain;
using Xunit;

namespace Identity.Tests;

/// <summary>Unit tests for <see cref="Role.Update"/> (tenant custom-role rename/re-describe).</summary>
public class RoleDomainTests
{
    private static Role NewRole() =>
        Role.Create(Guid.CreateVersion7(), "Reviewer", "Reviews cases", isSystemRole: false, scope: "Tenant");

    [Fact]
    public void Update_TrimsAndReportsChange()
    {
        var role = NewRole();
        var before = role.UpdatedAtUtc;

        var changed = role.Update("  Senior Reviewer  ", "  Reviews everything  ");

        Assert.True(changed);
        Assert.Equal("Senior Reviewer", role.Name);
        Assert.Equal("Reviews everything", role.Description);
        Assert.True(role.UpdatedAtUtc >= before);
    }

    [Fact]
    public void Update_BlankDescription_BecomesNull()
    {
        var role = NewRole();
        Assert.True(role.Update("Reviewer", "   "));
        Assert.Null(role.Description);
    }

    [Fact]
    public void Update_IsNoOp_WhenUnchanged()
    {
        var role = NewRole();
        Assert.False(role.Update("Reviewer", "Reviews cases"));
    }

    [Fact]
    public void Update_Rejects_BlankName()
    {
        var role = NewRole();
        Assert.Throws<ArgumentException>(() => role.Update("  ", "x"));
    }
}
