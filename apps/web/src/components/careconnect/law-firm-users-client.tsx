"use client";

import { useMemo, useState } from "react";

import { careConnectApi } from "@/lib/careconnect-api";
import { ApiError } from "@/lib/api-client";
import { ConfirmDialog } from "@/components/lien/modal";
import { LawFirmInviteUserModal } from "@/components/careconnect/law-firm-invite-user-modal";
import { LawFirmChangeRoleModal } from "@/components/careconnect/law-firm-change-role-modal";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { LawFirmUserSummary } from "@/types/careconnect";

const REFERRER_ROLE = "CARECONNECT_REFERRER";
const REFERRER_ADMIN_ROLE = "CARECONNECT_REFERRER_ADMIN";

const ROLE_LABELS: Record<string, string> = {
  [REFERRER_ROLE]: "Referrer",
  [REFERRER_ADMIN_ROLE]: "Admin",
};

const PAGE_SIZE = 10;
const STATUS_FILTERS = ["Active", "Invited", "Inactive"] as const;
const ROLE_FILTERS = [
  { value: REFERRER_ROLE, label: ROLE_LABELS[REFERRER_ROLE] },
  { value: REFERRER_ADMIN_ROLE, label: ROLE_LABELS[REFERRER_ADMIN_ROLE] },
] as const;

interface LawFirmUsersClientProps {
  initialUsers: LawFirmUserSummary[];
  fetchError: string | null;
}

type PendingAction =
  | { type: "activate"; user: LawFirmUserSummary }
  | { type: "deactivate"; user: LawFirmUserSummary };

/** A user's single effective CareConnect role for display — Admin takes precedence if both are somehow assigned. */
function currentRoleCode(user: LawFirmUserSummary): string | null {
  if (user.roles.some((r) => r.roleCode === REFERRER_ADMIN_ROLE)) return REFERRER_ADMIN_ROLE;
  if (user.roles.some((r) => r.roleCode === REFERRER_ROLE)) return REFERRER_ROLE;
  return null;
}

function StatusBadge({ status }: { status: string }) {
  if (status === "Active") {
    return (
      <span className="inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] font-semibold border bg-green-50 text-green-700 border-green-200">
        <span className="h-1.5 w-1.5 rounded-full bg-green-500" />
        Active
      </span>
    );
  }
  if (status === "Invited") {
    return (
      <span className="inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] font-semibold border bg-amber-50 text-amber-700 border-amber-200">
        <i className="ri-mail-send-line text-[11px]" />
        Invite sent
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] font-semibold border bg-gray-100 text-gray-500 border-gray-200">
      <span className="h-1.5 w-1.5 rounded-full bg-gray-400" />
      Inactive
    </span>
  );
}

