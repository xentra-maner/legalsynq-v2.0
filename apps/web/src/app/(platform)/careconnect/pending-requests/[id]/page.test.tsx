import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, test, vi } from 'vitest';
import type { ReactNode } from 'react';
import PendingReferralRequestDetailPage from './page';
import { careConnectApi } from '@/lib/careconnect-api';
import type { PendingReferralRequest } from '@/types/careconnect';

const pushMock = vi.fn();

vi.mock('next/navigation', () => ({
  useParams: () => ({ id: 'request-1' }),
  useRouter: () => ({ push: pushMock }),
}));

vi.mock('next/link', () => ({
  default: ({ href, children, ...props }: { href: string; children: ReactNode }) => (
    <a href={href} {...props}>{children}</a>
  ),
}));

vi.mock('next/dynamic', () => ({
  default: () => {
    function MockDynamicComponent() {
      return <div data-testid="public-network-map" />;
    }
    return MockDynamicComponent;
  },
}));

vi.mock('@/lib/careconnect-api', () => ({
  careConnectApi: {
    pendingReferralRequests: {
      getById: vi.fn(),
      update: vi.fn(),
      convert: vi.fn(),
      decline: vi.fn(),
      uploadAttachment: vi.fn(),
      getAttachmentSignedUrl: vi.fn(),
    },
    treatmentTypes: {
      list: vi.fn(),
    },
  },
}));

function ok<T>(data: T) {
  return { data, status: 200, correlationId: 'test-correlation' } as const;
}

const REQUEST: PendingReferralRequest = {
  id: 'request-1',
  tenantId: 'tenant-1',
  lawFirmOrganizationId: 'law-firm-1',
  lawFirmName: 'Acme Law',
  referralAttributionId: 'attr-1',
  origin: 'ReferralPortal',
  clientFirstName: '123',
  clientLastName: '123',
  clientDob: '2026-08-12',
  clientPhone: '2113123123',
  clientEmail: '',
  caseNumber: null,
  requestedService: 'General Referral',
  urgency: 'Normal',
  treatmentTypeId: null,
  dateOfAccident: '2099-09-09',
  recommendedProviderId: null,
  recommendedFacilityId: null,
  recommendedProviderName: null,
  recommendedFacilityName: null,
  preferredProviders: [],
  attachments: [],
  notes: '',
  lienCompanyName: '',
  lienCompanyEmail: '',
  status: 'PendingReview',
  convertedReferralId: null,
  convertedAtUtc: null,
  createdAtUtc: '2026-08-26T00:00:00Z',
  updatedAtUtc: '2026-08-26T00:00:00Z',
};

describe('PendingReferralRequestDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(careConnectApi.pendingReferralRequests.getById).mockResolvedValue(ok(REQUEST));
    vi.mocked(careConnectApi.treatmentTypes.list).mockResolvedValue(ok([]));
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/careconnect/public-network/api/public/network')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      throw new Error(`Unhandled fetch in test: ${url}`);
    }));
  });

  test('shows an inline error for a pending request with a future accident date', async () => {
    const user = userEvent.setup();
    render(<PendingReferralRequestDetailPage />);

    await waitFor(() => expect(screen.getByRole('button', { name: /edit/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /edit/i }));

    expect(screen.getByLabelText(/date of accident/i)).toBeInTheDocument();
    expect(screen.getByText('Date of accident cannot be in the future.')).toBeInTheDocument();
  });
});
