'use client';

import { useEffect, useState } from 'react';
import { careConnectApi } from '@/lib/careconnect-api';
import { ApiError } from '@/lib/api-client';
import type { ReferralAttribution, ReferralAttributionAccessCode, GeneratedReferralAttributionAccessCode } from '@/types/careconnect';

interface EditFormState {
  firstName:    string;
  lastName:     string;
  description:  string;
  displayOrder: string;
}

interface CodeFormState {
  accessStartAt: string;
  accessEndAt:   string;
}

const EMPTY_CODE_FORM: CodeFormState = { accessStartAt: '', accessEndAt: '' };

export function ReferralAttributionDetail({ id }: { id: string }) {
  const [attribution, setAttribution] = useState<ReferralAttribution | null>(null);
  const [error, setError]             = useState<string | null>(null);
  const [editing, setEditing]         = useState(false);
  const [editForm, setEditForm]       = useState<EditFormState | null>(null);
  const [saving, setSaving]           = useState(false);
  const [editError, setEditError]     = useState<string | null>(null);

  const [activeCode, setActiveCode]   = useState<ReferralAttributionAccessCode | null | undefined>(undefined);
  const [showCodeForm, setShowCodeForm] = useState(false);
  const [codeForm, setCodeForm]       = useState<CodeFormState>(EMPTY_CODE_FORM);
  const [codeSaving, setCodeSaving]   = useState(false);
  const [codeError, setCodeError]     = useState<string | null>(null);
  const [revealed, setRevealed]       = useState<GeneratedReferralAttributionAccessCode | null>(null);
  const [copied, setCopied]           = useState(false);

  async function loadAttribution() {
    try {
      const { data } = await careConnectApi.referralAttributions.getById(id);
      setAttribution(data);
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load Referral Origination.');
    }
  }

  async function loadCode() {
    try {
      const { data } = await careConnectApi.referralAttributionAccessCodes.getByAttribution(id);
      setActiveCode(data ?? null);
    } catch (err) {
      setCodeError(err instanceof ApiError ? err.message : 'Failed to load access code.');
    }
  }

  useEffect(() => { loadAttribution(); loadCode(); }, [id]); // eslint-disable-line react-hooks/exhaustive-deps

  function startEdit() {
    if (!attribution) return;
    setEditForm({
      firstName: attribution.firstName,
      lastName: attribution.lastName,
      description: attribution.description ?? '',
      displayOrder: attribution.displayOrder?.toString() ?? '',
    });
    setEditError(null);
    setEditing(true);
  }

  async function saveEdit() {
    if (!editForm) return;
    setSaving(true);
    setEditError(null);
    try {
      const displayOrder = editForm.displayOrder.trim() ? Number(editForm.displayOrder) : undefined;
      const { data } = await careConnectApi.referralAttributions.update(id, {
        firstName: editForm.firstName.trim(),
        lastName: editForm.lastName.trim(),
        description: editForm.description.trim() || undefined,
        displayOrder,
      });
      setAttribution(data);
      setEditing(false);
    } catch (err) {
      setEditError(err instanceof ApiError ? err.message : 'Failed to save changes.');
    } finally {
      setSaving(false);
    }
  }

  async function toggleActive() {
    if (!attribution) return;
    try {
      const { data } = await careConnectApi.referralAttributions.setActive(id, !attribution.isActive);
      setAttribution(data);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to update status.');
    }
  }

  async function handleGenerate() {
    setCodeSaving(true);
    setCodeError(null);
    try {
      const { data } = await careConnectApi.referralAttributionAccessCodes.generate({
        referralAttributionId: id,
        accessStartAtUtc: codeForm.accessStartAt || undefined,
        accessEndAtUtc: codeForm.accessEndAt || undefined,
      });
      setShowCodeForm(false);
      setCodeForm(EMPTY_CODE_FORM);
      setRevealed(data);
      setCopied(false);
      await loadCode();
    } catch (err) {
      setCodeError(err instanceof ApiError ? err.message : 'Failed to generate access code.');
    } finally {
      setCodeSaving(false);
    }
  }

  async function handleRevoke() {
    if (!activeCode) return;
    try {
      await careConnectApi.referralAttributionAccessCodes.setActive(activeCode.id, false);
      // Clear the reveal-once panel — it would otherwise keep showing the just-revoked
      // code with nothing on screen indicating it's no longer valid.
      setRevealed(null);
      await loadCode();
    } catch (err) {
      setCodeError(err instanceof ApiError ? err.message : 'Failed to revoke access code.');
    }
  }

  async function copyCode() {
    if (!revealed) return;
    try {
      await navigator.clipboard.writeText(revealed.code);
      setCopied(true);
    } catch {
      // Clipboard access can fail (permissions, non-secure context) — the code stays
      // visible on screen either way, so this is a soft failure, not a blocker.
    }
  }

  if (error) {
    return <div className="bg-red-50 border border-red-200 rounded-lg px-4 py-3 text-sm text-red-700">{error}</div>;
  }

  if (!attribution) {
    return <p className="text-sm text-gray-500">Loading…</p>;
  }

  return (
    <div className="space-y-4">
      <div className="bg-white border border-gray-200 rounded-lg p-5">
        <div className="flex items-start justify-between mb-4">
          <div>
            <h1 className="text-lg font-semibold text-gray-900">{attribution.firstName} {attribution.lastName}</h1>
            <p className="text-sm font-mono text-gray-500 mt-0.5">{attribution.code}</p>
          </div>
          <div className="flex items-center gap-2">
            <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
              attribution.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-600'
            }`}>
              {attribution.isActive ? 'Active' : 'Inactive'}
            </span>
            {!editing && (
              <>
                <button
                  onClick={startEdit}
                  title="Edit"
                  aria-label="Edit"
                  className="p-1.5 rounded hover:bg-gray-100 text-gray-400 hover:text-primary transition-colors cursor-pointer"
                >
                  <i className="ri-edit-line text-base" />
                </button>
                <button
                  onClick={toggleActive}
                  title={attribution.isActive ? 'Deactivate' : 'Activate'}
                  aria-label={attribution.isActive ? 'Deactivate' : 'Activate'}
                  className={`p-1.5 rounded hover:bg-gray-100 text-gray-400 transition-colors cursor-pointer ${
                    attribution.isActive ? 'hover:text-red-600' : 'hover:text-green-600'
                  }`}
                >
                  <i className={`text-base ${attribution.isActive ? 'ri-close-circle-line' : 'ri-checkbox-circle-line'}`} />
                </button>
              </>
            )}
          </div>
        </div>

        {editing && editForm ? (
          <div className="space-y-3 border-t border-gray-100 pt-4">
            {editError && <p className="text-xs text-red-600">{editError}</p>}
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label className="block text-xs font-medium text-gray-700 mb-1">First Name</label>
                <input
                  value={editForm.firstName}
                  onChange={e => setEditForm(f => f && { ...f, firstName: e.target.value })}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-gray-700 mb-1">Last Name</label>
                <input
                  value={editForm.lastName}
                  onChange={e => setEditForm(f => f && { ...f, lastName: e.target.value })}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-gray-700 mb-1">Display Order</label>
                <input
                  type="number"
                  value={editForm.displayOrder}
                  onChange={e => setEditForm(f => f && { ...f, displayOrder: e.target.value })}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                />
              </div>
              <div className="sm:col-span-2">
                <label className="block text-xs font-medium text-gray-700 mb-1">Description</label>
                <textarea
                  value={editForm.description}
                  onChange={e => setEditForm(f => f && { ...f, description: e.target.value })}
                  rows={2}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm resize-none"
                />
              </div>
            </div>
            <div className="flex justify-end gap-2">
              <button
                onClick={() => setEditing(false)}
                disabled={saving}
                className="text-sm text-gray-600 hover:text-gray-900 px-4 py-2 cursor-pointer disabled:cursor-not-allowed disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={saveEdit}
                disabled={saving || !editForm.firstName.trim() || !editForm.lastName.trim()}
                className="bg-primary text-white text-sm font-medium px-4 py-2 rounded-md hover:opacity-90 disabled:opacity-50 cursor-pointer disabled:cursor-not-allowed"
              >
                {saving ? 'Saving…' : 'Save'}
              </button>
            </div>
          </div>
        ) : (
          <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-4 border-t border-gray-100 pt-4">
            <div>
              <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">Description</dt>
              <dd className="mt-1 text-sm text-gray-900">{attribution.description ?? '—'}</dd>
            </div>
            <div>
              <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">Display Order</dt>
              <dd className="mt-1 text-sm text-gray-900">{attribution.displayOrder ?? '—'}</dd>
            </div>
            <div>
              <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">Used on a referral</dt>
              <dd className="mt-1 text-sm text-gray-900">{attribution.isUsed ? 'Yes' : 'No'}</dd>
            </div>
            <div>
              <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">Created</dt>
              <dd className="mt-1 text-sm text-gray-900">{new Date(attribution.createdAtUtc).toLocaleString()}</dd>
            </div>
            <div>
              <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">Last Updated</dt>
              <dd className="mt-1 text-sm text-gray-900">{new Date(attribution.updatedAtUtc).toLocaleString()}</dd>
            </div>
          </dl>
        )}
      </div>

      <div className="bg-white border border-gray-200 rounded-lg p-5">
        <h2 className="text-sm font-semibold text-gray-900 mb-1">Representative Access Code</h2>
        <p className="text-xs text-gray-500 mb-4">
          Generate a code and share it with the representative who should see this source&apos;s referrals — they
          enter it themselves at the portal URL shown on the Referral Originations list page.
        </p>

        {codeError && <p className="text-xs text-red-600 mb-3">{codeError}</p>}

        {!attribution.isActive && (activeCode || revealed) && (
          <div className="bg-amber-50 border border-amber-200 rounded-md px-3 py-2 text-xs text-amber-800 mb-3">
            This origination is deactivated — its access code will not grant viewing access until it&apos;s reactivated.
          </div>
        )}

        {revealed && (
          <div className="bg-indigo-50 border border-indigo-200 rounded-lg p-4 space-y-3 mb-4">
            <p className="text-xs text-gray-600">
              This code will not be shown again — copy it now and share it securely with the representative.
            </p>
            <div className="flex items-center gap-2">
              <code className="flex-1 bg-white border border-indigo-200 rounded-md px-4 py-2.5 text-lg font-mono tracking-wider text-gray-900">
                {revealed.code}
              </code>
              <button
                onClick={copyCode}
                className="bg-primary text-white text-sm font-medium px-4 py-2.5 rounded-md hover:opacity-90 transition-opacity cursor-pointer"
              >
                {copied ? 'Copied!' : 'Copy'}
              </button>
            </div>
          </div>
        )}

        {activeCode === undefined ? (
          <p className="text-sm text-gray-500">Loading…</p>
        ) : activeCode === null ? (
          showCodeForm ? (
            <div className="space-y-3">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                  <label className="block text-xs font-medium text-gray-700 mb-1">Access Start (optional)</label>
                  <input
                    type="date"
                    value={codeForm.accessStartAt}
                    onChange={e => setCodeForm(f => ({ ...f, accessStartAt: e.target.value }))}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                  />
                </div>
                <div>
                  <label className="block text-xs font-medium text-gray-700 mb-1">Access End (optional)</label>
                  <input
                    type="date"
                    value={codeForm.accessEndAt}
                    onChange={e => setCodeForm(f => ({ ...f, accessEndAt: e.target.value }))}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
                  />
                </div>
              </div>
              <p className="text-xs text-gray-500">
                Leave both blank for access starting immediately with no end date, or set a window to
                control when the code grants access.
              </p>
              <div className="flex justify-end gap-2">
                <button
                  onClick={() => setShowCodeForm(false)}
                  disabled={codeSaving}
                  className="text-sm text-gray-600 hover:text-gray-900 px-4 py-2 cursor-pointer disabled:cursor-not-allowed disabled:opacity-50"
                >
                  Cancel
                </button>
                <button
                  onClick={handleGenerate}
                  disabled={codeSaving}
                  className="bg-primary text-white text-sm font-medium px-4 py-2 rounded-md hover:opacity-90 disabled:opacity-50 cursor-pointer disabled:cursor-not-allowed"
                >
                  {codeSaving ? 'Generating…' : 'Generate'}
                </button>
              </div>
            </div>
          ) : (
            <button
              onClick={() => setShowCodeForm(true)}
              className="bg-primary text-white text-sm font-medium px-4 py-2 rounded-md hover:opacity-90 transition-opacity cursor-pointer"
            >
              + Generate Access Code
            </button>
          )
        ) : (
          <div className="flex items-center justify-between">
            <div className="text-sm text-gray-700 space-y-1">
              <p>
                <span className="font-medium">Status:</span> Active
              </p>
              <p className="text-xs text-gray-500">
                {activeCode.accessStartAtUtc || activeCode.accessEndAtUtc
                  ? `${activeCode.accessStartAtUtc ? new Date(activeCode.accessStartAtUtc).toLocaleDateString() : '—'} → ${activeCode.accessEndAtUtc ? new Date(activeCode.accessEndAtUtc).toLocaleDateString() : '—'}`
                  : 'Access window: Always'}
              </p>
            </div>
            <button
              onClick={handleRevoke}
              className="text-sm text-red-600 hover:text-red-700 font-medium cursor-pointer"
            >
              Revoke
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
