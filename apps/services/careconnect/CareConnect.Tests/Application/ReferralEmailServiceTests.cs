// LSCC-005 / LSCC-005-01: Tests for ReferralEmailService — 4-part token generation/validation,
// version embedding, expiry, tampering, and round-trip correctness.
using System.Security.Cryptography;
using System.Text;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

/// <summary>
/// LSCC-005 / LSCC-005-01 — Verifies the referral view token contract:
///   - GenerateViewToken(referralId, tokenVersion) produces a valid, URL-safe Base64 token
///   - ValidateViewToken returns a ViewTokenValidationResult (ReferralId + TokenVersion) for a valid token
///   - ValidateViewToken returns null for expired tokens
///   - ValidateViewToken returns null for old 3-part tokens (LSCC-005-01: backward incompatible by design)
///   - ValidateViewToken returns null for tampered / malformed tokens
///   - Round-trip generate → validate is stable and preserves both fields
/// </summary>
public class ReferralEmailServiceTests
{
    private const string TestSecret  = "TEST-REFERRAL-SECRET-KEY-2026";
    private const string TestBaseUrl = "http://localhost:3000";

    // ── Factory ──────────────────────────────────────────────────────────────

    private static ReferralEmailService BuildService(string? secret = TestSecret)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferralToken:Secret"] = secret,
                ["AppBaseUrl"]           = TestBaseUrl,
            })
            .Build();

        var notifications = new Mock<INotificationRepository>();
        var producer      = new Mock<INotificationsProducer>();
        ILogger<ReferralEmailService> logger = NullLogger<ReferralEmailService>.Instance;

        return new ReferralEmailService(notifications.Object, producer.Object, config,
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object, logger);
    }

    // ── Token format ─────────────────────────────────────────────────────────

    [Fact]
    public void GenerateViewToken_ReturnsNonEmptyString()
    {
        var svc   = BuildService();
        var token = svc.GenerateViewToken(Guid.CreateVersion7(), tokenVersion: 1);
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void GenerateViewToken_IsUrlSafeBase64_NoReservedChars()
    {
        var svc   = BuildService();
        var token = svc.GenerateViewToken(Guid.CreateVersion7(), tokenVersion: 1);

        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
    }

    [Fact]
    public void GenerateViewToken_TwoCallsSameId_ProducesNonEmptyTokens()
    {
        // Each token has a fresh expiry timestamp — two calls seconds apart may produce
        // different tokens. Either way, both must be non-empty and round-trip correctly.
        var svc = BuildService();
        var id  = Guid.CreateVersion7();
        var t1  = svc.GenerateViewToken(id, tokenVersion: 1);
        var t2  = svc.GenerateViewToken(id, tokenVersion: 1);

        Assert.False(string.IsNullOrWhiteSpace(t1));
        Assert.False(string.IsNullOrWhiteSpace(t2));
    }

    [Fact]
    public void GenerateViewToken_DifferentVersions_ProduceDifferentTokens()
    {
        // LSCC-005-01: token version is embedded in the HMAC payload, so version 1 and
        // version 2 tokens for the same referral must differ.
        var svc = BuildService();
        var id  = Guid.CreateVersion7();
        var t1  = svc.GenerateViewToken(id, tokenVersion: 1);
        var t2  = svc.GenerateViewToken(id, tokenVersion: 2);

        Assert.NotEqual(t1, t2);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_Generate_Validate_ReturnsOriginalReferralId()
    {
        var svc        = BuildService();
        var referralId = Guid.CreateVersion7();
        var token      = svc.GenerateViewToken(referralId, tokenVersion: 1);
        var result     = svc.ValidateViewToken(token);

        Assert.NotNull(result);
        Assert.Equal(referralId, result!.ReferralId);
    }

    [Fact]
    public void RoundTrip_Generate_Validate_PreservesTokenVersion()
    {
        // LSCC-005-01: the token version must round-trip correctly so callers can
        // detect revoked tokens by comparing result.TokenVersion with referral.TokenVersion.
        var svc        = BuildService();
        var referralId = Guid.CreateVersion7();

        var token2  = svc.GenerateViewToken(referralId, tokenVersion: 2);
        var result2 = svc.ValidateViewToken(token2);
        Assert.NotNull(result2);
        Assert.Equal(2, result2!.TokenVersion);

        var token7  = svc.GenerateViewToken(referralId, tokenVersion: 7);
        var result7 = svc.ValidateViewToken(token7);
        Assert.NotNull(result7);
        Assert.Equal(7, result7!.TokenVersion);
    }

    [Fact]
    public void RoundTrip_MultipleIds_EachValidatesToCorrectId()
    {
        var svc = BuildService();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();

        foreach (var id in ids)
        {
            var token  = svc.GenerateViewToken(id, tokenVersion: 1);
            var result = svc.ValidateViewToken(token);
            Assert.NotNull(result);
            Assert.Equal(id, result!.ReferralId);
            Assert.Equal(1, result.TokenVersion);
        }
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateViewToken_ExpiredToken_ReturnsNull()
    {
        var svc        = BuildService();
        var referralId = Guid.CreateVersion7();
        const int tokenVersion = 1;

        // Craft an expired 4-part token using the same algorithm the service uses.
        var expiry      = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
        var payload     = $"{referralId}:{tokenVersion}:{expiry}";
        var keyBytes    = Encoding.UTF8.GetBytes(TestSecret);
        using var hmac  = new HMACSHA256(keyBytes);
        var sig         = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var raw         = $"{payload}:{sig}";
        var token       = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                              .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(svc.ValidateViewToken(token));
    }

    // ── Old 3-part token rejection (LSCC-005-01) ─────────────────────────────

    [Fact]
    public void ValidateViewToken_OldThreePartToken_ReturnsNull()
    {
        // LSCC-005-01: tokens from before the hardening upgrade (3-part format) must be
        // rejected without throwing — they lack the version field and parts.Length != 4.
        var referralId  = Guid.CreateVersion7();
        var expiry      = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var payload     = $"{referralId}:{expiry}";              // old 2-field payload
        var keyBytes    = Encoding.UTF8.GetBytes(TestSecret);
        using var hmac  = new HMACSHA256(keyBytes);
        var sig         = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var raw         = $"{payload}:{sig}";                    // 3 parts
        var token       = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                              .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var svc = BuildService();
        Assert.Null(svc.ValidateViewToken(token));
    }

    // ── Tampering ────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateViewToken_TamperedSignature_ReturnsNull()
    {
        var svc        = BuildService();
        var referralId = Guid.CreateVersion7();
        var token      = svc.GenerateViewToken(referralId, tokenVersion: 1);

        // Decode, replace the HMAC with an all-zeros hex string of the same length, re-encode.
        var padded  = token.Replace('-', '+').Replace('_', '/');
        var mod     = padded.Length % 4;
        if (mod != 0) padded += new string('=', 4 - mod);
        var raw      = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        var lastSep  = raw.LastIndexOf(':');
        var tampered_raw = raw[..(lastSep + 1)] + new string('0', raw.Length - lastSep - 1);
        var tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered_raw))
                           .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(svc.ValidateViewToken(tampered));
    }

    [Fact]
    public void ValidateViewToken_WrongSecret_ReturnsNull()
    {
        var svcA   = BuildService("SECRET-A");
        var svcB   = BuildService("SECRET-B");
        var id     = Guid.CreateVersion7();
        var token  = svcA.GenerateViewToken(id, tokenVersion: 1);  // signed with A
        var result = svcB.ValidateViewToken(token);                  // validated with B
        Assert.Null(result);
    }

    [Fact]
    public void ValidateViewToken_VersionTampered_ReturnsNull()
    {
        // LSCC-005-01: if an attacker modifies the version field in the token body,
        // the HMAC computed over the (modified) payload will not match the real signature.
        var svc        = BuildService();
        var referralId = Guid.CreateVersion7();
        var token      = svc.GenerateViewToken(referralId, tokenVersion: 1);

        // Decode and replace the version digit.
        var padded = token.Replace('-', '+').Replace('_', '/');
        var mod    = padded.Length % 4;
        if (mod != 0) padded += new string('=', 4 - mod);
        var raw    = Encoding.UTF8.GetString(Convert.FromBase64String(padded));

        // Format: referralId:version:expiry:sig — tamper the version (field index 1)
        var parts   = raw.Split(':');
        parts[1]    = "999";  // inject a different version
        var tampered_raw = string.Join(':', parts);
        var tampered     = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered_raw))
                               .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(svc.ValidateViewToken(tampered));
    }

    // ── Malformed inputs ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notbase64!!!")]
    [InlineData("dGVzdA==")]         // base64 for "test" — not a valid token structure
    [InlineData("aGVsbG8=")]         // "hello"
    public void ValidateViewToken_MalformedInput_ReturnsNull(string bad)
    {
        var svc = BuildService();
        Assert.Null(svc.ValidateViewToken(bad));
    }

    [Fact]
    public void ValidateViewToken_NullEquivalent_ReturnsNull()
    {
        var svc = BuildService();
        Assert.Null(svc.ValidateViewToken(string.Empty));
    }

    // ── Dev fallback ─────────────────────────────────────────────────────────

    [Fact]
    public void Service_NoSecretConfigured_InDevelopment_StillGeneratesValidTokens()
    {
        // CC2-INT-B03: When ReferralToken:Secret is absent but environment is Development,
        // the service falls back to the dev constant. Tokens must still round-trip correctly.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppBaseUrl"]             = TestBaseUrl,
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                // NOTE: ReferralToken:Secret intentionally omitted
            })
            .Build();

        var notifications = new Mock<INotificationRepository>();
        var producer      = new Mock<INotificationsProducer>();
        ILogger<ReferralEmailService> logger = NullLogger<ReferralEmailService>.Instance;

        var svc  = new ReferralEmailService(notifications.Object, producer.Object, config,
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object, logger);
        var id   = Guid.CreateVersion7();
        var tok  = svc.GenerateViewToken(id, tokenVersion: 1);
        var res  = svc.ValidateViewToken(tok);

        Assert.NotNull(res);
        Assert.Equal(id, res!.ReferralId);
        Assert.Equal(1, res.TokenVersion);
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_SerializesNotificationUpdates()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferralToken:Secret"] = TestSecret,
                ["AppBaseUrl"]           = TestBaseUrl,
            })
            .Build();

        var notifications = new Mock<INotificationRepository>(MockBehavior.Strict);
        var producer = new Mock<INotificationsProducer>(MockBehavior.Strict);
        var tenantClient = new Mock<ITenantServiceClient>(MockBehavior.Strict);
        var subdomainCache = new Mock<ITenantSubdomainCache>(MockBehavior.Strict);

        var submitCount = 0;
        var allSubmissionsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid _,
                string _,
                string _,
                string _,
                string _,
                string? _,
                string? _,
                CancellationToken _) =>
            {
                if (Interlocked.Increment(ref submitCount) == 2)
                    allSubmissionsStarted.TrySetResult();

                await allSubmissionsStarted.Task;
            });

        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var activeUpdates = 0;
        var overlappingUpdatesDetected = 0;

        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(async (CareConnectNotification _, CancellationToken _) =>
            {
                var current = Interlocked.Increment(ref activeUpdates);
                if (current > 1)
                    Interlocked.Exchange(ref overlappingUpdatesDetected, 1);

                await Task.Delay(25);
                Interlocked.Decrement(ref activeUpdates);
            });

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            config,
            tenantClient.Object,
            subdomainCache.Object,
            NullLogger<ReferralEmailService>.Instance);

        await service.SendNewReferralNotificationAsync(
            BuildReferral(referrerEmail: "referrer@example.com"),
            BuildProvider(),
            CancellationToken.None);

        Assert.Equal(0, overlappingUpdatesDetected);
        notifications.Verify(
            n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_UsesLegacyReferralLink_ForPendingProvider()
    {
        var notifications = new Mock<INotificationRepository>();
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CareConnectNotification? providerNotification = null;
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Callback<CareConnectNotification, CancellationToken>((notification, _) =>
            {
                if (notification.RecipientType == NotificationRecipientType.Provider)
                    providerNotification = notification;
            })
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        var provider = BuildProvider();

        await service.SendNewReferralNotificationAsync(referral, provider, CancellationToken.None);

        Assert.NotNull(providerNotification);
        Assert.Contains("/referrals/thread?token=", providerNotification!.Message);
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_UsesViewLink_ForActiveProvider()
    {
        var notifications = new Mock<INotificationRepository>();
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CareConnectNotification? providerNotification = null;
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Callback<CareConnectNotification, CancellationToken>((notification, _) =>
            {
                if (notification.RecipientType == NotificationRecipientType.Provider)
                    providerNotification = notification;
            })
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        var provider = BuildProvider();
        provider.LinkOrganization(Guid.CreateVersion7());

        await service.SendNewReferralNotificationAsync(referral, provider, CancellationToken.None);

        Assert.NotNull(providerNotification);
        Assert.Contains("/referrals/thread?token=", providerNotification!.Message);
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_IncludesFacilityAddress_WhenReferralHasFacility()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? providerHtmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, _, _, body, _, _, _) => providerHtmlBody = body)
            .Returns(Task.CompletedTask);

        var referral = BuildReferral();
        var provider = BuildProvider();
        var facility = Facility.Create(
            Guid.CreateVersion7(),
            name: "Test Clinic - North",
            addressLine1: "456 North Ave",
            city: "Henderson",
            state: "NV",
            postalCode: "89052",
            phone: null,
            isActive: true,
            createdByUserId: null);
        typeof(Referral).GetProperty(nameof(Referral.Facility))!.SetValue(referral, facility);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        await service.SendNewReferralNotificationAsync(referral, provider, CancellationToken.None);

        Assert.NotNull(providerHtmlBody);
        Assert.Contains("456 North Ave", providerHtmlBody);
        Assert.Contains("Henderson", providerHtmlBody);
        Assert.DoesNotContain("Test Clinic - North", providerHtmlBody);
        Assert.DoesNotContain("123 Main St", providerHtmlBody);
    }

    /// <summary>
    /// Regression test for a real bug: ReferralService.CreateAsync fires the new-referral email
    /// using the in-memory Referral straight out of Referral.Create() — its Facility AND Provider
    /// navigation properties are both null (never loaded from the DB), even though a separately
    /// loaded Provider is passed alongside it. Location resolution must use that explicit Provider
    /// parameter as a fallback rather than silently rendering no location at all.
    /// </summary>
    [Fact]
    public async Task SendNewReferralNotificationAsync_UnhydratedReferral_FallsBackToExplicitProviderParameter()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? providerHtmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, _, _, body, _, _, _) => providerHtmlBody = body)
            .Returns(Task.CompletedTask);

        // BuildReferral() returns a raw Referral.Create() result — Facility and Provider
        // navigation are both null here, exactly like the object CreateAsync passes in.
        var referral = BuildReferral();
        var provider = BuildProvider();

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        await service.SendNewReferralNotificationAsync(referral, provider, CancellationToken.None);

        Assert.NotNull(providerHtmlBody);
        Assert.Contains("123 Main St", providerHtmlBody);
        Assert.Contains("Las Vegas", providerHtmlBody);
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_IncludesReferralOrigination_InReferrerConfirmationEmail()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? referrerHtmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, toAddress, _, body, _, _, _) =>
                {
                    if (toAddress == "referrer@example.com")
                        referrerHtmlBody = body;
                })
            .Returns(Task.CompletedTask);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        SetReferralAttribution(referral, "Cam", "Perry");

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        await service.SendNewReferralNotificationAsync(referral, BuildProvider(), CancellationToken.None);

        Assert.NotNull(referrerHtmlBody);
        Assert.Contains("Referral Origination", referrerHtmlBody);
        Assert.Contains("Cam Perry", referrerHtmlBody);
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_DoesNotIncludeReferralOrigination_InProviderEmail()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? providerHtmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, toAddress, _, body, _, _, _) =>
                {
                    if (toAddress == "provider@clinic.com")
                        providerHtmlBody = body;
                })
            .Returns(Task.CompletedTask);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        SetReferralAttribution(referral, "Cam", "Perry");

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        await service.SendNewReferralNotificationAsync(referral, BuildProvider(), CancellationToken.None);

        Assert.NotNull(providerHtmlBody);
        Assert.DoesNotContain("Referral Origination", providerHtmlBody);
        Assert.DoesNotContain("Cam Perry", providerHtmlBody);
    }

    [Fact]
    public async Task SendNewReferralNotificationAsync_SameReferralDifferentProviders_SendsEachProviderAndReferrerEmail()
    {
        var seenDedupeKeys = new HashSet<string>(StringComparer.Ordinal);
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CareConnectNotification notification, CancellationToken _) =>
                notification.DedupeKey is null || seenDedupeKeys.Add(notification.DedupeKey));
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sentTo = new List<string>();
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, toAddress, _, _, _, _, _) => sentTo.Add(toAddress))
            .Returns(Task.CompletedTask);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");

        await service.SendNewReferralNotificationAsync(referral, BuildProvider("provider-one@clinic.com"), CancellationToken.None);
        await service.SendNewReferralNotificationAsync(referral, BuildProvider("provider-two@clinic.com"), CancellationToken.None);

        Assert.Equal(2, sentTo.Count(address => address == "referrer@example.com"));
        Assert.Contains("provider-one@clinic.com", sentTo);
        Assert.Contains("provider-two@clinic.com", sentTo);
    }

    [Fact]
    public async Task SendCommentNotificationAsync_MentionsAttachmentsWithoutDirectFileUrls()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? htmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, _, _, body, _, _, _) => htmlBody = body)
            .Returns(Task.CompletedTask);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        var comment = new ReferralComment
        {
            Id = Guid.CreateVersion7(),
            TenantId = referral.TenantId,
            ReferralId = referral.Id,
            SenderType = "provider",
            SenderName = "Test Clinic",
            Message = "Please review the attached scan.",
            CreatedAt = DateTime.UtcNow,
            Attachments =
            [
                ReferralAttachment.Create(
                    referral.TenantId,
                    referral.Id,
                    "scan.png",
                    "image/png",
                    2048,
                    externalDocumentId: "doc-message-1",
                    externalStorageProvider: AttachmentScope.Shared,
                    status: "Uploaded",
                    notes: null,
                    createdByUserId: null,
                    referralCommentId: Guid.CreateVersion7())
            ],
        };

        await service.SendCommentNotificationAsync(referral, comment, CancellationToken.None);

        Assert.NotNull(htmlBody);
        Assert.Contains("1 attachment included", htmlBody);
        Assert.Contains("scan.png", htmlBody);
        Assert.DoesNotContain("doc-message-1", htmlBody);
        Assert.DoesNotContain("/attachments/", htmlBody);
    }

    [Fact]
    public async Task SendCommentNotificationAsync_IncludesReferralOrigination_WhenSentToReferrer()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? htmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, _, _, body, _, _, _) => htmlBody = body)
            .Returns(Task.CompletedTask);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        SetReferralAttribution(referral, "Cam", "Perry");
        var comment = new ReferralComment
        {
            Id = Guid.CreateVersion7(),
            TenantId = referral.TenantId,
            ReferralId = referral.Id,
            SenderType = "provider",
            SenderName = "Test Clinic",
            Message = "Please review this update.",
            CreatedAt = DateTime.UtcNow,
        };

        await service.SendCommentNotificationAsync(referral, comment, CancellationToken.None);

        Assert.NotNull(htmlBody);
        Assert.Contains("Referral Origination", htmlBody);
        Assert.Contains("Cam Perry", htmlBody);
    }

    [Fact]
    public async Task SendCommentNotificationAsync_DoesNotIncludeReferralOrigination_WhenSentToProvider()
    {
        var notifications = new Mock<INotificationRepository>();
        notifications
            .Setup(n => n.TryAddWithDedupeAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        notifications
            .Setup(n => n.UpdateAsync(It.IsAny<CareConnectNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? htmlBody = null;
        var producer = new Mock<INotificationsProducer>();
        producer
            .Setup(p => p.SubmitAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, string, string, string?, string?, CancellationToken>(
                (_, _, _, _, body, _, _, _) => htmlBody = body)
            .Returns(Task.CompletedTask);

        var service = new ReferralEmailService(
            notifications.Object,
            producer.Object,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReferralToken:Secret"] = TestSecret,
                    ["AppBaseUrl"] = TestBaseUrl,
                })
                .Build(),
            new Mock<ITenantServiceClient>().Object,
            new Mock<ITenantSubdomainCache>().Object,
            NullLogger<ReferralEmailService>.Instance);

        var referral = BuildReferral(referrerEmail: "referrer@example.com");
        SetReferralAttribution(referral, "Cam", "Perry");
        SetReferralProvider(referral, BuildProvider());
        var comment = new ReferralComment
        {
            Id = Guid.CreateVersion7(),
            TenantId = referral.TenantId,
            ReferralId = referral.Id,
            SenderType = "referrer",
            SenderName = "Referrer",
            Message = "Please review this update.",
            CreatedAt = DateTime.UtcNow,
        };

        await service.SendCommentNotificationAsync(referral, comment, CancellationToken.None);

        Assert.NotNull(htmlBody);
        Assert.DoesNotContain("Referral Origination", htmlBody);
        Assert.DoesNotContain("Cam Perry", htmlBody);
    }

    private static Referral BuildReferral(string? referrerEmail = null)
        => Referral.Create(
            tenantId:                   Guid.CreateVersion7(),
            referringOrganizationId:    null,
            receivingOrganizationId:    null,
            providerId:                 Guid.CreateVersion7(),
            subjectPartyId:             null,
            subjectNameSnapshot:        null,
            subjectDobSnapshot:         null,
            clientFirstName:            "Jane",
            clientLastName:             "Doe",
            clientDob:                  null,
            clientPhone:                "555-000-0001",
            clientEmail:                "client@example.com",
            caseNumber:                 null,
            requestedService:           "Physical Therapy",
            urgency:                    Referral.ValidUrgencies.Normal,
            notes:                      null,
            createdByUserId:            null,
            organizationRelationshipId: null,
            referrerEmail:              referrerEmail,
            referrerName:               "Referrer");

    private static void SetReferralAttribution(Referral referral, string firstName, string lastName)
    {
        var attribution = ReferralAttribution.Create(
            referral.TenantId,
            firstName,
            lastName,
            $"{firstName}_{lastName}".ToUpperInvariant(),
            null,
            true,
            null,
            null);

        typeof(Referral).GetProperty(nameof(Referral.ReferralAttribution))!.SetValue(referral, attribution);
    }

    private static void SetReferralProvider(Referral referral, Provider provider)
        => typeof(Referral).GetProperty(nameof(Referral.Provider))!.SetValue(referral, provider);

    private static Provider BuildProvider(string email = "provider@clinic.com")
        => Provider.Create(
            tenantId:           Guid.CreateVersion7(),
            name:               "Test Clinic",
            organizationName:   "Test Clinic LLC",
            email:              email,
            phone:              "555-000-9999",
            addressLine1:       "123 Main St",
            city:               "Las Vegas",
            state:              "NV",
            postalCode:         "89101",
            isActive:           true,
            acceptingReferrals: true,
            createdByUserId:    null);
}
