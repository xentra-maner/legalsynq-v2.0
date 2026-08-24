import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, test, vi } from 'vitest';
import { LawFirmUsersClient } from '../law-firm-users-client';
import { careConnectApi } from '@/lib/careconnect-api';
import { ApiError } from '@/lib/api-client';
import type { LawFirmUserSummary } from '@/types/careconnect';
import { createContext, useContext, useState, type ComponentPropsWithoutRef, type ReactNode } from 'react';

// The real DropdownMenu (@/components/ui/dropdown-menu) is Radix-based, and its
// scroll-lock dependency (react-remove-scroll, pulled in via @radix-ui/react-menu)
// resolves its own nested "react" copy from apps/web's local pnpm store under
// vitest's SSR module runner — a second React dispatcher that crashes with
// "Cannot read properties of null (reading 'useRef')" as soon as the menu opens.
// Stubbing the primitive with an equivalent plain-React implementation sidesteps
// that pnpm-nesting quirk while still exercising the real open/select/close flow.
const DropdownOpenContext = createContext<{ open: boolean; setOpen: (v: boolean) => void } | null>(null);

vi.mock('@/components/ui/dropdown-menu', () => ({
  DropdownMenu: ({ children }: { children: ReactNode }) => {
    const [open, setOpen] = useState(false);
    return <DropdownOpenContext.Provider value={{ open, setOpen }}>{children}</DropdownOpenContext.Provider>;
  },
  DropdownMenuTrigger: ({ children, onClick, ...props }: ComponentPropsWithoutRef<'button'>) => {
    const ctx = useContext(DropdownOpenContext)!;
    return (
      <button
        type="button"
        {...props}
        onClick={(e) => { onClick?.(e); ctx.setOpen(!ctx.open); }}
      >
        {children}
      </button>
    );
  },
  DropdownMenuContent: ({ children, ...props }: ComponentPropsWithoutRef<'div'>) => {
    const ctx = useContext(DropdownOpenContext)!;
    if (!ctx.open) return null;
    return <div role="menu" {...props}>{children}</div>;
  },
  DropdownMenuItem: ({ children, onClick, disabled, ...props }: ComponentPropsWithoutRef<'button'> & { disabled?: boolean }) => {
    const ctx = useContext(DropdownOpenContext)!;
    return (
      <button
        type="button"
        role="menuitem"
        disabled={disabled}
        {...props}
        onClick={(e) => { onClick?.(e); ctx.setOpen(false); }}
      >
        {children}
      </button>
    );
  },
  DropdownMenuSeparator: () => <hr />,
}));

// @/components/ui/select is Radix-based; its Select.Item click relies on
// pointer-capture APIs (hasPointerCapture/setPointerCapture) that jsdom
// doesn't implement, throwing "target.hasPointerCapture is not a function"
// as soon as an item is clicked. Codebase convention (see
// xenia-assistant.test.tsx) is to stub it with a native <select> for tests.
vi.mock('@/components/ui/select', () => ({
  Select: ({ name, value, onValueChange, disabled, children }: {
    name?: string;
    value?: string;
    onValueChange?: (value: string) => void;
    disabled?: boolean;
    children?: ReactNode;
  }) => {
    const ariaLabel =
      name === 'firm-user-status-filter'
        ? 'Filter by status'
        : name === 'firm-user-role-filter'
          ? 'Filter by role'
          : 'Role';

    return (
      <select
        aria-label={ariaLabel}
        value={value}
        disabled={disabled}
        onChange={(e) => onValueChange?.(e.target.value)}
      >
        {children}
      </select>
    );
  },
  SelectTrigger: ({ children }: { children?: ReactNode }) => <>{children}</>,
  SelectValue: () => null,
  SelectContent: ({ children }: { children?: ReactNode }) => <>{children}</>,
  SelectItem: ({ value, children }: { value: string; children?: ReactNode }) => (
    <option value={value}>{children}</option>
  ),
}));

vi.mock('@/lib/careconnect-api', () => ({
  careConnectApi: {
    lawFirmUsers: {
      list: vi.fn(),
      invite: vi.fn(),
      resendInvite: vi.fn(),
      activate: vi.fn(),
      deactivate: vi.fn(),
      assignRole: vi.fn(),
      revokeRole: vi.fn(),
    },
  },
}));

function apiResponse<T>(data: T) {
  return { data, correlationId: 'test-correlation', status: 200 };
}

