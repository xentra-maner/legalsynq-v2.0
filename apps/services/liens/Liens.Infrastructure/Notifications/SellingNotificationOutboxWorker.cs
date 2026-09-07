using Liens.Application.Interfaces;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Liens.Infrastructure.Notifications;

public sealed class SellingNotificationOutboxWorker : BackgroundService
{
    private const int BatchSize = 20;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SellingNotificationOutboxWorker> _logger;
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public SellingNotificationOutboxWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SellingNotificationOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await DispatchBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Selling notification outbox dispatch failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DispatchBatchAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<Guid> candidates;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            candidates = await db.SellingNotificationOutboxItems.AsNoTracking()
                .Where(item => item.ProcessedAtUtc == null &&
                               item.DeadLetteredAtUtc == null &&
                               item.NextAttemptAtUtc <= now &&
                               (item.LeaseUntilUtc == null || item.LeaseUntilUtc < now))
                .OrderBy(item => item.NextAttemptAtUtc)
                .ThenBy(item => item.Id)
                .Select(item => item.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
        }

        foreach (var id in candidates)
        {
            try
            {
                if (!await TryClaimAsync(id, now, ct)) continue;
                await DispatchOneAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Selling notification outbox item {OutboxItemId} failed to dispatch.", id);
            }
        }
    }

    private async Task<bool> TryClaimAsync(Guid id, DateTime now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        if (!db.Database.IsRelational())
        {
            var candidate = await db.SellingNotificationOutboxItems.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (candidate is null || candidate.NextAttemptAtUtc > now ||
                !candidate.TryLease(_workerId, now, now.AddMinutes(1))) return false;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var affected = await db.SellingNotificationOutboxItems
            .Where(item => item.Id == id &&
                           item.ProcessedAtUtc == null &&
                           item.DeadLetteredAtUtc == null &&
                           item.NextAttemptAtUtc <= now &&
                           (item.LeaseUntilUtc == null || item.LeaseUntilUtc < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LeaseOwner, _workerId)
                .SetProperty(item => item.LeaseUntilUtc, now.AddMinutes(1))
                .SetProperty(item => item.UpdatedAtUtc, now), ct);
        return affected == 1;
    }

    private async Task DispatchOneAsync(Guid id, CancellationToken ct)
    {
        NotificationInboxSendRequest request;
        using (var readScope = _scopeFactory.CreateScope())
        {
            var db = readScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var item = await db.SellingNotificationOutboxItems.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == id && candidate.LeaseOwner == _workerId, ct);
            request = new NotificationInboxSendRequest(
                item.TenantId, item.RecipientUserId, item.EventKey, item.Category,
                item.Title, item.Description, item.SourceDisplayName, item.SourceInitials,
                item.OccurredAtUtc, item.IdempotencyKey);
        }

        NotificationInboxSendResult result;
        try
        {
            using var sendScope = _scopeFactory.CreateScope();
            var publisher = sendScope.ServiceProvider.GetRequiredService<INotificationPublisher>();
            result = await publisher.SubmitInboxAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notifications submission threw for outbox item {OutboxItemId}.", id);
            result = new NotificationInboxSendResult(false, true, null, "Notifications submission failed unexpectedly.");
        }

        using var updateScope = _scopeFactory.CreateScope();
        var updateDb = updateScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var leased = await updateDb.SellingNotificationOutboxItems
            .SingleOrDefaultAsync(item => item.Id == id && item.LeaseOwner == _workerId, ct);
        if (leased is null) return;

        if (result.Succeeded)
            leased.MarkProcessed(DateTime.UtcNow);
        else
            leased.RecordFailure(result.Error ?? "Notification submission failed.", result.Retryable, DateTime.UtcNow);
        await updateDb.SaveChangesAsync(ct);
    }
}
