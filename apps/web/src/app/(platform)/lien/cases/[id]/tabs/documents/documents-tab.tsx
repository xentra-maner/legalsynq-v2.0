"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useLienStore } from "@/stores/lien-store";
import { casesService, type CaseDetail } from "@/lib/cases";
import { documentsService } from "@/lib/documents";
import { ApiError } from "@/lib/api-client";
import { LayoutSplit, type PanelMode } from "@/components/lien/layout-split";
import type { DropdownOption } from "@/lib/lookup/lookup.types";
import { FileDropzoneRef } from "@/components/lien/upload-document";
import { FeedsSection } from "../../components/feeds-section";
import { UploadDocumentSection } from "./sections/upload-document-section";
import { CaseDocumentsSection } from "./sections/case-documents-section";
import { LienDocumentsSection } from "./sections/lien-documents-section";
import type { DocumentType } from "./types";
import { ConfirmDialog, Modal } from "@/components/lien/modal";
import { MergeCaseForm } from "@/components/lien/forms/merge-case-form";
import { MergePdf } from "@/components/lien/merge-file";
import { Button } from "@/components/ui/button";
import { mergePdfsFromUrls } from "@/lib/pdf-merge.service";

export function DocumentsTab({
  docTypes,
  caseDetail,
  panelMode,
  lienid,
  onPanelModeChange,
}: {
  docTypes: DropdownOption[];
  caseDetail: CaseDetail;
  panelMode: PanelMode;
  lienid: string;
  onPanelModeChange: (m: PanelMode) => void;
}) {
  const addToast = useLienStore((s) => s.addToast);
  const dropzoneRef = useRef<FileDropzoneRef>(null);

  const [selectedDocType, setSelectedDocType] = useState("");
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [mergeFile, setMergeFile] = useState<{
    document: DocumentType | null;
    isOpen: boolean;
  }>({
    document: null,
    isOpen: false,
  });
  const [selectedMergeFiles, setSelectedMergeFiles] = useState<{
    document: DocumentType[];
    form?: {
      fileName: string;
      selectedDocType: string;
    };
  }>({
    document: [],
    form: {
      fileName: "",
      selectedDocType: "",
    },
  });

  const [caseDocuments, setCaseDocuments] = useState<DocumentType[]>([]);
  const [liensDocuments, setLiensDocuments] = useState<DocumentType[]>([]);
  const [confirmAction, showConfirmAction] = useState<{
    id: string;
    isOpen: boolean;
    type: string;
  }>({ id: "", isOpen: false, type: "" });
  const [submitting, setIsSubmitting] = useState<boolean>(false);

  const uploadCaseDocuments = useCallback(
    async (payload: any) => {
      if (!payload || payload.length == 0) return;
      setIsSubmitting(true);
      try {
        setIsSubmitting(true);
        const docType =
          (selectedDocType != ""
            ? selectedDocType
            : selectedMergeFiles.form?.selectedDocType) || "";
        for (const element of payload) {
          const formData = new FormData();
          formData.append("File", element ?? "");
          formData.append("caseId", caseDetail.id ?? "");
          formData.append("DocName", element.name);
          formData.append("DocDescription", "Legacy Case Document upload");
          formData.append("DocFileTypeId", docType);

          await casesService.uploadCaseDocuments(formData);
          addToast({
            type: "success",
            title: "Document Uploaded",
            description: `Document has been updated.`,
          });
        }

        setSelectedDocType("");
        dropzoneRef?.current?.reset();
      } catch (err) {
        console.log(err instanceof ApiError, { err });
        if (err instanceof ApiError) {
          addToast({
            type: "error",
            title: "Update Failed",
            description: err.message,
          });
        } else {
          addToast({
            type: "error",
            title: "Update Failed",
            description: "An unexpected error occurred",
          });
        }
      } finally {
        setIsSubmitting(false);
      }
    },
    [selectedFiles, submitting, selectedDocType],
  );

  const fetchDocuments = async () => {
    const docs = await casesService.loadDocuments(caseDetail.id);
    setCaseDocuments(docs.caseDocuments);
    setLiensDocuments(docs.liensDocuments);
  };

  async function deleteFileConfimation(fileId: string, type: string) {
    showConfirmAction({ isOpen: true, id: fileId, type: type });
  }
  const deleteFile = useCallback(async () => {
    try {
      if (confirmAction.type == "case")
        await casesService.deleteCaseDocument(confirmAction.id);
      if (confirmAction.type == "liens")
        await casesService.deleteLiensDocument(confirmAction.id);
      addToast({
        type: "success",
        title: "Delete Document",
        description: "Delete Document Successfully",
      });
      showConfirmAction({ id: "", isOpen: false, type: "" });
      fetchDocuments();
    } catch (err) {
      if (err instanceof ApiError) {
        addToast({
          type: "error",
          title: "Delete Failed",
          description: err.message,
        });
      }
    }
  }, [confirmAction]);

  const deleteFiles = useCallback(async (payload: DocumentType[]) => {
    try {
      for (const element of payload) {
        await casesService.deleteCaseDocument(element.id);

        showConfirmAction({ id: "", isOpen: false, type: "" });
        fetchDocuments();
      }
    } catch (err) {
      if (err instanceof ApiError) {
        addToast({
          type: "error",
          title: "Delete Failed",
          description: err.message,
        });
      }
    }
  }, []);

  const mergeFiles = async () => {
    setIsSubmitting(true);

    try {
      let docs = [];
      if (selectedMergeFiles.document.length > 0) {
        for (const element of selectedMergeFiles.document) {
          const documentId = element.url.split("/").filter(Boolean).pop();

          if (!documentId) {
            console.error("Invalid document ID:", element.url);
            addToast({
              type: "error",
              title: "Invalid Document",
              description: `Cannot extract ID from ${element.url}`,
            });
            return;
          }

          const viewUrl = await documentsService.getViewUrl(documentId);
          docs.push(viewUrl);
        }

        const mergePDF = await documentsService.pdfMerge(docs);
        // Decode base64 to Uint8Array
        const binaryString = atob(mergePDF);
        const bytes = new Uint8Array(binaryString.length);
        for (let i = 0; i < binaryString.length; i++) {
          bytes[i] = binaryString.charCodeAt(i);
        }

        const fileName = selectedMergeFiles?.form?.fileName ?? "";
        const file = new File([bytes], `${fileName}.pdf`, {
          type: "application/pdf",
        });
        if (file) {
          await uploadCaseDocuments([file]);
          await deleteFiles(selectedMergeFiles.document);
          setMergeFile({ document: null, isOpen: false });
          setSelectedMergeFiles({
            document: [],
            form: {
              fileName: "",
              selectedDocType: "",
            },
          });
        }
      }
    } catch (err) {
      if (err instanceof ApiError) {
        addToast({
          type: "error",
          title: "Merge Failed",
          description: err.message,
        });
      } else {
        console.log(err);
        addToast({
          type: "error",
          title: "Merge Failed",
          description: "Something went wrong",
        });
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  async function download(url: string) {
    if (!url) return;
    const documentId = url.split("/").filter(Boolean).pop();
    if (!documentId) return;
    try {
      const viewUrl = await documentsService.getViewUrl(documentId);
      window.open(viewUrl, "_blank");
    } catch (err) {
      addToast({
        type: "error",
        title: "Download Failed",
        description:
          err instanceof ApiError
            ? err.message
            : "An unexpected error occurred",
      });
    }
  }

  useEffect(() => {
    fetchDocuments();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [submitting]);

  const leftContent = (
    <div className="space-y-4">
      <UploadDocumentSection
        submitting={submitting}
        docTypes={docTypes}
        selectedDocType={selectedDocType}
        onSelectedDocTypeChange={setSelectedDocType}
        selectedFiles={selectedFiles}
        onFilesChange={setSelectedFiles}
        dropzoneRef={dropzoneRef}
        onAddDocument={() => uploadCaseDocuments(selectedFiles)}
      />

      <CaseDocumentsSection
        caseDocuments={caseDocuments}
        onDownload={download}
        onDelete={(d) => deleteFileConfimation(d, "case")}
        onMerge={(document: any) =>
          setMergeFile({ isOpen: true, document: document })
        }
      />

      <LienDocumentsSection
        onDownload={download}
        onDelete={(d) => deleteFileConfimation(d, "liens")}
        liensDocuments={liensDocuments}
      />

      {confirmAction.isOpen && (
        <ConfirmDialog
          open
          onClose={() => showConfirmAction({ id: "", isOpen: false, type: "" })}
          onConfirm={deleteFile}
          title="Delete Document"
          description={
            <>
              Are you sure you want to delete document? This action cannot be
              undone and will permanently remove the document.
            </>
          }
          confirmLabel="Delete"
          confirmVariant="danger"
        />
      )}

      {mergeFile.isOpen && (
        <Modal
          open={mergeFile.isOpen}
          onClose={() => {
            setMergeFile({ document: null, isOpen: false });
            setSelectedMergeFiles({
              document: [],
              form: {
                fileName: "",
                selectedDocType: "",
              },
            });
          }}
          title="Arrange & Merge Documents"
          size="xl"
          footer={
            <>
              <Button
                variant="secondary"
                onClick={() => {
                  setMergeFile({ document: null, isOpen: false });
                  setSelectedMergeFiles({
                    document: [],
                    form: {
                      fileName: "",
                      selectedDocType: "",
                    },
                  });
                }}
              >
                Maybe Later
              </Button>
              <Button
                variant="primary"
                disabled={
                  selectedMergeFiles?.document?.length <= 1 ||
                  selectedMergeFiles?.form?.selectedDocType == "" ||
                  submitting
                }
                onClick={() => mergeFiles()}
              >
                {submitting ? "Merging Files..." : "Merge Files"}
              </Button>
            </>
          }
        >
          <MergePdf
            open={mergeFile.isOpen}
            documents={caseDocuments}
            documentTypes={docTypes}
            selectedDocument={mergeFile.document}
            apiService={(documents, form) => {
              setSelectedMergeFiles({ document: documents, form: form });
            }}
          />
        </Modal>
      )}
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
    <LayoutSplit
      left={leftContent}
      right={rightContent}
      mode={panelMode}
      onModeChange={onPanelModeChange}
      showControls={false}
    />
  );
}
