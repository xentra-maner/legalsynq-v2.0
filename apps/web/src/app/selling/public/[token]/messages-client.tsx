"use client";

import { type FormEvent, type RefObject, useEffect, useMemo, useRef, useState } from "react";
import type { PublicBuyerPortalMessage } from "@/lib/liens/public-buyer-portal";
import { postPublicBuyerPortalMessage } from "@/lib/liens/public-buyer-portal-messages";

interface PublicPortalMessagesCardProps {
  token: string;
  audience: "buyer" | "seller";
  initialMessages?: PublicBuyerPortalMessage[];
}

const MAX_MESSAGE_LENGTH = 400;

export function PublicPortalMessagesCard({
  token,
  audience,
  initialMessages = [],
}: PublicPortalMessagesCardProps) {
  const [messages, setMessages] =
    useState<PublicBuyerPortalMessage[]>(initialMessages);
  const [draft, setDraft] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const threadScrollRef = useRef<HTMLDivElement | null>(null);
  const shouldScrollAfterSendRef = useRef(false);
  const trimmedDraft = draft.trim();
  const recipientLabel = audience === "seller" ? "buyer" : "seller";
  const emptyMessage = `No messages yet. Send a message to the ${recipientLabel} below.`;
  const canSend = trimmedDraft.length > 0 && !submitting;

  useEffect(() => {
    if (!shouldScrollAfterSendRef.current) return;

    shouldScrollAfterSendRef.current = false;
    const thread = threadScrollRef.current;
    if (!thread) return;

    if (typeof thread.scrollTo === "function") {
      thread.scrollTo({ top: thread.scrollHeight, behavior: "smooth" });
      return;
    }

    thread.scrollTop = thread.scrollHeight;
  }, [messages.length]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!canSend) return;

    setSubmitting(true);
    setError(null);
    const result = await postPublicBuyerPortalMessage(token, draft);
    setSubmitting(false);

    if (result.ok && result.message) {
      shouldScrollAfterSendRef.current = true;
      setMessages(current => [...current, result.message!]);
      setDraft("");
      return;
    }

    setError(result.error?.message ?? "The message could not be sent. Please try again.");
  }

  return (
    <details
      open
      className="public-portal-details group w-full max-w-[700px] rounded-2xl border border-[#e5e5e5] bg-white p-6 shadow-[0_1px_1.5px_rgba(0,0,0,0.1)] max-sm:rounded-[14px]"
      aria-labelledby="messages-title"
    >
      <summary className="-mx-2 flex min-h-10 cursor-pointer list-none items-center gap-3 rounded-lg px-2 py-1 transition-colors hover:bg-[#f5f5f5] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#ee7132] [&::-webkit-details-marker]:hidden">
        <i className="ri-arrow-down-s-line -rotate-90 text-2xl leading-none text-[#0a0a0a] transition-transform group-open:rotate-0" aria-hidden="true" />
        <h2 id="messages-title" className="m-0 text-lg font-extrabold leading-[1.6] tracking-normal">
          Messages
        </h2>
      </summary>
      <div className="details-content mt-6 flex flex-col gap-6">
        {messages.length === 0 ? (
          <EmptyState
            icon="ri-message-3-line"
            message={emptyMessage}
          />
        ) : (
          <MessageThread
            messages={messages}
            audience={audience}
            scrollContainerRef={threadScrollRef}
          />
        )}
        <form
          className="flex w-full items-center gap-4 rounded-xl border border-[#e5e5e5] py-3 pl-4 pr-3 shadow-[0_1px_2px_rgba(0,0,0,0.1)] transition-colors focus-within:border-[#ee7132]"
          onSubmit={handleSubmit}
        >
          <input
            aria-label="Message"
            placeholder="Type a message..."
            maxLength={MAX_MESSAGE_LENGTH}
            value={draft}
            onChange={event => {
              setDraft(event.target.value);
              if (error) setError(null);
            }}
            className="min-w-0 flex-1 border-0 text-sm text-[#737373] outline-none"
          />
          <span className="whitespace-nowrap text-sm text-[#737373]">{draft.length}/{MAX_MESSAGE_LENGTH}</span>
          <button
            type="submit"
            aria-label="Send message"
            disabled={!canSend}
            className="flex h-9 w-9 cursor-pointer items-center justify-center rounded-full border-0 bg-[#ee7132] text-white shadow-[0_1px_2px_rgba(0,0,0,0.1)] transition-colors hover:bg-[#d85f25] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#ee7132] active:bg-[#c95720] disabled:cursor-not-allowed disabled:opacity-60"
          >
            <i
              className={
                submitting
                  ? "ri-loader-4-line animate-spin text-base leading-none"
                  : "ri-send-plane-2-line text-base leading-none"
              }
              aria-hidden="true"
            />
          </button>
        </form>
        {error ? (
          <p role="alert" className="m-0 text-sm font-semibold leading-[1.6] text-red-600">
            {error}
          </p>
        ) : null}
      </div>
    </details>
  );
}

