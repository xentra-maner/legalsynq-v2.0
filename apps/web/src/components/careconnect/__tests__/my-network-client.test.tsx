import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, test, vi } from 'vitest';
import { MyNetworkClient } from '../my-network-client';
import { careConnectApi } from '@/lib/careconnect-api';
import type { NetworkDetail, NetworkProviderItem, ProviderSearchResult, SpecialtyOption } from '@/types/careconnect';

vi.mock('next/dynamic', () => ({
  default: () => {
    function MockDynamicComponent() {
      return <div data-testid="my-network-map" />;
    }
    return MockDynamicComponent;
  },
}));

vi.mock('@/lib/careconnect-api', () => ({
  careConnectApi: {
    networks: {
      create: vi.fn(),
      getMarkers: vi.fn(),
      searchProviders: vi.fn(),
      addProvider: vi.fn(),
      updateProvider: vi.fn(),
      removeProvider: vi.fn(),
    },
  },
}));

const SPECIALTIES: SpecialtyOption[] = [
  {
    id: 'specialty-1',
    name: 'Physical Therapy',
    code: 'PHYSICAL_THERAPY',
    description: null,
    isActive: true,
  },
  {
    id: 'specialty-2',
    name: 'Chiropractors',
    code: 'CHIROPRACTORS',
    description: null,
    isActive: true,
  },
];

const MULTI_SPECIALTIES: SpecialtyOption[] = [
  SPECIALTIES[0],
  SPECIALTIES[1],
  {
    id: 'specialty-3',
    name: 'Pain',
    code: 'PAIN',
    description: null,
    isActive: true,
  },
  {
    id: 'specialty-4',
    name: 'Spine',
    code: 'SPINE',
    description: null,
    isActive: true,
  },
];

const BASE_PROVIDER: NetworkProviderItem = {
  id: 'network-provider-1',
  networkProviderId: 'network-provider-1',
  providerId: 'provider-1',
  facilityId: 'facility-1',
  name: 'Atlas Rehab',
  title: null,
  organizationName: 'Atlas Health',
  facilityName: 'Atlas Health',
  email: 'atlas@example.com',
  phone: '5551234567',
  addressLine1: '123 Main St',
  city: 'Austin',
  state: 'TX',
  postalCode: '78701',
  isActive: true,
  acceptingReferrals: true,
  facilityIsActive: true,
  visibility: 'Private',
  accessStage: 'PUBLIC',
  specialties: [],
  primarySpecialtyId: null,
  primarySpecialty: null,
  distanceMiles: null,
  isMobile: false,
};

const BASE_SEARCH_RESULT: ProviderSearchResult = {
  id: 'provider-existing',
  facilityId: 'facility-existing',
  facilityName: 'Smith Family Practice',
  name: 'Dr. Jane Smith',
  title: 'Dr.',
  organizationName: 'Smith Family Practice',
  email: 'jane@example.com',
  phone: '5551234567',
  addressLine1: '123 Main St',
  city: 'Chicago',
  state: 'IL',
  postalCode: '60601',
  npi: '1234567890',
  isActive: true,
  acceptingReferrals: true,
  accessStage: 'PUBLIC',
  specialties: [SPECIALTIES[0]],
  primarySpecialtyId: SPECIALTIES[0].id,
  primarySpecialty: SPECIALTIES[0].name,
  distanceMiles: null,
};

function makeNetwork(providers: NetworkProviderItem[] = []): NetworkDetail {
  return {
    id: 'network-1',
    name: 'Preferred Providers',
    description: 'Demo network',
    providers,
    createdAtUtc: '2026-01-01T00:00:00Z',
    updatedAtUtc: '2026-01-01T00:00:00Z',
  };
}

function ok<T>(data: T) {
  return { data, status: 200, correlationId: 'test-correlation' } as const;
}

function labelByText(label: string, container: HTMLElement = document.body): HTMLElement {
  return within(container).getByText(
    (_content, element) => element?.tagName.toLowerCase() === 'label' && element.textContent?.trim() === label,
  );
}

function inputFor(label: string): HTMLInputElement {
  const labelNode = labelByText(label);
  const input = labelNode.parentElement?.querySelector('input');
  if (!input) throw new Error(`Input not found for ${label}`);
  return input as HTMLInputElement;
}

