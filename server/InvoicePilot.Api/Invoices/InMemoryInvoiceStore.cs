namespace InvoicePilot.Api.Invoices;

public sealed class InMemoryInvoiceStore : IInvoiceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Invoice> _invoices = new();
    private readonly Dictionary<string, byte[]> _files = new();

    public Task<(Invoice Invoice, bool Created)> AddAsync(Invoice invoice, byte[] content, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _invoices.Values.FirstOrDefault(i => i.File.Sha256 == invoice.File.Sha256);
            if (existing is not null) return Task.FromResult((existing, false));
            if (_invoices.ContainsKey(invoice.Id)) throw new InvalidOperationException($"Invoice {invoice.Id} already exists.");

            _invoices[invoice.Id] = invoice;
            _files[invoice.Id] = content.ToArray();
            return Task.FromResult((invoice, true));
        }
    }

    public Task<Invoice?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_invoices.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<Invoice>> ListAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Invoice>>(
                _invoices.Values
                    .OrderByDescending(i => i.ReceivedAt)
                    .ThenByDescending(i => i.Id, StringComparer.Ordinal)
                    .Take(limit)
                    .ToList());
        }
    }

    public Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Invoice>>(
                _invoices.Values.Where(i => i.Status == status).OrderBy(i => i.ReceivedAt).ToList());
        }
    }

    public Task<byte[]?> GetFileAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_files.TryGetValue(id, out var bytes) ? bytes.ToArray() : null);
    }

    public Task<Invoice?> TryUpdateAsync(Invoice invoice, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_invoices.TryGetValue(invoice.Id, out var current) || current.Version != invoice.Version)
            {
                return Task.FromResult<Invoice?>(null);
            }
            var saved = invoice with { Version = invoice.Version + 1 };
            _invoices[invoice.Id] = saved;
            return Task.FromResult<Invoice?>(saved);
        }
    }
}