const REFERRER_USER: LawFirmUserSummary = {
  userId: 'user-1',
  email: 'referrer@example.com',
  firstName: 'Ada',
  lastName: 'Lovelace',
  isActive: true,
  status: 'Active',
  roles: [{ assignmentId: 'assignment-1', roleCode: 'CARECONNECT_REFERRER' }],
};

const INACTIVE_USER: LawFirmUserSummary = {
  userId: 'user-2',
  email: 'inactive@example.com',
  firstName: 'Grace',
  lastName: 'Hopper',
  isActive: false,
  status: 'Inactive',
  roles: [],
};

const INVITED_USER: LawFirmUserSummary = {
  userId: 'user-3',
  email: 'invited@example.com',
  firstName: 'Katherine',
  lastName: 'Johnson',
  isActive: false,
  status: 'Invited',
  roles: [{ assignmentId: 'assignment-3', roleCode: 'CARECONNECT_REFERRER' }],
};

function makeUser(index: number): LawFirmUserSummary {
  return {
    userId: `user-${index}`,
    email: `user${index}@example.com`,
    firstName: `User${index}`,
    lastName: 'Tester',
    isActive: true,
    status: 'Active',
    roles: [{ assignmentId: `assignment-${index}`, roleCode: 'CARECONNECT_REFERRER' }],
  };
}

