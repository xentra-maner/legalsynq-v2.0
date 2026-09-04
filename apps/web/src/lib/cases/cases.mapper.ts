import { formatLegacyDateOnly } from "../format-date";
import { DocumentTypeResponse } from "../lookup/lookup.types";
import type {
  CaseResponseDto,
  CaseListItem,
  CaseDetail,
  PaginatedResultDto,
  PaginationMeta,
  UpdateCaseRequestDto,
  CreateMedicalLiensResponse,
  MedicalCodeLiensResponse,
  CaseDocuments,
  CaseDocument,
} from "./cases.types";

const CASE_STATUS_LABELS: Record<string, string> = {
  PreDemand: "Pre-Demand",
  DemandSent: "Demand Sent",
  InNegotiation: "In Negotiation",
  CaseSettled: "Case Settled",
  Closed: "Closed",
};

const LEGACY_FEED_TIMESTAMP_PATTERN =
  /^(\d{2})\/(\d{2})\/(\d{4}) (\d{2}):(\d{2}) (AM|PM)$/;

function safeString(val: string | null | undefined): string {
  return val ?? "";
}

export function formatDateField(val: string | null | undefined): string {
  if (!val) return "";
  try {
    return formatLegacyDateOnly(val);
  } catch {
    return val;
  }
}
export const dateConverter = (dateData: string) => {
  if (!dateData) return "";

  return formatLegacyDateOnly(dateData);
};

