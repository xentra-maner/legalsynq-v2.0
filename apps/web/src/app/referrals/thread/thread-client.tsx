"use client";

import { useState, useEffect, useTransition, useRef, useCallback } from "react";
import {
  acceptReferralByToken,
  declineReferralByToken,
  completeReferralByToken,
  cancelReferralByToken,
} from "./actions";
import { postPublicThreadComment } from "../lib/public-thread-comments";
import {
  CARECONNECT_MESSAGE_ALLOWED_TYPES,
  CARECONNECT_MESSAGE_MAX_FILES,
  formatCareConnectAttachmentBytes,
  makeSelectedCareConnectMessageFiles,
  type SelectedCareConnectMessageFile,
} from "@/lib/careconnect-message-attachments";
import type { ReferralMessageAttachment } from "@/types/careconnect";
import { formatReferralLocation } from "@/lib/referral-location";

interface Comment {
  id: string;
  senderType: string;
  senderName: string;
  message: string;
  createdAtUtc: string;
  attachments?: ReferralMessageAttachment[];
}

interface Attachment {
  id: string;
  fileName: string;
  contentType: string;
  fileSizeBytes: number;
}

interface ThreadData {
  referralId: string;
  status: string;
  // Patient
  clientName: string;
  clientPhone: string | null;
  clientEmail: string | null;
  clientDob: string | null;
  caseNumber: string | null;
  // Referral
  service: string;
  urgency: string | null;
  notes: string | null;
  dateOfAccident?: string;
  treatmentTypeId?: string;
  treatmentTypeName?: string;
  lienCompanyName?: string | null;
  lienCompanyEmail?: string | null;
  providerName: string;
  // Referral location — the specific facility this referral was routed to, falling back
  // to the provider's own address for legacy/single-location referrals.
  facilityName?: string | null;
  locationAddressLine1?: string;
  locationCity?: string;
  locationState?: string;
  locationPostalCode?: string;
  // Law firm / referrer
  referrerFirmName?: string | null;
  referrerName: string | null;
  referrerEmail: string | null;
  createdAtUtc: string;
  comments: Comment[];
  attachments: Attachment[];
  providerHasAccount?: boolean;
}

interface Props {
  token: string;
  data: ThreadData;
  loginUrl: string;
}

const STATUS_MAP: Record<
  string,
  { label: string; color: string; bg: string; border: string }
> = {
  New: {
    label: "Awaiting Your Response",
    color: "#92400e",
    bg: "#fffbeb",
    border: "#fcd34d",
  },
  NewOpened: {
    label: "Opened — Pending Response",
    color: "#1e40af",
    bg: "#eff6ff",
    border: "#93c5fd",
  },
  Accepted: {
    label: "Accepted",
    color: "#065f46",
    bg: "#ecfdf5",
    border: "#6ee7b7",
  },
  Declined: {
    label: "Declined",
    color: "#991b1b",
    bg: "#fef2f2",
    border: "#fca5a5",
  },
  Rejected: {
    label: "Declined",
    color: "#991b1b",
    bg: "#fef2f2",
    border: "#fca5a5",
  },
  Cancelled: {
    label: "Cancelled",
    color: "#374151",
    bg: "#f9fafb",
    border: "#d1d5db",
  },
  Completed: {
    label: "Completed",
    color: "#065f46",
    bg: "#ecfdf5",
    border: "#6ee7b7",
  },
  InProgress: {
    label: "In Progress",
    color: "#5b21b6",
    bg: "#f5f3ff",
    border: "#c4b5fd",
  },
};

export function formatDate(iso: string, timezone: string) {
  try {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;

    const parts = new Intl.DateTimeFormat("en-US", {
      month: "short",
      day: "numeric",
      year: "numeric",
      hour: "numeric",
      minute: "2-digit",
      hour12: true,
      timeZone: timezone,
    }).formatToParts(date);
    const get = (type: Intl.DateTimeFormatPartTypes) =>
      parts.find((part) => part.type === type)?.value;
    const month = get("month");
    const day = get("day");
    const year = get("year");
    const hour = get("hour");
    const minute = get("minute");
    const dayPeriod = get("dayPeriod");
    if (!month || !day || !year || !hour || !minute || !dayPeriod) return iso;

    return `${month} ${day}, ${year}, ${hour}:${minute} ${dayPeriod}`;
  } catch {
    return iso;
  }
}

function resolveBrowserTimezone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

function formatBytes(b: number) {
  if (b < 1024) return `${b} B`;
  if (b < 1048576) return `${(b / 1024).toFixed(1)} KB`;
  return `${(b / 1048576).toFixed(1)} MB`;
}