describe('LawFirmUsersClient', () => {
  beforeEach(() => {
    vi.mocked(careConnectApi.lawFirmUsers.list).mockResolvedValue(
      apiResponse([REFERRER_USER, INACTIVE_USER]),
    );
    vi.mocked(careConnectApi.lawFirmUsers.invite).mockResolvedValue(
      apiResponse({ userId: 'new-user', invitationId: 'inv-1', email: 'new@example.com', isNew: true }),
    );
    vi.mocked(careConnectApi.lawFirmUsers.resendInvite).mockResolvedValue(apiResponse(undefined));
    vi.mocked(careConnectApi.lawFirmUsers.activate).mockResolvedValue(apiResponse(undefined));
    vi.mocked(careConnectApi.lawFirmUsers.deactivate).mockResolvedValue(apiResponse(undefined));
    vi.mocked(careConnectApi.lawFirmUsers.assignRole).mockResolvedValue(
      apiResponse({ assignmentId: 'assignment-2' }),
    );
    vi.mocked(careConnectApi.lawFirmUsers.revokeRole).mockResolvedValue(apiResponse(undefined));
  });

  test('renders the firm\'s users scoped to what the server returned', () => {
    render(<LawFirmUsersClient initialUsers={[REFERRER_USER, INACTIVE_USER]} fetchError={null} />);

    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument();
    expect(screen.getByText('Grace Hopper')).toBeInTheDocument();
    expect(screen.getByText('referrer@example.com')).toBeInTheDocument();
  });

  test('shows the load error when the server fetch failed', () => {
    render(<LawFirmUsersClient initialUsers={[]} fetchError="Unable to load your firm's users." />);

    expect(screen.getByText("Unable to load your firm's users.")).toBeInTheDocument();
  });

  test('invite modal only offers the two allowed CareConnect roles', async () => {
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[]} fetchError={null} />);

    await user.click(screen.getByRole('button', { name: /invite user/i }));

    const dialog = screen.getByRole('dialog');
    const roleSelect = within(dialog).getByLabelText(/role/i) as HTMLSelectElement;
    const options = within(roleSelect).getAllByRole('option').map((o) => (o as HTMLOptionElement).value);
    expect(options).toEqual(['CARECONNECT_REFERRER', 'CARECONNECT_REFERRER_ADMIN']);
  });

  test('inviting a user calls the invite API and refreshes the list', async () => {
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[]} fetchError={null} />);

    await user.click(screen.getByRole('button', { name: /invite user/i }));
    await user.type(screen.getByLabelText(/first name/i), 'New');
    await user.type(screen.getByLabelText(/last name/i), 'Teammate');
    await user.type(screen.getByLabelText(/email/i), 'new@example.com');
    await user.click(screen.getByRole('button', { name: /send invite/i }));

    await waitFor(() => {
      expect(careConnectApi.lawFirmUsers.invite).toHaveBeenCalledWith({
        email: 'new@example.com',
        firstName: 'New',
        lastName: 'Teammate',
        roleCode: 'CARECONNECT_REFERRER',
      });
    });
    await waitFor(() => expect(careConnectApi.lawFirmUsers.list).toHaveBeenCalled());
  });

  test('invited users show invite sent status and can resend the invitation', async () => {
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[INVITED_USER]} fetchError={null} />);

    expect(screen.getAllByText('Invite sent')).toHaveLength(2);

    await user.click(screen.getByRole('button', { name: /user actions/i }));
    await user.click(screen.getByRole('menuitem', { name: /resend invite/i }));

    await waitFor(() => {
      expect(careConnectApi.lawFirmUsers.resendInvite).toHaveBeenCalledWith('user-3');
    });
    await waitFor(() => expect(careConnectApi.lawFirmUsers.list).toHaveBeenCalled());
  });

  test('searches and filters firm users by status and role', async () => {
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[REFERRER_USER, INACTIVE_USER, INVITED_USER]} fetchError={null} />);

    await user.type(screen.getByLabelText(/search firm users/i), 'katherine');

    expect(screen.getByText('Katherine Johnson')).toBeInTheDocument();
    expect(screen.queryByText('Ada Lovelace')).not.toBeInTheDocument();

    await user.clear(screen.getByLabelText(/search firm users/i));
    await user.selectOptions(screen.getByLabelText(/filter by status/i), 'Inactive');

    expect(screen.getByText('Grace Hopper')).toBeInTheDocument();
    expect(screen.queryByText('Ada Lovelace')).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText(/filter by status/i), 'all');
    await user.selectOptions(screen.getByLabelText(/filter by role/i), 'CARECONNECT_REFERRER');

    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument();
    expect(screen.getByText('Katherine Johnson')).toBeInTheDocument();
    expect(screen.queryByText('Grace Hopper')).not.toBeInTheDocument();
  });

  test('paginates the firm users table', async () => {
    const user = userEvent.setup();
    const manyUsers = Array.from({ length: 12 }, (_, index) => makeUser(index + 1));
    render(<LawFirmUsersClient initialUsers={manyUsers} fetchError={null} />);

    expect(screen.getByText('Showing 1-10 of 12')).toBeInTheDocument();
    expect(screen.getByText('User1 Tester')).toBeInTheDocument();
    expect(screen.queryByText('User11 Tester')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(screen.getByText('Showing 11-12 of 12')).toBeInTheDocument();
    expect(screen.getByText('User11 Tester')).toBeInTheDocument();
    expect(screen.queryByText('User1 Tester')).not.toBeInTheDocument();

    await user.type(screen.getByLabelText(/search firm users/i), 'user1');

    expect(screen.getByText('Page 1 of 1')).toBeInTheDocument();
    expect(screen.getByText('User1 Tester')).toBeInTheDocument();
  });

  test('deactivating a user calls the deactivate API after confirmation', async () => {
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[REFERRER_USER]} fetchError={null} />);

    await user.click(screen.getByRole('button', { name: /user actions/i }));
    await user.click(screen.getByRole('menuitem', { name: /deactivate/i }));
    await user.click(screen.getByRole('button', { name: /deactivate user/i }));

    await waitFor(() => {
      expect(careConnectApi.lawFirmUsers.deactivate).toHaveBeenCalledWith('user-1');
    });
  });

  test('changing the role via the Change Role modal assigns the new role and revokes the old one', async () => {
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[REFERRER_USER]} fetchError={null} />);

    await user.click(screen.getByRole('button', { name: /user actions/i }));
    await user.click(screen.getByRole('menuitem', { name: /change role/i }));

    const dialog = screen.getByRole('dialog');
    const roleSelect = within(dialog).getByLabelText(/role/i) as HTMLSelectElement;
    await user.selectOptions(roleSelect, 'CARECONNECT_REFERRER_ADMIN');
    await user.click(within(dialog).getByRole('button', { name: /save/i }));

    await waitFor(() => {
      expect(careConnectApi.lawFirmUsers.assignRole).toHaveBeenCalledWith('user-1', 'CARECONNECT_REFERRER_ADMIN');
    });
    await waitFor(() => {
      expect(careConnectApi.lawFirmUsers.revokeRole).toHaveBeenCalledWith('user-1', 'assignment-1');
    });
  });

  test('a 403 from the API surfaces a friendly message instead of crashing', async () => {
    vi.mocked(careConnectApi.lawFirmUsers.deactivate).mockRejectedValue(
      new ApiError(403, 'Forbidden', 'test-correlation'),
    );
    const user = userEvent.setup();
    render(<LawFirmUsersClient initialUsers={[REFERRER_USER]} fetchError={null} />);

    await user.click(screen.getByRole('button', { name: /user actions/i }));
    await user.click(screen.getByRole('menuitem', { name: /deactivate/i }));
    await user.click(screen.getByRole('button', { name: /deactivate user/i }));

    await waitFor(() => {
      expect(screen.getByText("You don't have permission to do this.")).toBeInTheDocument();
    });
  });
});
