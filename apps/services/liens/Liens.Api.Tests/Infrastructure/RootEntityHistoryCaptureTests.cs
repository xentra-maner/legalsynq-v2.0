using Liens.Api.Endpoints;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liens.Api.Tests.Infrastructure;

public sealed class RootEntityHistoryCaptureTests
{
    [Fact]
    public async Task Case_changes_are_combined_and_normalized_no_ops_are_suppressed()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var caseEntity = Case.Create(
            tenantId,
            Guid.NewGuid(),
            "26-10001",
            "Ada",
            "Lovelace",
            actorId,
            notes: "Initial note\n\n[legacy-meta]\ncaseManagerId=old-manager; shareCase=N");

        db.Cases.Add(caseEntity);
        await db.SaveChangesAsync();

        db.CaseUpdateHistories.Should().ContainSingle(item =>
            item.CaseId == caseEntity.Id && item.Action == "Case Created");

        caseEntity.Update(
            "Ada",
            "Lovelace",
            actorId,
            notes: "Initial note\n\n[legacy-meta]\ncaseManagerId=new-manager; shareCase=Y");
        await db.SaveChangesAsync();

        var update = db.CaseUpdateHistories.Single(item => item.Action == "Case Details Update");
        update.Description.Should().Contain("Case Manager ID: old-manager → new-manager");
        update.Description.Should().Contain("Share Case: N → Y");
        update.Description.Should().NotContain("[legacy-meta]");

        var count = await db.CaseUpdateHistories.CountAsync();
        caseEntity.Update(
            " Ada ",
            "Lovelace",
            actorId,
            notes: "Initial note\n\n[legacy-meta]\ncaseManagerId=new-manager; shareCase=Y");
        await db.SaveChangesAsync();

