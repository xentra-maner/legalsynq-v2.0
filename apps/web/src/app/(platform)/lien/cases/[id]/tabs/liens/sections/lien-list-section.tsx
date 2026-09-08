import type { ColumnDef, SortingState } from "@tanstack/react-table";
import { BaseTable } from "@/components/ui/base-table";
import { Pagination } from "@/components/ui/pagination";
import type { PaginationMeta } from "@/lib/billofsale";
import { CollapsibleSection } from "../../../components/collapsible-section";
import { formatCurrency } from "../../../utils/case-detail-utils";

export function LienListSection<TLien>({
  search,
  onSearchChange,
  filtered,
  paginatedLiens,
  pagination,
  onPageChange,
  columns,
  totalPurchase,
  totalBilling,
  onAddMedicalLien,
  onFilterClick,
  onRowClick,
  activeFilterCount = 0,
  sorting,
  onSortingChange,
  isLoading,
}: {
  search: string;
  onSearchChange: (v: string) => void;
  filtered: TLien[];
  paginatedLiens: TLien[];
  pagination: PaginationMeta;
  onPageChange: (page: number) => void;
  columns: ColumnDef<TLien, any>[];
  totalPurchase: number;
  totalBilling: number;
  onAddMedicalLien: () => void;
  onFilterClick?: () => void;
  onRowClick?: (id: number) => void;
  activeFilterCount?: number;
  sorting: SortingState;
  onSortingChange?: (sorting: any) => void;
  isLoading?: boolean;
}) {
  return (
    <CollapsibleSection title="Liens" icon="ri-stack-line">
      <div className="flex items-center gap-3 mb-4">
        <div className="relative flex-1">
          <i className="ri-search-line absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-sm" />
          <input
            type="text"
            value={search}
            onChange={(e) => onSearchChange(e.target.value)}
            placeholder="Search liens..."
            className="w-full pl-9 pr-3 py-2 text-sm border border-gray-200 rounded-lg bg-gray-50/50 focus:bg-white focus:border-primary/40 focus:ring-1 focus:ring-primary/20 outline-none transition-all"
          />
        </div>
        <button
          className="px-3.5 py-2 text-sm font-medium text-primary bg-primary/5 border border-primary/20 rounded-lg hover:bg-primary/10 transition-colors inline-flex items-center gap-1.5 whitespace-nowrap"
          onClick={onAddMedicalLien}
        >
          <i className="ri-link text-sm" />
          Add Medical Lien
        </button>
        {onFilterClick && (
          <button
            onClick={onFilterClick}
            className="relative flex items-center justify-center h-9 w-9 text-gray-500 border border-gray-200 rounded-lg hover:bg-gray-50 transition-colors"
          >
            <i className="ri-filter-3-line text-base" />
            {activeFilterCount > 0 && (
              <span className="absolute -top-1.5 -right-1.5 inline-flex items-center justify-center h-4 min-w-4 px-1 rounded-full bg-primary text-white text-[10px] font-semibold">
                {activeFilterCount}
              </span>
            )}
          </button>
        )}
      </div>

      {filtered.length === 0 ? (
        <div className="text-center py-8">
          <i className="ri-stack-line text-2xl text-gray-300" />
          <p className="text-sm text-gray-400 mt-2">
            {search
              ? "No liens match your search"
              : "No liens linked to this case"}
          </p>
        </div>
      ) : (
        <>
          <BaseTable
            columns={columns}
            data={paginatedLiens}
            getRowId={(l: any) => l.id}
            onRowClick={(l: any) => l.id && onRowClick?.(l.id)}
            enablePagination={true}
            manualSorting
            onSortingChange={onSortingChange}
            sorting={sorting}
            isLoading={isLoading}
            footerCells={[
              {
                content: `Totals (${filtered.length} lien${filtered.length !== 1 ? "s" : ""})`,
                colSpan: 4,
                className:
                  "text-xs font-semibold text-gray-500 uppercase tracking-wide",
              },
              {
                content: formatCurrency(totalPurchase),
                align: "right",
                className: "text-sm font-semibold text-gray-700 tabular-nums",
              },
              {
                content: formatCurrency(totalBilling),
                align: "right",
                className: "text-sm font-semibold text-gray-700 tabular-nums",
              },
              { content: null, colSpan: 3 },
            ]}
          />
        </>
      )}
    </CollapsibleSection>
  );
}
