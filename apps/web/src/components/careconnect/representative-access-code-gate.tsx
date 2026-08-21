'use client';

import { createContext, useContext, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import Image from 'next/image';
import { ApiError } from '@/lib/api-client';
import { verifyRepresentativeAccessCode } from '@/lib/representative-portal-api';

const STORAGE_KEY_PREFIX = 'cc-representative-code:';

interface RepresentativePortalContextValue {
  /** The raw access code — resent on every data request; never persisted beyond localStorage. */
  code:                         string;
  referralAttributionId:        string;
  referralAttributionFullName:  string;
  /** Tenant display name, resolved server-side in the layout — used to label the tenant's own network as "{tenantName} Preferred Providers". */
  tenantDisplayName:            string;
  /** Clears the stored code and returns to the gate. There is no "sign out" — no session exists to end. */
  lock:                         () => void;
}

const RepresentativePortalContext = createContext<RepresentativePortalContextValue | null>(null);

export function useRepresentativePortal(): RepresentativePortalContextValue {
  const ctx = useContext(RepresentativePortalContext);
  if (!ctx) throw new Error('useRepresentativePortal must be used within RepresentativeAccessCodeGate');
  return ctx;
}

interface Props {
  tenantId: string;
  tenantDisplayName: string;
  children: ReactNode;
}

/**
 * Fully anonymous gate for the Referral Portal — no login, matching the
 * public network directory's AccessCodeGate branding/UX. Unlike that gate (which caches a
 * one-time client-side "unlocked" boolean while the actual data endpoints stay open
 * regardless), this one persists the raw code itself and every page under it resends that
 * code on every data request; the backend re-verifies it from scratch each time (see
 * PublicRepresentativeEndpoints). Revoking a code or deactivating its attribution takes
 * effect on the very next request, not on next unlock.
 */
export function RepresentativeAccessCodeGate({ tenantId, tenantDisplayName, children }: Props) {
  const [ready, setReady] = useState(false);
  const [unlocked, setUnlocked] = useState<Omit<RepresentativePortalContextValue, 'lock' | 'tenantDisplayName'> | null>(null);
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const storageKey = `${STORAGE_KEY_PREFIX}${tenantId}`;

  useEffect(() => {
    let cancelled = false;

    async function loadStoredCode() {
      const stored = localStorage.getItem(storageKey);
      if (!stored) {
        setReady(true);
        return;
      }

      try {
        const { data } = await verifyRepresentativeAccessCode(stored);
        if (cancelled) return;

        if (data.ok && data.referralAttributionId) {
          setUnlocked({
            code: stored,
            referralAttributionId: data.referralAttributionId,
            referralAttributionFullName: data.referralAttributionFullName ?? '',
          });
        } else {
          localStorage.removeItem(storageKey);
        }
      } catch {
        // Network hiccup on load — don't discard a possibly-still-valid stored code,
        // just fall through to the gate; the user can retry.
      } finally {
        if (!cancelled) setReady(true);
      }
    }

    void loadStoredCode();
    return () => { cancelled = true; };
  }, [storageKey]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setSubmitting(true);
    setError('');
    const trimmed = code.trim();
    try {
      const { data } = await verifyRepresentativeAccessCode(trimmed);
      if (!data.ok || !data.referralAttributionId) {
        setError('This code is invalid, has expired, or no longer grants access.');
        setCode('');
        return;
      }
      localStorage.setItem(storageKey, trimmed);
      setUnlocked({
        code: trimmed,
        referralAttributionId: data.referralAttributionId,
        referralAttributionFullName: data.referralAttributionFullName ?? '',
      });
      setCode('');
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unable to verify access right now. Please try again.');
    } finally {
      setSubmitting(false);
    }
  }

  function lock() {
    localStorage.removeItem(storageKey);
    setUnlocked(null);
  }

  if (!ready) return null;

  if (unlocked) {
    return (
      <RepresentativePortalContext.Provider value={{ ...unlocked, tenantDisplayName, lock }}>
        {children}
      </RepresentativePortalContext.Provider>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center" style={{ background: '#f3f4f6' }}>
      <div className="bg-white rounded-2xl shadow-2xl w-full max-w-sm mx-4 overflow-hidden">
        {/* Header strip */}
        <div className="bg-gradient-to-r from-slate-900 to-slate-800 px-8 py-6 flex flex-col items-center gap-3">
          <Image
            src="/careconnect-logo.png"
            alt="CareConnect"
            width={140}
            height={36}
            style={{ width: 140, height: 'auto' }}
            className="object-contain"
            priority
          />
          <p className="text-slate-300 text-xs tracking-wide text-center">
            Referral Portal
          </p>
        </div>

        {/* Body */}
        <form onSubmit={handleSubmit} className="px-8 py-7 flex flex-col gap-5">
          <div>
            <h2 className="text-base font-semibold text-gray-900 text-center">
              Enter Access Code
            </h2>
            <p className="text-xs text-gray-500 text-center mt-1">
              Enter the access code provided by your tenant administrator to view referrals
              attributed to you.
            </p>
          </div>

          <div className="flex flex-col gap-1.5">
            <label htmlFor="rep-access-code" className="text-xs font-medium text-gray-700">
              Access Code
            </label>
            <input
              id="rep-access-code"
              type="password"
              autoFocus
              autoComplete="off"
              value={code}
              onChange={e => { setCode(e.target.value); setError(''); }}
              placeholder="Enter access code"
              className="w-full rounded-lg border border-gray-300 px-3.5 py-2.5 text-sm text-gray-900 placeholder-gray-400 shadow-sm transition focus:outline-none focus:ring-2 focus:ring-orange-500 focus:border-transparent"
            />
            {error && (
              <p className="text-xs text-red-600 flex items-center gap-1 mt-0.5">
                <i className="ri-error-warning-line" />
                {error}
              </p>
            )}
          </div>

          <button
            type="submit"
            disabled={!code.trim() || submitting}
            className="w-full cursor-pointer rounded-lg text-sm font-semibold py-2.5 transition-colors focus:outline-none focus:ring-2 focus:ring-orange-500 focus:ring-offset-2 bg-orange-500 text-white hover:bg-orange-600 disabled:cursor-not-allowed disabled:bg-orange-300"
          >
            {submitting ? 'Verifying…' : 'View My Referrals'}
          </button>
        </form>
      </div>
    </div>
  );
}