describe('MyNetworkClient', () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  test('renders every assigned specialty in the provider list', () => {
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([{
          ...BASE_PROVIDER,
          specialties: MULTI_SPECIALTIES,
          primarySpecialtyId: MULTI_SPECIALTIES[0].id,
          primarySpecialty: MULTI_SPECIALTIES[0].name,
        }])}
        fetchError={null}
        specialtyOptions={MULTI_SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    expect(screen.getByText('Physical Therapy')).toBeInTheDocument();
    expect(screen.getByText('Chiropractors')).toBeInTheDocument();
    expect(screen.getByText('Pain')).toBeInTheDocument();
    expect(screen.getByText('Spine')).toBeInTheDocument();
    expect(screen.queryByText(/\+\d+/)).not.toBeInTheDocument();
  });

  test('renders created by law firm only in the edit provider modal', async () => {
    const user = userEvent.setup();
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([
          { ...BASE_PROVIDER, createdByLawFirm: 'Acme Law Group' },
          { ...BASE_PROVIDER, id: 'network-provider-2', networkProviderId: 'network-provider-2', providerId: 'provider-2', facilityId: 'facility-2', createdByLawFirm: null },
        ])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
        tenantName="Test Tenant"
        canManageAll
        canManageVisibility
        canAddProviders
      />,
    );

    expect(screen.queryByText('Created By Law Firm')).not.toBeInTheDocument();
    expect(screen.queryByText('Acme Law Group')).not.toBeInTheDocument();

    await user.click(screen.getAllByTitle('Edit provider')[0]);

    expect(screen.getByText('Created By Law Firm')).toBeInTheDocument();
    expect(screen.getByText('Acme Law Group')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Close dialog' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await user.click(screen.getAllByTitle('Edit provider')[1]);

    expect(screen.getByText('Created By Law Firm')).toBeInTheDocument();
    expect(screen.getByText('N/A')).toBeInTheDocument();
  });

  test('requires a specialty before creating a provider and submits selected specialty codes', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.addProvider).mockResolvedValue(ok({
      ...BASE_PROVIDER,
      id: 'network-provider-new',
      networkProviderId: 'network-provider-new',
      providerId: 'provider-new',
      facilityId: 'facility-new',
      name: 'Jane Smith',
      specialties: [SPECIALTIES[0]],
      primarySpecialtyId: SPECIALTIES[0].id,
      primarySpecialty: SPECIALTIES[0].name,
    }));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork()}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByRole('button', { name: /Add Provider/i }));
    await user.click(screen.getByRole('button', { name: /Not found\? Add new instead/i }));

    await user.type(inputFor('Title'), 'Dr.');
    await user.type(screen.getByPlaceholderText('Jane'), 'Jane');
    await user.type(screen.getByPlaceholderText('Smith'), 'Smith');
    await user.type(screen.getByPlaceholderText('jane@example.com'), 'jane@example.com');
    await user.type(screen.getByPlaceholderText('(555) 555-5555'), '5555555555');
    await user.type(screen.getByPlaceholderText('123 Main St'), '123 Main St');
    await user.type(inputFor('City *'), 'Austin');
    await user.type(screen.getByPlaceholderText('IL'), 'TX');
    await user.type(screen.getByPlaceholderText('60601'), '78701');

    await user.click(screen.getByRole('button', { name: /Add to Registry & My Network/i }));

    expect(await screen.findAllByText('Select at least one specialty.')).not.toHaveLength(0);
    expect(careConnectApi.networks.addProvider).not.toHaveBeenCalled();

    await user.click(screen.getByRole('checkbox', { name: 'Physical Therapy' }));
    await user.click(screen.getByRole('button', { name: /Add to Registry & My Network/i }));

    await waitFor(() => expect(careConnectApi.networks.addProvider).toHaveBeenCalledTimes(1));
    expect(careConnectApi.networks.addProvider).toHaveBeenCalledWith(
      'network-1',
      expect.objectContaining({
        newProvider: expect.objectContaining({
          title: 'Dr.',
          firstName: 'Jane',
          lastName: 'Smith',
          specialtyCodes: ['PHYSICAL_THERAPY'],
          primarySpecialtyCode: 'PHYSICAL_THERAPY',
        }),
      }),
    );
  });

  test('adds a new location for an existing provider through the explicit search flow', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.searchProviders).mockResolvedValue(ok([BASE_SEARCH_RESULT]));
    vi.mocked(careConnectApi.networks.addProvider).mockResolvedValue(ok({
      ...BASE_PROVIDER,
      id: 'network-provider-location',
      networkProviderId: 'network-provider-location',
      providerId: BASE_SEARCH_RESULT.id,
      facilityId: 'facility-north',
      name: BASE_SEARCH_RESULT.name,
      title: BASE_SEARCH_RESULT.title,
      organizationName: BASE_SEARCH_RESULT.organizationName,
      facilityName: 'Smith Family Practice',
      email: BASE_SEARCH_RESULT.email,
      phone: BASE_SEARCH_RESULT.phone,
      addressLine1: '456 Oak Ave',
      city: 'Naperville',
      state: 'IL',
      postalCode: '60540',
      specialties: BASE_SEARCH_RESULT.specialties,
      primarySpecialtyId: BASE_SEARCH_RESULT.primarySpecialtyId,
      primarySpecialty: BASE_SEARCH_RESULT.primarySpecialty,
    }));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork()}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByRole('button', { name: /Add Provider/i }));
    await user.type(inputFor('Name or organization'), 'Jane Smith');
    await user.click(screen.getByRole('button', { name: /Search Registry/i }));

    expect(await screen.findByText('Dr. Jane Smith')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Add new location/i }));

    expect(screen.queryByText('Location / Facility name *')).not.toBeInTheDocument();
    const locationForm = screen.getByRole('button', { name: /Add Location to My Network/i }).closest('form') as HTMLFormElement;
    // Organization/Practice, Email, and Phone come from the existing provider record and are display-only here.
    expect(within(locationForm).queryByRole('textbox', { name: /Organization \/ Practice/i })).not.toBeInTheDocument();
    expect(within(locationForm).getByText('Smith Family Practice')).toBeInTheDocument();
    expect(within(locationForm).getByText(BASE_SEARCH_RESULT.email)).toBeInTheDocument();
    expect(within(locationForm).getByText('(555) 123-4567')).toBeInTheDocument();

    await user.type(screen.getByPlaceholderText('123 Main St'), '456 Oak Ave');
    await user.type(inputFor('City *'), 'Naperville');
    await user.type(screen.getByPlaceholderText('IL'), 'IL');
    await user.type(screen.getByPlaceholderText('60601'), '60540');
    await user.click(screen.getByRole('button', { name: /Add Location to My Network/i }));

    await waitFor(() => expect(careConnectApi.networks.addProvider).toHaveBeenCalledTimes(1));
    expect(careConnectApi.networks.addProvider).toHaveBeenCalledWith(
      'network-1',
      expect.objectContaining({
        existingProviderId: BASE_SEARCH_RESULT.id,
        newProvider: expect.objectContaining({
          organizationName: 'Smith Family Practice',
          email: BASE_SEARCH_RESULT.email,
          phone: BASE_SEARCH_RESULT.phone,
          addressLine1: '456 Oak Ave',
          city: 'Naperville',
          state: 'IL',
          postalCode: '60540',
        }),
      }),
    );
    const request = vi.mocked(careConnectApi.networks.addProvider).mock.calls[0]?.[1];
    expect(request).toBeDefined();
    expect(request?.existingFacilityId).toBeUndefined();
    expect(request?.newProvider?.specialtyCodes).toBeUndefined();
    expect(request?.newProvider?.npi).toBeUndefined();
  });

  test('shows every facility returned by provider registry search', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.searchProviders).mockResolvedValue(ok([
      {
        ...BASE_SEARCH_RESULT,
        facilityId: 'facility-greenland',
        facilityName: 'JD Clinic4',
        addressLine1: '120 Green Street',
        city: 'Greenland',
        state: 'AR',
        postalCode: '72701',
      },
      {
        ...BASE_SEARCH_RESULT,
        facilityId: 'facility-san-francisco',
        facilityName: 'JD Clinic4 - Bay',
        addressLine1: '120 Market Street',
        city: 'San Francisco',
        state: 'CA',
        postalCode: '94111',
      },
    ]));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork()}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByRole('button', { name: /Add Provider/i }));
    await user.type(inputFor('NPI number'), '5245147573');
    await user.click(screen.getByRole('button', { name: /Search Registry/i }));

    expect(await screen.findByText('JD Clinic4')).toBeInTheDocument();
    expect(screen.getByText('JD Clinic4 - Bay')).toBeInTheDocument();
    expect(screen.getByText(/120 Green Street Greenland, AR 72701/)).toBeInTheDocument();
    expect(screen.getByText(/120 Market Street San Francisco, CA 94111/)).toBeInTheDocument();
  });

  test('requires a specialty before saving provider setup and submits grouped setup fields', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.updateProvider).mockResolvedValue(ok({
      ...BASE_PROVIDER,
      specialties: [SPECIALTIES[1]],
      primarySpecialtyId: SPECIALTIES[1].id,
      primarySpecialty: SPECIALTIES[1].name,
    }));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([{ ...BASE_PROVIDER, name: 'Dr. Atlas Rehab', title: 'Dr.' }])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByTitle('Edit provider'));

    expect(inputFor('Title')).toHaveValue('Dr.');
    expect(inputFor('Email *')).toHaveValue('atlas@example.com');
    expect(inputFor('Phone *')).toHaveValue('(555) 123-4567');
    expect(screen.getByRole('button', { name: /Save Provider Setup/i })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Save Provider Setup/i }));

    expect(await screen.findAllByText('Select at least one specialty.')).not.toHaveLength(0);
    expect(careConnectApi.networks.updateProvider).not.toHaveBeenCalled();

    await user.click(screen.getByRole('checkbox', { name: 'Chiropractors' }));
    await user.click(screen.getByRole('button', { name: /Save Provider Setup/i }));

    await waitFor(() => expect(careConnectApi.networks.updateProvider).toHaveBeenCalledTimes(1));
    expect(careConnectApi.networks.updateProvider).toHaveBeenCalledWith(
      'network-1',
      'network-provider-1',
      expect.objectContaining({
        title: 'Dr.',
        organizationName: 'Atlas Health',
        email: 'atlas@example.com',
        phone: '5551234567',
        specialtyIds: ['specialty-2'],
      }),
    );
    expect(vi.mocked(careConnectApi.networks.updateProvider).mock.calls[0]?.[2].facilityName).toBeUndefined();
  });

  test('shows all provider locations in edit mode and soft deletes one location', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.removeProvider).mockResolvedValue(ok(undefined));

    const north = {
      ...BASE_PROVIDER,
      id: 'network-provider-1',
      networkProviderId: 'network-provider-1',
      providerId: 'provider-shared',
      facilityId: 'facility-north',
      facilityName: 'Atlas Health - North',
      city: 'Austin',
      specialties: [SPECIALTIES[0]],
      primarySpecialtyId: SPECIALTIES[0].id,
      primarySpecialty: SPECIALTIES[0].name,
    };
    const south = {
      ...BASE_PROVIDER,
      id: 'network-provider-2',
      networkProviderId: 'network-provider-2',
      providerId: 'provider-shared',
      facilityId: 'facility-south',
      facilityName: 'Atlas Health - South',
      city: 'Dallas',
      specialties: [SPECIALTIES[0]],
      primarySpecialtyId: SPECIALTIES[0].id,
      primarySpecialty: SPECIALTIES[0].name,
    };
    vi.mocked(careConnectApi.networks.updateProvider).mockImplementation(async (_networkId, entryId, request) => ok({
      ...(entryId === 'network-provider-2' ? south : north),
      title: request.title,
      organizationName: request.organizationName ?? undefined,
      email: request.email,
      phone: request.phone,
      specialties: [SPECIALTIES[0]],
      primarySpecialtyId: SPECIALTIES[0].id,
      primarySpecialty: SPECIALTIES[0].name,
    }));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([north, south])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getAllByTitle('Edit provider')[0]);

    expect(screen.queryByText('Location / Facility name *')).not.toBeInTheDocument();
    expect(labelByText('Email *')).toBeInTheDocument();
    expect(labelByText('Phone *')).toBeInTheDocument();
    expect(screen.getByText('2 locations')).toBeInTheDocument();

    await user.clear(inputFor('Email *'));
    await user.type(inputFor('Email *'), 'setup@example.com');
    await user.clear(inputFor('Phone *'));
    await user.type(inputFor('Phone *'), '5559876543');
    await user.click(screen.getByRole('button', { name: /Save Provider Setup/i }));

    await waitFor(() => expect(careConnectApi.networks.updateProvider).toHaveBeenCalledTimes(2));
    expect(careConnectApi.networks.updateProvider).toHaveBeenNthCalledWith(
      1,
      'network-1',
      'network-provider-1',
      expect.objectContaining({ email: 'setup@example.com', phone: '5559876543' }),
    );
    expect(careConnectApi.networks.updateProvider).toHaveBeenNthCalledWith(
      2,
      'network-1',
      'network-provider-2',
      expect.objectContaining({ email: 'setup@example.com', phone: '5559876543' }),
    );

    const southForm = screen.getByDisplayValue('Dallas').closest('form');
    expect(southForm).not.toBeNull();
    await user.click(within(southForm as HTMLFormElement).getByRole('button', { name: /Delete location/i }));

    const dialog = await screen.findByRole('dialog', { name: /Delete location\?/i });
    expect(within(dialog).getByText(/Atlas Health - South/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: /Delete location/i }));

    await waitFor(() => expect(careConnectApi.networks.removeProvider).toHaveBeenCalledTimes(1));
    // Delete location cascades the deactivation to the underlying Facility row, unlike
    // the "Remove from network" icon which omits this flag (see the dedicated test below).
    expect(careConnectApi.networks.removeProvider).toHaveBeenCalledWith('network-1', 'network-provider-2', true);
    // Deleted locations disappear from the Facilities panel instead of showing a "Deleted" badge.
    await waitFor(() => expect(screen.queryByDisplayValue('Dallas')).not.toBeInTheDocument());
    expect(screen.getByText('1 location')).toBeInTheDocument();
    // The deleted location's row also disappears from the provider list below, while
    // the sibling location for the same provider stays visible.
    expect(screen.queryByText('Atlas Health - South')).not.toBeInTheDocument();
    expect(screen.getByText('Atlas Health - North')).toBeInTheDocument();
  });

  test('toggling a location Active off (existing feature) keeps its row in the list — only Delete removes it', async () => {
    const user = userEvent.setup();
    const providerWithSpecialty = { ...BASE_PROVIDER, specialties: [SPECIALTIES[0]] };

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([providerWithSpecialty])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    // Toggling the "Active" checkbox and saving is a separate, pre-existing feature from
    // deletion. The backend never flips cc_Facilities.IsActive for this — only
    // RemoveProviderAsync (Delete location) does — so the row must stay visible.
    vi.mocked(careConnectApi.networks.updateProvider).mockResolvedValue(ok({
      ...providerWithSpecialty,
      isActive: false,
      facilityIsActive: true,
    }));

    await user.click(screen.getByTitle('Edit provider'));
    await user.click(screen.getByRole('checkbox', { name: 'Active' }));
    await user.click(screen.getByRole('button', { name: /Save Location/i }));

    await waitFor(() => expect(careConnectApi.networks.updateProvider).toHaveBeenCalledTimes(1));

    // Still present in the main list, just no longer showing as "Active".
    expect(screen.getByText('Atlas Rehab')).toBeInTheDocument();
  });

  test('"Remove from network" icon uses the styled ConfirmDialog, no Facility cascade, updated toast wording', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.removeProvider).mockResolvedValue(ok(undefined));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([BASE_PROVIDER])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByTitle('Remove from network'));

    const dialog = await screen.findByRole('dialog', { name: /Remove from network\?/i });
    expect(within(dialog).getByText(/Atlas Rehab/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: /Remove/i }));

    await waitFor(() => expect(careConnectApi.networks.removeProvider).toHaveBeenCalledTimes(1));
    // No cascadeFacility argument — distinct from Delete location, which passes `true`.
    expect(careConnectApi.networks.removeProvider).toHaveBeenCalledWith('network-1', 'network-provider-1');
    expect(await screen.findByText('Atlas Rehab set to inactive.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  test('submits backend geo point source after selecting a geocoded address', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => [
        {
          displayName: '123 North Usa Drive, Greenland, AR 72701',
          addressLine1: '123 North Usa Drive',
          city: 'Greenland',
          state: 'AR',
          postalCode: '72701',
          latitude: 35.9948,
          longitude: -94.1741,
        },
      ],
    });
    vi.stubGlobal('fetch', fetchMock);
    vi.mocked(careConnectApi.networks.addProvider).mockResolvedValue(ok({
      ...BASE_PROVIDER,
      id: 'network-provider-new',
      networkProviderId: 'network-provider-new',
      providerId: 'provider-new',
      facilityId: 'facility-new',
      name: 'Dr. Test Test',
      specialties: [SPECIALTIES[0]],
      primarySpecialtyId: SPECIALTIES[0].id,
      primarySpecialty: SPECIALTIES[0].name,
    }));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork()}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByRole('button', { name: /Add Provider/i }));
    await user.click(screen.getByRole('button', { name: /Not found\? Add new instead/i }));

    await user.type(inputFor('Title'), 'Dr.');
    await user.type(screen.getByPlaceholderText('Jane'), 'Test');
    await user.type(screen.getByPlaceholderText('Smith'), 'Test');
    await user.type(screen.getByPlaceholderText('jane@example.com'), 'test@example.com');
    await user.type(screen.getByPlaceholderText('(555) 555-5555'), '5123513513');
    await user.type(screen.getByPlaceholderText('123 Main St'), '123 North Usa Drive');
    await user.click(await screen.findByText('123 North Usa Drive, Greenland, AR 72701'));
    await user.click(screen.getByRole('checkbox', { name: 'Physical Therapy' }));
    await user.click(screen.getByRole('button', { name: /Add to Registry & My Network/i }));

    await waitFor(() => expect(careConnectApi.networks.addProvider).toHaveBeenCalledTimes(1));
    expect(careConnectApi.networks.addProvider).toHaveBeenCalledWith(
      'network-1',
      expect.objectContaining({
        newProvider: expect.objectContaining({
          title: 'Dr.',
          firstName: 'Test',
          addressLine1: '123 North Usa Drive',
          city: 'Greenland',
          state: 'AR',
          postalCode: '72701',
          latitude: 35.9948,
          longitude: -94.1741,
          geoPointSource: 'Geocoded',
        }),
      }),
    );
  });

  test('adds a new location from the edit-provider panel and returns to the edit view', async () => {
    const user = userEvent.setup();
    vi.mocked(careConnectApi.networks.addProvider).mockResolvedValue(ok({
      ...BASE_PROVIDER,
      id: 'network-provider-2',
      networkProviderId: 'network-provider-2',
      providerId: BASE_PROVIDER.providerId,
      facilityId: 'facility-south',
      facilityName: 'Atlas Health - South',
      city: 'Dallas',
      state: 'TX',
      addressLine1: '789 South St',
      postalCode: '75201',
    }));

    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([BASE_PROVIDER])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
      tenantName="Test Tenant"
      canManageAll
      canManageVisibility
      canAddProviders
      />,
    );

    await user.click(screen.getByTitle('Edit provider'));
    expect(screen.getByText('1 location')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Add location/i }));

    const newLocationCard = screen.getByText('New location').closest('form') as HTMLFormElement;
    expect(within(newLocationCard).queryByRole('textbox', { name: /Organization \/ Practice/i })).not.toBeInTheDocument();

    const newLocationCity = labelByText('City *', newLocationCard).parentElement?.querySelector('input') as HTMLInputElement;
    await user.type(within(newLocationCard).getByPlaceholderText('123 Main St'), '789 South St');
    await user.type(newLocationCity, 'Dallas');
    await user.type(within(newLocationCard).getByPlaceholderText('IL'), 'TX');
    await user.type(within(newLocationCard).getByPlaceholderText('60601'), '75201');
    await user.click(within(newLocationCard).getByRole('button', { name: /Save Location/i }));

    await waitFor(() => expect(careConnectApi.networks.addProvider).toHaveBeenCalledTimes(1));
    expect(careConnectApi.networks.addProvider).toHaveBeenCalledWith(
      'network-1',
      expect.objectContaining({
        existingProviderId: BASE_PROVIDER.providerId,
        newProvider: expect.objectContaining({
          addressLine1: '789 South St',
          city: 'Dallas',
          state: 'TX',
          postalCode: '75201',
        }),
      }),
    );

    expect(await screen.findByRole('heading', { name: /Edit Provider/i })).toBeInTheDocument();
    expect(screen.getByText('2 locations')).toBeInTheDocument();
    expect(screen.queryByText('New location')).not.toBeInTheDocument();
  });

  test('LSV3-1084: shows "{tenantName} Preferred Providers" heading and no description', () => {
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([BASE_PROVIDER])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
        tenantName="Acme Law"
        canManageAll
        canManageVisibility
        canAddProviders
      />,
    );

    expect(screen.getByRole('heading', { name: 'Acme Law Preferred Providers' })).toBeInTheDocument();
    expect(screen.queryByText('Demo network')).not.toBeInTheDocument();
  });

  test('LSV3-1213: supports Network Setup heading override', () => {
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([BASE_PROVIDER])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
        tenantName="Acme Law"
        headerLabel="Network Setup"
        canManageAll={false}
        canManageVisibility={false}
        canAddProviders
      />,
    );

    expect(screen.getByRole('heading', { name: 'Network Setup' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Acme Law Preferred Providers' })).not.toBeInTheDocument();
  });

  test("LSV3-1213: Network Setup can show only the caller organization's providers", () => {
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([
          { ...BASE_PROVIDER, owningOrganizationId: 'my-org' },
          {
            ...BASE_PROVIDER,
            id: 'network-provider-2',
            networkProviderId: 'network-provider-2',
            providerId: 'provider-2',
            facilityId: 'facility-2',
            name: 'Other Network Provider',
            facilityName: 'Other Network Provider',
            owningOrganizationId: 'other-org',
          },
          {
            ...BASE_PROVIDER,
            id: 'network-provider-3',
            networkProviderId: 'network-provider-3',
            providerId: 'provider-3',
            facilityId: 'facility-3',
            name: 'Tenant Network Provider',
            facilityName: 'Tenant Network Provider',
            owningOrganizationId: null,
          },
        ])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
        tenantName="Acme Law"
        headerLabel="Network Setup"
        canManageAll={false}
        canManageVisibility={false}
        canAddProviders
        callerOrgId="my-org"
        showOnlyCallerOrgProviders
      />,
    );

    expect(screen.getByText('Atlas Rehab')).toBeInTheDocument();
    expect(screen.queryByText('Other Network Provider')).not.toBeInTheDocument();
    expect(screen.queryByText('Tenant Network Provider')).not.toBeInTheDocument();
    expect(screen.getAllByText('1 provider')).toHaveLength(2);
  });

  test('LSV3-1084: hides edit/remove actions for providers the caller does not own when canManageAll is false', () => {
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([
          { ...BASE_PROVIDER, owningOrganizationId: 'other-org' },
        ])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
        tenantName="Acme Law"
        canManageAll={false}
        canManageVisibility={false}
        canAddProviders
        callerOrgId="my-org"
      />,
    );

    expect(screen.queryByTitle('Edit provider')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Remove from network')).not.toBeInTheDocument();
    expect(screen.queryByTitle('View only')).not.toBeInTheDocument();
  });

  test('LSV3-1084: shows edit/remove actions for a provider the caller owns even when canManageAll is false', () => {
    render(
      <MyNetworkClient
        initialNetwork={makeNetwork([
          { ...BASE_PROVIDER, owningOrganizationId: 'my-org' },
        ])}
        fetchError={null}
        specialtyOptions={SPECIALTIES}
        tenantName="Acme Law"
        canManageAll={false}
        canManageVisibility={false}
        canAddProviders
        callerOrgId="my-org"
      />,
    );

    expect(screen.getByTitle('Edit provider')).toBeInTheDocument();
    expect(screen.getByTitle('Remove from network')).toBeInTheDocument();
  });
});
