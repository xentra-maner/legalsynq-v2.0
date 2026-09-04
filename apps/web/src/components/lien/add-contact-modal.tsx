"use client";

import { useEffect, useState } from "react";
import { FormModal } from "@/components/lien/modal";
import { toast } from "sonner";
import { useSessionContext } from "@/providers/session-provider";
import { contactsService } from "@/lib/contacts";
import type { ContactDetail } from "@/lib/contacts";
import type { LookupData } from "@/lib/lookup/lookup.types";
import { ApiError } from "@/lib/api-client";
import { Combobox } from "@/components/ui/combobox";
import { formatPhoneInput, isValidPhone } from "@/lib/phone";
import { formatZipInput, isValidUsZipCode } from "@/lib/address";

/** Minimal shape needed to prefill the edit form — satisfied by both
 * ContactResponseDto (contact detail sections) and ContactListItem
 * (contacts list page), which lacks addressLine1/postalCode/contactSubtype. */
interface EditableContact {
  id: string;
  firstName: string;
  lastName: string;
  contactType: string;
  email?: string | null;
  phone?: string | null;
  phoneExtension?: string | null;
  addressLine1?: string | null;
  city?: string | null;
  state?: string | null;
  postalCode?: string | null;
  contactSubtype?: string | null;
}

interface AddContactModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: (contact: ContactDetail) => void;
  /** Fixed contact type; omit when `contactTypeOptions` is used instead. */
  contactType?: string;
  /** When set, renders a Contact Type select (disabled while editing) instead of a fixed contactType. */
  contactTypeOptions?: LookupData[];
  lawFirmId?: string;
  facilityId?: string;
  /** Generic parent-contact link for contact-person flows not represented by lawFirmId/facilityId. */
  parentContactId?: string;
  /** Fixed sub-contact role. Ignored when `roleOptions` is passed. */
  contactSubtype?: string;
  /** Renders a Role select bound to contactSubtype. */
  roleOptions?: LookupData[];
  /** Allows creating a new role inline from the picker, if the caller supports it. */
  allowCreateRole?: boolean;
  /** Called when a new role is created and should be added to the current options list. */
  onRoleCreated?: (role: LookupData) => void;
  title: string;
  subtitle?: string;
  /** Presence => edit mode: prefills the form and calls updateContact. */
  editTarget?: EditableContact | null;
  /** Hides the address/city/state/zip fields — used for sub-contacts (staff, law firm contacts) who don't need their own address. */
  hideAddress?: boolean;
  /** Overrides the default bg-primary styling on the Save button (e.g. selling's orange brand). */
  primaryButtonClassName?: string;
}

interface ContactTypeIconConfig {
  icon: string;
  iconBg: string;
  labelText: string;
  label: string;
}

const DEFAULT_ICON: ContactTypeIconConfig = {
  icon: "ri-contacts-book-line",
  iconBg: "bg-[#4184ff]",
  labelText: "text-[#0e5388]",
  label: "Contact Information",
};

/** Sub-role icons take priority over the parent contactType's icon. */
const SUBTYPE_ICONS: Record<string, ContactTypeIconConfig> = {
  facilitycontactperson: {
    icon: "ri-nurse-line",
    iconBg: "bg-blue-500",
    labelText: "text-sky-800",
    label: "Contact Person",
  },
  casemanager: {
    icon: "ri-briefcase-line",
    iconBg: "bg-green-500",
    labelText: "text-sky-800",
    label: "Case Manager",
  },
};

const CONTACT_TYPE_ICONS: Record<string, ContactTypeIconConfig> = {
  MedicalFacility: {
    icon: "ri-stethoscope-line",
    iconBg: "bg-[#f9c851]",
    labelText: "text-[#0e5388]",
    label: "Medical Facility",
  },
  LawFirm: {
    icon: "ri-scales-line",
    iconBg: "bg-[#6d65e6]",
    labelText: "text-[#0e5388]",
    label: "Law Firm",
  },
  Provider: {
    icon: "ri-first-aid-kit-line",
    iconBg: "bg-[#4fd1c5]",
    labelText: "text-[#0e5388]",
    label: "Medical Provider",
  },
  FundingCompany: {
    icon: "ri-money-dollar-box-line",
    iconBg: "bg-[#81d07d]",
    labelText: "text-[#0e5388]",
    label: "Funding Company",
  },
  Lead: {
    icon: "ri-user-star-line",
    iconBg: "bg-[#ff8acc]",
    labelText: "text-[#0e5388]",
    label: "Lead",
  },
};
// LienHolder and Facility are legacy aliases (see LEGACY_CONTACT_TYPE_CODE_ALIASES
// in add-contact-form.tsx) for FundingCompany and MedicalFacility respectively.
CONTACT_TYPE_ICONS.LienHolder = CONTACT_TYPE_ICONS.FundingCompany;
CONTACT_TYPE_ICONS.Facility = CONTACT_TYPE_ICONS.MedicalFacility;

