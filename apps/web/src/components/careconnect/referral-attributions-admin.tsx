'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { careConnectApi } from '@/lib/careconnect-api';
import { ApiError } from '@/lib/api-client';
import type { ReferralAttribution } from '@/types/careconnect';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';

interface FormState {
  firstName:     string;
  lastName:      string;
  code:          string;
  description:   string;
  displayOrder:  string;
}

const EMPTY_FORM: FormState = { firstName: '', lastName: '', code: '', description: '', displayOrder: '' };

const PAGE_SIZE = 10;

/**
 * Deliberately minimal list: First Name / Last Name / Status only. Every other
 * field, plus Edit and the access-code widget, live on the origination's own
 * detail page (/careconnect/referral-attributions/{id}) — reached via the row's
 * kebab menu "View" action. Keeps this list scannable and avoids duplicating
 * the edit/access-code surfaces in two places.
 */
export function ReferralAttributionsAdmin() {
  const router = useRouter();
  const [items, setItems]         = useState<ReferralAttribution[] | null>(null);
  const [error, setError]         = useState<string | null>(null);
  const [form, setForm]           = useState<FormState | null>(null);
  const [saving, setSaving]       = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [page, setPage]           = useState(1);

  const [portalUrl, setPortalUrl]     = useState('');
  const [portalUrlCopied, setPortalUrlCopied] = useState(false);

  useEffect(() => {
    setPortalUrl(window.location.origin + '/careconnect/referral/dashboard');
  }, []);

  async function copyPortalUrl() {
    if (!portalUrl) return;
    try {
      await navigator.clipboard.writeText(portalUrl);
      setPortalUrlCopied(true);
      setTimeout(() => setPortalUrlCopied(false), 2000);
    } catch {
      // Clipboard access can fail (permissions, non-secure context) — the URL stays
      // visible on screen either way, so this is a soft failure, not a blocker.
    }
  }

  async function load() {
    try {
      const { data } = await careConnectApi.referralAttributions.list();
      setItems(data);
      setError(null);
      setPage(1);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load Referral Originations.');
    }
  }

  useEffect(() => { load(); }, []);

  const totalCount = items?.length ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  const pageItems   = items?.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE) ?? [];

  function startCreate() {
    setForm({ ...EMPTY_FORM });
    setFormError(null);
  }

  async function handleSubmit() {
    if (!form) return;
    setSaving(true);
    setFormError(null);
    try {
      const displayOrder = form.displayOrder.trim() ? Number(form.displayOrder) : undefined;
      await careConnectApi.referralAttributions.create({
        firstName: form.firstName.trim(),
        lastName: form.lastName.trim(),
        code: form.code.trim(),
        description: form.description.trim() || undefined,
        displayOrder,
        isActive: true,
      });
      setForm(null);
      await load();
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : 'Failed to save Referral Origination.');
    } finally {
      setSaving(false);
    }
  }

  async function toggleActive(a: ReferralAttribution) {
    try {
      await careConnectApi.referralAttributions.setActive(a.id, !a.isActive);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to update status.');
    }
  }

  return (
    <div className="space-y-4">
      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {portalUrl && (
        <div className="flex items-center gap-2">
          <span className="text-xs font-medium text-gray-400 shrink-0">Referral Portal URL</span>
          <div className="flex items-center gap-1 rounded-md border border-gray-200 bg-gray-50 px-2.5 py-1 min-w-0">
            <i className="ri-link text-gray-400 text-xs shrink-0" />
            <span className="text-xs text-gray-600 font-mono truncate">{portalUrl}</span>
          </div>
          <button
            onClick={copyPortalUrl}
            title="Copy portal URL"
            className="shrink-0 inline-flex items-center gap-1 rounded-md border border-gray-200 bg-white px-2 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50 transition-colors cursor-pointer"
          >
            <i className={portalUrlCopied ? 'ri-check-line text-green-600' : 'ri-clipboard-line'} />
            {portalUrlCopied ? 'Copied!' : 'Copy'}
          </button>
          <a
            href={portalUrl}
            target="_blank"
            rel="noopener noreferrer"
            title="Open portal URL in new tab"
            className="shrink-0 inline-flex items-center gap-1 rounded-md border border-gray-200 bg-white px-2 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50 transition-colors cursor-pointer"
          >
            <i className="ri-external-link-line" />
            Open
          </a>
        </div>
      )}

      <div className="flex justify-end">
        {!form && (
          <button
            onClick={startCreate}
            className="bg-primary text-white text-sm font-medium px-4 py-2 rounded-md hover:opacity-90 transition-opacity cursor-pointer"
          >
            + Add Origination
          </button>
        )}
      </div>

      {form && (
        <div className="bg-white border border-gray-200 rounded-lg p-5 space-y-3">
          <h3 className="text-sm font-semibold text-gray-900">Add Referral Origination</h3>
          {formError && <p className="text-xs text-red-600">{formError}</p>}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-xs font-medium text-gray-700 mb-1">First Name</label>
              <input
                value={form.firstName}
                onChange={e => setForm(f => f && { ...f, firstName: e.target.value })}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                placeholder="Enter first name"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-700 mb-1">Last Name</label>
              <input
                value={form.lastName}
                onChange={e => setForm(f => f && { ...f, lastName: e.target.value })}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                placeholder="Enter last name"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-700 mb-1">Code</label>
              <input
                value={form.code}
                onChange={e => setForm(f => f && { ...f, code: e.target.value })}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                placeholder="Enter code"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-700 mb-1">Display Order</label>
              <input
                type="number"
                value={form.displayOrder}
                onChange={e => setForm(f => f && { ...f, displayOrder: e.target.value })}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
              />
            </div>
            <div className="sm:col-span-2">
              <label className="block text-xs font-medium text-gray-700 mb-1">Description</label>
              <textarea
                value={form.description}
                onChange={e => setForm(f => f && { ...f, description: e.target.value })}
                rows={2}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm resize-none"
              />
            </div>
          </div>
          <div className="flex justify-end gap-2 pt-1">
            <button
              onClick={() => setForm(null)}
              disabled={saving}
              className="text-sm text-gray-600 hover:text-gray-900 px-4 py-2 cursor-pointer disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              onClick={handleSubmit}
              disabled={saving || !form.firstName.trim() || !form.lastName.trim() || !form.code.trim()}
              className="bg-primary text-white text-sm font-medium px-4 py-2 rounded-md hover:opacity-90 disabled:opacity-50 cursor-pointer disabled:cursor-not-allowed"
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
          </div>
        </div>
      )}

      {items === null ? (
        <p className="text-sm text-gray-500">Loading…</p>
      ) : items.length === 0 ? (
        <div className="bg-white border border-gray-200 rounded-lg p-8 text-center text-sm text-gray-500">
          No Referral Originations configured yet.
        </div>
      ) : (
        <div className="bg-white border border-gray-200 rounded-lg overflow-hidden">
          <table className="w-full text-left">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50">
                <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">First Name</th>
                <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Last Name</th>
                <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Status</th>
                <th className="px-4 py-2.5" />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {pageItems.map(a => (
                <tr key={a.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3 text-sm text-gray-900">{a.firstName}</td>
                  <td className="px-4 py-3 text-sm text-gray-900">{a.lastName}</td>
                  <td className="px-4 py-3">
                    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                      a.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-600'
                    }`}>
                      {a.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-right">
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <button
                          title="Actions"
                          aria-label="Actions"
                          className="p-1.5 rounded hover:bg-gray-100 text-gray-400 hover:text-gray-700 transition-colors cursor-pointer"
                        >
                          <i className="ri-more-2-fill text-base" />
                        </button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem onClick={() => router.push(`/careconnect/referral-attributions/${a.id}`)}>
                          <i className="ri-eye-line" />
                          View
                        </DropdownMenuItem>
                        <DropdownMenuItem onClick={() => toggleActive(a)}>
                          <i className={a.isActive ? 'ri-close-circle-line' : 'ri-checkbox-circle-line'} />
                          {a.isActive ? 'Deactivate' : 'Activate'}
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="border-t border-gray-100 px-4 py-3 flex items-center justify-between">
            <span className="text-sm text-gray-500">
              Showing {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, totalCount)} of {totalCount.toLocaleString()}
            </span>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page <= 1}
                className="text-sm text-gray-600 hover:text-gray-900 cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
              >
                ← Prev
              </button>
              <span className="text-sm text-gray-500">Page {page} of {totalPages}</span>
              <button
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                className="text-sm text-gray-600 hover:text-gray-900 cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
              >
                Next →
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
