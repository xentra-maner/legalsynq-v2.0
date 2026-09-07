namespace Identity.Domain;

public class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? Title { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// UIX-003-03: Whether this account is administratively locked.
    /// Locked users cannot authenticate regardless of IsActive.
    /// </summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// UIX-003-03: When the account was locked. Null if not currently locked.
    /// </summary>
    public DateTime? LockedAtUtc { get; private set; }

    /// <summary>
    /// UIX-003-03: The admin user ID who locked this account. Null if not locked or locked by system.
    /// </summary>
    public Guid? LockedByAdminId { get; private set; }

    /// <summary>
    /// UIX-003-03: Timestamp of the user's most recent successful login.
    /// Updated on each successful authentication.
    /// </summary>
    public DateTime? LastLoginAtUtc { get; private set; }

    /// <summary>
    /// UIX-003-03: Monotonically-increasing session invalidation counter.
    /// Embedded in JWT as session_version. Force-logout increments this;
    /// tokens with a lower version are rejected by auth/me.
    /// </summary>
    public int SessionVersion { get; private set; }

    /// <summary>
    /// LS-COR-AUT-003: Monotonically-increasing access version counter.
    /// Embedded in JWT as access_version. Incremented whenever the user's
    /// product access or role assignments change. Tokens with a stale
    /// access_version are rejected by auth/me, forcing re-authentication
    /// to pick up updated claims.
    /// </summary>
    public int AccessVersion { get; private set; }

    /// <summary>
    /// Reference to the user's profile picture stored in the Documents service.
    /// Null when no avatar has been uploaded.
    /// </summary>
    public Guid? AvatarDocumentId { get; private set; }

    /// <summary>
    /// Primary phone number in E.164 format used as the SMS destination
    /// when this user is reached through a role- or org-addressed
    /// notification fan-out. Null when the user has not provided a phone
    /// number — the notifications service skips SMS dispatch in that case
    /// with reason "no_phone_on_file" so operators can see why members
    /// were not reached.
    /// </summary>
    public string? Phone { get; private set; }

    /// <summary>
    /// PUM-B01: Unified user type classification.
    /// Defaults to TenantUser for all users created via the tenant flow.
    /// PlatformInternal is reserved for LegalSynq staff accounts.
    /// ExternalCustomer is reserved for future Commerce/Support portals.
    /// </summary>
    public UserType UserType { get; private set; } = UserType.TenantUser;

    public ICollection<UserOrganizationMembership> OrganizationMemberships { get; private set; } = [];

    /// <summary>
    /// Multi-tenant account linking — all tenants this user belongs to.
    /// </summary>
    public ICollection<UserTenant> TenantMemberships { get; private set; } = [];

    // Phase 4+: scoped role assignments — sole authoritative role source (Phase G).
    public ICollection<ScopedRoleAssignment> ScopedRoleAssignments { get; private set; } = [];

    private User() { }

