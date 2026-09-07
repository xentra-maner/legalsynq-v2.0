namespace BuildingBlocks.Notifications;

/// <summary>
/// LS-NOTIF-CORE-022 — Canonical notification event key and template key registry.
///
/// <para>
/// <b>Event key format:</b> <c>&lt;domain&gt;.&lt;entity&gt;.&lt;action&gt;[.&lt;qualifier&gt;]</c><br />
/// All lowercase, dot-separated, stable. Not channel-specific, not template-specific.
/// </para>
///
/// <para>
/// <b>Template key format:</b> <c>&lt;purpose&gt;-&lt;channel&gt;[-&lt;variant&gt;]</c><br />
/// All lowercase, kebab-case, must end with a channel suffix (e.g. <c>-email</c>, <c>-sms</c>).
/// </para>
///
/// <para>
/// Governance rule: all new event keys and template keys MUST be registered here before use.
/// Adding a constant here is the single source of truth — the PR diff is the audit trail.
/// </para>
/// </summary>
public static class NotificationTaxonomy
{
    // ── Tenant registration domain ───────────────────────────────────────────

    public static class Tenant
    {
        public const string ProductKey   = "tenant";
        public const string SourceSystem = "tenant-service";

        public static class Events
        {
            public const string RegistrationSubmitted = "tenant.registration.submitted";
            public const string RegistrationDeclined  = "tenant.registration.declined";
        }
    }

    // ── Liens domain ──────────────────────────────────────────────────────────

    /// <summary>All canonical event keys produced by the Liens service.</summary>
    public static class Liens
    {
        public const string ProductKey    = "liens";
        public const string SourceSystem  = "liens-service";

        public static class Events
        {
            public const string OfferSubmitted       = "lien.offer.submitted";
            public const string SellingLienSubmitted = "lien.selling.submitted";
            public const string OfferAccepted        = "lien.offer.accepted";
            public const string OfferRejected        = "lien.offer.rejected";
            public const string OfferMessageCreated  = "lien.offer.message.created";
            public const string SaleFinalized        = "lien.sale.finalized";
            public const string SaleDocumentGenerated = "lien.sale.document.generated";
            public const string TaskAssigned         = "liens.task.assigned";
            public const string TaskReassigned       = "liens.task.reassigned";
        }

        public static class Templates
        {
            public const string OfferSubmittedEmail  = "lien-offer-submitted-email";
            public const string SellingLienSubmittedEmail = "lien-selling-submitted-email";
            public const string OfferAcceptedEmail   = "lien-offer-accepted-email";
            public const string OfferRejectedEmail   = "lien-offer-rejected-email";
            public const string SaleFinalizedEmail   = "lien-sale-finalized-email";
            public const string TaskAssignedEmail    = "lien-task-assigned-email";
        }
    }

    // ── CareConnect / Referral domain ─────────────────────────────────────────

    /// <summary>All canonical event keys produced by the CareConnect service (referral domain).</summary>
    public static class CareConnect
    {
        public const string ProductKey    = "careconnect";
        public const string SourceSystem  = "careconnect-service";

        public static class Events
        {
            public const string ReferralCreated          = "referral.created";
            public const string ReferralInviteResent     = "referral.invite.resent";
            public const string ReferralInviteRetry      = "referral.invite.retry";
            public const string ReferralAcceptedProvider = "referral.accepted.provider";
            public const string ReferralAcceptedReferrer = "referral.accepted.referrer";
            public const string ReferralAcceptedClient   = "referral.accepted.client";
            public const string ReferralDeclinedProvider = "referral.declined.provider";
            public const string ReferralDeclinedReferrer = "referral.declined.referrer";
            public const string ReferralCancelledProvider = "referral.cancelled.provider";
            public const string ReferralCancelledReferrer = "referral.cancelled.referrer";

            /// <summary>
            /// Generic fallback — should never be emitted in production.
            /// Present only to prevent null event keys on unmapped types.
            /// </summary>
            public const string FallbackGeneric          = "careconnect.notification";
        }

        public static class Templates
        {
            public const string ReferralCreatedEmail         = "referral-created-email";
            public const string ReferralInviteResentEmail    = "referral-invite-resent-email";
            public const string ReferralAcceptedProviderEmail = "referral-accepted-provider-email";
            public const string ReferralAcceptedReferrerEmail = "referral-accepted-referrer-email";
            public const string ReferralAcceptedClientEmail  = "referral-accepted-client-email";
            public const string ReferralDeclinedProviderEmail = "referral-declined-provider-email";
            public const string ReferralDeclinedReferrerEmail = "referral-declined-referrer-email";
            public const string ReferralCancelledEmail       = "referral-cancelled-email";
        }
    }

    // ── Comms domain ─────────────────────────────────────────────────────────

    /// <summary>All canonical event keys produced by the Comms service.</summary>
    public static class Comms
    {
        public const string ProductKey    = "comms";
        public const string SourceSystem  = "comms-service";

