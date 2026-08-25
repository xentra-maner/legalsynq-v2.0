'use client';

import { useState, useEffect, useTransition, useRef, useCallback } from 'react';
import { postPublicThreadComment } from '../lib/public-thread-comments';
import { ReferrerPortalAccessStatuses, type ReferrerPortalAccessStatusValue } from '@/types/careconnect';
import {
  CARECONNECT_MESSAGE_ALLOWED_TYPES,
  CARECONNECT_MESSAGE_MAX_FILES,
  formatCareConnectAttachmentBytes,
  makeSelectedCareConnectMessageFiles,
  type SelectedCareConnectMessageFile,
} from '@/lib/careconnect-message-attachments';
import type { ReferralMessageAttachment } from '@/types/careconnect';
import { formatReferralLocation } from '@/lib/referral-location';

interface ReferralAttributionSummary {
  id: string;
  firstName?: string | null;
  lastName?: string | null;
}

interface Comment {
  id:         string;
  senderType: string;
  senderName: string;
  message:    string;
  createdAtUtc: string;
  attachments?: ReferralMessageAttachment[];
}

interface ThreadData {
  referralId:         string;
  tenantId:           string;
  status:             string;
  clientName:         string;
  service:            string;
  urgency?:           string;
  providerName:       string;
  // Referral location — the specific facility this referral was routed to, falling back
  // to the provider's own address for legacy/single-location referrals.
  facilityName?:          string | null;
  locationAddressLine1?:  string;
  locationCity?:          string;
  locationState?:         string;
  locationPostalCode?:    string;
  referrerFirmName?:  string | null;
  referrerName:       string | null;
  referrerEmail:      string | null;
  referrerPhone?:     string | null;
  notes:              string | null;
  dateOfAccident?:    string;
  treatmentTypeId?:   string;
  treatmentTypeName?: string;
  referralAttribution?: ReferralAttributionSummary | null;
  lienCompanyName?:   string | null;
  lienCompanyEmail?:  string | null;
  createdAtUtc:       string;
  comments:           Comment[];
}

interface Props {
  token:           string;
  data:            ThreadData;
  portalAccessStatus: ReferrerPortalAccessStatusValue;
  loginUrl:        string;
  enrollToken:     string | null;
}

type StatusKey = 'New' | 'NewOpened' | 'Accepted' | 'Completed' | 'Declined' | 'Rejected' | 'Cancelled' | 'InProgress';

const STATUS_CONFIG: Record<StatusKey, { label: string; color: string; bg: string; border: string; step: number }> = {
  New:        { label: 'Awaiting Provider Response', color: '#92400e', bg: '#fffbeb', border: '#fcd34d', step: 1 },
  NewOpened:  { label: 'Opened by Provider',         color: '#1e40af', bg: '#eff6ff', border: '#93c5fd', step: 1 },
  InProgress: { label: 'In Progress',                color: '#5b21b6', bg: '#f5f3ff', border: '#c4b5fd', step: 2 },
  Accepted:   { label: 'Accepted by Provider',       color: '#065f46', bg: '#ecfdf5', border: '#6ee7b7', step: 3 },
  Completed:  { label: 'Completed',                  color: '#065f46', bg: '#ecfdf5', border: '#6ee7b7', step: 4 },
  Declined:   { label: 'Declined by Provider',       color: '#991b1b', bg: '#fef2f2', border: '#fca5a5', step: -1 },
  Rejected:   { label: 'Declined by Provider',       color: '#991b1b', bg: '#fef2f2', border: '#fca5a5', step: -1 },
  Cancelled:  { label: 'Cancelled',                  color: '#374151', bg: '#f9fafb', border: '#d1d5db', step: -1 },
};

function formatDate(iso: string, timezone: string) {
  try {
    return new Date(iso).toLocaleString('en-US', {
      month: 'short', day: 'numeric', year: 'numeric',
      hour: 'numeric', minute: '2-digit', hour12: true, timeZone: timezone,
    });
  } catch { return iso; }
}

function resolveBrowserTimezone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

function referralOriginationName(attribution?: ReferralAttributionSummary | null): string | null {
  const firstName = attribution?.firstName?.trim() ?? '';
  const lastName = attribution?.lastName?.trim() ?? '';
  const name = [firstName, lastName].filter(Boolean).join(' ');
  return name || null;
}

