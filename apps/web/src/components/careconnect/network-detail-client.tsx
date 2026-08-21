'use client';

import { useState, useRef } from 'react';
import { careConnectApi } from '@/lib/careconnect-api';
import { AccessStageBadge } from '@/components/careconnect/status-badge';
import { formatPhoneDisplay, formatPhoneInput, isValidPhone, stripPhone } from '@/lib/phone';
import { isValidUsZipCode } from '@/lib/address';
import { networkDisplayName } from '@/lib/network-display-name';
import { Modal } from '@/components/ui/modal';
import { ProviderEditModal } from '@/components/careconnect/provider-edit-modal';
import type {
  NetworkDetail,
  NetworkProviderItem,
  ProviderSearchResult,
  SpecialtyOption,
} from '@/types/careconnect';

interface AddressSuggestion {
  displayName: string;
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
  latitude: number;
  longitude: number;
}

const GEOCODED_POINT_SOURCE = 'Geocoded';
const MAX_SERVICE_RADIUS_MILES = 60;
const DEFAULT_SERVICE_RADIUS_MILES = '25';

function networkProviderEntryId(provider: Pick<NetworkProviderItem, 'id' | 'networkProviderId'>): string {
  return provider.networkProviderId || provider.id;
}

function providerLocationKey(providerId: string, facilityId?: string | null): string {
  return `${providerId}:${facilityId ?? ''}`;
}

function searchResultKey(provider: ProviderSearchResult): string {
  return providerLocationKey(provider.id, provider.facilityId);
}

interface NetworkDetailClientProps {
  network:        NetworkDetail;
  specialtyOptions: SpecialtyOption[];
  /** LSV3-1084: tenant display name used for the "{tenantName} Preferred Providers" heading. */
  tenantName: string;
  /** True for NetworkManager / TenantAdmin / PlatformAdmin — can edit/remove any provider. */
  canManageAll: boolean;
  /** The caller's own organization id — a CareConnectReferrerAdmin without canManageAll may only edit/remove providers their org added. */
  callerOrgId?: string | null;
}

type AddMode = 'search' | 'create';

const PAGE_SIZE = 10;

const EMPTY_FORM = {
  title: '', firstName: '', lastName: '', organizationName: '', email: '', phone: '',
  addressLine1: '', city: '', state: '', postalCode: '',
  npi: '', isActive: true, acceptingReferrals: true,
  specialtyIds: [] as string[],
  isMobile: false, serviceRadiusMiles: DEFAULT_SERVICE_RADIUS_MILES,
};