        (await db.CaseUpdateHistories.CountAsync()).Should().Be(count);
    }

    [Fact]
    public async Task Equivalent_legacy_case_status_labels_do_not_create_a_change()
    {
        await using var db = CreateDbContext();
        var actorId = Guid.NewGuid();
        var caseEntity = Case.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "26-10001-A",
            "Katherine",
            "Johnson",
            actorId,
            notes: "Original note");
        caseEntity.TransitionStatus(CaseStatus.InNegotiation, actorId);
        db.Cases.Add(caseEntity);
        await db.SaveChangesAsync();

        caseEntity.Update(
            "Katherine",
            "Johnson",
            actorId,
            notes: "Original note\n\n[legacy-meta]\nstatusLabel=Negotiations");
        await db.SaveChangesAsync();

        db.CaseUpdateHistories.Should().ContainSingle(item => item.Action == "Case Created");
    }

    [Fact]
    public async Task Moving_a_lien_projects_one_combined_update_to_both_case_timelines()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var oldCaseId = Guid.NewGuid();
        var newCaseId = Guid.NewGuid();
        var lien = Lien.Create(
            tenantId,
            orgId,
            "26-10001-1",
            LienType.MedicalLien,
            1250m,
            actorId,
            caseId: oldCaseId);

        db.Liens.Add(lien);
        await db.SaveChangesAsync();
        var creationHistoryIds = await db.LienStatusHistories
            .Select(item => item.Id)
            .ToListAsync();

        lien.AttachCase(newCaseId, actorId);
        lien.SetFinancials(1500m, actorId);
        await db.SaveChangesAsync();

        var histories = await db.LienStatusHistories
            .Where(item => item.LienId == lien.Id && !creationHistoryIds.Contains(item.Id))
            .OrderBy(item => item.CaseId)
            .ToListAsync();
        histories.Should().HaveCount(2);
        histories.Select(item => item.CaseId).Should().BeEquivalentTo([oldCaseId, newCaseId]);
        histories.Should().OnlyContain(item => item.Description.Contains("Case ID:") &&
                                               item.Description.Contains("Original Amount: 1250.00 → 1500.00"));
    }

    [Fact]
    public async Task Case_history_is_append_only_and_has_no_case_foreign_key()
    {
        await using var db = CreateDbContext();
        var actorId = Guid.NewGuid();
        var caseEntity = Case.Create(
            Guid.NewGuid(), Guid.NewGuid(), "26-10002", "Grace", "Hopper", actorId);
        db.Cases.Add(caseEntity);
        await db.SaveChangesAsync();

        var createdHistory = await db.CaseUpdateHistories.SingleAsync();
        db.Entry(createdHistory).State = EntityState.Modified;
        var updateHistory = () => db.SaveChangesAsync();
        await updateHistory.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");

        db.Entry(createdHistory).State = EntityState.Unchanged;
        var deletingActorId = Guid.NewGuid();
        caseEntity.MarkForDeletion(deletingActorId);
        db.Cases.Remove(caseEntity);
        await db.SaveChangesAsync();

        (await db.CaseUpdateHistories.CountAsync()).Should().Be(2);
        db.CaseUpdateHistories.Should().Contain(item =>
            item.Action == "Case Deleted" && item.ActorUserId == deletingActorId);
        var historyModel = db.Model.FindEntityType(typeof(CaseUpdateHistory))!;
        historyModel.GetForeignKeys().Should().BeEmpty();
    }

    [Fact]
    public async Task Lien_history_is_append_only_and_retained_after_lien_deletion()
    {
        await using var db = CreateDbContext();
        var actorId = Guid.NewGuid();
        var lien = Lien.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "26-10003-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: Guid.NewGuid());
        db.Liens.Add(lien);
        await db.SaveChangesAsync();

        var createdHistory = await db.LienStatusHistories.SingleAsync();
        db.Entry(createdHistory).State = EntityState.Modified;
        var updateHistory = () => db.SaveChangesAsync();
        await updateHistory.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");

        db.Entry(createdHistory).State = EntityState.Unchanged;
        db.Liens.Remove(lien);
        await db.SaveChangesAsync();

        (await db.LienStatusHistories.CountAsync()).Should().Be(2);
        db.LienStatusHistories.Should().Contain(item => item.Description.StartsWith("Lien Deleted."));
        var historyModel = db.Model.FindEntityType(typeof(LienStatusHistory))!;
        historyModel.GetForeignKeys().Should().BeEmpty();
    }

    [Fact]
    public async Task Equivalent_business_statuses_preserve_the_actual_lien_status_transition()
    {
        await using var db = CreateDbContext();
        var actorId = Guid.NewGuid();
        var lien = Lien.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "26-10004-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: Guid.NewGuid());
        db.Liens.Add(lien);
        await db.SaveChangesAsync();

        lien.SetLegacyMedicalStatus("Open", actorId);
        await db.SaveChangesAsync();

        var update = await db.LienStatusHistories
            .Where(item => item.LienId == lien.Id && item.Description.StartsWith("Lien Update."))
            .SingleAsync();
        update.Description.Should().Contain("Status: Draft → Active");
    }

    [Fact]
    public async Task Semantic_lien_history_suppresses_only_the_case_projection_it_already_covers()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var oldCaseId = Guid.NewGuid();
        var newCaseId = Guid.NewGuid();
        var lien = Lien.Create(
            tenantId,
            Guid.NewGuid(),
            "26-10005-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: oldCaseId);
        db.Liens.Add(lien);
        await db.SaveChangesAsync();
        var creationHistoryIds = await db.LienStatusHistories.Select(item => item.Id).ToListAsync();

        lien.AttachCase(newCaseId, actorId);
        db.LienStatusHistories.Add(LienStatusHistory.Create(
            tenantId,
            lien.Id,
            newCaseId,
            "Semantic lien move activity.",
            actorId));
        await db.SaveChangesAsync();

        var moveRows = await db.LienStatusHistories
            .Where(item => item.LienId == lien.Id && !creationHistoryIds.Contains(item.Id))
            .ToListAsync();
        moveRows.Should().HaveCount(2);
        moveRows.Count(item => item.CaseId == oldCaseId).Should().Be(1);
        moveRows.Count(item => item.CaseId == newCaseId).Should().Be(1);
    }

    [Fact]
    public async Task Description_changes_are_logged_when_a_lien_also_has_notes()
    {
        await using var db = CreateDbContext();
        var actorId = Guid.NewGuid();
        var lien = Lien.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "26-10006-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: Guid.NewGuid(),
            description: "Old description",
            notes: "Persistent note");
        db.Liens.Add(lien);
        await db.SaveChangesAsync();

        lien.Update(
            lien.LienType,
            lien.OriginalAmount,
            actorId,
            description: "New description",
            notes: "Persistent note");
        await db.SaveChangesAsync();

        var update = await db.LienStatusHistories
            .Where(item => item.LienId == lien.Id && item.Description.StartsWith("Lien Update."))
            .SingleAsync();
        update.Description.Should().Contain("Description: Old description → New description");
    }

    [Fact]
    public async Task Semantic_history_is_enriched_with_every_root_change()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var lien = Lien.Create(
            tenantId,
            Guid.NewGuid(),
            "26-10007-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: caseId);
        db.Liens.Add(lien);
        await db.SaveChangesAsync();
        var creationHistoryIds = await db.LienStatusHistories.Select(item => item.Id).ToListAsync();

        lien.SetLegacyMedicalStatus("Closed", actorId);
        db.LienStatusHistories.Add(LienStatusHistory.Create(
            tenantId,
            lien.Id,
            caseId,
            "Status changed while recording payment.",
            actorId));
        await db.SaveChangesAsync();

        var update = await db.LienStatusHistories
            .Where(item => item.LienId == lien.Id && !creationHistoryIds.Contains(item.Id))
            .SingleAsync();
        update.Description.Should().StartWith("Status changed while recording payment.");
        update.Description.Should().Contain("Status: Open → Closed");
        update.Description.Should().Contain("Closed At UTC: blank →");
    }

    [Fact]
    public async Task Semantic_creation_is_one_row_and_contains_only_concise_creation_fields()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var lien = Lien.Create(
            tenantId,
            Guid.NewGuid(),
            "26-10008-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: caseId,
            initialServiceDate: new DateOnly(2026, 7, 27),
            description: "Initial description",
            notes: "Initial note",
            purchaseDate: new DateOnly(2026, 6, 22));
        db.Liens.Add(lien);
        db.LienStatusHistories.Add(LienStatusHistory.Create(
            tenantId,
            lien.Id,
            caseId,
            "Lien Status: Pending. Selling lien created",
            actorId));

        await db.SaveChangesAsync();

        var history = await db.LienStatusHistories.SingleAsync(item => item.LienId == lien.Id);
        history.Description.Should().Be(
            "Lien Created. Lien Status: Pending. Selling lien created. Changes: " +
            "Lien Code: 26-10008-1; Status: \"\"; " +
            "Purchase Date: 06/22/2026; Initial Service Date: 07/27/2026.");
    }

    [Fact]
    public async Task Deletion_logs_description_and_note_as_distinct_root_fields()
    {
        await using var db = CreateDbContext();
        var actorId = Guid.NewGuid();
        var lien = Lien.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "26-10009-1",
            LienType.MedicalLien,
            1000m,
            actorId,
            caseId: Guid.NewGuid(),
            description: "Initial description",
            notes: "Initial note");
        db.Liens.Add(lien);
        await db.SaveChangesAsync();

        db.Liens.Remove(lien);
        await db.SaveChangesAsync();

        var deletion = await db.LienStatusHistories.SingleAsync(item =>
            item.LienId == lien.Id && item.Description.StartsWith("Lien Deleted."));
        deletion.Description.Should().Contain("Note: Initial note → blank");
        deletion.Description.Should().Contain("Description: Initial description → blank");
    }

    [Fact]
    public async Task Scheduled_law_firm_switch_does_not_overwrite_concurrent_notes()
    {
        var databaseName = $"scheduled-law-firm-{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<LiensDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        var actorId = Guid.NewGuid();
        var pendingLawFirmId = Guid.NewGuid();

        await using var staleDb = new LiensDbContext(options);
        var caseEntity = Case.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "26-10010",
            "Concurrent",
            "Editor",
            actorId,
            notes: $"[legacy-meta] pendingLawFirmId={pendingLawFirmId}; switchedDate=2026-09-03");
        staleDb.Cases.Add(caseEntity);
        await staleDb.SaveChangesAsync();

        await using (var concurrentDb = new LiensDbContext(options))
        {
            var concurrentlyEdited = await concurrentDb.Cases.SingleAsync();
            var concurrentNotes =
                $"A user changed these notes while the scheduler was running.\n\n[legacy-meta]\n" +
                $"pendingLawFirmId={pendingLawFirmId}; switchedDate=2026-09-03";
            concurrentlyEdited.Update(
                "Concurrent",
                "Editor",
                actorId,
                notes: concurrentNotes);
            await concurrentDb.SaveChangesAsync();
        }

        var applied = await LawFirmChangeHistory.ApplyDueScheduledSwitchesAsync(
            staleDb,
            new DateOnly(2026, 9, 3),
            CancellationToken.None);

        applied.Should().Be(0);
        caseEntity.Notes.Should().StartWith("A user changed these notes while the scheduler was running.");
        staleDb.Model.FindEntityType(typeof(Case))!
            .FindProperty(nameof(Case.Notes))!
            .IsConcurrencyToken.Should().BeTrue();
    }

    private static LiensDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LiensDbContext>()
            .UseInMemoryDatabase($"root-history-{Guid.NewGuid()}")
            .Options;
        return new LiensDbContext(options);
    }
}
