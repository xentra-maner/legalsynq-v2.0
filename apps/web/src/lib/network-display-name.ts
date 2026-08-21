/**
 * LSV3-1084: the original tenant-wide network (OwningOrganizationId == null, created
 * before CareConnectReferrerAdmin existed) displays as "{tenantName} Preferred Providers"
 * for every viewer. A network created afterward by a specific organization (law firm or
 * lien company) displays its own real name instead — showing the tenant's name for an
 * org-owned network would misleadingly suggest the tenant admin owns it.
 */
export function networkDisplayName(
  network: { name: string; owningOrganizationId?: string | null },
  tenantName: string,
): string {
  return network.owningOrganizationId ? network.name : `${tenantName} Preferred Providers`;
}
