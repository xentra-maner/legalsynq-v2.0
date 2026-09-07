using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.DTOs;
using Notifications.Application.Interfaces;
using Notifications.Application.Options;
using Notifications.Domain;
using Notifications.Infrastructure.Services;
using System.Text.Json;
using Xunit;

namespace Notifications.Tests;

/// <summary>
/// Verifies that the <see cref="NotificationServiceImpl.SubmitAsync"/> method
/// propagates the actual <see cref="ProviderFailure.Category"/> from the provider
/// adapter onto <see cref="NotificationResultDto.FailureCategory"/>.
///
/// Task #114, requirement (3):
///   "notification-level FailureCategory reflects actual SendGrid failure category
///    (e.g. auth_config_failure) rather than the generic retryable_provider_failure."
/// </summary>
public class NotificationServiceFailureCategoryTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NotificationServiceImpl BuildService(
        IEmailProviderAdapter emailAdapter,
        IUserInboxService? inbox = null,
        StubNotificationRepository? notificationRepository = null)
    {
        var notifRepo    = notificationRepository ?? new StubNotificationRepository();
        var attemptRepo  = new StubNotificationAttemptRepository();
        var eventRepo    = new StubNotificationEventRepository();
        var issueRepo    = new StubDeliveryIssueRepository();
        var routing      = new StubProviderRoutingService();
        var contact      = new StubContactEnforcementService();
        var usage        = new StubUsageEvaluationService();
        var metering     = new StubUsageMeteringService();
        var templateRes  = new StubTemplateResolutionService();
        var templateRend = new StubTemplateRenderingService();
        var branding     = new StubBrandingResolutionService();
        var smsAdapter        = new StubSmsProviderAdapter();
        var smsRuntime        = new StubSmsProviderRuntimeResolver();
        var recipient         = new StubRecipientResolver();
        var audit             = new StubAuditEventClient();
        var costOptions       = Options.Create(new SmsCostAnalyticsOptions());
        var smsRouting        = new StubSmsRoutingEngine();
        var routingDecisions  = new StubSmsRoutingDecisionRepository();
        var retrySuppression  = new StubSmsRetrySuppressionService();
        var governance        = new StubSmsGovernancePolicyService();
        var templateGov       = new StubSmsTemplateGovernanceService();
        var logger            = NullLogger<NotificationServiceImpl>.Instance;

        return new NotificationServiceImpl(
            notifRepo, attemptRepo, eventRepo, issueRepo,
            routing, contact, usage, metering,
            templateRes, templateRend, branding,
            emailAdapter, smsAdapter,
            smsRuntime, recipient, inbox ?? new StubUserInboxService(), audit,
            costOptions, smsRouting, routingDecisions,
            retrySuppression, governance, templateGov,
            new StubGovernanceExecutionRuntime(),
            logger);
    }

    private static SubmitNotificationDto EmailRequest(string toEmail = "to@example.com") => new()
    {
        Channel   = "email",
        Recipient = new { email = toEmail },
        Message   = new { subject = "Test subject", body = "Hello" },
    };

    [Theory]
    [InlineData("in_app")]
    [InlineData("in-app")]
    [InlineData("inapp")]
    public async Task SubmitAsync_NormalizesInAppAliases(string channel)
    {
        var inbox = new StubUserInboxService();
        var service = BuildService(new StubEmailProviderAdapter(new EmailSendResult { Success = true }), inbox);
        var userId = Guid.NewGuid();

        var result = await service.SubmitAsync(TenantId, new SubmitNotificationDto
        {
            Channel = channel,
            ProductKey = "liens",
            EventKey = "lien.offer.submitted",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Recipient = new { mode = "UserId", userId },
            Message = new { type = "lien.offer.submitted" },
            InboxPresentation = new InboxPresentationDto
            {
                Category = "lien",
                Title = "Offer Submitted",
                Description = "Your offer was submitted.",
                SourceDisplayName = "Synq Selling",
                SourceInitials = "SS",
                OccurredAtUtc = DateTime.UtcNow,
            },
        });

        Assert.Equal("sent", result.Status);
        Assert.Equal("in_app", inbox.Notification!.Channel);
    }

    [Fact]
    public async Task SubmitAsync_ReplaysExistingInAppNotificationWithoutCreatingAnotherInboxItem()
    {
        var key = Guid.NewGuid().ToString("N");
        var winner = ExistingInAppNotification(key);
        var repository = new StubNotificationRepository { Existing = winner };
        var inbox = new StubUserInboxService();
        var service = BuildService(
            new StubEmailProviderAdapter(new EmailSendResult { Success = true }),
            inbox,
            repository);

        var result = await service.SubmitAsync(TenantId, InAppRequest(key));

        Assert.Equal(winner.Id, result.Id);
        Assert.Equal("sent", result.Status);
        Assert.Equal(0, inbox.CreateCalls);
    }

    [Fact]
    public async Task SubmitAsync_LoadsDatabaseWinnerAfterConcurrentInAppInsertRace()
    {
        var key = Guid.NewGuid().ToString("N");
        var winner = ExistingInAppNotification(key);
        var repository = new StubNotificationRepository
        {
            Existing = winner,
            MissesBeforeExisting = 1,
        };
        var inbox = new StubUserInboxService
        {
            CreateException = new Microsoft.EntityFrameworkCore.DbUpdateException("duplicate idempotency key"),
        };
        var service = BuildService(
            new StubEmailProviderAdapter(new EmailSendResult { Success = true }),
            inbox,
            repository);

        var result = await service.SubmitAsync(TenantId, InAppRequest(key));

        Assert.Equal(winner.Id, result.Id);
        Assert.Equal("sent", result.Status);
        Assert.Equal(1, inbox.CreateCalls);
        Assert.Equal(2, repository.FindCalls);
    }

    private static SubmitNotificationDto InAppRequest(string idempotencyKey) => new()
    {
        Channel = "in_app",
        ProductKey = "liens",
        EventKey = "lien.offer.submitted",
        IdempotencyKey = idempotencyKey,
        Recipient = new { mode = "UserId", userId = Guid.NewGuid() },
        Message = new { type = "lien.offer.submitted" },
        InboxPresentation = new InboxPresentationDto
        {
            Category = "lien",
            Title = "Offer Submitted",
            Description = "Your offer was submitted.",
            SourceDisplayName = "Synq Selling",
            SourceInitials = "SS",
            OccurredAtUtc = DateTime.UtcNow,
        },
    };

    private static Notification ExistingInAppNotification(string idempotencyKey) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = TenantId,
        Channel = "in_app",
        Status = "sent",
        RecipientJson = "{}",
        MessageJson = "{}",
        IdempotencyKey = idempotencyKey,
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_FailureCategory_IsAuthConfigFailure_WhenAdapterReturnsAuthConfigFailure()
    {
        // Arrange: email adapter returns non-retryable auth_config_failure
        var adapter = new StubEmailProviderAdapter(new EmailSendResult
        {
            Success = false,
            Failure = new ProviderFailure
            {
                Category  = "auth_config_failure",
                Message   = "Invalid SendGrid API key",
                Retryable = false,
            },
        });

        var svc    = BuildService(adapter);
        var result = await svc.SubmitAsync(TenantId, EmailRequest());

        Assert.Equal("auth_config_failure", result.FailureCategory);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task SubmitAsync_FailureCategory_IsInvalidRecipient_WhenAdapterReturnsInvalidRecipient()
    {
        var adapter = new StubEmailProviderAdapter(new EmailSendResult
        {
            Success = false,
            Failure = new ProviderFailure
            {
                Category  = "invalid_recipient",
                Message   = "The recipient email address is invalid",
                Retryable = false,
            },
        });

        var svc    = BuildService(adapter);
        var result = await svc.SubmitAsync(TenantId, EmailRequest("bad@"));

        Assert.Equal("invalid_recipient", result.FailureCategory);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task SubmitAsync_FailureCategory_IsRetryableProviderFailure_WhenAdapterReturnsRetryableError()
    {
        // Arrange: adapter returns retryable failure (e.g. 429 Too Many Requests)
        var adapter = new StubEmailProviderAdapter(new EmailSendResult
        {
            Success = false,
            Failure = new ProviderFailure
            {
                Category  = "retryable_provider_failure",
                Message   = "Too many requests",
                Retryable = true,
            },
        });

        var svc    = BuildService(adapter);
        var result = await svc.SubmitAsync(TenantId, EmailRequest());

        // After exhausting all routes with only retryable failures, the service
        // schedules a retry and uses the generic "retryable_provider_failure" category.
        Assert.Equal("retryable_provider_failure", result.FailureCategory);
        Assert.Equal("retrying", result.Status);
    }

    [Fact]
    public async Task SubmitAsync_ForwardsDisableClickTrackingFlagToEmailProvider()
    {
        var adapter = new StubEmailProviderAdapter(new EmailSendResult { Success = true });
        var svc = BuildService(adapter);
        var request = new SubmitNotificationDto
        {
            Channel = "email",
            Recipient = new { email = "to@example.com" },
            Message = new
            {
                subject = "New Lien Offer",
                body = "View Lien for Sale: https://tenant.legalsynq.test/selling/public/token",
                html = "<!doctype html><html><body><a href=\"https://tenant.legalsynq.test/selling/public/token\">View Lien for Sale</a></body></html>",
                disableClickTracking = true,
            },
        };

        await svc.SubmitAsync(TenantId, request);

        Assert.NotNull(adapter.LastPayload);
        Assert.True(adapter.LastPayload!.DisableClickTracking);
    }

    [Fact]
    public async Task SubmitAsync_FailureCategory_IsAuthConfigFailure_WhenNoProviderRoutesConfigured()
    {
        // With an empty route list (routing service returns nothing) the service
        // sets FailureCategory = "auth_config_failure" and status = "failed".
        var adapter = new StubEmailProviderAdapter(new EmailSendResult { Success = true });
        var svc     = BuildService(adapter); // adapter won't even be called

        // Swap in a no-routes routing service on the same instance by building
        // a fresh service with the no-routes stub.
        var noRouteSvc = new NotificationServiceImpl(
            new StubNotificationRepository(),
            new StubNotificationAttemptRepository(),
            new StubNotificationEventRepository(),
            new StubDeliveryIssueRepository(),
            new NoRoutesProviderRoutingService(),
            new StubContactEnforcementService(),
            new StubUsageEvaluationService(),
            new StubUsageMeteringService(),
            new StubTemplateResolutionService(),
            new StubTemplateRenderingService(),
            new StubBrandingResolutionService(),
            adapter,
            new StubSmsProviderAdapter(),
            new StubSmsProviderRuntimeResolver(),
            new StubRecipientResolver(),
            new StubUserInboxService(),
            new StubAuditEventClient(),
            Options.Create(new SmsCostAnalyticsOptions()),
            new StubSmsRoutingEngine(),
            new StubSmsRoutingDecisionRepository(),
            new StubSmsRetrySuppressionService(),
            new StubSmsGovernancePolicyService(),
            new StubSmsTemplateGovernanceService(),
            new StubGovernanceExecutionRuntime(),
            NullLogger<NotificationServiceImpl>.Instance);

        var result = await noRouteSvc.SubmitAsync(TenantId, EmailRequest());

        Assert.Equal("auth_config_failure", result.FailureCategory);
        Assert.Equal("failed", result.Status);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubEmailProviderAdapter : IEmailProviderAdapter
    {
        private readonly EmailSendResult _result;
        public string ProviderType => "sendgrid";
        public EmailSendPayload? LastPayload { get; private set; }
        public StubEmailProviderAdapter(EmailSendResult result) => _result = result;
        public Task<bool>               ValidateConfigAsync() => Task.FromResult(true);
        public Task<EmailSendResult>    SendAsync(EmailSendPayload payload)
        {
            LastPayload = payload;
            return Task.FromResult(_result);
        }
        public Task<ProviderHealthResult> HealthCheckAsync() => Task.FromResult(new ProviderHealthResult { Status = "healthy" });
    }

    private sealed class StubSmsProviderAdapter : ISmsProviderAdapter
    {
        public string ProviderType => "twilio";
        public Task<bool>              ValidateConfigAsync() => Task.FromResult(true);
        public Task<SmsSendResult>     SendAsync(SmsSendPayload payload) => Task.FromResult(new SmsSendResult { Success = true });
        public Task<ProviderHealthResult> HealthCheckAsync() => Task.FromResult(new ProviderHealthResult { Status = "healthy" });
    }

    private sealed class StubNotificationRepository : INotificationRepository
    {
        private int _findCalls;

        public Notification? Existing { get; init; }
        public int MissesBeforeExisting { get; init; }
        public int FindCalls => _findCalls;

        public Task<Notification?> GetByIdAsync(Guid id)
            => Task.FromResult<Notification?>(null);

        public Task<Notification?> GetByIdAndTenantAsync(Guid id, Guid tenantId)
            => Task.FromResult<Notification?>(null);

        public Task<Notification?> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey)
        {
            var call = Interlocked.Increment(ref _findCalls);
            return Task.FromResult(call <= MissesBeforeExisting ? null : Existing);
        }

        public Task<List<Notification>> GetByTenantAsync(Guid tenantId, int limit = 50, int offset = 0)
            => Task.FromResult(new List<Notification>());

        public Task<Notification> CreateAsync(Notification notification)
        {
            if (notification.Id == Guid.Empty)
                notification.Id = Guid.CreateVersion7();
            return Task.FromResult(notification);
        }

        public Task UpdateAsync(Notification notification) => Task.CompletedTask;

        public Task UpdateStatusAsync(Guid id, string status, string? providerUsed = null,
            string? failureCategory = null, string? lastErrorMessage = null)
            => Task.CompletedTask;

        public Task<(List<Notification> Items, int Total)> GetPagedAsync(Guid tenantId, NotificationListQuery query)
            => Task.FromResult((new List<Notification>(), 0));

        public Task<NotificationStatsData> GetStatsAsync(Guid tenantId, NotificationStatsQuery query)
            => Task.FromResult(new NotificationStatsData());

        public Task<(List<Notification> Items, int Total)> GetPagedAdminAsync(Guid? tenantId, NotificationListQuery query)
            => Task.FromResult((new List<Notification>(), 0));

        public Task<NotificationStatsData> GetStatsAdminAsync(Guid? tenantId, NotificationStatsQuery query)
            => Task.FromResult(new NotificationStatsData());

        public Task<List<Notification>> GetEligibleForRetryAsync(int batchSize = 10)
            => Task.FromResult(new List<Notification>());

        public Task<List<Notification>> GetStalledProcessingAsync(TimeSpan threshold, int batchSize = 20)
            => Task.FromResult(new List<Notification>());
    }

    private sealed class StubNotificationAttemptRepository : INotificationAttemptRepository
    {
        public Task<NotificationAttempt?> GetByIdAsync(Guid id)
            => Task.FromResult<NotificationAttempt?>(null);

        public Task<NotificationAttempt?> FindByProviderMessageIdAsync(string providerMessageId)
            => Task.FromResult<NotificationAttempt?>(null);

        public Task<List<NotificationAttempt>> GetByNotificationIdAsync(Guid notificationId)
            => Task.FromResult(new List<NotificationAttempt>());

        public Task<NotificationAttempt> CreateAsync(NotificationAttempt attempt)
        {
            if (attempt.Id == Guid.Empty)
                attempt.Id = Guid.CreateVersion7();
            return Task.FromResult(attempt);
        }

        public Task UpdateAsync(NotificationAttempt attempt) => Task.CompletedTask;

        public Task UpdateStatusAsync(Guid id, string status, DateTime? completedAt = null)
            => Task.CompletedTask;

        public Task<List<NotificationAttempt>> GetStaleSmsAttemptsAsync(
            int limit, DateTime olderThan, IReadOnlyCollection<string> statuses, CancellationToken ct = default)
            => Task.FromResult(new List<NotificationAttempt>());

        public Task UpdateReconciliationTrackingAsync(
            Guid attemptId, string outcome, string? errorCode, string? providerStatus,
            string? normalizedStatus, DateTime reconciledAt, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateCostAsync(
            Guid attemptId, decimal? estimatedCostAmount, decimal? actualCostAmount,
            string? costCurrency, string costSource, DateTime costRecordedAt, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubNotificationEventRepository : INotificationEventRepository
    {
        public Task<NotificationEvent?> FindByDedupKeyAsync(string dedupKey)
            => Task.FromResult<NotificationEvent?>(null);

        public Task<NotificationEvent> CreateAsync(NotificationEvent evt)
        {
            if (evt.Id == Guid.Empty) evt.Id = Guid.CreateVersion7();
            return Task.FromResult(evt);
        }

        public Task<List<NotificationEvent>> GetByNotificationIdAsync(Guid notificationId, int limit = 50)
            => Task.FromResult(new List<NotificationEvent>());
    }

    private sealed class StubDeliveryIssueRepository : IDeliveryIssueRepository
    {
        public Task<DeliveryIssue?> CreateIfNotExistsAsync(DeliveryIssue issue)
            => Task.FromResult<DeliveryIssue?>(issue);

        public Task<List<DeliveryIssue>> GetByTenantAsync(Guid tenantId, int limit = 50, int offset = 0)
            => Task.FromResult(new List<DeliveryIssue>());

        public Task<List<DeliveryIssue>> GetByNotificationIdAsync(Guid notificationId)
            => Task.FromResult(new List<DeliveryIssue>());
    }

    private sealed class StubProviderRoutingService : IProviderRoutingService
    {
        public Task<List<ProviderRoute>> ResolveRoutesAsync(Guid tenantId, string channel)
            => Task.FromResult(new List<ProviderRoute>
            {
                new() { ProviderType = "sendgrid", OwnershipMode = "platform" },
            });
    }

    private sealed class NoRoutesProviderRoutingService : IProviderRoutingService
    {
        public Task<List<ProviderRoute>> ResolveRoutesAsync(Guid tenantId, string channel)
            => Task.FromResult(new List<ProviderRoute>());
    }

    private sealed class StubContactEnforcementService : IContactEnforcementService
    {
        public Task<ContactEnforcementResult> EvaluateAsync(ContactEnforcementInput input)
            => Task.FromResult(new ContactEnforcementResult { Allowed = true });
    }

    private sealed class StubUsageEvaluationService : IUsageEvaluationService
    {
        public Task<EnforcementDecision> CheckRequestAllowedAsync(Guid tenantId, string channel)
            => Task.FromResult(new EnforcementDecision { Allowed = true });

        public Task<EnforcementDecision> CheckAttemptAllowedAsync(Guid tenantId, string channel)
            => Task.FromResult(new EnforcementDecision { Allowed = true });
    }

    private sealed class StubUsageMeteringService : IUsageMeteringService
    {
        public Task MeterAsync(MeterEventInput input)     => Task.CompletedTask;
        public Task MeterBatchAsync(IEnumerable<MeterEventInput> inputs) => Task.CompletedTask;
    }

    private sealed class StubTemplateResolutionService : ITemplateResolutionService
    {
        public Task<ResolvedTemplate?> ResolveAsync(Guid tenantId, string templateKey, string channel)
            => Task.FromResult<ResolvedTemplate?>(null);

        public Task<ResolvedTemplate?> ResolveByProductAsync(Guid tenantId, string templateKey, string channel, string productType)
            => Task.FromResult<ResolvedTemplate?>(null);
    }

    private sealed class StubTemplateRenderingService : ITemplateRenderingService
    {
        public RenderResult Render(string? subjectTemplate, string bodyTemplate, string? textTemplate, Dictionary<string, string> data)
            => new() { Subject = "", Body = "", Text = null };

        public RenderResult RenderBranded(string? subjectTemplate, string bodyTemplate, string? textTemplate, Dictionary<string, string> data, Dictionary<string, string> brandingTokens)
            => new() { Subject = "", Body = "", Text = null };
    }

    private sealed class StubBrandingResolutionService : IBrandingResolutionService
    {
        public Task<ResolvedBranding> ResolveAsync(Guid tenantId, string productType)
            => Task.FromResult(new ResolvedBranding());

        public ResolvedBranding GetDefault(string productType) => new();

        public Dictionary<string, string> BuildBrandingTokens(ResolvedBranding branding)
            => new();
    }

    private sealed class StubRecipientResolver : IRecipientResolver
    {
        public Task<IReadOnlyList<ResolvedRecipient>> ResolveAsync(Guid tenantId, JsonElement recipient)
            => Task.FromResult<IReadOnlyList<ResolvedRecipient>>(Array.Empty<ResolvedRecipient>());
    }

    private sealed class StubAuditEventClient : IAuditEventClient
    {
        public Task<IngestResult> IngestAsync(IngestAuditEventRequest request, CancellationToken ct = default)
            => Task.FromResult(new IngestResult(true, Guid.CreateVersion7().ToString(), null, 202));

        public Task<BatchIngestResult> IngestBatchAsync(BatchIngestRequest request, CancellationToken ct = default)
            => Task.FromResult(new BatchIngestResult(0, 0, 0, Array.Empty<IngestResult>()));
    }

    private sealed class StubSmsProviderRuntimeResolver : ISmsProviderRuntimeResolver
    {
        public Task<SmsProviderRuntimeContext> ResolveForSendAsync(
            Guid tenantId, string providerType, Guid? providerConfigId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderRuntimeContext { Success = true, ProviderType = providerType });

        public Task<SmsProviderRuntimeContext> ResolveForReconciliationAsync(
            Guid? tenantId, string providerType, Guid? providerConfigId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderRuntimeContext { Success = true, ProviderType = providerType });
    }

    private sealed class StubSmsRoutingEngine : ISmsRoutingEngine
    {
        public Task<SmsRoutingDecisionResult> SelectRouteAsync(
            SmsRoutingRequest request, CancellationToken ct = default)
            => Task.FromResult(new SmsRoutingDecisionResult { Success = false, FailureCode = "no_route" });
    }

    private sealed class StubSmsRoutingDecisionRepository : ISmsRoutingDecisionRepository
    {
        public Task<SmsRoutingDecision> CreateAsync(SmsRoutingDecision decision, CancellationToken ct = default)
            => Task.FromResult(decision);

        public Task UpdateAttemptIdAsync(Guid decisionId, Guid attemptId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<(IReadOnlyList<SmsRoutingDecision> Items, int Total)> ListAsync(
            SmsRoutingDecisionQuery query, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<SmsRoutingDecision>, int)>((Array.Empty<SmsRoutingDecision>(), 0));

        public Task<SmsRoutingDecisionSummaryDto> GetSummaryAsync(
            SmsRoutingDecisionQuery query, CancellationToken ct = default)
            => Task.FromResult(new SmsRoutingDecisionSummaryDto());
    }

    private sealed class StubSmsRetrySuppressionService : ISmsRetrySuppressionService
    {
        public Task<SmsRetrySuppressionResult> EvaluateAsync(
            SmsRetrySuppressionRequest request, CancellationToken ct)
            => Task.FromResult(new SmsRetrySuppressionResult { DecisionType = "allow" });
    }

    private sealed class StubSmsGovernancePolicyService : ISmsGovernancePolicyService
    {
        public Task<SmsGovernanceEvaluationResult> EvaluatePreSendAsync(
            SmsGovernanceEvaluationRequest request, CancellationToken ct = default)
            => Task.FromResult(new SmsGovernanceEvaluationResult { DecisionType = "allow" });

        public Task<SmsGovernanceEvaluationResult> EvaluateRetryAsync(
            SmsGovernanceEvaluationRequest request, CancellationToken ct = default)
            => Task.FromResult(new SmsGovernanceEvaluationResult { DecisionType = "allow" });

        public Task<SmsGovernanceEvaluationResult> EvaluateEscalationAsync(
            SmsGovernanceEvaluationRequest request, CancellationToken ct = default)
            => Task.FromResult(new SmsGovernanceEvaluationResult { DecisionType = "allow" });
    }

    private sealed class StubSmsTemplateGovernanceService : ISmsTemplateGovernanceService
    {
        public Task<SmsTemplateGovernanceResult> EvaluateAsync(
            SmsTemplateGovernanceRequest request, CancellationToken ct = default)
            => Task.FromResult(new SmsTemplateGovernanceResult { ShouldProceed = true });

        public Task<(bool Passed, List<string> Errors)> ValidateVariablesAsync(
            ValidateTemplateVariablesRequest request, CancellationToken ct = default)
            => Task.FromResult((true, new List<string>()));

        public string ClassifyContent(ClassifyTemplateRequest request) => "transactional";

        public Task<Guid> CreateTemplateAsync(
            CreateSmsTemplateRequest request, CancellationToken ct = default)
            => Task.FromResult(Guid.CreateVersion7());

        public Task<bool> UpdateTemplateAsync(
            UpdateSmsTemplateRequest request, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> ArchiveTemplateAsync(
            Guid templateId, string? requestedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<Guid> CreateVersionAsync(
            CreateSmsTemplateVersionRequest request, CancellationToken ct = default)
            => Task.FromResult(Guid.CreateVersion7());

        public Task<bool> SubmitForReviewAsync(
            Guid templateId, string? requestedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> ApproveVersionAsync(
            Guid templateId, string approvedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> RejectVersionAsync(
            Guid templateId, string rejectedBy, string reason, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<(int Total, IReadOnlyList<SmsTemplate> Items)> GetTemplatesAsync(
            TemplateGovernancePolicyQuery query, CancellationToken ct = default)
            => Task.FromResult<(int, IReadOnlyList<SmsTemplate>)>((0, Array.Empty<SmsTemplate>()));

        public Task<(int Total, IReadOnlyList<SmsTemplateGovernanceDecision> Items)> GetDecisionsAsync(
            TemplateGovernanceDecisionQuery query, CancellationToken ct = default)
            => Task.FromResult<(int, IReadOnlyList<SmsTemplateGovernanceDecision>)>(
                (0, Array.Empty<SmsTemplateGovernanceDecision>()));
    }

    private sealed class StubGovernanceExecutionRuntime : IGovernanceExecutionRuntime
    {
        public Task<GovernanceExecutionResult> EvaluateAsync(
            GovernanceExecutionContext context, CancellationToken ct = default)
            => Task.FromResult(new GovernanceExecutionResult { ShouldProceed = true });

        public Task<GovernanceSimulationResult> SimulateAsync(
            GovernanceSimulationRequest request, CancellationToken ct = default)
            => Task.FromResult(new GovernanceSimulationResult());

        public Task<IReadOnlyList<GovernanceChannelRuntimeStatus>> GetChannelRuntimeStatusAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GovernanceChannelRuntimeStatus>>(
                Array.Empty<GovernanceChannelRuntimeStatus>());
    }

    private sealed class StubUserInboxService : IUserInboxService
    {
        public Notification? Notification { get; private set; }
        public Exception? CreateException { get; init; }
        public int CreateCalls { get; private set; }
        public Task CreateWithNotificationAsync(Notification notification, Guid recipientUserId, string productKey, string eventKey, InboxPresentationDto presentation, CancellationToken ct = default)
        {
            CreateCalls++;
            if (CreateException is not null) throw CreateException;
            Notification = notification;
            return Task.CompletedTask;
        }
        public Task<UserInboxPageDto> ListAsync(Guid tenantId, Guid userId, string? category, string? readState, int page, int pageSize, DateTime? asOfUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UserInboxSummaryDto> GetSummaryAsync(Guid tenantId, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UserInboxReadResultDto?> MarkReadAsync(Guid tenantId, Guid userId, Guid itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MarkAllInboxReadResultDto> MarkAllReadAsync(Guid tenantId, Guid userId, DateTime throughUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DismissAsync(Guid tenantId, Guid userId, Guid itemId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
