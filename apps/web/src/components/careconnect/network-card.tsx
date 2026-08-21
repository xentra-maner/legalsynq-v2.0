'use client';

import Link    from 'next/link';
import { useState } from 'react';
import type { NetworkSummary } from '@/types/careconnect';
import type { PublicNetworkSummary } from '@/lib/public-network-api';
import { networkDisplayName } from '@/lib/network-display-name';

export function NetworkCard({
  network,
  tenantName,
  tenantLogoUrl,
  href,
}: {
  network:       NetworkSummary | PublicNetworkSummary;
  /** LSV3-1084: tenant display name used for the "{tenantName} Preferred Providers" card label. */
  tenantName:    string;
  tenantLogoUrl: string;
  href?:         string;
}) {
  const [logoFailed, setLogoFailed] = useState(false);

  const displayName = networkDisplayName(network, tenantName);

  const initials = displayName
    .split(' ')
    .slice(0, 2)
    .map(w => w[0] ?? '')
    .join('')
    .toUpperCase();

  const hasProviders = network.providerCount > 0;

  return (
    <Link
      href={href ?? `/careconnect/browse-networks/${network.id}`}
      className="group relative flex flex-col rounded-xl border border-gray-200 bg-white p-5 transition-all duration-150 hover:-translate-y-0.5 hover:border-orange-200 hover:shadow-lg hover:shadow-orange-900/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-orange-400 focus-visible:ring-offset-2"
    >
      <div className="flex items-start justify-between">
        {/* Tenant logo with initials fallback */}
        <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-lg overflow-hidden bg-orange-50 ring-1 ring-orange-100 group-hover:bg-orange-100 transition-colors">
          {!logoFailed ? (
            <img
              src={tenantLogoUrl}
              alt=""
              className="h-full w-full object-contain p-1.5"
              onError={() => setLogoFailed(true)}
            />
          ) : (
            <span className="text-orange-600 text-sm font-bold select-none">
              {initials || <i className="ri-share-circle-line text-xl" />}
            </span>
          )}
        </div>

        <span
          className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium ${
            hasProviders
              ? 'bg-emerald-50 text-emerald-700'
              : 'bg-gray-100 text-gray-500'
          }`}
        >
          <i className="ri-hospital-line text-xs" />
          {network.providerCount}
        </span>
      </div>

      {/* Name */}
      <p className="mt-3.5 text-sm font-semibold text-gray-900 group-hover:text-orange-700 transition-colors line-clamp-2">
        {displayName}
      </p>

      {/* Description — omitted for a tenant-admin-owned network; its description is an
          internal management detail, not something meaningful to show referrers. */}
      {network.owningOrganizationId && (
        <p className="mt-1 text-xs text-gray-500 line-clamp-2 min-h-[2rem]">
          {network.description || 'No description provided.'}
        </p>
      )}

      {/* CTA */}
      <div className="mt-4 flex items-center gap-1 border-t border-gray-100 pt-3 text-xs font-medium text-orange-600 group-hover:text-orange-700">
        View providers & refer
        <i className="ri-arrow-right-line transition-transform group-hover:translate-x-0.5" />
      </div>
    </Link>
  );
}
