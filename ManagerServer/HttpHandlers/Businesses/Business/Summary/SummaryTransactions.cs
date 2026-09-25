using ManagerServer.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ManagerServer.Globalization;
using ManagerServer.Helpers;

namespace ManagerServer.HttpHandlers.Businesses.Business.Summary
{
    [ProtoContract]
    [Title(nameof(Strings.Summary), nameof(Strings.Transactions))]
    [Guide("The `Summary` - `Transactions` screen provides a consolidated view of all recent transactions across your business. This central location allows you to monitor all financial activity without navigating to individual account screens.")]
    [Guide("From this screen, you can quickly review transactions from various sources including sales invoices, purchase invoices, payments, receipts, journal entries, and other transaction types. Each transaction displays key information such as date, description, account affected, and amount.")]
    [Guide("Use the search and filter options to find specific transactions or narrow down the list by date range, transaction type, or account. You can click on any transaction to view its full details or make corrections if needed.")]
    [Guide("This summary view is particularly useful for daily review of business activities, identifying unusual transactions, and ensuring all entries have been recorded correctly. The real-time nature of this list means any transaction entered elsewhere in the system will immediately appear here.")]
    [Columns]
    internal sealed class SummaryTransactions : BaseGeneralLedgerTransactionsInheritable
    {
        protected override void OnAfterHeader(Context context)
        {
            if (From.HasValue)
            {
                using (Div(@class: "card-header bg-yellow-50"))
                {
                    using (Div(@class: "flex gap-2 items-center"))
                    {
                        I(@class: "fas fa-fw fa-filter text-neutral-400");
                        using (Span(@class: "font-semibold")) Write(Strings.Filter + ":");
                        Write(" " + string.Format(
                            Strings.For_the_period_from_XXX_to_XXX,
                            From.Value.ToLocalShortDisplayString(),
                            To.ToLocalShortDisplayString()));
                    }
                }
            }

            base.OnAfterHeader(context);
        }
    }
}
