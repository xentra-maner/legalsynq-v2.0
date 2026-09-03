import { DocumentType } from "@/app/(platform)/lien/cases/[id]/tabs/documents/types";
import { DropdownOption } from "@/lib/lookup/lookup.types";
import { mergePdfsFromUrls } from "@/lib/pdf-merge.service";
import React, { useState, useEffect } from "react";
import Field from "./field";

interface CaseDocument {
  id: string;
  liensId: string;
  caseId: string;
  url: string;
  created: string;
  filename?: string;
  typeId?: any;
  [key: string]: any;
  mimeType: string;
}

interface MergePdfProps {
  open: boolean;
  documents?: DocumentType[];
  documentTypes?: DropdownOption[];
  selectedDocument: DocumentType | null;
  apiService?: (
    formData: DocumentType[],
    form: {
      fileName: string;
      selectedDocType: string;
    },
  ) => void;
}

export const MergePdf: React.FC<MergePdfProps> = ({
  documents = [],
  documentTypes = [],
  selectedDocument,
  apiService,
}) => {
  const [fileName, setFileName] = useState<string>("");
  const [selectedDocType, setSelectedDocType] = useState<string>();

  const [checks, setChecks] = useState<string[]>([]);
  const [checkedDocuments, setCheckedDocument] = useState<DocumentType[]>([]);
  const [safeDocuments, setSafeDocuments] = useState<DocumentType[]>([]);

  // HTML5 Drag and Drop tracking state
  const [draggedIndex, setDraggedIndex] = useState<number | null>(null);

  useEffect(() => {
    if (!selectedDocument) return;

    const initialChecks = [selectedDocument.id];
    const initialUrls = [selectedDocument];
    const filteredSafeDocs: DocumentType[] = [];

    documents.forEach((data: any) => {
      if (
        (selectedDocument.liensId === data.liensId &&
          data.mimeType === ".pdf") ||
        data.mimeType === ".PDF"
      ) {
        filteredSafeDocs.push(data);
      }
    });

    setChecks(initialChecks);
    setCheckedDocument(initialUrls);
    setSafeDocuments(filteredSafeDocs);
  }, [selectedDocument, documents]);

  useEffect(() => {
    if (checkedDocuments.length > 0) {
      apiService?.(checkedDocuments, {
        fileName: fileName,
        selectedDocType: selectedDocType ?? "",
      });
    }
  }, [checks, fileName, selectedDocType]);

  // HTML5 Drag and Drop handlers (replacing CDK Drag Drop)
  const handleDragStart = (index: number) => {
    setDraggedIndex(index);
  };

  const handleDragOver = (e: React.DragEvent, index: number) => {
    e.preventDefault();
    if (draggedIndex === null || draggedIndex === index) return;

    const updatedDocs = [...safeDocuments];
    const draggedItem = updatedDocs[draggedIndex];
    updatedDocs.splice(draggedIndex, 1);
    updatedDocs.splice(index, 0, draggedItem);

    setDraggedIndex(index);
    setSafeDocuments(updatedDocs);

    // Update checkedDocuments if the dragged item is checked
    if (checks.includes(draggedItem.id)) {
      const updatedCheckedDocs = updatedDocs.filter((doc) =>
        checks.includes(doc.id),
      );
      setCheckedDocument(updatedCheckedDocs);
    }
  };

  const handleDragEnd = () => {
    setDraggedIndex(null);
  };

  const toggle = (
    e: React.ChangeEvent<HTMLInputElement>,
    isAll: boolean,
    d?: DocumentType,
  ) => {
    const stat = e.target.checked;
    if (isAll) {
      if (stat) {
        const allIds: string[] = [];
        const allUrls: DocumentType[] = [];
        safeDocuments.forEach((data: DocumentType) => {
          if (
            data.liensId == selectedDocument?.liensId &&
            data.mimeType === ".pdf"
          ) {
            allIds.push(data.id);
            allUrls.push(data);
          }
        });
        setChecks(allIds);
        setCheckedDocument(allUrls);
      } else {
        setChecks([]);
        setCheckedDocument([]);
      }
    } else {
      if (d) {
        if (stat) {
          setChecks((prev) => [...prev, d.id]);
          setCheckedDocument((prev) => [...prev, d]);
        } else {
          setChecks((prev) => prev.filter((id) => id !== d.id));
          setCheckedDocument((prev) => prev.filter((id) => id.id !== d.id));
        }
      }
    }
  };

  return (
    <>
      <div className="modal-body">
        <div className="container-fluid">
          <div className="row">
            <div className="col-12">
              <span className="font-semibold text-md">
                Set the order of your documents
              </span>
            </div>

            <div className="col-12">
              <span className="text-gray-400 text-sm">
                Review the selected documents below. Drag and reorder them to
                control how they will appear in the final merge file
              </span>
            </div>
          </div>

          <div className="row mt-3">
            <div className="col-12 mb-4">
              <Field
                label="File Name"
                value={fileName}
                onChange={(v: string) => setFileName(v.toString())}
                placeholder="File Name"
                required
              />
            </div>
            <div className="col-12 mb-5">
              <Field
                label="Document Type"
                required
                value={selectedDocType}
                options={documentTypes}
                onChange={(v: string) => setSelectedDocType(v.toString())}
                placeholder="Select document type..."
                type="select"
              />
            </div>

            <div className="bg-white border border-gray-200 rounded-xl overflow-hidden shadow-sm">
              {/* Header */}
              <div className="grid grid-cols-[40px_40px_2fr_1fr_1fr] items-center px-4 py-3 bg-[#eaeff3] text-xs font-semibold text-gray-600 uppercase tracking-wider">
                <div className="flex items-center justify-center">
                  <input
                    type="checkbox"
                    className="w-4 h-4 text-blue-600 border-gray-300 rounded focus:ring-blue-500 cursor-pointer"
                    onChange={(e) => toggle(e, true)}
                    checked={
                      checks.length === safeDocuments.length &&
                      safeDocuments.length > 0
                    }
                  />
                </div>
                <div></div> {/* Empty header for drag handle */}
                <div className="px-2">Name</div>
                <div>Document Type</div>
                <div>Last Update</div>
              </div>

              {/* Body */}
              <div className="divide-y divide-gray-100">
                {safeDocuments.map((d, index) => (
                  <div
                    key={d.id}
                    className="grid grid-cols-[40px_40px_2fr_1fr_1fr] items-center px-4 py-3 bg-white hover:bg-gray-50 transition-colors group"
                    draggable
                    onDragStart={() => handleDragStart(index)}
                    onDragOver={(e) => handleDragOver(e, index)}
                    onDragEnd={handleDragEnd}
                  >
                    {/* Checkbox */}
                    <div className="flex items-center justify-center">
                      <input
                        type="checkbox"
                        className="w-4 h-4 text-blue-600 border-gray-300 rounded focus:ring-blue-500 cursor-pointer"
                        checked={checks.includes(d.id)}
                        onChange={(e) => toggle(e, false, d)}
                      />
                    </div>

                    {/* Drag handle icon */}
                    <div className="flex items-center justify-center text-gray-400 group-hover:text-gray-600 cursor-grab active:cursor-grabbing">
                      <i className="ri-draggable text-lg"></i>
                    </div>

                    {/* Name with file icon badge */}
                    <div className="flex items-center space-x-3 px-2 overflow-hidden">
                      <span className="flex-shrink-0 w-8 h-8 rounded-lg bg-blue-50 text-blue-600 flex items-center justify-center">
                        <i className="ri-file-line text-base"></i>
                      </span>
                      <span className="text-sm font-medium text-gray-800 truncate">
                        {d["filename"]}
                      </span>
                    </div>

                    {/* Type */}
                    <div className="text-sm text-gray-500 truncate">
                      {d.documentType ?? "--"}
                    </div>

                    {/* Date */}
                    <div className="text-sm text-gray-500">{d.created}</div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </div>
    </>
  );
};
