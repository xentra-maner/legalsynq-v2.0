"use client";

import { type ReactNode } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useRepresentativePortal } from "@/components/careconnect/representative-access-code-gate";
import { clsx } from "clsx";

interface RepresentativeShellProps {
  children: ReactNode;
}

const NAV_ITEMS = [
  { href: "/careconnect/referral/dashboard", label: "Dashboard", icon: "ri-dashboard-line" },
  { href: "/careconnect/referral/submit", label: "Submit Request", icon: "ri-send-plane-line" },
  { href: "/careconnect/referral/referrals", label: "My Referrals", icon: "ri-file-list-3-line" },
];

const ACTIVE_COLOR = "#f97316"; // orange-500 — same GLOBAL_DEFAULTS.appearance.nav.activeColor as the main app
const ACTIVE_BG    = "#fff7ed"; // orange-50  — same GLOBAL_DEFAULTS.appearance.nav.activeBg

/**
 * Dedicated layout shell for the restricted, fully anonymous Referral
 * Portal. Deliberately does NOT import AppShell, PRODUCT_NAV, or any admin navigation
 * component — this is structural isolation, not conditional hiding. There is no session
 * here at all: this shell can never reach tenant settings, user administration,
 * attribution configuration, admin referral queues, provider/law-firm administration,
 * billing, or any other admin surface, because those routes and components are simply
 * never imported here.
 *
 * Visually mirrors the authenticated CareConnect shell (TopBar + Sidebar) rather than
 * inventing new chrome: navy top bar (#0f1928) with the CareConnect wordmark, white
 * sidebar with the same "Synq CareConnect" product icon/nav-item styling (including the
 * orange active-item accent — GLOBAL_DEFAULTS.appearance.nav in config/app-settings.ts),
 * and a plain white content area. The only differences are what can't exist without a
 * session: no product switcher, no notification bell, no user avatar/profile menu — those
 * are replaced with the unlocked attribution's name and a "Lock" action.
 */
export function RepresentativeShell({ children }: RepresentativeShellProps) {
  return (
    <div className="flex flex-col h-screen overflow-hidden">
      <RepresentativeTopBar />
      <div className="flex flex-1 overflow-hidden">
        <RepresentativeSidebar />
        <main className="flex-1 overflow-y-auto bg-white p-6">
          {children}
        </main>
      </div>
    </div>
  );
}

function RepresentativeTopBar() {
  const { referralAttributionFullName, lock } = useRepresentativePortal();

  return (
    <header
      className="flex items-center h-14 px-4 shrink-0 gap-3"
      style={{ backgroundColor: "#0f1928" }}
    >
      {/* eslint-disable-next-line @next/next/no-img-element -- static product wordmark, not tenant-branded */}
      <img
        src="/careconnect-logo.png"
        alt="CareConnect"
        style={{ height: 32, width: "auto" }}
        className="object-contain shrink-0"
      />

      <div className="flex-1" />

      {referralAttributionFullName && (
        <span className="text-sm text-slate-300 truncate max-w-[220px]">
          {referralAttributionFullName}
        </span>
      )}
      <button
        onClick={lock}
        title="Lock — clear this code from this device"
        className="flex items-center gap-1.5 h-8 px-3 rounded-lg text-slate-300 hover:bg-white/10 hover:text-white transition-colors shrink-0 cursor-pointer"
      >
        <i className="ri-lock-line text-[15px] leading-none" />
        <span className="text-sm font-medium">Lock</span>
      </button>
    </header>
  );
}

function RepresentativeSidebar() {
  const pathname = usePathname() ?? "";

  return (
    <aside className="shrink-0 flex flex-col bg-white border-r border-gray-200 overflow-hidden w-[220px]">
      <div className="shrink-0 flex items-center border-b border-gray-100 h-12 px-4">
        <div className="flex items-center gap-2 min-w-0">
          {/* eslint-disable-next-line @next/next/no-img-element -- small static product icon */}
          <img src="/product-icons/synqconnect.png" alt="" aria-hidden className="w-4 h-4 shrink-0 object-contain" />
          <span className="text-[12px] font-semibold text-gray-700 truncate">Synq CareConnect</span>
        </div>
      </div>

      <div className="flex-1 overflow-y-auto overflow-x-hidden py-2">
        <nav className="space-y-0.5 px-3">
          {NAV_ITEMS.map(item => {
            const isActive = pathname === item.href || pathname.startsWith(item.href + "/");
            return (
              <Link
                key={item.href}
                href={item.href}
                className={clsx(
                  "relative flex cursor-pointer items-center gap-2.5 px-3 py-2.5 rounded-lg text-[12px] font-medium transition-colors",
                  !isActive && "text-gray-600 hover:bg-gray-100 hover:text-gray-900",
                )}
                style={isActive ? { backgroundColor: ACTIVE_BG, color: "#0f1928" } : undefined}
              >
                {isActive && (
                  <span
                    className="absolute left-0 top-1.5 bottom-1.5 w-0.5 rounded-full"
                    style={{ backgroundColor: ACTIVE_COLOR }}
                  />
                )}
                <i
                  className={`${item.icon} text-[16px] leading-none shrink-0`}
                  style={{ color: isActive ? ACTIVE_COLOR : undefined }}
                />
                <span className="flex-1">{item.label}</span>
              </Link>
            );
          })}
        </nav>
      </div>
    </aside>
  );
}
