'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { careConnectApi } from '@/lib/careconnect-api';
import { FormModal } from '@/components/ui/modal';
import { networkDisplayName } from '@/lib/network-display-name';
import type { NetworkSummary } from '@/types/careconnect';

interface NetworkListClientProps {
  initialNetworks: NetworkSummary[];
  /** LSV3-1084: tenant display name used for the "{tenantName} Preferred Providers" row label. */
  tenantName: string;
  /** True for NetworkManager / TenantAdmin / PlatformAdmin — can rename/delete any network. */
  canManageAll: boolean;
  /** The caller's own organization id — a CareConnectReferrerAdmin without canManageAll may only rename/delete a network their org created. */
  callerOrgId?: string | null;
}

export function NetworkListClient({ initialNetworks, tenantName, canManageAll, callerOrgId }: NetworkListClientProps) {
  const router = useRouter();
  const [networks, setNetworks] = useState<NetworkSummary[]>(initialNetworks);
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formName, setFormName] = useState('');
  const [formDescription, setFormDescription] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  function openCreate() {
    setFormName('');
    setFormDescription('');
    setError(null);
    setEditingId(null);
    setShowCreateForm(true);
  }

  function openEdit(network: NetworkSummary) {
    setFormName(network.name);
    setFormDescription(network.description);
    setError(null);
    setEditingId(network.id);
    setShowCreateForm(true);
  }

  function cancelForm() {
    setShowCreateForm(false);
    setEditingId(null);
    setError(null);
  }

  async function handleSubmit() {
    if (!formName.trim()) {
      setError('Network name is required.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      if (editingId) {
        const { data: updated } = await careConnectApi.networks.update(editingId, {
          name: formName.trim(),
          description: formDescription.trim(),
        });
        setNetworks(prev => prev.map(n => n.id === editingId ? updated : n));
      } else {
        const { data: created } = await careConnectApi.networks.create({
          name: formName.trim(),
          description: formDescription.trim(),
        });
        setNetworks(prev => [...prev, created]);
      }
      setShowCreateForm(false);
      setEditingId(null);
    } catch {
      setError('Failed to save network. Please try again.');
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(id: string) {
    if (!confirm('Delete this network? Providers in the network will be removed.')) return;
    setDeletingId(id);
    try {
      await careConnectApi.networks.delete(id);
      setNetworks(prev => prev.filter(n => n.id !== id));
    } catch {
      alert('Failed to delete network. Please try again.');
    } finally {
      setDeletingId(null);
    }
  }

  const tenantNetworks = networks.filter(n => !n.owningOrganizationId);
  const orgNetworks = networks.filter(n => n.owningOrganizationId);

  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600">
          {networks.length === 0 ? 'No networks yet.' : `${networks.length} network${networks.length === 1 ? '' : 's'}`}
        </p>
        <button
          onClick={openCreate}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 cursor-pointer"
        >
          <i className="ri-add-line" />
          New Network
        </button>
      </div>

      {/* New/Edit Network modal */}
      <FormModal
        open={showCreateForm}
        onClose={cancelForm}
        onSubmit={handleSubmit}
        title={editingId ? 'Edit Network' : 'New Network'}
        submitLabel={editingId ? 'Update' : 'Create'}
        submitDisabled={!formName.trim()}
        loading={saving}
        size="sm"
      >
        <form onSubmit={e => { e.preventDefault(); handleSubmit(); }} className="space-y-3">
          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">Name *</label>
            <input
              type="text"
              value={formName}
              onChange={e => setFormName(e.target.value)}
              maxLength={200}
              placeholder="e.g. Southern California PT Network"
              className="w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">Description</label>
            <textarea
              value={formDescription}
              onChange={e => setFormDescription(e.target.value)}
              rows={2}
              maxLength={1000}
              placeholder="Optional description..."
              className="w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
            />
          </div>
          {error && (
            <p className="text-xs text-red-600">{error}</p>
          )}
        </form>
      </FormModal>

      {/* Network list */}
      {networks.length === 0 && !showCreateForm ? (
        <div className="rounded-lg border-2 border-dashed border-gray-200 py-12 text-center">
          <i className="ri-share-forward-2-line text-3xl text-gray-300" />
          <p className="mt-2 text-sm text-gray-500">No networks yet. Create one to group providers.</p>
          <button
            onClick={openCreate}
            className="mt-4 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 cursor-pointer"
          >
            Create First Network
          </button>
        </div>
      ) : (
        <div className="space-y-6">
          {tenantNetworks.length > 0 && (
            <NetworkSection
              icon="ri-building-4-line"
              label="Tenant Provider Network"
              description="Shared network managed by the tenant admin."
              networks={tenantNetworks}
              tenantName={tenantName}
              canManageAll={canManageAll}
              callerOrgId={callerOrgId}
              router={router}
              openEdit={openEdit}
              handleDelete={handleDelete}
              deletingId={deletingId}
            />
          )}
          {orgNetworks.length > 0 && (
            <NetworkSection
              icon="ri-briefcase-4-line"
              label="Law Firm Provider Networks"
              description="Networks created and managed by your own organization."
              networks={orgNetworks}
              tenantName={tenantName}
              canManageAll={canManageAll}
              callerOrgId={callerOrgId}
              router={router}
              openEdit={openEdit}
              handleDelete={handleDelete}
              deletingId={deletingId}
            />
          )}
        </div>
      )}
    </div>
  );
}

