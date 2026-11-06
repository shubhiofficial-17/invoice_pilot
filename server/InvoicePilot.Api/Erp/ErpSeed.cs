namespace InvoicePilot.Api.Erp;

/// <summary>
/// Demo ERP data. Each record is here for a reason: later milestones (matching,
/// the agent, the evaluation set) build invoices against it, so the scenarios
/// below must stay stable.
/// </summary>
public static class ErpSeed
{
    public const string Buyer = "priya.nair@invoicepilot.example";
    public const string FacilitiesBuyer = "rahul.mehta@invoicepilot.example";

    public static ErpData Create() => new(Suppliers(), PurchaseOrders(), Receipts(), PostedInvoices());

    private static List<Supplier> Suppliers() =>
    [
        new("SUP-ACME", "Acme Industrial Supplies Pvt Ltd", "27AAACA1234F1Z5", "INR", 30, "ar@acme-industrial.example", SupplierStatus.Active),
        new("SUP-BRIGHT", "Bright Office Co", "29AABCB5678G1Z2", "INR", 45, "billing@brightoffice.example", SupplierStatus.Active),
        new("SUP-NORTH", "Northwind Electronics Inc", "US-84-1234567", "USD", 60, "receivables@northwind.example", SupplierStatus.Active),
        new("SUP-ZEN", "Zen Logistics LLP", "07AAFFZ9012H1Z8", "INR", 15, "accounts@zenlogistics.example", SupplierStatus.Active),
        // Blocked: no new invoice from this supplier may be approved.
        new("SUP-OLD", "Oldfield Metals", "24AAGCO3456J1Z1", "INR", 30, "finance@oldfield.example", SupplierStatus.Blocked),
    ];

    private static List<PurchaseOrder> PurchaseOrders() =>
    [
        // Fully received. Clean match when invoiced as ordered.
        new("PO-1001", "SUP-ACME", "INR", Buyer, PoStatus.Open, new DateOnly(2026, 10, 1),
        [
            new(1, "BOLT-M8", "Steel bolt M8 x 40", 1000, 4.50m, 0.18m),
            new(2, "NUT-M8", "Hex nut M8", 1000, 1.20m, 0.18m),
        ]),
        // Fully received; two tax rates on one PO.
        new("PO-1002", "SUP-BRIGHT", "INR", FacilitiesBuyer, PoStatus.Open, new DateOnly(2026, 10, 3),
        [
            new(1, "PAPER-A4", "A4 paper, 500-sheet ream", 50, 320.00m, 0.12m),
            new(2, "TONER-85A", "Laser toner cartridge 85A", 10, 4500.00m, 0.18m),
        ]),
        // USD, no tax. Already invoiced once (NW-55012 below).
        new("PO-1003", "SUP-NORTH", "USD", Buyer, PoStatus.Open, new DateOnly(2026, 9, 20),
        [
            new(1, "MOD-RF900", "RF communication module 900 MHz", 200, 38.00m, 0m),
        ]),
        // A service: one unit of freight.
        new("PO-1004", "SUP-ZEN", "INR", FacilitiesBuyer, PoStatus.Open, new DateOnly(2026, 10, 10),
        [
            new(1, "SVC-FREIGHT", "Inbound freight, Pune to Bengaluru, October", 1, 25000.00m, 0.18m),
        ]),
        // Only 300 of 500 received so far: invoicing all 500 is a quantity exception.
        new("PO-1005", "SUP-ACME", "INR", Buyer, PoStatus.Open, new DateOnly(2026, 10, 12),
        [
            new(1, "GLOVE-NIT", "Nitrile safety gloves, box of 100", 500, 85.00m, 0.12m),
        ]),
        // Supplier is blocked.
        new("PO-1006", "SUP-OLD", "INR", Buyer, PoStatus.Closed, new DateOnly(2026, 8, 14),
        [
            new(1, "SHEET-SS", "Stainless steel sheet 2 mm", 40, 2150.00m, 0.18m),
        ]),
        // Cancelled: nothing may be invoiced against it.
        new("PO-1007", "SUP-BRIGHT", "INR", FacilitiesBuyer, PoStatus.Cancelled, new DateOnly(2026, 10, 5),
        [
            new(1, "CHAIR-ERG", "Ergonomic office chair", 12, 9800.00m, 0.18m),
        ]),
        // Ordered but nothing received yet.
        new("PO-1008", "SUP-NORTH", "USD", Buyer, PoStatus.Open, new DateOnly(2026, 10, 20),
        [
            new(1, "MOD-LTE", "LTE communication module", 100, 52.00m, 0m),
            new(2, "ANT-EXT", "External antenna", 100, 6.50m, 0m),
        ]),
    ];

    private static List<GoodsReceipt> Receipts() =>
    [
        new("GRN-5001", "PO-1001", new DateOnly(2026, 10, 8), [new(1, 1000), new(2, 1000)]),
        new("GRN-5002", "PO-1002", new DateOnly(2026, 10, 9), [new(1, 50), new(2, 10)]),
        // PO-1003 arrived in two deliveries.
        new("GRN-5003", "PO-1003", new DateOnly(2026, 9, 28), [new(1, 120)]),
        new("GRN-5006", "PO-1003", new DateOnly(2026, 10, 6), [new(1, 80)]),
        new("GRN-5004", "PO-1004", new DateOnly(2026, 10, 31), [new(1, 1)]),
        new("GRN-5005", "PO-1005", new DateOnly(2026, 10, 22), [new(1, 300)]),
    ];

    private static List<PostedInvoice> PostedInvoices() =>
    [
        new("SUP-ACME", "AC/2026/0311", new DateOnly(2026, 9, 5), "PO-0990", "INR", 41300.00m, PostedInvoiceStatus.Paid),
        new("SUP-BRIGHT", "BOC-7781", new DateOnly(2026, 9, 18), "PO-0987", "INR", 17920.00m, PostedInvoiceStatus.Paid),
        // A resubmitted NW-55012 is a duplicate.
        new("SUP-NORTH", "NW-55012", new DateOnly(2026, 10, 7), "PO-1003", "USD", 7600.00m, PostedInvoiceStatus.Posted),
        new("SUP-ZEN", "ZL-0921", new DateOnly(2026, 9, 30), "PO-0995", "INR", 29500.00m, PostedInvoiceStatus.Paid),
    ];
}
