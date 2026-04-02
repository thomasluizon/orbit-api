namespace Orbit.Application.Subscriptions;

public record CheckoutResponse(string Url);

public record PortalResponse(string Url);

public record SubscriptionStatusResponse(
    string Plan,
    bool HasProAccess,
    bool IsTrialActive,
    DateTime? TrialEndsAt,
    DateTime? PlanExpiresAt,
    int AiMessagesUsed,
    int AiMessagesLimit,
    bool IsLifetimePro,
    string? SubscriptionInterval);

public record AdRewardResponse(int BonusMessagesGranted, int TotalBonusMessages, int NewLimit);

public record PlanPriceDto(long UnitAmount, string Currency);

public record PlansResponse(
    PlanPriceDto Monthly,
    PlanPriceDto Yearly,
    int SavingsPercent,
    int? CouponPercentOff,
    string Currency);

public record PaymentMethodDto(string Brand, string Last4, int ExpMonth, int ExpYear);

public record InvoiceDto(
    string Id,
    DateTime Date,
    long AmountPaid,
    string Currency,
    string Status,
    string? HostedInvoiceUrl,
    string? InvoicePdf,
    string BillingReason);

public record BillingDetailsResponse(
    string Status,
    DateTime CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    string Interval,
    long AmountPerPeriod,
    string Currency,
    PaymentMethodDto? PaymentMethod,
    IReadOnlyList<InvoiceDto> RecentInvoices);
