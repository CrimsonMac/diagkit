using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace DiagKit.Report;

/// <summary>Renders a report as a single self-contained HTML file (no scripts, no external assets).</summary>
public static class HtmlReportWriter
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Sev(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.Warning => "warning",
        Severity.Info => "info",
        _ => "ok",
    };

    private static string SevLabel(Severity s) => s switch
    {
        Severity.Critical => "Critical",
        Severity.Warning => "Worth fixing",
        Severity.Info => "Good to know",
        _ => "Fine",
    };

    public static string Render(DiagnosticReport r)
    {
        var sb = new StringBuilder(64 * 1024);
        Severity worst = ReportFacts.Worst(r);

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(H($"{r.MachineName}: {ReportFacts.Verdict(r)}")).Append("</title>\n");
        sb.Append("<style>").Append(Css).Append("</style>\n</head>\n<body>\n");

        // Masthead: the verdict is the first and largest thing on the page.
        sb.Append("<header class=\"masthead\" data-sev=\"").Append(Sev(worst)).Append("\">\n<div class=\"wrap\">\n");
        sb.Append("<p class=\"machine\">").Append(H(r.MachineName)).Append("<span>").Append(H(ReportFacts.MachineTitle(r))).Append("</span></p>\n");
        sb.Append("<h1><span class=\"led\" data-sev=\"").Append(Sev(worst)).Append("\"></span>").Append(H(ReportFacts.Verdict(r))).Append("</h1>\n");
        sb.Append("<p class=\"lede\">").Append(H(ReportFacts.VerdictDetail(r))).Append("</p>\n");
        sb.Append("<ul class=\"tally\">");
        foreach (Severity s in new[] { Severity.Critical, Severity.Warning, Severity.Info, Severity.Ok })
        {
            int n = ReportFacts.Count(r, s);
            sb.Append("<li data-sev=\"").Append(Sev(s)).Append("\"").Append(n == 0 ? " class=\"zero\"" : "").Append("><span class=\"led\" data-sev=\"")
                .Append(n == 0 ? "off" : Sev(s)).Append("\"></span><b>").Append(n).Append("</b>").Append(H(SevLabel(s).ToLowerInvariant())).Append("</li>");
        }

        sb.Append("</ul>\n");
        sb.Append("<p class=\"stamp\">").Append(r.Scope != null ? H(r.Scope) + ". " : "").Append("Checked ").Append(H(TextUtil.DateTimeShort(r.CreatedAt)))
            .Append(" in ").Append(r.DurationSeconds.ToString("0")).Append(" s with DiagKit ").Append(H(r.AppVersion)).Append("</p>\n");
        sb.Append("</div>\n</header>\n<main class=\"wrap\">\n");

        // What changed since the previous check, for a second visit or a check after a repair.
        if (r.Comparison is { } cmp)
        {
            sb.Append("<section class=\"changes\">\n<h2>Since the last check</h2>\n<p class=\"lede\">").Append(H(ReportComparer.Summary(cmp))).Append("</p>\n");
            if (cmp.Changes.Count > 0)
            {
                sb.Append("<ul>");
                foreach (ReportChange c in cmp.Changes)
                {
                    sb.Append("<li><span class=\"led\" data-sev=\"").Append(Sev(c.Severity)).Append("\"></span><b>").Append(H(ReportComparer.KindLabel(c.Kind)))
                        .Append("</b><span>").Append(H(c.Text)).Append("</span></li>");
                }

                sb.Append("</ul>\n");
            }

            if (cmp.Note != null)
            {
                sb.Append("<p class=\"note\">").Append(H(cmp.Note)).Append("</p>\n");
            }

            sb.Append("</section>\n");
        }

        // Findings
        sb.Append("<section class=\"findings\">\n<h2>Findings</h2>\n");
        foreach (Finding f in r.Findings)
        {
            bool open = f.Severity >= Severity.Warning;
            sb.Append("<details class=\"finding\" data-sev=\"").Append(Sev(f.Severity)).Append('"').Append(open ? " open" : "").Append(">\n<summary>");
            sb.Append("<span class=\"led\" data-sev=\"").Append(Sev(f.Severity)).Append("\"></span>");
            sb.Append("<span class=\"title\">").Append(H(f.Title)).Append("</span>");
            sb.Append("<span class=\"cat\">").Append(H(f.Category)).Append("</span></summary>\n<div class=\"body\">\n");
            sb.Append("<p>").Append(H(f.Detail)).Append("</p>\n");
            if (!string.IsNullOrWhiteSpace(f.Action))
            {
                sb.Append("<p class=\"action\"><b>What to do</b>").Append(H(f.Action)).Append("</p>\n");
            }

            if (f.Evidence.Count > 0)
            {
                sb.Append("<ul class=\"evidence\">");
                foreach (string e in f.Evidence)
                {
                    sb.Append("<li>").Append(H(e)).Append("</li>");
                }

                sb.Append("</ul>\n");
            }

            sb.Append("</div>\n</details>\n");
        }

        if (r.Findings.Count == 0)
        {
            sb.Append("<p class=\"empty\">").Append(r.Scope != null && ReportFacts.FailedSteps(r) == 0
                ? "The parts that were checked had nothing to report."
                : "No findings were produced. See the collection log at the bottom.").Append("</p>\n");
        }

        sb.Append("</section>\n");

        // Readings
        List<Reading> readings = ReportFacts.Readings(r);
        if (readings.Count > 0)
        {
            sb.Append("<section>\n<h2>Readings</h2>\n<div class=\"readings\">");
            foreach (Reading rd in readings)
            {
                sb.Append("<div class=\"reading\" data-sev=\"").Append(Sev(rd.Severity)).Append("\"><span class=\"value\">").Append(H(rd.Value))
                    .Append("<small>").Append(H(rd.Unit)).Append("</small></span><span class=\"label\">").Append(H(rd.Label)).Append("</span></div>");
            }

            sb.Append("</div>\n</section>\n");
        }

        // Drives
        List<DriveRow> drives = ReportFacts.Drives(r);
        if (drives.Count > 0)
        {
            sb.Append("<section>\n<h2>Drives</h2>\n<div class=\"scroll\"><table class=\"drives\">\n<thead><tr><th>Drive</th><th>Type</th><th>Size</th><th>Health</th><th class=\"num\">Temp</th><th class=\"num\">Powered on</th><th class=\"num\">Wear</th></tr></thead>\n<tbody>\n");
            foreach (DriveRow d in drives)
            {
                sb.Append("<tr><td>").Append(H(d.Name)).Append("</td><td>").Append(H(d.Kind)).Append("</td><td>").Append(H(d.Size))
                    .Append("</td><td><span class=\"led\" data-sev=\"").Append(Sev(d.Severity)).Append("\"></span>").Append(H(d.Health))
                    .Append("</td><td class=\"num\">").Append(H(d.Temperature)).Append("</td><td class=\"num\">").Append(H(d.PowerOn))
                    .Append("</td><td class=\"num\">").Append(H(d.Wear)).Append("</td></tr>\n");
            }

            sb.Append("</tbody></table></div>\n</section>\n");
        }

        // Spec sheet
        sb.Append("<section>\n<h2>This PC</h2>\n<dl class=\"spec\">\n");
        foreach (Fact fact in ReportFacts.Overview(r))
        {
            sb.Append("<div><dt>").Append(H(fact.Label)).Append("</dt><dd>").Append(H(fact.Value)).Append("</dd></div>\n");
        }

        sb.Append("</dl>\n</section>\n");

        // Collection log
        sb.Append("<section class=\"log\">\n<h2>How the check ran</h2>\n<div class=\"scroll\"><table>\n<tbody>\n");
        foreach (CollectorRecord c in r.Collectors)
        {
            string sev = c.Status switch
            {
                "ok" => "ok",
                "partial" => "warning",
                _ => "critical",
            };
            sb.Append("<tr><td><span class=\"led\" data-sev=\"").Append(sev).Append("\"></span>").Append(H(c.Name)).Append("</td><td>")
                .Append(H(c.Message)).Append("</td><td class=\"num\">").Append(c.Seconds.ToString("0.0")).Append(" s</td></tr>\n");
        }

        sb.Append("</tbody></table></div>\n<p class=\"note\">Raw data from every step (SMART JSON, event summaries, tool output) is in the <code>raw</code> folder next to this file.</p>\n</section>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private const string Css = """
:root {
  --bg: #171b21; --surface: #1e232b; --raised: #252b34; --line: #2f3642;
  --text: #e7eaee; --muted: #8e97a4; --faint: #5f6876;
  --critical: #ff5a5f; --warning: #ffb020; --info: #7fa7d9; --ok: #4ade80; --off: #3a424f;
  --display: Bahnschrift, "DIN Alternate", "Barlow Semi Condensed", "Segoe UI", system-ui, sans-serif;
  --body: "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, Roboto, sans-serif;
  color-scheme: dark;
}
@media (prefers-color-scheme: light) {
  :root {
    --bg: #eef1f4; --surface: #ffffff; --raised: #f5f7f9; --line: #d5dbe2;
    --text: #1b2129; --muted: #5b6573; --faint: #8b95a3;
    --critical: #d93036; --warning: #b77900; --info: #4a6fa5; --ok: #1f9d55; --off: #c3cad3;
    color-scheme: light;
  }
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.55 var(--body); }
.wrap { max-width: 960px; margin: 0 auto; padding: 0 24px; }
h1, h2 { font-family: var(--display); font-weight: 600; letter-spacing: -0.01em; }
h2 { font-size: 19px; margin: 44px 0 14px; color: var(--text); }
code { font-size: 0.92em; background: var(--raised); padding: 1px 5px; border-radius: 3px; }

.led { --c: var(--off); display: inline-block; flex: none; width: 9px; height: 9px; border-radius: 50%;
  background: var(--c); margin-right: 10px; vertical-align: 1px; }
.led[data-sev="critical"] { --c: var(--critical); box-shadow: 0 0 0 3px color-mix(in srgb, var(--c) 22%, transparent), 0 0 12px var(--c); }
.led[data-sev="warning"] { --c: var(--warning); box-shadow: 0 0 0 3px color-mix(in srgb, var(--c) 22%, transparent), 0 0 10px var(--c); }
.led[data-sev="info"] { --c: var(--info); box-shadow: 0 0 0 3px color-mix(in srgb, var(--c) 18%, transparent); }
.led[data-sev="ok"] { --c: var(--ok); box-shadow: 0 0 0 3px color-mix(in srgb, var(--c) 18%, transparent); }

.masthead { border-bottom: 1px solid var(--line); background: var(--surface); padding: 36px 0 28px; }
.machine { margin: 0 0 18px; font-family: var(--display); font-size: 14px; color: var(--muted); }
.machine span { margin-left: 12px; color: var(--faint); }
.masthead h1 { display: flex; align-items: center; margin: 0; font-size: clamp(28px, 5vw, 42px); line-height: 1.1; }
.masthead h1 .led { width: 14px; height: 14px; margin-right: 18px; }
.lede { margin: 12px 0 22px; max-width: 62ch; color: var(--muted); font-size: 16px; }
.tally { display: flex; flex-wrap: wrap; gap: 8px 28px; list-style: none; margin: 0; padding: 0; }
.tally li { display: flex; align-items: center; color: var(--muted); }
.tally b { font-family: var(--display); font-size: 22px; color: var(--text); margin-right: 7px; font-weight: 600; }
.tally li.zero b { color: var(--faint); }
.stamp { margin: 22px 0 0; font-size: 13px; color: var(--faint); }

.finding { background: var(--surface); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 6px; margin-bottom: 8px; }
.finding[data-sev="critical"] { border-left-color: var(--critical); }
.finding[data-sev="warning"] { border-left-color: var(--warning); }
.finding summary { display: flex; align-items: center; gap: 4px; padding: 13px 16px; cursor: pointer; list-style: none; }
.finding summary::-webkit-details-marker { display: none; }
.finding summary:focus-visible { outline: 2px solid var(--info); outline-offset: 2px; }
.finding .title { flex: 1; font-weight: 600; }
.finding .cat { font-size: 13px; color: var(--faint); white-space: nowrap; }
.finding .body { padding: 0 16px 16px 35px; color: var(--muted); max-width: 78ch; }
.finding .body p { margin: 0 0 10px; }
.finding .action { color: var(--text); }
.finding .action b { display: block; font-family: var(--display); font-weight: 600; font-size: 13px; color: var(--muted); margin-bottom: 2px; }
.evidence { margin: 10px 0 0; padding: 10px 14px; list-style: none; background: var(--raised); border-radius: 4px; font-size: 13.5px; }
.evidence li { padding: 3px 0; overflow-wrap: anywhere; }
.evidence li + li { border-top: 1px solid var(--line); }
.empty { color: var(--muted); }

.changes .lede { margin: 0 0 12px; }
.changes ul { margin: 0; padding: 4px 16px; list-style: none; background: var(--surface); border: 1px solid var(--line); border-radius: 6px; }
.changes li { display: flex; align-items: baseline; padding: 8px 0; overflow-wrap: anywhere; }
.changes li + li { border-top: 1px solid var(--line); }
.changes li b { flex: none; width: 76px; font-family: var(--display); font-weight: 600; font-size: 13px; color: var(--muted); }
.changes .note { margin: 10px 0 0; }

.readings { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 1px; background: var(--line);
  border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
.reading { background: var(--surface); padding: 14px 16px; display: flex; flex-direction: column; }
.reading .value { font-family: var(--display); font-size: 30px; line-height: 1; font-variant-numeric: tabular-nums; }
.reading .value small { font-size: 14px; color: var(--muted); margin-left: 4px; }
.reading .label { margin-top: 8px; font-size: 13px; color: var(--muted); }
.reading[data-sev="warning"] .value { color: var(--warning); }
.reading[data-sev="critical"] .value { color: var(--critical); }

.scroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 14px; }
th { text-align: left; font-weight: 600; font-size: 13px; color: var(--muted); padding: 8px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
td { padding: 9px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
td .led { margin-right: 8px; }
.num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }

.spec { margin: 0; border-top: 1px solid var(--line); }
.spec div { display: grid; grid-template-columns: 160px 1fr; gap: 16px; padding: 9px 0; border-bottom: 1px solid var(--line); }
.spec dt { color: var(--muted); }
.spec dd { margin: 0; overflow-wrap: anywhere; }

.log td:first-child { white-space: nowrap; }
.log td { color: var(--muted); }
.note { font-size: 13px; color: var(--faint); margin: 14px 0 60px; }

@media (max-width: 600px) {
  .wrap { padding: 0 16px; }
  .spec div { grid-template-columns: 1fr; gap: 2px; }
  .finding .cat { display: none; }
  .finding .body { padding-left: 16px; }
}
@media print {
  .finding { break-inside: avoid; }
  .finding:not([open]) .body { display: block; }
}
""";
}
