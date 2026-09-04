import type { ColumnDef } from "@tanstack/react-table";
import { BaseTable } from "@/components/ui/base-table";
import type { CaseUpdatesItem } from "@/lib/cases/cases.types";
import { CollapsibleSection } from "../../../components/collapsible-section";
import { NoteCell } from "@/app/(platform)/lien/reports/components/note-cell";

export type CaseLienUpdateRow = CaseUpdatesItem & { lienId?: string };

const liensUpdatesColumns: ColumnDef<CaseLienUpdateRow, any>[] = [
  {
    id: "timestamp",
    header: "Timestamp",
    cell: ({ row }) => (
      <span className="text-xs text-gray-500 whitespace-nowrap">
        {row.original.timestamp}
      </span>
    ),
  },
  {
    id: "lienNumber",
    header: "Lien Id",
    cell: ({ row }) => (
      <span className="text-sm text-gray-600 whitespace-nowrap">
        {row.original.lienCode ?? row.original.lienId}
      </span>
    ),
  },
  {
    id: "action",
    header: "Actions",
    cell: ({ row }) => (
      <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium rounded bg-gray-100 text-gray-600">
        {row.original.action}
      </span>
    ),
  },
  {
    id: "description",
    header: "Description",
    cell: ({ row }) => <NoteCell value={row.original.description}></NoteCell>,
  },
  {
    id: "updatedBy",
    header: "Updated By",
    cell: ({ row }) => (
      <span className="text-sm text-gray-500 whitespace-nowrap">
        {row.original.updatedBy}
      </span>
    ),
  },
];

export function LienUpdatesSection({
  liensUpdates,
  entriesCount,
}: {
  liensUpdates: CaseLienUpdateRow[];
  entriesCount: number;
}) {
  return (
    <CollapsibleSection title="Updates" icon="ri-history-line">
      <BaseTable
        columns={liensUpdatesColumns}
        data={liensUpdates ?? []}
        enablePagination={false}
        emptyMessage="No updates found."
      />
      <div className="mt-3 flex items-center justify-between">
        <p className="text-xs text-gray-400">Showing {entriesCount} entries</p>
      </div>
    </CollapsibleSection>
  );
}
