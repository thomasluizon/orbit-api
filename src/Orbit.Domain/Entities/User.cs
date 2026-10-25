using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public partial class User : Entity
{
    private TimeProvider _timeProvider = TimeProvider.System;
    private const int DefaultStreakDaysPerFreeze = 7;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailRegex();

    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? TimeZone { get; private set; }
    public bool AiMemoryEnabled { get; private set; } = true;
    public bool AiSummaryEnabled { get; private set; } = true;
    public bool ProactiveAstraEnabled { get; private set; } = true;
    public bool EffectiveProactivePushEnabled => HasProAccess && ProactiveAstraEnabled;
    public bool HasCompletedOnboarding { get; private set; } = false;
    public bool HasCompletedTour { get; private set; } = false;
    public bool HasCreatedFirstHabit { get; private set; } = false;
    public bool HasLoggedFirstHabit { get; private set; } = false;
    public bool HasTriedAstra { get; private set; } = false;
    public bool HasCompletedOnboardingChecklist { get; private set; } = false;
    public string? Language { get; private set; }
    public UserPlan SubscriptionPlan { get; private set; } = UserPlan.Free;

    public string? StripeCustomerId { get; private set; }
    public string? StripeSubscriptionId { get; private set; }
    public DateTime? SubscriptionExpiresAtUtc { get; private set; }

    public DateTime? BonusProExpiresAtUtc { get; private set; }
    public DateTime? TrialEndsAt { get; private set; }
    public bool IsLifetimePro { get; private set; } = false;
    public int AiMessagesUsedToday { get; private set; } = 0;
    public DateOnly? AiMessagesLocalDate { get; private set; }
    public SubscriptionInterval? SubscriptionInterval { get; private set; }
    public SubscriptionSource? SubscriptionSource { get; private set; }
    public SubscriptionLapseReason? SubscriptionLapseReason { get; private set; }
    public DateTime? SubscriptionEndedAtUtc { get; private set; }
    public DateTime? StripeSubscriptionEventCreatedAtUtc { get; private set; }
    public string? PlayPurchaseToken { get; private set; }
    public bool StripeRenewalStoppedByDeletion { get; private set; }
    public bool PlayRenewalStoppedByDeletion { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool HasImportedCalendar { get; private set; } = false;
    public bool HasSeenImportPrompt { get; private set; } = false;
    public string? GoogleAccessToken { get; private set; }
    public string? GoogleRefreshToken { get; private set; }
    public bool GoogleCalendarAutoSyncEnabled { get; private set; }
    public string? GoogleCalendarSelectedIds { get; private set; }
    public GoogleCalendarAutoSyncStatus? GoogleCalendarAutoSyncStatus { get; private set; }
    public DateTime? GoogleCalendarLastSyncedAt { get; private set; }
    public string? GoogleCalendarLastSyncError { get; private set; }
    public DateTime? GoogleCalendarSyncReconciledAt { get; private set; }
    public bool IsDeactivated { get; private set; }
    public DateTime? DeactivatedAt { get; private set; }
    public DateTime? ConversationHistoryClearedAtUtc { get; private set; }
    public DateTime? ScheduledDeletionAt { get; private set; }
    public int WeekStartDay { get; private set; } = 1;
    public bool? Uses24HourClockPreference { get; private set; }
    public int ReminderPreferencesVersion { get; private set; }
    public string? ReferralCode { get; private set; }
    public Guid? ReferredByUserId { get; private set; }
    public bool SocialOptIn { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool? MarketingEmailConsent { get; private set; }
    public DateTime? MarketingConsentUpdatedAtUtc { get; private set; }
    public int TotalXp { get; private set; } = 0;
    public int Level { get; private set; } = 1;
    public string? ReferralCouponId { get; private set; }
    public int AdRewardBonusMessages { get; private set; } = 0;
    public DateTime? LastAdRewardAt { get; private set; }
    public DateOnly? LastAdRewardLocalDate { get; private set; }
    public int AdRewardsClaimedToday { get; private set; } = 0;
    public int CurrentStreak { get; private set; } = 0;
    public int LongestStreak { get; private set; } = 0;
    public DateOnly? LastActiveDate { get; private set; }
    public DateOnly? LastPurgedCompletionDate { get; private set; }
    public int StreakFreezesAccumulated { get; private set; } = 0;
    public int LastFreezeAwardStreak { get; private set; } = 0;
    public int? PreGapFreezeAwardStreak { get; private set; }
    public DateOnly? PreGapLastActiveDate { get; private set; }
    public string? ThemePreference { get; private set; }
    /// <summary>
    /// Either null or <see cref="ColorSchemes.Granted"/> once the account writes again. No migration
    /// rewrites the column, so a row written before the collapse still holds its historical value
    /// and the data export still reports it truthfully.
    /// </summary>
    public string? ColorScheme { get; private set; }
    /// <summary>Public-profile storage retained until https://github.com/thomasluizon/orbit-tickets/issues/1555 contracts the schema.</summary>
    public string? PublicProfileSlug { get; private set; }
    public bool PublicProfileShowStreak { get; private set; } = true;
    public bool PublicProfileShowLevel { get; private set; } = true;
    public bool PublicProfileShowAchievements { get; private set; } = true;
    public bool PublicProfileShowTopHabits { get; private set; } = false;

    [NotMapped]
    public UserPlan Plan => BonusProExpiresAtUtc.HasValue ? UserPlan.Pro : SubscriptionPlan;

    [NotMapped]
    public DateTime? PlanExpiresAt => BonusProExpiresAtUtc.HasValue
        && (!SubscriptionExpiresAtUtc.HasValue || BonusProExpiresAtUtc > SubscriptionExpiresAtUtc)
            ? BonusProExpiresAtUtc
            : SubscriptionExpiresAtUtc;

    [NotMapped]
    public bool IsPro => IsLifetimePro
        || (SubscriptionPlan == UserPlan.Pro && SubscriptionExpiresAtUtc > _timeProvider.GetUtcNow().UtcDateTime)
        || BonusProExpiresAtUtc > _timeProvider.GetUtcNow().UtcDateTime;

    [NotMapped]
    public bool IsTrialActive => !IsPro && TrialEndsAt > _timeProvider.GetUtcNow().UtcDateTime;

    [NotMapped]
    public bool HasProAccess => IsPro || IsTrialActive;

    [NotMapped]
    public bool HasActivePaidSubscription => SubscriptionPlan == UserPlan.Pro
        && SubscriptionExpiresAtUtc > _timeProvider.GetUtcNow().UtcDateTime
        && (SubscriptionSource == Enums.SubscriptionSource.Stripe && !string.IsNullOrWhiteSpace(StripeSubscriptionId)
            || SubscriptionSource == Enums.SubscriptionSource.GooglePlay && !string.IsNullOrWhiteSpace(PlayPurchaseToken)
            || SubscriptionSource == Enums.SubscriptionSource.AppStore && !string.IsNullOrWhiteSpace(AppStoreOriginalTransactionId));

    private User() { }

    public static Result<User> Create(string name, string email, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<User>(DomainErrors.NameRequired);

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<User>(DomainErrors.EmailRequired);

        var trimmedEmail = email.Trim();
        if (!EmailRegex().IsMatch(trimmedEmail))
            return Result.Failure<User>(DomainErrors.InvalidEmailFormat);

        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow().UtcDateTime;
        return Result.Success(new User
        {
            _timeProvider = clock,
            Name = name.Trim(),
            Email = trimmedEmail.ToLowerInvariant(),
            CreatedAtUtc = now,
            TrialEndsAt = now.AddDays(7)
        });
    }

    public void RecordPurgedCompletion(DateOnly date)
    {
        if (LastPurgedCompletionDate is null || date > LastPurgedCompletionDate.Value)
            LastPurgedCompletionDate = date;
    }

    public DateOnly? GetLastCompletionDate(DateOnly? liveCompletionDate)
    {
        return liveCompletionDate is null || LastPurgedCompletionDate > liveCompletionDate
            ? LastPurgedCompletionDate
            : liveCompletionDate;
    }

    public Result SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(DomainErrors.NameRequired);

        var trimmedName = name.Trim();
        if (trimmedName.Length > DomainConstants.MaxUserNameLength)
            return Result.Failure(DomainErrors.NameTooLong.Format(DomainConstants.MaxUserNameLength));

        Name = trimmedName;
        return Result.Success();
    }

    /// <summary>
    /// Stores the IANA zone id after trimming it. Trimming here is the boundary fix for
    /// <c>"America/Sao_Paulo "</c>: every reader calls <c>TimeZoneHelper.FindTimeZone</c>, which stays
    /// strict on purpose so a malformed stored id is logged rather than silently repaired.
    /// A blank id is rejected instead of stored, so no reader ever falls back to UTC over whitespace.
    /// </summary>
    public Result SetTimeZone(string ianaTimeZoneId)
    {
        var trimmedTimeZoneId = ianaTimeZoneId?.Trim();
        if (string.IsNullOrEmpty(trimmedTimeZoneId))
            return Result.Failure(DomainErrors.InvalidTimezone.Format(ianaTimeZoneId ?? string.Empty));

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(trimmedTimeZoneId);
            TimeZone = trimmedTimeZoneId;
            ReminderPreferencesVersion++;
            return Result.Success();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Result.Failure(DomainErrors.InvalidTimezone.Format(trimmedTimeZoneId));
        }
    }


    public void SetAiSummary(bool enabled) => AiSummaryEnabled = enabled;

    public void SetProactiveAstraEnabled(bool enabled) => ProactiveAstraEnabled = enabled;

    public void SetLanguage(string? language)
    {
        Language = language;
        ReminderPreferencesVersion++;
    }

    /// <summary>
    /// Persists the user's Google Calendar selection as a JSON array of calendar ids.
    /// A null <see cref="GoogleCalendarSelectedIds"/> means "all owned calendars" (the
    /// default); an empty input list clears the selection back to that default.
    /// </summary>
    public void SetSelectedCalendars(IReadOnlyCollection<string> calendarIds)
    {
        var normalized = calendarIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        GoogleCalendarSelectedIds = normalized.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(normalized);
    }

    /// <summary>
    /// Deserializes <see cref="GoogleCalendarSelectedIds"/> into the user's chosen calendar
    /// ids, or null when no explicit selection exists (callers then fall back to all owned
    /// calendars). Malformed stored JSON is treated as "no selection".
    /// </summary>
    public IReadOnlyList<string>? GetSelectedCalendarIds()
    {
        if (string.IsNullOrWhiteSpace(GoogleCalendarSelectedIds))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(GoogleCalendarSelectedIds);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public Result SetThemePreference(string? preference)
    {
        if (preference is not null && preference is not ("dark" or "light"))
            return Result.Failure(DomainErrors.InvalidThemePreference);
        ThemePreference = preference;
        return Result.Success();
    }

    /// <summary>
    /// Accepts every historical key so an old client's write still succeeds, then stores
    /// <see cref="ColorSchemes.Granted"/> instead of the requested one, because every read path
    /// reports the granted accent. Storing the request would let the write and the read disagree.
    /// A null request still clears the preference rather than writing a value no read surfaces.
    /// </summary>
    public Result SetColorScheme(string? colorScheme)
    {
        if (colorScheme is not null && !ColorSchemes.AcceptedValues.Contains(colorScheme))
            return Result.Failure(DomainErrors.InvalidColorScheme);
        ColorScheme = colorScheme is null ? null : ColorSchemes.Granted;
        return Result.Success();
    }

    public void CompleteOnboarding() => HasCompletedOnboarding = true;

    public void MarkFirstHabitCreated() => HasCreatedFirstHabit = true;

    public void MarkFirstHabitLogged() => HasLoggedFirstHabit = true;

    public void MarkAstraUsed() => HasTriedAstra = true;

    public void CompleteOnboardingChecklist() => HasCompletedOnboardingChecklist = true;

    public void CompleteTour() => HasCompletedTour = true;

    public void ResetTour() => HasCompletedTour = false;

    public void SetStripeCustomerId(string customerId) => StripeCustomerId = customerId;

    public void GrantBonusProDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        if (HasActivePaidSubscription)
            throw new InvalidOperationException("Bonus days cannot extend a paid subscription period.");

        var nowAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = PlanExpiresAt > nowAtUtc ? PlanExpiresAt.Value : nowAtUtc;
        var bonusExpiresAtUtc = expiresAtUtc.AddDays(days);
        BonusProExpiresAtUtc = bonusExpiresAtUtc;
    }

    public void SetStripeSubscription(
        string subscriptionId,
        DateTime expiresAt,
        SubscriptionInterval? interval = null,
        DateTime? eventCreatedAtUtc = null)
    {
        if (!TryAcceptStripeEvent(eventCreatedAtUtc, acceptEqualTimestamp: true))
            return;

        RememberPaidCoverage();
        if (StripeSubscriptionId != subscriptionId)
            StripeRenewalStoppedByDeletion = false;
        StripeSubscriptionId = subscriptionId;
        StorePaidCoverage(Enums.SubscriptionSource.Stripe, expiresAt, interval ?? StripeCoverageInterval, null);
        SelectPaidCoverage(Enums.SubscriptionSource.Stripe);
        if (PlayCoverageExpiresAtUtc <= _timeProvider.GetUtcNow().UtcDateTime)
        {
            PlayPurchaseToken = null;
            PlayRenewalStoppedByDeletion = false;
        }
    }

    public void SetPlaySubscription(string purchaseToken, DateTime expiresAt,
        SubscriptionInterval? interval = null, bool isInGracePeriod = false)
    {
        RememberPaidCoverage();
        LinkPlayPurchaseToken(purchaseToken);
        var reason = isInGracePeriod ? Enums.SubscriptionLapseReason.PaymentFailed : (SubscriptionLapseReason?)null;
        StorePaidCoverage(Enums.SubscriptionSource.GooglePlay, expiresAt, interval ?? PlayCoverageInterval, reason);
        SelectPaidCoverage(Enums.SubscriptionSource.GooglePlay);
    }

    public void LinkPlayPurchaseToken(string purchaseToken)
    {
        if (PlayPurchaseToken != purchaseToken)
            PlayRenewalStoppedByDeletion = false;
        PlayPurchaseToken = purchaseToken;
    }

    /// <summary>
    /// Claims a linked subscription observed renewing. Persist this intent before requesting
    /// cancellation so provider acceptance remains attributable after a local rollback or lost response.
    /// </summary>
    public void RecordDeletionStoppedRenewal(SubscriptionSource source, string providerIdentifier)
    {
        EnsureValidSubscriptionSource(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentifier);
        var expectedIdentifier = source == Enums.SubscriptionSource.Stripe ? StripeSubscriptionId : PlayPurchaseToken;
        if (!IsDeactivated || expectedIdentifier != providerIdentifier)
            throw new InvalidOperationException("Deletion cancellation must match a deactivated account's linked subscription.");

        if (source == Enums.SubscriptionSource.Stripe)
            StripeRenewalStoppedByDeletion = true;
        else
            PlayRenewalStoppedByDeletion = true;
    }

    public void RecordStripeRenewalRestored(string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (IsDeactivated || StripeSubscriptionId != subscriptionId || !StripeRenewalStoppedByDeletion)
            throw new InvalidOperationException("Only deletion-owned Stripe cancellation can be restored on an active account.");
        ReleaseStripeDeletionCancellation(subscriptionId);
    }

    public void ReleaseStripeDeletionCancellation(string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (StripeSubscriptionId != subscriptionId || !StripeRenewalStoppedByDeletion)
            throw new InvalidOperationException("Only linked Stripe deletion cancellation ownership can be released.");
        StripeRenewalStoppedByDeletion = false;
    }

    public void CancelStripeSubscription(
        SubscriptionLapseReason reason,
        DateTime? eventCreatedAtUtc = null)
    {
        EnsureValidLapseReason(reason);
        if (!TryAcceptStripeEvent(eventCreatedAtUtc, acceptEqualTimestamp: true))
            return;

        RememberPaidCoverage();
        StripeSubscriptionId = null;
        StripeRenewalStoppedByDeletion = false;
        StorePaidCoverage(Enums.SubscriptionSource.Stripe, null, null, null);
        if (SubscriptionSource == Enums.SubscriptionSource.Stripe)
            SelectPaidCoverage(Enums.SubscriptionSource.Stripe, reason);
    }

    public void CancelPlaySubscription(SubscriptionLapseReason reason)
    {
        EnsureValidLapseReason(reason);
        RememberPaidCoverage();
        PlayPurchaseToken = null;
        PlayRenewalStoppedByDeletion = false;
        StorePaidCoverage(Enums.SubscriptionSource.GooglePlay, null, null, null);
        if (SubscriptionSource == Enums.SubscriptionSource.GooglePlay)
            SelectPaidCoverage(Enums.SubscriptionSource.GooglePlay, reason);
    }

    public void RecordSubscriptionLapseReason(
        SubscriptionSource source,
        SubscriptionLapseReason reason,
        DateTime? eventCreatedAtUtc = null)
    {
        EnsureValidSubscriptionSource(source);
        EnsureValidLapseReason(reason);

        if (SubscriptionSource != source)
            return;

        if (source == Enums.SubscriptionSource.Stripe
            && !TryAcceptStripeEvent(eventCreatedAtUtc, acceptEqualTimestamp: false))
            return;

        SubscriptionLapseReason = reason;
        StorePaidCoverage(source, SubscriptionExpiresAtUtc, SubscriptionInterval, reason);
    }

    private void ClearSubscriptionEntitlement(SubscriptionLapseReason reason)
    {
        SubscriptionPlan = UserPlan.Free;
        SubscriptionExpiresAtUtc = null;
        SubscriptionInterval = null;
        SubscriptionSource = null;
        SubscriptionLapseReason = reason;
        SubscriptionEndedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
    }

    private static void EnsureValidLapseReason(SubscriptionLapseReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
    }

    private static void EnsureValidSubscriptionSource(SubscriptionSource source)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
    }

    private bool TryAcceptStripeEvent(DateTime? eventCreatedAtUtc, bool acceptEqualTimestamp)
    {
        if (!eventCreatedAtUtc.HasValue)
            return true;

        if (eventCreatedAtUtc.Value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Stripe event time must be UTC.", nameof(eventCreatedAtUtc));

        if (StripeSubscriptionEventCreatedAtUtc.HasValue
            && (acceptEqualTimestamp
                ? eventCreatedAtUtc.Value < StripeSubscriptionEventCreatedAtUtc.Value
                : eventCreatedAtUtc.Value <= StripeSubscriptionEventCreatedAtUtc.Value))
            return false;

        StripeSubscriptionEventCreatedAtUtc = eventCreatedAtUtc.Value;
        return true;
    }

    public void StartTrial(DateTime endsAt) => TrialEndsAt = endsAt;

    public void IncrementAiMessageCount(DateOnly userToday)
    {
        if (!AiMessagesLocalDate.HasValue || AiMessagesLocalDate.Value < userToday)
        {
            AiMessagesUsedToday = 0;
            AiMessagesLocalDate = userToday;
        }
        AiMessagesUsedToday++;
    }

    public int GetAiMessagesUsedToday(DateOnly userToday) =>
        AiMessagesLocalDate == userToday ? AiMessagesUsedToday : 0;

    public void RefundAiMessage(DateOnly reservedDate)
    {
        if (AiMessagesLocalDate == reservedDate && AiMessagesUsedToday > 0)
            AiMessagesUsedToday--;
    }

    public int GetAiMessagesUsedForQuota(DateOnly userToday) =>
        AiMessagesLocalDate >= userToday ? AiMessagesUsedToday : 0;

    public void SetGoogleTokens(string accessToken, string? refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        GoogleAccessToken = accessToken;
        if (refreshToken is not null)
            GoogleRefreshToken = refreshToken;

        if (GoogleCalendarAutoSyncStatus == Enums.GoogleCalendarAutoSyncStatus.ReconnectRequired)
        {
            GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.Idle;
            GoogleCalendarLastSyncError = null;
        }
    }

    public void MarkCalendarImported() => HasImportedCalendar = true;

    public void MarkImportPromptSeen() => HasSeenImportPrompt = true;

    public Result EnableCalendarAutoSync()
    {
        if (!HasProAccess)
            return Result.Failure(DomainErrors.CalendarAutoSyncProRequired);

        if (GoogleAccessToken is null)
            return Result.Failure(DomainErrors.CalendarAutoSyncNotConnected);

        GoogleCalendarAutoSyncEnabled = true;
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.Idle;
        GoogleCalendarLastSyncError = null;
        return Result.Success();
    }

    public void DisableCalendarAutoSync()
    {
        GoogleCalendarAutoSyncEnabled = false;
        GoogleCalendarLastSyncError = null;
    }

    public void MarkCalendarSyncReconnectRequired(string error)
    {
        GoogleCalendarAutoSyncEnabled = false;
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.ReconnectRequired;
        GoogleCalendarLastSyncError = error;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
    }

    public void MarkCalendarSyncSuccess(DateTime utcNow)
    {
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.Idle;
        GoogleCalendarLastSyncedAt = utcNow;
        GoogleCalendarLastSyncError = null;
    }

    public void MarkCalendarSyncTransientError(string error)
    {
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.TransientError;
        GoogleCalendarLastSyncError = error;
    }

    public void MarkCalendarSyncReconciled(DateTime utcNow)
    {
        GoogleCalendarSyncReconciledAt = utcNow;
    }

    public void Deactivate(DateTime scheduledDeletion)
    {
        IsDeactivated = true;
        ReminderPreferencesVersion++;
        DeactivatedAt = DateTime.UtcNow;
        ScheduledDeletionAt = scheduledDeletion;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
        GoogleCalendarAutoSyncEnabled = false;
    }

    public void CancelDeactivation()
    {
        PlayRenewalStoppedByDeletion = false;
        if (!IsLifetimePro && SubscriptionExpiresAtUtc.HasValue
            && SubscriptionExpiresAtUtc.Value <= _timeProvider.GetUtcNow().UtcDateTime)
            SubscriptionPlan = UserPlan.Free;

        IsDeactivated = false;
        ReminderPreferencesVersion++;
        DeactivatedAt = null;
        ScheduledDeletionAt = null;
    }

    public Result SetWeekStartDay(int day)
    {
        if (day is not (0 or 1))
            return Result.Failure(DomainErrors.InvalidWeekStartDay);

        WeekStartDay = day;
        ReminderPreferencesVersion++;
        return Result.Success();
    }

    public void SetClockFormat(bool uses24HourClock) => Uses24HourClockPreference = uses24HourClock;

    public void SetReferralCode(string code) => ReferralCode = code;

    /// <summary>
    /// Grants this user the single administrative role — the only code path that sets IsAdmin, which
    /// mints the JWT admin claim the "Admin" policy gates admin-only endpoints on. Idempotent. The
    /// first admin is bootstrapped by a direct DB update; this mutator is the seam a future in-app
    /// admin dashboard uses to grant admin.
    /// </summary>
    public void GrantAdmin() => IsAdmin = true;

    /// <summary>
    /// Grants permanent Pro entitlement that never expires and outranks any subscription or trial
    /// state (see <see cref="IsPro"/>). Idempotent. Lifetime Pro has no self-serve purchase flow; it is
    /// bootstrapped by a direct DB update, and this mutator is the domain seam a future comp/grant path
    /// (or admin dashboard) uses instead of writing the column directly.
    /// </summary>
    public void GrantLifetimePro() => IsLifetimePro = true;

    /// <summary>
    /// Records the user's explicit product-marketing-email consent decision (<c>true</c> = opted in,
    /// <c>false</c> = opted out) and stamps the decision time. A null value means never asked; once set
    /// it stays non-null so the one-time in-app consent prompt never shows again.
    /// </summary>
    public void SetMarketingConsent(bool consented)
    {
        MarketingEmailConsent = consented;
        MarketingConsentUpdatedAtUtc = DateTime.UtcNow;
    }

    public void SetReferredBy(Guid referrerUserId) => ReferredByUserId = referrerUserId;

    public void ExtendTrial(int days)
    {
        if (TrialEndsAt is null || TrialEndsAt.Value < DateTime.UtcNow)
            TrialEndsAt = DateTime.UtcNow.AddDays(days);
        else
            TrialEndsAt = TrialEndsAt.Value.AddDays(days);
    }

    public void SetReferralCoupon(string? couponId) => ReferralCouponId = couponId;

    public void AddXp(int amount)
    {
        if (amount <= 0) return;
        TotalXp += amount;
    }

    public void SetLevel(int level)
    {
        if (level < 1) return;
        Level = level;
    }

    public void UpdateStreak(DateOnly today)
    {
        if (LastActiveDate == today)
            return;
        if (LastActiveDate == today.AddDays(-1))
        {
            CurrentStreak++;
        }
        else
        {
            CurrentStreak = 1;
        }

        LastActiveDate = today;

        if (CurrentStreak > LongestStreak)
            LongestStreak = CurrentStreak;
    }

    public void SetStreakState(int currentStreak, int longestStreak, DateOnly? lastActiveDate)
    {
        var normalizedStreak = Math.Max(0, currentStreak);
        if (normalizedStreak < CurrentStreak)
        {
            if (PreGapFreezeAwardStreak is null)
            {
                PreGapFreezeAwardStreak = LastFreezeAwardStreak;
                PreGapLastActiveDate = LastActiveDate;
            }

            LastFreezeAwardStreak = 0;
        }
        CurrentStreak = normalizedStreak;
        LongestStreak = Math.Max(CurrentStreak, longestStreak);
        LastActiveDate = lastActiveDate;
    }

    public void RestoreStreakAfterGapRepair(
        int currentStreak, int longestStreak, DateOnly? lastActiveDate, DateOnly precedingDate,
        int? preGapStreak)
    {
        var earnedBeforeTheGap = GetEligibleFreezeAwardStreak(
            preGapStreak.HasValue ? Math.Min(preGapStreak.Value, currentStreak) : currentStreak,
            DefaultStreakDaysPerFreeze);
        var savedCursor = PreGapLastActiveDate == precedingDate && PreGapFreezeAwardStreak.HasValue
            ? PreGapFreezeAwardStreak.Value
            : (int?)null;
        var awardCursor = savedCursor.HasValue
            ? (preGapStreak.HasValue ? Math.Min(savedCursor.Value, earnedBeforeTheGap) : savedCursor.Value)
            : earnedBeforeTheGap;
        SetStreakState(currentStreak, longestStreak, lastActiveDate);
        LastFreezeAwardStreak = awardCursor;
        PreGapFreezeAwardStreak = null;
        PreGapLastActiveDate = null;
    }

    public bool AwardStreakFreezeIfEligible(
        int maxAccumulated = 3, int daysPerFreeze = DefaultStreakDaysPerFreeze)
    {
        if (CurrentStreak < daysPerFreeze)
            return false;

        var eligibleMilestone = GetEligibleFreezeAwardStreak(CurrentStreak, daysPerFreeze);

        if (StreakFreezesAccumulated >= maxAccumulated)
        {
            LastFreezeAwardStreak = eligibleMilestone;
            return false;
        }

        if (eligibleMilestone <= LastFreezeAwardStreak)
            return false;

        var milestonesCrossed = (eligibleMilestone - LastFreezeAwardStreak) / daysPerFreeze;
        var awardable = Math.Min(milestonesCrossed, maxAccumulated - StreakFreezesAccumulated);

        StreakFreezesAccumulated += awardable;
        LastFreezeAwardStreak += awardable * daysPerFreeze;
        return awardable > 0;
    }

    private static int GetEligibleFreezeAwardStreak(int currentStreak, int daysPerFreeze) =>
        currentStreak - (currentStreak % daysPerFreeze);

    public Result ConsumeStreakFreezes(int count)
    {
        if (count <= 0)
            return Result.Failure(DomainErrors.InvalidStreakGap);
        if (StreakFreezesAccumulated < count)
            return Result.Failure(DomainErrors.InsufficientStreakFreezes);

        StreakFreezesAccumulated -= count;
        return Result.Success();
    }

    public Result ConsumeStreakFreeze()
    {
        if (StreakFreezesAccumulated <= 0)
            return Result.Failure(DomainErrors.NoStreakFreezesAccumulated);
        StreakFreezesAccumulated--;
        return Result.Success();
    }

    /// <summary>
    /// Resets progress and integration state (onboarding, gamification, calendar) to defaults while
    /// preserving identity, preferences, subscription, and metered AI usage. The daily message
    /// quota and ad reward allowances are kept so an account reset cannot refill the AI paygate.
    /// </summary>
    public void ResetAccount()
    {
        ConversationHistoryClearedAtUtc = DateTime.UtcNow;
        ReminderPreferencesVersion++;
        HasCompletedOnboarding = false;
        HasCompletedTour = false;
        HasCreatedFirstHabit = false;
        HasLoggedFirstHabit = false;
        HasTriedAstra = false;
        HasCompletedOnboardingChecklist = false;
        TotalXp = 0;
        Level = 1;
        CurrentStreak = 0;
        LongestStreak = 0;
        LastActiveDate = null;
        StreakFreezesAccumulated = 0;
        LastFreezeAwardStreak = 0;
        PreGapFreezeAwardStreak = null;
        PreGapLastActiveDate = null;
        HasImportedCalendar = false;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
        GoogleCalendarAutoSyncEnabled = false;
        GoogleCalendarSelectedIds = null;
        GoogleCalendarAutoSyncStatus = null;
        GoogleCalendarLastSyncedAt = null;
        GoogleCalendarLastSyncError = null;
        GoogleCalendarSyncReconciledAt = null;
    }
}
