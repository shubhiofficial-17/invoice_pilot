namespace InvoicePilot.Api.Invoices;

public interface IInvoiceStore
{
    /// <summary>
    /// Stores a new invoice and its file. If a file with the same SHA-256 already exists,
    /// nothing is stored and the existing invoice is returned with Created = false.
    /// </summary>
    Task<(Invoice Invoice, bool Created)> AddAsync(Invoice invoice, byte[] content, CancellationToken ct = default);

    Task<Invoice?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Invoice>> ListAsync(int limit = 100, CancellationToken ct = default);

    Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default);

    Task<byte[]?> GetFileAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Saves <paramref name="invoice"/> only if the stored version still equals invoice.Version.
    /// Returns the saved invoice (with Version + 1), or null if someone else wrote first.
    /// </summary>
    Task<Invoice?> TryUpdateAsync(Invoice invoice, CancellationToken ct = default);
}
