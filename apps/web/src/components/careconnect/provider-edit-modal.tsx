"use client";

import { useEffect, useState } from "react";

import { careConnectApi } from "@/lib/careconnect-api";
import { formatPhoneInput, isValidPhone, stripPhone } from "@/lib/phone";
import { isValidUsZipCode } from "@/lib/address";
import type {
  NetworkDetail,
  NetworkProviderItem,
  SpecialtyOption,
  UpdateNetworkProviderRequest,
} from "@/types/careconnect";
import { Modal, ConfirmDialog } from "@/components/ui/modal";

const MAX_SERVICE_RADIUS_MILES = 60;
const DEFAULT_SERVICE_RADIUS_MILES = "25";

interface SetupForm {
  title: string;
  firstName: string;
  lastName: string;
  organizationName: string;
  email: string;
  phone: string;
  specialtyIds: string[];
  visibility: string;
}

interface EditLocationForm {
  entryId: string;
  providerId: string;
  facilityName: string;
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
  isActive: boolean;
  acceptingReferrals: boolean;
  facilityIsActive: boolean;
  isMobile: boolean;
  serviceRadiusMiles: string;
}

interface NewLocationForm {
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
  isActive: boolean;
  acceptingReferrals: boolean;
  isMobile: boolean;
  serviceRadiusMiles: string;
}

const EMPTY_NEW_LOCATION: NewLocationForm = {
  addressLine1: "",
  city: "",
  state: "",
  postalCode: "",
  isActive: true,
  acceptingReferrals: true,
  isMobile: false,
  serviceRadiusMiles: DEFAULT_SERVICE_RADIUS_MILES,
};

function networkProviderEntryId(
  provider: Pick<NetworkProviderItem, "id" | "networkProviderId">,
): string {
  return provider.networkProviderId || provider.id;
}

function providerIdentityId(provider: NetworkProviderItem): string {
  return provider.providerId || provider.id;
}

function formatLocationLine(location: {
  addressLine1?: string | null;
  city?: string | null;
  state?: string | null;
  postalCode?: string | null;
  isMobile?: boolean;
  serviceRadiusMiles?: number | null;
}): string {
  const cityState = [location.city, location.state].filter(Boolean).join(", ");
  if (location.isMobile) {
    const parts = [location.addressLine1, cityState].filter(Boolean).join(" · ");
    return location.serviceRadiusMiles != null
      ? `Mobile · ${parts} · ${location.serviceRadiusMiles}mi radius`
      : `Mobile · ${parts}`;
  }
  return [location.addressLine1, cityState, location.postalCode]
    .filter(Boolean)
    .join(" ");
}

function toEditLocationForm(provider: NetworkProviderItem): EditLocationForm {
  return {
    entryId: networkProviderEntryId(provider),
    providerId: providerIdentityId(provider),
    facilityName: provider.facilityName || provider.organizationName || provider.name,
    addressLine1: provider.addressLine1 ?? "",
    city: provider.city ?? "",
    state: provider.state ?? "",
    postalCode: provider.postalCode ?? "",
    isActive: provider.isActive,
    acceptingReferrals: provider.acceptingReferrals,
    facilityIsActive: provider.facilityIsActive,
    isMobile: provider.isMobile,
    serviceRadiusMiles:
      provider.serviceRadiusMiles != null
        ? String(provider.serviceRadiusMiles)
        : DEFAULT_SERVICE_RADIUS_MILES,
  };
}

function splitProviderName(name: string): {
  title: string;
  firstName: string;
  lastName: string;
} {
  const KNOWN_PROVIDER_TITLES: Record<string, string> = {
    dr: "Dr.", "dr.": "Dr.", mr: "Mr.", "mr.": "Mr.",
    mrs: "Mrs.", "mrs.": "Mrs.", ms: "Ms.", "ms.": "Ms.",
    prof: "Prof.", "prof.": "Prof.",
  };
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const title = parts.length > 0 ? (KNOWN_PROVIDER_TITLES[parts[0].toLowerCase()] ?? "") : "";
  if (title) parts.shift();
  if (parts.length <= 1) return { title, firstName: parts[0] ?? "", lastName: "" };
  return { title, firstName: parts.slice(0, -1).join(" "), lastName: parts[parts.length - 1] ?? "" };
}

interface ProviderEditModalProps {
  network: NetworkDetail;
  provider: NetworkProviderItem | null;
  providers: NetworkProviderItem[];
  specialtyOptions: SpecialtyOption[];
  /** 4-AC3: True only for TenantAdmin / PlatformAdmin — can toggle Public/Private visibility. */
  canManageVisibility: boolean;
  onClose: () => void;
  onProvidersChange: (
    updater: (prev: NetworkProviderItem[]) => NetworkProviderItem[],
  ) => void;
  onToast?: (msg: string) => void;
}