        public static class Events
        {
            /// <summary>An outbound email was submitted through the Comms service.</summary>
            public const string EmailOutbound     = "comms.email.outbound";

            /// <summary>
            /// An SLA alert was triggered. Append a dot-separated qualifier for the
            /// specific trigger type, e.g. <c>comms.sla.alert.breached</c>.
            /// Valid qualifiers: breached, approaching, escalated.
            /// </summary>
            public const string SlaAlertPrefix    = "comms.sla.alert";

            public const string SlaAlertBreached   = "comms.sla.alert.breached";
            public const string SlaAlertApproaching = "comms.sla.alert.approaching";
            public const string SlaAlertEscalated  = "comms.sla.alert.escalated";
        }

        public static class Templates
        {
            public const string OutboundEmail      = "comms-outbound-email";
            public const string SlaAlertInternal   = "comms-sla-alert-internal";
        }
    }

    // ── Reports domain ────────────────────────────────────────────────────────

    /// <summary>All canonical event keys produced by the Reports service.</summary>
    public static class Reports
    {
        public const string ProductKey    = "reports";
        public const string SourceSystem  = "reports-service";

        public static class Events
        {
            public const string DeliveryRequested = "report.delivery";
        }

        public static class Templates
        {
            public const string DeliveryEmail     = "report-delivery-email";
        }
    }

    // ── Flow domain ───────────────────────────────────────────────────────────

    /// <summary>
    /// All canonical event keys produced by the Flow service.
    /// These match the <c>IFlowEvent.EventKey</c> properties in <c>FlowEvents.cs</c>.
    /// </summary>
    public static class Flow
    {
        public const string ProductKey    = "flow";
        public const string SourceSystem  = "flow-service";

        public static class Events
        {
            public const string WorkflowCreated      = "flow.workflow.created";
            public const string WorkflowStateChanged = "flow.workflow.state_changed";
            public const string WorkflowCompleted    = "flow.workflow.completed";
            public const string TaskAssigned         = "flow.task.assigned";
            public const string TaskCompleted        = "flow.task.completed";
        }

        public static class Templates
        {
            public const string TaskAssignedEmail    = "flow-task-assigned-email";
            public const string TaskCompletedEmail   = "flow-task-completed-email";
        }
    }

    // ── Identity domain ───────────────────────────────────────────────────────

    /// <summary>
    /// Canonical event keys for the Identity service.
    /// NOTE: Identity currently sends via /internal/send-email and does NOT yet
    /// use these event keys. Migration tracked in LS-NOTIF-CORE-024.
    /// </summary>
    public static class Identity
    {
        public const string ProductKey    = "identity";
        public const string SourceSystem  = "identity-service";

        public static class Events
        {
            public const string UserInviteSent       = "identity.user.invite.sent";
            public const string UserPasswordReset     = "identity.user.password.reset";
            public const string UserPasswordChanged   = "identity.user.password.changed";
            public const string UserCreated           = "identity.user.created";
        }

        public static class Templates
        {
            public const string InviteEmail           = "identity-invite-email";
            public const string PasswordResetEmail    = "identity-password-reset-email";
        }
    }

    // ── Support domain ───────────────────────────────────────────────────────

    /// <summary>All canonical event keys produced by the Support service.</summary>
    public static class Support
    {
        public const string ProductKey   = "support";
        public const string SourceSystem = "support-service";

        public static class Events
        {
            public const string TicketCreated       = "support.ticket.created";
            public const string TicketAssigned      = "support.ticket.assigned";
            public const string TicketUpdated       = "support.ticket.updated";
            public const string TicketStatusChanged = "support.ticket.status_changed";
            public const string TicketCommentAdded  = "support.ticket.comment_added";
        }

        public static class Templates
        {
            public const string TicketCreatedEmail       = "support-ticket-created-email";
            public const string TicketAssignedEmail      = "support-ticket-assigned-email";
            public const string TicketUpdatedEmail       = "support-ticket-updated-email";
            public const string TicketStatusChangedEmail = "support-ticket-status-changed-email";
            public const string TicketCommentAddedEmail  = "support-ticket-comment-added-email";
        }
    }

    // ── Channel constants ─────────────────────────────────────────────────────

    /// <summary>Valid delivery channel values for <see cref="NotificationsProducerRequest.Channel"/>.</summary>
    public static class Channels
    {
        public const string Email    = "email";
        public const string Sms      = "sms";
        public const string InApp    = "in_app";
        public const string Push     = "push";
        public const string Event    = "event";
        public const string Internal = "internal";
    }

    // ── Priority constants ────────────────────────────────────────────────────

    /// <summary>Valid delivery priority hint values.</summary>
    public static class Priority
    {
        public const string Low      = "low";
        public const string Normal   = "normal";
        public const string High     = "high";
        public const string Critical = "critical";
    }
}
