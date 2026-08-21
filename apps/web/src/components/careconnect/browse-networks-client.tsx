'use client';

import { useMemo, useState } from 'react';
import { NetworkCard } from '@/components/careconnect/network-card';
import type { PublicNetworkSummary } from '@/lib/public-network-api';

type TenantNetworkGroup = {
  tenantId:   string;
  tenantCode: string;
  tenantName: string;
  networks:   PublicNetworkSummary[];
};

export function BrowseNetworksClient({ tenantNetworkGroups }: { tenantNetworkGroups: TenantNetworkGroup[] }) {
  const [query, setQuery] = useState('');

  const totalNetworks = useMemo(
    () => tenantNetworkGroups.reduce((sum, g) => sum + g.networks.length, 0),
    [tenantNetworkGroups],
  );

  const filteredGroups = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return tenantNetworkGroups;
    return tenantNetworkGroups
      .map(group => ({
        ...group,
        networks: group.networks.filter(
          n =>
            n.name.toLowerCase().includes(q) ||
            (n.description ?? '').toLowerCase().includes(q) ||
            group.tenantName.toLowerCase().includes(q),
        ),
      }))
      .filter(group => group.networks.length > 0);
  }, [tenantNetworkGroups, query]);

  const showMultipleTenants = tenantNetworkGroups.length > 1;

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">Available Networks</h1>
          <p className="text-sm text-gray-500 mt-0.5">
            Select a network from any CareConnect tenant assigned to this account.
          </p>
        </div>

        <div className="flex items-center gap-3">
          <span className="hidden sm:inline-flex items-center gap-1.5 rounded-full bg-orange-50 px-3 py-1 text-xs font-medium text-orange-700">
            <i className="ri-share-circle-line" />
            {totalNetworks} network{totalNetworks !== 1 ? 's' : ''}
          </span>

          <div className="relative w-full sm:w-64">
            <i className="ri-search-line pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-sm" />
            <input
              type="text"
              value={query}
              onChange={e => setQuery(e.target.value)}
              placeholder="Search networks..."
              className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 placeholder-gray-400 shadow-sm transition focus:border-orange-300 focus:outline-none focus:ring-2 focus:ring-orange-100"
            />
          </div>
        </div>
      </div>

      {filteredGroups.length === 0 ? (
        <div className="flex flex-col items-center justify-center rounded-xl border border-dashed border-gray-200 bg-gray-50/50 py-20 text-center">
          <i className="ri-search-line text-4xl text-gray-300 mb-3" />
          <p className="text-sm font-medium text-gray-500">No networks match &ldquo;{query}&rdquo;</p>
          <button
            type="button"
            onClick={() => setQuery('')}
            className="mt-3 text-xs font-medium text-orange-600 hover:text-orange-700"
          >
            Clear search
          </button>
        </div>
      ) : (
        <div className="space-y-8">
          {filteredGroups.map(group => {
            const tenantLogoUrl = `/api/branding/logo/public?tenantCode=${encodeURIComponent(group.tenantCode)}`;
            return (
              <section key={group.tenantId} className="space-y-3">
                {showMultipleTenants && (
                  <div className="flex items-center gap-2 border-b border-gray-100 pb-2">
                    <h2 className="text-sm font-semibold text-gray-900">{group.tenantName}</h2>
                    <span className="text-xs text-gray-400">{group.tenantCode}</span>
                    <span className="ml-auto text-xs text-gray-400">
                      {group.networks.length} network{group.networks.length !== 1 ? 's' : ''}
                    </span>
                  </div>
                )}

                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
                  {group.networks.map(network => (
                    <NetworkCard
                      key={`${group.tenantId}:${network.id}`}
                      network={network}
                      tenantName={group.tenantName}
                      tenantLogoUrl={tenantLogoUrl}
                      href={`/careconnect/browse-networks/${network.id}?tenantId=${encodeURIComponent(group.tenantId)}`}
                    />
                  ))}
                </div>
              </section>
            );
          })}
        </div>
      )}
    </div>
  );
}
