"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import { useLienStore } from "@/stores/lien-store";
import { useRoleAccess } from "@/hooks/use-role-access";
import { casesService, type CaseDetail } from "@/lib/cases";
import { ApiError } from "@/lib/api-client";
import { StatusBadge } from "@/components/lien/status-badge";
import { ConfirmDialog, Modal } from "@/components/lien/modal";
import { type PanelMode } from "@/components/lien/layout-split";
import MedicalLienComponent from "@/components/lien/add-medical-lien/add-medical-lien/medical-lien-component";
import { lookupService } from "@/lib/lookup";
import type { DropdownOption } from "@/lib/lookup/lookup.types";
import {
  useCaseDetail,
  useCaseLiens,
  useCasesUpdates,
  useDeleteCase,
  useSettlementPaymentDetails,
} from "@/hooks/use-case-liens";
import { MergeCaseForm } from "@/components/lien/forms/merge-case-form";
import { HeaderMeta } from "./components/header-meta";
import { CaseDetailContextProvider } from "./case-detail-context";
import { documentsService } from "@/lib/documents";
import { SettlementStatusChip } from "@/components/lien/settlement-status-chip";

const TABS = [
  { key: "details", label: "Details" },
  { key: "liens", label: "Liens" },
  { key: "documents", label: "Documents" },
  { key: "servicing", label: "Servicing" },
  { key: "notes", label: "Case Tracking Notes" },
  { key: "taskmanager", label: "Task Manager" },
] as const;