const s: Record<string, React.CSSProperties> = {
  page:       { minHeight: '100vh', background: '#f8fafc', fontFamily: 'system-ui,-apple-system,sans-serif', color: '#111827' },
  header:     { background: '#0f172a', padding: '20px 24px', color: '#fff' },
  headerInner:{ maxWidth: 680, margin: '0 auto' },
  headerLabel:{ margin: '0 0 4px', fontSize: 12, color: '#94a3b8', letterSpacing: '0.05em', textTransform: 'uppercase' as const },
  headerTitle:{ margin: 0, fontSize: 20, fontWeight: 700 },
  inner:      { maxWidth: 680, margin: '0 auto', padding: '24px 16px' },
  card:       { background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0', padding: '20px 24px', marginBottom: 20 },
  cardTitle:  { margin: '0 0 14px', fontSize: 15, fontWeight: 700, color: '#0f172a' },
  grid2:      { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 24px' },
  upgradeBox: {
    background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0',
    padding: '20px 24px', marginBottom: 20,
    borderLeft: '4px solid #1a56db',
  },
  btnPrimary: {
    display: 'inline-block', background: '#1a56db', color: '#fff', border: 'none',
    padding: '10px 22px', borderRadius: 6, fontSize: 13, fontWeight: 700,
    cursor: 'pointer', textAlign: 'center' as const, textDecoration: 'none',
  },
  btnOutline: {
    display: 'inline-block', background: '#fff', color: '#1a56db', border: '2px solid #1a56db',
    padding: '8px 20px', borderRadius: 6, fontSize: 13, fontWeight: 700,
    cursor: 'pointer', textAlign: 'center' as const, textDecoration: 'none',
  },
  input:      { width: '100%', boxSizing: 'border-box' as const, padding: '9px 12px', fontSize: 14, border: '1px solid #d1d5db', borderRadius: 6, color: '#111827', fontFamily: 'inherit' },
  textarea:   { width: '100%', boxSizing: 'border-box' as const, padding: '9px 12px', fontSize: 14, border: '1px solid #d1d5db', borderRadius: 6, color: '#111827', fontFamily: 'inherit', resize: 'vertical' as const },
};

// ── Status tracker ─────────────────────────────────────────────────────────────

function StatusTracker({ status }: { status: string }) {
  const cfg = STATUS_CONFIG[status as StatusKey] ?? STATUS_CONFIG.New;
  const declined = status === 'Rejected' || status === 'Declined' || status === 'Cancelled';

  const steps = [
    { label: 'Submitted',         done: true },
    { label: 'Awaiting Response', done: cfg.step >= 2 || declined },
    { label: 'Accepted',          done: cfg.step >= 3 || declined },
    { label: declined ? cfg.label : 'Completed', done: cfg.step >= 4 || declined },
  ];

  return (
    <div style={{ marginBottom: 20 }}>
      {/* Status badge */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16 }}>
        <span style={{
          background: cfg.bg, color: cfg.color, border: `1px solid ${cfg.border}`,
          borderRadius: 20, padding: '4px 14px', fontSize: 12, fontWeight: 700,
        }}>
          {cfg.label}
        </span>
      </div>

      {/* Progress bar */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 0 }}>
        {steps.map((step, i) => {
          const isLast  = i === steps.length - 1;
          const active  = (cfg.step === i + 1) || (isLast && cfg.step >= 3);
          const isDone  = step.done;
          const isError = isLast && declined;

          const circleColor  = isError ? '#dc2626' : isDone || active ? '#1a56db' : '#d1d5db';
          const circleTextCl = isError ? '#fff' : isDone || active ? '#fff' : '#9ca3af';
          const labelColor   = isError ? '#dc2626' : active ? '#1a56db' : isDone ? '#374151' : '#9ca3af';

          return (
            <div key={i} style={{ display: 'flex', alignItems: 'center', flex: isLast ? 0 : 1 }}>
              <div style={{ display: 'flex', flexDirection: 'column' as const, alignItems: 'center' }}>
                <div style={{
                  width: 28, height: 28, borderRadius: '50%',
                  background: circleColor, display: 'flex', alignItems: 'center', justifyContent: 'center',
                  fontSize: 12, fontWeight: 700, color: circleTextCl,
                  flexShrink: 0,
                }}>
                  {isError ? '✕' : isDone ? '✓' : i + 1}
                </div>
                <p style={{ margin: '4px 0 0', fontSize: 11, fontWeight: 600, color: labelColor, textAlign: 'center' as const, whiteSpace: 'nowrap' }}>
                  {step.label}
                </p>
              </div>
              {!isLast && (
                <div style={{
                  flex: 1, height: 2,
                  background: isDone ? '#1a56db' : '#e2e8f0',
                  margin: '0 4px', marginBottom: 18,
                }} />
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ── Main component ─────────────────────────────────────────────────────────────

export function FirmStatusClient({ token, data, portalAccessStatus, loginUrl, enrollToken }: Props) {
  const [timezone, setTimezone] = useState('UTC');
  const [comments,  setComments] = useState<Comment[]>(data.comments);
  const [message,   setMessage]  = useState('');
  const [files,     setFiles]    = useState<SelectedCareConnectMessageFile[]>([]);
  const [fileError, setFileError] = useState('');
  const [formError, setFormError] = useState('');
  const [sent,      setSent]      = useState(false);
  const [isPending, startTransition] = useTransition();
  const bottomRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [attLoading, setAttLoading] = useState<Record<string, boolean>>({});
  const [attError, setAttError] = useState<Record<string, string | null>>({});

  useEffect(() => { bottomRef.current?.scrollIntoView(); }, []);

  const enrollUrl = enrollToken ? `/enroll?token=${enrollToken}` : '#';
  const hasPortalAccess = portalAccessStatus === ReferrerPortalAccessStatuses.ActiveInTenant;
  const isExistingCrossTenantUser = portalAccessStatus === ReferrerPortalAccessStatuses.ExistingUserOtherTenant;
  const portalCta = isExistingCrossTenantUser
    ? {
        title: 'Link this network to your account',
        description: 'This email already has a CareConnect account. Continue with your existing password to verify it is you, then this network will be added to the same account.',
        primaryLabel: 'Link this network',
        secondaryLabel: 'Log in to another account',
        accent: '#0f766e',
        bg: '#f0fdfa',
        border: '#14b8a6',
        note: 'No new account will be created when the password matches your existing CareConnect account.',
      }
    : {
        title: 'See all your referrals in one place',
        description: 'Create a CareConnect portal account to track all referral statuses, view full patient records, communicate with providers, and generate reports.',
        primaryLabel: 'Get full portal access',
        secondaryLabel: null,
        accent: '#1e3a8a',
        bg: '#fff',
        border: '#1a56db',
        note: null,
      };

  useEffect(() => {
    setTimezone(resolveBrowserTimezone());
  }, []);

  const addMessageFiles = useCallback((incoming: File[]) => {
    const result = makeSelectedCareConnectMessageFiles(incoming, files.length);
    setFileError(result.error ?? '');
    if (result.files.length > 0) {
      setFiles(prev => [...prev, ...result.files]);
    }
  }, [files.length]);

  const removeMessageFile = useCallback((id: string) => {
    setFileError('');
    setFiles(prev => prev.filter(file => file.id !== id));
  }, []);

  const openAttachment = useCallback(async (attachmentId: string) => {
    setAttLoading(prev => ({ ...prev, [attachmentId]: true }));
    setAttError(prev => ({ ...prev, [attachmentId]: null }));
    try {
      const url =
        `/api/public/careconnect/api/referrals/${data.referralId}/public-attachments/${attachmentId}/url` +
        `?token=${encodeURIComponent(token)}&download=false`;
      const res = await fetch(url);
      if (!res.ok) {
        setAttError(prev => ({ ...prev, [attachmentId]: 'Could not load this attachment. Please try again.' }));
        return;
      }
      const body = await res.json() as { url?: string };
      if (!body.url) {
        setAttError(prev => ({ ...prev, [attachmentId]: 'Attachment URL unavailable.' }));
        return;
      }
      window.open(body.url, '_blank', 'noopener,noreferrer');
    } catch {
      setAttError(prev => ({ ...prev, [attachmentId]: 'Network error. Please try again.' }));
    } finally {
      setAttLoading(prev => ({ ...prev, [attachmentId]: false }));
    }
  }, [data.referralId, token]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setFormError('');
    setFileError('');
    setSent(false);
    startTransition(async () => {
      const result = await postPublicThreadComment(token, 'referrer', message, files);
      if (!result.success) { setFormError(result.error ?? 'An error occurred.'); return; }
      if (result.comment) setComments(prev => [...prev, result.comment!]);
      setMessage('');
      setFiles([]);
      setSent(true);
      setTimeout(() => bottomRef.current?.scrollIntoView({ behavior: 'smooth' }), 100);
    });
  };

  const location = formatReferralLocation(data);
  const originationName = referralOriginationName(data.referralAttribution);

  return (
    <div style={s.page}>
      {/* Header */}
      <div style={s.header}>
        <div style={s.headerInner}>
          <p style={s.headerLabel}>LegalSynq CareConnect</p>
          <h1 style={s.headerTitle}>Referral Status</h1>
        </div>
      </div>

      <div style={s.inner}>
        {/* Status tracker */}
        <div style={s.card}>
          <h2 style={s.cardTitle}>Referral Progress</h2>
          <StatusTracker status={data.status} />
          <div style={s.grid2}>
            <FieldBlock label="Patient"   value={data.clientName} />
            <FieldBlock label="Service"   value={data.service} />
            <FieldBlock label="Provider"  value={data.providerName} />
            {location && <FieldBlock label="Provider Location" value={location} />}
            <FieldBlock label="Submitted" value={formatDate(data.createdAtUtc, timezone)} />
            <FieldBlock label="Urgency" value={data.urgency ?? '—'} />
            <FieldBlock label="Type of Treatment" value={data.treatmentTypeName ?? '—'} />
            {originationName && <FieldBlock label="Referral Origination" value={originationName} />}
            <FieldBlock label="Date of Accident" value={data.dateOfAccident ?? '—'} />
            {data.lienCompanyName && <FieldBlock label="Lien Company" value={data.lienCompanyName} />}
            {data.lienCompanyEmail && <FieldBlock label="Lien Company Email" value={data.lienCompanyEmail} />}
          </div>
        </div>

        {/* Portal CTA — login prompt if already active, linking/enrollment panel otherwise */}
        {hasPortalAccess ? (
          <div style={{ ...s.upgradeBox, borderLeft: '4px solid #16a34a', background: '#f0fdf4' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 14, flexWrap: 'wrap' as const }}>
              <p style={{ margin: 0, fontSize: 14, color: '#166534' }}>
                Log in to CareConnect to view all your referrals and track responses in one place.
              </p>
              <a href={loginUrl} style={{ ...s.btnPrimary, background: '#16a34a', whiteSpace: 'nowrap' as const }}>
                Log in to CareConnect
              </a>
            </div>
          </div>
        ) : (
          <div style={{ ...s.upgradeBox, background: portalCta.bg, borderLeft: `4px solid ${portalCta.border}` }}>
            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 14, flexWrap: 'wrap' as const }}>
              <div style={{ flex: 1, minWidth: 200 }}>
                <p style={{ margin: '0 0 4px', fontSize: 14, fontWeight: 700, color: portalCta.accent }}>
                  {portalCta.title}
                </p>
                <p style={{ margin: 0, fontSize: 13, color: '#374151', lineHeight: 1.5 }}>
                  {portalCta.description}
                </p>
                {portalCta.note && (
                  <p style={{ margin: '8px 0 0', fontSize: 12, color: '#0f766e', lineHeight: 1.45 }}>
                    {portalCta.note}
                  </p>
                )}
              </div>
              <div style={{
                display: 'flex',
                flexDirection: 'column' as const,
                gap: 8,
                minWidth: 160,
                alignItems: portalCta.secondaryLabel ? 'stretch' : 'center',
                justifyContent: 'center',
              }}>
                <a
                  href={enrollUrl}
                  onClick={!enrollToken ? (e: React.MouseEvent) => e.preventDefault() : undefined}
                  style={{ ...s.btnPrimary, background: portalCta.border }}
                >
                  {portalCta.primaryLabel}
                </a>
                {portalCta.secondaryLabel && (
                  <a href={loginUrl} style={{ ...s.btnOutline, color: portalCta.border, borderColor: portalCta.border, fontSize: 12, padding: '7px 16px' }}>
                    {portalCta.secondaryLabel}
                  </a>
                )}
              </div>
            </div>
          </div>
        )}

        {/* Message thread */}
        <div style={s.card}>
          <h2 style={s.cardTitle}>Messages</h2>
          <div style={{ height: 420, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 14 }}>
            {comments.length === 0 ? (
              <p style={{ margin: 0, fontSize: 14, color: '#94a3b8', fontStyle: 'italic' }}>
                No messages yet. Use the form below to send a message to the provider.
              </p>
            ) : (
              comments.map(c => (
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
            <div ref={bottomRef} />
          </div>
        </div>

        {/* Send message form — referrer side */}
        <div style={s.card}>
          <h2 style={s.cardTitle}>Send a Message to the Provider</h2>
          {sent && (
            <div style={{ background: '#f0fdf4', border: '1px solid #bbf7d0', borderRadius: 6, padding: '10px 14px', marginBottom: 14 }}>
              <p style={{ margin: 0, fontSize: 14, color: '#166534' }}>Message sent. The provider will receive an email notification.</p>
            </div>
          )}
          {formError && (
            <div style={{ background: '#fef2f2', border: '1px solid #fecaca', borderRadius: 6, padding: '10px 14px', marginBottom: 14 }}>
              <p style={{ margin: 0, fontSize: 14, color: '#991b1b' }}>{formError}</p>
            </div>
          )}
          <form onSubmit={handleSubmit}>
            <div style={{ marginBottom: 18 }}>
              <label style={{ display: 'block', fontSize: 13, fontWeight: 600, color: '#374151', marginBottom: 6 }}>Message</label>
              <textarea
                style={s.textarea}
                value={message}
                onChange={e => setMessage(e.target.value)}
                placeholder="Type your message here…"
                rows={4}
                maxLength={4000}
              />
              <p style={{ margin: '4px 0 0', fontSize: 12, color: '#9ca3af', textAlign: 'right' as const }}>{message.length}/4000</p>
            </div>
            <div style={{ marginBottom: 18 }}>
              <input
                ref={fileInputRef}
                type="file"
                multiple
                accept={CARECONNECT_MESSAGE_ALLOWED_TYPES.join(',')}
                onChange={e => {
                  addMessageFiles(Array.from(e.target.files ?? []));
                  e.target.value = '';
                }}
                style={{ display: 'none' }}
                aria-hidden="true"
                tabIndex={-1}
              />
              <button
                type="button"
                onClick={() => fileInputRef.current?.click()}
                disabled={isPending || files.length >= CARECONNECT_MESSAGE_MAX_FILES}
                style={{
                  display: 'flex',
                  width: '100%',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  gap: 10,
                  border: '1px dashed #cbd5e1',
                  background: '#f8fafc',
                  color: '#475569',
                  borderRadius: 6,
                  padding: '9px 12px',
                  cursor: isPending || files.length >= CARECONNECT_MESSAGE_MAX_FILES ? 'not-allowed' : 'pointer',
                  opacity: isPending || files.length >= CARECONNECT_MESSAGE_MAX_FILES ? 0.65 : 1,
                  fontSize: 13,
                  fontFamily: 'inherit',
                }}
              >
                <span>Attach files</span>
                <span style={{ fontSize: 11, color: '#94a3b8' }}>{files.length}/{CARECONNECT_MESSAGE_MAX_FILES}</span>
              </button>
              {files.length > 0 && (
                <ul style={{ listStyle: 'none', padding: 0, margin: '8px 0 0', display: 'flex', flexDirection: 'column', gap: 6 }}>
                  {files.map(selected => (
                    <li key={selected.id} style={{ display: 'flex', alignItems: 'center', gap: 8, border: '1px solid #e2e8f0', background: '#f8fafc', borderRadius: 6, padding: '6px 8px', fontSize: 12 }}>
                      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: '#334155' }}>
                        {selected.file.name}
                      </span>
                      <span style={{ color: '#94a3b8', flexShrink: 0 }}>{formatCareConnectAttachmentBytes(selected.file.size)}</span>
                      <button
                        type="button"
                        onClick={() => removeMessageFile(selected.id)}
                        disabled={isPending}
                        aria-label={`Remove ${selected.file.name}`}
                        style={{ border: 'none', background: 'transparent', color: '#94a3b8', cursor: isPending ? 'not-allowed' : 'pointer', padding: 2, fontSize: 16, lineHeight: 1 }}
                      >
                        ×
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              {fileError && <p style={{ margin: '6px 0 0', fontSize: 12, color: '#dc2626' }}>{fileError}</p>}
            </div>
            <button type="submit" disabled={isPending} style={{ ...s.btnPrimary, width: '100%', boxSizing: 'border-box' as const, opacity: isPending ? 0.7 : 1, cursor: isPending ? 'not-allowed' : 'pointer' }}>
              {isPending ? 'Sending…' : 'Send Message'}
            </button>
          </form>
        </div>

        <p style={{ textAlign: 'center', marginTop: 8, marginBottom: 24, fontSize: 12, color: '#94a3b8' }}>
          Accessible only with the secure link from your referral confirmation email.
        </p>
      </div>
    </div>
  );
}

function FieldBlock({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p style={{ margin: '0 0 2px', fontSize: 11, fontWeight: 600, color: '#94a3b8', textTransform: 'uppercase', letterSpacing: '0.05em' }}>{label}</p>
      <p style={{ margin: 0, fontSize: 14, color: '#0f172a', fontWeight: 500 }}>{value || '—'}</p>
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
  onOpenAttachment: (attachmentId: string) => void;
  attLoading: Record<string, boolean>;
  attError: Record<string, string | null>;
}) {
  const isProvider = comment.senderType === 'provider';
  const isSelf = !isProvider;
  const attachments = comment.attachments ?? [];
  return (
    <div style={{ display: 'flex', flexDirection: isSelf ? 'row-reverse' : 'row', gap: 10, alignItems: 'flex-start' }}>
      <div style={{
        width: 34, height: 34, borderRadius: '50%', flexShrink: 0,
        background: isProvider ? '#dbeafe' : '#fef3c7',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontSize: 14, fontWeight: 700,
        color: isProvider ? '#1d4ed8' : '#92400e',
      }}>
        {comment.senderName.charAt(0).toUpperCase()}
      </div>
      <div style={{ maxWidth: '80%' }}>
        <div style={{ display: 'flex', gap: 8, alignItems: 'baseline', flexDirection: isSelf ? 'row-reverse' : 'row', marginBottom: 4 }}>
          <span style={{ fontSize: 13, fontWeight: 600, color: '#374151' }}>{comment.senderName}</span>
          <span style={{ fontSize: 11, color: '#9ca3af' }}>{formatDate(comment.createdAtUtc, timezone)}</span>
        </div>
        {comment.message.trim().length > 0 && (
          <div style={{
            background: isProvider ? '#eff6ff' : '#fef3c7',
            border: `1px solid ${isProvider ? '#bfdbfe' : '#fde68a'}`,
            borderRadius: isSelf ? '12px 4px 12px 12px' : '4px 12px 12px 12px',
            padding: '10px 14px',
          }}>
            <p style={{ margin: 0, fontSize: 14, color: '#111827', lineHeight: 1.6, whiteSpace: 'pre-wrap' }}>{comment.message}</p>
          </div>
        )}
        {attachments.length > 0 && (
          <div style={{ marginTop: 8, display: 'flex', flexDirection: 'column', gap: 6, alignItems: isSelf ? 'flex-end' : 'flex-start' }}>
            {attachments.map(att => {
              const loading = attLoading[att.id] ?? false;
              const error = attError[att.id] ?? null;
              return (
                <div key={att.id} style={{ maxWidth: '100%' }}>
                  <button
                    type="button"
                    onClick={() => onOpenAttachment(att.id)}
                    disabled={loading}
                    style={{
                      display: 'inline-flex',
                      alignItems: 'center',
                      gap: 6,
                      maxWidth: '100%',
                      border: '1px solid #e2e8f0',
                      background: '#fff',
                      color: '#334155',
                      borderRadius: 6,
                      padding: '5px 8px',
                      fontSize: 12,
                      cursor: loading ? 'wait' : 'pointer',
                      opacity: loading ? 0.7 : 1,
                    }}
                    title={`View ${att.fileName}`}
                  >
                    <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{att.fileName}</span>
                    <span style={{ color: '#94a3b8', flexShrink: 0 }}>{loading ? 'Opening...' : formatCareConnectAttachmentBytes(att.fileSizeBytes)}</span>
                  </button>
                  {error && <p style={{ margin: '4px 0 0', fontSize: 12, color: '#dc2626' }}>{error}</p>}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