function NetworkSection({
  icon,
  label,
  description,
  networks,
  tenantName,
  canManageAll,
  callerOrgId,
  router,
  openEdit,
  handleDelete,
  deletingId,
}: {
  icon: string;
  label: string;
  description: string;
  networks: NetworkSummary[];
  tenantName: string;
  canManageAll: boolean;
  callerOrgId?: string | null;
  router: ReturnType<typeof useRouter>;
  openEdit: (network: NetworkSummary) => void;
  handleDelete: (id: string) => void;
  deletingId: string | null;
}) {
  return (
    <div>
      <div className="flex items-center gap-2 mb-2">
        <i className={`${icon} text-gray-400 text-base`} />
        <h2 className="text-sm font-semibold text-gray-700">{label}</h2>
        <span className="text-xs text-gray-400">({networks.length})</span>
      </div>
      <p className="text-xs text-gray-400 mb-2">{description}</p>
      <div className="divide-y divide-gray-100 rounded-lg border border-gray-200 bg-white overflow-hidden">
        {networks.map(network => (
          <div key={network.id} className="flex items-center justify-between px-4 py-3 hover:bg-gray-50 transition-colors">
            <div className="min-w-0 flex-1">
              <Link
                href={`/careconnect/networks/${network.id}`}
                className="block font-medium text-gray-900 hover:text-blue-600 truncate"
              >
                {networkDisplayName(network, tenantName)}
              </Link>
              <p className="text-xs text-gray-400 mt-0.5">
                {network.providerCount} provider{network.providerCount === 1 ? '' : 's'}
              </p>
            </div>
            <div className="flex items-center gap-1.5 ml-4">
              <button
                onClick={() => router.push(`/careconnect/networks/${network.id}`)}
                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-gray-200 px-2.5 text-xs font-medium text-gray-700 hover:bg-gray-50 hover:border-gray-300 cursor-pointer"
                title="Manage network"
              >
                <i className="ri-settings-4-line text-sm" />
                Manage
              </button>
              {(canManageAll ||
                (!!callerOrgId && network.owningOrganizationId === callerOrgId)) && (
                <>
                  <button
                    onClick={() => openEdit(network)}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-gray-200 text-gray-500 hover:bg-gray-50 hover:border-gray-300 hover:text-gray-700 cursor-pointer"
                    title="Edit network"
                  >
                    <i className="ri-edit-line text-sm" />
                  </button>
                  <button
                    onClick={() => handleDelete(network.id)}
                    disabled={deletingId === network.id}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-gray-200 text-red-500 hover:bg-red-50 hover:border-red-300 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer"
                    title="Delete network"
                  >
                    <i className="ri-delete-bin-line text-sm" />
                  </button>
                </>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
