using BuildingBlocks.Domain;
using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;

namespace CareConnect.Infrastructure.Data;

public class CareConnectDbContext : DbContext
{
    public CareConnectDbContext(DbContextOptions<CareConnectDbContext> options) : base(options) { }

    public DbSet<Party>        Parties       => Set<Party>();
    public DbSet<PartyContact> PartyContacts => Set<PartyContact>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ProviderCategory> ProviderCategories => Set<ProviderCategory>();
    public DbSet<Specialty> Specialties => Set<Specialty>();
    public DbSet<ProviderSpecialty> ProviderSpecialties => Set<ProviderSpecialty>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<PendingReferralRequest> PendingReferralRequests => Set<PendingReferralRequest>();
    public DbSet<PendingReferralProviderPreference> PendingReferralProviderPreferences => Set<PendingReferralProviderPreference>();
    public DbSet<PendingReferralAttachment> PendingReferralAttachments => Set<PendingReferralAttachment>();
    public DbSet<ReferralStatusHistory> ReferralStatusHistories => Set<ReferralStatusHistory>();
    public DbSet<ReferralProviderReassignment> ReferralProviderReassignments => Set<ReferralProviderReassignment>();
    public DbSet<Facility> Facilities => Set<Facility>();
    public DbSet<ProviderFacility> ProviderFacilities => Set<ProviderFacility>();
    public DbSet<ServiceOffering> ServiceOfferings => Set<ServiceOffering>();
    public DbSet<ProviderServiceOffering> ProviderServiceOfferings => Set<ProviderServiceOffering>();
    public DbSet<ProviderAvailabilityTemplate> ProviderAvailabilityTemplates => Set<ProviderAvailabilityTemplate>();
    public DbSet<AppointmentSlot> AppointmentSlots => Set<AppointmentSlot>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentStatusHistory> AppointmentStatusHistories => Set<AppointmentStatusHistory>();
    public DbSet<ProviderAvailabilityException> ProviderAvailabilityExceptions => Set<ProviderAvailabilityException>();
    public DbSet<ReferralNote> ReferralNotes => Set<ReferralNote>();
    public DbSet<AppointmentNote> AppointmentNotes => Set<AppointmentNote>();
    public DbSet<ReferralAttachment> ReferralAttachments => Set<ReferralAttachment>();
    public DbSet<AppointmentAttachment> AppointmentAttachments => Set<AppointmentAttachment>();
    public DbSet<CareConnectNotification> CareConnectNotifications => Set<CareConnectNotification>();
    // LSCC-009: Provider activation queue
    public DbSet<ActivationRequest> ActivationRequests => Set<ActivationRequest>();

    // LSCC-01-004: Operational visibility — blocked access event log.
    public DbSet<BlockedProviderAccessLog> BlockedProviderAccessLogs => Set<BlockedProviderAccessLog>();

    // CC2-INT-B06: Provider networks (role-based network management)
    public DbSet<ProviderNetwork>  ProviderNetworks  => Set<ProviderNetwork>();
    public DbSet<NetworkProvider>  NetworkProviders  => Set<NetworkProvider>();

    // Public referral comment thread (token-authenticated, no login required)
    public DbSet<ReferralComment> ReferralComments => Set<ReferralComment>();

    // Referral Attribution — configurable referral-source tracking and representative access.
    public DbSet<ReferralAttribution> ReferralAttributions => Set<ReferralAttribution>();
    public DbSet<ReferralAttributionAccessCode> ReferralAttributionAccessCodes => Set<ReferralAttributionAccessCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CareConnectDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAtUtc == default)
                    entry.Property(nameof(AuditableEntity.CreatedAtUtc)).CurrentValue = now;

                entry.Property(nameof(AuditableEntity.UpdatedAtUtc)).CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(AuditableEntity.UpdatedAtUtc)).CurrentValue = now;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
