using System.Net;
using System.Text.Json;
using Identity.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Identity.Tests;

public sealed class TenantRegistrationApprovedEmailTests
{
    [Fact]
    public async Task Approved_registration_uses_dedicated_copy_setup_link_and_logo()
    {
        var handler = new CaptureHandler();
        var client = new NotificationsEmailClient(
            new TestHttpClientFactory(handler),
            Options.Create(new NotificationsServiceOptions { BaseUrl = "https://notifications.example.test" }),
            NullLogger<NotificationsEmailClient>.Instance);

        var result = await client.SendTenantRegistrationApprovedEmailAsync(
            "jane@example.test",
            "Jane Doe",
            "Sterling Associates",
            "https://sterling.example.test/accept-invite?token=secret",
            48,
            Guid.Parse("10000000-0000-0000-0000-000000000001"));

        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.Equal("identity.tenant.registration.approved", root.GetProperty("eventKey").GetString());
        Assert.Equal("Your LegalSynq tenant application has been accepted", root.GetProperty("subject").GetString());

        var message = root.GetProperty("message");
        var html = message.GetProperty("html").GetString()!;
        Assert.Contains("Your tenant application has been accepted", html);
        Assert.Contains("Sterling Associates", html);
        Assert.Contains("Set up your account", html);
        Assert.Contains("We are now provisioning your LegalSynq tenant", html);
        Assert.DoesNotContain("tenant is ready", html);
        Assert.Contains("48&nbsp;hours", html);
        Assert.Contains("src=\"cid:legalsynq-logo\"", html);
        Assert.Contains("margin:0 auto 16px", html);
        var attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("legalsynq-logo", attachment.GetProperty("contentId").GetString());
        Assert.Equal("inline", attachment.GetProperty("disposition").GetString());
        Assert.Equal("image/png", attachment.GetProperty("type").GetString());
        Assert.NotEmpty(attachment.GetProperty("content").GetString()!);
        Assert.DoesNotContain("Or copy and paste this link", html);
        Assert.DoesNotContain("An administrator has invited you", html);
    }

    [Fact]
    public async Task CareConnect_law_firm_invite_uses_dedicated_copy_and_centered_logo()
    {
        var handler = new CaptureHandler();
        var client = new NotificationsEmailClient(
            new TestHttpClientFactory(handler),
            Options.Create(new NotificationsServiceOptions { BaseUrl = "https://notifications.example.test" }),
            NullLogger<NotificationsEmailClient>.Instance);

        var result = await client.SendCareConnectLawFirmInviteEmailAsync(
            "john@example.test",
            "John Doe",
            "https://firm.example.test/accept-invite?token=secret",
            Guid.Parse("10000000-0000-0000-0000-000000000001"));

        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.Equal("identity.careconnect.law_firm.invite.sent", root.GetProperty("eventKey").GetString());
        Assert.Equal("You've been invited to CareConnect", root.GetProperty("subject").GetString());

        var message = root.GetProperty("message");
        var html = message.GetProperty("html").GetString()!;
        Assert.Contains("You've been invited to CareConnect", html);
        Assert.Contains("LegalSynq CareConnect", html);
        Assert.Contains("Activate CareConnect account", html);
        Assert.Contains("src=\"cid:legalsynq-logo\"", html);
        Assert.Contains("margin:0 auto 16px", html);
        Assert.DoesNotContain("Or copy and paste this link", html);
        Assert.DoesNotContain("An administrator has invited you", html);
        var attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("legalsynq-logo", attachment.GetProperty("contentId").GetString());
        Assert.Equal("inline", attachment.GetProperty("disposition").GetString());
        Assert.Equal("image/png", attachment.GetProperty("type").GetString());
        Assert.NotEmpty(attachment.GetProperty("content").GetString()!);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"status\":\"sent\"}"),
            };
        }
    }
}