export function NetworkDetailClient({
  network,
  specialtyOptions,
  tenantName,
  canManageAll,
  callerOrgId,
}: NetworkDetailClientProps) {
  const [providers, setProviders] = useState<NetworkProviderItem[]>(network.providers);
  const [page, setPage] = useState(1);
  const [showAddModal, setShowAddModal] = useState(false);
  const [editingProvider, setEditingProvider] = useState<NetworkProviderItem | null>(null);

  // Add provider state
  const [addMode, setAddMode] = useState<AddMode>('search');
  const [searchQuery, setSearchQuery] = useState({ name: '', phone: '', npi: '', city: '' });
  const [searchResults, setSearchResults] = useState<ProviderSearchResult[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);

  // New provider form
  const [newForm, setNewForm] = useState(EMPTY_FORM);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // Address autocomplete state
  const [addrSuggestions, setAddrSuggestions] = useState<AddressSuggestion[]>([]);
  const [addrLoading, setAddrLoading] = useState(false);
  const [addrOpen, setAddrOpen] = useState(false);
  const [geoLat, setGeoLat] = useState<number | null>(null);
  const [geoLng, setGeoLng] = useState<number | null>(null);
  const addrDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Remove
  const [removingId, setRemovingId] = useState<string | null>(null);

  // Adding association from search
  const [addingId, setAddingId] = useState<string | null>(null);

  const searchInputRef = useRef<HTMLInputElement>(null);

  const hasInvalidPhone = newForm.phone.trim().length > 0 && !isValidPhone(newForm.phone);
  const hasInvalidPostalCode =
    !newForm.isMobile && newForm.postalCode.trim().length > 0 && !isValidUsZipCode(newForm.postalCode);
  const hasInvalidServiceRadius =
    newForm.isMobile &&
    (!newForm.serviceRadiusMiles.trim() ||
      !(Number(newForm.serviceRadiusMiles) > 0) ||
      Number(newForm.serviceRadiusMiles) > MAX_SERVICE_RADIUS_MILES);
  const hasNoSpecialty = newForm.specialtyIds.length === 0;
  const selectedSpecialtyCodes = () => {
    const selected = new Set(newForm.specialtyIds);
    return specialtyOptions.filter(s => selected.has(s.id)).map(s => s.code);
  };

  // ── Address autocomplete ──────────────────────────────────────────────────

  function handleAddressChange(value: string) {
    setNewForm(f => ({ ...f, addressLine1: value }));
    setGeoLat(null);
    setGeoLng(null);
    if (addrDebounce.current) clearTimeout(addrDebounce.current);
    if (value.trim().length < 3) {
      setAddrSuggestions([]);
      setAddrOpen(false);
      return;
    }
    addrDebounce.current = setTimeout(async () => {
      setAddrLoading(true);
      try {
        const res = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}`, {
          credentials: 'include',
        });
        if (res.ok) {
          const suggestions: AddressSuggestion[] = await res.json();
          setAddrSuggestions(suggestions);
          setAddrOpen(suggestions.length > 0);
        }
      } catch {
        /* silently ignore */
      } finally {
        setAddrLoading(false);
      }
    }, 300);
  }

  function selectAddress(s: AddressSuggestion) {
    setNewForm(f => ({
      ...f,
      addressLine1: s.addressLine1,
      city: s.city,
      state: s.state,
      postalCode: s.postalCode,
    }));
    setGeoLat(s.latitude);
    setGeoLng(s.longitude);
    setAddrSuggestions([]);
    setAddrOpen(false);
  }

  // ── Search ──────────────────────────────────────────────────────────────────

  async function handleSearch(e: React.FormEvent) {
    e.preventDefault();
    const hasQuery = Object.values(searchQuery).some(v => v.trim() !== '');
    if (!hasQuery) {
      setSearchError('Enter at least one search field.');
      return;
    }
    setSearching(true);
    setSearchError(null);
    setSearchResults(null);
    try {
      const { data } = await careConnectApi.networks.searchProviders(network.id, {
        name:  searchQuery.name  || undefined,
        phone: stripPhone(searchQuery.phone) || undefined,
        npi:   searchQuery.npi   || undefined,
        city:  searchQuery.city  || undefined,
      });
      setSearchResults(data ?? []);
    } catch {
      setSearchError('Search failed. Please try again.');
    } finally {
      setSearching(false);
    }
  }

  // ── Associate existing ──────────────────────────────────────────────────────

  async function handleAssociate(provider: ProviderSearchResult) {
    const key = searchResultKey(provider);
    setAddingId(key);
    try {
      const { data } = await careConnectApi.networks.addProvider(network.id, {
        existingProviderId: provider.id,
        existingFacilityId: provider.facilityId ?? undefined,
      });
      if (data && !providers.find(p => networkProviderEntryId(p) === networkProviderEntryId(data))) {
        setProviders(prev => [...prev, data]);
      }
      setSearchResults(null);
      setSearchQuery({ name: '', phone: '', npi: '', city: '' });
    } catch {
      setSearchError('Failed to add provider to network. Please try again.');
    } finally {
      setAddingId(null);
    }
  }

  // ── Create new ──────────────────────────────────────────────────────────────

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (hasNoSpecialty) {
      setCreateError('Select at least one specialty.');
      return;
    }
    if (hasInvalidPhone || hasInvalidPostalCode || hasInvalidServiceRadius) return;
    const specialtyCodes = selectedSpecialtyCodes();
    if (specialtyCodes.length === 0) {
      setCreateError('Select at least one specialty.');
      return;
    }
    setCreating(true);
    setCreateError(null);
    try {
      const { data } = await careConnectApi.networks.addProvider(network.id, {
        newProvider: {
          title:               newForm.title.trim() || undefined,
          firstName:           newForm.firstName.trim(),
          lastName:            newForm.lastName.trim(),
          organizationName:    newForm.organizationName.trim() || undefined,
          email:               newForm.email.trim(),
          phone:               stripPhone(newForm.phone),
          addressLine1:        newForm.addressLine1.trim(),
          city:                newForm.city.trim(),
          state:               newForm.state.trim(),
          postalCode:          newForm.postalCode.trim(),
          isActive:            newForm.isActive,
          acceptingReferrals:  newForm.acceptingReferrals,
          npi:                 newForm.npi.trim() || undefined,
          specialtyCodes,
          primarySpecialtyCode: specialtyCodes[0],
          isMobile:            newForm.isMobile,
          serviceRadiusMiles:  newForm.isMobile ? Number(newForm.serviceRadiusMiles) : null,
          latitude:            geoLat ?? undefined,
          longitude:           geoLng ?? undefined,
          geoPointSource:      geoLat != null ? GEOCODED_POINT_SOURCE : undefined,
        },
      });
      if (data && !providers.find(p => p.id === data.id)) {
        setProviders(prev => [...prev, data]);
      }
      setNewForm(EMPTY_FORM);
      setGeoLat(null);
      setGeoLng(null);
      setAddMode('search');
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Failed to add provider. Please try again.';
      setCreateError(msg);
    } finally {
      setCreating(false);
    }
  }

  // ── Remove ──────────────────────────────────────────────────────────────────

  async function handleRemoveProvider(providerId: string) {
    if (!confirm('Remove this provider from the network? The provider stays in the shared registry.')) return;
    setRemovingId(providerId);
    try {
      await careConnectApi.networks.removeProvider(network.id, providerId);
      setProviders(prev => prev.filter(p => p.id !== providerId));
    } catch {
      alert('Failed to remove provider. Please try again.');
    } finally {
      setRemovingId(null);
    }
  }

  function closeAddModal() {
    setShowAddModal(false);
    setAddMode('search');
    setSearchQuery({ name: '', phone: '', npi: '', city: '' });
    setSearchResults(null);
    setSearchError(null);
    setNewForm(EMPTY_FORM);
    setCreateError(null);
    setGeoLat(null);
    setGeoLng(null);
    setAddrSuggestions([]);
    setAddrOpen(false);
  }

  const alreadyInNetwork = new Set(providers.map(p => providerLocationKey(p.providerId ?? p.id, p.facilityId)));
  const providerIdsInNetwork = new Set(providers.map(p => p.providerId ?? p.id));
  // LSV3-1084: a CareConnectReferrerAdmin may only add providers to a network their own
  // organization created; a tenant-admin-owned (or NetworkManager-owned) network is view-only.
  const canAddProviders =
    canManageAll || (!!callerOrgId && network.owningOrganizationId === callerOrgId);

  const totalPages = Math.max(1, Math.ceil(providers.length / PAGE_SIZE));
  const currentPage = Math.min(page, totalPages);
  const pagedProviders = providers.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE);

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">{networkDisplayName(network, tenantName)}</h1>
          <p className="mt-0.5 text-xs text-gray-400">
            {providers.length} provider{providers.length === 1 ? '' : 's'}
          </p>
        </div>
        {/* LSV3-1084: hidden for a CareConnectReferrerAdmin viewing a network their
            organization did not create (tenant-admin-owned networks are view-only). */}
        {canAddProviders && (
          <button
            onClick={() => setShowAddModal(true)}
            className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 transition-colors shrink-0 cursor-pointer"
          >
            <i className="ri-user-add-line" />
            Add Provider
          </button>
        )}
      </div>

      {/* Add Provider modal */}
      <Modal
        open={showAddModal}
        onClose={closeAddModal}
        title="Add Provider"
        size="2xl"
        titleSizeClassName="text-xl"
        cardClassName="rounded-[20px] shadow-xl border border-neutral-200"
        headerClassName="flex items-center justify-between px-8 pt-8 pb-6"
        bodyClassName="flex-1 overflow-y-auto px-8 pb-8"
        titleExtra={
          addMode === 'create' ? (
            <span className="inline-flex items-center h-7 px-3 py-1 rounded-2xl text-sm font-medium text-green-700 bg-green-500/5 border border-green-500">
              New Provider
            </span>
          ) : undefined
        }
        headerActions={
          <div className="flex rounded-[10px] overflow-hidden border border-neutral-200 text-sm">
            <button
              onClick={() => { setAddMode('search'); setCreateError(null); }}
              className={`px-3 py-1.5 cursor-pointer ${addMode === 'search' ? 'bg-blue-600 text-white' : 'bg-white text-gray-600 hover:bg-gray-50'}`}
            >
              Search Registry
            </button>
            <button
              onClick={() => { setAddMode('create'); setSearchResults(null); setSearchError(null); }}
              className={`px-3 py-1.5 border-l border-neutral-200 cursor-pointer ${addMode === 'create' ? 'bg-blue-600 text-white' : 'bg-white text-gray-600 hover:bg-gray-50'}`}
            >
              Add New
            </button>
          </div>
        }
      >
        {/* Shared registry notice */}
        <p className="text-sm text-blue-700 bg-blue-50 border border-blue-100 rounded-lg px-4 py-3 mb-6">
          <i className="ri-information-line mr-1" />
          Providers are shared across the platform. Adding to this network creates an association — you are not taking ownership.
        </p>

        {/* ── Search Mode ── */}
        {addMode === 'search' && (
          <>
            <form onSubmit={handleSearch} className="space-y-2">
              <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                <input
                  ref={searchInputRef}
                  type="text"
                  value={searchQuery.name}
                  onChange={e => setSearchQuery(q => ({ ...q, name: e.target.value }))}
                  placeholder="Name or org"
                  className="h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
                <input
                  type="text"
                  value={searchQuery.phone}
                  onChange={e => setSearchQuery(q => ({ ...q, phone: formatPhoneInput(e.target.value) }))}
                  placeholder="(555) 555-5555"
                  className="h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
                <input
                  type="text"
                  value={searchQuery.npi}
                  onChange={e => setSearchQuery(q => ({ ...q, npi: e.target.value }))}
                  placeholder="NPI number"
                  className="h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
                <input
                  type="text"
                  value={searchQuery.city}
                  onChange={e => setSearchQuery(q => ({ ...q, city: e.target.value }))}
                  placeholder="City"
                  className="h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
              </div>
              {searchError && <p className="text-xs text-red-600">{searchError}</p>}
              <button
                type="submit"
                disabled={searching}
                className="rounded-[10px] bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer"
              >
                {searching ? 'Searching…' : 'Search Shared Registry'}
              </button>
            </form>

            {/* Search Results */}
            {searchResults !== null && (
              <div className="mt-3">
                {searchResults.length === 0 ? (
                  <div className="rounded-md bg-yellow-50 border border-yellow-200 px-3 py-2 text-sm text-yellow-800">
                    No providers found. Switch to <button onClick={() => setAddMode('create')} className="underline font-medium cursor-pointer">Add New</button> to register a new provider.
                  </div>
                ) : (
                  <div className="divide-y divide-gray-100 rounded-md border border-gray-200 bg-white overflow-hidden max-h-72 overflow-y-auto">
                    {searchResults.map(p => {
                      const key = searchResultKey(p);
                      const inNetwork = p.facilityId
                        ? alreadyInNetwork.has(key)
                        : providerIdsInNetwork.has(p.id);
                      return (
                        <div key={key} className="flex items-center justify-between px-3 py-2 hover:bg-gray-50">
                          <div className="min-w-0 flex-1">
                            <div className="flex items-center gap-2">
                              <p className="text-sm font-medium text-gray-900 truncate">{p.name}</p>
                              <AccessStageBadge stage={p.accessStage} />
                            </div>
                            {p.organizationName && (
                              <p className="text-xs text-gray-500 truncate">{p.organizationName}</p>
                            )}
                            <p className="text-xs text-gray-400">
                              {p.city}, {p.state}
                              {p.npi && <span className="ml-2 font-mono">NPI: {p.npi}</span>}
                            </p>
                            {p.specialties?.length > 0 && (
                              <div className="mt-1 flex flex-wrap gap-1">
                                {p.specialties.map(s => (
                                  <span key={s.id} className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700 border border-blue-200">
                                    {s.name}
                                  </span>
                                ))}
                              </div>
                            )}
                          </div>
                          <div className="ml-3 flex-shrink-0">
                            {inNetwork ? (
                              <span className="text-xs text-green-600 font-medium">
                                <i className="ri-check-line mr-1" />In network
                              </span>
                            ) : (
                              <button
                                onClick={() => handleAssociate(p)}
                                disabled={addingId === key}
                                className="rounded-md bg-blue-600 px-3 py-1 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer"
                              >
                                {addingId === key ? 'Adding…' : 'Add to Network'}
                              </button>
                            )}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            )}
          </>
        )}

        {/* ── Create Mode ── */}
        {addMode === 'create' && (
          <form onSubmit={handleCreate} className="space-y-6">
            <p className="text-sm text-gray-500">
              Fill in the provider details. The NPI field is strongly recommended — it prevents duplicate records in the shared registry.
            </p>

            {/* ── Provider Setup ── */}
            <div className="space-y-3">
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">Title</label>
                  <input
                    value={newForm.title}
                    onChange={e => setNewForm(f => ({ ...f, title: e.target.value }))}
                    className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    placeholder="Dr."
                    maxLength={50}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">First name <span className="text-red-500">*</span></label>
                  <input
                    required
                    value={newForm.firstName}
                    onChange={e => setNewForm(f => ({ ...f, firstName: e.target.value }))}
                    className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    placeholder="Jane"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">Last name <span className="text-red-500">*</span></label>
                  <input
                    required
                    value={newForm.lastName}
                    onChange={e => setNewForm(f => ({ ...f, lastName: e.target.value }))}
                    className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    placeholder="Smith"
                  />
                </div>
              </div>
              <div className="grid grid-cols-1 gap-3 lg:grid-cols-3">
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">Organization / Practice</label>
                  <input
                    value={newForm.organizationName}
                    onChange={e => setNewForm(f => ({ ...f, organizationName: e.target.value }))}
                    className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    placeholder="Smith Family Practice"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">Email <span className="text-red-500">*</span></label>
                  <input
                    required
                    type="email"
                    value={newForm.email}
                    onChange={e => setNewForm(f => ({ ...f, email: e.target.value }))}
                    className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    placeholder="jane@example.com"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">Phone <span className="text-red-500">*</span></label>
                  <input
                    required
                    type="tel"
                    value={newForm.phone}
                    onChange={e => setNewForm(f => ({ ...f, phone: formatPhoneInput(e.target.value) }))}
                    className={`w-full h-9 rounded-lg border px-3 py-1 text-sm focus:outline-none ${
                      hasInvalidPhone
                        ? 'border-red-300 focus:border-red-400'
                        : 'border-neutral-200 focus:border-blue-500'
                    }`}
                    placeholder="(555) 555-5555"
                  />
                  {hasInvalidPhone && (
                    <p className="text-xs text-red-500 mt-1">Phone number must be 10 digits.</p>
                  )}
                </div>
                <div>
                  <label className="block text-sm font-medium text-neutral-950 mb-3">NPI Number</label>
                  <input
                    value={newForm.npi}
                    onChange={e => setNewForm(f => ({ ...f, npi: e.target.value }))}
                    className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none font-mono"
                    placeholder="1234567890"
                    maxLength={10}
                  />
                </div>
                <div className="lg:col-span-2">
                  <label className="block text-sm font-medium text-neutral-950 mb-3">Specialty <span className="text-red-500">*</span></label>
                  <div className={`grid grid-cols-1 gap-x-20 gap-y-3 sm:grid-cols-2 rounded-lg border p-4 ${
                    hasNoSpecialty && createError === 'Select at least one specialty.'
                      ? 'border-red-300 bg-red-50'
                      : 'border-neutral-200 bg-white'
                  }`}>
                    {specialtyOptions.length === 0 ? (
                      <p className="col-span-full text-sm text-gray-400">No active specialties are configured.</p>
                    ) : specialtyOptions.map(s => {
                      const checked = newForm.specialtyIds.includes(s.id);
                      return (
                        <label
                          key={s.id}
                          className="flex items-center gap-2 cursor-pointer rounded px-1 py-1 text-sm text-gray-700 hover:bg-gray-50"
                        >
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={e => {
                              setNewForm(f => ({
                                ...f,
                                specialtyIds: e.target.checked
                                  ? [...f.specialtyIds, s.id]
                                  : f.specialtyIds.filter(id => id !== s.id),
                              }));
                              if (createError === 'Select at least one specialty.') {
                                setCreateError(null);
                              }
                            }}
                            className="size-5 rounded-md border-[1.5px] border-neutral-200 text-blue-600 focus:ring-blue-500"
                          />
                          {s.name}
                        </label>
                      );
                    })}
                  </div>
                  {hasNoSpecialty && createError === 'Select at least one specialty.' && (
                    <p className="text-xs text-red-500 mt-1">Select at least one specialty.</p>
                  )}
                </div>
              </div>
            </div>

            {/* ── Facility ── */}
            <div className="border-t border-gray-100 pt-6">
              <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500 mb-3">Facility</h3>
              <div className="rounded-lg border border-slate-200 bg-white p-6 space-y-5">
                <div className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    id="newProviderIsMobile"
                    checked={newForm.isMobile}
                    onChange={e =>
                      setNewForm(f => ({
                        ...f,
                        isMobile: e.target.checked,
                        postalCode: e.target.checked ? '' : f.postalCode,
                      }))
                    }
                    className="size-5 rounded-md border-[1.5px] border-neutral-200"
                  />
                  <label htmlFor="newProviderIsMobile" className="text-sm font-medium text-neutral-950">
                    Mobile / roaming provider (no fixed address)
                  </label>
                </div>
                <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
                  <div className="relative lg:col-span-2">
                    <label className="block text-sm font-medium text-neutral-950 mb-3">
                      {newForm.isMobile ? 'Service area description' : 'Address'} <span className="text-red-500">*</span>
                    </label>
                    <div className="relative">
                      <input
                        required
                        autoComplete="off"
                        value={newForm.addressLine1}
                        onChange={e =>
                          newForm.isMobile
                            ? setNewForm(f => ({ ...f, addressLine1: e.target.value }))
                            : handleAddressChange(e.target.value)
                        }
                        onFocus={() => !newForm.isMobile && addrSuggestions.length > 0 && setAddrOpen(true)}
                        onBlur={() => setTimeout(() => setAddrOpen(false), 150)}
                        className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none pr-7"
                        placeholder={newForm.isMobile ? 'e.g. Greater Las Vegas Metro' : '123 Main St'}
                      />
                      {addrLoading && !newForm.isMobile && (
                        <span className="absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 rounded-full border-2 border-blue-400 border-t-transparent animate-spin" />
                      )}
                    </div>
                    {!newForm.isMobile && addrOpen && addrSuggestions.length > 0 && (
                      <ul className="absolute z-50 mt-1 w-full rounded-md border border-gray-200 bg-white shadow-lg text-sm overflow-hidden">
                        {addrSuggestions.map((s, i) => (
                          <li
                            key={i}
                            onMouseDown={() => selectAddress(s)}
                            className="flex items-start gap-2 px-3 py-2 cursor-pointer hover:bg-blue-50 transition-colors border-b border-gray-100 last:border-0"
                          >
                            <i className="ri-map-pin-line text-gray-400 mt-0.5 shrink-0" />
                            <span className="text-gray-700">{s.displayName}</span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-neutral-950 mb-3">City <span className="text-red-500">*</span></label>
                    <input
                      required
                      value={newForm.city}
                      onChange={e => setNewForm(f => ({ ...f, city: e.target.value }))}
                      className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    />
                  </div>
                  <div className="flex gap-2">
                    <div className="flex-1">
                      <label className="block text-sm font-medium text-neutral-950 mb-3">State <span className="text-red-500">*</span></label>
                      <input
                        required
                        value={newForm.state}
                        onChange={e => setNewForm(f => ({ ...f, state: e.target.value }))}
                        className="w-full h-9 rounded-lg border border-neutral-200 px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                        placeholder="CA"
                        maxLength={2}
                      />
                    </div>
                    <div className="flex-1">
                      {newForm.isMobile ? (
                        <>
                          <label className="block text-sm font-medium text-neutral-950 mb-3">Service radius (mi) <span className="text-red-500">*</span></label>
                          <input
                            required
                            type="number"
                            min={1}
                            max={MAX_SERVICE_RADIUS_MILES}
                            value={newForm.serviceRadiusMiles}
                            onChange={e => setNewForm(f => ({ ...f, serviceRadiusMiles: e.target.value }))}
                            className={`w-full h-9 rounded-lg border px-3 py-1 text-sm focus:outline-none ${
                              hasInvalidServiceRadius
                                ? 'border-red-300 focus:border-red-400'
                                : 'border-neutral-200 focus:border-blue-500'
                            }`}
                          />
                          {hasInvalidServiceRadius && (
                            <p className="text-xs text-red-500 mt-1">
                              Enter a radius between 1 and {MAX_SERVICE_RADIUS_MILES} miles.
                            </p>
                          )}
                        </>
                      ) : (
                        <>
                          <label className="block text-sm font-medium text-neutral-950 mb-3">Postal Code <span className="text-red-500">*</span></label>
                          <input
                            required
                            value={newForm.postalCode}
                            onChange={e => setNewForm(f => ({ ...f, postalCode: e.target.value }))}
                            className={`w-full h-9 rounded-lg border px-3 py-1 text-sm focus:outline-none ${
                              hasInvalidPostalCode
                                ? 'border-red-300 focus:border-red-400'
                                : 'border-neutral-200 focus:border-blue-500'
                            }`}
                            placeholder="90210"
                          />
                          {hasInvalidPostalCode && (
                            <p className="text-xs text-red-500 mt-1">ZIP code must be 5 digits or 5+4 format.</p>
                          )}
                        </>
                      )}
                    </div>
                  </div>
                </div>

                <div className="flex gap-5 border-t border-gray-100 pt-4">
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={newForm.isActive}
                      onChange={e => setNewForm(f => ({ ...f, isActive: e.target.checked }))}
                      className="size-5 rounded-md border-[1.5px] border-neutral-200"
                    />
                    <span className="text-sm text-gray-700">Active</span>
                  </label>
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={newForm.acceptingReferrals}
                      onChange={e => setNewForm(f => ({ ...f, acceptingReferrals: e.target.checked }))}
                      className="size-5 rounded-md border-[1.5px] border-neutral-200"
                    />
                    <span className="text-sm text-gray-700">Accepting referrals</span>
                  </label>
                </div>
              </div>
            </div>

            {createError && <p className="text-sm text-red-600">{createError}</p>}
            <div className="flex gap-2">
              <button
                type="submit"
                disabled={creating || hasInvalidPhone || hasInvalidPostalCode || hasInvalidServiceRadius}
                className="inline-flex items-center gap-2 rounded-[10px] bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors"
              >
                <i className="ri-save-line" />
                {creating ? 'Adding…' : 'Register & Add to Network'}
              </button>
              <button
                type="button"
                onClick={() => { setAddMode('search'); setNewForm(EMPTY_FORM); setCreateError(null); }}
                className="rounded-[10px] border border-neutral-200 bg-white px-4 py-2 text-sm font-medium text-neutral-950 hover:bg-gray-50 cursor-pointer"
              >
                Cancel
              </button>
            </div>
          </form>
        )}
      </Modal>

      <ProviderEditModal
        network={network}
        provider={editingProvider}
        providers={providers}
        specialtyOptions={specialtyOptions}
        onClose={() => setEditingProvider(null)}
        onProvidersChange={setProviders}
      />

      {/* Providers */}
      {providers.length === 0 ? (
        <div className="rounded-lg border-2 border-dashed border-gray-200 py-12 text-center">
          <i className="ri-hospital-line text-3xl text-gray-300" />
          <p className="mt-2 text-sm text-gray-500">No providers in this network yet.</p>
          <p className="text-xs text-gray-400 mt-1">Search the registry above to add providers.</p>
        </div>
      ) : (
        <div className="rounded-xl border border-gray-200 bg-white overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[600px] table-fixed text-sm">
              <thead className="bg-gray-50 border-b border-gray-200">
                <tr>
                  <th className="w-[22%] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Provider
                  </th>
                  <th className="w-[22%] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Specialties
                  </th>
                  <th className="w-[22%] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Contact
                  </th>
                  <th className="w-[17%] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Location
                  </th>
                  <th className="w-[15%] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Referrals
                  </th>
                  <th className="w-[2%]" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {pagedProviders.map(provider => (
                  <tr key={provider.id} className="hover:bg-gray-50 transition-colors">
                    <td className="px-4 py-3">
                      <p className="font-medium text-gray-900 leading-tight">
                        {provider.organizationName || provider.name}
                      </p>
                      {provider.organizationName && (
                        <p className="text-xs text-gray-500 mt-0.5 leading-tight">{provider.name}</p>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      {provider.specialties?.length ? (
                        <div className="flex flex-wrap gap-1">
                          {provider.specialties.map(s => (
                            <span
                              key={s.id}
                              className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700 border border-blue-200"
                            >
                              {s.name}
                            </span>
                          ))}
                        </div>
                      ) : (
                        <span className="text-xs text-gray-400">—</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-gray-600 text-xs space-y-0.5">
                      <p>{provider.email}</p>
                      <p>{formatPhoneDisplay(provider.phone)}</p>
                    </td>
                    <td className="px-4 py-3 text-xs text-gray-600 whitespace-nowrap">
                      {provider.city}, {provider.state}
                    </td>
                    <td className="px-4 py-3">
                      <span
                        className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
                          provider.acceptingReferrals
                            ? 'bg-green-50 text-green-700 border-green-200'
                            : 'bg-amber-50 text-amber-700 border-amber-200'
                        }`}
                      >
                        {provider.acceptingReferrals ? 'Accepting' : 'Not accepting'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-right">
                      {(canManageAll ||
                        (!!callerOrgId && provider.owningOrganizationId === callerOrgId)) && (
                        <div className="flex items-center justify-end gap-1">
                          <button
                            onClick={() => setEditingProvider(provider)}
                            className="p-1.5 rounded-md text-gray-400 hover:text-blue-600 hover:bg-blue-50 cursor-pointer transition-colors"
                            title="Edit provider"
                          >
                            <i className="ri-pencil-line text-base" />
                          </button>
                          <button
                            onClick={() => handleRemoveProvider(provider.id)}
                            disabled={removingId === provider.id}
                            className="p-1.5 rounded-md text-gray-400 hover:text-red-600 hover:bg-red-50 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer transition-colors"
                            title="Remove from network (association only)"
                          >
                            <i
                              className={`text-base ${removingId === provider.id ? 'ri-loader-4-line animate-spin' : 'ri-close-circle-line'}`}
                            />
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          {providers.length > PAGE_SIZE && (
            <div className="px-4 py-3 border-t border-gray-100 flex items-center justify-between">
              <p className="text-xs text-gray-400">
                Showing {(currentPage - 1) * PAGE_SIZE + 1}–{Math.min(currentPage * PAGE_SIZE, providers.length)} of {providers.length}
              </p>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => setPage(p => Math.max(1, p - 1))}
                  disabled={currentPage <= 1}
                  className="text-xs text-blue-600 hover:underline disabled:text-gray-300 disabled:no-underline disabled:cursor-not-allowed cursor-pointer"
                >
                  ← Previous
                </button>
                <span className="text-xs text-gray-400">
                  Page {currentPage} of {totalPages}
                </span>
                <button
                  onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                  disabled={currentPage >= totalPages}
                  className="text-xs text-blue-600 hover:underline disabled:text-gray-300 disabled:no-underline disabled:cursor-not-allowed cursor-pointer"
                >
                  Next →
                </button>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
