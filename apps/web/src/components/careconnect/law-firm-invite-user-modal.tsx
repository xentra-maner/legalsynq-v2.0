"use client";

import { useState } from "react";

import { careConnectApi } from "@/lib/careconnect-api";
import { ApiError } from "@/lib/api-client";
import { Modal } from "@/components/ui/modal";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

const REFERRER_ROLE = "CARECONNECT_REFERRER";
const REFERRER_ADMIN_ROLE = "CARECONNECT_REFERRER_ADMIN";

interface LawFirmInviteUserModalProps {
  open: boolean;
  onClose: () => void;
  onInvited: () => void;
}

export function LawFirmInviteUserModal({ open, onClose, onInvited }: LawFirmInviteUserModalProps) {
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [email, setEmail] = useState("");
  const [roleCode, setRoleCode] = useState(REFERRER_ROLE);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function reset() {
    setFirstName("");
    setLastName("");
    setEmail("");
    setRoleCode(REFERRER_ROLE);
    setError(null);
  }

  function handleClose() {
    if (submitting) return;
    reset();
    onClose();
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!firstName.trim() || !lastName.trim() || !email.trim()) {
      setError("First name, last name, and email are required.");
      return;
    }

    setSubmitting(true);
    setError(null);
    try {
      await careConnectApi.lawFirmUsers.invite({
        email: email.trim(),
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        roleCode,
      });
      reset();
      onInvited();
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.isConflict ? "A user with this email already exists." : err.message);
      } else {
        setError("Unable to send the invitation. Please try again.");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal open={open} onClose={handleClose} title="Invite User" size="sm">
      <form id="law-firm-invite-user-form" onSubmit={handleSubmit} className="space-y-4">
        {error && (
          <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="law-firm-invite-first-name" className="mb-1 block text-sm font-medium text-gray-700">
              First name <span className="text-red-500">*</span>
            </label>
            <input
              id="law-firm-invite-first-name"
              type="text"
              placeholder="Enter first name"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-primary focus:outline-none"
              disabled={submitting}
            />
          </div>
          <div>
            <label htmlFor="law-firm-invite-last-name" className="mb-1 block text-sm font-medium text-gray-700">
              Last name <span className="text-red-500">*</span>
            </label>
            <input
              id="law-firm-invite-last-name"
              type="text"
              placeholder="Enter last name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-primary focus:outline-none"
              disabled={submitting}
            />
          </div>
        </div>

        <div>
          <label htmlFor="law-firm-invite-email" className="mb-1 block text-sm font-medium text-gray-700">
            Email <span className="text-red-500">*</span>
          </label>
          <input
            id="law-firm-invite-email"
            type="email"
            placeholder="Enter email address"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-primary focus:outline-none"
            disabled={submitting}
          />
        </div>

        <div>
          <label htmlFor="law-firm-invite-role" className="mb-1 block text-sm font-medium text-gray-700">Role</label>
          <Select value={roleCode} onValueChange={setRoleCode} disabled={submitting}>
            <SelectTrigger id="law-firm-invite-role" className="h-10 w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={REFERRER_ROLE}>Referrer</SelectItem>
              <SelectItem value={REFERRER_ADMIN_ROLE}>Admin</SelectItem>
            </SelectContent>
          </Select>
          <p className="mt-1 text-xs text-gray-500">
            Admins can manage this firm&rsquo;s users and providers.
          </p>
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <button
            type="button"
            onClick={handleClose}
            disabled={submitting}
            className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={submitting}
            className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:opacity-50"
          >
            {submitting ? "Sending…" : "Send Invite"}
          </button>
        </div>
      </form>
    </Modal>
  );
}