const s: Record<string, React.CSSProperties> = {
  page: {
    minHeight: "100vh",
    margin: "0 auto",
    overflow: "hidden",
    background: "#f8fafc",
    fontFamily: "system-ui,-apple-system,sans-serif",
    color: "#111827",
    display: "flex",
    justifyContent: "center",
  },
  header: {
    maxWidth: 320,
    maxHeight: "97vh",
    background: "#0f172a",
    padding: "24px",
    color: "#fff",
    overflow: "hidden",
  },
  headerInner: { maxWidth: 680 },
  label: {
    margin: "0 0 4px",
    fontSize: 12,
    color: "#D4D4D4",
    letterSpacing: "0.05em",
    textTransform: "uppercase" as const,
  },
  title: { margin: 0, fontSize: 18, fontWeight: 500, color: "#FAFAFA" },
  inner: {
    maxWidth: 680,
    maxHeight: "97vh",
    overflowY: "auto",
    padding: "0 16px",
    marginTop: "10px",
  },
  card: {
    background: "#fff",
    borderRadius: 10,
    border: "1px solid #e2e8f0",
    padding: "20px 24px",
    marginBottom: 20,
  },
  cardTitle: {
    margin: "0 0 14px",
    fontSize: 15,
    fontWeight: 700,
    color: "#0f172a",
  },
  grid2: { display: "grid", gridTemplateColumns: "1fr 1fr", gap: "10px 24px" },
  fieldLabel: {
    margin: "0 0 2px",
    fontSize: 11,
    fontWeight: 600,
    color: "#94a3b8",
    textTransform: "uppercase" as const,
    letterSpacing: "0.05em",
  },
  fieldVal: { margin: 0, fontSize: 14, color: "#0f172a", fontWeight: 500 },
  attRow: {
    display: "flex",
    alignItems: "center",
    gap: 10,
    padding: "10px 14px",
    borderRadius: 8,
    border: "1px solid #e2e8f0",
    background: "#f8fafc",
    marginBottom: 8,
    textDecoration: "none",
  },
  btnPrimary: {
    display: "block",
    width: "100%",
    boxSizing: "border-box" as const,
    background: "#2563EB",
    color: "#fff",
    border: "none",
    padding: "11px 20px",
    borderRadius: 6,
    fontSize: 14,
    fontWeight: 700,
    cursor: "pointer",
    textAlign: "center" as const,
    textDecoration: "none",
  },
  btnOutline: {
    display: "block",
    width: "100%",
    boxSizing: "border-box" as const,
    background: "#fff",
    color: "#2563EB",
    border: "2px solid #2563EB",
    padding: "10px 20px",
    borderRadius: 6,
    fontSize: 14,
    fontWeight: 700,
    cursor: "pointer",
    textAlign: "center" as const,
    textDecoration: "none",
  },
  btnDanger: {
    display: "block",
    width: "100%",
    boxSizing: "border-box" as const,
    background: "#fff",
    color: "#dc2626",
    border: "2px solid #fca5a5",
    padding: "10px 20px",
    borderRadius: 6,
    fontSize: 14,
    fontWeight: 700,
    cursor: "pointer",
    textAlign: "center" as const,
    textDecoration: "none",
  },
  btnSuccess: {
    display: "block",
    width: "100%",
    boxSizing: "border-box" as const,
    background: "#16a34a",
    color: "#fff",
    border: "none",
    padding: "11px 20px",
    borderRadius: 6,
    fontSize: 14,
    fontWeight: 700,
    cursor: "pointer",
    textAlign: "center" as const,
    textDecoration: "none",
  },
  btnGray: {
    display: "block",
    width: "100%",
    boxSizing: "border-box" as const,
    background: "#fff",
    color: "#374151",
    border: "2px solid #d1d5db",
    padding: "10px 20px",
    borderRadius: 6,
    fontSize: 14,
    fontWeight: 700,
    cursor: "pointer",
    textAlign: "center" as const,
    textDecoration: "none",
  },
  input: {
    width: "100%",
    boxSizing: "border-box" as const,
    padding: "9px 12px",
    fontSize: 14,
    border: "1px solid #d1d5db",
    borderRadius: 6,
    color: "#111827",
    fontFamily: "inherit",
  },
  textarea: {
    width: "100%",
    boxSizing: "border-box" as const,
    padding: "9px 12px",
    fontSize: 14,
    border: "1px solid #d1d5db",
    borderRadius: 6,
    color: "#111827",
    fontFamily: "inherit",
    resize: "vertical" as const,
  },
};

type ActionState =
  | "idle"
  | "accepting"
  | "declining"
  | "completing"
  | "cancelling"
  | "accepted"
  | "declined"
  | "completed"
  | "cancelled"
  | "error";

