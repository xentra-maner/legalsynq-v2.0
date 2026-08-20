'use client';

import { useState, useEffect, type FormEvent } from 'react';
import { useRouter } from 'next/navigation';
import { careConnectApi } from '@/lib/careconnect-api';
import { ApiError } from '@/lib/api-client';
import type { CreateReferralRequest, ReferralAttributionSummary, ReferralUrgencyValue } from '@/types/careconnect';
import { URGENCY_OPTIONS } from '@/types/careconnect';
import { formatPhoneInput, stripPhone, isValidPhone } from '@/lib/phone';
import { isValidIsoDate, hasReasonableYear } from '@/lib/daterange';

interface TreatmentType {
  id:   string;
  name: string;
}

interface CreateReferralFormProps {
  /** Pre-selected provider — set when form opens from the provider detail page */
  providerId:   string;
  providerName: string;
  /** Called when the user cancels or after successful submission */
  onClose: () => void;
  /** LSCC-005: referrer identity forwarded to the backend for email notifications */
  referrerEmail?: string;
  referrerName?:  string;
}

const SERVICE_TYPES = [
  'General Referral',
  'Consultation',
  'Initial Service',
  'Diagnostic Service',
  'Laboratory Service',
  'Imaging/Radiology',
  'Emergency Service',
  'Home Health Service',
  'Specialist Referral',
  'Telehealth Service',
  'Follow-up Service',
];

