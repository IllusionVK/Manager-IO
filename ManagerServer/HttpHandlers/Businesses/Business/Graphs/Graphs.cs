using ManagerServer.Attributes;
using ManagerServer.Globalization;
using ManagerServer.Helpers;
using ManagerServer.Model.Enums;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ManagerServer.HttpHandlers.Businesses.Business.Graphs
{
    [ProtoContract]
    [Title(nameof(Strings.Graphs))]
    [Guide("The `Graphs` tab provides visual dashboards for reviewing the financial performance of your business.")]
    [Guide("Choose a date range at the top of the page. Use the previous and next buttons to move by the same reporting interval. The profit margin chart also shows the five preceding periods.")]
    internal sealed partial class Graphs : BusinessTemplate
    {
        [ProtoMember(1)] public DateTime? From { get; set; }
        [ProtoMember(2)] public DateTime? To { get; set; }

        protected override void InnerGet2()
        {
            var (from, to) = GetPeriod();
            var previous = ShiftPeriod(from, to, -1);
            var next = ShiftPeriod(from, to, 1);

            using (Div(@class: "card"))
            {
                using (Div(@class: "card-header"))
                {
                    using (Div(@class: "flex flex-col gap-4"))
                    {
                        using (Div(@class: "card-title")) Write(Strings.Graphs);

                        using (Div(@class: "flex flex-wrap items-end gap-2 print:hidden"))
                        {
                            using (A(href: WithPeriod(previous.From, previous.To).ToUrl(), @class: "btn", title: "Previous period"))
                            {
                                I(@class: "fas fa-chevron-left");
                            }

                            using (Form(action: this.ToUrl(), method: "POST", @class: "flex flex-wrap items-end gap-2"))
                            {
                                Write("<input type=\"hidden\" name=\"GraphForm\" value=\"dashboard\">");
                                using (Label(@class: "flex flex-col gap-1 mb-0 text-sm font-semibold"))
                                {
                                    using (Span()) Write(Strings.FromDate);
                                    InputDate(name: nameof(From), value: from, @class: "form-control");
                                }
                                using (Label(@class: "flex flex-col gap-1 mb-0 text-sm font-semibold"))
                                {
                                    using (Span()) Write(Strings.ToDate);
                                    InputDate(name: nameof(To), value: to, @class: "form-control");
                                }
                                using (Button(type: "submit", @class: "btn")) Write(Strings.ApplyChanges);
                            }

                            using (A(href: WithPeriod(next.From, next.To).ToUrl(), @class: "btn", title: "Next period"))
                            {
                                I(@class: "fas fa-chevron-right");
                            }
                        }
                    }
                }

                using (Div(@class: "card-inset"))
                {
                    var current = RenderIncomeStatementSankey(from, to);
                    RenderProfitMarginChart(from, to, current);
                    RenderCustomerSpending(from, to);
                }
            }
        }

        protected override async Task InnerPost()
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();
                if (form["GraphForm"] == "customer")
                {
                    UpdateCustomerPeriod(form);
                    return;
                }
                if (DateTime.TryParseExact(form[nameof(From)].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                    && DateTime.TryParseExact(form[nameof(To)].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
                {
                    if (to < from) (from, to) = (to, from);
                    Response.Redirect(WithPeriod(from, to).ToUrl());
                    return;
                }
            }

            Response.Redirect(this.ToUrl());
        }

        private (DateTime From, DateTime To) GetPeriod()
        {
            if (From.HasValue && To.HasValue)
            {
                return From.Value <= To.Value ? (From.Value.Date, To.Value.Date) : (To.Value.Date, From.Value.Date);
            }

            var summary = ApplicationData.Businesses.Get(Business).Single<ManagerServer.Model.Summary>();
            if (summary.ShowBalancesForSpecifiedPeriod)
            {
                var to = summary.ToDate == ManagerServer.Model.Enums.DateType.Today ? DateTime.Today : summary.ToDateValue;
                return (summary.FromDate.Date, to.Date);
            }

            return (new DateTime(DateTime.Today.Year, 1, 1), DateTime.Today);
        }

        private static (DateTime From, DateTime To) ShiftPeriod(DateTime from, DateTime to, int direction)
        {
            for (var months = 1; months <= 120; months++)
            {
                if (from.AddMonths(months).AddDays(-1) == to)
                {
                    var shiftedFrom = from.AddMonths(direction * months);
                    return (shiftedFrom, shiftedFrom.AddMonths(months).AddDays(-1));
                }
            }

            var days = (to - from).Days + 1;
            return (from.AddDays(direction * days), to.AddDays(direction * days));
        }

        private Dictionary<Guid, decimal> GetProfitAndLossBalances(DateTime from, DateTime to)
        {
            var database = ApplicationData.Businesses.Get(Business);
            var transactions = new ManagerServer.Query.GeneralLedger.GeneralLedger(Business)
                .DisposeFixedAssets()
                .DisposeIntangibleAssets()
                .Revaluate(from, to);

            var summary = database.Single<ManagerServer.Model.Summary>();
            if (summary.ShowBalancesOnCashBasis)
            {
                transactions = new ManagerServer.Query.GeneralLedger.GeneralLedger(Business)
                    .DisposeFixedAssets()
                    .DisposeIntangibleAssets()
                    .AutomaticallyMatchSalesInvoices()
                    .AutomaticallyMatchPurchaseInvoices()
                    .ConvertSalesInvoicesToCashBasis2(new[] { from.AddDays(-1), to })
                    .ConvertPurchaseInvoicesToCashBasis2(new[] { from.AddDays(-1), to })
                    .Revaluate(from, to);
            }

            var aggregations = transactions.GetAggregations();
            return aggregations.GetProfitAndLossAccountKeys()
                .ToDictionary(x => x, x => aggregations.GetProfitAndLossAccountAmount(x, from, to));
        }

        private (decimal Revenue, decimal Profit) RenderIncomeStatementSankey(DateTime from, DateTime to)
        {
            Script("resources/chartjs/chart.umd.min.js?version=" + typeof(Template).Assembly.GetName().Version);
            Script("resources/chartjs/chartjs-plugin-datalabels.min.js?version=" + typeof(Template).Assembly.GetName().Version);
            var currency = ApplicationData.Businesses.Get(Business).Single<ManagerServer.Model.BaseCurrency>();
            var chartOfAccounts = new ManagerServer.Query.GeneralLedger.ChartOfAccountsModel(Business);
            var balances = GetProfitAndLossBalances(from, to);

            var revenue = new List<SankeyItem>();
            var costOfRevenue = new List<SankeyItem>();
            var operatingCosts = new List<SankeyItem>();
            var otherCosts = new List<SankeyItem>();
            var grossProfitPosition = chartOfAccounts.ProfitAndLossStatement
                .Where(x => x.IsSubtotal && ContainsAny(x.Name, "gross profit", "gross margin"))
                .Select(x => (int?)x.Position)
                .FirstOrDefault();
            var operatingProfitPosition = chartOfAccounts.ProfitAndLossStatement
                .Where(x => x.IsSubtotal && ContainsAny(x.Name, "operating profit", "operating income"))
                .Select(x => (int?)x.Position)
                .FirstOrDefault();
            var showGrossProfitStage = grossProfitPosition.HasValue || chartOfAccounts.ProfitAndLossStatement
                .Where(x => !x.IsSubtotal && x.IsExpenseGroup)
                .Any(group => group.GetAllAccounts().Any(account => IsCostOfRevenue(group, account)));

            foreach (var group in chartOfAccounts.ProfitAndLossStatement.Where(x => !x.IsSubtotal))
            {
                foreach (var account in group.GetAllAccounts())
                {
                    var raw = balances.TryGetValue(account.Key, out var value) ? value : 0m;
                    var amount = group.IsExpenseGroup ? raw : raw * -1m;
                    if (amount == 0m) continue;

                    var item = new SankeyItem { Name = account.Name, Amount = Math.Abs(amount) };
                    if (!group.IsExpenseGroup)
                    {
                        if (amount > 0m) revenue.Add(item); else otherCosts.Add(item);
                        continue;
                    }

                    if (amount < 0m)
                    {
                        revenue.Add(item);
                        continue;
                    }

                    switch (ClassifyExpense(group, account, grossProfitPosition, operatingProfitPosition))
                    {
                        case ExpenseCategory.CostOfRevenue: costOfRevenue.Add(item); break;
                        case ExpenseCategory.Other: otherCosts.Add(item); break;
                        default: operatingCosts.Add(item); break;
                    }
                }
            }

            revenue = Consolidate(revenue, "Other revenue");
            costOfRevenue = Consolidate(costOfRevenue, "Other cost of revenue");
            operatingCosts = Consolidate(operatingCosts, "Other operating costs");
            otherCosts = Consolidate(otherCosts, "Other costs");

            var totalRevenue = revenue.Sum(x => x.Amount);
            var totalCostOfRevenue = costOfRevenue.Sum(x => x.Amount);
            var totalOperatingCosts = operatingCosts.Sum(x => x.Amount);
            var totalOtherCosts = otherCosts.Sum(x => x.Amount);
            var grossProfit = totalRevenue - totalCostOfRevenue;
            var operatingProfit = grossProfit - totalOperatingCosts;
            var profit = operatingProfit - totalOtherCosts;

            using (Div(@class: "flex flex-col gap-4"))
            {
                using (Div(@class: "flex items-center justify-between gap-4"))
                {
                    using (Div())
                    {
                        using (H2(@class: "text-lg font-bold")) Write("Income statement flow");
                        using (Div(@class: "text-sm text-[var(--muted-foreground)]")) Write("Income flows through expenses to the profit or loss for the selected period.");
                    }
                    using (Div(@class: "text-right"))
                    {
                        using (Div(@class: "text-sm text-[var(--muted-foreground)]")) Write(Strings.ProfitLossForThePeriod);
                        using (Div(@class: $"text-xl font-bold tabular-nums {(profit < 0 ? "text-red-600" : "text-emerald-700")}"))
                        {
                            Write(profit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                        }
                    }
                }

                if (!revenue.Any() && !costOfRevenue.Any() && !operatingCosts.Any() && !otherCosts.Any())
                {
                    using (Div(@class: "border border-[var(--border)] rounded p-8 text-center text-[var(--muted-foreground)]"))
                    {
                        Write("There is no income statement activity for this period.");
                    }
                    return (totalRevenue, profit);
                }

                RenderSankeyChart(revenue, costOfRevenue, operatingCosts, otherCosts, grossProfit, operatingProfit, profit, showGrossProfitStage, currency);
            }

            return (totalRevenue, profit);
        }

        private void RenderProfitMarginChart(DateTime from, DateTime to, (decimal Revenue, decimal Profit) current)
        {
            var chartOfAccounts = new ManagerServer.Query.GeneralLedger.ChartOfAccountsModel(Business);
            var periods = new List<(DateTime From, DateTime To, decimal Revenue, decimal Profit)>();
            var periodFrom = from;
            var periodTo = to;
            periods.Add((from, to, current.Revenue, current.Profit));

            for (var i = 0; i < 5; i++)
            {
                (periodFrom, periodTo) = ShiftPeriod(periodFrom, periodTo, -1);
                var balances = GetProfitAndLossBalances(periodFrom, periodTo);
                decimal revenue = 0m;
                decimal profit = 0m;

                foreach (var group in chartOfAccounts.ProfitAndLossStatement.Where(x => !x.IsSubtotal))
                {
                    foreach (var account in group.GetAllAccounts())
                    {
                        var raw = balances.TryGetValue(account.Key, out var value) ? value : 0m;
                        var amount = group.IsExpenseGroup ? raw : -raw;
                        if ((!group.IsExpenseGroup && amount > 0m) || (group.IsExpenseGroup && amount < 0m))
                            revenue += Math.Abs(amount);
                        profit -= raw;
                    }
                }

                periods.Add((periodFrom, periodTo, revenue, profit));
            }

            periods.Reverse();
            using (Div(@class: "flex flex-col gap-4 mt-8"))
            {
                using (Div())
                {
                    using (H2(@class: "text-lg font-bold")) Write("Profit margin");
                    using (Div(@class: "text-sm text-[var(--muted-foreground)]")) Write("Revenue bars and profit margin for the selected period and five preceding periods.");
                }

                using (Div(@class: "border border-[var(--border)] rounded bg-[var(--card)] p-3", style: "height:380px"))
                {
                    Write("<canvas id=\"profitMarginChart\" role=\"img\" aria-label=\"Revenue bars and profit margin line over six periods\">Revenue bars and profit margin line over six periods</canvas>");
                }
            }

            using (Div(@class: "flex flex-col gap-4 mt-8"))
            {
                using (Div())
                {
                    using (H2(@class: "text-lg font-bold")) Write("Revenue trend");
                    using (Div(@class: "text-sm text-[var(--muted-foreground)]")) Write("Revenue for the selected period and five preceding periods.");
                }
                using (Div(@class: "border border-[var(--border)] rounded bg-[var(--card)] p-3", style: "height:340px"))
                    Write("<canvas id=\"revenueTrendChart\" role=\"img\" aria-label=\"Revenue line over six periods\">Revenue line over six periods</canvas>");
            }

            var currency = ApplicationData.Businesses.Get(Business).Single<ManagerServer.Model.BaseCurrency>();
            var points = periods.Select(period => new
            {
                from = period.From.ToLocalShortDisplayString(),
                to = period.To.ToLocalShortDisplayString(),
                revenue = period.Revenue.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short),
                revenueValue = period.Revenue,
                profit = period.Profit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short),
                margin = period.Revenue == 0m ? (decimal?)null : Math.Round(period.Profit / period.Revenue * 100m, 2),
            }).ToArray();
            using (Script()) Write(BuildProfitMarginScript(points, currency));
            using (Script()) Write(BuildRevenueTrendScript(points, currency));
        }

        private static string BuildProfitMarginScript(object points, ManagerServer.Model.Currency currency)
        {
            var pointsJson = JsonSerializer.Serialize(points);
            return $$"""
                (() => {
                    const points = {{pointsJson}};
                    {{BuildCurrencyFormatterScript(currency)}}
                    const canvas = document.getElementById('profitMarginChart');
                    new Chart(canvas, {
                        type: 'bar',
                        plugins: [ChartDataLabels],
                        data: {
                            labels: points.map(point => [point.from, point.to]),
                            datasets: [
                                {
                                    type: 'bar', label: 'Revenue', yAxisID: 'revenue',
                                    data: points.map(point => point.revenueValue),
                                    backgroundColor: '#93c5fd', borderColor: '#2563eb', borderWidth: 1,
                                    maxBarThickness: 52, order: 2,
                                    datalabels: {
                                        formatter: value => formatCurrency(value, true),
                                        anchor: 'end', align: 'top', offset: 4, clamp: true,
                                        color: '#1e3a8a', backgroundColor: 'rgba(255,255,255,0.9)',
                                        borderRadius: 3, padding: 2, font: { weight: 'bold', size: 11 }
                                    }
                                },
                                {
                                    type: 'line', label: 'Profit margin', yAxisID: 'margin',
                                    data: points.map(point => point.margin),
                                    borderColor: '#047857', backgroundColor: '#047857',
                                    pointBackgroundColor: points.map((_, index) => index === points.length - 1 ? '#065f46' : '#047857'),
                                    pointRadius: 5, pointHoverRadius: 7, borderWidth: 2,
                                    tension: 0.2, spanGaps: false, order: 1,
                                    datalabels: {
                                        display: context => context.dataset.data[context.dataIndex] !== null,
                                        formatter: value => `${value.toFixed(1)}%`,
                                        anchor: 'end', align: 'top', offset: 5, clamp: true,
                                        color: '#065f46', backgroundColor: 'rgba(255,255,255,0.9)',
                                        borderRadius: 3, padding: 2, font: { weight: 'bold', size: 11 }
                                    }
                                }
                            ]
                        },
                        options: {
                            responsive: true,
                            maintainAspectRatio: false,
                            layout: { padding: { top: 24 } },
                            plugins: {
                                legend: { position: 'bottom' },
                                tooltip: {
                                    callbacks: {
                                        title: items => items.length ? `${points[items[0].dataIndex].from} – ${points[items[0].dataIndex].to}` : '',
                                        label: item => item.dataset.yAxisID === 'revenue'
                                            ? `Revenue: ${points[item.dataIndex].revenue}`
                                            : `Profit margin: ${item.parsed.y.toFixed(2)}%`,
                                        afterLabel: item => item.dataset.yAxisID === 'margin' ? `Net profit: ${points[item.dataIndex].profit}` : ''
                                    }
                                }
                            },
                            scales: {
                                revenue: {
                                    type: 'linear', position: 'left', beginAtZero: true,
                                    title: { display: true, text: `Revenue (${currencyCode})` },
                                    ticks: { callback: value => formatCurrency(value, true) }
                                },
                                margin: {
                                    type: 'linear', position: 'right',
                                    title: { display: true, text: 'Profit margin (%)' },
                                    grid: { drawOnChartArea: false },
                                    ticks: { callback: value => `${value}%` }
                                },
                                x: { title: { display: true, text: 'Period' } }
                            }
                        }
                    });
                })();
                """;
        }

        private static string BuildRevenueTrendScript(object points, ManagerServer.Model.Currency currency)
        {
            var pointsJson = JsonSerializer.Serialize(points);
            return $$"""
                (() => {
                    const points = {{pointsJson}};
                    {{BuildCurrencyFormatterScript(currency)}}
                    new Chart(document.getElementById('revenueTrendChart'), {
                        type: 'line',
                        plugins: [ChartDataLabels],
                        data: {
                            labels: points.map(point => [point.from, point.to]),
                            datasets: [{
                                label: 'Revenue', data: points.map(point => point.revenueValue),
                                borderColor: '#1d4ed8', backgroundColor: '#1d4ed8',
                                pointRadius: 5, pointHoverRadius: 7, borderWidth: 2,
                                tension: 0.2, fill: false
                            }]
                        },
                        options: {
                            responsive: true, maintainAspectRatio: false,
                            layout: { padding: { top: 24 } },
                            plugins: {
                                legend: { display: false },
                                datalabels: {
                                    formatter: value => formatCurrency(value, true),
                                    anchor: 'end', align: 'top', offset: 5, clamp: true,
                                    color: '#1e3a8a', backgroundColor: 'rgba(255,255,255,0.9)',
                                    borderRadius: 3, padding: 2, font: { weight: 'bold', size: 11 }
                                },
                                tooltip: {
                                    callbacks: {
                                        title: items => items.length ? `${points[items[0].dataIndex].from} – ${points[items[0].dataIndex].to}` : '',
                                        label: item => `Revenue: ${points[item.dataIndex].revenue}`
                                    }
                                }
                            },
                            scales: {
                                y: {
                                    beginAtZero: true,
                                    title: { display: true, text: `Revenue (${currencyCode})` },
                                    ticks: { callback: value => formatCurrency(value, true) }
                                },
                                x: { title: { display: true, text: 'Period' } }
                            }
                        }
                    });
                })();
                """;
        }

        private static string BuildCurrencyFormatterScript(ManagerServer.Model.Currency currency)
        {
            return $$"""
                const currencyCode = {{JsonSerializer.Serialize(currency.GetCode())}};
                const currencyPrefix = {{JsonSerializer.Serialize(currency.GetPrefix())}};
                const currencySuffix = {{JsonSerializer.Serialize(currency.GetSuffix())}};
                const formatCurrency = (value, compact = false) => {
                    const amount = new Intl.NumberFormat(undefined, compact
                        ? Math.abs(value) >= 1000000
                            ? { notation: 'compact', minimumFractionDigits: 2, maximumFractionDigits: 2 }
                            : { notation: 'compact', maximumFractionDigits: 1 }
                        : { maximumFractionDigits: 2 }).format(Math.abs(value));
                    const sign = value < 0 ? '−' : '';
                    return currencyPrefix
                        ? `${sign}${currencyPrefix}${amount}${currencySuffix ? ' ' + currencySuffix : ''}`
                        : `${sign}${amount} ${currencySuffix || currencyCode}`;
                };
                """;
        }

        private static ExpenseCategory ClassifyExpense(ManagerServer.Query.GeneralLedger.ChartOfAccountsModel.Group group, ManagerServer.Query.GeneralLedger.ChartOfAccountsModel.Account account, int? grossProfitPosition, int? operatingProfitPosition)
        {
            var description = string.Join(" ", group.Name, account.Parent?.Name, account.Name, account.SystemName);
            if (IsCostOfRevenue(group, account))
            {
                return ExpenseCategory.CostOfRevenue;
            }
            if (ContainsAny(description, "income tax", "tax expense", "interest expense", "finance cost", "financing cost", "non-operating", "other expense"))
            {
                return ExpenseCategory.Other;
            }
            if (grossProfitPosition.HasValue && group.Position < grossProfitPosition.Value) return ExpenseCategory.CostOfRevenue;
            if (operatingProfitPosition.HasValue && group.Position > operatingProfitPosition.Value) return ExpenseCategory.Other;
            return ExpenseCategory.Operating;
        }

        private static bool IsCostOfRevenue(ManagerServer.Query.GeneralLedger.ChartOfAccountsModel.Group group, ManagerServer.Query.GeneralLedger.ChartOfAccountsModel.Account account)
        {
            var description = string.Join(" ", group.Name, account.Parent?.Name, account.Name, account.SystemName);
            return ContainsAny(description, "cost of revenue", "cost of sales", "cost of goods", "cost of income", "cogs", "direct cost", "inventory purchases");
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private void RenderSankeyChart(List<SankeyItem> revenueItems, List<SankeyItem> costOfRevenueItems, List<SankeyItem> operatingCostItems, List<SankeyItem> otherCostItems, decimal grossProfit, decimal operatingProfit, decimal netProfit, bool showGrossProfitStage, ManagerServer.Model.Currency currency)
        {
            const string revenueKey = "revenue";
            const string costOfRevenueKey = "costOfRevenue";
            const string grossProfitKey = "grossProfit";
            const string operatingCostsKey = "operatingCosts";
            const string operatingProfitKey = "operatingProfit";
            const string otherCostsKey = "otherCosts";
            const string netProfitKey = "netProfit";

            var revenue = revenueItems.Sum(x => x.Amount);
            var costOfRevenue = costOfRevenueItems.Sum(x => x.Amount);
            var operatingCosts = operatingCostItems.Sum(x => x.Amount);
            var otherCosts = otherCostItems.Sum(x => x.Amount);
            var flows = new List<Dictionary<string, object>>();
            var labels = new Dictionary<string, string>();
            var colors = new Dictionary<string, string>();
            var columns = new Dictionary<string, int>();
            var priorities = new Dictionary<string, int>();
            var operatingColumn = showGrossProfitStage ? 3 : 2;
            var resultColumn = operatingColumn + 1;

            AddNode(labels, colors, columns, priorities, revenueKey, MetricLabel("Revenue", revenue, revenue, currency), "#9ca3af", 1, 0);
            if (showGrossProfitStage)
            {
                AddNode(labels, colors, columns, priorities, grossProfitKey, MetricLabel(grossProfit < 0m ? "Gross loss" : "Gross profit", grossProfit, revenue, currency), grossProfit >= 0m ? "#16a34a" : "#dc2626", 2, grossProfit < 0m ? 1 : 0);
                AddNode(labels, colors, columns, priorities, costOfRevenueKey, MetricLabel("Cost of revenue", costOfRevenue, revenue, currency), "#dc2626", 2, grossProfit < 0m ? 0 : 1);
            }
            AddNode(labels, colors, columns, priorities, operatingProfitKey, MetricLabel(operatingProfit < 0m ? "Operating loss" : "Operating profit", operatingProfit, revenue, currency), operatingProfit >= 0m ? "#16a34a" : "#dc2626", operatingColumn, operatingProfit < 0m ? 1 : 0);
            AddNode(labels, colors, columns, priorities, operatingCostsKey, MetricLabel("Operating costs", operatingCosts, revenue, currency), "#dc2626", operatingColumn, operatingProfit < 0m ? 0 : 1);
            AddNode(labels, colors, columns, priorities, netProfitKey, MetricLabel(netProfit < 0m ? "Net loss" : "Net profit", netProfit, revenue, currency), netProfit >= 0m ? "#16a34a" : "#dc2626", resultColumn, netProfit < 0m ? operatingCostItems.Count + 3 : 0);
            if (otherCostItems.Any()) AddNode(labels, colors, columns, priorities, otherCostsKey, MetricLabel("Other costs", otherCosts, revenue, currency), "#dc2626", resultColumn, netProfit < 0m ? 0 : 1);

            for (var i = 0; i < revenueItems.Count; i++)
            {
                var key = "revenueItem" + i;
                AddNode(labels, colors, columns, priorities, key, MetricLabel(revenueItems[i].Name, revenueItems[i].Amount, revenue, currency), "#9ca3af", 0, i);
                AddFlow(flows, key, revenueKey, revenueItems[i].Amount, currency);
            }

            if (showGrossProfitStage)
            {
                AddFlow(flows, revenueKey, grossProfitKey, Math.Max(0m, grossProfit), currency);
                AddFlow(flows, revenueKey, costOfRevenueKey, Math.Min(revenue, costOfRevenue), currency);
                AddFlow(flows, grossProfitKey, operatingProfitKey, Math.Max(0m, operatingProfit), currency);
                AddFlow(flows, grossProfitKey, operatingCostsKey, Math.Min(Math.Max(0m, grossProfit), operatingCosts), currency);
            }
            else
            {
                AddFlow(flows, revenueKey, operatingProfitKey, Math.Max(0m, operatingProfit), currency);
                AddFlow(flows, revenueKey, operatingCostsKey, Math.Min(revenue, operatingCosts), currency);
            }
            AddFlow(flows, operatingProfitKey, netProfitKey, Math.Max(0m, netProfit), currency);
            if (otherCostItems.Any()) AddFlow(flows, operatingProfitKey, otherCostsKey, Math.Min(Math.Max(0m, operatingProfit), otherCosts), currency);

            for (var i = 0; showGrossProfitStage && i < costOfRevenueItems.Count; i++)
            {
                var key = "costOfRevenueItem" + i;
                AddNode(labels, colors, columns, priorities, key, MetricLabel(costOfRevenueItems[i].Name, costOfRevenueItems[i].Amount, revenue, currency), "#9ca3af", operatingColumn, i + 2);
                AddFlow(flows, costOfRevenueKey, key, costOfRevenueItems[i].Amount, currency);
            }

            for (var i = 0; i < operatingCostItems.Count; i++)
            {
                var key = "operatingCostItem" + i;
                AddNode(labels, colors, columns, priorities, key, MetricLabel(operatingCostItems[i].Name, operatingCostItems[i].Amount, revenue, currency), "#9ca3af", resultColumn, i + 2);
                AddFlow(flows, operatingCostsKey, key, operatingCostItems[i].Amount, currency);
            }

            for (var i = 0; i < otherCostItems.Count; i++)
            {
                var key = "otherCostItem" + i;
                AddNode(labels, colors, columns, priorities, key, MetricLabel(otherCostItems[i].Name, otherCostItems[i].Amount, revenue, currency), "#9ca3af", resultColumn + 1, i);
                AddFlow(flows, otherCostsKey, key, otherCostItems[i].Amount, currency);
            }

            var detailCount = new[] { revenueItems.Count, costOfRevenueItems.Count + 2, operatingCostItems.Count + 2, otherCostItems.Count }.Max();
            var height = Math.Max(520, 120 + detailCount * 48);
            using (Div(@class: "border border-[var(--border)] rounded bg-[var(--card)] p-3", style: $"height:{height}px"))
            {
                Write("<canvas id=\"incomeStatementSankey\" role=\"img\" aria-label=\"Income statement Sankey chart\">Income statement Sankey chart</canvas>");
            }
            using (Div(@class: "flex flex-wrap justify-center text-sm text-[var(--muted-foreground)] mt-2", style: "gap:0.25rem 2rem"))
            {
                using (Span()) Write("Revenue: " + revenue.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                if (showGrossProfitStage) using (Span()) Write((grossProfit < 0m ? "Gross loss: " : "Gross profit: ") + grossProfit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                using (Span()) Write((operatingProfit < 0m ? "Operating loss: " : "Operating profit: ") + operatingProfit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                using (Span()) Write((netProfit < 0m ? "Net loss: " : "Net profit: ") + netProfit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
            }
            if (grossProfit < 0m || operatingProfit < 0m || netProfit < 0m)
            {
                using (Div(@class: "flex flex-wrap items-center justify-center gap-2 text-sm mt-2"))
                {
                    using (Span(@class: "font-semibold")) Write("Subtotal progression:");
                    if (showGrossProfitStage)
                    {
                        using (Span(@class: grossProfit < 0m ? "text-red-600" : ""))
                            Write((grossProfit < 0m ? "Gross loss " : "Gross profit ") + grossProfit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                        using (Span(@class: "text-[var(--muted-foreground)]")) Write("→");
                    }
                    using (Span(@class: operatingProfit < 0m ? "text-red-600" : ""))
                        Write((operatingProfit < 0m ? "Operating loss " : "Operating profit ") + operatingProfit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                    using (Span(@class: "text-[var(--muted-foreground)]")) Write("→");
                    using (Span(@class: netProfit < 0m ? "text-red-600" : ""))
                        Write((netProfit < 0m ? "Net loss " : "Net profit ") + netProfit.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short));
                }
                using (Div(@class: "text-center text-sm text-[var(--muted-foreground)]"))
                    Write("A loss is the difference after expenses, not an additional expense or flow.");
            }

            var version = typeof(Template).Assembly.GetName().Version.ToString();
            Script("resources/chartjs/chartjs-chart-sankey.min.js?version=" + version);
            using (Script())
            {
                Write(BuildChartScript(flows, labels, colors, columns, priorities));
            }
        }

        private static List<SankeyItem> Consolidate(List<SankeyItem> items, string otherName)
        {
            const int maximumItems = 8;
            var ordered = items.OrderByDescending(x => Math.Abs(x.Amount)).ToList();
            if (ordered.Count <= maximumItems) return ordered;

            var result = ordered.Take(maximumItems - 1).ToList();
            result.Add(new SankeyItem
            {
                Name = otherName,
                Amount = ordered.Skip(maximumItems - 1).Sum(x => x.Amount),
            });
            return result;
        }

        private static void AddNode(Dictionary<string, string> labels, Dictionary<string, string> colors, Dictionary<string, int> columns, Dictionary<string, int> priorities, string key, string label, string color, int column, int priority)
        {
            labels[key] = label;
            colors[key] = color;
            columns[key] = column;
            priorities[key] = priority;
        }

        private static string MetricLabel(string name, decimal amount, decimal revenue, ManagerServer.Model.Currency currency)
        {
            var percentage = revenue == 0m ? 0m : amount / Math.Abs(revenue) * 100m;
            return name + "\n" + amount.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short) + " (" + percentage.ToString("0.#", CultureInfo.CurrentCulture) + "%)";
        }

        private static void AddFlow(List<Dictionary<string, object>> flows, string from, string to, decimal amount, ManagerServer.Model.Currency currency)
        {
            if (amount <= 0m) return;
            flows.Add(new Dictionary<string, object>
            {
                ["from"] = from,
                ["to"] = to,
                ["flow"] = amount,
                ["formatted"] = amount.ToCurrencyStringWithParentheses(currency, CurrencySymbol.Short),
            });
        }

        private static string BuildChartScript(List<Dictionary<string, object>> flows, Dictionary<string, string> labels, Dictionary<string, string> colors, Dictionary<string, int> columns, Dictionary<string, int> priorities)
        {
            var dataJson = JsonSerializer.Serialize(flows);
            var labelsJson = JsonSerializer.Serialize(labels);
            var colorsJson = JsonSerializer.Serialize(colors);
            var columnsJson = JsonSerializer.Serialize(columns);
            var prioritiesJson = JsonSerializer.Serialize(priorities);
            return $$"""
                (() => {
                    const canvas = document.getElementById('incomeStatementSankey');
                    const labels = {{labelsJson}};
                    const colors = {{colorsJson}};
                    const stageNodes = new Set(['revenue', 'costOfRevenue', 'grossProfit', 'operatingCosts', 'operatingProfit', 'otherCosts', 'netProfit']);
                    new Chart(canvas, {
                        type: 'sankey',
                        data: {
                            datasets: [{
                                data: {{dataJson}},
                                labels,
                                column: {{columnsJson}},
                                priority: {{prioritiesJson}},
                                colorFrom: context => colors[context.raw.from] || '#64748b',
                                colorTo: context => colors[context.raw.to] || '#64748b',
                                colorMode: 'gradient',
                                alpha: 0.62,
                                borderWidth: 0,
                                nodeWidth: 20,
                                nodePadding: node => stageNodes.has(node.key) ? { before: 24, after: 24 } : { before: 18, after: 18 },
                                nodeMinSize: node => stageNodes.has(node.key) ? 6 : 12,
                                nodeLabels: {
                                    color: getComputedStyle(document.body).color,
                                    font: { size: 10, weight: '600', lineHeight: 1 },
                                    padding: 1
                                }
                            }]
                        },
                        options: {
                            responsive: true,
                            maintainAspectRatio: false,
                            animation: { duration: 450 },
                            layout: { padding: { left: 12, right: 12, top: 12, bottom: 12 } },
                            plugins: {
                                legend: { display: false },
                                tooltip: {
                                    callbacks: {
                                        title: () => '',
                                        label: context => {
                                            const item = context.raw;
                                            const name = key => labels[key].split('\n')[0];
                                            return `${name(item.from)} → ${name(item.to)}: ${item.formatted}`;
                                        }
                                    }
                                }
                            }
                        }
                    });
                })();
                """;
        }

        private sealed class SankeyItem
        {
            public string Name;
            public decimal Amount;
        }

        private enum ExpenseCategory
        {
            CostOfRevenue,
            Operating,
            Other,
        }
    }
}
