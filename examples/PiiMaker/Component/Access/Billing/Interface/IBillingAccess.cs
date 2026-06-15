namespace PiiMaker.Access.Billing.Interface;

/// <summary>
/// Resource-access for the payment provider (a SoEx component contract; Task-returning). Charging is
/// at-least-once — the framework's idempotency keeps a redelivered charge from billing twice. Invoices are
/// a lawful-basis carve-out, written outward and retained through erasure, never returned as the result.
/// </summary>
public interface IBillingAccess
{
    // period is long: a component operation's arguments round-trip through the transport serializer, which
    // materialises integers as Int64 — an int parameter would fail to bind on dispatch.

    /// <summary>Attempts to charge the subscriber for a period; returns whether it succeeded.</summary>
    Task<bool> ChargeAsync(string subscriberId, long period);

    /// <summary>Records an invoice for a successful charge.</summary>
    Task InvoiceAsync(string subscriberId, long period);
}