export function CreateReferralForm({ providerId, providerName, onClose, referrerEmail, referrerName }: CreateReferralFormProps) {
  const router = useRouter();

  // Subject party (client) fields
  const [clientFirstName,  setClientFirstName]  = useState('');
  const [clientLastName,   setClientLastName]   = useState('');
  const [clientDob,        setClientDob]        = useState('');
  const [clientPhone,      setClientPhone]      = useState('');
  const [clientEmail,      setClientEmail]      = useState('');

  // Accident / case details
  const [dateOfAccident,  setDateOfAccident]  = useState('');

  // Referral details
  const [caseNumber,      setCaseNumber]      = useState('');
  const [urgency,         setUrgency]         = useState<ReferralUrgencyValue>('Normal');
  const [serviceType,     setServiceType]     = useState('General Referral');
  const [treatmentTypeId, setTreatmentTypeId] = useState('');
  const [notes,            setNotes]            = useState('');
  const [lienCompanyName,  setLienCompanyName]  = useState('');
  const [lienCompanyEmail, setLienCompanyEmail] = useState('');
  // Referral Attribution — optional, defaults to blank. Never auto-selected.
  const [referralAttributionId, setReferralAttributionId] = useState('');

  const [treatmentTypes,   setTreatmentTypes]   = useState<TreatmentType[]>([]);
  const [attributionOptions, setAttributionOptions] = useState<ReferralAttributionSummary[]>([]);

  const [loading,          setLoading]          = useState(false);
  const [error,            setError]            = useState<string | null>(null);
  const [fieldErrors,      setFieldErrors]      = useState<Record<string, string>>({});

  useEffect(() => {
    fetch('/api/careconnect/api/treatment-types')
      .then(r => r.ok ? r.json() : [])
      .then((data: TreatmentType[]) => setTreatmentTypes(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    careConnectApi.referralAttributions.options()
      .then(({ data }) => setAttributionOptions(data))
      .catch(() => {}); // non-fatal — field simply shows no options
  }, []);

  function validate(): boolean {
    const errs: Record<string, string> = {};
    if (!clientFirstName.trim()) errs.clientFirstName = 'First name is required';
    if (!clientLastName.trim())  errs.clientLastName  = 'Last name is required';
    if (!clientPhone.trim())      errs.clientPhone = 'Phone is required';
    else if (!isValidPhone(clientPhone)) errs.clientPhone = 'Enter a valid 10-digit phone number';
    if (!clientEmail.trim())     errs.clientEmail = 'Email is required';
    if (!dateOfAccident)         errs.dateOfAccident = 'Date of accident is required';
    else if (!isValidIsoDate(dateOfAccident)) errs.dateOfAccident = 'Enter a valid date';
    else if (!hasReasonableYear(dateOfAccident)) errs.dateOfAccident = 'Please enter a valid year (1900 or later)';
    else if (new Date(dateOfAccident) > new Date()) errs.dateOfAccident = 'Date of accident cannot be in the future';
    if (clientDob && !isValidIsoDate(clientDob)) errs.clientDob = 'Enter a valid date of birth';
    else if (clientDob && !hasReasonableYear(clientDob)) errs.clientDob = 'Please enter a valid year (1900 or later)';
    else if (clientDob && new Date(clientDob) > new Date()) errs.clientDob = 'Date of birth cannot be in the future';
    setFieldErrors(errs);
    return Object.keys(errs).length === 0;
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (!validate()) return;

    setError(null);
    setLoading(true);

    const payload: CreateReferralRequest = {
      providerId,
      clientFirstName:  clientFirstName.trim(),
      clientLastName:   clientLastName.trim(),
      clientDob:        clientDob || undefined,
      clientPhone:      stripPhone(clientPhone),
      clientEmail:      clientEmail.trim(),
      caseNumber:       caseNumber.trim() || undefined,
      dateOfAccident:   dateOfAccident || undefined,
      requestedService: serviceType || undefined,
      urgency,
      treatmentTypeId:  treatmentTypeId || undefined,
      notes:            notes.trim() || undefined,
      lienCompanyName:  lienCompanyName.trim() || undefined,
      lienCompanyEmail: lienCompanyEmail.trim() || undefined,
      referralAttributionId: referralAttributionId || undefined,
      referrerEmail:    referrerEmail || undefined,
      referrerName:     referrerName  || undefined,
    };

    try {
      const { data: referral } = await careConnectApi.referrals.create(payload);
      // Navigate to the newly created referral's detail page
      router.push(`/careconnect/referrals/${referral.id}`);
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.isUnauthorized) { router.push('/login'); return; }
        if (err.isForbidden) {
          setError(`Access denied (${err.correlationId}). You may not have permission to create referrals.`);
          return;
        }
        setError(err.message);
      } else {
        setError('An unexpected error occurred. Please try again.');
      }
    } finally {
      setLoading(false);
    }
  }

  const hasClientPhoneValue   = clientPhone.trim().length > 0;
  const hasInvalidClientPhone = hasClientPhoneValue && !isValidPhone(clientPhone);

  const canSubmit =
    !!clientFirstName.trim() &&
    !!clientLastName.trim() &&
    hasClientPhoneValue && !hasInvalidClientPhone &&
    !!clientEmail.trim() &&
    !!dateOfAccident &&
    (!dateOfAccident || (isValidIsoDate(dateOfAccident) && hasReasonableYear(dateOfAccident) && new Date(dateOfAccident) <= new Date())) &&
    (!clientDob || (isValidIsoDate(clientDob) && hasReasonableYear(clientDob) && new Date(clientDob) <= new Date()));

  function InputError({ field }: { field: string }) {
    return fieldErrors[field]
      ? <p className="mt-1 text-xs text-red-600">{fieldErrors[field]}</p>
      : null;
  }

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      {/* Backdrop */}
      <div
        className="fixed inset-0 bg-black/30 backdrop-blur-sm"
        onClick={onClose}
      />

      {/* Dialog */}
      <div className="relative min-h-screen flex items-center justify-center p-4">
        <div className="relative w-full max-w-2xl bg-white rounded-xl shadow-xl">
          {/* Header */}
          <div className="flex items-center justify-between px-6 py-4 border-b border-gray-100">
            <div>
              <h2 className="text-base font-semibold text-gray-900">Create Referral</h2>
              <p className="text-sm text-gray-500 mt-0.5">To: {providerName}</p>
            </div>
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-700 transition-colors p-1"
              aria-label="Close"
            >
              ✕
            </button>
          </div>

          <form onSubmit={handleSubmit} className="px-6 py-5 space-y-6">
            {/* Global error */}
            {error && (
              <div className="bg-red-50 border border-red-200 rounded-md px-4 py-3 text-sm text-red-700">
                {error}
              </div>
            )}

            {/* Client information */}
            <fieldset>
              <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
                Client Information
              </legend>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    First name <span className="text-red-500">*</span>
                  </label>
                  <input
                    type="text"
                    value={clientFirstName}
                    onChange={e => { setClientFirstName(e.target.value); setFieldErrors(fe => ({ ...fe, clientFirstName: '' })); }}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                  <InputError field="clientFirstName" />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Last name <span className="text-red-500">*</span>
                  </label>
                  <input
                    type="text"
                    value={clientLastName}
                    onChange={e => { setClientLastName(e.target.value); setFieldErrors(fe => ({ ...fe, clientLastName: '' })); }}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                  <InputError field="clientLastName" />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Date of birth</label>
                  <input
                    type="date"
                    value={clientDob}
                    min="1900-01-01"
                    max={new Date().toISOString().split('T')[0]}
                    onChange={e => {
                      const v = e.target.value;
                      setClientDob(v);
                      if (v && isValidIsoDate(v) && !hasReasonableYear(v))
                        setFieldErrors(fe => ({ ...fe, clientDob: 'Please enter a valid year (1900 or later)' }));
                      else if (v && isValidIsoDate(v) && new Date(v) > new Date())
                        setFieldErrors(fe => ({ ...fe, clientDob: 'Date of birth cannot be in the future' }));
                      else
                        setFieldErrors(fe => ({ ...fe, clientDob: '' }));
                    }}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                  <InputError field="clientDob" />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Date of accident <span className="text-red-500">*</span>
                  </label>
                  <input
                    type="date"
                    value={dateOfAccident}
                    min="1900-01-01"
                    max={new Date().toISOString().split('T')[0]}
                    onChange={e => {
                      const v = e.target.value;
                      setDateOfAccident(v);
                      if (v && isValidIsoDate(v) && !hasReasonableYear(v))
                        setFieldErrors(fe => ({ ...fe, dateOfAccident: 'Please enter a valid year (1900 or later)' }));
                      else if (v && isValidIsoDate(v) && new Date(v) > new Date())
                        setFieldErrors(fe => ({ ...fe, dateOfAccident: 'Date of accident cannot be in the future' }));
                      else
                        setFieldErrors(fe => ({ ...fe, dateOfAccident: '' }));
                    }}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                  <InputError field="dateOfAccident" />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Phone <span className="text-red-500">*</span>
                  </label>
                  <input
                    type="tel"
                    value={clientPhone}
                    placeholder="(555) 555-5555"
                    onChange={e => { setClientPhone(formatPhoneInput(e.target.value)); setFieldErrors(fe => ({ ...fe, clientPhone: '' })); }}
                    className={`w-full border rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 ${
                      hasInvalidClientPhone
                        ? 'border-red-300 focus:ring-red-400'
                        : 'border-gray-300 focus:ring-primary'
                    }`}
                  />
                  {hasInvalidClientPhone && (
                    <p className="text-xs text-red-500 mt-1">Phone number must be 10 digits.</p>
                  )}
                  <InputError field="clientPhone" />
                </div>

                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Email <span className="text-red-500">*</span>
                  </label>
                  <input
                    type="email"
                    value={clientEmail}
                    onChange={e => { setClientEmail(e.target.value); setFieldErrors(fe => ({ ...fe, clientEmail: '' })); }}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                  <InputError field="clientEmail" />
                </div>
              </div>
            </fieldset>

            {/* Referral details */}
            <fieldset>
              <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
                Referral Details
              </legend>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Type of service</label>
                  <select
                    value={serviceType}
                    onChange={e => setServiceType(e.target.value)}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary bg-white"
                  >
                    {SERVICE_TYPES.map(s => <option key={s} value={s}>{s}</option>)}
                  </select>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Urgency</label>
                  <select
                    value={urgency}
                    onChange={e => setUrgency(e.target.value as ReferralUrgencyValue)}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary bg-white"
                  >
                    {URGENCY_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                  </select>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Type of treatment</label>
                  <select
                    value={treatmentTypeId}
                    onChange={e => setTreatmentTypeId(e.target.value)}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary bg-white"
                  >
                    <option value="">None</option>
                    {treatmentTypes.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
                  </select>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Case number</label>
                  <input
                    type="text"
                    value={caseNumber}
                    onChange={e => setCaseNumber(e.target.value)}
                    placeholder="Optional"
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                </div>

                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Referral Attribution</label>
                  <select
                    value={referralAttributionId}
                    onChange={e => setReferralAttributionId(e.target.value)}
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary bg-white"
                  >
                    <option value="">Select a referral source</option>
                    {attributionOptions.map(a => (
                      <option key={a.id} value={a.id}>
                        {a.firstName} {a.lastName}
                      </option>
                    ))}
                  </select>
                  <p className="mt-1 text-xs text-gray-500">
                    Select the person, Campaign, or partner responsible for originating this referral.
                  </p>
                </div>

                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Notes</label>
                  <textarea
                    value={notes}
                    onChange={e => setNotes(e.target.value)}
                    rows={3}
                    placeholder="Optional additional notes for the provider…"
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary resize-none"
                  />
                </div>

                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Lien company name</label>
                  <input
                    type="text"
                    value={lienCompanyName}
                    onChange={e => setLienCompanyName(e.target.value)}
                    placeholder="Optional"
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                </div>

                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Lien company email</label>
                  <input
                    type="email"
                    value={lienCompanyEmail}
                    onChange={e => setLienCompanyEmail(e.target.value)}
                    placeholder="Optional"
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
                  />
                </div>
              </div>
            </fieldset>

            {/* Actions */}
            <div className="flex items-center justify-end gap-3 pt-2 border-t border-gray-100">
              <button
                type="button"
                onClick={onClose}
                disabled={loading}
                className="text-sm text-gray-600 hover:text-gray-900 px-4 py-2 rounded-md transition-colors disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={loading || !canSubmit}
                className="bg-primary text-white text-sm font-medium px-5 py-2 rounded-md hover:opacity-90 disabled:opacity-60 disabled:cursor-not-allowed transition-opacity"
              >
                {loading ? 'Creating…' : 'Create Referral'}
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}