export function ThreadClient({ token, data, loginUrl }: Props) {
  const [timezone, setTimezone] = useState("UTC");
  const [comments, setComments] = useState<Comment[]>(data.comments);
  const [message, setMessage] = useState("");
  const [files, setFiles] = useState<SelectedCareConnectMessageFile[]>([]);
  const [fileError, setFileError] = useState("");
  const [formError, setFormError] = useState("");
  const [sent, setSent] = useState(false);
  const [isPending, startTransition] = useTransition();
  const bottomRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const [actionState, setActionState] = useState<ActionState>("idle");
  const [actionError, setActionError] = useState("");
  const [liveStatus, setLiveStatus] = useState(data.status);

  const [attLoading, setAttLoading] = useState<
    Record<string, "view" | "download" | null>
  >({});
  const [attError, setAttError] = useState<Record<string, string | null>>({});

  const [liveTreatmentName] = useState<string | undefined>(
    data.treatmentTypeName,
  );
  const location = formatReferralLocation(data);

  // Decline notes state
  const [showDeclineForm, setShowDeclineForm] = useState(false);
  const [declineNotes, setDeclineNotes] = useState("");

  // Cancel confirmation state
  const [showCancelConfirm, setShowCancelConfirm] = useState(false);

  useEffect(() => {
    setTimezone(resolveBrowserTimezone());
  }, []);

  const openAttachment = useCallback(
    async (attachmentId: string, forDownload: boolean) => {
      const key = forDownload ? "download" : "view";
      setAttLoading((prev) => ({ ...prev, [attachmentId]: key }));
      setAttError((prev) => ({ ...prev, [attachmentId]: null }));
      try {
        const url =
          `/api/public/careconnect/api/referrals/${data.referralId}/public-attachments/${attachmentId}/url` +
          `?token=${encodeURIComponent(token)}&download=${forDownload}`;
        const res = await fetch(url);
        if (!res.ok) {
          setAttError((prev) => ({
            ...prev,
            [attachmentId]: "Could not load this document. Please try again.",
          }));
          return;
        }
        const body = (await res.json()) as { url?: string };
        if (!body.url) {
          setAttError((prev) => ({
            ...prev,
            [attachmentId]: "Document URL unavailable.",
          }));
          return;
        }
        window.open(body.url, "_blank", "noopener,noreferrer");
      } catch {
        setAttError((prev) => ({
          ...prev,
          [attachmentId]: "Network error. Please try again.",
        }));
      } finally {
        setAttLoading((prev) => ({ ...prev, [attachmentId]: null }));
      }
    },
    [data.referralId, token],
  );

  const addMessageFiles = useCallback(
    (incoming: File[]) => {
      const result = makeSelectedCareConnectMessageFiles(
        incoming,
        files.length,
      );
      setFileError(result.error ?? "");
      if (result.files.length > 0) {
        setFiles((prev) => [...prev, ...result.files]);
      }
    },
    [files.length],
  );

  const removeMessageFile = useCallback((id: string) => {
    setFileError("");
    setFiles((prev) => prev.filter((file) => file.id !== id));
  }, []);

  const st = STATUS_MAP[liveStatus] ?? {
    label: liveStatus,
    color: "#374151",
    bg: "#f9fafb",
    border: "#d1d5db",
  };

  const isNewOrOpened = liveStatus === "New" || liveStatus === "NewOpened";
  const isAcceptedOrInProgress =
    liveStatus === "Accepted" || liveStatus === "InProgress";
  const isTerminal = ["Completed", "Cancelled", "Declined"].includes(
    liveStatus,
  );
  const hasRecentAction = [
    "accepted",
    "declined",
    "completed",
    "cancelled",
  ].includes(actionState);

  const showActionCard =
    isNewOrOpened ||
    isAcceptedOrInProgress ||
    hasRecentAction ||
    actionState === "error";

  const referralId = data.referralId;
  const activateUrl = `/referrals/introduction?token=${encodeURIComponent(token)}`;

  const handleAccept = () => {
    setActionError("");
    setActionState("accepting");
    startTransition(async () => {
      const result = await acceptReferralByToken(referralId, token);
      if (!result.success) {
        setActionState("error");
        setActionError(
          result.error ?? "Could not accept the referral. Please try again.",
        );
        return;
      }
      setActionState("accepted");
      setLiveStatus("Accepted");
    });
  };

  const handleDecline = () => {
    setActionError("");
    setActionState("declining");
    startTransition(async () => {
      const result = await declineReferralByToken(
        referralId,
        token,
        declineNotes || undefined,
      );
      if (!result.success) {
        setActionState("error");
        setActionError(
          result.error ?? "Could not decline the referral. Please try again.",
        );
        return;
      }
      setActionState("declined");
      setLiveStatus("Declined");
    });
  };

  const handleComplete = () => {
    setActionError("");
    setActionState("completing");
    startTransition(async () => {
      const result = await completeReferralByToken(referralId, token);
      if (!result.success) {
        setActionState("error");
        setActionError(
          result.error ?? "Could not complete the referral. Please try again.",
        );
        return;
      }
      setActionState("completed");
      setLiveStatus("Completed");
    });
  };

  const handleCancel = () => {
    setActionError("");
    setActionState("cancelling");
    startTransition(async () => {
      const result = await cancelReferralByToken(referralId, token);
      if (!result.success) {
        setActionState("error");
        setActionError(
          result.error ?? "Could not cancel the referral. Please try again.",
        );
        return;
      }
      setActionState("cancelled");
      setLiveStatus("Cancelled");
    });
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setFormError("");
    setFileError("");
    setSent(false);
    startTransition(async () => {
      const result = await postPublicThreadComment(
        token,
        "provider",
        message,
        files,
      );
      if (!result.success) {
        setFormError(result.error ?? "An error occurred.");
        return;
      }
      if (result.comment) setComments((prev) => [...prev, result.comment!]);
      setMessage("");
      setFiles([]);
      setSent(true);
      setTimeout(
        () => bottomRef.current?.scrollIntoView({ behavior: "smooth" }),
        100,
      );
    });
  };

  return (
    <div style={s.page}>
      {/* Header */}
      {/* <div className="flex mx-auto"> */}
      <div
        style={s.header}
        className="rounded-xl w-full max-w-2xl p-6 mt-[10px] flex flex-col bg-[#0C1D33] relative"
      >
        <div className="absolute -right-[100px] -top-[100px] h-[300px] w-[300px] rounded-full border-[50px] border-[#EE7132] box-border pointer-events-none opacity-[0.05]" />
        <div className="absolute -bottom-[100px] -left-[100px] h-[300px] w-[300px] rounded-full border-[50px] border-[#EE7132] box-border pointer-events-none opacity-[0.05]" />
        {/* Header Section */}
        <div style={s.headerInner} className="flex flex-col z-10  text-white">
          <div className="flex items-start gap-4">
            <svg
              width="24"
              height="24"
              viewBox="0 0 24 24"
              fill="none"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path
                d="M6.06498 7.24678C6.06498 6.64431 6.54746 6.15414 7.1405 6.15414H12.1982V4.4762H7.1405C5.63711 4.4762 4.41333 5.71946 4.41333 7.24678V12.6624H6.06498V7.24678ZM6.06498 16.6261V16.4451H4.41333V16.6261C4.41333 18.1534 5.63711 19.3967 7.1405 19.3967H10.3489V17.7187H7.1405C6.54746 17.72 6.06498 17.2285 6.06498 16.6261ZM16.3728 4.4762H13.3101V6.15414H16.3728C16.9658 6.15414 17.4483 6.64431 17.4483 7.24678V7.5533H19.1V7.24678C19.1 5.71946 17.8775 4.4762 16.3728 4.4762ZM17.4496 16.6261C17.4496 17.2285 16.9671 17.7187 16.3741 17.7187H11.4621V19.3967H16.3741C17.8775 19.3967 19.1013 18.1534 19.1013 16.6261V11.3716H17.4496V16.6261Z"
                fill="white"
              />
              <path
                d="M10.3099 2.51955C10.3099 2.05581 10.6818 1.67794 11.1383 1.67794H12.4817C12.9382 1.67794 13.3101 2.05581 13.3101 2.51955V9.48103H14.9618V2.51955C14.9618 1.12964 13.8485 0 12.4817 0H11.1396C9.77145 0 8.65952 1.12964 8.65952 2.51955V3.44969H10.3112V2.51955H10.3099Z"
                fill="#EE7132"
              />
              <path
                d="M21.2022 13.4605C21.6586 13.4605 22.0306 13.0826 22.0306 12.6189V11.2541C22.0306 10.7903 21.6586 10.4124 21.2022 10.4124H8.71988V8.7345H21.2022C22.569 8.7345 23.6822 9.86413 23.6822 11.2541V12.6189C23.6822 14.0075 22.5703 15.1384 21.2022 15.1384H20.2866V13.4605H21.2022Z"
                fill="#EE7132"
              />
              <path
                d="M13.3718 21.4805C13.3718 21.9442 12.9999 22.3221 12.5434 22.3221H11.1999C10.7435 22.3221 10.3715 21.9442 10.3715 21.4805V14.519H8.71988V21.4805C8.71988 22.8691 9.83181 24 11.1999 24H12.5434C13.9102 24 15.0234 22.8704 15.0234 21.4805V20.5503H13.3718V21.4805Z"
                fill="#EE7132"
              />
              <path
                d="M2.48007 10.5379C2.02359 10.5379 1.65165 10.9158 1.65165 11.3795V12.7444C1.65165 13.2081 2.02359 13.586 2.48007 13.586H14.9623V15.2639H2.48007C1.11193 15.2639 0 14.1329 0 12.7444V11.3795C0 9.99094 1.11193 8.85999 2.48007 8.85999H3.39563V10.5379H2.48007Z"
                fill="#EE7132"
              />
            </svg>

            {/* <img src="/favicon.png" alt="icon" className="w-5 mb-2" /> */}
            <div>
              <p style={s.label}>LegalSynq CareConnect</p>
              <h1 style={s.title}>Provider Referral Portal</h1>
            </div>
          </div>
        </div>

        <div className="flex flex-col mx-auto justify-center w-full min-h-[80vh] z-10">
          {/* 50px Orange Divider Line */}
          <div className="w-[50px] rounded-xl border-t-[3px] border-[#EE7132] my-4"></div>

          {/* Manage Section (Vertical Stack) */}
          <div className="flex flex-col gap-4 w-full">
            <div className="max-w-md">
              <p
                style={{
                  margin: "0 0 8px",
                  fontSize: 28,
                  fontWeight: 700,
                  color: "#FFFFFF",
                  lineHeight: 1.2,
                }}
              >
                Manage all your referrals in one place
              </p>
              <p
                style={{
                  margin: "0 0 8px",
                  fontSize: 13,
                  color: "#ffffffc0",
                  lineHeight: 1.5,
                }}
              >
                {data.providerHasAccount
                  ? "Log in to your provider dashboard to accept referrals, view patient details, and manage this case."
                  : "Activate your provider account to accept referrals, view patient details, track statuses, and collaborate — all from a single dashboard."}
              </p>
            </div>

            <div>
              {data.providerHasAccount ? (
                <a
                  href={loginUrl}
                  style={{
                    ...s.btnPrimary,
                    padding: "9px 16px",
                    fontSize: 13,
                  }}
                >
                  Log in to your account
                </a>
              ) : (
                <a
                  href={activateUrl}
                  style={{
                    ...s.btnPrimary,
                    padding: "9px 16px",
                    fontSize: 13,
                  }}
                >
                  Activate Portal
                </a>
              )}
            </div>
          </div>
        </div>
      </div>
      <div style={s.inner}>
        {/* Portal upgrade banner */}

        {/* Status + referral summary */}
        <div style={s.card}>
          <div
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "space-between",
              marginBottom: 18,
              flexWrap: "wrap" as const,
              gap: 8,
            }}
          >
            <h2
              style={{
                margin: 0,
                fontSize: 15,
                fontWeight: 700,
                color: "#0f172a",
              }}
            >
              Referral Summary
            </h2>
            <span
              style={{
                background: st.bg,
                color: st.color,
                border: `1px solid ${st.border}`,
                borderRadius: 20,
                padding: "3px 12px",
                fontSize: 12,
                fontWeight: 600,
              }}
            >
              {st.label}
            </span>
          </div>

          {/* Referral meta */}
          <div style={s.grid2}>
            <FieldBlock label="Service" value={data.service} />
            <FieldBlock
              label="Submitted"
              value={formatDate(data.createdAtUtc, timezone)}
            />
            {data.urgency && (
              <FieldBlock label="Urgency" value={data.urgency} />
            )}
            {data.caseNumber && (
              <FieldBlock label="Case #" value={data.caseNumber} />
            )}
            <FieldBlock
              label="Type of Treatment"
              value={liveTreatmentName ?? "—"}
            />
            <FieldBlock
              label="Date of Accident"
              value={data.dateOfAccident ?? "—"}
            />
            {location && (
              <FieldBlock label="Provider Location" value={location} />
            )}
          </div>

          {/* Notes */}
          {data.notes && (
            <div style={{ marginTop: 14 }}>
              <p
                style={{
                  margin: "0 0 4px",
                  fontSize: 11,
                  fontWeight: 600,
                  color: "#94a3b8",
                  textTransform: "uppercase",
                  letterSpacing: "0.05em",
                }}
              >
                Notes
              </p>
              <p
                style={{
                  margin: 0,
                  fontSize: 14,
                  color: "#374151",
                  lineHeight: 1.6,
                  whiteSpace: "pre-wrap",
                }}
              >
                {data.notes}
              </p>
            </div>
          )}

          {/* Divider */}
          <div style={{ borderTop: "1px solid #e2e8f0", margin: "18px 0" }} />

          {/* Patient information */}
          <p
            style={{
              margin: "0 0 12px",
              fontSize: 12,
              fontWeight: 700,
              color: "#0f172a",
              textTransform: "uppercase",
              letterSpacing: "0.06em",
            }}
          >
            Patient Information
          </p>
          <div style={s.grid2}>
            <FieldBlock label="Full Name" value={data.clientName} />
            {data.clientDob && (
              <FieldBlock label="Date of Birth" value={data.clientDob} />
            )}
            {data.clientPhone && (
              <FieldBlock label="Phone" value={data.clientPhone} />
            )}
            {data.clientEmail && (
              <FieldBlock label="Email" value={data.clientEmail} />
            )}
          </div>

          {/* Divider */}
          <div style={{ borderTop: "1px solid #e2e8f0", margin: "18px 0" }} />

          {/* Referring law firm */}
          <p
            style={{
              margin: "0 0 12px",
              fontSize: 12,
              fontWeight: 700,
              color: "#0f172a",
              textTransform: "uppercase",
              letterSpacing: "0.06em",
            }}
          >
            Referring Law Firm
          </p>
          <div style={s.grid2}>
            {data.referrerFirmName && (
              <FieldBlock label="Law Firm" value={data.referrerFirmName} />
            )}
            <FieldBlock label="Contact Name" value={data.referrerName ?? "—"} />
            {data.referrerEmail && (
              <FieldBlock label="Email" value={data.referrerEmail} />
            )}
          </div>

          {(data.lienCompanyName || data.lienCompanyEmail) && (
            <>
              {/* Divider */}
              <div style={{ borderTop: '1px solid #e2e8f0', margin: '18px 0' }} />

              {/* Lien company */}
              <p style={{ margin: '0 0 12px', fontSize: 12, fontWeight: 700, color: '#0f172a', textTransform: 'uppercase', letterSpacing: '0.06em' }}>Lien Company</p>
              <div style={s.grid2}>
                {data.lienCompanyName && <FieldBlock label="Name" value={data.lienCompanyName} />}
                {data.lienCompanyEmail && <FieldBlock label="Email" value={data.lienCompanyEmail} />}
              </div>
            </>
          )}
        </div>

        {/* Action Card — status-aware */}
        {showActionCard && (
          <div style={s.card}>
            <h2 style={s.cardTitle}>
              {isNewOrOpened
                ? "Your Response"
                : isAcceptedOrInProgress
                  ? "Referral Actions"
                  : "Referral Status"}
            </h2>

            {/* Success: accepted */}
            {actionState === "accepted" && (
              <div
                style={{
                  background: "#ecfdf5",
                  border: "1px solid #6ee7b7",
                  borderRadius: 8,
                  padding: "14px 18px",
                }}
              >
                <p
                  style={{
                    margin: 0,
                    fontSize: 14,
                    fontWeight: 700,
                    color: "#065f46",
                  }}
                >
                  Referral accepted — thank you!
                </p>
                <p
                  style={{
                    margin: "6px 0 0",
                    fontSize: 13,
                    color: "#047857",
                  }}
                >
                  The referring party has been notified. You can log in to your
                  provider dashboard to view full patient details and manage the
                  case.
                </p>
                <a
                  href={loginUrl}
                  style={{
                    ...s.btnPrimary,
                    marginTop: 14,
                    display: "inline-block",
                    width: "auto",
                    padding: "9px 20px",
                    fontSize: 13,
                  }}
                >
                  Go to dashboard
                </a>
              </div>
            )}

            {/* Success: declined */}
            {actionState === "declined" && (
              <div
                style={{
                  background: "#fef2f2",
                  border: "1px solid #fca5a5",
                  borderRadius: 8,
                  padding: "14px 18px",
                }}
              >
                <p
                  style={{
                    margin: 0,
                    fontSize: 14,
                    fontWeight: 700,
                    color: "#991b1b",
                  }}
                >
                  Referral declined.
                </p>
                <p
                  style={{
                    margin: "6px 0 0",
                    fontSize: 13,
                    color: "#b91c1c",
                  }}
                >
                  The referring party has been notified. If you change your
                  mind, please contact them directly.
                </p>
              </div>
            )}

            {/* Success: completed */}
            {actionState === "completed" && (
              <div
                style={{
                  background: "#ecfdf5",
                  border: "1px solid #6ee7b7",
                  borderRadius: 8,
                  padding: "14px 18px",
                }}
              >
                <p
                  style={{
                    margin: 0,
                    fontSize: 14,
                    fontWeight: 700,
                    color: "#065f46",
                  }}
                >
                  Referral marked as completed.
                </p>
                <p
                  style={{
                    margin: "6px 0 0",
                    fontSize: 13,
                    color: "#047857",
                  }}
                >
                  This referral has been completed. No further action is needed.
                </p>
              </div>
            )}

            {/* Success: cancelled */}
            {actionState === "cancelled" && (
              <div
                style={{
                  background: "#f9fafb",
                  border: "1px solid #d1d5db",
                  borderRadius: 8,
                  padding: "14px 18px",
                }}
              >
                <p
                  style={{
                    margin: 0,
                    fontSize: 14,
                    fontWeight: 700,
                    color: "#374151",
                  }}
                >
                  Referral cancelled.
                </p>
                <p
                  style={{
                    margin: "6px 0 0",
                    fontSize: 13,
                    color: "#6b7280",
                  }}
                >
                  This referral has been cancelled. The referring party has been
                  notified.
                </p>
              </div>
            )}

            {/* Error banner */}
            {actionState === "error" && actionError && (
              <div
                style={{
                  background: "#fef2f2",
                  border: "1px solid #fecaca",
                  borderRadius: 6,
                  padding: "10px 14px",
                  marginBottom: 14,
                }}
              >
                <p style={{ margin: 0, fontSize: 14, color: "#991b1b" }}>
                  {actionError}
                </p>
              </div>
            )}

            {/* New/NewOpened: Accept + Decline */}
            {isNewOrOpened && !hasRecentAction && (
              <>
                {!showDeclineForm ? (
                  <>
                    <p
                      style={{
                        margin: "0 0 16px",
                        fontSize: 13,
                        color: "#6b7280",
                      }}
                    >
                      Respond directly from this page, or log in to your
                      provider dashboard.
                    </p>
                    <div style={{ display: "flex", gap: 10 }}>
                      <button
                        onClick={handleAccept}
                        disabled={isPending}
                        style={{
                          ...s.btnPrimary,
                          flex: 1,
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        {actionState === "accepting"
                          ? "Accepting…"
                          : "Accept Referral"}
                      </button>
                      <button
                        onClick={() => setShowDeclineForm(true)}
                        disabled={isPending}
                        style={{
                          ...s.btnDanger,
                          flex: 1,
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        Decline Referral
                      </button>
                    </div>
                    <p
                      style={{
                        margin: "10px 0 0",
                        fontSize: 11,
                        color: "#9ca3af",
                        textAlign: "center" as const,
                      }}
                    >
                      Your response is securely recorded.{" "}
                      <a
                        href={loginUrl}
                        style={{
                          color: "#6b7280",
                          textDecoration: "underline",
                        }}
                      >
                        Log in
                      </a>{" "}
                      to manage from your dashboard.
                    </p>
                  </>
                ) : (
                  <div>
                    <p
                      style={{
                        margin: "0 0 10px",
                        fontSize: 13,
                        color: "#6b7280",
                      }}
                    >
                      Reason for declining (optional):
                    </p>
                    <textarea
                      style={{ ...s.textarea, marginBottom: 12 }}
                      value={declineNotes}
                      onChange={(e) => setDeclineNotes(e.target.value)}
                      placeholder="Let the referring party know why…"
                      rows={3}
                      maxLength={2000}
                    />
                    <div style={{ display: "flex", gap: 10 }}>
                      <button
                        onClick={handleDecline}
                        disabled={isPending}
                        style={{
                          ...s.btnDanger,
                          flex: 1,
                          background: "#dc2626",
                          color: "#fff",
                          border: "none",
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        {actionState === "declining"
                          ? "Declining…"
                          : "Confirm Decline"}
                      </button>
                      <button
                        onClick={() => {
                          setShowDeclineForm(false);
                          setDeclineNotes("");
                        }}
                        disabled={isPending}
                        style={{
                          ...s.btnGray,
                          flex: 1,
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        Go back
                      </button>
                    </div>
                  </div>
                )}
              </>
            )}

            {/* Accepted/InProgress: Completed + Cancel */}
            {isAcceptedOrInProgress && !hasRecentAction && (
              <>
                {!showCancelConfirm ? (
                  <>
                    <p
                      style={{
                        margin: "0 0 16px",
                        fontSize: 13,
                        color: "#6b7280",
                      }}
                    >
                      This referral has been accepted. You can mark it as
                      completed or cancel it.
                    </p>
                    <div style={{ display: "flex", gap: 10 }}>
                      <button
                        onClick={handleComplete}
                        disabled={isPending}
                        style={{
                          ...s.btnSuccess,
                          flex: 1,
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        {actionState === "completing"
                          ? "Completing…"
                          : "Mark as Completed"}
                      </button>
                      <button
                        onClick={() => setShowCancelConfirm(true)}
                        disabled={isPending}
                        style={{
                          ...s.btnGray,
                          flex: 1,
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        Cancel Referral
                      </button>
                    </div>
                  </>
                ) : (
                  <div>
                    <p
                      style={{
                        margin: "0 0 14px",
                        fontSize: 14,
                        fontWeight: 600,
                        color: "#374151",
                      }}
                    >
                      Are you sure you want to cancel this referral?
                    </p>
                    <div style={{ display: "flex", gap: 10 }}>
                      <button
                        onClick={handleCancel}
                        disabled={isPending}
                        style={{
                          ...s.btnDanger,
                          flex: 1,
                          background: "#dc2626",
                          color: "#fff",
                          border: "none",
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        {actionState === "cancelling"
                          ? "Cancelling…"
                          : "Yes, Cancel Referral"}
                      </button>
                      <button
                        onClick={() => setShowCancelConfirm(false)}
                        disabled={isPending}
                        style={{
                          ...s.btnGray,
                          flex: 1,
                          opacity: isPending ? 0.7 : 1,
                          cursor: isPending ? "not-allowed" : "pointer",
                        }}
                      >
                        Keep Referral
                      </button>
                    </div>
                  </div>
                )}
              </>
            )}
          </div>
        )}

        {/* Attachments */}
        {data.attachments && data.attachments.length > 0 && (
          <div style={s.card}>
            <h2 style={s.cardTitle}>Documents ({data.attachments.length})</h2>
            {data.attachments.map((att) => {
              const loading = attLoading[att.id] ?? null;
              const errMsg = attError[att.id] ?? null;
              const busy = loading !== null;
              return (
                <div key={att.id}>
                  <div
                    style={{
                      ...s.attRow,
                      cursor: busy ? "wait" : "pointer",
                      opacity: busy ? 0.75 : 1,
                    }}
                    onClick={() => !busy && openAttachment(att.id, false)}
                    role="button"
                    tabIndex={0}
                    onKeyDown={(e) => {
                      if (e.key === "Enter" || e.key === " ") {
                        e.preventDefault();
                        if (!busy) openAttachment(att.id, false);
                      }
                    }}
                    aria-label={`View ${att.fileName}`}
                  >
                    <svg
                      width="20"
                      height="20"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="#6b7280"
                      strokeWidth="1.5"
                      style={{ flexShrink: 0 }}
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                      />
                    </svg>
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <p
                        style={{
                          margin: 0,
                          fontSize: 13,
                          fontWeight: 600,
                          color: "#0f172a",
                          overflow: "hidden",
                          textOverflow: "ellipsis",
                          whiteSpace: "nowrap",
                        }}
                      >
                        {att.fileName}
                      </p>
                      <p style={{ margin: 0, fontSize: 11, color: "#9ca3af" }}>
                        {formatBytes(att.fileSizeBytes)}
                        {loading === "view" && (
                          <span style={{ marginLeft: 6, color: "#6b7280" }}>
                            Opening…
                          </span>
                        )}
                        {loading === "download" && (
                          <span style={{ marginLeft: 6, color: "#6b7280" }}>
                            Downloading…
                          </span>
                        )}
                      </p>
                    </div>
                    <div
                      style={{
                        display: "flex",
                        gap: 6,
                        alignItems: "center",
                        flexShrink: 0,
                      }}
                      onClick={(e) => e.stopPropagation()}
                    >
                      <button
                        title="View document"
                        disabled={busy}
                        onClick={(e) => {
                          e.stopPropagation();
                          openAttachment(att.id, false);
                        }}
                        style={{
                          background: "none",
                          border: "none",
                          cursor: busy ? "not-allowed" : "pointer",
                          padding: 4,
                          borderRadius: 4,
                          display: "flex",
                          alignItems: "center",
                        }}
                      >
                        <svg
                          width="16"
                          height="16"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="#6b7280"
                          strokeWidth="2"
                        >
                          <path
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                          />
                          <path
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            d="M2.458 12C3.732 7.943 7.523 5 12 5c4.477 0 8.268 2.943 9.542 7-1.274 4.057-5.065 7-9.542 7-4.477 0-8.268-2.943-9.542-7z"
                          />
                        </svg>
                      </button>
                      <button
                        title="Download document"
                        disabled={busy}
                        onClick={(e) => {
                          e.stopPropagation();
                          openAttachment(att.id, true);
                        }}
                        style={{
                          background: "none",
                          border: "none",
                          cursor: busy ? "not-allowed" : "pointer",
                          padding: 4,
                          borderRadius: 4,
                          display: "flex",
                          alignItems: "center",
                        }}
                      >
                        <svg
                          width="16"
                          height="16"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="#9ca3af"
                          strokeWidth="2"
                        >
                          <path
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4"
                          />
                        </svg>
                      </button>
                    </div>
                  </div>
                  {errMsg && (
                    <p
                      style={{
                        margin: "-4px 0 8px",
                        fontSize: 12,
                        color: "#dc2626",
                        paddingLeft: 4,
                      }}
                    >
                      {errMsg}
                    </p>
                  )}
                </div>
              );
            })}
          </div>
        )}

        {/* Message thread */}
        <div style={s.card}>
          <h2 style={s.cardTitle}>Messages</h2>
          <div
            style={{
              height: 420,
              overflowY: "auto",
              display: "flex",
              flexDirection: "column",
              gap: 14,
            }}
          >
            {comments.length === 0 ? (
              <div className="text-center py-8 p-4">
                <i className="ri-message-2-line text-2xl py-3 px-4 bg-[#F5F5F5] rounded-md"></i>

                <p className="mt-3 p-4 text-sm text-gray-400">
                  No messages yet. Send the first message below.
                </p>
              </div>
            ) : (
              comments.map((c) => (
                <CommentBubble
                  key={c.id}
                  comment={c}
                  timezone={timezone}
                  onOpenAttachment={openAttachment}
                  attLoading={attLoading}
                  attError={attError}
                />
              ))
            )}
            {/* Send message form — provider side only */}
            <div>
              {sent && (
                <div
                  style={{
                    background: "#f0fdf4",
                    border: "1px solid #bbf7d0",
                    borderRadius: 6,
                    padding: "10px 14px",
                    marginBottom: 14,
                  }}
                >
                  <p style={{ margin: 0, fontSize: 14, color: "#166534" }}>
                    Message sent. The referring party will receive an email
                    notification.
                  </p>
                </div>
              )}
              {formError && (
                <div
                  style={{
                    background: "#fef2f2",
                    border: "1px solid #fecaca",
                    borderRadius: 6,
                    padding: "10px 14px",
                    marginBottom: 14,
                  }}
                >
                  <p style={{ margin: 0, fontSize: 14, color: "#991b1b" }}>
                    {formError}
                  </p>
                </div>
              )}
              <form onSubmit={handleSubmit}>
                <div style={{ marginBottom: 18 }}>
                  <div style={{ position: "relative", marginBottom: 18 }}>
                    <textarea
                      style={{
                        ...s.textarea,
                        width: "100%",
                        boxSizing: "border-box",
                        paddingRight: 180,
                        paddingBottom: 30,
                      }}
                      value={message}
                      onChange={(e) => setMessage(e.target.value)}
                      placeholder="Type a message..."
                      rows={1}
                      maxLength={4000}
                    />

                    {/* Floating buttons */}
                    <div
                      style={{
                        position: "absolute",
                        right: 10,
                        bottom: 40,
                        display: "flex",
                        alignItems: "center",
                        gap: 8,
                      }}
                    >
                      {/* Hidden file input */}
                      <input
                        ref={fileInputRef}
                        type="file"
                        multiple
                        accept={CARECONNECT_MESSAGE_ALLOWED_TYPES.join(",")}
                        onChange={(e) => {
                          addMessageFiles(Array.from(e.target.files ?? []));
                          e.target.value = "";
                        }}
                        style={{ display: "none" }}
                        aria-hidden="true"
                        tabIndex={-1}
                      />

                      {/* Attach button */}
                      <button
                        type="button"
                        onClick={() => fileInputRef.current?.click()}
                        disabled={
                          isPending ||
                          files.length >= CARECONNECT_MESSAGE_MAX_FILES
                        }
                        style={{
                          display: "flex",
                          alignItems: "center",
                          gap: 5,
                          border: "none",
                          background: "#f1f5f9",
                          color: "#475569",
                          borderRadius: 20,
                          padding: "8px 10px",
                          cursor:
                            isPending ||
                            files.length >= CARECONNECT_MESSAGE_MAX_FILES
                              ? "not-allowed"
                              : "pointer",
                          opacity:
                            isPending ||
                            files.length >= CARECONNECT_MESSAGE_MAX_FILES
                              ? 0.65
                              : 1,
                          fontSize: 13,
                          fontFamily: "inherit",
                        }}
                      >
                        <i className="ri-upload-2-line"></i>
                      </button>

                      {/* Send button */}
                      <button
                        type="submit"
                        disabled={isPending}
                        style={{
                          display: "flex",
                          alignItems: "center",
                          justifyContent: "center",
                          width: 38,
                          height: 38,
                          border: "none",
                          borderRadius: 20,
                          background: "#2563eb",
                          color: "#fff",
                          cursor: isPending ? "not-allowed" : "pointer",
                          opacity: isPending ? 0.65 : 1,
                        }}
                      >
                        <i className="ri-send-plane-fill"></i>
                      </button>
                    </div>

                    {/* Character count */}
                    <p
                      style={{
                        margin: "4px 0 0",
                        fontSize: 12,
                        color: "#9ca3af",
                        textAlign: "right" as const,
                      }}
                    >
                      {message.length}/4000
                    </p>
                  </div>
                  {files.length > 0 && (
                    <div className="border border-gray-300 border-dashed rounded-xl bg-[#f8fafc] p-4">
                      <div className="grid grid-cols-2 text-sm">
                        <h2 className="text-sm font-semibold">Attach files</h2>

                        <span className="text-right text-gray-600">
                          {files.length}/{CARECONNECT_MESSAGE_MAX_FILES}
                        </span>
                      </div>

                      <ul className="list-none p-0 m-2 flex flex-col gap-3">
                        {files.map((selected) => (
                          <li
                            key={selected.id}
                            className={`flex items-start gap-3 py-4 ${files.length > 1 ? "border-b border-gray-300" : ""}`}
                          >
                            <i className="ri-file-text-line text-2xl p-3 px-4 bg-[#F5F5F5] rounded-md" />

                            <p className="flex-1 min-w-0 overflow-hidden text-ellipsis whitespace-nowrap">
                              {selected.file.name}
                              <br></br>
                              <span style={{ color: "#94a3b8", flexShrink: 0 }}>
                                {formatCareConnectAttachmentBytes(
                                  selected.file.size,
                                )}
                              </span>
                            </p>
                            <button
                              type="button"
                              onClick={() => removeMessageFile(selected.id)}
                              disabled={isPending}
                              aria-label={`Remove ${selected.file.name}`}
                              style={{
                                border: "none",
                                background: "transparent",
                                color: "#94a3b8",
                                cursor: isPending ? "not-allowed" : "pointer",
                                padding: 2,
                                fontSize: 16,
                                lineHeight: 1,
                              }}
                            >
                              ×
                            </button>
                          </li>
                        ))}
                      </ul>
                      {fileError && (
                        <p
                          style={{
                            margin: "6px 0 0",
                            fontSize: 12,
                            color: "#dc2626",
                          }}
                        >
                          {fileError}
                        </p>
                      )}
                    </div>
                  )}
                </div>
              </form>
            </div>
            <div ref={bottomRef} />
          </div>
        </div>

        <p
          style={{
            textAlign: "center",
            marginTop: 8,
            marginBottom: 24,
            fontSize: 12,
            color: "#94a3b8",
          }}
        >
          Accessible only with the secure link from your referral email. This
          link expires 30 days from the referral date.
        </p>
      </div>
    </div>
    // </div>
  );
}

function FieldBlock({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p
        style={{
          margin: "0 0 2px",
          fontSize: 11,
          fontWeight: 600,
          color: "#94a3b8",
          textTransform: "uppercase",
          letterSpacing: "0.05em",
        }}
      >
        {label}
      </p>
      <p style={{ margin: 0, fontSize: 14, color: "#0f172a", fontWeight: 500 }}>
        {value || "—"}
      </p>
    </div>
  );
}

function CommentBubble({
  comment,
  timezone,
  onOpenAttachment,
  attLoading,
  attError,
}: {
  comment: Comment;
  timezone: string;
  onOpenAttachment: (attachmentId: string, forDownload: boolean) => void;
  attLoading: Record<string, "view" | "download" | null>;
  attError: Record<string, string | null>;
}) {
  const isProvider = comment.senderType === "provider";
  const attachments = comment.attachments ?? [];
  return (
    <div
      style={{
        display: "flex",
        flexDirection: isProvider ? "row-reverse" : "row",
        gap: 10,
        alignItems: "flex-start",
      }}
    >
      <div
        style={{
          width: 34,
          height: 34,
          borderRadius: "50%",
          flexShrink: 0,
          background: isProvider ? "#dbeafe" : "#fef3c7",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          fontSize: 14,
          fontWeight: 700,
          color: isProvider ? "#1d4ed8" : "#92400e",
        }}
      >
        {comment.senderName.charAt(0).toUpperCase()}
      </div>
      <div style={{ maxWidth: "80%" }}>
        <div
          style={{
            display: "flex",
            gap: 8,
            alignItems: "baseline",
            flexDirection: isProvider ? "row-reverse" : "row",
            marginBottom: 4,
          }}
        >
          <span style={{ fontSize: 13, fontWeight: 600, color: "#374151" }}>
            {comment.senderName}
          </span>
          <span style={{ fontSize: 11, color: "#9ca3af" }}>
            {formatDate(comment.createdAtUtc, timezone)}
          </span>
        </div>
        {comment.message.trim().length > 0 && (
          <div
            style={{
              background: isProvider ? "#eff6ff" : "#fafaf9",
              border: `1px solid ${isProvider ? "#bfdbfe" : "#e7e5e4"}`,
              borderRadius: isProvider
                ? "12px 4px 12px 12px"
                : "4px 12px 12px 12px",
              padding: "10px 14px",
            }}
          >
            <p
              style={{
                margin: 0,
                fontSize: 14,
                color: "#111827",
                lineHeight: 1.6,
                whiteSpace: "pre-wrap",
              }}
            >
              {comment.message}
            </p>
          </div>
        )}
        {attachments.length > 0 && (
          <div
            style={{
              marginTop: 8,
              display: "flex",
              flexDirection: "column",
              gap: 6,
              alignItems: isProvider ? "flex-end" : "flex-start",
            }}
          >
            {attachments.map((att) => {
              const loading = attLoading[att.id] ?? null;
              const error = attError[att.id] ?? null;
              return (
                <div key={att.id} style={{ maxWidth: "100%" }}>
                  <button
                    type="button"
                    onClick={() => onOpenAttachment(att.id, false)}
                    disabled={loading !== null}
                    style={{
                      display: "inline-flex",
                      alignItems: "center",
                      gap: 6,
                      maxWidth: "100%",
                      border: "1px solid #e2e8f0",
                      background: "#fff",
                      color: "#334155",
                      borderRadius: 6,
                      padding: "5px 8px",
                      fontSize: 12,
                      cursor: loading ? "wait" : "pointer",
                      opacity: loading ? 0.7 : 1,
                    }}
                    title={`View ${att.fileName}`}
                  >
                    <span
                      style={{
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                      }}
                    >
                      {att.fileName}
                    </span>
                    <span style={{ color: "#94a3b8", flexShrink: 0 }}>
                      {loading === "view"
                        ? "Opening..."
                        : formatBytes(att.fileSizeBytes)}
                    </span>
                  </button>
                  {error && (
                    <p
                      style={{
                        margin: "4px 0 0",
                        fontSize: 12,
                        color: "#dc2626",
                      }}
                    >
                      {error}
                    </p>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
