using BuildingBlocks.Domain;
using Liens.Application.Services;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Liens.Infrastructure.Persistence;

public class LiensDbContext : DbContext
{
    public LiensDbContext(DbContextOptions<LiensDbContext> options) : base(options) { }

    public DbSet<Case> Cases => Set<Case>();
    public DbSet<CaseUpdateHistory> CaseUpdateHistories => Set<CaseUpdateHistory>();
    public DbSet<CaseNumberReservation> CaseNumberReservations => Set<CaseNumberReservation>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Facility> Facilities => Set<Facility>();
    public DbSet<FacilityContactPerson>   FacilityContactPersons   => Set<FacilityContactPerson>();
    public DbSet<CompanyType> CompanyTypes => Set<CompanyType>();
    public DbSet<ContactPersonType> ContactPersonTypes => Set<ContactPersonType>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyContactPerson> CompanyContactPersons => Set<CompanyContactPerson>();
    public DbSet<SellingPartyAlias> SellingPartyAliases => Set<SellingPartyAlias>();
    public DbSet<SellingPartyBackfillCheckpoint> SellingPartyBackfillCheckpoints => Set<SellingPartyBackfillCheckpoint>();
    public DbSet<SellingPartyBackfillQuarantine> SellingPartyBackfillQuarantines => Set<SellingPartyBackfillQuarantine>();
    public DbSet<LienReduction>           LienReductions           => Set<LienReduction>();
    public DbSet<LienSettlement>          LienSettlements          => Set<LienSettlement>();
    public DbSet<SettlementPaymentDetail> SettlementPaymentDetails => Set<SettlementPaymentDetail>();
    public DbSet<DIYReportConfig>         DIYReportConfigs         => Set<DIYReportConfig>();
    public DbSet<BatchTemplate> BatchTemplates => Set<BatchTemplate>();
    public DbSet<BatchUpload> BatchUploads => Set<BatchUpload>();
    public DbSet<BatchUploadDetail> BatchUploadDetails => Set<BatchUploadDetail>();
    public DbSet<ManualMedicalCode>       ManualMedicalCodes       => Set<ManualMedicalCode>();
    public DbSet<LookupValue> LookupValues => Set<LookupValue>();
    public DbSet<Lien> Liens => Set<Lien>();
    public DbSet<LienStatusHistory> LienStatusHistories => Set<LienStatusHistory>();
    public DbSet<LienOffer> LienOffers => Set<LienOffer>();
    public DbSet<SellingPortfolio> SellingPortfolios => Set<SellingPortfolio>();
    public DbSet<SellingPortfolioLien> SellingPortfolioLiens => Set<SellingPortfolioLien>();
    public DbSet<SellingPortfolioBuyer> SellingPortfolioBuyers => Set<SellingPortfolioBuyer>();
    public DbSet<SellingPortfolioStatusHistory> SellingPortfolioStatusHistory => Set<SellingPortfolioStatusHistory>();
    public DbSet<SellingPortfolioActivity> SellingPortfolioActivities => Set<SellingPortfolioActivity>();
    public DbSet<SellingBuyerAccessLink> SellingBuyerAccessLinks => Set<SellingBuyerAccessLink>();
    public DbSet<SellingCaseDraft> SellingCaseDrafts => Set<SellingCaseDraft>();
    public DbSet<SellingIdempotencyRecord> SellingIdempotencyRecords => Set<SellingIdempotencyRecord>();
    public DbSet<SellingPortalMessage> SellingPortalMessages => Set<SellingPortalMessage>();
    public DbSet<SellingPortalMessageAttachment> SellingPortalMessageAttachments => Set<SellingPortalMessageAttachment>();
    public DbSet<SellingNotificationOutboxItem> SellingNotificationOutboxItems => Set<SellingNotificationOutboxItem>();
    public DbSet<BillOfSale> BillsOfSale => Set<BillOfSale>();
    public DbSet<ServicingItem> ServicingItems => Set<ServicingItem>();
    public DbSet<LienTask> LienTasks => Set<LienTask>();
    public DbSet<LienTaskLienLink> LienTaskLienLinks => Set<LienTaskLienLink>();
    public DbSet<LienWorkflowConfig>      LienWorkflowConfigs     => Set<LienWorkflowConfig>();
    public DbSet<LienWorkflowStage>       LienWorkflowStages      => Set<LienWorkflowStage>();
    public DbSet<LienWorkflowTransition>  LienWorkflowTransitions => Set<LienWorkflowTransition>();
    // TASK-MIG-09: LienTaskTemplates DbSet removed — liens_TaskTemplates dropped (MIG-09 migration)
    public DbSet<LienTaskGenerationRule> LienTaskGenerationRules => Set<LienTaskGenerationRule>();
    public DbSet<LienGeneratedTaskMetadata> LienGeneratedTaskMetadatas => Set<LienGeneratedTaskMetadata>();
    public DbSet<LienTaskNote> LienTaskNotes => Set<LienTaskNote>();
    public DbSet<LienCaseNote> LienCaseNotes => Set<LienCaseNote>();
    public DbSet<LegacyImportApproval> LegacyImportApprovals => Set<LegacyImportApproval>();
    public DbSet<LegacyImportRun> LegacyImportRuns => Set<LegacyImportRun>();
    public DbSet<LegacyIdCrosswalk> LegacyIdCrosswalks => Set<LegacyIdCrosswalk>();
    public DbSet<LegacyImportException> LegacyImportExceptions => Set<LegacyImportException>();
    public DbSet<LegacyUpdateEvent> LegacyUpdateEvents => Set<LegacyUpdateEvent>();
    public DbSet<LegacyFieldMigrationState> LegacyFieldMigrationStates => Set<LegacyFieldMigrationState>();
    public DbSet<AutoGeneratedReport> AutoGeneratedReports => Set<AutoGeneratedReport>();
    public DbSet<SynqLienDocumentAssociation> SynqLienDocumentAssociations =>
        Set<SynqLienDocumentAssociation>();
    // TASK-MIG-09: LienTaskGovernanceSettings DbSet removed — liens_TaskGovernanceSettings dropped (MIG-09 migration)

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LiensDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureCaseNumbersAreReserved();
        EnsureHistoriesAreAppendOnly();
        var now = DateTime.UtcNow;

