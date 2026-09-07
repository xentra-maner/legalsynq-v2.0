using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.DTOs;
using Notifications.Application.Interfaces;
using Notifications.Application.Options;
using Notifications.Domain;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;

namespace Notifications.Infrastructure.Services;

public class NotificationServiceImpl : INotificationService
{
    private readonly INotificationRepository _notificationRepo;
    private readonly INotificationAttemptRepository _attemptRepo;
    private readonly INotificationEventRepository _eventRepo;
    private readonly IDeliveryIssueRepository _deliveryIssueRepo;
    private readonly IProviderRoutingService _routingService;
    private readonly IContactEnforcementService _contactEnforcement;
    private readonly IUsageEvaluationService _usageEvaluation;
    private readonly IUsageMeteringService _metering;
    private readonly ITemplateResolutionService _templateResolution;
    private readonly ITemplateRenderingService _templateRendering;
    private readonly IBrandingResolutionService _brandingResolution;
    private readonly IEmailProviderAdapter _sendGridAdapter;
    private readonly ISmsProviderAdapter _twilioAdapter;
    private readonly ISmsProviderRuntimeResolver _smsRuntimeResolver;
    private readonly IRecipientResolver _recipientResolver;
    private readonly IUserInboxService _userInboxService;
    private readonly IAuditEventClient _auditClient;
    private readonly SmsCostAnalyticsOptions _costOptions;
    // LS-NOTIF-SMS-014: intelligent routing engine + decision persistence
    private readonly ISmsRoutingEngine _smsRoutingEngine;
    private readonly ISmsRoutingDecisionRepository _routingDecisionRepo;
    // LS-NOTIF-SMS-016: retry suppression (optional — degrades to allow when null)
    private readonly ISmsRetrySuppressionService? _retrySuppressionService;
    // LS-NOTIF-SMS-017: governance policy evaluation (optional — degrades to allow when null)
    private readonly ISmsGovernancePolicyService? _governanceService;
    // LS-NOTIF-SMS-018: template governance — content classification, variable validation, compliance
    private readonly ISmsTemplateGovernanceService? _templateGovernanceService;
    // LS-NOTIF-SMS-025: cross-channel governance execution runtime (optional — degrades to allow when null)
    private readonly IGovernanceExecutionRuntime? _governanceRuntime;
    private readonly ILogger<NotificationServiceImpl> _logger;

    private static readonly JsonSerializerOptions _camelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public NotificationServiceImpl(
        INotificationRepository notificationRepo,
        INotificationAttemptRepository attemptRepo,
        INotificationEventRepository eventRepo,
        IDeliveryIssueRepository deliveryIssueRepo,
        IProviderRoutingService routingService,
        IContactEnforcementService contactEnforcement,
        IUsageEvaluationService usageEvaluation,
        IUsageMeteringService metering,
        ITemplateResolutionService templateResolution,
        ITemplateRenderingService templateRendering,
        IBrandingResolutionService brandingResolution,
        IEmailProviderAdapter sendGridAdapter,
        ISmsProviderAdapter twilioAdapter,
        ISmsProviderRuntimeResolver smsRuntimeResolver,
        IRecipientResolver recipientResolver,
        IUserInboxService userInboxService,
        IAuditEventClient auditClient,
        IOptions<SmsCostAnalyticsOptions> costOptions,
        ISmsRoutingEngine smsRoutingEngine,
        ISmsRoutingDecisionRepository routingDecisionRepo,
        ISmsRetrySuppressionService retrySuppressionService,
        ISmsGovernancePolicyService governanceService,
        ISmsTemplateGovernanceService templateGovernanceService,
        IGovernanceExecutionRuntime governanceRuntime,
        ILogger<NotificationServiceImpl> logger)
    {
        _notificationRepo        = notificationRepo;
        _attemptRepo             = attemptRepo;
        _eventRepo               = eventRepo;
        _deliveryIssueRepo       = deliveryIssueRepo;
        _routingService          = routingService;
        _contactEnforcement      = contactEnforcement;
        _usageEvaluation         = usageEvaluation;
        _metering                = metering;
        _templateResolution      = templateResolution;
        _templateRendering       = templateRendering;
        _brandingResolution      = brandingResolution;
        _sendGridAdapter         = sendGridAdapter;
        _twilioAdapter           = twilioAdapter;
        _smsRuntimeResolver      = smsRuntimeResolver;
        _recipientResolver       = recipientResolver;
        _userInboxService        = userInboxService;
        _auditClient             = auditClient;
        _costOptions             = costOptions.Value;
        _smsRoutingEngine        = smsRoutingEngine;
        _routingDecisionRepo     = routingDecisionRepo;
        _retrySuppressionService = retrySuppressionService;
        _governanceService         = governanceService;
        _templateGovernanceService = templateGovernanceService;
        _governanceRuntime         = governanceRuntime;
        _logger                    = logger;
    }

    // ─── Submit ──────────────────────────────────────────────────────────────

