export interface ContactResponseDto {
  id: string;
  contactType: string;
  firstName: string;
  lastName: string;
  displayName: string;
  title?: string | null;
  organization?: string | null;
  email?: string | null;
  phone?: string | null;
  phoneExtension?: string | null;
  fax?: string | null;
  website?: string | null;
  addressLine1?: string | null;
  city?: string | null;
  state?: string | null;
  postalCode?: string | null;
  notes?: string | null;
  isActive: boolean;
  activeCases: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  /**
   * Present (non-null) only for MedicalFacility contacts. `null`/absent for
   * every other contactType.
   */
  contactSubtype?: string | null;
  lawFirmId?: string | null;
  /**
   * Overloaded — its meaning depends on `contactSubtype` on this same
   * record, not on some other lookup:
   *
   * - `contactSubtype` is null (a "main" MedicalFacility contact — the
   *   facility org record itself, e.g. "Arlington General Hospital"): this
   *   is the **legacy Facility entity id** — a different entity than any
   *   Contact record, from the pre-unified-Contacts data model that the
   *   lien-facility API (get-facility/update-facility) still speaks. It is
   *   NOT this contact's own `id`, and a direct `contacts/{id}` lookup
   *   using it 404s (see contact-entity-select.tsx's
   *   `needsFacilityFallback` — it falls back to a `FacilityId=`-scoped
   *   search instead). May be `null` for a main contact created entirely
   *   through the unified Contacts UI, which never got a legacy link.
   * - `contactSubtype` is `"FacilityContactPerson"` (staff under a
   *   facility): this is a **Contact id** — the parent main contact's own
   *   `id` (see AddContactModal, which passes the selected facility's
   *   `id` straight through as the new sub-contact's `facilityId`).
   *
   * In short: on a main contact, `facilityId` points at a different kind of
   * entity entirely (legacy Facility, not Contact); on a sub-contact, it
   * points at another Contact (its parent facility).
   */
  facilityId?: string | null;
}

export interface PaginatedResultDto<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface CreateContactRequestDto {
  contactType: string;
  fullName?: string;
  fullname?: string;
  firstName?: string;
  lastName?: string;
  title?: string;
  organization?: string;
  email?: string;
  phone?: string;
  fax?: string;
  website?: string;
  addressLine1?: string;
  city?: string;
  state?: string;
  postalCode?: string;
  notes?: string;
  contactSubtype?: string;
  lawFirmId?: string;
  facilityId?: string;
  /**
   * TODO(backend): speculative generic parent-contact link, for contact
   * types other than LawFirm/MedicalFacility (which use lawFirmId/
   * facilityId above). Not yet supported by the API — wired up ahead of
   * the backend change so "Contact Person" creation for any contact type
   * (see components/selling/contact-person-section.tsx) just needs the
   * API to start reading this field. Sent as undefined until then.
   */
  parentContactId?: string;
}

export interface UpdateContactRequestDto {
  contactType: string;
  fullName?: string;
  fullname?: string;
  firstName?: string;
  lastName?: string;
  title?: string;
  organization?: string;
  email?: string;
  phone?: string;
  fax?: string;
  website?: string;
  addressLine1?: string;
  city?: string;
  state?: string;
  postalCode?: string;
  notes?: string;
  contactSubtype?: string;
  lawFirmId?: string;
  facilityId?: string;
  /** TODO(backend): see the matching field on CreateContactRequestDto. */
  parentContactId?: string;
}

export interface ContactsQuery {
  search?: string;
  ContactType?: string;
  isActive?: boolean;
  page?: number;
  pageSize?: number;
  LawFirmId?: string;
  FacilityId?: string;
  /**
   * TODO(backend): speculative generic parent-contact filter, the query
   * counterpart of CreateContactRequestDto.parentContactId above. Not yet
   * supported by the API.
   */
  ParentContactId?: string;
  // Explicit "" (as opposed to omitting the field) tells the API to only
  // return contacts with no subtype — i.e. main contacts, not sub-contacts
  // like law firm staff or facility staff.
  ContactSubtype?: string | null;
}

export interface ContactListItem {
  id: string;
  firstName: string;
  lastName: string;
  contactType: string;
  displayName: string;
  organization: string;
  email: string;
  phone: string;
  city: string;
  state: string;
  isActive: boolean;
  activeCases: number;
  createdAt: string;
  /** Overloaded by this record's contactSubtype — see the doc on ContactResponseDto.facilityId above. */
  facilityId: string | null;
  lawFirmId: string | null;
}

export interface ContactDetail extends ContactListItem {
  firstName: string;
  lastName: string;
  title: string;
  fax: string;
  website: string;
  addressLine1: string;
  postalCode: string;
  notes: string;
  updatedAt: string;
  contactSubtype: string | null;
  facilityId: string | null;
  lawFirmId: string | null;
}

export interface ExportResponse {
  data: string;
}

export interface ContactCaseSummary {
  id: string;
  caseNumber: string;
  personName: string;
  accidentType: string | null;
  dateOfLoss: string | null;
  dateOfBirth: string | null;
  status: string;
  // Lien-level fields — the case v3 endpoints (law/leads/medical/facility/funding)
  // return case records only, with no lien id or amounts. Null until the
  // API/type exposes them.
  lienId: string | null;
  billingAmount: number | null;
  purchaseAmount: number | null;
  // Sum of originalAmount across the case's liens. For case-level contact
  // types (LawFirm/Lead) this is the case's real total. For lien-level
  // types (MedicalFacility/Provider/FundingCompany) this is currently
  // summed over ALL of the case's liens, not just this contact's — see
  // the KNOWN ISSUE note in contacts.service.ts#getCasesByContact.
  totalBilling: number | null;
}

export interface PaginationMeta {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}
