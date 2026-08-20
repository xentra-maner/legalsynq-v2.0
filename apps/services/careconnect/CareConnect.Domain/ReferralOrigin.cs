namespace CareConnect.Domain;

public static class ReferralOrigin
{
    public const string ReferralAssociate = "ReferralAssociate";
    public const string LawFirm = "LawFirm";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ReferralAssociate,
        LawFirm,
    };
}