export function LawFirmUsersClient({ initialUsers, fetchError }: LawFirmUsersClientProps) {
  const [users, setUsers] = useState<LawFirmUserSummary[]>(initialUsers);
  const [loadError, setLoadError] = useState<string | null>(fetchError);
  const [toast, setToast] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("all");
  const [roleFilter, setRoleFilter] = useState("all");
  const [page, setPage] = useState(1);
  const [inviteOpen, setInviteOpen] = useState(false);
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null);
  const [actionLoading, setActionLoading] = useState(false);
  const [resendingUserId, setResendingUserId] = useState<string | null>(null);
  const [roleModalUser, setRoleModalUser] = useState<LawFirmUserSummary | null>(null);
  const [roleSaving, setRoleSaving] = useState(false);

  const filteredUsers = useMemo(() => {
    const query = search.trim().toLowerCase();

    return users.filter((user) => {
      const roleCode = currentRoleCode(user);
      const matchesSearch =
        !query ||
        `${user.firstName} ${user.lastName}`.toLowerCase().includes(query) ||
        user.email.toLowerCase().includes(query);
      const matchesStatus = statusFilter === "all" || user.status === statusFilter;
      const matchesRole = roleFilter === "all" || roleCode === roleFilter;

      return matchesSearch && matchesStatus && matchesRole;
    });
  }, [users, search, statusFilter, roleFilter]);

  const totalPages = Math.max(1, Math.ceil(filteredUsers.length / PAGE_SIZE));
  const currentPage = Math.min(page, totalPages);
  const pageStart = (currentPage - 1) * PAGE_SIZE;
  const pageUsers = filteredUsers.slice(pageStart, pageStart + PAGE_SIZE);
  const hasFilters = search.trim() !== "" || statusFilter !== "all" || roleFilter !== "all";

  function resetToFirstPage() {
    setPage(1);
  }

  function showToast(msg: string) {
    setToast(msg);
    setTimeout(() => setToast(null), 4000);
  }

  function friendlyError(err: unknown, fallback: string): string {
    if (err instanceof ApiError) {
      if (err.isForbidden) return "You don't have permission to do this.";
      if (err.isConflict) return err.message || "That user is already in this state.";
      return err.message || fallback;
    }
    return fallback;
  }

  async function refresh() {
    try {
      const { data } = await careConnectApi.lawFirmUsers.list();
      setUsers(data);
      setLoadError(null);
    } catch (err) {
      setLoadError(friendlyError(err, "Unable to load your firm's users. Please try again."));
    }
  }

  async function handleInvited() {
    setInviteOpen(false);
    showToast("Invitation sent.");
    await refresh();
  }

  async function confirmPendingAction() {
    if (!pendingAction) return;
    setActionLoading(true);
    try {
      if (pendingAction.type === "activate") {
        await careConnectApi.lawFirmUsers.activate(pendingAction.user.userId);
        showToast(`${pendingAction.user.firstName} ${pendingAction.user.lastName} activated.`);
      } else {
        await careConnectApi.lawFirmUsers.deactivate(pendingAction.user.userId);
        showToast(`${pendingAction.user.firstName} ${pendingAction.user.lastName} deactivated.`);
      }
      setPendingAction(null);
      await refresh();
    } catch (err) {
      showToast(friendlyError(err, "That action could not be completed. Please try again."));
    } finally {
      setActionLoading(false);
    }
  }

  async function handleResendInvite(user: LawFirmUserSummary) {
    setResendingUserId(user.userId);
    try {
      await careConnectApi.lawFirmUsers.resendInvite(user.userId);
      showToast(`Invitation resent to ${user.email}.`);
      await refresh();
    } catch (err) {
      showToast(friendlyError(err, "The invitation could not be resent. Please try again."));
    } finally {
      setResendingUserId(null);
    }
  }

  async function handleRoleSave(user: LawFirmUserSummary, roleCode: string) {
    setRoleSaving(true);
    try {
      await careConnectApi.lawFirmUsers.assignRole(user.userId, roleCode);
      // Revoke any other CareConnect role assignments so the badge reflects a single
      // current role going forward (assign-then-revoke, not atomic, but assign succeeding
      // first means a failed revoke just leaves an extra role rather than none at all).
      const otherAssignments = user.roles.filter((r) => r.roleCode !== roleCode);
      for (const assignment of otherAssignments) {
        await careConnectApi.lawFirmUsers.revokeRole(user.userId, assignment.assignmentId);
      }
      showToast(`Role updated to ${ROLE_LABELS[roleCode] ?? roleCode}.`);
      setRoleModalUser(null);
      await refresh();
    } catch (err) {
      showToast(friendlyError(err, "That role could not be updated. Please try again."));
    } finally {
      setRoleSaving(false);
    }
  }

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-semibold text-gray-900">Firm Users</h1>
          <p className="text-sm text-gray-500">
            View and manage the users in your law firm — invite teammates, control their
            access, and assign CareConnect roles.
          </p>
        </div>
        <button
          type="button"
          onClick={() => setInviteOpen(true)}
          className="inline-flex items-center gap-1.5 rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 transition-colors whitespace-nowrap"
        >
          <i className="ri-user-add-line text-base" />
          Invite User
        </button>
      </div>

      {loadError && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {loadError}
        </div>
      )}

      <div className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-col gap-3 border-b border-gray-100 p-4 lg:flex-row lg:items-center lg:justify-between">
          <div className="relative w-full lg:max-w-md">
            <i className="ri-search-line pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-gray-400" />
            <input
              type="search"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                resetToFirstPage();
              }}
              placeholder="Search by name or email..."
              aria-label="Search firm users"
              className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 outline-none transition-colors placeholder:text-gray-400 focus:border-primary focus:ring-2 focus:ring-primary/10"
            />
          </div>
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:flex lg:items-center">
            <Select
              name="firm-user-status-filter"
              value={statusFilter}
              onValueChange={(value) => {
                setStatusFilter(value);
                resetToFirstPage();
              }}
            >
              <SelectTrigger aria-label="Filter by status" className="h-10 min-w-[140px]">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">All statuses</SelectItem>
              {STATUS_FILTERS.map((status) => (
                <SelectItem key={status} value={status}>
                  {status === "Invited" ? "Invite sent" : status}
                </SelectItem>
              ))}
              </SelectContent>
            </Select>
            <Select
              name="firm-user-role-filter"
              value={roleFilter}
              onValueChange={(value) => {
                setRoleFilter(value);
                resetToFirstPage();
              }}
            >
              <SelectTrigger aria-label="Filter by role" className="h-10 min-w-[120px]">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">All roles</SelectItem>
              {ROLE_FILTERS.map((role) => (
                <SelectItem key={role.value} value={role.value}>
                  {role.label}
                </SelectItem>
              ))}
              </SelectContent>
            </Select>
          </div>
        </div>

        {users.length === 0 && !loadError ? (
          <div className="px-6 py-14 text-center">
            <i className="ri-user-search-line text-3xl text-gray-300 mb-2 block" />
            <p className="text-sm text-gray-400">No users yet. Invite your first teammate to get started.</p>
          </div>
        ) : pageUsers.length === 0 ? (
          <div className="px-6 py-14 text-center">
            <i className="ri-user-search-line text-3xl text-gray-300 mb-2 block" />
            <p className="text-sm font-medium text-gray-500">No users match your filters.</p>
            <p className="mt-1 text-xs text-gray-400">Try clearing your search or selecting a different status or role.</p>
          </div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-gray-100 text-sm">
                <thead>
                  <tr className="bg-gray-50 text-xs font-medium text-gray-500 uppercase tracking-wider">
                    <th className="px-4 py-3 text-left">User</th>
                    <th className="px-4 py-3 text-left">Email</th>
                    <th className="px-4 py-3 text-left">Status</th>
                    <th className="px-4 py-3 text-left">Roles</th>
                    <th className="px-4 py-3 text-right">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {pageUsers.map((user) => {
                    const roleCode = currentRoleCode(user);
                    return (
                      <tr key={user.userId} className="hover:bg-gray-50 transition-colors">
                        <td className="px-4 py-3 whitespace-nowrap">
                          <span className="font-medium text-gray-900">
                            {user.firstName} {user.lastName}
                          </span>
                        </td>
                        <td className="px-4 py-3 whitespace-nowrap text-gray-600">{user.email}</td>
                        <td className="px-4 py-3 whitespace-nowrap">
                          <StatusBadge status={user.status} />
                        </td>
                        <td className="px-4 py-3 whitespace-nowrap">
                          <span className="inline-flex rounded-full border border-gray-200 bg-gray-50 px-2.5 py-0.5 text-xs font-medium text-gray-700">
                            {ROLE_LABELS[roleCode ?? ""] ?? "No role"}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-right">
                          <DropdownMenu>
                            <DropdownMenuTrigger
                              onClick={(e) => e.stopPropagation()}
                              aria-label="User actions"
                              className="p-1.5 rounded-lg hover:bg-gray-100 text-gray-400 hover:text-gray-600 transition-colors outline-none"
                            >
                              <i className="ri-more-2-fill text-base" />
                            </DropdownMenuTrigger>
                            <DropdownMenuContent align="end" className="w-48">
                              <DropdownMenuItem onClick={() => setRoleModalUser(user)}>
                                <i className="ri-shield-user-line text-gray-400" />
                                Change Role
                              </DropdownMenuItem>
                              {user.status === "Invited" && (
                                <DropdownMenuItem
                                  onClick={() => handleResendInvite(user)}
                                  disabled={resendingUserId === user.userId}
                                >
                                  <i className="ri-mail-send-line text-gray-400" />
                                  {resendingUserId === user.userId ? "Resending..." : "Resend Invite"}
                                </DropdownMenuItem>
                              )}
                              <DropdownMenuSeparator />
                              {user.isActive ? (
                                <DropdownMenuItem
                                  onClick={() => setPendingAction({ type: "deactivate", user })}
                                  className="text-red-600 focus:bg-red-50"
                                >
                                  <i className="ri-user-unfollow-line" />
                                  Deactivate
                                </DropdownMenuItem>
                              ) : (
                                <DropdownMenuItem
                                  onClick={() => setPendingAction({ type: "activate", user })}
                                  className="text-green-700 focus:bg-green-50"
                                >
                                  <i className="ri-user-follow-line" />
                                  Activate
                                </DropdownMenuItem>
                              )}
                            </DropdownMenuContent>
                          </DropdownMenu>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>

            <div className="flex flex-col gap-3 border-t border-gray-100 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
              <span className="text-sm text-gray-500">
                Showing {pageStart + 1}-{Math.min(pageStart + PAGE_SIZE, filteredUsers.length)} of{" "}
                {filteredUsers.length.toLocaleString()}
                {hasFilters ? ` filtered from ${users.length.toLocaleString()}` : ""}
              </span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={currentPage <= 1}
                  className="text-sm text-gray-600 hover:text-gray-900 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  Previous
                </button>
                <span className="text-sm text-gray-500">
                  Page {currentPage} of {totalPages}
                </span>
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                  disabled={currentPage >= totalPages}
                  className="text-sm text-gray-600 hover:text-gray-900 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  Next
                </button>
              </div>
            </div>
          </>
        )}
      </div>

      {toast && (
        <div className="fixed bottom-5 right-5 z-50 flex items-center gap-2 rounded-lg bg-gray-900 px-4 py-3 text-sm text-white shadow-lg">
          <i className="ri-checkbox-circle-line text-green-400" />
          {toast}
        </div>
      )}

      <LawFirmInviteUserModal
        open={inviteOpen}
        onClose={() => setInviteOpen(false)}
        onInvited={handleInvited}
      />

      <LawFirmChangeRoleModal
        user={roleModalUser}
        currentRoleCode={roleModalUser ? currentRoleCode(roleModalUser) : null}
        onClose={() => setRoleModalUser(null)}
        onSave={(roleCode) => {
          if (roleModalUser) return handleRoleSave(roleModalUser, roleCode);
        }}
        saving={roleSaving}
      />

      <ConfirmDialog
        open={!!pendingAction}
        onClose={() => setPendingAction(null)}
        onConfirm={confirmPendingAction}
        loading={actionLoading}
        title={pendingAction?.type === "activate" ? "Activate user" : "Deactivate user"}
        description={
          pendingAction?.type === "activate"
            ? `${pendingAction.user.firstName} ${pendingAction.user.lastName} will regain access to CareConnect.`
            : pendingAction
              ? `${pendingAction.user.firstName} ${pendingAction.user.lastName} will lose access to CareConnect immediately.`
              : ""
        }
        confirmLabel={pendingAction?.type === "deactivate" ? "Deactivate User" : "Confirm"}
        confirmVariant={pendingAction?.type === "deactivate" ? "danger" : "primary"}
      />
    </div>
  );
}
