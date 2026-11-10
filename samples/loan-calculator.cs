#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;
using static Reverb.Notebook;

// %% [markdown] id=intro
// # Loan calculator
//
// Move the sliders. The payment, the charts and the schedule follow. Run it with `reverb run` to
// share it as an app with the code hidden.

// %% id=inputs
var amount = Ui.Slider("Loan amount", min: 10_000, max: 1_000_000, value: 300_000, step: 5_000);
var rate = Ui.Slider("Interest rate (% a year)", min: 0.5, max: 12, value: 5.5, step: 0.1);
var years = Ui.Slider("Term (years)", min: 5, max: 40, value: 25);
var extra = Ui.Number("Extra paid each month", value: 0, min: 0, max: 10_000);

// %% id=payment
var monthlyRate = rate.Value / 100 / 12;
var months = (int)years.Value * 12;
var payment = amount.Value * monthlyRate / (1 - Math.Pow(1 + monthlyRate, -months));
Md($"Monthly payment: **{payment:N2}**{(extra.Value > 0 ? $", or **{payment + extra.Value:N2}** with the extra" : "")}.");

// %% id=schedule
// Month by month: interest on what's left, the rest of the payment pays the loan down.
var schedule = new List<Month>();
var balance = amount.Value;
for (var n = 1; balance > 0.005 && n <= months; n++)
{
    var interest = balance * monthlyRate;
    var principal = Math.Min(balance, payment + extra.Value - interest);
    balance -= principal;
    schedule.Add(new Month(n, interest, principal, balance));
}
var totalInterest = schedule.Sum(m => m.Interest);
Md($"Paid off in **{schedule.Count / 12} years and {schedule.Count % 12} months**, with **{totalInterest:N0}** paid in interest.");

// %% id=balance
Chart.Line(schedule.Where(m => m.Number % 12 == 0 || m.Number == schedule.Count), x: m => m.Number / 12.0, y: m => Math.Round(m.Balance), title: "Balance left", xTitle: "Years", yTitle: "Balance");

// %% id=yearly
var yearly = schedule
    .GroupBy(m => (m.Number - 1) / 12 + 1)
    .Select(g => new Year(g.Key, Math.Round(g.Sum(m => m.Interest), 2), Math.Round(g.Sum(m => m.Principal), 2), Math.Round(g.Last().Balance, 2)))
    .ToList();
Chart.Bar(
    yearly.SelectMany(y => new[] { new Part(y.Number, "Interest", y.Interest), new Part(y.Number, "Principal", y.Principal) }),
    x: p => p.Year, y: p => p.Amount, series: p => p.Kind, title: "Where each year's payments go");

// %% id=table
yearly.Dump("Year by year");

// %% types
record Month(int Number, double Interest, double Principal, double Balance);
record Year(int Number, double Interest, double Principal, double BalanceLeft);
record Part(int Year, string Kind, double Amount);