const LAW_FIRM_SUBCONTACT_ICON: ContactTypeIconConfig = {
  icon: "ri-briefcase-line",
  iconBg: "bg-[#81d07d]",
  labelText: "text-[#0e5388]",
  label: "Legal Contacts",
};

function getContactTypeIcon(
  contactType: string,
  contactSubtype: string,
): ContactTypeIconConfig {
  return (
    SUBTYPE_ICONS[contactSubtype.toLowerCase()] ??
    CONTACT_TYPE_ICONS[contactType] ??
    DEFAULT_ICON
  );
}

const EMPTY_FORM = {
  firstName: "",
  lastName: "",
  fullName: "",
  contactType: "",
  contactSubtype: "",
  email: "",
  phone: "",
  phoneExtension: "",
  addressLine1: "",
  city: "",
  state: "",
  postalCode: "",
};

type ValidatableField =
  | "contactType"
  | "contactSubtype"
  | "firstName"
  | "lastName"
  | "fullName"
  | "email"
  | "phone"
  | "phoneExtension"
  | "postalCode";

export function AddContactModal({
  open,
  onClose,
  onSaved,
  contactType,
  contactTypeOptions,
  lawFirmId,
  facilityId,
  parentContactId,
  contactSubtype,
  roleOptions,
  allowCreateRole,
  onRoleCreated,
  title,
  subtitle,
  editTarget,
  hideAddress,
  primaryButtonClassName,
}: AddContactModalProps) {
  const { lookup } = useSessionContext();
  const isEdit = Boolean(editTarget);
  const hasExtension = Boolean(contactType === "LawFirm");
  // Sub-contacts (facility/law firm staff, case managers) keep separate
  // First/Last Name inputs; everything else ("main" contacts) collapses
  // them into a single Full Name input that's split on submit.
  const isSubContact =
    Boolean(hideAddress) ||
    Boolean(contactSubtype) ||
    Boolean(roleOptions && roleOptions.length > 0);
  const isMainContact = !isSubContact;

  const [form, setForm] = useState({
    ...EMPTY_FORM,
    contactType: contactType ?? "",
    contactSubtype: contactSubtype ?? "",
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);

  const states =
    lookup?.State?.map((s) => ({
      key: s.id,
      value: s.code,
      label: `${s.name} (${s.code})`,
    })) ?? [];

  useEffect(() => {
    if (!open) return;
    if (editTarget) {
      const firstName = editTarget.firstName ?? "";
      const lastName = editTarget.lastName ?? "";
      setForm({
        firstName,
        lastName,
        fullName: `${firstName} ${lastName}`.trim(),
        contactType: editTarget.contactType ?? contactType ?? "",
        contactSubtype: editTarget.contactSubtype ?? contactSubtype ?? "",
        email: editTarget.email ?? "",
        phone: editTarget.phone ?? "",
        phoneExtension: editTarget.phoneExtension ?? "",
        addressLine1: editTarget.addressLine1 ?? "",
        city: editTarget.city ?? "",
        state: editTarget.state ?? "",
        postalCode: editTarget.postalCode ?? "",
      });
    } else {
      setForm({
        ...EMPTY_FORM,
        contactType: contactType ?? "",
        contactSubtype: contactSubtype ?? "",
      });
    }
    setErrors({});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, editTarget]);

  const FIELDS: readonly ValidatableField[] = isMainContact
    ? [
        "contactType",
        "contactSubtype",
        "fullName",
        "email",
        "phone",
        "postalCode",
      ]
    : [
        "contactType",
        "contactSubtype",
        "firstName",
        "lastName",
        "email",
        "phone",
        "postalCode",
      ];

  const validateField = (
    field: ValidatableField,
    f: typeof form,
  ): string | undefined => {
    switch (field) {
      case "contactType":
        return f.contactType ? undefined : "Type is required";
      case "contactSubtype":
        return roleOptions && roleOptions.length > 0 && !f.contactSubtype
          ? "Role is required"
          : undefined;
      case "firstName":
        return f.firstName.trim() ? undefined : "First name is required";
      case "lastName":
        return f.lastName.trim() ? undefined : "Last name is required";
      case "fullName":
        return f.fullName.trim() ? undefined : "Full name is required";
      case "email":
        return f.email && !/^\S+@\S+\.\S+$/.test(f.email)
          ? "Invalid email format"
          : undefined;
      case "phone":
        return f.phone && !isValidPhone(f.phone)
          ? "Phone number must be 10 digits"
          : undefined;
      case "postalCode":
        return !hideAddress && f.postalCode && !isValidUsZipCode(f.postalCode)
          ? "Zip code must be 5 or 9 digits (XXXXX or XXXXX-XXXX)"
          : undefined;
    }
  };

  const validate = () => {
    const e: Record<string, string> = {};
    for (const field of FIELDS) {
      const msg = validateField(field, form);
      if (msg) e[field] = msg;
    }
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const handleSubmit = async () => {
    if (!validate()) return;
    setSubmitting(true);
    try {
      const payload = {
        contactType: form.contactType,
        contactSubtype: form.contactSubtype || undefined,
        lawFirmId: lawFirmId || undefined,
        facilityId: facilityId || undefined,
        parentContactId: parentContactId || undefined,
        ...(isMainContact
          ? { fullName: form.fullName.trim() }
          : {
              firstName: form.firstName.trim(),
              lastName: form.lastName.trim(),
            }),
        email: form.email.trim() || undefined,
        phone: form.phone.trim() || undefined,
        phoneExtension: form.phoneExtension.trim() || undefined,
        ...(hideAddress
          ? {}
          : {
              addressLine1: form.addressLine1.trim() || undefined,
              city: form.city.trim() || undefined,
              state: form.state || undefined,
              postalCode: form.postalCode.trim() || undefined,
            }),
      };

      const saved = isEdit
        ? await contactsService.updateContact(editTarget!.id, payload)
        : await contactsService.createContact(payload);

      const savedName = isMainContact
        ? form.fullName.trim()
        : `${form.firstName} ${form.lastName}`;
      toast.success(isEdit ? "Contact updated" : "Contact created", {
        description: savedName,
      });
      onSaved(saved);
    } catch (err) {
      toast.error(
        isEdit ? "Couldn't update contact" : "Couldn't create contact",
        {
          description:
            err instanceof ApiError
              ? err.message
              : "An unexpected error occurred",
        },
      );
    } finally {
      setSubmitting(false);
    }
  };

  // Revalidates a field the moment it changes — the act of changing it is what
  // marks it "dirty", so required-but-untouched fields still surface their
  // error only on submit (see `validate`), while edited fields update live.
  const setField = <K extends keyof typeof EMPTY_FORM>(
    field: K,
    value: string,
  ) => {
    const next = { ...form, [field]: value };
    setForm(next);
    setErrors((e) => {
      const isValidatedField = (FIELDS as readonly string[]).includes(field);
      const msg = isValidatedField
        ? validateField(field as (typeof FIELDS)[number], next)
        : undefined;
      if (!msg) {
        if (!e[field]) return e;
        const { [field]: _removed, ...rest } = e;
        return rest;
      }
      return { ...e, [field]: msg };
    });
  };

  const inputCls = (field: string) =>
    `w-full border rounded-lg px-3 py-2 text-sm ${errors[field] ? "border-red-300" : "border-gray-200"} focus:outline-none focus:ring-2 focus:ring-primary/20 focus:border-primary`;

  // When a Contact Type select is shown, the type is still being chosen, so the
  // header stays generic rather than jumping between per-type icons.
  // When a Role select is shown instead, the role is user-chosen and shouldn't
  // override the parent entity's header (e.g. Law Firm contacts keep the
  // briefcase header regardless of which role is selected).
  const typeIcon = contactTypeOptions
    ? DEFAULT_ICON
    : roleOptions
      ? form.contactType === "LawFirm"
        ? LAW_FIRM_SUBCONTACT_ICON
        : (CONTACT_TYPE_ICONS[form.contactType] ?? DEFAULT_ICON)
      : getContactTypeIcon(form.contactType, form.contactSubtype);

  return (
    <FormModal
      open={open}
      onClose={onClose}
      onSubmit={handleSubmit}
      title={title}
      subtitle={subtitle}
      submitLabel={submitting ? (isEdit ? "Saving..." : "Creating...") : "Save"}
      submitDisabled={submitting}
      primaryButtonClassName={primaryButtonClassName}
    >
      <div className="flex items-center gap-2.5 mb-4">
        <div
          className={`w-9 h-9 rounded-lg ${typeIcon.iconBg} text-white flex items-center justify-center shrink-0`}
        >
          <i className={`${typeIcon.icon} text-lg`} />
        </div>
        <span className={`text-sm font-semibold ${typeIcon.labelText}`}>
          {typeIcon.label}
        </span>
      </div>

      <div className="space-y-4">
        {contactTypeOptions && (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Contact Type<span className="text-red-500 ml-0.5">*</span>
            </label>
            <Combobox
              value={form.contactType}
              onChange={(v) => setField("contactType", v)}
              options={contactTypeOptions.map((t) => ({
                value: t.code,
                label: t.name.toUpperCase(),
              }))}
              disabled={isEdit}
              error={Boolean(errors.contactType)}
              placeholder="Select..."
              clearable
            />
            {errors.contactType && (
              <p className="text-xs text-red-500 mt-1">{errors.contactType}</p>
            )}
          </div>
        )}

        {isMainContact ? (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Name<span className="text-red-500 ml-0.5">*</span>
            </label>
            <input
              type="text"
              value={form.fullName}
              onChange={(e) => setField("fullName", e.target.value)}
              placeholder="Name"
              className={inputCls("fullName")}
            />
            {errors.fullName && (
              <p className="text-xs text-red-500 mt-1">{errors.fullName}</p>
            )}
          </div>
        ) : (
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                First Name<span className="text-red-500 ml-0.5">*</span>
              </label>
              <input
                type="text"
                value={form.firstName}
                onChange={(e) => setField("firstName", e.target.value)}
                placeholder="First name"
                className={inputCls("firstName")}
              />
              {errors.firstName && (
                <p className="text-xs text-red-500 mt-1">{errors.firstName}</p>
              )}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Last Name<span className="text-red-500 ml-0.5">*</span>
              </label>
              <input
                type="text"
                value={form.lastName}
                onChange={(e) => setField("lastName", e.target.value)}
                placeholder="Last name"
                className={inputCls("lastName")}
              />
              {errors.lastName && (
                <p className="text-xs text-red-500 mt-1">{errors.lastName}</p>
              )}
            </div>
          </div>
        )}

        <div className="grid grid-cols-1 gap-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Email Address
            </label>
            <input
              type="email"
              value={form.email}
              onChange={(e) => setField("email", e.target.value)}
              placeholder="email@example.com"
              className={inputCls("email")}
            />
            {errors.email && (
              <p className="text-xs text-red-500 mt-1">{errors.email}</p>
            )}
          </div>
        </div>
        <div
          className={
            hasExtension ? "grid grid-cols-2 gap-4" : "grid grid-cols-1 gap-4"
          }
        >
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Phone
            </label>
            <input
              type="text"
              value={form.phone}
              onChange={(e) =>
                setField("phone", formatPhoneInput(e.target.value))
              }
              placeholder="(555) 555-0000"
              className={inputCls("phone")}
            />
            {errors.phone && (
              <p className="text-xs text-red-500 mt-1">{errors.phone}</p>
            )}
          </div>

          {hasExtension && (
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Extension
              </label>
              <input
                type="text"
                value={form.phoneExtension}
                onChange={(e) => setField("phoneExtension", e.target.value)}
                placeholder=""
                className={inputCls("phone")}
              />
              {errors.phoneExtension && (
                <p className="text-xs text-red-500 mt-1">
                  {errors.phoneExtension}
                </p>
              )}
            </div>
          )}
        </div>

        {!hideAddress && (
          <>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Address
              </label>
              <input
                type="text"
                value={form.addressLine1}
                onChange={(e) => setField("addressLine1", e.target.value)}
                placeholder="Address"
                className={inputCls("addressLine1")}
              />
            </div>

            <div className="grid grid-cols-3 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  City
                </label>
                <input
                  type="text"
                  value={form.city}
                  onChange={(e) => setField("city", e.target.value)}
                  placeholder="City"
                  className={inputCls("city")}
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  State
                </label>
                <Combobox
                  value={form.state}
                  onChange={(v) => setField("state", v)}
                  options={states.map((s) => ({
                    value: s.value,
                    label: s.label,
                  }))}
                  error={Boolean(errors.state)}
                  placeholder="Select..."
                  clearable
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Zip Code
                </label>
                <input
                  type="text"
                  value={form.postalCode}
                  onChange={(e) =>
                    setField("postalCode", formatZipInput(e.target.value))
                  }
                  placeholder="Zip Code"
                  className={inputCls("postalCode")}
                />
                {errors.postalCode && (
                  <p className="text-xs text-red-500 mt-1">
                    {errors.postalCode}
                  </p>
                )}
              </div>
            </div>
          </>
        )}

        {roleOptions && roleOptions.length > 0 && (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              <span className="text-red-500 mr-0.5">*</span>Role
            </label>
            <Combobox
              value={form.contactSubtype}
              onChange={(v) => setField("contactSubtype", v)}
              options={roleOptions.map((r) => ({
                value: r.code,
                label: r.name,
              }))}
              error={Boolean(errors.contactSubtype)}
              placeholder="— Select Role —"
              clearable
            />
            {errors.contactSubtype && (
              <p className="text-xs text-red-500 mt-1">
                {errors.contactSubtype}
              </p>
            )}
          </div>
        )}
      </div>
    </FormModal>
  );
}
