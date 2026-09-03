import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, test, vi } from "vitest";
import { postPublicBuyerPortalMessage } from "@/lib/liens/public-buyer-portal-messages";
import { PublicPortalMessagesCard } from "./messages-client";

vi.mock("@/lib/liens/public-buyer-portal-messages", () => ({
  postPublicBuyerPortalMessage: vi.fn(),
}));

const postPublicBuyerPortalMessageMock = vi.mocked(postPublicBuyerPortalMessage);
const scrollToMock = vi.fn();

describe("PublicPortalMessagesCard", () => {
  beforeEach(() => {
    postPublicBuyerPortalMessageMock.mockReset();
    scrollToMock.mockReset();
    Object.defineProperty(HTMLElement.prototype, "scrollTo", {
      configurable: true,
      value: scrollToMock,
    });
  });

  test("posts a buyer message and appends it to the thread", async () => {
    postPublicBuyerPortalMessageMock.mockResolvedValue({
      ok: true,
      status: 201,
      correlationId: "corr-message",
      message: {
        id: "message-1",
        senderType: "buyer",
        senderName: "Buyer Reviewer",
        senderEmail: "buyer@example.test",
        message: "Can you confirm the signed LOP is final?",
        createdAtUtc: "2026-07-28T12:30:00Z",
      },
    });

    render(
      <PublicPortalMessagesCard
        token="token-abc"
        audience="buyer"
        initialMessages={[]}
      />,
    );

    expect(scrollToMock).not.toHaveBeenCalled();

    await userEvent.type(
      screen.getByRole("textbox", { name: "Message" }),
      "Can you confirm the signed LOP is final?",
    );
    await userEvent.click(screen.getByRole("button", { name: "Send message" }));

    expect(postPublicBuyerPortalMessageMock).toHaveBeenCalledWith(
      "token-abc",
      "Can you confirm the signed LOP is final?",
    );
    await waitFor(() => {
      expect(screen.getByText("Can you confirm the signed LOP is final?")).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(scrollToMock).toHaveBeenCalledWith(
        expect.objectContaining({
          behavior: "smooth",
          top: expect.any(Number),
        }),
      );
    });
    expect(screen.getByRole("textbox", { name: "Message" })).toHaveValue("");
  });

  test("renders seller links with a composer and existing buyer messages", () => {
    render(
      <PublicPortalMessagesCard
        token="seller-token"
        audience="seller"
        initialMessages={[
          {
            id: "message-1",
            senderType: "buyer",
            senderName: "Buyer Reviewer",
            senderEmail: "buyer@example.test",
            message: "We are reviewing the lien package.",
            createdAtUtc: "2026-07-28T12:30:00Z",
          },
        ]}
      />,
    );

    expect(screen.getByRole("textbox", { name: "Message" })).toBeInTheDocument();
    expect(screen.getByText("Buyer Reviewer")).toBeInTheDocument();
    expect(screen.getByText("We are reviewing the lien package.")).toBeInTheDocument();
  });

  test("renders suffix-less UTC timestamps in the browser timezone", () => {
    const timestamp = "2026-07-28T12:30:00";
    const expectedTimestamp = new Intl.DateTimeFormat("en-US", {
      month: "long",
      day: "numeric",
      year: "numeric",
      hour: "numeric",
      minute: "2-digit",
    }).format(new Date(`${timestamp}Z`));

    render(
      <PublicPortalMessagesCard
        token="seller-token"
        audience="seller"
        initialMessages={[
          {
            id: "message-1",
            senderType: "buyer",
            senderName: "Buyer Reviewer",
            senderEmail: "buyer@example.test",
            message: "We are reviewing the lien package.",
            createdAtUtc: timestamp,
          },
        ]}
      />,
    );

    expect(screen.getByText(expectedTimestamp)).toBeInTheDocument();
  });

  test("shows the API error when sending fails", async () => {
    postPublicBuyerPortalMessageMock.mockResolvedValue({
      ok: false,
      status: 410,
      correlationId: null,
      error: {
        code: "expired",
        title: "Lien offer link expired",
        message: "This secure link has expired.",
      },
    });

    render(
      <PublicPortalMessagesCard
        token="expired-token"
        audience="seller"
        initialMessages={[]}
      />,
    );

    await userEvent.type(screen.getByRole("textbox", { name: "Message" }), "Following up.");
    await userEvent.click(screen.getByRole("button", { name: "Send message" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("This secure link has expired.");
  });
});