export const dateConvertertoIso = (dateData: string) => {
  if (!dateData) return "";

  const isoDateOnlyMatch = /^(\d{4})-(\d{2})-(\d{2})$/.exec(dateData.trim());
  if (isoDateOnlyMatch) return isoDateOnlyMatch[0];

  const usDateOnlyMatch = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(dateData.trim());
  if (usDateOnlyMatch) {
    const [, month, day, year] = usDateOnlyMatch;
    return `${year}-${month}-${day}`;
  }

  const d = new Date(dateData);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

export function mapCaseToListItem(dto: CaseResponseDto): CaseListItem {
  return {
    id: dto.id,
    caseId: dto.id,
    caseNumber: dto.caseNumber,
    clientName:
      dto.clientDisplayName ||
      `${dto.clientFirstName} ${dto.clientLastName}`.trim(),
    title: safeString(dto.title || dto.externalReference),
    status: dto.status,
    statusLabel:
      safeString(dto.statusLabel) ||
      CASE_STATUS_LABELS[dto.status] ||
      dto.status,
    lawFirm: safeString((dto as any).lawFirm),
    caseManager: safeString((dto as any).caseManager),
    accidentType: safeString((dto as any).accidentType),
    dateOfIncident: formatDateField(dto.dateOfIncident),
    clientDob: formatDateField(dto.clientDob),
    insuranceCarrier: safeString(dto.insuranceCarrier),
    demandAmount: dto.demandAmount ?? null,
    settlementAmount: dto.settlementAmount ?? null,
    createdAt: formatDateField(dto.createdAtUtc),
    updatedAt: formatDateField(dto.updatedAtUtc),
  };
}

function insertSpaceInCamelCase(str: string): string {
  // Check if the string contains a camelCase or PascalCase pattern (lowercase followed by uppercase)
  const isCamelOrPascal = /[a-z][A-Z]/.test(str);

  if (isCamelOrPascal) {
    // Insert a space before any capital letter preceded by a lowercase letter or digit
    return str.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  }

  return str;
}

export function mapCaseToDetail(dto: CaseResponseDto): CaseDetail {
  return {
    id: dto.id,
    caseNumber: dto.caseNumber,
    externalReference: safeString(dto.externalReference),
    title: safeString(dto.title),
    clientName:
      dto.clientDisplayName ||
      `${dto.clientFirstName} ${dto.clientLastName}`.trim(),
    clientFirstName: dto.clientFirstName,
    clientLastName: dto.clientLastName,
    status: dto.status,
    statusLabel:
      safeString(dto.statusLabel) ||
      CASE_STATUS_LABELS[dto.status] ||
      dto.status,
    dateOfIncident: formatDateField(dto.dateOfIncident),
    clientDob: formatDateField(dto.clientDob),
    clientPhone: safeString(dto.clientPhone),
    clientEmail: safeString(dto.clientEmail),
    clientAddress: safeString(dto.clientAddress),
    clientStreetAddress: safeString(dto.clientStreetAddress),
    clientCity: safeString(dto.clientCity),
    clientState: safeString(dto.clientState),
    clientZipcode: safeString(dto.clientZipcode),
    sex: safeString(dto.sex),
    caseType: insertSpaceInCamelCase(safeString(dto.caseType)),
    currentMedicalStatus: safeString(dto.currentMedicalStatus),
    stateOfIncident: safeString(dto.stateOfIncident),
    trackingFollowUpDate: formatDateField(dto.trackingFollowUpDate),
    trackingFollowUp: safeString(
      dto.trackingFollowUp ?? dto.trackingFollowUpDate,
    ),
    leadId: safeString(dto.leadId),
    lienStatus: safeString(dto.lienStatus),
    shareCase: safeString(dto.shareCase),
    minorComp: safeString(dto.minorComp),
    caseDropped: safeString(dto.caseDropped),
    childSupportLiens: safeString(dto.childSupportLiens),
    isUccFiled: safeString(dto.isUccFiled),
    insuranceCarrier: safeString(dto.insuranceCarrier),
    policyNumber: safeString(dto.policyNumber),
    claimNumber: safeString(dto.claimNumber),
    demandAmount: dto.demandAmount ?? null,
    settlementAmount: dto.settlementAmount ?? null,
    settlementStatus: safeString(dto.settlementStatus),
    settlementStatusId: safeString(dto.settlementStatusId),
    description: safeString(dto.description),
    notes: safeString(dto.notes),
    openedAt: formatDateField(dto.openedAtUtc),
    closedAt: formatDateField(dto.closedAtUtc),
    createdAt: formatDateField(dto.createdAtUtc),
    updatedAt: formatDateField(dto.updatedAtUtc),
    caseManager: safeString(dto.caseManager),
    caseManagerId: safeString(dto.caseManagerId),
    attorneyId: safeString(dto.attorneyId),
    switchedDate: safeString(dto.switchedDate),
    lawFirm: safeString(dto.lawFirm),
    lawFirmId: safeString(dto.lawFirmId),
    accidentType: safeString(dto.accidentType),
  };
}

export function mapDtoToUpdateRequest(
  dto: CaseResponseDto,
): UpdateCaseRequestDto {
  return {
    caseId: dto.id,
    currentStatus: dto.status,
    currentMedicalStatus: dto.currentMedicalStatus ?? "",
    caseType: dto.caseType ?? "",
    stateOfIncident: dto.stateOfIncident ?? "",
    trackingFollowUp: safeString(
      dto.trackingFollowUp ?? dto.trackingFollowUpDate,
    ),
    dateOfLoss: dto.dateOfIncident ?? "",
    leadId: dto.leadId ?? "",
    shareCase: dto.shareCase ?? "",
    minorComp: dto.minorComp ?? "",
    caseDropped: dto.caseDropped ?? "",
    childSupportLiens: dto.childSupportLiens ?? "",
    clientFirstName: dto.clientFirstName,
    clientLastName: dto.clientLastName,
    externalReference: dto.externalReference ?? undefined,
    title: dto.title ?? undefined,
    clientDob: dto.clientDob ?? undefined,
    clientPhone: dto.clientPhone ?? undefined,
    clientEmail: dto.clientEmail ?? undefined,
    clientAddress: dto.clientAddress ?? undefined,
    dateOfIncident: dto.dateOfIncident ?? undefined,
    insuranceCarrier: dto.insuranceCarrier ?? undefined,
    policyNumber: dto.policyNumber ?? undefined,
    claimNumber: dto.claimNumber ?? undefined,
    description: dto.description ?? null,
    notes: dto.notes ?? null,
    status: dto.status,
    demandAmount: dto.demandAmount ?? null,
    settlementAmount: dto.settlementAmount ?? null,
  };
}

export function mapPagination<T>(
  result: PaginatedResultDto<T>,
): PaginationMeta {
  return {
    page: result.page,
    pageSize: result.pageSize,
    totalCount: result.totalCount,
    totalPages: Math.ceil(result.totalCount / Math.max(result.pageSize, 1)),
  };
}

export function mapMedicalInfo(
  result: CreateMedicalLiensResponse,
): CreateMedicalLiensResponse {
  return {
    id: result.id,
    caseId: result.caseId,
    status: result.status,
    purchaseDate: dateConvertertoIso(formatDateField(result.purchaseDate)),
    initialServiceDate: dateConvertertoIso(
      formatDateField(result.initialServiceDate),
    ),
    endServiceDate: result.endServiceDate
      ? dateConvertertoIso(formatDateField(result.endServiceDate))
      : "",
    note: result.note,
    isBulk: (result.isBulk == "Yes" || result.isBulk == "Y").toString(),
    isServicing: (
      result.isServicing == "Yes" || result.isServicing == "Y"
    ).toString(),
    fundingCompany: result.fundingCompany,
    fundingCompanyId: result.fundingCompanyId,
  };
}

export function mapMedicalCodes(result: MedicalCodeLiensResponse[]): {
  codeRows: MedicalCodeLiensResponse[];
} {
  return {
    codeRows: result.map((r) => ({
      ...r,
      billingAmount: +r.billingAmount,
      medicareCost: +r.medicareCost,
      purchaseAmount: +r.purchaseAmount,
    })),
  };
}

function getDocumentTypeById(id: string, docs: DocumentTypeResponse[]) {
  const doc = docs.find((d) => d.id == id);
  const fallback = docs.find(
    (d) => d.code === "Other" || d.name.toLowerCase() === "other",
  );
  return doc?.name ?? fallback?.name ?? "Other";
}

export function mapDocuments(
  result: any,
  cat: DocumentTypeResponse[],
): CaseDocuments {
  console.log(cat);

  let liens: CaseDocument[] = [];
  let cases: CaseDocument[] = [];
  (result.data || []).map((data: CaseDocument) => {
    data.documentType = getDocumentTypeById(
      data.documentTypeId || data.typeId || "",
      cat,
    );
    if (data.liensId) {
      liens.push(data);
    } else {
      cases.push(data);
    }
  });
  return {
    caseDocuments: cases,
    liensDocuments: liens,
  };
}
export function toIsoUtc(created: string): string {
  const match = LEGACY_FEED_TIMESTAMP_PATTERN.exec(created.trim());
  if (!match) return created;
  const [, month, day, year, hour12, minute, meridiem] = match;
  let hour = Number(hour12) % 12;
  if (meridiem === "PM") hour += 12;
  return `${year}-${month}-${day}T${String(hour).padStart(2, "0")}:${minute}:00Z`;
}
