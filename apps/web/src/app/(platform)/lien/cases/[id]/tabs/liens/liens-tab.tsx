"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import type { ColumnDef, SortingState } from "@tanstack/react-table";
import {
  CaseLienItemMetadata,
  casesService,
  type CaseDetail,
  type CaseLienItem,
} from "@/lib/cases";
import type { LiensQuery } from "@/lib/liens";
import {
  useCaseLiens,
  useCaseLiensUpdates,
  useDeleteLien,
} from "@/hooks/use-case-liens";
import { StatusBadge } from "@/components/lien/status-badge";
import { LayoutSplit, type PanelMode } from "@/components/lien/layout-split";
import { ConfirmDialog } from "@/components/lien/modal";
import { useLienStore } from "@/stores/lien-store";
import type { PaginationMeta } from "@/lib/billofsale";
import { FeedsSection } from "../../components/feeds-section";
import { formatCurrency } from "../../utils/case-detail-utils";
import { LienListSection } from "./sections/lien-list-section";
import {
  CaseLiensFilter,
  EMPTY_CASE_LIENS_FILTERS,
  countActiveCaseLiensFilters,
  type CaseLiensFilterValues,
} from "./sections/case-liens-filter";
import {
  LienUpdatesSection,
  type CaseLienUpdateRow,
} from "./sections/lien-updates-section";