        ChangeTracker.DetectChanges();
        CaptureRootEntityHistories(now);

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

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureCaseNumbersAreReserved();
        EnsureHistoriesAreAppendOnly();
        var now = DateTime.UtcNow;
        ChangeTracker.DetectChanges();
        CaptureRootEntityHistories(now);

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
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private static readonly IReadOnlyDictionary<string, string> CaseBusinessFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Case.OrgId)] = "Law Firm Organization ID",
            [nameof(Case.CaseNumber)] = "Case Code",
            [nameof(Case.ExternalReference)] = "External Reference",
            [nameof(Case.Title)] = "Title",
            [nameof(Case.ClientFirstName)] = "Client First Name",
            [nameof(Case.ClientLastName)] = "Client Last Name",
            [nameof(Case.ClientDob)] = "Date of Birth",
            [nameof(Case.ClientPhone)] = "Phone",
            [nameof(Case.ClientEmail)] = "Email",
            [nameof(Case.ClientAddress)] = "Address",
            [nameof(Case.ClientAddressLine1)] = "Street Address",
            [nameof(Case.ClientCity)] = "City",
            [nameof(Case.ClientState)] = "State",
            [nameof(Case.ClientPostalCode)] = "Zip Code",
            [nameof(Case.Status)] = "Status",
            [nameof(Case.DateOfIncident)] = "Date of Loss",
            [nameof(Case.OpenedAtUtc)] = "Opened At",
            [nameof(Case.ClosedAtUtc)] = "Closed At",
            [nameof(Case.InsuranceCarrier)] = "Insurance Carrier",
            [nameof(Case.PolicyNumber)] = "Policy Number",
            [nameof(Case.ClaimNumber)] = "Claim Number",
            [nameof(Case.DemandAmount)] = "Demand Amount",
            [nameof(Case.SettlementAmount)] = "Settlement Amount",
            [nameof(Case.Description)] = "Description",
            [nameof(Case.IncidentState)] = "State of Incident",
            [nameof(Case.CurrentMedicalStatus)] = "Current Medical Status",
            [nameof(Case.TrackingFollowUpDate)] = "Tracking Follow Up Date",
            [nameof(Case.MinorComp)] = "Minor Comp",
            [nameof(Case.CaseDropped)] = "Case Dropped",
            [nameof(Case.HandlingLawFirmCompanyId)] = "Law Firm Company ID",
            [nameof(Case.CaseManagerContactPersonId)] = "Case Manager ID",
            [nameof(Case.AttorneyContactPersonId)] = "Attorney ID",
        };

    private static readonly IReadOnlyDictionary<string, string> LienBusinessFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Lien.OrgId)] = "Organization ID",
            [nameof(Lien.LienNumber)] = "Lien Code",
            [nameof(Lien.ExternalReference)] = "Funding Company ID",
            [nameof(Lien.LienType)] = "Lien Type",
            [nameof(Lien.Status)] = "Status",
            [nameof(Lien.CaseId)] = "Case ID",
            [nameof(Lien.SellingCaseId)] = "Selling Case ID",
            [nameof(Lien.MovedToManagementAtUtc)] = "Moved To Management At UTC",
            [nameof(Lien.FacilityId)] = "Facility ID",
            [nameof(Lien.SubjectPartyId)] = "Subject Party ID",
            [nameof(Lien.SubjectFirstName)] = "Subject First Name",
            [nameof(Lien.SubjectLastName)] = "Subject Last Name",
            [nameof(Lien.IsConfidential)] = "Confidential",
            [nameof(Lien.OriginalAmount)] = "Original Amount",
            [nameof(Lien.CurrentBalance)] = "Current Balance",
            [nameof(Lien.OfferPrice)] = "Offer Price",
            [nameof(Lien.PurchasePrice)] = "Purchase Price",
            [nameof(Lien.PayoffAmount)] = "Payoff Amount",
            [nameof(Lien.Jurisdiction)] = "Jurisdiction",
            [nameof(Lien.BuyerMessage)] = "Buyer Message",
            [nameof(Lien.IncidentDate)] = "Incident Date",
            [nameof(Lien.PurchaseDate)] = "Purchase Date",
            [nameof(Lien.ReceivableDueDate)] = "Receivable Due Date",
            [nameof(Lien.InitialServiceDate)] = "Initial Service Date",
            [nameof(Lien.EndServiceDate)] = "End Service Date",
            [nameof(Lien.IsBulk)] = "Bulk",
            [nameof(Lien.IsServicing)] = "Servicing",
            [nameof(Lien.OpenedAtUtc)] = "Opened At UTC",
            [nameof(Lien.ClosedAtUtc)] = "Closed At UTC",
            [nameof(Lien.SellingOrgId)] = "Selling Organization ID",
            [nameof(Lien.BuyingOrgId)] = "Buying Organization ID",
            [nameof(Lien.HoldingOrgId)] = "Holding Organization ID",
            [nameof(Lien.SellerStatus)] = "Seller Status",
            [nameof(Lien.ListingVisibility)] = "Listing Visibility",
            [nameof(Lien.FundingCompanyId)] = "Funding Company ID",
            [nameof(Lien.FundingCompanyContactId)] = "Funding Company Contact ID",
            [nameof(Lien.FundingCompanyCompanyId)] = "Funding Company ID",
            [nameof(Lien.FundingCompanyContactPersonId)] = "Funding Company Contact ID",
            [nameof(Lien.MedicalProviderCompanyId)] = "Medical Provider ID",
            [nameof(Lien.MedicalFacilityCompanyId)] = "Medical Facility ID",
            [nameof(Lien.AskAmount)] = "Ask Amount",
            [nameof(Lien.HighestBidAmount)] = "Highest Bid Amount",
            [nameof(Lien.SubmittedForSaleAtUtc)] = "Submitted For Sale At UTC",
            [nameof(Lien.SoldAtUtc)] = "Sold At UTC",
            [nameof(Lien.WithdrawnAtUtc)] = "Withdrawn At UTC",
            [nameof(Lien.ArchivedAtUtc)] = "Archived At UTC",
            [nameof(Lien.ArchivedReason)] = "Archived Reason",
        };

    private static readonly HashSet<string> LienCreationHistoryFields =
        new(StringComparer.Ordinal)
        {
            "Lien Code",
            "Status",
            "Purchase Date",
            "Initial Service Date",
        };

    private void CaptureRootEntityHistories(DateTime now)
    {
        foreach (var entry in ChangeTracker.Entries<Case>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToList())
        {
            var changes = BuildChanges(entry, CaseBusinessFields, expandCaseNotes: true);
            if (entry.State == EntityState.Modified && changes.Count == 0)
                continue;

            var action = entry.State switch
            {
                EntityState.Added => "Case Created",
                EntityState.Deleted => "Case Deleted",
                _ => "Case Details Update",
            };
            var actorUserId = ResolveActor(entry.Entity.CreatedByUserId, entry.Entity.UpdatedByUserId);
            CaseUpdateHistories.Add(CaseUpdateHistory.Create(
                entry.Entity.TenantId,
                entry.Entity.Id,
                action,
                BuildCaseHistoryDescription(entry, action, changes),
                actorUserId,
                now));
        }

        var existingSemanticHistory = ChangeTracker.Entries<LienStatusHistory>()
            .Where(entry => entry.State == EntityState.Added)
            .GroupBy(entry => entry.Entity.LienId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Entity).ToList());

        foreach (var entry in ChangeTracker.Entries<Lien>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToList())
        {
            var changes = BuildChanges(entry, LienBusinessFields, expandCaseNotes: false);
            if (entry.State == EntityState.Modified && changes.Count == 0)
                continue;
            var historyChanges = entry.State == EntityState.Added
                ? changes
                    .Where(change => LienCreationHistoryFields.Contains(change.Field))
                    .Select(change => change.Field == "Status" && entry.Entity.Status == LienStatus.Draft
                        ? change with { NewValue = string.Empty }
                        : change)
                    .ToList()
                : changes;

            var oldCaseId = entry.State == EntityState.Added
                ? null
                : entry.OriginalValues.GetValue<Guid?>(nameof(Lien.CaseId));
            var newCaseId = entry.State == EntityState.Deleted ? null : entry.Entity.CaseId;
            var activity = entry.State switch
            {
                EntityState.Added => "Lien Created",
                EntityState.Deleted => "Lien Deleted",
                _ => "Lien Update",
            };
            if (entry.State == EntityState.Modified &&
                changes.All(change => change.Field is "Status" or "Closed At UTC"))
            {
                var oldStatus = RootEntityHistoryFormatter.DisplayLienStatus(
                    entry.OriginalValues[nameof(Lien.Status)]) as string;
                var newStatus = RootEntityHistoryFormatter.DisplayLienStatus(
                    entry.CurrentValues[nameof(Lien.Status)]) as string;
                if (!string.Equals(oldStatus, newStatus, StringComparison.Ordinal))
                {
                    var isLogicalDelete = string.Equals(newStatus, "Rejected", StringComparison.Ordinal) &&
                                          string.Equals(oldStatus, "Closed", StringComparison.Ordinal);
                    activity = isLogicalDelete
                        ? "Lien status updated to Delete"
                        : $"Lien status updated to {newStatus}";
                    if (isLogicalDelete)
                    {
                        var statusIndex = changes.FindIndex(change => change.Field == "Status");
                        if (statusIndex >= 0)
                            changes[statusIndex] = changes[statusIndex] with { NewValue = "Delete" };
                    }
                }
            }
            var description = entry.State == EntityState.Added
                ? RootEntityHistoryFormatter.BuildCreationDescription(activity, historyChanges)
                : RootEntityHistoryFormatter.BuildDescription(activity, historyChanges);
            var actorUserId = ResolveActor(entry.Entity.CreatedByUserId, entry.Entity.UpdatedByUserId);
            var visibleCaseIds = new[] { oldCaseId, newCaseId }
                .Where(caseId => caseId.HasValue)
                .Select(caseId => caseId!.Value)
                .Distinct()
                .Select(caseId => (Guid?)caseId)
                .ToList();
            if (visibleCaseIds.Count == 0)
                visibleCaseIds.Add(null);

            existingSemanticHistory.TryGetValue(entry.Entity.Id, out var semanticEntries);
            var semanticProjections = new List<(Guid? CaseId, string Description)>();
            foreach (var semanticGroup in (semanticEntries ?? [])
                         .Where(history => visibleCaseIds.Contains(history.CaseId))
                         .GroupBy(history => history.CaseId))
            {
                // Several older callers could split one logical activity into multiple rows.
                // Keep the primary activity and attach the save-boundary field projection.
                var primary = semanticGroup.Last();
                foreach (var duplicate in semanticGroup.Where(history => history != primary))
                    Entry(duplicate).State = EntityState.Detached;

                var semanticActivity = RootEntityHistoryFormatter.ExtractActivity(primary.Description);
                semanticActivity = entry.State switch
                {
                    EntityState.Added when !semanticActivity.StartsWith("Lien Created", StringComparison.Ordinal) =>
                        $"Lien Created. {semanticActivity}",
                    EntityState.Deleted when !semanticActivity.StartsWith("Lien Deleted", StringComparison.Ordinal) =>
                        $"Lien Deleted. {semanticActivity}",
                    _ => semanticActivity,
                };
                var enrichedDescription = entry.State == EntityState.Added
                    ? RootEntityHistoryFormatter.BuildCreationDescription(semanticActivity, historyChanges)
                    : RootEntityHistoryFormatter.BuildDescription(semanticActivity, historyChanges);
                primary.ReplacePendingDescription(enrichedDescription);
                semanticProjections.Add((primary.CaseId, enrichedDescription));
            }

            foreach (var caseId in visibleCaseIds.Where(caseId =>
                         semanticProjections.All(projection => projection.CaseId != caseId)))
            {
                var projectedDescription = semanticProjections.Count > 0
                    ? semanticProjections[0].Description
                    : description;
                LienStatusHistories.Add(LienStatusHistory.Create(
                    entry.Entity.TenantId,
                    entry.Entity.Id,
                    caseId,
                    projectedDescription,
                    actorUserId));
            }
        }
    }

    private static List<LienFieldChange> BuildChanges<TEntity>(
        EntityEntry<TEntity> entry,
        IReadOnlyDictionary<string, string> fields,
        bool expandCaseNotes)
        where TEntity : class
    {
        var changes = new List<LienFieldChange>();
        foreach (var (propertyName, displayName) in fields)
        {
            var previousValue = entry.State == EntityState.Added ? null : entry.OriginalValues[propertyName];
            var currentValue = entry.State == EntityState.Deleted ? null : entry.CurrentValues[propertyName];
            var valuesEqual = typeof(TEntity) == typeof(Case) && propertyName == nameof(Case.Status)
                ? RootEntityHistoryFormatter.CaseStatusesEqual(previousValue, currentValue)
                : RootEntityHistoryFormatter.ValuesEqual(previousValue, currentValue);
            if (!valuesEqual)
            {
                if (typeof(TEntity) == typeof(Lien) && propertyName == nameof(Lien.Status))
                {
                    var previousStatus = RootEntityHistoryFormatter.DisplayLienStatus(previousValue);
                    var currentStatus = RootEntityHistoryFormatter.DisplayLienStatus(currentValue);
                    if (!RootEntityHistoryFormatter.ValuesEqual(previousStatus, currentStatus))
                    {
                        previousValue = previousStatus;
                        currentValue = currentStatus;
                    }
                }
                changes.Add(new LienFieldChange(displayName, previousValue, currentValue));
            }
        }

        if (expandCaseNotes)
        {
            var previousNotes = entry.State == EntityState.Added ? null : entry.OriginalValues[nameof(Case.Notes)];
            var currentNotes = entry.State == EntityState.Deleted ? null : entry.CurrentValues[nameof(Case.Notes)];
            changes.AddRange(RootEntityHistoryFormatter.ExpandCaseNotes(previousNotes, currentNotes));

            var previousStatus = entry.State == EntityState.Added
                ? null
                : entry.OriginalValues[nameof(Case.Status)];
            var currentStatus = entry.State == EntityState.Deleted
                ? null
                : entry.CurrentValues[nameof(Case.Status)];
            changes.RemoveAll(change =>
                change.Field == "Status Label" &&
                (RootEntityHistoryFormatter.ValuesEqual(change.PreviousValue, null) ||
                 RootEntityHistoryFormatter.CaseStatusesEqual(change.PreviousValue, previousStatus)) &&
                (RootEntityHistoryFormatter.ValuesEqual(change.NewValue, null) ||
                 RootEntityHistoryFormatter.CaseStatusesEqual(change.NewValue, currentStatus)));
        }

        if (typeof(TEntity) == typeof(Lien))
        {
            var previousDescription = entry.State == EntityState.Added ? null : entry.OriginalValues[nameof(Lien.Description)];
            var currentDescription = entry.State == EntityState.Deleted ? null : entry.CurrentValues[nameof(Lien.Description)];
            var previousNotes = entry.State == EntityState.Added ? null : entry.OriginalValues[nameof(Lien.Notes)];
            var currentNotes = entry.State == EntityState.Deleted ? null : entry.CurrentValues[nameof(Lien.Notes)];
            var previousLogicalNote = ResolveLienNote(previousNotes, previousDescription);
            var currentLogicalNote = ResolveLienNote(currentNotes, currentDescription);
            if (!RootEntityHistoryFormatter.ValuesEqual(previousLogicalNote, currentLogicalNote))
                changes.Add(new LienFieldChange("Note", previousLogicalNote, currentLogicalNote));

            var shouldCaptureDescriptionSeparately = entry.State switch
            {
                EntityState.Added =>
                    !string.IsNullOrWhiteSpace(currentNotes as string) &&
                    !string.IsNullOrWhiteSpace(currentDescription as string),
                EntityState.Deleted =>
                    !string.IsNullOrWhiteSpace(previousNotes as string) &&
                    !string.IsNullOrWhiteSpace(previousDescription as string),
                _ =>
                    !string.IsNullOrWhiteSpace(previousNotes as string) &&
                    !string.IsNullOrWhiteSpace(currentNotes as string) &&
                    !RootEntityHistoryFormatter.ValuesEqual(previousDescription, currentDescription),
            };
            if (shouldCaptureDescriptionSeparately)
            {
                changes.Add(new LienFieldChange("Description", previousDescription, currentDescription));
            }
        }

        return changes;
    }

    private static Guid ResolveActor(Guid? createdByUserId, Guid? updatedByUserId) =>
        updatedByUserId ?? createdByUserId ?? throw new InvalidOperationException(
            "Case and lien history requires a creating or updating user.");

    private static object? ResolveLienNote(object? notes, object? description) =>
        string.IsNullOrWhiteSpace(notes as string) ? description : notes;

    private static string BuildCaseHistoryDescription(
        EntityEntry<Case> entry,
        string action,
        IReadOnlyCollection<LienFieldChange> changes)
    {
        if (entry.State != EntityState.Added)
            return RootEntityHistoryFormatter.BuildDescription(action, changes);

        return $"Case created. Code: {entry.Entity.CaseNumber}; " +
               $"Client: {entry.Entity.ClientFirstName} {entry.Entity.ClientLastName}; " +
               $"Status: {entry.Entity.Status}. " +
               RootEntityHistoryFormatter.BuildDescription(action, changes);
    }

    private void EnsureCaseNumbersAreReserved()
    {
        var addedCases = ChangeTracker.Entries<Case>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();
        if (addedCases.Count == 0)
            return;

        var trackedReservations = ChangeTracker.Entries<CaseNumberReservation>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => (entry.Entity.TenantId, entry.Entity.CaseNumber))
            .ToHashSet();

        foreach (var caseEntity in addedCases)
        {
            var key = (caseEntity.TenantId, caseEntity.CaseNumber);
            if (trackedReservations.Add(key))
            {
                CaseNumberReservations.Add(
                    CaseNumberReservation.Create(caseEntity.TenantId, caseEntity.CaseNumber));
            }
        }
    }

    private void EnsureHistoriesAreAppendOnly()
    {
        if (ChangeTracker.Entries<LegacyUpdateEvent>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Legacy update events are append-only and cannot be modified or deleted through EF Core.");
        }

        if (ChangeTracker.Entries<CaseUpdateHistory>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Case update history is append-only and cannot be modified or deleted through EF Core.");
        }

        if (ChangeTracker.Entries<LienStatusHistory>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Lien status history is append-only and cannot be modified or deleted through EF Core.");
        }
    }
}