    /// <summary>
    /// Marks the user as inactive. Idempotent — safe to call when already inactive.
    /// Returns true if state changed, false if already inactive.
    /// </summary>
    public bool Deactivate()
    {
        if (!IsActive) return false;
        IsActive     = false;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Marks the user as active. Idempotent — safe to call when already active.
    /// Returns true if state changed, false if already active.
    /// </summary>
    public bool Activate()
    {
        if (IsActive) return false;
        IsActive     = true;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// UIX-003-03: Locks this account. Idempotent — safe to call when already locked.
    /// Returns true if state changed, false if already locked.
    /// Locked users are rejected during login and all active sessions become invalid
    /// because SessionVersion is incremented, invalidating existing JWTs.
    /// </summary>
    public bool Lock(Guid? lockedByAdminId = null)
    {
        var now = DateTime.UtcNow;
        if (IsLocked) return false;
        IsLocked         = true;
        LockedAtUtc      = now;
        LockedByAdminId  = lockedByAdminId;
        SessionVersion++;
        UpdatedAtUtc     = now;
        return true;
    }

    /// <summary>
    /// UIX-003-03: Unlocks this account. Idempotent — safe to call when not locked.
    /// Returns true if state changed, false if already unlocked.
    /// </summary>
    public bool Unlock()
    {
        if (!IsLocked) return false;
        IsLocked        = false;
        LockedAtUtc     = null;
        LockedByAdminId = null;
        UpdatedAtUtc    = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// UIX-003-03: Records a successful login timestamp.
    /// Called at the end of a successful LoginAsync.
    /// </summary>
    public void RecordLogin()
    {
        LastLoginAtUtc = DateTime.UtcNow;
        UpdatedAtUtc   = DateTime.UtcNow;
    }

    /// <summary>
    /// UIX-003-03: Increments the session version to invalidate all existing JWTs.
    /// Used by force-logout. Always changes state (never idempotent by design).
    /// </summary>
    public void IncrementSessionVersion()
    {
        SessionVersion++;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// LS-COR-AUT-003: Increments the access version to mark existing JWTs as stale.
    /// Called when the user's product access or role assignments change.
    /// </summary>
    public void IncrementAccessVersion()
    {
        AccessVersion++;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Replaces the stored password hash. Used when an invited user sets their
    /// password on first login (accept-invite flow) or when admin-triggered
    /// password reset is confirmed by the user.
    /// </summary>
    public void SetPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash   = passwordHash;
        SessionVersion++;
        UpdatedAtUtc   = DateTime.UtcNow;
    }

    /// <summary>
    /// Associates a Documents-service document ID as the user's profile picture.
    /// The document must be uploaded before calling this method.
    /// </summary>
    public void SetAvatar(Guid documentId)
    {
        AvatarDocumentId = documentId;
        UpdatedAtUtc     = DateTime.UtcNow;
    }

    /// <summary>
    /// Removes the user's profile picture. Idempotent — safe to call when already null.
    /// </summary>
    public void ClearAvatar()
    {
        AvatarDocumentId = null;
        UpdatedAtUtc     = DateTime.UtcNow;
    }

    /// <summary>
    /// Records the user's primary phone number, normalised by trimming
    /// surrounding whitespace. Pass null or whitespace to clear the field.
    /// Idempotent — safe to call when the value is unchanged.
    /// </summary>
    public bool SetPhone(string? phone)
    {
        var normalised = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (string.Equals(Phone, normalised, StringComparison.Ordinal)) return false;
        Phone        = normalised;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Records the user's optional professional title, such as Dr. or Esq.
    /// </summary>
    public bool SetTitle(string? title)
    {
        var normalised = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (string.Equals(Title, normalised, StringComparison.Ordinal)) return false;
        Title        = normalised;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Updates the user's first and last name. Both parts are required — a name
    /// is treated as a pair. Trims surrounding whitespace. Idempotent: returns
    /// false (and touches nothing) when neither part changes.
    /// </summary>
    public bool UpdateName(string firstName, string lastName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        var f = firstName.Trim();
        var l = lastName.Trim();
        if (string.Equals(FirstName, f, StringComparison.Ordinal) &&
            string.Equals(LastName, l, StringComparison.Ordinal)) return false;
        FirstName    = f;
        LastName     = l;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Changes the user's email (a login identifier). Lowercased and trimmed to
    /// match <see cref="Create"/>. Bumps <see cref="SessionVersion"/> so existing
    /// JWTs are invalidated. Idempotent: returns false when unchanged.
    /// Uniqueness is enforced by the caller and the database index.
    /// </summary>
    public bool ChangeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var e = email.ToLowerInvariant().Trim();
        if (string.Equals(Email, e, StringComparison.Ordinal)) return false;
        Email          = e;
        SessionVersion++;
        UpdatedAtUtc   = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// PUM-B01: Updates the user type classification.
    /// Idempotent — safe to call when the value is unchanged.
    /// </summary>
    public bool SetUserType(UserType userType)
    {
        if (UserType == userType) return false;
        UserType     = userType;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    public static User Create(
        Guid tenantId,
        string email,
        string passwordHash,
        string firstName,
        string lastName,
        UserType userType = UserType.TenantUser,
        string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);

        var now = DateTime.UtcNow;
        return new User
        {
            Id             = Guid.CreateVersion7(),
            Email          = email.ToLowerInvariant().Trim(),
            PasswordHash   = passwordHash,
            Title          = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            FirstName      = firstName.Trim(),
            LastName       = lastName.Trim(),
            IsActive       = true,
            IsLocked       = false,
            SessionVersion = 0,
            AccessVersion  = 0,
            UserType       = userType,
            CreatedAtUtc   = now,
            UpdatedAtUtc   = now
        };
    }
}
