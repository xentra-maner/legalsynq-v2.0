# Referral Origination & Referral Representative Portal — Operational Guide

A step-by-step guide to setting up and using this feature. No technical background needed.

---

## What This Feature Does

- You can create named "sources" for referrals — for example, a person like **Cam Perry**, a
  campaign, or a partner. This is called a **Referral Origination**.
- When a law firm submits a referral, they can optionally pick a source.
- You can generate an **access code** for a source and give it to the person who should see
  its referrals. That person enters the code themselves at a portal link — **no account or
  login required** — and sees **only** the referrals that came from their source — nothing
  else in the system.

---

## Who Does What

| Role | What they do |
|---|---|
| **Tenant/Platform Administrator** (existing admin role — not new) | Creates referral sources, generates access codes for them, and can view (but not change) a referral's source afterward. |
| **Law Firm User** | Optionally picks a source when submitting a referral. |
| **Referral Representative** (not a role, not an account — anyone with the code) | Opens the portal link, enters the access code, and sees a simple, read-only screen with only their referrals. No login. |

---

## Before You Begin: One-Time Setup

One thing needs to be turned on before this feature can be used for the first time. **This step
must be done by your engineering/IT team** — it is not available as a button yet.

1. **Turn the feature on for your organization.** Ask engineering to enable the "Referral
   Representative Portal" setting for your account.

There is no separate step to designate someone as a "Referral Representative" and no account
to create — the portal is open to anyone with a valid link and code, no CareConnect login of
any kind. The access code is what actually grants (and limits) access. Once the feature is
turned on, everything below can be done by a Tenant Administrator without any further
engineering help.

---

## Step 1: Create a Referral Source

1. In the CareConnect sidebar, click **Referral Originations**.
2. Click **+ Add Origination**.
3. Fill in:
   - **First Name** and **Last Name** — the name people will see (example: `Cam` / `Perry`).
   - **Code** — a short internal identifier with no spaces (example: `CAM_PERRY`). This cannot
     be changed later, so choose carefully.
   - **Display Order** (optional) — controls the order it appears in the dropdown list.
4. Click **Save**.

The new source is now available for law firms to select and, once you generate a code for it
(Step 2), for a representative to see its referrals.

The list only shows First Name, Last Name, and Status — click the **⋮** menu on a source's row
and choose **View** to see everything else (code, description, display order, and the access
code widget from Step 2), or **Deactivate**/**Activate** to retire or restore it. Deactivating
does not delete anything or affect past referrals — it only stops the source from being offered
on new submissions, and immediately cuts off any active representative access code for it too
(see the note in Step 2).

---

## Step 2: Give a Person Access as a Referral Representative

1. In the CareConnect sidebar, click **Referral Originations**, then open the **⋮** menu on the
   source and choose **View**.
2. Under **Representative Access Code**, click **+ Generate Access Code**.
3. (Optional) Set a start date and/or end date if access should only apply during a certain
   period. Leave blank for ongoing access.
4. Click **Generate**.
5. A code appears on screen (for example `H7G4-4G6V-XU`) — **copy it now**. It will not be
   shown again after you leave this screen or generate another code.
6. Share the code with the person securely (in person, a password manager, etc. — not an
   unencrypted email if you can avoid it). The **Referral Originations** list page (Step 1) has
   a **Representative Portal URL** with **Copy**/**Open** buttons — that's the same link for
   every representative, so send it along with the code.
7. That person opens the portal link and enters the code themselves — no account, no login,
   nothing to activate ahead of time. See Step 5 below.

**Important things to know:**

- Creating a source does **not** automatically give anyone access to it. Access only happens
  once someone actually has a valid code for it.
- You never pick or type a person's account here — the code is the only thing that matters,
  and there is no account to link it to. Whoever has a valid code gets access, so treat codes
  like passwords — anyone who has the code and the portal link can use it.
- **Only one code can be active per source at a time.** If a code already exists, you'll need
  to revoke it before generating a replacement — the source's detail view only offers
  **Generate** when there's no active code, and shows **Revoke** when there is. If more than
  one person needs to see the same source's referrals, share the same code with all of them —
  the code itself is what's checked, not who's using it.
