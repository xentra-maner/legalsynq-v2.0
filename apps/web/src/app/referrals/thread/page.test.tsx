import { describe, expect, test, vi, beforeEach } from 'vitest';

const { redirectMock, threadClientMock, headersMock } = vi.hoisted(() => ({
  redirectMock: vi.fn((url: string) => {
    throw new Error(`REDIRECT:${url}`);
  }),
  threadClientMock: vi.fn(() => null),
  headersMock: vi.fn(async () => new Headers([
    ['host', 'rl-liens1.legalsynq.net'],
    ['x-forwarded-proto', 'https'],
  ])),
}));

vi.mock('next/navigation', () => ({
  redirect: redirectMock,
}));

vi.mock('next/headers', () => ({
  headers: headersMock,
}));

vi.mock('./thread-client', () => ({
  ThreadClient: threadClientMock,
}));

import ReferralThreadPage from './page';

describe('ReferralThreadPage', () => {
  beforeEach(() => {
    redirectMock.mockClear();
    threadClientMock.mockClear();
    headersMock.mockClear();
    vi.unstubAllGlobals();
  });

  test('renders the referral thread for pending providers instead of redirecting to activation', async () => {
    const threadData = {
      referralId: 'ref-123',
      providerHasAccount: false,
      status: 'New',
      clientName: 'Jane Doe',
      service: 'Physical Therapy',
      providerName: 'Demo Provider',
      referralAttribution: { id: 'attr-1', firstName: 'Cam', lastName: 'Perry' },
      createdAtUtc: '2026-06-11T00:00:00Z',
      comments: [],
      attachments: [],
    };

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => threadData,
    }));

    const result = await ReferralThreadPage({ searchParams: Promise.resolve({ token: 'abc123' }) });

    expect(redirectMock).not.toHaveBeenCalled();
    expect(result).toMatchObject({
      type: threadClientMock,
      props: {
        token: 'abc123',
        data: expect.not.objectContaining({
          referralAttribution: expect.anything(),
        }),
      },
    });
  });
});
