import type { ColumnDef } from "@tanstack/react-table";
import { BaseTable } from "@/components/ui/base-table";
import type { CaseUpdatesItem } from "@/lib/cases/cases.types";
import { CollapsibleSection } from "../../../components/collapsible-section";
import { NoteCell } from "@/app/(platform)/lien/reports/components/note-cell";

const caseUpdatesColumns: ColumnDef<CaseUpdatesItem, any>[] = [
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

export function UpdatesSection({ u }: { u: CaseUpdatesItem[] }) {
  return (
    <CollapsibleSection title="Updates" icon="ri-history-line">
      <BaseTable
        columns={caseUpdatesColumns}
        data={u ?? []}
        enablePagination={false}
        emptyMessage="No updates found."
      />
      <div className="mt-3 flex items-center justify-between">
        <p className="text-xs text-gray-400">Showing {u?.length} entries</p>
      </div>
    </CollapsibleSection>
  );
}