- The start/end dates control **when the code is usable**, not when it can be entered — someone
  can type in a future-dated code right away, but the portal won't show any referrals until
  the window opens. Once usable, the portal shows all current referrals tied to the source,
  including ones submitted before the code was generated.
- The code is checked fresh on every visit to the portal — nothing is "used up" or tied to a
  specific person. If someone loses their code, revoke it and generate a new one so the old
  one stops working.
- If you deactivate the source itself, anyone using its code immediately loses access too.
  Reactivating the source restores access without needing a new code.

**To remove access:** open the source's detail view and click **Revoke** next to its access
code. This takes effect immediately — the code stops working on its very next use, even for
someone who already has the portal open.

---

## Step 3: Law Firm — Selecting a Source When Submitting a Referral

When a law firm user fills out a referral form, they will see a field:

> **Referral Origination**
> *Select the person, Campaign, or partner responsible for originating this referral.*

- This field is optional and starts blank — nothing is pre-selected.
- The law firm user picks the correct source from the dropdown, or leaves it blank if not
  applicable.
- Selecting a source does not change which provider the referral goes to.

---

## Step 4: Admin — Viewing a Referral's Source

1. Open any referral's detail page.
2. You'll see a **Referral Origination** field showing the source that was selected at
   submission, or a dash (**—**) if none was chosen.

The source is set only once, by the law firm at the moment they submit the referral, and
cannot be changed afterward by anyone — including administrators. If a referral was
submitted with the wrong source (or none at all), there is currently no way to correct it
after the fact; the law firm needs to get it right at submission time.

---

## Step 5: Using the Representative Portal (For the Representative)

1. Open the portal link your administrator sent you. No account, no login, no sign-up.
2. You'll land on a branded **Enter Access Code** screen. Type in the code your administrator
   gave you (dashes included or not, it doesn't matter) and click **View My Referrals**.
3. Once unlocked, you'll see two options: **Dashboard** and **My Referrals**. You will not see
   any admin or settings screens — only these.
4. **Dashboard** shows totals: how many referrals originated from your source, and how many
   are new, open, or closed. You can filter by date range.
5. **My Referrals** shows a list of only the referrals originated from your source. You can
   filter by status or date.
6. Click any referral to see its details: reference number, submission date, status, law firm,
   provider, and status history.
7. This portal is **view-only** — nothing can be edited from here.
8. Click **Lock** at any time to clear the code from this device and return to the code entry
   screen — useful on a shared computer. There's no separate "sign out" since there was never
   a sign-in.

If the code doesn't work, double-check you typed it correctly, or contact your Tenant
Administrator — the code may have been revoked, expired, or the source it belongs to may have
been deactivated.

---

## Common Questions

**Q: Does the person need a special role, account, or login to become a Referral
Representative?**
A: No. Nobody logs in. Anyone with the portal link and a valid code can use it — there's
nothing to assign or set up ahead of time. If the portal shows nothing, it's because the code
hasn't been entered yet, or it was revoked or expired — never because of a missing role or
account.

**Q: I turned off a source (deactivated it). Did that remove access?**
A: Immediately. Deactivating a source both stops it from being offered on new referrals AND
cuts off its code's access to the portal right away. Reactivating the source restores access
without generating a new code.

**Q: I revoked an access code. Why can someone still see something in their browser?**
A: The code is checked every time the portal loads something, so it should stop immediately.
If someone still sees an old page, it's likely a page they had open before you revoked the
code — refreshing the page will show the correct (blocked) result.

**Q: Can the portal show referrals from other tenants or companies?**
A: No. It only ever shows referrals that belong to your organization and that originated from
the source the code was generated for.

**Q: Can anything be edited or deleted from the portal?**
A: No. It's read-only in this release.

**Q: Can more than one person use the same code?**
A: Yes. The code isn't tied to a specific person or device — anyone who has it and the portal
link can use it, from as many places as they like, at the same time. If that's not what you
want, treat the code like a shared password and control who you give it to; revoke and
regenerate it if it's shared more widely than intended.