export function ProviderEditModal({
  network,
  provider,
  providers,
  specialtyOptions,
  canManageVisibility,
  onClose,
  onProvidersChange,
  onToast,
}: ProviderEditModalProps) {
  const open = provider !== null;
  const identityId = provider ? providerIdentityId(provider) : null;

  const [setupForm, setSetupForm] = useState<SetupForm>({
    title: "", firstName: "", lastName: "", organizationName: "",
    email: "", phone: "", specialtyIds: [], visibility: "Private",
  });
  const [editLocations, setEditLocations] = useState<EditLocationForm[]>([]);
  const [savingEdit, setSavingEdit] = useState(false);
  const [savingLocationId, setSavingLocationId] = useState<string | null>(null);
  const [deletingLocationId, setDeletingLocationId] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<{ entryId: string; displayName: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [addingLocation, setAddingLocation] = useState(false);
  const [newLocation, setNewLocation] = useState<NewLocationForm>(EMPTY_NEW_LOCATION);
  const [savingNewLocation, setSavingNewLocation] = useState(false);

  // Reset the form whenever a different provider identity is opened for editing —
  // deliberately NOT re-run on every `providers` change, since successful saves
  // update `editLocations` locally instead of resetting mid-edit.
  useEffect(() => {
    if (!provider) return;
    const { title, firstName, lastName } = splitProviderName(provider.name);
    setSetupForm({
      title: provider.title?.trim() || title,
      firstName,
      lastName,
      organizationName: provider.organizationName ?? "",
      email: provider.email ?? "",
      phone: formatPhoneInput(provider.phone ?? ""),
      specialtyIds: provider.specialties?.map((s) => s.id) ?? [],
      visibility: provider.visibility ?? "Private",
    });
    const pid = providerIdentityId(provider);
    setEditLocations(
      providers.filter((p) => providerIdentityId(p) === pid).map(toEditLocationForm),
    );
    setError(null);
    setAddingLocation(false);
    setNewLocation(EMPTY_NEW_LOCATION);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identityId]);

  const activeEditLocations = editLocations.filter((l) => l.facilityIsActive);
  const hasInvalidPhone = setupForm.phone.trim().length > 0 && !isValidPhone(setupForm.phone);
  const hasNoSpecialty = setupForm.specialtyIds.length === 0;
  const hasInvalidNewPostalCode =
    !newLocation.isMobile &&
    newLocation.postalCode.trim().length > 0 &&
    !isValidUsZipCode(newLocation.postalCode);
  const hasInvalidNewServiceRadius =
    newLocation.isMobile &&
    (!newLocation.serviceRadiusMiles.trim() ||
      !(Number(newLocation.serviceRadiusMiles) > 0) ||
      Number(newLocation.serviceRadiusMiles) > MAX_SERVICE_RADIUS_MILES);

  if (!provider) return null;

  function locationHasInvalidPostalCode(location: Pick<EditLocationForm, "postalCode" | "isMobile">): boolean {
    return !location.isMobile && location.postalCode.trim().length > 0 && !isValidUsZipCode(location.postalCode);
  }

  function locationHasInvalidServiceRadius(location: Pick<EditLocationForm, "isMobile" | "serviceRadiusMiles">): boolean {
    return (
      location.isMobile &&
      (!location.serviceRadiusMiles.trim() ||
        !(Number(location.serviceRadiusMiles) > 0) ||
        Number(location.serviceRadiusMiles) > MAX_SERVICE_RADIUS_MILES)
    );
  }

  function toggleSpecialty(id: string) {
    setSetupForm((f) => ({
      ...f,
      specialtyIds: f.specialtyIds.includes(id)
        ? f.specialtyIds.filter((existing) => existing !== id)
        : [...f.specialtyIds, id],
    }));
    if (error === "Select at least one specialty.") setError(null);
  }

  function updateEditLocation(entryId: string, patch: Partial<EditLocationForm>) {
    setEditLocations((prev) =>
      prev.map((location) => (location.entryId === entryId ? { ...location, ...patch } : location)),
    );
  }

  function validateSetupFields(): boolean {
    if (!setupForm.firstName.trim() || !setupForm.lastName.trim()) {
      setError("First name and last name are required.");
      return false;
    }
    if (!setupForm.email.trim()) {
      setError("Email is required.");
      return false;
    }
    if (!setupForm.phone.trim()) {
      setError("Phone is required.");
      return false;
    }
    if (hasNoSpecialty) {
      setError("Select at least one specialty.");
      return false;
    }
    if (hasInvalidPhone) return false;
    return true;
  }

  function buildProviderUpdatePayload(location: EditLocationForm): UpdateNetworkProviderRequest {
    return {
      title: setupForm.title.trim() || null,
      firstName: setupForm.firstName.trim(),
      lastName: setupForm.lastName.trim(),
      organizationName: setupForm.organizationName.trim() || null,
      email: setupForm.email.trim(),
      phone: stripPhone(setupForm.phone),
      addressLine1: location.addressLine1.trim(),
      city: location.city.trim(),
      state: location.state.trim().toUpperCase(),
      postalCode: location.postalCode.trim() || null,
      isActive: location.isActive,
      acceptingReferrals: location.acceptingReferrals,
      specialtyIds: setupForm.specialtyIds,
      isMobile: location.isMobile,
      serviceRadiusMiles: location.isMobile ? Number(location.serviceRadiusMiles) : null,
      // 4-AC3: only send Visibility when the caller is permitted to change it — the
      // backend also enforces this (403 for non-tenant-admins), this is defense in depth.
      visibility: canManageVisibility ? setupForm.visibility : undefined,
    };
  }

  function applyProviderUpdate(updated: NetworkProviderItem) {
    const entryId = networkProviderEntryId(updated);
    onProvidersChange((prev) =>
      prev.map((p) => {
        if (providerIdentityId(p) !== providerIdentityId(updated)) return p;
        if (networkProviderEntryId(p) === entryId) return updated;
        return {
          ...p,
          name: updated.name,
          title: updated.title,
          organizationName: updated.organizationName,
          email: updated.email,
          phone: updated.phone,
          accessStage: updated.accessStage,
          specialties: updated.specialties,
          primarySpecialtyId: updated.primarySpecialtyId,
          primarySpecialty: updated.primarySpecialty,
          visibility: updated.visibility,
        };
      }),
    );
  }

  async function handleUpdateProviderSetup() {
    if (!validateSetupFields()) return;
    if (editLocations.length === 0) {
      setError("At least one location is required before saving provider setup.");
      return;
    }
    setSavingEdit(true);
    setSavingLocationId(null);
    setError(null);
    try {
      const updatedItems: NetworkProviderItem[] = [];
      for (const location of editLocations) {
        const { data } = await careConnectApi.networks.updateProvider(
          network.id, location.entryId, buildProviderUpdatePayload(location),
        );
        updatedItems.push(data);
        applyProviderUpdate(data);
      }
      setEditLocations((prev) =>
        prev.map((item) => {
          const updated = updatedItems.find((p) => networkProviderEntryId(p) === item.entryId);
          return updated ? toEditLocationForm(updated) : item;
        }),
      );
      onToast?.(
        `${[setupForm.title, setupForm.firstName, setupForm.lastName].map((v) => v.trim()).filter(Boolean).join(" ")} provider setup updated.`,
      );
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Failed to update provider setup. Please try again.");
    } finally {
      setSavingEdit(false);
    }
  }

  async function handleUpdateLocation(e: React.FormEvent, entryId: string) {
    e.preventDefault();
    const location = editLocations.find((item) => item.entryId === entryId);
    if (!location) return;
    if (!validateSetupFields()) return;
    if (hasInvalidPhone || locationHasInvalidPostalCode(location) || locationHasInvalidServiceRadius(location)) return;

    setSavingEdit(true);
    setSavingLocationId(entryId);
    setError(null);
    try {
      const { data } = await careConnectApi.networks.updateProvider(
        network.id, entryId, buildProviderUpdatePayload(location),
      );
      applyProviderUpdate(data);
      setEditLocations((prev) => prev.map((item) => (item.entryId === entryId ? toEditLocationForm(data) : item)));
      onToast?.(`${data.facilityName || data.name} updated.`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Failed to update provider location. Please try again.");
    } finally {
      setSavingEdit(false);
      setSavingLocationId(null);
    }
  }

  function requestDeleteLocation(entryId: string, displayName: string) {
    if (activeEditLocations.length <= 1) {
      setError("A provider must have at least one location. Add another location before deleting this one.");
      return;
    }
    setDeleteTarget({ entryId, displayName });
  }

  async function confirmDeleteLocation() {
    if (!deleteTarget) return;
    const { entryId, displayName } = deleteTarget;
    setDeletingLocationId(entryId);
    setError(null);
    try {
      await careConnectApi.networks.removeProvider(network.id, entryId, true);
      onProvidersChange((prev) =>
        prev.map((p) =>
          networkProviderEntryId(p) === entryId
            ? { ...p, isActive: false, acceptingReferrals: false, facilityIsActive: false }
            : p,
        ),
      );
      setEditLocations((prev) =>
        prev.map((item) =>
          item.entryId === entryId
            ? { ...item, isActive: false, acceptingReferrals: false, facilityIsActive: false }
            : item,
        ),
      );
      onToast?.(`${displayName || "Location"} deleted.`);
      setDeleteTarget(null);
    } catch {
      setError("Failed to delete provider location. Please try again.");
    } finally {
      setDeletingLocationId(null);
    }
  }

  async function handleSaveNewLocation(e: React.FormEvent) {
    e.preventDefault();
    if (!identityId) return;
    // Specialty stays on the existing provider identity — a new location doesn't
    // re-collect it, unlike Save Provider Setup.
    if (hasInvalidPhone || hasInvalidNewPostalCode || hasInvalidNewServiceRadius) return;
    setSavingNewLocation(true);
    setError(null);
    try {
      const { data } = await careConnectApi.networks.addProvider(network.id, {
        existingProviderId: identityId,
        newProvider: {
          title: setupForm.title.trim() || undefined,
          firstName: setupForm.firstName.trim(),
          lastName: setupForm.lastName.trim(),
          organizationName: setupForm.organizationName.trim() || undefined,
          email: setupForm.email.trim(),
          phone: stripPhone(setupForm.phone),
          addressLine1: newLocation.addressLine1.trim(),
          city: newLocation.city.trim(),
          state: newLocation.state.trim().toUpperCase(),
          postalCode: newLocation.postalCode.trim() || null,
          isActive: newLocation.isActive,
          acceptingReferrals: newLocation.acceptingReferrals,
          isMobile: newLocation.isMobile,
          serviceRadiusMiles: newLocation.isMobile ? Number(newLocation.serviceRadiusMiles) : null,
        },
      });
      onProvidersChange((prev) =>
        prev.find((p) => networkProviderEntryId(p) === networkProviderEntryId(data))
          ? prev
          : [...prev, data],
      );
      setEditLocations((prev) => [...prev, toEditLocationForm(data)]);
      onToast?.(`${data.facilityName || data.name} location added.`);
      setAddingLocation(false);
      setNewLocation(EMPTY_NEW_LOCATION);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Failed to add provider location. Please try again.");
    } finally {
      setSavingNewLocation(false);
    }
  }

  return (
    <>
      <ConfirmDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        onConfirm={confirmDeleteLocation}
        title="Delete location?"
        description={`Delete ${deleteTarget?.displayName || "this location"}? This will mark it inactive and stop accepting referrals.`}
        confirmLabel="Delete location"
        confirmVariant="danger"
        loading={!!deletingLocationId}
      />
      <Modal
        size="2xl"
        open={open}
        onClose={onClose}
        title="Edit Provider"
        titleSizeClassName="text-xl"
        cardClassName="rounded-[20px] shadow-xl border border-neutral-200"
        headerClassName="flex items-center justify-between px-8 pt-8 pb-6"
        bodyClassName="flex-1 overflow-y-auto px-8 pb-8"
        titleExtra={
          <span className="inline-flex items-center h-7 px-3 py-1 rounded-2xl text-sm font-medium text-green-700 bg-green-500/5 border border-green-500">
            Provider Setup
          </span>
        }
      >
        <div className="space-y-6">
          <p className="text-sm text-violet-800 bg-violet-50 border border-violet-200 rounded-lg px-4 py-3">
            <i className="ri-information-line mr-1" />
            Update provider setup details and manage each facility/location in this
            network. Specialty is required before saving changes.
          </p>

          <div className="flex items-center justify-between gap-3 rounded-lg border border-neutral-200 bg-white px-4 py-3">
            <div>
              <p className="text-sm font-medium text-neutral-950">Network visibility</p>
              <p className="text-xs text-gray-500 mt-0.5">
                {setupForm.visibility === "Public"
                  ? "Public — visible in the shared tenant network."
                  : "Private — visible only within this Law Firm's network."}
                {!canManageVisibility && " Only a tenant administrator can change this."}
              </p>
            </div>
            {canManageVisibility ? (
              <div className="inline-flex rounded-lg border border-gray-200 bg-white overflow-hidden shrink-0">
                <button
                  type="button"
                  onClick={() => setSetupForm((f) => ({ ...f, visibility: "Private" }))}
                  className={`px-3 py-1.5 text-xs font-medium transition-colors cursor-pointer ${
                    setupForm.visibility !== "Public"
                      ? "bg-neutral-900 text-white"
                      : "bg-white text-gray-600 hover:bg-gray-50"
                  }`}
                >
                  Private
                </button>
                <button
                  type="button"
                  onClick={() => setSetupForm((f) => ({ ...f, visibility: "Public" }))}
                  className={`px-3 py-1.5 text-xs font-medium transition-colors cursor-pointer ${
                    setupForm.visibility === "Public"
                      ? "bg-blue-600 text-white"
                      : "bg-white text-gray-600 hover:bg-gray-50"
                  }`}
                >
                  Public
                </button>
              </div>
            ) : (
              <span
                className={`inline-flex items-center rounded-full px-2.5 py-1 text-[11px] font-medium border shrink-0 ${
                  setupForm.visibility === "Public"
                    ? "bg-blue-50 text-blue-700 border-blue-200"
                    : "bg-gray-50 text-gray-600 border-gray-200"
                }`}
              >
                {setupForm.visibility === "Public" ? "Public" : "Private"}
              </span>
            )}
          </div>

          <div className="space-y-3">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
              <div>
                <label className="block text-sm font-medium text-neutral-950 mb-3">Title</label>
                <input
                  value={setupForm.title}
                  onChange={(e) => setSetupForm((f) => ({ ...f, title: e.target.value }))}
                  placeholder="Dr."
                  maxLength={50}
                  className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-neutral-950 mb-3">First name <span className="text-red-500">*</span></label>
                <input
                  required
                  value={setupForm.firstName}
                  onChange={(e) => setSetupForm((f) => ({ ...f, firstName: e.target.value }))}
                  placeholder="Jane"
                  className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-neutral-950 mb-3">Last name <span className="text-red-500">*</span></label>
                <input
                  required
                  value={setupForm.lastName}
                  onChange={(e) => setSetupForm((f) => ({ ...f, lastName: e.target.value }))}
                  placeholder="Smith"
                  className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
              </div>
            </div>

            <div className="grid grid-cols-1 gap-3 lg:grid-cols-3">
              <div>
                <label className="block text-sm font-medium text-neutral-950 mb-3">Organization / Practice</label>
                <input
                  value={setupForm.organizationName}
                  onChange={(e) => setSetupForm((f) => ({ ...f, organizationName: e.target.value }))}
                  placeholder="Smith Family Practice"
                  className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-neutral-950 mb-3">Email <span className="text-red-500">*</span></label>
                <input
                  required
                  type="email"
                  value={setupForm.email}
                  onChange={(e) => setSetupForm((f) => ({ ...f, email: e.target.value }))}
                  placeholder="jane@example.com"
                  className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-neutral-950 mb-3">Phone <span className="text-red-500">*</span></label>
                <input
                  required
                  type="tel"
                  value={setupForm.phone}
                  onChange={(e) => setSetupForm((f) => ({ ...f, phone: formatPhoneInput(e.target.value) }))}
                  placeholder="(555) 555-5555"
                  className={`w-full h-9 rounded-lg border bg-white px-3 py-1 text-sm focus:outline-none ${
                    hasInvalidPhone ? "border-red-300 focus:border-red-400" : "border-neutral-200 focus:border-blue-500"
                  }`}
                />
                {hasInvalidPhone && <p className="text-xs text-red-500 mt-1">Phone number must be 10 digits.</p>}
              </div>
              <div className="lg:col-span-3">
                <label className="block text-sm font-medium text-neutral-950 mb-3">Specialty <span className="text-red-500">*</span></label>
                <div
                  className={`rounded-lg border bg-white p-4 ${
                    hasNoSpecialty && error === "Select at least one specialty." ? "border-red-300" : "border-neutral-200"
                  }`}
                >
                  {specialtyOptions.length === 0 ? (
                    <p className="text-sm text-gray-400">No active specialties are configured.</p>
                  ) : (
                    <div className="grid grid-cols-1 gap-x-20 gap-y-3 sm:grid-cols-2">
                      {specialtyOptions.map((s) => (
                        <label key={s.id} className="flex items-center gap-2 rounded px-1 py-1 text-sm text-gray-700 hover:bg-gray-50">
                          <input
                            type="checkbox"
                            checked={setupForm.specialtyIds.includes(s.id)}
                            onChange={() => toggleSpecialty(s.id)}
                            className="size-5 rounded-md border-[1.5px] border-neutral-200 text-blue-600 focus:ring-blue-500"
                          />
                          <span>{s.name}</span>
                        </label>
                      ))}
                    </div>
                  )}
                </div>
                {hasNoSpecialty && error === "Select at least one specialty." && (
                  <p className="text-xs text-red-500 mt-1">Select at least one specialty.</p>
                )}
              </div>
            </div>
          </div>

          <div className="flex flex-wrap items-center justify-end gap-3">
            {error && <p className="mr-auto text-sm text-red-600">{error}</p>}
            <button
              type="button"
              onClick={handleUpdateProviderSetup}
              disabled={savingEdit || editLocations.length === 0 || hasInvalidPhone}
              className="inline-flex items-center gap-2 rounded-[10px] bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
            >
              {savingEdit && savingLocationId === null ? (
                <>
                  <span className="h-3.5 w-3.5 rounded-full border-2 border-white/60 border-t-transparent animate-spin" />
                  Saving…
                </>
              ) : (
                <>
                  <i className="ri-save-line" />
                  Save Provider Setup
                </>
              )}
            </button>
          </div>

          <div className="flex items-center justify-between border-t border-gray-100 pt-6">
            <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500">Facilities</h3>
            <div className="flex items-center gap-3">
              <span className="text-xs text-gray-400">
                {activeEditLocations.length} location{activeEditLocations.length === 1 ? "" : "s"}
              </span>
              <button
                type="button"
                onClick={() => {
                  setAddingLocation(true);
                  setNewLocation(EMPTY_NEW_LOCATION);
                }}
                disabled={addingLocation}
                className="inline-flex items-center gap-1 rounded-md border border-cyan-300 bg-white px-2.5 py-1 text-xs font-medium text-cyan-700 hover:bg-cyan-50 disabled:opacity-50 transition-colors"
              >
                <i className="ri-map-pin-add-line" />
                Add location
              </button>
            </div>
          </div>

          <div className="space-y-3">
            {activeEditLocations.map((location, index) => {
              const invalidZip = locationHasInvalidPostalCode(location);
              const invalidRadius = locationHasInvalidServiceRadius(location);
              const saving = savingLocationId === location.entryId;
              const deleting = deletingLocationId === location.entryId;
              const isLastRemainingLocation = activeEditLocations.length <= 1;

              return (
                <form
                  key={location.entryId}
                  onSubmit={(e) => handleUpdateLocation(e, location.entryId)}
                  className="rounded-lg border border-slate-200 bg-white p-6 space-y-5"
                >
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="text-sm font-semibold text-gray-900">Location {index + 1}</p>
                        <span
                          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
                            location.isActive
                              ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                              : "bg-gray-50 text-gray-500 border-gray-200"
                          }`}
                        >
                          {location.isActive ? "Active" : "Inactive"}
                        </span>
                        <span
                          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
                            location.acceptingReferrals
                              ? "bg-green-50 text-green-700 border-green-200"
                              : "bg-amber-50 text-amber-700 border-amber-200"
                          }`}
                        >
                          {location.acceptingReferrals ? "Accepting" : "Not accepting"}
                        </span>
                      </div>
                      <p className="mt-0.5 truncate text-sm text-gray-500">
                        {formatLocationLine({ ...location, serviceRadiusMiles: Number(location.serviceRadiusMiles) })}
                      </p>
                    </div>
                    <button
                      type="button"
                      onClick={() => requestDeleteLocation(location.entryId, location.facilityName)}
                      disabled={deleting || saving || isLastRemainingLocation}
                      title={isLastRemainingLocation ? "A provider must have at least one location" : undefined}
                      className="inline-flex items-center gap-1 rounded-[10px] border border-red-200 bg-white px-3 py-1.5 text-xs font-medium text-red-600 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      <i className={deleting ? "ri-loader-4-line animate-spin" : "ri-delete-bin-line"} />
                      {deleting ? "Deleting…" : "Delete location"}
                    </button>
                  </div>

                  <div className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      id={`locationIsMobile-${location.entryId}`}
                      checked={location.isMobile}
                      onChange={(e) =>
                        updateEditLocation(location.entryId, {
                          isMobile: e.target.checked,
                          postalCode: e.target.checked ? "" : location.postalCode,
                        })
                      }
                      className="size-5 rounded-md border-[1.5px] border-neutral-200 text-violet-600 focus:ring-violet-500"
                    />
                    <label htmlFor={`locationIsMobile-${location.entryId}`} className="text-sm font-medium text-neutral-950">
                      Mobile / roaming provider (no fixed address)
                    </label>
                  </div>
                  <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
                    <div className="lg:col-span-2">
                      <label className="block text-sm font-medium text-neutral-950 mb-3">
                        {location.isMobile ? "Service area description" : "Address"} <span className="text-red-500">*</span>
                      </label>
                      <input
                        required
                        value={location.addressLine1}
                        onChange={(e) => updateEditLocation(location.entryId, { addressLine1: e.target.value })}
                        placeholder={location.isMobile ? "e.g. Greater Las Vegas Metro" : "123 Main St"}
                        className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-neutral-950 mb-3">City <span className="text-red-500">*</span></label>
                      <input
                        required
                        value={location.city}
                        onChange={(e) => updateEditLocation(location.entryId, { city: e.target.value })}
                        className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                    <div className="flex gap-2">
                      <div className="flex-1">
                        <label className="block text-sm font-medium text-neutral-950 mb-3">State <span className="text-red-500">*</span></label>
                        <input
                          required
                          value={location.state}
                          onChange={(e) => updateEditLocation(location.entryId, { state: e.target.value })}
                          placeholder="IL"
                          maxLength={2}
                          className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm uppercase focus:border-blue-500 focus:outline-none"
                        />
                      </div>
                      <div className="flex-1">
                        {location.isMobile ? (
                          <>
                            <label className="block text-sm font-medium text-neutral-950 mb-3">Service radius (mi) <span className="text-red-500">*</span></label>
                            <input
                              required
                              type="number"
                              min={1}
                              max={MAX_SERVICE_RADIUS_MILES}
                              value={location.serviceRadiusMiles}
                              onChange={(e) => updateEditLocation(location.entryId, { serviceRadiusMiles: e.target.value })}
                              className={`w-full h-9 rounded-lg border bg-white px-3 py-1 text-sm focus:outline-none ${
                                invalidRadius ? "border-red-300 focus:border-red-400" : "border-neutral-200 focus:border-blue-500"
                              }`}
                            />
                            {invalidRadius && (
                              <p className="text-xs text-red-500 mt-1">
                                Enter a radius between 1 and {MAX_SERVICE_RADIUS_MILES} miles.
                              </p>
                            )}
                          </>
                        ) : (
                          <>
                            <label className="block text-sm font-medium text-neutral-950 mb-3">ZIP <span className="text-red-500">*</span></label>
                            <input
                              required
                              value={location.postalCode}
                              onChange={(e) => updateEditLocation(location.entryId, { postalCode: e.target.value })}
                              placeholder="60601"
                              className={`w-full h-9 rounded-lg border bg-white px-3 py-1 text-sm focus:outline-none ${
                                invalidZip ? "border-red-300 focus:border-red-400" : "border-neutral-200 focus:border-blue-500"
                              }`}
                            />
                            {invalidZip && <p className="text-xs text-red-500 mt-1">ZIP code must be 5 digits or 5+4 format.</p>}
                          </>
                        )}
                      </div>
                    </div>
                  </div>

                  <div className="flex flex-wrap items-center justify-between gap-3 border-t border-gray-100 pt-4">
                    <div className="flex items-center gap-5">
                      <label className="flex items-center gap-2 cursor-pointer select-none">
                        <input
                          type="checkbox"
                          checked={location.isActive}
                          onChange={(e) => updateEditLocation(location.entryId, { isActive: e.target.checked })}
                          className="size-5 rounded-md border-[1.5px] border-neutral-200 text-blue-600"
                        />
                        <span className="text-sm text-gray-700">Active</span>
                      </label>
                      <label className="flex items-center gap-2 cursor-pointer select-none">
                        <input
                          type="checkbox"
                          checked={location.acceptingReferrals}
                          onChange={(e) => updateEditLocation(location.entryId, { acceptingReferrals: e.target.checked })}
                          className="size-5 rounded-md border-[1.5px] border-neutral-200 text-blue-600"
                        />
                        <span className="text-sm text-gray-700">Accepting referrals</span>
                      </label>
                    </div>
                    <button
                      type="submit"
                      disabled={savingEdit || deleting || hasInvalidPhone || invalidZip || invalidRadius}
                      className="inline-flex items-center gap-2 rounded-[10px] bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                    >
                      {saving ? (
                        <>
                          <span className="h-3.5 w-3.5 rounded-full border-2 border-white/60 border-t-transparent animate-spin" />
                          Saving…
                        </>
                      ) : (
                        <>
                          <i className="ri-save-line" />
                          Save Location
                        </>
                      )}
                    </button>
                  </div>
                </form>
              );
            })}

            {addingLocation && (
              <form
                onSubmit={handleSaveNewLocation}
                className="rounded-lg border border-cyan-200 bg-cyan-50/30 p-6 space-y-5"
              >
                <div className="flex items-center justify-between">
                  <p className="text-sm font-semibold text-gray-900">New location</p>
                  <button
                    type="button"
                    onClick={() => setAddingLocation(false)}
                    className="text-xs text-gray-500 hover:underline"
                  >
                    Cancel
                  </button>
                </div>
                <div className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    id="newLocationIsMobile"
                    checked={newLocation.isMobile}
                    onChange={(e) =>
                      setNewLocation((f) => ({ ...f, isMobile: e.target.checked, postalCode: e.target.checked ? "" : f.postalCode }))
                    }
                    className="size-5 rounded-md border-[1.5px] border-neutral-200 text-violet-600 focus:ring-violet-500"
                  />
                  <label htmlFor="newLocationIsMobile" className="text-sm font-medium text-neutral-950">
                    Mobile / roaming provider (no fixed address)
                  </label>
                </div>
                <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
                  <div className="lg:col-span-2">
                    <label className="block text-sm font-medium text-neutral-950 mb-3">
                      {newLocation.isMobile ? "Service area description" : "Address"} <span className="text-red-500">*</span>
                    </label>
                    <input
                      required
                      value={newLocation.addressLine1}
                      onChange={(e) => setNewLocation((f) => ({ ...f, addressLine1: e.target.value }))}
                      placeholder={newLocation.isMobile ? "e.g. Greater Las Vegas Metro" : "123 Main St"}
                      className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-neutral-950 mb-3">City <span className="text-red-500">*</span></label>
                    <input
                      required
                      value={newLocation.city}
                      onChange={(e) => setNewLocation((f) => ({ ...f, city: e.target.value }))}
                      className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm focus:border-blue-500 focus:outline-none"
                    />
                  </div>
                  <div className="flex gap-2">
                    <div className="flex-1">
                      <label className="block text-sm font-medium text-neutral-950 mb-3">State <span className="text-red-500">*</span></label>
                      <input
                        required
                        value={newLocation.state}
                        onChange={(e) => setNewLocation((f) => ({ ...f, state: e.target.value }))}
                        placeholder="IL"
                        maxLength={2}
                        className="w-full h-9 rounded-lg border border-neutral-200 bg-white px-3 py-1 text-sm uppercase focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                    <div className="flex-1">
                      {newLocation.isMobile ? (
                        <>
                          <label className="block text-sm font-medium text-neutral-950 mb-3">Service radius (mi) <span className="text-red-500">*</span></label>
                          <input
                            required
                            type="number"
                            min={1}
                            max={MAX_SERVICE_RADIUS_MILES}
                            value={newLocation.serviceRadiusMiles}
                            onChange={(e) => setNewLocation((f) => ({ ...f, serviceRadiusMiles: e.target.value }))}
                            className={`w-full h-9 rounded-lg border bg-white px-3 py-1 text-sm focus:outline-none ${
                              hasInvalidNewServiceRadius ? "border-red-300 focus:border-red-400" : "border-neutral-200 focus:border-blue-500"
                            }`}
                          />
                          {hasInvalidNewServiceRadius && (
                            <p className="text-xs text-red-500 mt-1">
                              Enter a radius between 1 and {MAX_SERVICE_RADIUS_MILES} miles.
                            </p>
                          )}
                        </>
                      ) : (
                        <>
                          <label className="block text-sm font-medium text-neutral-950 mb-3">ZIP <span className="text-red-500">*</span></label>
                          <input
                            required
                            value={newLocation.postalCode}
                            onChange={(e) => setNewLocation((f) => ({ ...f, postalCode: e.target.value }))}
                            placeholder="60601"
                            className={`w-full h-9 rounded-lg border bg-white px-3 py-1 text-sm focus:outline-none ${
                              hasInvalidNewPostalCode ? "border-red-300 focus:border-red-400" : "border-neutral-200 focus:border-blue-500"
                            }`}
                          />
                          {hasInvalidNewPostalCode && <p className="text-xs text-red-500 mt-1">ZIP code must be 5 digits or 5+4 format.</p>}
                        </>
                      )}
                    </div>
                  </div>
                </div>
                <div className="flex items-center justify-between gap-3 border-t border-cyan-100 pt-4">
                  <div className="flex items-center gap-5">
                    <label className="flex items-center gap-2 cursor-pointer select-none">
                      <input
                        type="checkbox"
                        checked={newLocation.isActive}
                        onChange={(e) => setNewLocation((f) => ({ ...f, isActive: e.target.checked }))}
                        className="size-5 rounded-md border-[1.5px] border-neutral-200 text-blue-600"
                      />
                      <span className="text-sm text-gray-700">Active</span>
                    </label>
                    <label className="flex items-center gap-2 cursor-pointer select-none">
                      <input
                        type="checkbox"
                        checked={newLocation.acceptingReferrals}
                        onChange={(e) => setNewLocation((f) => ({ ...f, acceptingReferrals: e.target.checked }))}
                        className="size-5 rounded-md border-[1.5px] border-neutral-200 text-blue-600"
                      />
                      <span className="text-sm text-gray-700">Accepting referrals</span>
                    </label>
                  </div>
                  <button
                    type="submit"
                    disabled={savingNewLocation || hasInvalidPhone || hasInvalidNewPostalCode || hasInvalidNewServiceRadius}
                    className="inline-flex items-center gap-2 rounded-[10px] bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                  >
                    {savingNewLocation ? (
                      <>
                        <span className="h-3.5 w-3.5 rounded-full border-2 border-white/60 border-t-transparent animate-spin" />
                        Adding…
                      </>
                    ) : (
                      <>
                        <i className="ri-map-pin-add-line" />
                        Save Location
                      </>
                    )}
                  </button>
                </div>
              </form>
            )}
          </div>

          <button
            type="button"
            onClick={onClose}
            className="rounded-[10px] border border-neutral-200 bg-white px-4 py-2 text-sm font-medium text-neutral-950 hover:bg-gray-50"
          >
            Close
          </button>
        </div>
      </Modal>
    </>
  );
}
