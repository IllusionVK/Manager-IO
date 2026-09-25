using ManagerServer.Globalization;
using ManagerServer.Helpers;
using ManagerServer.Model;
using ManagerServer.Model.Enums;
using Microsoft.AspNetCore.Http;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ManagerServer.HttpHandlers.Businesses.Business.Graphs
{
    internal sealed partial class Graphs
    {
        [ProtoMember(3)] public string CustomerPeriodMode { get; set; }
        [ProtoMember(4)] public DateTime? CustomerFrom { get; set; }
        [ProtoMember(5)] public DateTime? CustomerTo { get; set; }

        private Graphs WithPeriod(DateTime from, DateTime to) => new Graphs
        {
            Business = Business,
            From = from,
            To = to,
            CustomerPeriodMode = CustomerPeriodMode,
            CustomerFrom = CustomerFrom,
            CustomerTo = CustomerTo,
        };

        private void UpdateCustomerPeriod(IFormCollection form)
        {
            var mode = form[nameof(CustomerPeriodMode)].ToString();
            if (mode != "all" && mode != "custom") mode = "selected";

            DateTime? customFrom = CustomerFrom;
            DateTime? customTo = CustomerTo;
            if (mode == "custom")
            {
                if (!DateTime.TryParseExact(form[nameof(CustomerFrom)].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                    || !DateTime.TryParseExact(form[nameof(CustomerTo)].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
                {
                    Response.Redirect(this.ToUrl());
                    return;
                }
                if (to < from) (from, to) = (to, from);
                customFrom = from;
                customTo = to;
            }

            Response.Redirect(new Graphs
            {
                Business = Business,
                From = From,
                To = To,
                CustomerPeriodMode = mode,
                CustomerFrom = customFrom,
                CustomerTo = customTo,
            }.ToUrl());
        }

        private void RenderCustomerSpending(DateTime selectedFrom, DateTime selectedTo)
        {
            var mode = CustomerPeriodMode == "all" || CustomerPeriodMode == "custom" ? CustomerPeriodMode : "selected";
            var from = mode == "custom" && CustomerFrom.HasValue ? CustomerFrom.Value.Date : selectedFrom;
            var to = mode == "custom" && CustomerTo.HasValue ? CustomerTo.Value.Date : selectedTo;
            if (to < from) (from, to) = (to, from);

            var currency = ApplicationData.Businesses.Get(Business).Single<BaseCurrency>();
            var allSales = new ManagerServer.Query.GeneralLedger.GeneralLedger(Business)
                .Where(x => x.Customer != null && x.GeneralLedgerAccount.IsAccountsReceivable
                    && (x.Transaction is SalesInvoice || x.Transaction is CreditNote))
                .Select(x => new CustomerSale(x.Customer.Key, x.Customer.NameWithCode, x.Date.Date, x.BaseAmount))
                .ToArray();
            var sales = mode == "all" ? allSales : allSales.Where(x => x.Date >= from && x.Date <= to).ToArray();
            var totals = sales.GroupBy(x => new { x.CustomerKey, x.CustomerName })
                .Select(x => new CustomerTotal(x.Key.CustomerKey, x.Key.CustomerName, x.Sum(y => y.Amount)))
                .Where(x => x.Amount != 0m)
                .OrderByDescending(x => x.Amount)
                .ThenBy(x => x.Name)
                .ToArray();
            var periods = new List<(DateTime From, DateTime To)> { (from, to) };
            if (mode == "all" && allSales.Length > 0)
            {
                var firstSale = allSales.Min(x => x.Date);
                var lastSale = allSales.Max(x => x.Date);
                while (periods[0].From > firstSale)
                    periods.Insert(0, ShiftPeriod(periods[0].From, periods[0].To, -1));
                while (periods[^1].To < lastSale)
                    periods.Add(ShiftPeriod(periods[^1].From, periods[^1].To, 1));
                periods = periods.Where(period => allSales.Any(sale => sale.Date >= period.From && sale.Date <= period.To)).ToList();
            }
            else
            {
                for (var i = 0; i < 5; i++)
                    periods.Insert(0, ShiftPeriod(periods[0].From, periods[0].To, -1));
            }

            var barSales = allSales.Where(x => x.Date >= periods[0].From && x.Date <= periods[^1].To).ToArray();
            var barTotals = barSales.GroupBy(x => new { x.CustomerKey, x.CustomerName })
                .Select(x => new CustomerTotal(x.Key.CustomerKey, x.Key.CustomerName, x.Sum(y => y.Amount)))
                .OrderByDescending(x => x.Amount)
                .ThenBy(x => x.Name)
                .ToArray();

            using (Div(id: "customer-spending", @class: "flex flex-col gap-4 mt-8"))
            {
                using (Div())
                {
                    using (H2(@class: "text-lg font-bold")) Write("Customer spending");
                    using (Div(@class: "text-sm text-[var(--muted-foreground)]"))
                        Write("Sales invoice totals including tax, less credit notes, in the business's base currency.");
                }

                using (Form(action: this.ToUrl(), method: "POST", @class: "flex flex-wrap items-end gap-2 print:hidden",
                    onsubmit: "sessionStorage.setItem('graphsCustomerScrollY', String(window.scrollY))"))
                {
                    Write("<input type=\"hidden\" name=\"GraphForm\" value=\"customer\">");
                    using (Label(@class: "flex flex-col gap-1 mb-0 text-sm font-semibold"))
                    {
                        using (Span()) Write("Period");
                        using (Select(name: nameof(CustomerPeriodMode), id: "customerPeriodMode", @class: "form-select", onchange: "document.getElementById('customerCustomDates').classList.toggle('hidden', this.value !== 'custom')"))
                        {
                            Option(value: "selected", text: "Use selected period", selected: mode == "selected");
                            Option(value: "all", text: "Show all data", selected: mode == "all");
                            Option(value: "custom", text: "Use custom period", selected: mode == "custom");
                        }
                    }

                    using (Div(id: "customerCustomDates", @class: $"flex flex-wrap items-end gap-2 {(mode == "custom" ? "" : "hidden")}"))
                    {
                        using (Label(@class: "flex flex-col gap-1 mb-0 text-sm font-semibold"))
                        {
                            using (Span()) Write(Strings.FromDate);
                            InputDate(name: nameof(CustomerFrom), value: CustomerFrom ?? selectedFrom, @class: "form-control");
                        }
                        using (Label(@class: "flex flex-col gap-1 mb-0 text-sm font-semibold"))
                        {
                            using (Span()) Write(Strings.ToDate);
                            InputDate(name: nameof(CustomerTo), value: CustomerTo ?? selectedTo, @class: "form-control");
                        }
                    }
                    using (Button(type: "submit", @class: "btn")) Write(Strings.ApplyChanges);
                }
                using (Script()) Write("""
                    (() => {
                        const savedY = sessionStorage.getItem('graphsCustomerScrollY');
                        if (savedY === null) return;
                        sessionStorage.removeItem('graphsCustomerScrollY');
                        window.addEventListener('load', () => window.scrollTo(0, Number(savedY)), { once: true });
                    })();
                    """);

                if (sales.Length > 0)
                {
                    var displayFrom = mode == "all" ? sales.Min(x => x.Date) : from;
                    var displayTo = mode == "all" ? sales.Max(x => x.Date) : to;
                    using (Div(@class: "text-sm text-[var(--muted-foreground)]"))
                        Write(string.Format(Strings.For_the_period_from_XXX_to_XXX, displayFrom.ToLocalShortDisplayString(), displayTo.ToLocalShortDisplayString()));
                }

                RenderCustomerPie(totals, currency);
                RenderCustomerStackedBars(barSales, barTotals, periods, mode == "all", currency);
            }
        }

        private void RenderCustomerPie(CustomerTotal[] totals, BaseCurrency currency)
        {
            var positive = totals.Where(x => x.Amount > 0m).ToArray();
            using (Div(@class: "flex flex-col gap-3"))
            {
                using (H3(@class: "text-base font-bold")) Write("Share of customer sales");
                if (positive.Length == 0)
                {
                    using (Div(@class: "border border-[var(--border)] rounded p-8 text-center text-[var(--muted-foreground)]"))
                        Write("There are no positive customer totals to show in a pie chart.");
                    return;
                }

                var slices = positive.Take(11).Select(x => (x.Name, x.Amount)).ToList();
                if (positive.Length > 11) slices.Add(("Other customers", positive.Skip(11).Sum(x => x.Amount)));
                if (positive.Length > 11)
                    using (Div(@class: "text-sm text-[var(--muted-foreground)]")) Write("The largest 11 customers are shown separately; the rest are combined.");
                if (totals.Any(x => x.Amount < 0m))
                    using (Div(@class: "text-sm text-[var(--muted-foreground)]")) Write("Customers with a net credit are shown below zero in the bar chart and are excluded from the pie chart.");
                using (Div(@class: "border border-[var(--border)] rounded bg-[var(--card)] p-3", style: "height:390px"))
                    Write("<canvas id=\"customerSpendingPie\" role=\"img\" aria-label=\"Customer sales pie chart\">Customer sales pie chart</canvas>");

                var items = slices.Select(x => new { name = x.Name, amount = x.Amount, formatted = x.Amount.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short) }).ToArray();
                using (Script()) Write(BuildPieScript(items, currency));
            }
        }

        private void RenderCustomerStackedBars(CustomerSale[] sales, CustomerTotal[] totals,
            IReadOnlyList<(DateTime From, DateTime To)> periods, bool allData, BaseCurrency currency)
        {
            var customers = totals.Select(x => x.CustomerKey).ToArray();
            var bucketTotals = sales.Select(sale => (Sale: sale, Index: FindPeriod(sale.Date, periods)))
                .Where(x => x.Index >= 0)
                .GroupBy(x => (x.Sale.CustomerKey, x.Index))
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Sale.Amount));
            var datasets = periods.Select((period, index) => new
            {
                label = $"{period.From.ToLocalShortDisplayString()} – {period.To.ToLocalShortDisplayString()}",
                color = CustomerPeriodColors[index % CustomerPeriodColors.Length],
                data = customers.Select(customer => bucketTotals.TryGetValue((customer, index), out var amount) ? amount : 0m).ToArray(),
            }).ToArray();

            using (Div(@class: "flex flex-col gap-3"))
            {
                using (H3(@class: "text-base font-bold")) Write("Customer sales by period");
                using (Div(@class: "text-sm text-[var(--muted-foreground)]"))
                    Write(allData
                        ? "Each column is a customer; the coloured stacks cover all data in intervals matching the date range at the top of this page."
                        : "Each column is a customer; the coloured stacks show the chosen period and five preceding periods of the same length.");
                if (sales.Length == 0)
                {
                    using (Div(@class: "border border-[var(--border)] rounded p-8 text-center text-[var(--muted-foreground)]"))
                        Write("There are no customer sales for these periods.");
                    return;
                }
                using (Div(@class: "border border-[var(--border)] rounded bg-[var(--card)] p-3 overflow-x-auto"))
                {
                    var width = Math.Max(900, totals.Length * 90);
                    using (Div(style: $"width:{width}px;height:440px"))
                        Write("<canvas id=\"customerSpendingBars\" role=\"img\" aria-label=\"Customer sales stacked bar chart\">Customer sales stacked bar chart</canvas>");
                }
                var names = totals.Select(x => x.Name).ToArray();
                using (Script()) Write(BuildStackedBarScript(names, datasets, currency));
            }
        }

        private static int FindPeriod(DateTime date, IReadOnlyList<(DateTime From, DateTime To)> periods)
        {
            for (var i = 0; i < periods.Count; i++)
                if (date >= periods[i].From && date <= periods[i].To) return i;
            return -1;
        }

        private static string BuildPieScript(object items, BaseCurrency currency)
        {
            var json = JsonSerializer.Serialize(items);
            return $$"""
                (() => {
                    const items = {{json}};
                    {{BuildCurrencyFormatterScript(currency)}}
                    const colors = {{JsonSerializer.Serialize(CustomerChartColors)}};
                    const total = items.reduce((sum, item) => sum + item.amount, 0);
                    new Chart(document.getElementById('customerSpendingPie'), {
                        type: 'pie',
                        plugins: [ChartDataLabels],
                        data: {
                            labels: items.map(item => item.name),
                            datasets: [{ data: items.map(item => item.amount), backgroundColor: items.map((_, index) => colors[index % colors.length]) }]
                        },
                        options: {
                            responsive: true,
                            maintainAspectRatio: false,
                            plugins: {
                                legend: { position: 'right', labels: { boxWidth: 14 } },
                                datalabels: {
                                    display: context => items[context.dataIndex].amount / total >= 0.08,
                                    formatter: value => [formatCurrency(value, true), `${(value / total * 100).toFixed(1)}%`],
                                    anchor: 'center', align: 'center', clamp: true,
                                    color: '#ffffff', textStrokeColor: 'rgba(0,0,0,0.65)', textStrokeWidth: 2,
                                    font: { weight: 'bold', size: 11 }, textAlign: 'center'
                                },
                                tooltip: { callbacks: { label: item => `${item.label}: ${items[item.dataIndex].formatted} (${(item.parsed / total * 100).toFixed(1)}%)` } }
                            }
                        }
                    });
                })();
                """;
        }

        private static string BuildStackedBarScript(object names, object datasets, BaseCurrency currency)
        {
            var namesJson = JsonSerializer.Serialize(names);
            var datasetsJson = JsonSerializer.Serialize(datasets);
            return $$"""
                (() => {
                    const periods = {{datasetsJson}};
                    {{BuildCurrencyFormatterScript(currency)}}
                    const maxSegment = Math.max(0, ...periods.flatMap(period => period.data.map(amount => Math.abs(amount))));
                    new Chart(document.getElementById('customerSpendingBars'), {
                        type: 'bar',
                        plugins: [ChartDataLabels],
                        data: {
                            labels: {{namesJson}},
                            datasets: periods.map(period => ({ label: period.label, data: period.data, backgroundColor: period.color, stack: 'sales' }))
                        },
                        options: {
                            responsive: true,
                            maintainAspectRatio: false,
                            plugins: {
                                legend: { position: 'bottom' },
                                datalabels: {
                                    display: context => {
                                        const amount = context.dataset.data[context.dataIndex];
                                        return amount !== 0 && Math.abs(amount) >= maxSegment * 0.07 ? 'auto' : false;
                                    },
                                    formatter: value => formatCurrency(value, true),
                                    anchor: 'center', align: 'center', clamp: true, clip: true,
                                    color: '#ffffff', textStrokeColor: 'rgba(0,0,0,0.7)', textStrokeWidth: 2,
                                    font: { weight: 'bold', size: 10 }, padding: 1
                                },
                                tooltip: { callbacks: { label: item => `${item.dataset.label}: ${formatCurrency(item.parsed.y)}` } }
                            },
                            scales: {
                                x: { stacked: true, ticks: { maxRotation: 50, minRotation: 30, autoSkip: false } },
                                y: { stacked: true, title: { display: true, text: `Sales (${currencyCode})` }, ticks: { callback: value => formatCurrency(value, true) } }
                            }
                        }
                    });
                })();
                """;
        }

        private static readonly string[] CustomerChartColors =
        [
            "#2563eb", "#f97316", "#16a34a", "#a855f7", "#e11d48", "#0891b2",
            "#ca8a04", "#4f46e5", "#db2777", "#0d9488", "#7c3aed", "#ea580c",
        ];

        private static readonly string[] CustomerPeriodColors =
        [
            "#48a7e0", "#289187", "#6f2baa", "#193e6f", "#c72c33", "#f0b719", "#48af2e", "#631c56", "#2c40c7", "#00cbee",
        ];

        private sealed record CustomerSale(Guid CustomerKey, string CustomerName, DateTime Date, decimal Amount);
        private sealed record CustomerTotal(Guid CustomerKey, string Name, decimal Amount);
    }
}
