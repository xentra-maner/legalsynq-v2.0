"use client";

import { useEffect, useState } from "react";

import { Modal } from "@/components/ui/modal";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { LawFirmUserSummary } from "@/types/careconnect";

const REFERRER_ROLE = "CARECONNECT_REFERRER";
const REFERRER_ADMIN_ROLE = "CARECONNECT_REFERRER_ADMIN";

const ROLE_LABELS: Record<string, string> = {
  [REFERRER_ROLE]: "Referrer",
  [REFERRER_ADMIN_ROLE]: "Admin",
};

interface LawFirmChangeRoleModalProps {
  user: LawFirmUserSummary | null;
  currentRoleCode: string | null;
  onClose: () => void;
  onSave: (roleCode: string) => Promise<void> | void;
  saving: boolean;
}

export function LawFirmChangeRoleModal({ user, currentRoleCode, onClose, onSave, saving }: LawFirmChangeRoleModalProps) {
  const [roleCode, setRoleCode] = useState(currentRoleCode ?? REFERRER_ROLE);

  useEffect(() => {
    setRoleCode(currentRoleCode ?? REFERRER_ROLE);
  }, [user, currentRoleCode]);

  if (!user) return null;

  const unchanged = roleCode === currentRoleCode;

  return (
    <Modal open={!!user} onClose={onClose} title="Change Role" size="sm">
      <div className="space-y-4">
        <p className="text-sm text-gray-600">
          Update the CareConnect role for <span className="font-medium text-gray-900">{user.firstName} {user.lastName}</span>.
        </p>

        <div>
          <label htmlFor="law-firm-change-role-select" className="mb-1 block text-sm font-medium text-gray-700">
            Role
          </label>
          <Select value={roleCode} onValueChange={setRoleCode} disabled={saving}>
            <SelectTrigger id="law-firm-change-role-select" className="h-10 w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={REFERRER_ROLE}>{ROLE_LABELS[REFERRER_ROLE]}</SelectItem>
              <SelectItem value={REFERRER_ADMIN_ROLE}>{ROLE_LABELS[REFERRER_ADMIN_ROLE]}</SelectItem>
            </SelectContent>
          </Select>
          <p className="mt-1 text-xs text-gray-500">
            Admins can manage this firm&rsquo;s users and providers.
          </p>
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <button
            type="button"
            onClick={onClose}
            disabled={saving}
            className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={() => onSave(roleCode)}
            disabled={saving || unchanged}
            className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:opacity-50"
          >
            {saving ? "Saving…" : "Save"}
          </button>
        </div>
      </div>
    </Modal>
  );
}