export function LiensTab({
  caseId,
  liens: liensProp,
  liensPagination,
  caseDetail,
  panelMode,
  onPanelModeChange,
  onAddMedicalLien,
}: {
  caseId: string;
  liens: CaseLienItem[] & CaseLienItemMetadata[];
  liensPagination: PaginationMeta;
  caseDetail: CaseDetail;
  panelMode: PanelMode;
  onPanelModeChange: (m: PanelMode) => void;
  onAddMedicalLien: (m: boolean) => void;
}) {
  const router = useRouter();
  const addToast = useLienStore((s) => s.addToast);
  const [search, setSearch] = useState("");
  const [showFilter, setShowFilter] = useState(false);
  const [filters, setFilters] = useState<CaseLiensFilterValues>(
    EMPTY_CASE_LIENS_FILTERS,
  );
  const activeFilterCount = countActiveCaseLiensFilters(filters);
  const [lienToDelete, setLienToDelete] = useState<{
    id: string;
    lienNumber: string;
  } | null>(null);
  const deleteLien = useDeleteLien(caseId);

  const [pagination, setPagination] = useState<PaginationMeta>(liensPagination);
  const [sorting, setSorting] = useState<SortingState>([]);

  const serverQuery = useMemo<LiensQuery>(
    () => ({
      pageSize: liensPagination.pageSize,
      medicalFacilityIds: filters.medicalFacilityIds,
      lienStatusIds: filters.lienStatusIds,
      purchaseDateFrom: filters.purchaseDateFrom || undefined,
      purchaseDateTo: filters.purchaseDateTo || undefined,
      initialServiceDateFrom: filters.initialServiceDateFrom || undefined,
      initialServiceDateTo: filters.initialServiceDateTo || undefined,
      sortBy: sorting[0]?.id ?? "lienNumber",
      sortDirection: sorting[0]?.desc ? "desc" : "asc",
    }),
    [liensPagination.pageSize, filters, sorting],
  );
  const { data: filteredLiens } = useCaseLiens(caseId, serverQuery, "liens");
  const liensData = (filteredLiens?.items ??
    liensProp) as unknown as (CaseLienItem & CaseLienItemMetadata)[];

  const { data: liensUpdates } = useCaseLiensUpdates(caseId);
  /* TEMP: visual fallback data for UI review only */
  const displayLiens = liensData.map((l) => {
    return {
      ...l,
      facility: l.facility || "",
      facilityName: l.facilityName || "",
      serviceDate: l.serviceDate || "",
      purchaseDate: l.purchaseDate || "",
      purchaseAmount: l.purchaseAmount || 0,
    };
  });

  const filtered = useMemo(() => {
    if (!search.trim()) return displayLiens;

    const q = search.toLowerCase();
    return displayLiens.filter((l) => {
      return (
        l.lienNumber.toLowerCase().includes(q) ||
        l.facilityName.toLowerCase().includes(q) ||
        l.lienType.toLowerCase().includes(q) ||
        l.status.toLowerCase().includes(q)
      );
    });
  }, [displayLiens, search]);

  const paginatedLiens = useMemo(() => {
    const startIndex = (pagination.page - 1) * pagination.pageSize;
    return filtered.slice(startIndex, startIndex + pagination.pageSize);
  }, [filtered, pagination.page, pagination.pageSize]);

  useEffect(() => {
    const totalCount =
      filteredLiens?.pagination.totalCount ?? liensPagination.totalCount;
    const totalPages = Math.max(1, Math.ceil(totalCount / pagination.pageSize));
    const safePage = Math.min(pagination.page, totalPages);
    setPagination((prev) => {
      if (
        prev.totalCount === totalCount &&
        prev.totalPages === totalPages &&
        prev.page === safePage
      ) {
        return prev;
      }

      return { ...prev, totalCount, totalPages, page: safePage };
    });
  }, [filtered.length, pagination.page, pagination.pageSize, filteredLiens]);

  const handleApplyFilter = (next: CaseLiensFilterValues) => {
    setFilters(next);
    setPagination((prev) => ({ ...prev, page: 1 }));
  };

  const handleConfirmDelete = async () => {
    if (!lienToDelete) return;
    try {
      await deleteLien.mutateAsync(lienToDelete.id);
      addToast({
        type: "success",
        title: "Lien Deleted",
        description: `Lien ${lienToDelete.lienNumber} was deleted.`,
      });
      setLienToDelete(null);
    } catch {
      addToast({
        type: "error",
        title: "Delete Failed",
        description: "Could not delete this lien. Please try again.",
      });
    }
  };

  const totalBilling =
    filtered.reduce(
      (sum, l) => sum + Math.round((l.originalAmount ?? 0) * 100),
      0,
    ) / 100;
  const totalPurchase =
    filtered.reduce(
      (sum, l) => sum + Math.round((l.purchaseAmount ?? 0) * 100),
      0,
    ) / 100;

  const lienRowColumns: ColumnDef<(typeof displayLiens)[number], any>[] = [
    {
      id: "lienNumber",
      header: "Lien ID",
      accessorKey: "lienNumber",
      cell: ({ row }) => (
        <span className="text-sm text-gray-600 max-w-40 whitespace-nowrap">
          {row.original.lienNumber}
        </span>
      ),
    },
    {
      id: "facilityName",
      header: "Facility Name",
      accessorKey: "facilityName",

      cell: ({ row }) => (
        <span className="text-sm text-gray-600 truncate max-w-40 block">
          {row.original.facilityName}
        </span>
      ),
    },
    {
      id: "serviceDate",
      header: "Initial Service Date",
      accessorKey: "initialServiceDate",
      cell: ({ row }) => (
        <span className="text-sm text-gray-600  whitespace-nowrap">
          {row.original.serviceDate}
        </span>
      ),
    },
    {
      id: "purchaseDate",
      header: "Purchase Date",
      accessorKey: "purchaseDate",
      cell: ({ row }) => (
        <span className="text-sm text-gray-600  whitespace-nowrap">
          {row.original.purchaseDate}
        </span>
      ),
    },
    {
      id: "purchaseAmount",
      header: "Purchase Amount",
      accessorKey: "purchaseAmount",
      cell: ({ row }) => (
        <span className="text-sm text-gray-600  tabular-nums">
          {formatCurrency(row.original.purchaseAmount)}
        </span>
      ),
    },
    {
      id: "originalAmount",
      header: "Billing Amount",
      accessorKey: "originalAmount",
      cell: ({ row }) => (
        <span className="text-sm text-gray-600  font-medium tabular-nums">
          {formatCurrency(row.original.originalAmount)}
        </span>
      ),
    },
    {
      id: "isServicing",
      header: "Servicing",
      accessorKey: "isServicing",

      cell: ({ row }) => (
        <span
          className={`text-sm font-medium ${row.original.isServicing ? "text-primary" : "text-gray-600"}`}
        >
          {row.original.isServicing ? "Yes" : "No"}
        </span>
      ),
    },
    {
      id: "payment",
      header: "Amount Received",
      accessorKey: "payment",

      cell: ({ row }) => (
        <span className="text-sm text-gray-700 tabular-nums">
          {formatCurrency(row.original.paymentAmount ?? 0)}
        </span>
      ),
    },
    {
      id: "status",
      header: "Lien Status",
      accessorKey: "status",
      cell: ({ row }) => <StatusBadge status={row.original.status} />,
    },
    {
      id: "actions",
      header: "",
      cell: ({ row }) => (
        <button
          onClick={(e) => {
            e.stopPropagation();
            setLienToDelete({
              id: row.original.id,
              lienNumber: row.original.lienNumber,
            });
          }}
          className="w-7 h-7 flex items-center justify-center rounded hover:bg-red-50 text-gray-400 hover:text-red-600 transition-colors"
          title="Delete lien"
        >
          <i className="ri-delete-bin-line text-sm" />
        </button>
      ),
    },
  ];

  const leftContent = (
    <div className="space-y-4">
      <LienListSection
        search={search}
        onSearchChange={(v) => {
          setSearch(v);
          setPagination((prev) => ({ ...prev, page: 1 }));
        }}
        filtered={filtered}
        paginatedLiens={paginatedLiens}
        pagination={pagination}
        onPageChange={(page) => setPagination((p) => ({ ...p, page }))}
        columns={lienRowColumns}
        totalPurchase={totalPurchase}
        totalBilling={totalBilling}
        onAddMedicalLien={() => onAddMedicalLien(true)}
        onFilterClick={() => setShowFilter(true)}
        activeFilterCount={activeFilterCount}
        onRowClick={(id) => router.push(`/lien/cases/${caseId}/liens/${id}`)}
        onSortingChange={(e) => setSorting(e)}
        sorting={sorting}
      />

      <LienUpdatesSection
        liensUpdates={liensUpdates ?? []}
        entriesCount={liensUpdates?.length ?? 0}
      />
    </div>
  );

  const rightContent = (
    <FeedsSection
      caseId={caseDetail.id}
      panelMode={panelMode}
      onPanelModeChange={onPanelModeChange}
    />
  );

  return (
    <>
      <LayoutSplit
        left={leftContent}
        right={rightContent}
        mode={panelMode}
        onModeChange={onPanelModeChange}
        showControls={false}
      />
      <CaseLiensFilter
        open={showFilter}
        onClose={() => setShowFilter(false)}
        value={filters}
        onApplyFilter={handleApplyFilter}
      />
      <ConfirmDialog
        open={!!lienToDelete}
        onClose={() => setLienToDelete(null)}
        onConfirm={handleConfirmDelete}
        title="Delete Lien Confirmation"
        description={
          <>
            Are you sure you want to delete{" "}
            <span className="font-semibold text-primary">
              {lienToDelete?.lienNumber}
            </span>
            ? This action cannot be undone and will permanently remove all
            associated data.
          </>
        }
        confirmLabel="Yes, Delete Lien"
        confirmVariant="danger"
        loading={deleteLien.isPending}
        warningTitle="Warning: Deleting this lien will also remove:"
        warningItems={[
          "All case associations",
          "All uploaded documents",
          "All activity history",
        ]}
      />
    </>
  );
}
