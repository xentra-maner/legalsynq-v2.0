import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, test, vi } from 'vitest';
import ReferralPortalSubmitPage from './page';

vi.mock('next/dynamic', () => ({
  default: () => {
    function MockDynamicComponent() {
      return <div data-testid="public-network-map" />;
    }
    return MockDynamicComponent;
  },
}));

vi.mock('sonner', () => ({
  toast: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

vi.mock('@/components/careconnect/representative-access-code-gate', () => ({
  useRepresentativePortal: () => ({ code: 'access-code' }),
}));

vi.mock('@/lib/representative-portal-api', () => ({
  createPendingReferralRequest: vi.fn(),
  fetchReferralPortalLawFirms: vi.fn().mockResolvedValue({
    data: [{ id: 'law-firm-1', name: 'Acme Law' }],
  }),
  fetchReferralPortalTreatmentTypes: vi.fn().mockResolvedValue({ data: [] }),
  uploadRepresentativePendingRequestAttachment: vi.fn(),
}));

describe('ReferralPortalSubmitPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/public/careconnect/api/public/network')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      throw new Error(`Unhandled fetch in test: ${url}`);
    }));
  });

  test('shows an inline error when date of accident has an invalid year', async () => {
    render(<ReferralPortalSubmitPage />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /submit request/i })).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText(/date of accident/i), {
      target: { value: '1899-12-31' },
    });

    expect(screen.getByText('Please enter a valid year (1900 or later).')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /submit request/i })).toBeDisabled();
  });
});