    public async Task<NotificationResultDto> SubmitAsync(Guid tenantId, SubmitNotificationDto request)
    {
        request.Channel = NormalizeChannel(request.Channel);
        var recipientJson = JsonSerializer.Serialize(request.Recipient);
        JsonElement recipientEl;
        try { recipientEl = JsonDocument.Parse(recipientJson).RootElement.Clone(); }
        catch { recipientEl = default; }

        if (request.Channel == "in_app")
            ValidateInboxRequest(request, recipientEl);

        var mode = ReadRecipientMode(recipientEl);
        var isFanOut = recipientEl.ValueKind == JsonValueKind.Array
                    || string.Equals(mode, "Role", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mode, "Org",  StringComparison.OrdinalIgnoreCase);

        if (!isFanOut)
            return await DispatchSingleAsync(tenantId, request, recipientJson);

        if (!string.IsNullOrEmpty(request.IdempotencyKey))
        {
            var existing = await _notificationRepo.FindByIdempotencyKeyAsync(tenantId, request.IdempotencyKey);
            if (existing != null)
                return MapToResult(existing);
        }

        var fanOutMode = mode ?? (recipientEl.ValueKind == JsonValueKind.Array ? "Batch" : "FanOut");
        var resolved   = await _recipientResolver.ResolveAsync(tenantId, recipientEl);
        var roleKey    = ReadRecipientField(recipientEl, "roleKey");
        var orgId      = ReadRecipientField(recipientEl, "orgId");

        var perRecipient = new List<FanOutPerRecipient>(resolved.Count);
        var dispatched   = new List<NotificationResultDto>(resolved.Count);

        foreach (var r in resolved)
        {
            var skipReason = ClassifySkipReason(request.Channel, r);
            if (skipReason != null)
            {
                perRecipient.Add(new FanOutPerRecipient
                {
                    UserId = r.UserId, Email = r.Email, OrgId = r.OrgId,
                    Status = "skipped", Reason = skipReason,
                });
                continue;
            }

            var perRequest = ClonePerRecipient(request, r);
            var perRecipientJson = JsonSerializer.Serialize(perRequest.Recipient);
            var dispatchResult = await DispatchSingleAsync(tenantId, perRequest, perRecipientJson);
            dispatched.Add(dispatchResult);

            perRecipient.Add(new FanOutPerRecipient
            {
                UserId = r.UserId, Email = r.Email, OrgId = r.OrgId,
                Status = dispatchResult.Status,
                Reason = dispatchResult.BlockedReasonCode ?? dispatchResult.FailureCategory,
                NotificationId = dispatchResult.Id == Guid.Empty ? null : dispatchResult.Id.ToString(),
            });
        }

        var summary = BuildFanOutSummary(fanOutMode, roleKey, orgId, request.Channel, resolved.Count, perRecipient);
        var parent  = await PersistFanOutParentAsync(tenantId, request, recipientJson, summary);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType = "notification.fanout",
                Action = "notification.fanout",
                SourceSystem = "notifications",
                Description = $"Fan-out {fanOutMode}: resolved={summary.TotalResolved} sent={summary.SentCount} " +
                              $"failed={summary.FailedCount} blocked={summary.BlockedCount} skipped={summary.SkippedCount}",
                Scope = new AuditEventScopeDto { TenantId = tenantId.ToString() }
            });
        }
        catch { /* audit best-effort */ }

        if (resolved.Count == 0)
            _logger.LogWarning("Notification fan-out resolved 0 recipients for tenant {TenantId} mode {Mode}", tenantId, fanOutMode);

        return BuildFanOutResult(parent, summary, dispatched);
    }

    // ─── List / Get ───────────────────────────────────────────────────────────

    public async Task<NotificationDto?> GetByIdAsync(Guid tenantId, Guid id)
    {
        var n = await _notificationRepo.GetByIdAndTenantAsync(id, tenantId);
        return n != null ? MapToDto(n) : null;
    }

    public async Task<List<NotificationDto>> ListAsync(Guid tenantId, int limit = 50, int offset = 0)
    {
        var list = await _notificationRepo.GetByTenantAsync(tenantId, limit, offset);
        return list.Select(MapToDto).ToList();
    }

    public async Task<PagedNotificationsResponse> ListPagedAsync(Guid tenantId, NotificationListQuery query)
    {
        var (items, total) = await _notificationRepo.GetPagedAsync(tenantId, query);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page     = Math.Max(1, query.Page);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        return new PagedNotificationsResponse
        {
            Items      = items.Select(MapToDto).ToList(),
            Page       = page,
            PageSize   = pageSize,
            TotalCount = total,
            TotalPages = totalPages,
            AppliedFilters = new AppliedFiltersDto
            {
                Status        = query.Status,
                Channel       = query.Channel,
                Provider      = query.Provider,
                Recipient     = query.Recipient,
                ProductKey    = query.ProductKey,
                From          = query.From,
                To            = query.To,
                SortBy        = query.SortBy,
                SortDirection = query.SortDirection,
            },
        };
    }

    // ─── Stats ────────────────────────────────────────────────────────────────

    public async Task<NotificationStatsDto> GetStatsAsync(Guid tenantId, NotificationStatsQuery query)
    {
        var data = await _notificationRepo.GetStatsAsync(tenantId, query);

        var queued = (data.StatusCounts.GetValueOrDefault("accepted", 0)
                    + data.StatusCounts.GetValueOrDefault("processing", 0)
                    + data.StatusCounts.GetValueOrDefault("retrying", 0));

        return new NotificationStatsDto
        {
            TotalCount        = data.TotalCount,
            QueuedCount       = queued,
            SentCount         = data.StatusCounts.GetValueOrDefault("sent", 0),
            DeliveredCount    = data.DeliveredCount,
            FailedCount       = data.StatusCounts.GetValueOrDefault("failed", 0)
                              + data.StatusCounts.GetValueOrDefault("dead-letter", 0),
            SuppressedCount   = data.StatusCounts.GetValueOrDefault("blocked", 0),
            PartialCount      = data.StatusCounts.GetValueOrDefault("partial", 0),
            ChannelBreakdown  = data.ChannelCounts,
            ProviderBreakdown = data.ProviderCounts,
            StatusDistribution = data.StatusCounts,
            RecentTrend       = data.Trend,
            AppliedFilters    = new AppliedFiltersDto
            {
                Channel    = query.Channel,
                Status     = query.Status,
                Provider   = query.Provider,
                ProductKey = query.ProductKey,
                From       = query.From,
                To         = query.To,
            },
        };
    }

    // ─── Events ───────────────────────────────────────────────────────────────

    public async Task<List<NotificationEventDto>> GetEventsAsync(Guid tenantId, Guid id)
    {
        var notification = await _notificationRepo.GetByIdAndTenantAsync(id, tenantId);
        if (notification == null) return new List<NotificationEventDto>();

        return await BuildEventTimelineAsync(notification);
    }

    // ─── Issues ───────────────────────────────────────────────────────────────

    public async Task<List<NotificationIssueDto>> GetIssuesAsync(Guid tenantId, Guid id)
    {
        // Verify tenant ownership
        var notification = await _notificationRepo.GetByIdAndTenantAsync(id, tenantId);
        if (notification == null) return new List<NotificationIssueDto>();

        var issues = await _deliveryIssueRepo.GetByNotificationIdAsync(id);
        return issues.Select(i => new NotificationIssueDto
        {
            Id                = i.Id,
            IssueType         = i.IssueType,
            Channel           = i.Channel,
            Provider          = string.IsNullOrEmpty(i.Provider) ? null : i.Provider,
            RecommendedAction = i.RecommendedAction,
            DetailsJson       = i.DetailsJson,
            IsResolved        = i.IsResolved,
            ResolvedAt        = i.ResolvedAt,
            CreatedAt         = i.CreatedAt,
        }).ToList();
    }

    // ─── Retry ────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> RetryableFailureCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "retryable_provider_failure",
        "provider_unavailable",
        "auth_config_failure",
    };

    public async Task<RetryResultDto?> RetryAsync(Guid tenantId, Guid id, string? actorUserId = null)
    {
        var notification = await _notificationRepo.GetByIdAndTenantAsync(id, tenantId);
        if (notification == null) return null;

        if (notification.Status != "failed")
            return new RetryResultDto
            {
                NotificationId = id,
                PreviousStatus = notification.Status,
                NewStatus      = notification.Status,
                FailureCategory = "not_retryable",
                LastErrorMessage = $"Notification is not in a retryable state (current status: {notification.Status})",
                RetriedAt      = DateTime.UtcNow,
            };

        if (!string.IsNullOrEmpty(notification.FailureCategory) &&
            !RetryableFailureCategories.Contains(notification.FailureCategory))
            return new RetryResultDto
            {
                NotificationId = id,
                PreviousStatus = notification.Status,
                NewStatus      = notification.Status,
                FailureCategory = notification.FailureCategory,
                LastErrorMessage = $"Failure category '{notification.FailureCategory}' is not retryable",
                RetriedAt      = DateTime.UtcNow,
            };

        var previousStatus = notification.Status;

        // Determine base attempt number from existing attempts
        var existingAttempts = await _attemptRepo.GetByNotificationIdAsync(id);
        var baseAttemptNumber = existingAttempts.Count;

        notification.Status = "processing";
        notification.FailureCategory = null;
        notification.LastErrorMessage = null;
        await _notificationRepo.UpdateAsync(notification);

        await ExecuteSendLoopAsync(tenantId, notification, baseAttemptNumber);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "notification.retry",
                Action       = "notification.retry",
                SourceSystem = "notifications",
                Description  = $"Operator-triggered retry for notification {id}; previous status: {previousStatus}; new status: {notification.Status}; actor: {actorUserId ?? "internal"}",
                Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString(), UserId = actorUserId }
            });
        }
        catch { /* audit best-effort */ }

        return new RetryResultDto
        {
            NotificationId   = id,
            PreviousStatus   = previousStatus,
            NewStatus        = notification.Status,
            ProviderUsed     = notification.ProviderUsed,
            FailureCategory  = notification.FailureCategory,
            LastErrorMessage = notification.LastErrorMessage,
            RetriedAt        = DateTime.UtcNow,
        };
    }

    // ─── Resend ───────────────────────────────────────────────────────────────

    public async Task<ResendResultDto?> ResendAsync(Guid tenantId, Guid id, string? actorUserId = null)
    {
        var original = await _notificationRepo.GetByIdAndTenantAsync(id, tenantId);
        if (original == null) return null;

        // Build metadata with resendOf link
        Dictionary<string, object?> metaDict = new();
        if (!string.IsNullOrEmpty(original.MetadataJson))
        {
            try { metaDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(original.MetadataJson) ?? new(); }
            catch { metaDict = new(); }
        }
        metaDict["resendOf"] = original.Id.ToString();
        var newMetaJson = JsonSerializer.Serialize(metaDict);

        // Rebuild original message/recipient from stored JSON
        object recipient;
        object message;
        try { recipient = JsonSerializer.Deserialize<object>(original.RecipientJson) ?? new(); } catch { recipient = new(); }
        try { message   = JsonSerializer.Deserialize<object>(original.MessageJson)   ?? new(); } catch { message   = new(); }

        var resendRequest = new SubmitNotificationDto
        {
            Channel        = original.Channel,
            Recipient      = recipient,
            Message        = message,
            Metadata       = JsonSerializer.Deserialize<object>(newMetaJson),
            IdempotencyKey = null,            // Force fresh dispatch
            TemplateKey    = original.TemplateKey,
            Severity       = original.Severity,
            Category       = original.Category,
        };

        var result = await SubmitAsync(tenantId, resendRequest);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "notification.resend",
                Action       = "notification.resend",
                SourceSystem = "notifications",
                Description  = $"Operator-triggered resend of notification {id}; new notification {result.Id}; actor: {actorUserId ?? "internal"}",
                Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString(), UserId = actorUserId }
            });
        }
        catch { /* audit best-effort */ }

        return new ResendResultDto
        {
            OriginalNotificationId = id,
            NewNotificationId      = result.Id,
            Status                 = result.Status,
            CreatedAt              = DateTime.UtcNow,
        };
    }

    // ─── Admin cross-tenant operations ───────────────────────────────────────

    public async Task<NotificationDto?> AdminGetByIdAsync(Guid notificationId, string actorUserId)
    {
        var n = await _notificationRepo.GetByIdAsync(notificationId);
        if (n == null) return null;
        return MapToDto(n);
    }

    public async Task<PagedNotificationsResponse> AdminListPagedAsync(Guid? tenantId, NotificationListQuery query, string actorUserId)
    {
        var query2 = new NotificationListQuery
        {
            Page          = query.Page == 0 ? 1 : query.Page,
            PageSize      = query.PageSize == 0 ? 50 : query.PageSize,
            Status        = query.Status,
            Channel       = query.Channel,
            Provider      = query.Provider,
            Recipient     = query.Recipient,
            ProductKey    = query.ProductKey,
            From          = query.From,
            To            = query.To,
            SortBy        = query.SortBy,
            SortDirection = query.SortDirection,
        };

        var (items, total) = await _notificationRepo.GetPagedAdminAsync(tenantId, query2);

        _logger.LogInformation(
            "Admin list query by {ActorUserId}: tenantFilter={TenantId} page={Page} pageSize={PageSize} total={Total}",
            actorUserId, tenantId?.ToString() ?? "ALL", query2.Page, query2.PageSize, total);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "admin.notification.list",
                Action       = "admin.notification.list",
                SourceSystem = "notifications",
                Description  = $"Admin list query by {actorUserId}; tenantFilter={tenantId?.ToString() ?? "ALL"}; total={total}",
                Scope        = new AuditEventScopeDto
                {
                    ScopeType = LegalSynq.AuditClient.Enums.ScopeType.Platform,
                    TenantId  = tenantId?.ToString(),
                    UserId    = actorUserId,
                }
            });
        }
        catch { /* audit best-effort */ }

        var pageSize = Math.Clamp(query2.PageSize, 1, 200);
        return new PagedNotificationsResponse
        {
            Items          = items.Select(MapToDto).ToList(),
            TotalCount     = total,
            Page           = query2.Page,
            PageSize       = pageSize,
            TotalPages     = (int)Math.Ceiling((double)total / pageSize),
            AppliedFilters = new AppliedFiltersDto
            {
                Status        = query2.Status,
                Channel       = query2.Channel,
                Provider      = query2.Provider,
                Recipient     = query2.Recipient,
                ProductKey    = query2.ProductKey,
                From          = query2.From,
                To            = query2.To,
                SortBy        = query2.SortBy,
                SortDirection = query2.SortDirection,
            },
        };
    }

    public async Task<NotificationStatsDto> AdminGetStatsAsync(Guid? tenantId, NotificationStatsQuery query, string actorUserId)
    {
        var data = await _notificationRepo.GetStatsAdminAsync(tenantId, query);

        _logger.LogInformation(
            "Admin stats query by {ActorUserId}: tenantFilter={TenantId}",
            actorUserId, tenantId?.ToString() ?? "ALL");

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "admin.notification.stats",
                Action       = "admin.notification.stats",
                SourceSystem = "notifications",
                Description  = $"Admin stats query by {actorUserId}; tenantFilter={tenantId?.ToString() ?? "ALL"}; total={data.TotalCount}",
                Scope        = new AuditEventScopeDto
                {
                    ScopeType = LegalSynq.AuditClient.Enums.ScopeType.Platform,
                    TenantId  = tenantId?.ToString(),
                    UserId    = actorUserId,
                }
            });
        }
        catch { /* audit best-effort */ }

        return BuildStatsDto(data, query);
    }

    public async Task<List<NotificationEventDto>> AdminGetEventsAsync(Guid notificationId, string actorUserId)
    {
        var notification = await _notificationRepo.GetByIdAsync(notificationId);
        if (notification == null) return new List<NotificationEventDto>();

        _logger.LogInformation(
            "Admin events lookup by {ActorUserId}: notificationId={NotificationId} tenantId={TenantId}",
            actorUserId, notificationId, notification.TenantId);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "admin.notification.events",
                Action       = "admin.notification.events",
                SourceSystem = "notifications",
                Description  = $"Admin events lookup by {actorUserId}; notificationId={notificationId}; tenantId={notification.TenantId}",
                Scope        = new AuditEventScopeDto
                {
                    ScopeType = LegalSynq.AuditClient.Enums.ScopeType.Platform,
                    TenantId  = notification.TenantId.ToString(),
                    UserId    = actorUserId,
                }
            });
        }
        catch { /* audit best-effort */ }

        return await BuildEventTimelineAsync(notification);
    }

    public async Task<List<NotificationIssueDto>> AdminGetIssuesAsync(Guid notificationId, string actorUserId)
    {
        var notification = await _notificationRepo.GetByIdAsync(notificationId);
        if (notification == null) return new List<NotificationIssueDto>();

        _logger.LogInformation(
            "Admin issues lookup by {ActorUserId}: notificationId={NotificationId} tenantId={TenantId}",
            actorUserId, notificationId, notification.TenantId);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "admin.notification.issues",
                Action       = "admin.notification.issues",
                SourceSystem = "notifications",
                Description  = $"Admin issues lookup by {actorUserId}; notificationId={notificationId}; tenantId={notification.TenantId}",
                Scope        = new AuditEventScopeDto
                {
                    ScopeType = LegalSynq.AuditClient.Enums.ScopeType.Platform,
                    TenantId  = notification.TenantId.ToString(),
                    UserId    = actorUserId,
                }
            });
        }
        catch { /* audit best-effort */ }

        var issues = await _deliveryIssueRepo.GetByNotificationIdAsync(notificationId);
        return issues.Select(i => new NotificationIssueDto
        {
            Id                = i.Id,
            IssueType         = i.IssueType,
            Channel           = i.Channel,
            Provider          = string.IsNullOrEmpty(i.Provider) ? null : i.Provider,
            RecommendedAction = i.RecommendedAction,
            DetailsJson       = i.DetailsJson,
            IsResolved        = i.IsResolved,
            ResolvedAt        = i.ResolvedAt,
            CreatedAt         = i.CreatedAt,
        }).ToList();
    }

    public async Task<RetryResultDto?> AdminRetryAsync(Guid notificationId, string actorUserId)
    {
        var notification = await _notificationRepo.GetByIdAsync(notificationId);
        if (notification == null) return null;

        var tenantId = notification.TenantId.GetValueOrDefault();

        if (notification.Status != "failed")
            return new RetryResultDto
            {
                NotificationId   = notificationId,
                PreviousStatus   = notification.Status,
                NewStatus        = notification.Status,
                FailureCategory  = "not_retryable",
                LastErrorMessage = $"Notification is not in a retryable state (current status: {notification.Status})",
                RetriedAt        = DateTime.UtcNow,
            };

        if (!string.IsNullOrEmpty(notification.FailureCategory) &&
            !RetryableFailureCategories.Contains(notification.FailureCategory))
            return new RetryResultDto
            {
                NotificationId   = notificationId,
                PreviousStatus   = notification.Status,
                NewStatus        = notification.Status,
                FailureCategory  = notification.FailureCategory,
                LastErrorMessage = $"Failure category '{notification.FailureCategory}' is not retryable",
                RetriedAt        = DateTime.UtcNow,
            };

        var previousStatus = notification.Status;
        var existingAttempts = await _attemptRepo.GetByNotificationIdAsync(notificationId);
        var baseAttemptNumber = existingAttempts.Count;

        notification.Status = "processing";
        notification.FailureCategory = null;
        notification.LastErrorMessage = null;
        await _notificationRepo.UpdateAsync(notification);

        await ExecuteSendLoopAsync(tenantId, notification, baseAttemptNumber);

        _logger.LogInformation(
            "Admin retry by {ActorUserId}: notificationId={NotificationId} tenantId={TenantId} previousStatus={Prev} newStatus={New}",
            actorUserId, notificationId, tenantId, previousStatus, notification.Status);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "admin.notification.retry",
                Action       = "admin.notification.retry",
                SourceSystem = "notifications",
                Description  = $"Admin retry by {actorUserId}; notificationId={notificationId}; tenantId={tenantId}; previousStatus={previousStatus}; newStatus={notification.Status}",
                Scope        = new AuditEventScopeDto
                {
                    ScopeType = LegalSynq.AuditClient.Enums.ScopeType.Platform,
                    TenantId  = tenantId.ToString(),
                    UserId    = actorUserId,
                }
            });
        }
        catch { /* audit best-effort */ }

        return new RetryResultDto
        {
            NotificationId   = notificationId,
            PreviousStatus   = previousStatus,
            NewStatus        = notification.Status,
            ProviderUsed     = notification.ProviderUsed,
            FailureCategory  = notification.FailureCategory,
            LastErrorMessage = notification.LastErrorMessage,
            RetriedAt        = DateTime.UtcNow,
        };
    }

    public async Task<ResendResultDto?> AdminResendAsync(Guid notificationId, string actorUserId)
    {
        var original = await _notificationRepo.GetByIdAsync(notificationId);
        if (original == null) return null;

        var tenantId = original.TenantId.GetValueOrDefault();

        Dictionary<string, object?> metaDict = new();
        if (!string.IsNullOrEmpty(original.MetadataJson))
        {
            try { metaDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(original.MetadataJson) ?? new(); }
            catch { metaDict = new(); }
        }
        metaDict["resendOf"]    = original.Id.ToString();
        metaDict["adminResend"] = actorUserId;
        var newMetaJson = JsonSerializer.Serialize(metaDict);

        object recipient;
        object message;
        try { recipient = JsonSerializer.Deserialize<object>(original.RecipientJson) ?? new(); } catch { recipient = new(); }
        try { message   = JsonSerializer.Deserialize<object>(original.MessageJson)   ?? new(); } catch { message   = new(); }

        var resendRequest = new SubmitNotificationDto
        {
            Channel        = original.Channel,
            Recipient      = recipient,
            Message        = message,
            Metadata       = JsonSerializer.Deserialize<object>(newMetaJson),
            IdempotencyKey = null,
            TemplateKey    = original.TemplateKey,
            Severity       = original.Severity,
            Category       = original.Category,
        };

        var result = await SubmitAsync(tenantId, resendRequest);

        _logger.LogInformation(
            "Admin resend by {ActorUserId}: originalId={OriginalId} tenantId={TenantId} newId={NewId}",
            actorUserId, notificationId, tenantId, result.Id);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "admin.notification.resend",
                Action       = "admin.notification.resend",
                SourceSystem = "notifications",
                Description  = $"Admin resend by {actorUserId}; originalId={notificationId}; tenantId={tenantId}; newId={result.Id}",
                Scope        = new AuditEventScopeDto
                {
                    ScopeType = LegalSynq.AuditClient.Enums.ScopeType.Platform,
                    TenantId  = tenantId.ToString(),
                    UserId    = actorUserId,
                }
            });
        }
        catch { /* audit best-effort */ }

        return new ResendResultDto
        {
            OriginalNotificationId = notificationId,
            NewNotificationId      = result.Id,
            Status                 = result.Status,
            CreatedAt              = DateTime.UtcNow,
        };
    }

    // ─── Shared helpers ───────────────────────────────────────────────────────

    private async Task<List<NotificationEventDto>> BuildEventTimelineAsync(Notification notification)
    {
        var events = new List<NotificationEventDto>();

        events.Add(new NotificationEventDto
        {
            Id          = notification.Id,
            EventType   = "created",
            Source      = "system",
            Timestamp   = notification.CreatedAt,
            Description = $"Notification accepted (channel={notification.Channel}, status={notification.Status})",
        });

        var attempts = await _attemptRepo.GetByNotificationIdAsync(notification.Id);
        foreach (var attempt in attempts)
        {
            events.Add(new NotificationEventDto
            {
                Id          = attempt.Id,
                EventType   = attempt.IsFailover ? "failover_attempted" : "attempted",
                Source      = "system",
                Timestamp   = attempt.CreatedAt,
                Description = $"Attempt #{attempt.AttemptNumber} via {attempt.Provider} — status: {attempt.Status}",
                Provider    = attempt.Provider,
                ProviderMessageId = attempt.ProviderMessageId,
            });

            if (attempt.CompletedAt.HasValue && attempt.Status != "sending")
            {
                events.Add(new NotificationEventDto
                {
                    Id          = attempt.Id,
                    EventType   = attempt.Status == "sent" ? "sent" : "attempt_failed",
                    Source      = "system",
                    Timestamp   = attempt.CompletedAt.Value,
                    Description = attempt.Status == "sent"
                        ? $"Sent via {attempt.Provider}"
                        : $"Attempt #{attempt.AttemptNumber} failed: {attempt.ErrorMessage ?? attempt.FailureCategory}",
                    Provider    = attempt.Provider,
                    ProviderMessageId = attempt.ProviderMessageId,
                });
            }
        }

        var providerEvents = await _eventRepo.GetByNotificationIdAsync(notification.Id);
        foreach (var evt in providerEvents)
        {
            events.Add(new NotificationEventDto
            {
                Id          = evt.Id,
                EventType   = evt.NormalizedEventType,
                Source      = $"provider:{evt.Provider}",
                Timestamp   = evt.EventTimestamp,
                Description = $"Provider event: {evt.RawEventType} (normalized: {evt.NormalizedEventType})",
                Provider    = evt.Provider,
                ProviderMessageId = evt.ProviderMessageId,
                MetadataJson = evt.MetadataJson,
            });
        }

        if (notification.Status is "blocked" or "failed" or "partial")
        {
            events.Add(new NotificationEventDto
            {
                Id          = notification.Id,
                EventType   = notification.Status,
                Source      = "system",
                Timestamp   = notification.UpdatedAt,
                Description = notification.LastErrorMessage
                    ?? $"Notification reached terminal status: {notification.Status}",
            });
        }

        return events.OrderBy(e => e.Timestamp).ThenBy(e => e.EventType).ToList();
    }

    private static NotificationStatsDto BuildStatsDto(NotificationStatsData data, NotificationStatsQuery? query = null)
    {
        var queued = data.StatusCounts.GetValueOrDefault("accepted", 0)
                   + data.StatusCounts.GetValueOrDefault("processing", 0)
                   + data.StatusCounts.GetValueOrDefault("retrying", 0);

        return new NotificationStatsDto
        {
            TotalCount         = data.TotalCount,
            QueuedCount        = queued,
            SentCount          = data.StatusCounts.GetValueOrDefault("sent", 0),
            DeliveredCount     = data.DeliveredCount,
            FailedCount        = data.StatusCounts.GetValueOrDefault("failed", 0)
                               + data.StatusCounts.GetValueOrDefault("dead-letter", 0),
            SuppressedCount    = data.StatusCounts.GetValueOrDefault("blocked", 0),
            PartialCount       = data.StatusCounts.GetValueOrDefault("partial", 0),
            ChannelBreakdown   = data.ChannelCounts,
            ProviderBreakdown  = data.ProviderCounts,
            StatusDistribution = data.StatusCounts,
            RecentTrend        = data.Trend,
            AppliedFilters     = new AppliedFiltersDto
            {
                Channel    = query?.Channel,
                Status     = query?.Status,
                Provider   = query?.Provider,
                ProductKey = query?.ProductKey,
                From       = query?.From,
                To         = query?.To,
            },
        };
    }

    // ─── Send loop (shared by initial dispatch and retry) ────────────────────

    private async Task ExecuteSendLoopAsync(Guid tenantId, Notification notification, int baseAttemptNumber = 0)
    {
        var contactValue = ExtractContactValue(notification.Channel, notification.RecipientJson);
        var routes = await _routingService.ResolveRoutesAsync(tenantId, notification.Channel);

        // LS-NOTIF-SMS-014: Apply intelligent routing engine for SMS channel.
        // The engine selects the best route from candidates and returns a decision.
        // We move the selected route to the front so the existing failover loop
        // still handles retries — no pipeline rewrite required.
        // Email routing is unchanged: SendGrid only, no routing engine applied.
        Guid? routingDecisionId = null;
        if (string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase) && routes.Count > 0)
        {
            try
            {
                var engineResult = await _smsRoutingEngine.SelectRouteAsync(new SmsRoutingRequest
                {
                    TenantId       = tenantId,
                    NotificationId = notification.Id,
                    CandidateRoutes = routes.AsReadOnly(),
                    // CountryCode and Region derivation not yet implemented (see LS-NOTIF-SMS-014 gap #3)
                }, CancellationToken.None);

                if (engineResult.Success && engineResult.SelectedRoute != null)
                {
                    // Reorder: move selected route to front so the send loop tries it first.
                    var selected = routes.FirstOrDefault(r =>
                        string.Equals(r.ProviderType, engineResult.SelectedProvider, StringComparison.OrdinalIgnoreCase) &&
                        r.TenantProviderConfigId == engineResult.SelectedProviderConfigId);
                    if (selected != null && routes.IndexOf(selected) != 0)
                    {
                        routes.Remove(selected);
                        routes.Insert(0, selected);
                    }
                }

                // Persist routing decision (best-effort — never blocks the send path)
                try
                {
                    var decision = new Notifications.Domain.SmsRoutingDecision
                    {
                        Id                       = Guid.CreateVersion7(),
                        TenantId                 = tenantId,
                        NotificationId           = notification.Id,
                        RoutingPolicyId          = engineResult.MatchedPolicyId,
                        RoutingMode              = engineResult.RoutingMode,
                        SelectedProvider         = engineResult.Success ? engineResult.SelectedProvider : "no_route",
                        SelectedProviderConfigId = engineResult.SelectedProviderConfigId,
                        ProviderOwnershipMode    = engineResult.ProviderOwnershipMode,
                        CandidateProvidersJson   = engineResult.CandidateProviders.Count > 0
                            ? System.Text.Json.JsonSerializer.Serialize(engineResult.CandidateProviders) : null,
                        ExcludedProvidersJson    = engineResult.ExcludedProviders.Count > 0
                            ? System.Text.Json.JsonSerializer.Serialize(engineResult.ExcludedProviders) : null,
                        DecisionReason           = engineResult.DecisionReason,
                        EstimatedCostAmount      = engineResult.EstimatedCostAmount,
                        CostCurrency             = engineResult.CostCurrency,
                        CountryCode              = engineResult.CountryCode,
                        Region                   = engineResult.Region,
                        // LS-NOTIF-SMS-015: Adaptive routing metadata
                        InferredCountryCode      = engineResult.InferredCountryCode,
                        InferredRegion           = engineResult.InferredRegion,
                        ProviderQualityScore     = engineResult.ProviderQualityScore,
                        AdaptiveScore            = engineResult.AdaptiveScore,
                        AdaptiveInputsJson       = engineResult.AdaptiveInputsJson,
                        CreatedAt                = DateTime.UtcNow,
                    };
                    var persisted = await _routingDecisionRepo.CreateAsync(decision);
                    routingDecisionId = persisted.Id;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SMS routing: failed to persist routing decision for notification {NotificationId} — send continues", notification.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SMS routing engine threw for notification {NotificationId} — falling back to priority order", notification.Id);
            }
        }

        // LS-NOTIF-SMS-017: Governance pre-send evaluation (SMS only).
        // Runs after routing selection but before the send loop.
        // Covers: quiet_hours, geographic_restriction, rate_limit, provider_governance.
        // Safe-degrades to allow on any error — never blocks delivery pipeline.
        if (string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase) &&
            _governanceService != null)
        {
            try
            {
                var govResult = await _governanceService.EvaluatePreSendAsync(
                    new SmsGovernanceEvaluationRequest
                    {
                        TenantId                = tenantId,
                        NotificationId          = notification.Id,
                        RecipientPhoneTransient = contactValue, // used transiently for country inference only
                        ProviderType            = routes.Count > 0 ? routes[0].ProviderType : null,
                        ProviderConfigId        = routes.Count > 0 ? routes[0].TenantProviderConfigId : null,
                        RetryCount              = notification.RetryCount,
                        IsRetry                 = false,
                        NowUtc                  = DateTime.UtcNow,
                    }, CancellationToken.None);

                if (govResult.ShouldBlock)
                {
                    notification.Status           = "dead-letter";
                    notification.FailureCategory  = $"governance_{govResult.ReasonCode}";
                    notification.LastErrorMessage = $"SMS governance blocked delivery: {govResult.PolicyType}/{govResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    await CreateDeadLetterIssueAsync(notification);
                    _logger.LogInformation(
                        "ExecuteSendLoopAsync: notification {Id} blocked by governance ({PolicyType}/{Reason})",
                        notification.Id, govResult.PolicyType, govResult.ReasonCode);
                    return;
                }
                else if (govResult.ShouldDelay || govResult.ShouldThrottle)
                {
                    notification.Status           = "retrying";
                    notification.NextRetryAt      = govResult.EffectiveAt ?? DateTime.UtcNow.AddMinutes(30);
                    notification.LastErrorMessage = $"SMS governance deferred delivery: {govResult.PolicyType}/{govResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    _logger.LogInformation(
                        "ExecuteSendLoopAsync: notification {Id} deferred by governance ({PolicyType}/{Reason}) until {Until}",
                        notification.Id, govResult.PolicyType, govResult.ReasonCode, notification.NextRetryAt);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Never crash the delivery pipeline on governance errors
                _logger.LogWarning(ex,
                    "ExecuteSendLoopAsync: governance pre-send evaluation threw for {Id} — defaulting to allow",
                    notification.Id);
            }
        }

        // LS-NOTIF-SMS-018: Template governance pre-send evaluation (SMS only).
        // Runs after LS-017 governance, before body extraction and provider execution.
        // Covers: template approval status, content classification, variable validation,
        //         prohibited-content enforcement, length limits.
        // Safe-degrades to allow on any error — never blocks delivery pipeline.
        if (string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase) &&
            _templateGovernanceService != null)
        {
            try
            {
                var tplResult = await _templateGovernanceService.EvaluateAsync(
                    new SmsTemplateGovernanceRequest
                    {
                        TenantId       = tenantId,
                        NotificationId = notification.Id,
                        TemplateKey    = notification.TemplateKey,
                        RenderedBody   = notification.RenderedBody,
                        IsRetry        = false,
                        RetryCount     = notification.RetryCount,
                        NowUtc         = DateTime.UtcNow,
                    }, CancellationToken.None);

                if (tplResult.ShouldBlock)
                {
                    notification.Status           = "dead-letter";
                    notification.FailureCategory  = $"template_governance_{tplResult.ReasonCode}";
                    notification.LastErrorMessage = $"SMS template governance blocked delivery: {tplResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    await CreateDeadLetterIssueAsync(notification);
                    _logger.LogInformation(
                        "ExecuteSendLoopAsync: notification {Id} blocked by template governance ({Decision}/{Reason})",
                        notification.Id, tplResult.DecisionType, tplResult.ReasonCode);
                    return;
                }
                else if (tplResult.DecisionType == "warn")
                {
                    _logger.LogWarning(
                        "ExecuteSendLoopAsync: template governance warn for notification {Id} ({Reason}) — proceeding",
                        notification.Id, tplResult.ReasonCode);
                }
            }
            catch (Exception ex)
            {
                // Never crash the delivery pipeline on template governance errors
                _logger.LogWarning(ex,
                    "ExecuteSendLoopAsync: template governance evaluation threw for {Id} — defaulting to allow",
                    notification.Id);
            }
        }

        string? subject = notification.RenderedSubject;
        // RenderedBody holds the HTML template output; RenderedText is the plain-text
        // alternative.  Swap from the old assignment so SendGrid receives the content
        // in the correct slots: Html → text/html, Body → text/plain.
        string? html    = notification.RenderedBody;
        string? body    = notification.RenderedText;
        var emailAttachments = string.Equals(notification.Channel, "email", StringComparison.OrdinalIgnoreCase)
            ? ExtractEmailAttachments(notification.MessageJson)
            : [];
        var disableClickTracking = string.Equals(notification.Channel, "email", StringComparison.OrdinalIgnoreCase) &&
                                   ExtractDisableClickTracking(notification.MessageJson);

        if (string.IsNullOrEmpty(subject) || (string.IsNullOrEmpty(html) && string.IsNullOrEmpty(body)))
        {
            try
            {
                var msg = JsonSerializer.Deserialize<JsonElement>(notification.MessageJson);
                subject ??= msg.TryGetProperty("subject", out var s) ? s.GetString() : "";
                if (string.IsNullOrEmpty(html))
                    html = msg.TryGetProperty("html", out var h) ? h.GetString() : null;
                if (string.IsNullOrEmpty(body))
                    body = msg.TryGetProperty("body", out var b) ? b.GetString() : "";
            }
            catch { /* use whatever we have */ }
        }

        // Ensure a text/plain part is always present — required by strict mail clients.
        // When the template only defines an HTML body, fall back to a minimal stub.
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(html))
            body = "Please view this email in an HTML-capable mail client.";

        // LS-NOTIF-SMS-025: Cross-channel governance enforcement for Email.
        // SMS channel has its own governance pipeline (LS-017–023) and is not evaluated here.
        // Evaluation runs once before the failover loop; PayloadTextForEvaluation is TRANSIENT.
        if (string.Equals(notification.Channel, "email", StringComparison.OrdinalIgnoreCase) &&
            _governanceRuntime != null)
        {
            try
            {
                var govCtx = new GovernanceExecutionContext
                {
                    NotificationId           = notification.Id,
                    TenantId                 = tenantId == Guid.Empty ? (Guid?)null : tenantId,
                    ChannelType              = notification.Channel,
                    TemplateId               = notification.TemplateId,
                    TemplateKey              = notification.TemplateKey,
                    SubjectMetadata          = subject,   // rendered subject — safe content label
                    PayloadTextForEvaluation = body,      // TRANSIENT — in-memory only, never persisted
                    EvaluationContext        = "delivery",
                    ExecutedAtUtc            = DateTime.UtcNow,
                };

                var govResult = await _governanceRuntime.EvaluateAsync(govCtx, CancellationToken.None);

                if (govResult.ShouldBlock || govResult.RequiresReview)
                {
                    notification.Status           = "dead-letter";
                    notification.FailureCategory  = $"governance_{govResult.ReasonCode}";
                    notification.LastErrorMessage = $"Email governance blocked delivery: {govResult.DecisionType}/{govResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    await CreateDeadLetterIssueAsync(notification);
                    _logger.LogInformation(
                        "ExecuteSendLoopAsync: email notification {Id} {Decision} by LS-025 governance ({Reason}) — aborting send",
                        notification.Id, govResult.DecisionType, govResult.ReasonCode);
                    return;
                }

                if (govResult.ShouldWarn)
                {
                    _logger.LogInformation(
                        "ExecuteSendLoopAsync: email notification {Id} governance warn ({Reason}) — proceeding with send",
                        notification.Id, govResult.ReasonCode);
                }
            }
            catch (Exception ex)
            {
                // Never crash the delivery pipeline on governance errors — fail open
                _logger.LogWarning(ex,
                    "ExecuteSendLoopAsync: LS-025 governance threw for email {Id} — defaulting to allow",
                    notification.Id);
            }
        }

        ProviderFailure? lastFailure = null;

        foreach (var route in routes)
        {
            var attemptNumber = baseAttemptNumber + routes.IndexOf(route) + 1;
            var attempt = await _attemptRepo.CreateAsync(new NotificationAttempt
            {
                TenantId            = tenantId,
                NotificationId      = notification.Id,
                Channel             = notification.Channel,
                Provider            = route.ProviderType,
                Status              = "sending",
                AttemptNumber       = attemptNumber,
                ProviderOwnershipMode = route.OwnershipMode,
                ProviderConfigId    = route.TenantProviderConfigId,
                IsFailover          = route.IsFailover
            });

            await _metering.MeterAsync(new MeterEventInput
            {
                TenantId = tenantId,
                UsageUnit = notification.Channel == "email" ? "email_attempt" : "sms_attempt",
                Channel = notification.Channel,
                NotificationId = notification.Id,
                NotificationAttemptId = attempt.Id,
                Provider = route.ProviderType,
                ProviderOwnershipMode = route.OwnershipMode,
                ProviderConfigId = route.TenantProviderConfigId
            });

            bool success;
            string? providerMessageId = null;
            ProviderFailure? failure = null;

            if (notification.Channel == "email")
            {
                var result = await _sendGridAdapter.SendAsync(new EmailSendPayload
                {
                    To = contactValue ?? "",
                    Subject = subject ?? "",
                    Body = body ?? "",
                    Html = html,
                    Attachments = emailAttachments,
                    DisableClickTracking = disableClickTracking,
                });
                success = result.Success;
                providerMessageId = result.ProviderMessageId;
                failure = result.Failure;
            }
            else
            {
                // LS-NOTIF-SMS-005: Resolve the correct tenant or platform Twilio adapter
                // using the config ID already set on this route by ProviderRoutingService.
                var runtimeCtx = await _smsRuntimeResolver.ResolveForSendAsync(
                    tenantId, route.ProviderType, route.TenantProviderConfigId);

                if (!runtimeCtx.Success)
                {
                    _logger.LogWarning(
                        "SMS send: provider runtime resolution failed for route {Provider}/{ConfigId}: {Code}",
                        route.ProviderType, route.TenantProviderConfigId, runtimeCtx.ErrorCode);
                    success = false;
                    failure = new ProviderFailure
                    {
                        Category  = runtimeCtx.ErrorCode ?? "provider_config_failure",
                        Message   = runtimeCtx.ErrorMessage ?? "SMS provider configuration failed",
                        Retryable = runtimeCtx.Retryable,
                    };
                }
                else
                {
                    var result = await runtimeCtx.Adapter!.SendAsync(new SmsSendPayload
                    {
                        To = contactValue ?? "", Body = body ?? ""
                    });
                    success = result.Success;
                    providerMessageId = result.ProviderMessageId;
                    failure = result.Failure;
                }
            }

            if (success)
            {
                attempt.Status = "sent";
                attempt.ProviderMessageId = providerMessageId;
                attempt.CompletedAt = DateTime.UtcNow;
                await _attemptRepo.UpdateAsync(attempt);

                // ── LS-NOTIF-SMS-013: record estimated SMS cost (best-effort, non-blocking) ──
                if (notification.Channel == "sms" && _costOptions.Enabled)
                {
                    try
                    {
                        var estimatedCost = _costOptions.GetEstimatedCost(route.ProviderType);
                        var costSource    = estimatedCost.HasValue ? "estimated" : "unavailable";
                        await _attemptRepo.UpdateCostAsync(
                            attempt.Id,
                            estimatedCostAmount: estimatedCost,
                            actualCostAmount:    null,
                            costCurrency:        _costOptions.DefaultCurrency,
                            costSource:          costSource,
                            costRecordedAt:      DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "LS-NOTIF-SMS-013: Cost recording failed for attempt {AttemptId} (non-fatal)",
                            attempt.Id);
                    }
                }
                // ── end cost recording ────────────────────────────────────────────────────────

                notification.Status              = "sent";
                notification.ProviderUsed        = route.ProviderType;
                notification.ProviderOwnershipMode = route.OwnershipMode;
                notification.ProviderConfigId    = route.TenantProviderConfigId;
                notification.PlatformFallbackUsed = route.IsPlatformFallback;
                await _notificationRepo.UpdateAsync(notification);

                await _metering.MeterAsync(new MeterEventInput
                {
                    TenantId = tenantId,
                    UsageUnit = notification.Channel == "email" ? "email_sent" : "sms_sent",
                    Channel = notification.Channel,
                    NotificationId = notification.Id,
                    NotificationAttemptId = attempt.Id,
                    Provider = route.ProviderType,
                    ProviderOwnershipMode = route.OwnershipMode
                });
                try
                {
                    await _auditClient.IngestAsync(new IngestAuditEventRequest
                    {
                        EventType    = "notification.sent",
                        Action       = "notification.sent",
                        SourceSystem = "notifications",
                        Outcome      = "success",
                        Description  = $"{notification.Channel} notification sent via {route.ProviderType} to {MaskRecipient(contactValue)}",
                        Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() },
                        Entity       = new AuditEventEntityDto { Type = "NOTIFICATION", Id = notification.Id.ToString() },
                        Metadata     = JsonSerializer.Serialize(new
                        {
                            notification_id     = notification.Id,
                            channel             = notification.Channel,
                            template_key        = notification.TemplateKey,
                            subject             = subject,
                            recipient           = MaskRecipient(contactValue),
                            provider            = route.ProviderType,
                            provider_message_id = providerMessageId,
                            attempt_number      = attemptNumber,
                        }),
                    });
                }
                catch { /* audit best-effort */ }
                return;
            }

            attempt.Status = "failed";
            attempt.FailureCategory = failure?.Category;
            attempt.ErrorMessage = failure?.Message;
            attempt.CompletedAt = DateTime.UtcNow;
            await _attemptRepo.UpdateAsync(attempt);

            lastFailure = failure;

            if (route.IsFailover)
                await _metering.MeterAsync(new MeterEventInput { TenantId = tenantId, UsageUnit = "provider_failover_attempt", Channel = notification.Channel, NotificationId = notification.Id, NotificationAttemptId = attempt.Id, Provider = route.ProviderType });

            if (failure?.Retryable != true) break;

            // Email has a single registered adapter (SendGrid); iterating additional routes
            // would call the same provider again. Exit the loop so the retry scheduler
            // handles backoff instead of retrying synchronously through the same adapter.
            if (string.Equals(notification.Channel, "email", StringComparison.OrdinalIgnoreCase)) break;
        }

        var isRetryable = routes.Count > 0;
        if (!isRetryable)
        {
            notification.Status = "failed";
            notification.FailureCategory = "auth_config_failure";
            notification.LastErrorMessage = "No provider routes configured";
            await _notificationRepo.UpdateAsync(notification);
            try
            {
                await _auditClient.IngestAsync(new IngestAuditEventRequest
                {
                    EventType    = "notification.failed",
                    Action       = "notification.failed",
                    SourceSystem = "notifications",
                    Outcome      = "failure",
                    Description  = $"{notification.Channel} notification failed — no provider routes configured",
                    Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() },
                    Entity       = new AuditEventEntityDto { Type = "NOTIFICATION", Id = notification.Id.ToString() },
                    Metadata     = JsonSerializer.Serialize(new
                    {
                        notification_id  = notification.Id,
                        channel          = notification.Channel,
                        template_key     = notification.TemplateKey,
                        subject          = subject,
                        recipient        = MaskRecipient(contactValue),
                        failure_category = "auth_config_failure",
                        failure_reason   = "No provider routes configured",
                    }),
                });
            }
            catch { /* audit best-effort */ }
        }
        else if (notification.RetryCount >= notification.MaxRetries)
        {
            notification.Status = "dead-letter";
            notification.FailureCategory = "max_retries_exhausted";
            notification.LastErrorMessage = $"Delivery failed after {notification.RetryCount} retries - all routes exhausted";
            await _notificationRepo.UpdateAsync(notification);
            await CreateDeadLetterIssueAsync(notification);
            try
            {
                await _auditClient.IngestAsync(new IngestAuditEventRequest
                {
                    EventType    = "notification.dead_letter",
                    Action       = "notification.dead_letter",
                    SourceSystem = "notifications",
                    Outcome      = "failure",
                    Description  = $"{notification.Channel} notification moved to dead-letter after {notification.RetryCount} retries",
                    Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() },
                    Entity       = new AuditEventEntityDto { Type = "NOTIFICATION", Id = notification.Id.ToString() },
                    Metadata     = JsonSerializer.Serialize(new
                    {
                        notification_id  = notification.Id,
                        channel          = notification.Channel,
                        template_key     = notification.TemplateKey,
                        subject          = subject,
                        recipient        = MaskRecipient(contactValue),
                        failure_category = "max_retries_exhausted",
                        retry_count      = notification.RetryCount,
                    }),
                });
            }
            catch { /* audit best-effort */ }
        }
        else if (lastFailure?.Retryable == false)
        {
            // Non-retryable provider failure (e.g. auth_config_failure, invalid_recipient).
            // Surface the actual failure category on the notification so operators can
            // diagnose on the detail page without reading raw logs.
            notification.Status = "failed";
            notification.FailureCategory = lastFailure.Category;
            notification.LastErrorMessage = lastFailure.Message ?? $"Non-retryable provider failure: {lastFailure.Category}";
            await _notificationRepo.UpdateAsync(notification);
            try
            {
                await _auditClient.IngestAsync(new IngestAuditEventRequest
                {
                    EventType    = "notification.failed",
                    Action       = "notification.failed",
                    SourceSystem = "notifications",
                    Outcome      = "failure",
                    Description  = $"{notification.Channel} notification failed: {lastFailure.Category}",
                    Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() },
                    Entity       = new AuditEventEntityDto { Type = "NOTIFICATION", Id = notification.Id.ToString() },
                    Metadata     = JsonSerializer.Serialize(new
                    {
                        notification_id  = notification.Id,
                        channel          = notification.Channel,
                        template_key     = notification.TemplateKey,
                        subject          = subject,
                        recipient        = MaskRecipient(contactValue),
                        failure_category = lastFailure.Category,
                        failure_message  = lastFailure.Message,
                    }),
                });
            }
            catch { /* audit best-effort */ }
        }
        else
        {
            notification.RetryCount++;
            notification.NextRetryAt = ComputeNextRetryAt(notification.RetryCount);
            notification.Status = "retrying";
            notification.FailureCategory = "retryable_provider_failure";
            notification.LastErrorMessage = $"All routes exhausted - retry #{notification.RetryCount} scheduled at {notification.NextRetryAt:u}";
            await _notificationRepo.UpdateAsync(notification);
            try
            {
                await _auditClient.IngestAsync(new IngestAuditEventRequest
                {
                    EventType    = "notification.retrying",
                    Action       = "notification.retrying",
                    SourceSystem = "notifications",
                    Description  = $"{notification.Channel} notification scheduled for retry #{notification.RetryCount}",
                    Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() },
                    Entity       = new AuditEventEntityDto { Type = "NOTIFICATION", Id = notification.Id.ToString() },
                    Metadata     = JsonSerializer.Serialize(new
                    {
                        notification_id  = notification.Id,
                        channel          = notification.Channel,
                        template_key     = notification.TemplateKey,
                        subject          = subject,
                        recipient        = MaskRecipient(contactValue),
                        failure_category = lastFailure?.Category,
                        retry_count      = notification.RetryCount,
                        next_retry_at    = notification.NextRetryAt,
                    }),
                });
            }
            catch { /* audit best-effort */ }
        }
    }

    /// <summary>
    /// Masks a recipient address (email or phone) for audit log storage.
    /// Shows enough to identify the domain/country code without exposing the full address.
    /// e.g. "john.doe@example.com" → "jo***@example.com", "+15551234567" → "+1***"
    /// </summary>
    private static string MaskRecipient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "***";

        var at = value.IndexOf('@');
        if (at > 0)
        {
            var local   = value[..at];
            var domain  = value[at..];
            var visible = local.Length > 2 ? local[..2] : local[..1];
            return visible + "***" + domain;
        }

        // Phone number: keep country code prefix only.
        return value.Length > 3 ? value[..3] + "***" : "***";
    }

    private static DateTime ComputeNextRetryAt(int retryCount) => retryCount switch
    {
        1 => DateTime.UtcNow.AddMinutes(1),
        2 => DateTime.UtcNow.AddMinutes(5),
        _ => DateTime.UtcNow.AddMinutes(30),
    };

    private async Task CreateDeadLetterIssueAsync(Notification notification)
    {
        try
        {
            await _deliveryIssueRepo.CreateIfNotExistsAsync(new DeliveryIssue
            {
                TenantId             = notification.TenantId.GetValueOrDefault(),
                NotificationId       = notification.Id,
                Channel              = notification.Channel,
                Provider             = notification.ProviderUsed ?? "unknown",
                IssueType            = "max_retries_exhausted",
                RecommendedAction    = "Notification exceeded maximum retry attempts. Manual intervention or resend required.",
                DetailsJson          = JsonSerializer.Serialize(new
                {
                    retryCount       = notification.RetryCount,
                    maxRetries       = notification.MaxRetries,
                    failureCategory  = notification.FailureCategory,
                    lastErrorMessage = notification.LastErrorMessage,
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create dead-letter issue for notification {Id}", notification.Id);
        }
    }

    // ─── Worker operations ────────────────────────────────────────────────────

    public async Task ProcessAutoRetryAsync(Guid notificationId)
    {
        var notification = await _notificationRepo.GetByIdAsync(notificationId);
        if (notification == null)
        {
            _logger.LogWarning("ProcessAutoRetryAsync: notification {Id} not found", notificationId);
            return;
        }

        if (notification.Status != "retrying")
        {
            _logger.LogWarning("ProcessAutoRetryAsync: notification {Id} is not in retrying status (current: {Status}) — skipping", notificationId, notification.Status);
            return;
        }

        var tenantId = notification.TenantId.GetValueOrDefault();
        var existingAttempts = await _attemptRepo.GetByNotificationIdAsync(notificationId);
        var baseAttemptNumber = existingAttempts.Count;

        notification.Status = "processing";
        notification.NextRetryAt = null;
        await _notificationRepo.UpdateAsync(notification);

        // LS-NOTIF-SMS-016: Recipient intelligence suppression check (SMS only).
        // Safe-degrades to allow on any error — never blocks delivery pipeline.
        if (string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase) &&
            _retrySuppressionService != null)
        {
            try
            {
                var phone = ExtractContactValue("sms", notification.RecipientJson);
                var suppressionResult = await _retrySuppressionService.EvaluateAsync(
                    new SmsRetrySuppressionRequest
                    {
                        RecipientPhone  = phone,
                        TenantId        = notification.TenantId,
                        NotificationId  = notification.Id,
                        ProviderType    = notification.ProviderUsed,
                        RetryCount      = notification.RetryCount,
                        FailureCategory = notification.FailureCategory,
                    }, CancellationToken.None);

                if (suppressionResult.ShouldBlock)
                {
                    notification.Status          = "dead-letter";
                    notification.FailureCategory = $"suppressed_{suppressionResult.ReasonCode}";
                    notification.LastErrorMessage = $"Retry suppressed by recipient intelligence: {suppressionResult.DecisionType} ({suppressionResult.ReasonCode})";
                    await _notificationRepo.UpdateAsync(notification);
                    await CreateDeadLetterIssueAsync(notification);
                    _logger.LogInformation(
                        "ProcessAutoRetryAsync: notification {Id} suppressed ({Decision}/{Reason}) — moved to dead-letter",
                        notificationId, suppressionResult.DecisionType, suppressionResult.ReasonCode);
                    return;
                }
                else if (suppressionResult.ShouldDefer)
                {
                    notification.Status       = "retrying";
                    notification.NextRetryAt  = DateTime.UtcNow.AddMinutes(30);
                    notification.LastErrorMessage = $"Retry deferred by recipient intelligence: {suppressionResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    _logger.LogInformation(
                        "ProcessAutoRetryAsync: notification {Id} deferred by soft-suppress ({Reason})",
                        notificationId, suppressionResult.ReasonCode);
                    return;
                }
                // warn or allow — proceed normally
                if (suppressionResult.DecisionType == "warn")
                {
                    _logger.LogWarning(
                        "ProcessAutoRetryAsync: suppression warn for notification {Id} ({Reason}) — proceeding",
                        notificationId, suppressionResult.ReasonCode);
                }
            }
            catch (Exception ex)
            {
                // Never crash the delivery pipeline on suppression errors
                _logger.LogWarning(ex,
                    "ProcessAutoRetryAsync: suppression evaluation threw for {Id} — defaulting to allow",
                    notificationId);
            }
        }

        // LS-NOTIF-SMS-017: Governance retry evaluation (SMS only).
        // Runs after LS-016 recipient intelligence suppression, before ExecuteSendLoopAsync.
        // Covers: quiet_hours, rate_limit, provider_governance, retry_governance.
        // Safe-degrades to allow on any error — never blocks delivery pipeline.
        if (string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase) &&
            _governanceService != null)
        {
            try
            {
                var phone = ExtractContactValue("sms", notification.RecipientJson);
                var govResult = await _governanceService.EvaluateRetryAsync(
                    new SmsGovernanceEvaluationRequest
                    {
                        TenantId                = notification.TenantId,
                        NotificationId          = notification.Id,
                        RecipientPhoneTransient = phone, // used transiently for country inference only
                        ProviderType            = notification.ProviderUsed,
                        RetryCount              = notification.RetryCount,
                        IsRetry                 = true,
                        NowUtc                  = DateTime.UtcNow,
                    }, CancellationToken.None);

                if (govResult.ShouldBlock)
                {
                    notification.Status           = "dead-letter";
                    notification.FailureCategory  = $"governance_{govResult.ReasonCode}";
                    notification.LastErrorMessage = $"SMS retry blocked by governance: {govResult.PolicyType}/{govResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    await CreateDeadLetterIssueAsync(notification);
                    _logger.LogInformation(
                        "ProcessAutoRetryAsync: notification {Id} blocked by governance ({PolicyType}/{Reason}) — moved to dead-letter",
                        notificationId, govResult.PolicyType, govResult.ReasonCode);
                    return;
                }
                else if (govResult.ShouldDelay || govResult.ShouldThrottle)
                {
                    notification.Status           = "retrying";
                    notification.NextRetryAt      = govResult.EffectiveAt ?? DateTime.UtcNow.AddMinutes(30);
                    notification.LastErrorMessage = $"SMS retry deferred by governance: {govResult.PolicyType}/{govResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    _logger.LogInformation(
                        "ProcessAutoRetryAsync: notification {Id} deferred by governance ({PolicyType}/{Reason}) until {Until}",
                        notificationId, govResult.PolicyType, govResult.ReasonCode, notification.NextRetryAt);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Never crash the delivery pipeline on governance errors
                _logger.LogWarning(ex,
                    "ProcessAutoRetryAsync: governance retry evaluation threw for {Id} — defaulting to allow",
                    notificationId);
            }
        }

        // LS-NOTIF-SMS-018: Template governance retry evaluation (SMS only).
        if (string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase) &&
            _templateGovernanceService != null)
        {
            try
            {
                var tplRetryResult = await _templateGovernanceService.EvaluateAsync(
                    new SmsTemplateGovernanceRequest
                    {
                        TenantId       = tenantId,
                        NotificationId = notification.Id,
                        TemplateKey    = notification.TemplateKey,
                        RenderedBody   = notification.RenderedBody,
                        IsRetry        = true,
                        RetryCount     = notification.RetryCount,
                        NowUtc         = DateTime.UtcNow,
                    }, CancellationToken.None);

                if (tplRetryResult.ShouldBlock)
                {
                    notification.Status           = "dead-letter";
                    notification.FailureCategory  = $"template_governance_{tplRetryResult.ReasonCode}";
                    notification.LastErrorMessage = $"SMS template governance blocked retry: {tplRetryResult.ReasonCode}";
                    await _notificationRepo.UpdateAsync(notification);
                    await CreateDeadLetterIssueAsync(notification);
                    _logger.LogInformation(
                        "ProcessAutoRetryAsync: notification {Id} blocked by template governance ({Decision}/{Reason}) — not retrying",
                        notificationId, tplRetryResult.DecisionType, tplRetryResult.ReasonCode);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ProcessAutoRetryAsync: template governance retry evaluation threw for {Id} — defaulting to allow",
                    notificationId);
            }
        }

        _logger.LogInformation("ProcessAutoRetryAsync: executing retry #{RetryCount} for notification {Id}", notification.RetryCount, notificationId);

        await ExecuteSendLoopAsync(tenantId, notification, baseAttemptNumber);

        try
        {
            await _auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType    = "notification.auto_retry",
                Action       = "notification.auto_retry",
                SourceSystem = "notifications",
                Description  = $"Worker auto-retry #{notification.RetryCount} for notification {notificationId}; result: {notification.Status}",
                Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() }
            });
        }
        catch { /* audit best-effort */ }
    }

    public async Task ReconcileStalledAsync()
    {
        var stalled = await _notificationRepo.GetStalledProcessingAsync(TimeSpan.FromMinutes(5), batchSize: 20);
        if (stalled.Count == 0) return;

        _logger.LogWarning("ReconcileStalledAsync: found {Count} stalled processing notifications", stalled.Count);

        foreach (var notification in stalled)
        {
            var tenantId = notification.TenantId.GetValueOrDefault();

            if (notification.RetryCount < notification.MaxRetries)
            {
                notification.RetryCount++;
                notification.NextRetryAt = ComputeNextRetryAt(notification.RetryCount);
                notification.Status = "retrying";
                notification.FailureCategory = "stalled_processing";
                notification.LastErrorMessage = $"Notification stalled in processing state — retry #{notification.RetryCount} scheduled";
                await _notificationRepo.UpdateAsync(notification);
                _logger.LogInformation("Stalled notification {Id} scheduled for retry #{Retry}", notification.Id, notification.RetryCount);
            }
            else
            {
                notification.Status = "dead-letter";
                notification.FailureCategory = "max_retries_exhausted";
                notification.LastErrorMessage = $"Delivery failed: stalled after {notification.RetryCount} retries";
                await _notificationRepo.UpdateAsync(notification);
                await CreateDeadLetterIssueAsync(notification);
                _logger.LogWarning("Stalled notification {Id} moved to dead-letter after {Retries} retries", notification.Id, notification.RetryCount);
            }

            try
            {
                await _auditClient.IngestAsync(new IngestAuditEventRequest
                {
                    EventType    = "notification.stalled_reconciled",
                    Action       = "notification.stalled_reconciled",
                    SourceSystem = "notifications",
                    Description  = $"Stalled notification {notification.Id} reconciled to status '{notification.Status}'",
                    Scope        = new AuditEventScopeDto { TenantId = tenantId.ToString() }
                });
            }
            catch { /* audit best-effort */ }
        }
    }

    // ─── Single dispatch ─────────────────────────────────────────────────────

    private async Task<NotificationResultDto> DispatchSingleAsync(Guid tenantId, SubmitNotificationDto request, string recipientJson)
    {
        var messageJson = JsonSerializer.Serialize(request.Message);

        // Prefer the top-level Subject field when the producer sets it explicitly.
        // Fall back to extracting "subject" from the Message payload for producers that
        // embed it there (e.g. CareConnect sends message: { subject, html }).
        string? inlineSubject = request.Subject;
        if (string.IsNullOrEmpty(inlineSubject))
        {
            try
            {
                var msgEl = JsonSerializer.Deserialize<JsonElement>(messageJson);
                if (msgEl.TryGetProperty("subject", out var subProp))
                    inlineSubject = subProp.GetString();
            }
            catch { /* best-effort; subject stays null */ }
        }

        // Merge canonical producer context fields into metadata (LS-NOTIF-CORE-020).
        // Producer-supplied metadata is preserved; canonical fields are added as
        // fallback keys so they never overwrite intentional metadata values.
        Dictionary<string, object?> metaDict;
        if (request.Metadata != null)
        {
            try
            {
                var raw = JsonSerializer.Serialize(request.Metadata);
                metaDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw) ?? new();
            }
            catch { metaDict = new(); }
        }
        else { metaDict = new(); }

        if (!string.IsNullOrEmpty(request.EventKey))      metaDict.TryAdd("eventKey",      request.EventKey);
        if (!string.IsNullOrEmpty(request.SourceSystem))  metaDict.TryAdd("sourceSystem",  request.SourceSystem);
        if (!string.IsNullOrEmpty(request.CorrelationId)) metaDict.TryAdd("correlationId", request.CorrelationId);
        if (!string.IsNullOrEmpty(request.RequestedBy))   metaDict.TryAdd("requestedBy",   request.RequestedBy);

        var metadataJson = metaDict.Count > 0 ? JsonSerializer.Serialize(metaDict) : null;

        if (!string.IsNullOrEmpty(request.IdempotencyKey))
        {
            var existing = await _notificationRepo.FindByIdempotencyKeyAsync(tenantId, request.IdempotencyKey);
            if (existing != null)
                return MapToResult(existing);
        }

        var rateCheck = await _usageEvaluation.CheckRequestAllowedAsync(tenantId, request.Channel);
        if (!rateCheck.Allowed)
        {
            await _metering.MeterAsync(new MeterEventInput { TenantId = tenantId, UsageUnit = "api_notification_request_rejected", Channel = request.Channel });
            return new NotificationResultDto { Status = "blocked", BlockedByPolicy = true, BlockedReasonCode = rateCheck.Code, LastErrorMessage = rateCheck.Reason };
        }

        var contactValue = ExtractContactValue(request.Channel, recipientJson);
        ContactEnforcementResult? enforcement = null;
        if (!string.IsNullOrEmpty(contactValue))
        {
            enforcement = await _contactEnforcement.EvaluateAsync(new ContactEnforcementInput
            {
                TenantId = tenantId, Channel = request.Channel, ContactValue = contactValue,
                OverrideSuppression = request.OverrideSuppression ?? false, OverrideReason = request.OverrideReason
            });
        }

        // Canonical product key: prefer ProductKey, fall back to legacy ProductType.
        var effectiveProductKey = request.ProductKey ?? request.ProductType;

        var notification = new Notification
        {
            TenantId = tenantId, Channel = request.Channel, Status = "accepted",
            RecipientJson = recipientJson, MessageJson = messageJson, MetadataJson = metadataJson,
            IdempotencyKey = request.IdempotencyKey, TemplateKey = request.TemplateKey,
            BlockedByPolicy = enforcement is { Allowed: false },
            BlockedReasonCode = enforcement is { Allowed: false } ? enforcement.ReasonCode : null,
            OverrideUsed = enforcement?.OverrideUsed ?? false,
            Severity = request.Severity,
            Category = request.Category ?? effectiveProductKey
        };

        if (enforcement is { Allowed: false })
        {
            notification.Status = "blocked";
            notification = await _notificationRepo.CreateAsync(notification);
            await _metering.MeterAsync(new MeterEventInput { TenantId = tenantId, UsageUnit = "suppressed_notification_request_rejected", Channel = request.Channel, NotificationId = notification.Id });
            return MapToResult(notification);
        }

        // Seed renderedSubject with the inline subject extracted from the message payload
        // so it is stored even when no template is involved (e.g. CareConnect pre-renders
        // its own HTML and passes subject/html directly).  Template rendering overwrites
        // this when a TemplateKey + TemplateData pair is present.
        string? renderedSubject = inlineSubject, renderedBody = null, renderedText = null;
        Guid? templateId = null, templateVersionId = null;

        if (!string.IsNullOrEmpty(request.TemplateKey) && request.TemplateData != null)
        {
            ResolvedTemplate? resolved;
            if (!string.IsNullOrEmpty(effectiveProductKey))
                resolved = await _templateResolution.ResolveByProductAsync(tenantId, request.TemplateKey, request.Channel, effectiveProductKey);
            else
                resolved = await _templateResolution.ResolveAsync(tenantId, request.TemplateKey, request.Channel);

            if (resolved != null)
            {
                templateId = resolved.Template.Id;
                templateVersionId = resolved.Version.Id;

                RenderResult rendered;
                if (request.BrandedRendering == true && !string.IsNullOrEmpty(effectiveProductKey))
                {
                    var branding = await _brandingResolution.ResolveAsync(tenantId, effectiveProductKey);
                    var tokens = _brandingResolution.BuildBrandingTokens(branding);
                    rendered = _templateRendering.RenderBranded(resolved.Version.SubjectTemplate, resolved.Version.BodyTemplate, resolved.Version.TextTemplate, request.TemplateData, tokens);
                }
                else
                {
                    rendered = _templateRendering.Render(resolved.Version.SubjectTemplate, resolved.Version.BodyTemplate, resolved.Version.TextTemplate, request.TemplateData);
                }

                renderedSubject = rendered.Subject;
                renderedBody    = rendered.Body;
                renderedText    = rendered.Text;

                await _metering.MeterAsync(new MeterEventInput { TenantId = tenantId, UsageUnit = "template_render", Channel = request.Channel, NotificationId = notification.Id });
            }
        }

        notification.TemplateId        = templateId;
        notification.TemplateVersionId = templateVersionId;
        notification.RenderedSubject   = renderedSubject;
        notification.RenderedBody      = renderedBody;
        notification.RenderedText      = renderedText;

        // In-app deliveries have no provider. Persist the operational row and
        // personal inbox projection atomically in one DbContext save.
        if (request.Channel == "in_app")
        {
            notification.Status = "sent";
            using var recipientDocument = JsonDocument.Parse(recipientJson);
            var recipientUserId = Guid.Parse(ReadRecipientField(recipientDocument.RootElement, "userId")!);
            try
            {
                await _userInboxService.CreateWithNotificationAsync(
                    notification,
                    recipientUserId,
                    effectiveProductKey!,
                    request.EventKey!,
                    request.InboxPresentation!);
            }
            catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var winner = await _notificationRepo.FindByIdempotencyKeyAsync(tenantId, request.IdempotencyKey);
                if (winner is not null) return MapToResult(winner);
                throw;
            }

            await _metering.MeterAsync(new MeterEventInput { TenantId = tenantId, UsageUnit = "api_notification_request", Channel = request.Channel, NotificationId = notification.Id });
            try { await _auditClient.IngestAsync(new IngestAuditEventRequest { EventType = "notification.sent", Action = "notification.sent", SourceSystem = "notifications", Description = "In-app notification persisted", Scope = new AuditEventScopeDto { TenantId = tenantId.ToString() } }); } catch { }
            return MapToResult(notification);
        }

        notification.Status = "processing";
        notification = await _notificationRepo.CreateAsync(notification);
        await _metering.MeterAsync(new MeterEventInput { TenantId = tenantId, UsageUnit = "api_notification_request", Channel = request.Channel, NotificationId = notification.Id });

        await ExecuteSendLoopAsync(tenantId, notification);
        return MapToResult(notification);
    }

    // ─── Fan-out helpers ──────────────────────────────────────────────────────

    private static string? ClassifySkipReason(string channel, ResolvedRecipient r)
    {
        var ch = channel?.Trim().ToLowerInvariant();
        return ch switch
        {
            "email"                 => string.IsNullOrWhiteSpace(r.Email)  ? "no_email_on_file"  : null,
            "sms"                   => string.IsNullOrWhiteSpace(r.Phone)  ? "no_phone_on_file"  : null,
            "push"                  => string.IsNullOrWhiteSpace(r.UserId) ? "no_user_for_push"  : null,
            "in_app"                 => string.IsNullOrWhiteSpace(r.UserId) ? "no_user_for_inapp" : null,
            _                       => null,
        };
    }

    private async Task<Notification> PersistFanOutParentAsync(
        Guid tenantId, SubmitNotificationDto request, string recipientJson, FanOutSummary summary)
    {
        Dictionary<string, object?> metaDict;
        if (request.Metadata != null)
        {
            try
            {
                var existing = JsonSerializer.Serialize(request.Metadata);
                metaDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(existing) ?? new();
            }
            catch { metaDict = new(); }
        }
        else { metaDict = new(); }

        var summaryJson = JsonSerializer.Serialize(summary, _camelCaseOptions);
        metaDict["fanout"] = JsonSerializer.Deserialize<JsonElement>(summaryJson);

        var status =
            summary.TotalResolved == 0                               ? "blocked" :
            summary.SentCount     == summary.TotalResolved           ? "sent"    :
            summary.SentCount     == 0 && summary.FailedCount == 0   ? "blocked" :
            summary.SentCount     == 0                               ? "failed"  :
                                                                       "partial";

        var parent = new Notification
        {
            TenantId      = tenantId,
            Channel       = request.Channel,
            Status        = status,
            RecipientJson = recipientJson,
            MessageJson   = JsonSerializer.Serialize(request.Message),
            MetadataJson  = JsonSerializer.Serialize(metaDict),
            IdempotencyKey = request.IdempotencyKey,
            TemplateKey   = request.TemplateKey,
            BlockedByPolicy = status == "blocked",
            BlockedReasonCode = summary.TotalResolved == 0 ? "recipient_set_empty" : null,
            LastErrorMessage = status == "sent"
                ? null
                : $"fanout: resolved={summary.TotalResolved} sent={summary.SentCount} " +
                  $"failed={summary.FailedCount} blocked={summary.BlockedCount} skipped={summary.SkippedCount}",
            Severity = request.Severity,
            Category = request.Category ?? request.ProductKey ?? request.ProductType,
        };
        return await _notificationRepo.CreateAsync(parent);
    }

    private static FanOutSummary BuildFanOutSummary(
        string? mode, string? roleKey, string? orgId, string channel,
        int totalResolved, List<FanOutPerRecipient> perRecipient)
    {
        var sent    = perRecipient.Count(p => p.Status == "sent");
        var failed  = perRecipient.Count(p => p.Status == "failed");
        var blocked = perRecipient.Count(p => p.Status == "blocked");
        var skipped = perRecipient.Count(p => p.Status == "skipped");

        var skippedByReason = perRecipient
            .Where(p => p.Status == "skipped" && !string.IsNullOrEmpty(p.Reason))
            .GroupBy(p => p.Reason!)
            .ToDictionary(g => g.Key, g => g.Count());

        var blockedByReason = perRecipient
            .Where(p => p.Status == "blocked" && !string.IsNullOrEmpty(p.Reason))
            .GroupBy(p => p.Reason!)
            .ToDictionary(g => g.Key, g => g.Count());

        return new FanOutSummary
        {
            Mode          = mode,
            RoleKey       = roleKey,
            OrgId         = orgId,
            Channel       = channel,
            TotalResolved = totalResolved,
            SentCount     = sent,
            FailedCount   = failed,
            BlockedCount  = blocked,
            SkippedCount  = skipped,
            DeliveredByChannel = sent > 0 ? new Dictionary<string, int> { [channel] = sent } : new(),
            SkippedByReason = skippedByReason,
            BlockedByReason = blockedByReason,
            Recipients    = perRecipient,
        };
    }

    private static NotificationResultDto BuildFanOutResult(
        Notification parent, FanOutSummary summary, List<NotificationResultDto> dispatched) => new()
    {
        Id = parent.Id,
        Status = parent.Status,
        ProviderUsed = dispatched.FirstOrDefault(r => !string.IsNullOrEmpty(r.ProviderUsed))?.ProviderUsed,
        PlatformFallbackUsed = dispatched.Any(r => r.PlatformFallbackUsed),
        BlockedByPolicy = parent.BlockedByPolicy || summary.BlockedCount > 0,
        BlockedReasonCode = parent.BlockedReasonCode
            ?? dispatched.FirstOrDefault(r => !string.IsNullOrEmpty(r.BlockedReasonCode))?.BlockedReasonCode,
        OverrideUsed = dispatched.Any(r => r.OverrideUsed),
        FailureCategory = dispatched.FirstOrDefault(r => !string.IsNullOrEmpty(r.FailureCategory))?.FailureCategory,
        LastErrorMessage = parent.LastErrorMessage,
    };

    private static string? ReadRecipientField(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        }
        return null;
    }

    private static SubmitNotificationDto ClonePerRecipient(SubmitNotificationDto src, ResolvedRecipient r)
    {
        var dict = new Dictionary<string, string?>
        {
            ["mode"] = !string.IsNullOrEmpty(r.UserId) ? "UserId" : "Email",
        };
        if (!string.IsNullOrEmpty(r.UserId)) dict["userId"] = r.UserId;
        if (!string.IsNullOrEmpty(r.Email))  dict["email"]  = r.Email;
        if (!string.IsNullOrEmpty(r.Phone))  dict["phone"]  = r.Phone;
        if (!string.IsNullOrEmpty(r.OrgId))  dict["orgId"]  = r.OrgId;

        return new SubmitNotificationDto
        {
            Channel        = src.Channel,
            Recipient      = dict,
            Message        = src.Message,
            Metadata       = src.Metadata,
            IdempotencyKey = string.IsNullOrEmpty(src.IdempotencyKey)
                ? null : $"{src.IdempotencyKey}:{r.StableKey}",
            TemplateKey    = src.TemplateKey,
            TemplateData   = src.TemplateData,
            ProductType    = src.ProductType,
            ProductKey     = src.ProductKey,
            EventKey       = src.EventKey,
            SourceSystem   = src.SourceSystem,
            CorrelationId  = src.CorrelationId,
            RequestedBy    = src.RequestedBy,
            Priority       = src.Priority,
            BrandedRendering  = src.BrandedRendering,
            OverrideSuppression = src.OverrideSuppression,
            OverrideReason = src.OverrideReason,
            Severity       = src.Severity,
            Category       = src.Category,
            InboxPresentation = src.InboxPresentation,
        };
    }

    private static string NormalizeChannel(string? channel)
    {
        var normalized = channel?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "in-app" or "inapp" or "in_app" ? "in_app" : normalized;
    }

    private static void ValidateInboxRequest(SubmitNotificationDto request, JsonElement recipient)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("idempotencyKey is required for in_app notifications.");
        if (request.IdempotencyKey.Length > 255)
            throw new ArgumentException("idempotencyKey cannot exceed 255 characters.");
        if (!string.Equals(request.ProductKey, "liens", StringComparison.Ordinal))
            throw new ArgumentException("productKey must be liens for the Selling inbox.");
        if (request.EventKey is not (
            "lien.offer.submitted" or
            "lien.offer.accepted" or
            "lien.offer.rejected" or
            "lien.offer.message.created"))
            throw new ArgumentException("eventKey is not supported by the Selling inbox.");
        if (!Guid.TryParse(ReadRecipientField(recipient, "userId"), out var recipientUserId) || recipientUserId == Guid.Empty)
            throw new ArgumentException("A concrete GUID recipient userId is required for in_app notifications.");

        var presentation = request.InboxPresentation
            ?? throw new ArgumentException("inboxPresentation is required for in_app notifications.");
        presentation.Category = (presentation.Category ?? string.Empty).Trim().ToLowerInvariant();
        presentation.Title = (presentation.Title ?? string.Empty).Trim();
        presentation.Description = (presentation.Description ?? string.Empty).Trim();
        presentation.SourceDisplayName = (presentation.SourceDisplayName ?? string.Empty).Trim();
        presentation.SourceInitials = (presentation.SourceInitials ?? string.Empty).Trim();

        if (presentation.Category is not ("lien" or "message"))
            throw new ArgumentException("inboxPresentation.category must be lien or message.");
        if (presentation.Title.Length is < 1 or > 160)
            throw new ArgumentException("inboxPresentation.title must be between 1 and 160 characters.");
        if (presentation.Description.Length is < 1 or > 500)
            throw new ArgumentException("inboxPresentation.description must be between 1 and 500 characters.");
        if (presentation.SourceDisplayName.Length is < 1 or > 160)
            throw new ArgumentException("inboxPresentation.sourceDisplayName must be between 1 and 160 characters.");
        if (presentation.SourceInitials.Length is < 1 or > 8)
            throw new ArgumentException("inboxPresentation.sourceInitials must be between 1 and 8 characters.");
        if (presentation.OccurredAtUtc == default)
            throw new ArgumentException("inboxPresentation.occurredAtUtc is required.");

        presentation.OccurredAtUtc = presentation.OccurredAtUtc.Kind switch
        {
            DateTimeKind.Utc => presentation.OccurredAtUtc,
            DateTimeKind.Local => presentation.OccurredAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(presentation.OccurredAtUtc, DateTimeKind.Utc),
        };
    }

    // ─── Fan-out nested types ─────────────────────────────────────────────────

    public sealed class FanOutSummary
    {
        public string? Mode { get; set; }
        public string? RoleKey { get; set; }
        public string? OrgId { get; set; }
        public string Channel { get; set; } = string.Empty;
        public int TotalResolved { get; set; }
        public int SentCount { get; set; }
        public int FailedCount { get; set; }
        public int BlockedCount { get; set; }
        public int SkippedCount { get; set; }
        public Dictionary<string, int> DeliveredByChannel { get; set; } = new();
        public Dictionary<string, int> SkippedByReason { get; set; } = new();
        public Dictionary<string, int> BlockedByReason { get; set; } = new();
        public List<FanOutPerRecipient> Recipients { get; set; } = new();
    }

    public sealed class FanOutPerRecipient
    {
        public string? UserId { get; set; }
        public string? Email { get; set; }
        public string? OrgId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public string? NotificationId { get; set; }
    }

    // ─── Mappers ──────────────────────────────────────────────────────────────

    private static NotificationResultDto MapToResult(Notification n) => new()
    {
        Id = n.Id, Status = n.Status, ProviderUsed = n.ProviderUsed,
        PlatformFallbackUsed = n.PlatformFallbackUsed, BlockedByPolicy = n.BlockedByPolicy,
        BlockedReasonCode = n.BlockedReasonCode, OverrideUsed = n.OverrideUsed,
        FailureCategory = n.FailureCategory, LastErrorMessage = n.LastErrorMessage
    };

    private static NotificationDto MapToDto(Notification n) => new()
    {
        Id = n.Id, TenantId = n.TenantId, Channel = n.Channel, Status = n.Status,
        RecipientJson = n.RecipientJson, MessageJson = n.MessageJson, MetadataJson = n.MetadataJson,
        IdempotencyKey = n.IdempotencyKey, ProviderUsed = n.ProviderUsed,
        FailureCategory = n.FailureCategory, LastErrorMessage = n.LastErrorMessage,
        TemplateId = n.TemplateId, TemplateVersionId = n.TemplateVersionId, TemplateKey = n.TemplateKey,
        RenderedSubject = n.RenderedSubject, RenderedBody = n.RenderedBody, RenderedText = n.RenderedText,
        ProviderOwnershipMode = n.ProviderOwnershipMode, ProviderConfigId = n.ProviderConfigId,
        PlatformFallbackUsed = n.PlatformFallbackUsed, BlockedByPolicy = n.BlockedByPolicy,
        BlockedReasonCode = n.BlockedReasonCode, OverrideUsed = n.OverrideUsed,
        Severity = n.Severity, Category = n.Category,
        CreatedAt = n.CreatedAt, UpdatedAt = n.UpdatedAt
    };

    private static string? ReadRecipientMode(JsonElement recipient)
    {
        if (recipient.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in recipient.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "mode", StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number when prop.Value.TryGetInt32(out var n) => n switch
                {
                    0 => "UserId",
                    1 => "Email",
                    2 => "Role",
                    3 => "Org",
                    _ => null,
                },
                _ => null,
            };
        }
        return null;
    }

    private static string? ExtractContactValue(string channel, string recipientJson)
    {
        try
        {
            var doc = JsonDocument.Parse(recipientJson);
            if (channel == "email") return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
            if (channel == "sms")   return doc.RootElement.TryGetProperty("phone", out var p) ? p.GetString() : null;
            return null;
        }
        catch { return null; }
    }

    private static List<EmailAttachmentPayload> ExtractEmailAttachments(string messageJson)
    {
        var attachments = new List<EmailAttachmentPayload>();

        if (string.IsNullOrWhiteSpace(messageJson))
            return attachments;

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (!doc.RootElement.TryGetProperty("attachments", out var rawAttachments) ||
                rawAttachments.ValueKind != JsonValueKind.Array)
            {
                return attachments;
            }

            foreach (var rawAttachment in rawAttachments.EnumerateArray())
            {
                if (rawAttachment.ValueKind != JsonValueKind.Object)
                    continue;

                var content = ReadStringProperty(rawAttachment, "content");
                var filename = ReadStringProperty(rawAttachment, "filename");

                if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(filename))
                    continue;

                attachments.Add(new EmailAttachmentPayload
                {
                    Content = content,
                    Filename = filename,
                    Type = ReadStringProperty(rawAttachment, "type") ?? "application/octet-stream",
                    Disposition = ReadStringProperty(rawAttachment, "disposition") ?? "attachment",
                    ContentId = ReadStringProperty(rawAttachment, "contentId") ??
                                ReadStringProperty(rawAttachment, "content_id"),
                });
            }
        }
        catch
        {
            return [];
        }

        return attachments;
    }

    private static bool ExtractDisableClickTracking(string messageJson)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (!TryGetProperty(doc.RootElement, "disableClickTracking", out var value))
                return false;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}