function MessageThread({
  messages,
  audience,
  scrollContainerRef,
}: {
  messages: PublicBuyerPortalMessage[];
  audience: "buyer" | "seller";
  scrollContainerRef: RefObject<HTMLDivElement>;
}) {
  const sortedMessages = useMemo(
    () =>
      [...messages].sort(
        (a, b) =>
          new Date(a.createdAtUtc).getTime() -
          new Date(b.createdAtUtc).getTime(),
      ),
    [messages],
  );

  return (
    <div
      ref={scrollContainerRef}
      className="flex max-h-[360px] flex-col gap-3 overflow-y-auto pr-1"
      aria-label="Message thread"
    >
      {sortedMessages.map(message => {
        const isCurrentAudience = message.senderType === audience;
        return (
          <article
            key={message.id}
            className={`flex ${isCurrentAudience ? "justify-end" : "justify-start"}`}
          >
            <div className={`max-w-[82%] rounded-2xl px-4 py-3 text-sm leading-[1.6] shadow-[0_1px_2px_rgba(0,0,0,0.08)] max-sm:max-w-[92%] ${
              isCurrentAudience
                ? "bg-[#ee7132] text-white"
                : "border border-[#e5e5e5] bg-[#fafafa] text-[#0a0a0a]"
            }`}>
              <div className={`mb-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs font-semibold leading-[1.4] ${
                isCurrentAudience ? "text-white/85" : "text-[#737373]"
              }`}>
                <span>{isCurrentAudience ? "You" : message.senderName}</span>
                <span aria-hidden="true">&middot;</span>
                <time dateTime={message.createdAtUtc}>{formatMessageTime(message.createdAtUtc)}</time>
              </div>
              <p className="m-0 whitespace-pre-wrap break-words">{message.message}</p>
            </div>
          </article>
        );
      })}
    </div>
  );
}

function EmptyState({ icon, message }: { icon: string; message: string }) {
  return (
    <div className="flex flex-col items-center gap-4 py-10 text-center text-sm leading-[1.6] text-[#737373]">
      <span className="flex h-14 w-14 items-center justify-center rounded-xl bg-[#f5f5f5] text-[#333]">
        <i className={`${icon} text-2xl leading-none`} aria-hidden="true" />
      </span>
      <p className="m-0">{message}</p>
    </div>
  );
}

function formatMessageTime(value: string) {
  const date = new Date(normalizeUtcTimestamp(value));
  if (Number.isNaN(date.getTime())) return "";

  return new Intl.DateTimeFormat("en-US", {
    month: "long",
    day: "numeric",
    year: "numeric",
    hour: "numeric",
    minute: "2-digit",
  }).format(date);
}

function normalizeUtcTimestamp(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return trimmed;
  return /(?:Z|[+-]\d{2}:?\d{2})$/i.test(trimmed) ? trimmed : `${trimmed}Z`;
}

function formatFileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(0)} KB`;
  return `${(kb / 1024).toFixed(1)} MB`;
}

function fileIconFor(fileName: string) {
  const extension = fileName.split(".").pop()?.toLowerCase();
  if (extension === "pdf") return "ri-file-pdf-2-line";
  if (["jpg", "jpeg", "png"].includes(extension ?? "")) return "ri-image-line";
  if (["xlsx", "xls", "csv"].includes(extension ?? "")) return "ri-file-excel-2-line";
  if (extension === "docx") return "ri-file-word-2-line";
  return "ri-file-line";
}