export function CaseDetailShell({
  id,
  children,
}: {
  id: string;
  children: React.ReactNode;
}) {
  const { mutateAsync: deleteCase } = useDeleteCase();
  const { data: caseDetail, isLoading: loading, refetch } = useCaseDetail(id);

  const queryClient = useQueryClient();

  const router = useRouter();
  const pathname = usePathname();
  const addToast = useLienStore((s) => s.addToast);
  const ra = useRoleAccess();

  const [documentTypes, setDocumentTypes] = useState<DropdownOption[]>([]);

  const {
    data: relatedLiensWithMetadata = {
      items: [],
      pagination: { page: 1, pageSize: 20, totalCount: 0, totalPages: 1 },
    },
    dataUpdatedAt: liensUpdatedAt,
    refetch: refetchLiens,
    isFetching: isLiensFetching,
  } = useCaseLiens(id, { pageSize: 20 });
  const relatedLiens = relatedLiensWithMetadata.items;
  const totalCount = relatedLiensWithMetadata?.pagination?.totalCount ?? 0;
  const {
    data: casePayments = [],
    dataUpdatedAt: paymentsUpdatedAt,
    refetch: refetchPayments,
    isFetching: isPaymentsFetching,
  } = useSettlementPaymentDetails(id);
  // const [loading, setLoading] = useState(true);

  const { data: caseUpdates } = useCasesUpdates(id);

  const [error, setError] = useState<string | null>(null);
  const [panelMode, setPanelMode] = useState<PanelMode>("split");
  const [confirmAction, setConfirmAction] = useState<{
    id: string;
    status?: string;
    name: string;
    actionType: "deleteCase";
  } | null>(null);
  const [showMedicalLienModal, setShowMedicalLienModal] = useState(false);
  const [actionOpen, setActionOpen] = useState(false);
  const [showMergeCase, setShowMergeCase] = useState(false);
  const [showPayoffQoute, setShowPayoffQoute] = useState({
    isOpen: false,
    url: "",
  });

  const fetchDocumentTypes = useCallback(async () => {
    // setLoading(true);
    setError(null);
    try {
      const types = await lookupService.getDocumentType();

      setDocumentTypes(
        types.map((t) => {
          return { key: t.id, value: t.id, label: t.name };
        }),
      );
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.isNotFound ? "Document types not found." : err.message);
      } else {
        setError("Failed to load document types");
      }
    } finally {
      // setLoading(false);
    }
  }, [id]);

  useEffect(() => {
    fetchDocumentTypes();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const canEdit = ra.can("case:edit");

  if (loading) {
    return (
      <div className="p-10 text-center">
        <div className="inline-block h-6 w-6 animate-spin rounded-full border-2 border-primary border-t-transparent" />
        <p className="text-sm text-gray-400 mt-2">Loading case details...</p>
      </div>
    );
  }

  if (error || !caseDetail) {
    return (
      <div className="p-10 text-center space-y-3">
        <i className="ri-error-warning-line text-3xl text-gray-300" />
        <p className="text-sm text-gray-500">{error || "Case not found."}</p>
        <Link
          href="/lien/cases"
          className="text-sm text-primary hover:underline"
        >
          Back to Cases
        </Link>
      </div>
    );
  }

  const d = caseDetail;

  const handleDeleteCase = () => {
    setConfirmAction({
      id: caseDetail.id,
      name: caseDetail.caseNumber,
      actionType: "deleteCase",
    });
  };
  const handleMergeCase = () => {
    setTimeout(() => {
      queryClient.invalidateQueries({
        queryKey: ["cases"],
      });
      router.push("/lien/cases");
    }, 1000);
  };

  const generatePayoff = async () => {
    try {
      const response = await casesService.payoffQoute(id);
      const viewUrl = await documentsService.getViewUrl(response.url);
      if (viewUrl) {
        setShowPayoffQoute({ isOpen: true, url: viewUrl });
      } else {
        addToast({
          type: "error",
          title: "Generate Payoff Failed",
          description: response.message,
        });
      }
    } catch (err) {
      console.log(err);
      const message =
        err instanceof ApiError ? err.message : "Failed to generate payoff";
      addToast({
        type: "error",
        title: "Generate Payoff Failed",
        description: message,
      });
      setConfirmAction(null);
    }
  };

  const handleConfirmAction = async () => {
    if (!confirmAction) return;

    try {
      if (confirmAction.actionType === "deleteCase") {
        await deleteCase(confirmAction.id);
        addToast({
          type: "success",
          title: "Case Deleted",
          description: `Case ${confirmAction.id} has been successfully deleted.`,
        });
        setTimeout(() => {
          router.push("/lien/cases");
        }, 500);
      }
      setConfirmAction(null);
    } catch (err) {
      const message =
        err instanceof ApiError ? err.message : "Failed to complete action";
      addToast({ type: "error", title: "Action Failed", description: message });
      setConfirmAction(null);
    }
  };

  return (
    <div className="flex flex-col h-full min-h-0">
      <div className="px-6 pt-3 pb-0 text-xs text-gray-400 flex items-center gap-1">
        <Link
          href="/lien/cases"
          className="hover:text-gray-600 transition-colors"
        >
          Cases
        </Link>
        <i className="ri-arrow-right-s-line text-sm" />
        <span className="text-gray-500">Liens Management</span>
      </div>

      <div className="mx-6 mt-2 bg-white border border-gray-200 rounded-lg">
        <div className="px-6 py-4">
          <div className="flex flex-col md:flex-row align-items-center justify-evenly gap-8 sm:gap-4 py-2 ">
            <div className="min-w-[160px] col-lg-3 col-12 mb-2 ">
              {/* TEMP: UI mock data for visual review only */}
              <h1 className="text-xl font-bold text-gray-900 leading-tight">
                {d.clientName || ""}
              </h1>
              <p className="text-xs text-gray-400 mt-1.5 font-medium">
                {d.caseNumber}
              </p>
              {d.lienStatus == "Closed" && (
                <SettlementStatusChip
                  status={d.settlementStatusId || d.lienStatus}
                  label={
                    d.lienStatus
                      ? `${d.lienStatus}${d.settlementStatus ? `-${d.settlementStatus}` : ""}`
                      : ""
                  }
                />
              )}
            </div>

            <div className="min-w-0 col-lg-9 flex-1">
              <div className="grid grid-cols-2 md:grid-cols-4 gap-x-6 md:gap-x-4 gap-y-2">
                <HeaderMeta
                  label="Case Type"
                  value={d.caseType || "Lien Case"}
                />
                <HeaderMeta label="Case Status">
                  <StatusBadge status={d.status} />
                </HeaderMeta>
                <HeaderMeta
                  label="Date of Loss"
                  value={d.dateOfIncident || ""}
                />
                <HeaderMeta label="Date of Birth" value={d.clientDob || ""} />
                {/* TEMP: UI mock data for visual review only */}
                <HeaderMeta
                  label="State of Incident"
                  value={d.stateOfIncident}
                />
                <HeaderMeta label="Law Firm" value={d.lawFirm || ""} />
                {/* TEMP: UI mock data for visual review only */}
                <HeaderMeta label="Case Manager" value={d.caseManager || ""} />
                {canEdit ? (
                  <div className="flex items-end">
                    <div className="relative">
                      {/* Dropdown Button */}
                      <button
                        onClick={() => setActionOpen(!actionOpen)}
                        className="flex items-center gap-1.5 text-sm font-medium text-white bg-primary hover:bg-primary/90 rounded-lg px-2 py-2 transition-colors"
                      >
                        Actions
                        <i className="ri-arrow-down-s-line text-base" />
                      </button>
                      {/* Dropdown Menu */}
                      {actionOpen && (
                        <div className="absolute right-0 mt-2 w-48 bg-white border border-gray-200 rounded-lg shadow-lg z-50">
                          <button
                            onClick={() => {
                              generatePayoff();
                              setActionOpen(false);
                            }}
                            className="w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                          >
                            Payoff Quote
                          </button>
                          <button
                            onClick={() => {
                              setShowMergeCase(true);
                              setActionOpen(false);
                            }}
                            className="w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                          >
                            Merge Case
                          </button>
                          {/* Filter */}

                          <button
                            onClick={() => {
                              handleDeleteCase();
                              setActionOpen(false);
                            }}
                            className="text-left px-4 py-2 text-sm hover:bg-gray-100 text-red-600"
                          >
                            Delete Case
                          </button>
                        </div>
                      )}
                    </div>
                  </div>
                ) : (
                  <div />
                )}
              </div>
            </div>
          </div>
        </div>

        <div className="border-t border-gray-100 px-6">
          <nav className="flex flex-wrap gap-4 -mb-px">
            {TABS.map((tab) => {
              const href = `/lien/cases/${id}/${tab.key}`;
              const isActive = pathname?.startsWith(href);
              return (
                <Link
                  key={tab.key}
                  href={href}
                  className={[
                    "px-4 py-2.5 text-sm font-medium border-b-2 transition-colors whitespace-nowrap",
                    isActive
                      ? "border-primary text-primary"
                      : "border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300",
                  ].join(" ")}
                >
                  {tab.label}
                  {tab.key === "liens" && (
                    <span className="ml-1.5 inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 text-[10px] font-semibold rounded-full bg-primary/10 text-primary">
                      {totalCount}
                    </span>
                  )}
                </Link>
              );
            })}
          </nav>
        </div>
      </div>

      <div className="flex-1 min-h-0 overflow-auto bg-gray-50 px-6 py-5">
        <CaseDetailContextProvider
          value={{
            id,
            d,
            caseUpdates,
            documentTypes,
            relatedLiens,
            liensPagination: relatedLiensWithMetadata.pagination,
            liensLoadedAt: liensUpdatedAt ? new Date(liensUpdatedAt) : null,
            refetchLiens,
            isLiensFetching,
            casePayments,
            paymentsLoadedAt: paymentsUpdatedAt
              ? new Date(paymentsUpdatedAt)
              : null,
            refetchPayments: async () => {
              await refetchPayments();
              refetchLiens();
            },
            isPaymentsFetching,
            canEdit,
            panelMode,
            setPanelMode,
            openMedicalLienModal: setShowMedicalLienModal,
          }}
        >
          {children}
        </CaseDetailContextProvider>
      </div>

      {confirmAction && (
        <ConfirmDialog
          open
          onClose={() => setConfirmAction(null)}
          onConfirm={handleConfirmAction}
          title={"Delete Case"}
          description={`Are you sure you want to delete case ${confirmAction.name}? This action cannot be undone.`}
          confirmLabel={"Delete"}
        />
      )}

      {showMergeCase && (
        <MergeCaseForm
          open={showMergeCase}
          caseNumber={d.id}
          onClose={() => setShowMergeCase(false)}
          onCreated={handleMergeCase}
        />
      )}

      {showMedicalLienModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 overflow-y-auto">
          <div className="bg-white rounded-lg shadow-lg max-w-2xl w-full mx-4 my-6">
            <MedicalLienComponent
              caseInfo={{ ...caseDetail }}
              caseId={id}
              onClose={() => {
                setShowMedicalLienModal(false);
                queryClient.invalidateQueries({ queryKey: ["case-liens", id] });
                queryClient.invalidateQueries({
                  queryKey: ["case-liens-all", id],
                });
              }}
              onSave={() => {
                queryClient.invalidateQueries({
                  queryKey: ["lien-updates", id],
                });

                queryClient.invalidateQueries({
                  queryKey: ["case-updates", id],
                });
              }}
            />
          </div>
        </div>
      )}
      {showPayoffQoute.isOpen && (
        <Modal
          size="xl"
          open={showPayoffQoute.isOpen}
          title="Payoff Quote"
          onClose={() => setShowPayoffQoute({ isOpen: false, url: "" })}
        >
          <div className="min-h-[75vh]">
            <object
              data={showPayoffQoute.url}
              type="application/pdf"
              width="100%"
              height="100%"
              className="min-h-[75vh]"
            >
              <p>
                It appears your browser does not support PDFs.{" "}
                <a href={showPayoffQoute.url ?? ""}>Download the PDF</a>.
              </p>
            </object>
          </div>
        </Modal>
      )}
    </div>
  );
}
