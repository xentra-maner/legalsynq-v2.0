import { StatusBadge } from "@/components/lien/status-badge";
import { ContactEntitySelect } from "@/components/lien/contact-entity-select";
import Field from "@/components/lien/field";
import type { CaseDetail } from "@/lib/cases";
import type { DropdownOption } from "@/lib/lookup/lookup.types";
import { CollapsibleSection } from "../../../components/collapsible-section";
import { FieldGrid, FieldItem } from "../../../components/field-grid";
import type { ChangeEvent } from "react";

export function CaseTrackingSection({
  d,
  canEdit,
  editing,
  onStartEdit,
  form,
  updateField,
  tDateOfIncident,
  setTDateOfIncident,
  tTrackingFollowUpDate,
  setTTrackingFollowUpDate,
  caseStatusList,
  medicalStatus,
  accidentType,
  state,
  tSaving,
  onSave,
  onCancel,
  onUpdateCaseFlag,
  checkStatus,
}: {
  d: CaseDetail;
  canEdit: boolean;
  editing: boolean;
  onStartEdit: () => void;
  form: CaseDetail;
  updateField: (field: keyof CaseDetail, value: string) => void;
  tDateOfIncident: string;
  setTDateOfIncident: (v: string) => void;
  tTrackingFollowUpDate: string;
  setTTrackingFollowUpDate: (v: string) => void;
  caseStatusList: DropdownOption[];
  medicalStatus: DropdownOption[];
  accidentType: DropdownOption[];
  state: DropdownOption[];
  tSaving: boolean;
  onSave: () => void;
  onCancel: () => void;
  onUpdateCaseFlag: (field: keyof CaseDetail, value: string) => void;
  checkStatus: (value: string) => void;
}) {
  const flags: { label: string; key: keyof CaseDetail }[] = [
    { label: "Share this case with Associated Law Firm", key: "shareCase" },
    { label: "UCC Filed", key: "isUccFiled" },
    { label: "Case Dropped", key: "caseDropped" },
    { label: "Child Support", key: "childSupportLiens" },
    { label: "Minor Comp", key: "minorComp" },
  ];
  return (
    <CollapsibleSection
      title="Case Tracking"
      icon="ri-compass-3-line"
      onEdit={canEdit && !editing ? onStartEdit : undefined}
    >
      <div className="mb-3">
        <p className="text-xs font-medium text-gray-500 uppercase tracking-wide">
          Case Details
        </p>
      </div>

      {editing ? (
        <div className="space-y-3">
          <div className="grid grid-cols-3 gap-x-8 gap-y-3">
            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                Tracking Follow Up
              </label>

              <Field
                label=""
                type="date"
                value={tTrackingFollowUpDate}
                onChange={(e) => {
                  updateField("trackingFollowUpDate", e.toString());
                  setTTrackingFollowUpDate(e.toString());
                }}
              />
            </div>

            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                Case Status
              </label>
              <Field
                label=""
                value={form.status}
                options={caseStatusList}
                onChange={(v: string) => {
                  updateField("status", v.toString());
                  checkStatus(v.toString());
                }}
                placeholder="Case  Status"
                type="select"
              />
            </div>

            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                Current Medical Status
              </label>
              <Field
                label=""
                value={form.currentMedicalStatus}
                options={medicalStatus}
                onChange={(v: string) =>
                  updateField("currentMedicalStatus", v.toString())
                }
                placeholder="Medical Status"
                type="select"
              />
            </div>
            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                Case Type
              </label>

              <Field
                label=""
                value={form.caseType}
                options={accidentType}
                placeholder=""
                onChange={(v: string) => {
                  updateField("caseType", v.toString());
                }}
                type="select"
              />
            </div>

            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                Date of Incident
              </label>
              <Field
                label=""
                type="date"
                value={tDateOfIncident}
                onChange={(e) => {
                  setTDateOfIncident(e.toString());
                  updateField("dateOfIncident", e.toString());
                }}
                placeholder={tDateOfIncident}
                maxDate={new Date()}
              />
            </div>
            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                State of Incident
              </label>
              <Field
                label=""
                value={form.stateOfIncident}
                options={state}
                onChange={(v: string) =>
                  updateField("stateOfIncident", v.toString())
                }
                placeholder="State"
                type="select"
              />
            </div>
            <div>
              <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
                Lead
              </label>
              <ContactEntitySelect
                contactType="Lead"
                value={form.leadId}
                onChange={(v) => updateField("leadId", v)}
                placeholder="Select lead..."
                searchPlaceholder="Search leads..."
                allowCreate
                createLabel="Add Lead"
              />
            </div>
          </div>
          <div>
            <label className="block text-[11px] font-medium text-gray-400 uppercase tracking-wide mb-1">
              Case Tracking Note
            </label>
            <Field
              label=""
              value={form.notes}
              type="textarea"
              onChange={(v) => updateField("notes", v.toString())}
              placeholder=""
            />
          </div>
          <div className="flex items-center gap-2 pt-1 mt-4">
            <button
              onClick={onSave}
              disabled={tSaving}
              className="px-4 py-2 text-sm font-medium bg-primary text-white rounded-lg hover:bg-primary/90 transition-colors inline-flex items-center gap-1.5 disabled:opacity-60"
            >
              {tSaving ? (
                <>
                  <i className="ri-loader-4-line text-sm animate-spin" />
                  Saving...
                </>
              ) : (
                <>
                  <i className="ri-save-line text-sm" />
                  Save
                </>
              )}
            </button>
            <button
              onClick={onCancel}
              disabled={tSaving}
              className="px-4 py-2 text-sm font-medium text-gray-500 bg-white border border-gray-200 rounded-lg hover:bg-gray-50 transition-colors"
            >
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <>
          <FieldGrid>
            {/* TEMP: Tracking Follow Up not supported by API */}
            <FieldItem
              label="Tracking Follow Up"
              value={d.trackingFollowUpDate || ""}
            />
            <div>
              <dt className="text-[11px] font-medium text-gray-400 uppercase tracking-wide leading-tight">
                Current Status
              </dt>
              <dd className="mt-1">
                <StatusBadge status={d.status} />
              </dd>
            </div>
            {/* TEMP: Current Medical Status not supported by API */}
            <FieldItem
              label="Current Medical Status"
              value={d.currentMedicalStatus || ""}
            />
            <FieldItem label="Case Type" value={d.caseType || ""} />
            <FieldItem
              label="Date of Incident"
              value={d.dateOfIncident || ""}
            />
            <FieldItem
              label="State of Incident"
              value={d.stateOfIncident || ""}
            />
            <FieldItem label="Lead" value="" />
          </FieldGrid>

          <div className="mt-4 pt-4 border-t border-gray-100">
            <dt className="text-[11px] font-medium text-gray-400 uppercase tracking-wide leading-tight">
              Case Tracking Note
            </dt>
            <dd className="text-sm text-gray-600 mt-1.5 leading-relaxed">
              {d.description || d.notes || ""}
            </dd>
          </div>
        </>
      )}

      {/* Case Flags — API-backed */}
      <div className="mt-4 pt-4 border-t border-gray-100">
        <div className="flex items-center gap-2 mb-3">
          <p className="text-[11px] font-medium text-gray-400 uppercase tracking-wide leading-tight">
            Case Flags
          </p>
        </div>
        <div className="grid grid-cols-3 gap-x-6 gap-y-2.5">
          {flags.map((flag) => (
            <label key={flag.key} className="flex items-center gap-2.5">
              <input
                type="checkbox"
                disabled={flag.key == "minorComp"}
                checked={form[flag.key as keyof CaseDetail] === "Yes"}
                className="w-4 h-4 rounded border-gray-300"
                onChange={(e: ChangeEvent<HTMLInputElement>) => {
                  updateField(
                    flag.key as keyof CaseDetail,
                    e.target.checked ? "Yes" : "No",
                  );
                  onUpdateCaseFlag(
                    flag.key,
                    e.target.checked ? "true" : "false",
                  );
                }}
              />
              <span className="text-sm text-gray-400 select-none">
                {flag.label}
              </span>
            </label>
          ))}
        </div>
      </div>
    </CollapsibleSection>
  );
}
