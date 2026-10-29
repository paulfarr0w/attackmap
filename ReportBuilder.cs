using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AttackMap;

/// <summary>Turns the ban history into one self-contained HTML page.</summary>
public static class ReportBuilder
{
    // Days of history above which the over-time chart switches from daily to weekly points.
    private const int WeeklyThresholdDays = 120;

    public static string Build(AppState state, AppConfig config, DateTimeOffset now)
    {
        var events = state.Events;
        var payload = BuildPayload(events, config, now);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // The default encoder escapes <, >, & and quotes, so the JSON is safe inside a <script> block.
        });

        var theme = config.Theme.Trim().ToLowerInvariant() switch
        {
            "dark" => " data-theme=\"dark\"",
            "auto" => "",
            _ => " data-theme=\"light\"",
        };

        var attribution = "";
        if (!string.IsNullOrWhiteSpace(config.GeoIpAttribution))
        {
            var text = WebUtility.HtmlEncode(config.GeoIpAttribution);
            attribution = Uri.TryCreate(config.GeoIpAttributionUrl, UriKind.Absolute, out var url) &&
                          (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                ? $" · <a href=\"{WebUtility.HtmlEncode(url.ToString())}\" target=\"_blank\" rel=\"noopener\">{text}</a>"
                : $" · {text}";
        }

        return Template
            .Replace("__THEME_ATTR__", theme)
            .Replace("__TITLE__", WebUtility.HtmlEncode(config.Title))
            .Replace("__ATTRIBUTION__", attribution)
            .Replace("__DATA__", json);
    }

    private static object BuildPayload(List<BanEvent> events, AppConfig config, DateTimeOffset now)
    {
        var tz = TimeZoneInfo.Local;
        DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, tz).DateTime);
        string Stamp(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        // Shared by the global top-IPs table and each country's own top-IPs list (for the map click-through).
        List<(string Ip, int Count, string Last, DateTimeOffset LastSort, string Jails)> GroupIps(IEnumerable<BanEvent> evts) =>
            evts.GroupBy(e => e.Ip)
                .Select(g =>
                {
                    var latest = g.MaxBy(e => e.Time)!;
                    return (g.Key, g.Count(), Stamp(latest.Time), latest.Time,
                        string.Join(", ", g.Select(e => e.Jail).Where(j => !string.IsNullOrEmpty(j)).Distinct().OrderBy(j => j)));
                })
                .ToList();

        var countries = events
            .GroupBy(e => e.CountryCode)
            .Select(g => new
            {
                Code = g.Key,
                Name = g.OrderByDescending(e => e.Time).First().CountryName,
                Count = g.Count(),
                Ips = g.Select(e => e.Ip).Distinct().Count(),
                TopIps = GroupIps(g)
                    .OrderByDescending(x => x.Count).ThenByDescending(x => x.LastSort)
                    .Take(Math.Max(1, config.TopIpsPerCountry))
                    .Select(x => new { x.Ip, x.Count, x.Last })
                    .ToList(),
            })
            .OrderByDescending(c => c.Count).ThenBy(c => c.Name)
            .ToList();

        var topIps = events
            .GroupBy(e => e.Ip)
            .Select(g =>
            {
                var latest = g.MaxBy(e => e.Time)!;
                return new
                {
                    Ip = g.Key,
                    Code = latest.CountryCode,
                    Name = latest.CountryName,
                    Count = g.Count(),
                    Last = Stamp(latest.Time),
                    LastSort = latest.Time,
                    Jails = string.Join(", ", g.Select(e => e.Jail).Where(j => !string.IsNullOrEmpty(j)).Distinct().OrderBy(j => j)),
                };
            })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.LastSort)
            .Take(Math.Max(1, config.TopIpCount))
            .Select(x => new { x.Ip, x.Code, x.Name, x.Count, x.Last, x.Jails })
            .ToList();

        // Over time: one point per day, or per week (starting Monday) for long histories. Gaps are filled with zero.
        var labels = new List<string>();
        var values = new List<int>();
        var granularity = "day";
        if (events.Count > 0)
        {
            var days = events.GroupBy(e => Day(e.Time)).ToDictionary(g => g.Key, g => g.Count());
            var first = days.Keys.Min();
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
            if (days.Keys.Max() > last) last = days.Keys.Max();

            if (last.DayNumber - first.DayNumber > WeeklyThresholdDays)
            {
                granularity = "week";
                static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                for (var w = WeekStart(first); w <= last; w = w.AddDays(7))
                {
                    var total = 0;
                    for (var d = w; d < w.AddDays(7); d = d.AddDays(1))
                        total += days.GetValueOrDefault(d);
                    labels.Add(w.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    values.Add(total);
                }
            }
            else
            {
                for (var d = first; d <= last; d = d.AddDays(1))
                {
                    labels.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    values.Add(days.GetValueOrDefault(d));
                }
            }
        }

        var weekAgo = now.AddDays(-7);
        return new
        {
            Generated = Stamp(now),
            Summary = new
            {
                Total = events.Count,
                UniqueIps = events.Select(e => e.Ip).Distinct().Count(),
                Countries = countries.Count(c => c.Code != GeoLookup.Unknown.Code),
                Last7Days = events.Count(e => e.Time >= weekAgo),
                First = events.Count > 0 ? Day(events.Min(e => e.Time)).ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null,
            },
            Countries = countries,
            TopCountryCount = Math.Max(1, config.TopCountryCount),
            TopIps = topIps,
            Timeline = new { Granularity = granularity, Labels = labels, Values = values },
        };
    }

    private const string Template = """
<!doctype html>
<html lang="en"__THEME_ATTR__>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/jsvectormap@1.7.0/dist/jsvectormap.min.css" integrity="sha384-DkPMVIJ2hERal/WJDrT3mRGo8KOwiHVdjiyRxCFlUnMdvu52V0Y1CL1yULYSDn4h" crossorigin="anonymous">
<style>
  :root {
    color-scheme: light;
    --surface-0: #f5f4f1;
    --surface-1: #fcfcfb;
    --border: #e3e2de;
    --grid: #ebeae6;
    --text-primary: #0b0b0b;
    --text-secondary: #52514e;
    --text-muted: #6f6e69;
    --series-1: #2a78d6;
    --map-empty: #e6e5e1;
    --seq-1: #b7d3f6; --seq-2: #86b6ef; --seq-3: #5598e7;
    --seq-4: #2a78d6; --seq-5: #1c5cab; --seq-6: #104281;
    --link: #1c5cab;
  }
  @media (prefers-color-scheme: dark) {
    :root:where(:not([data-theme="light"])) {
      color-scheme: dark;
      --surface-0: #121211; --surface-1: #1a1a19; --border: #2f2f2d; --grid: #2a2a28;
      --text-primary: #f0efec; --text-secondary: #c3c2b7; --text-muted: #9a998f;
      --series-1: #3987e5; --map-empty: #2c2c2a;
      --seq-1: #104281; --seq-2: #184f95; --seq-3: #256abf;
      --seq-4: #3987e5; --seq-5: #6da7ec; --seq-6: #9ec5f4;
      --link: #86b6ef;
    }
  }
  :root[data-theme="dark"] {
    color-scheme: dark;
    --surface-0: #121211; --surface-1: #1a1a19; --border: #2f2f2d; --grid: #2a2a28;
    --text-primary: #f0efec; --text-secondary: #c3c2b7; --text-muted: #9a998f;
    --series-1: #3987e5; --map-empty: #2c2c2a;
    --seq-1: #104281; --seq-2: #184f95; --seq-3: #256abf;
    --seq-4: #3987e5; --seq-5: #6da7ec; --seq-6: #9ec5f4;
    --link: #86b6ef;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px 16px 32px;
    background: var(--surface-0); color: var(--text-primary);
    font: 15px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  }
  .wrap { max-width: 1100px; margin: 0 auto; }
  h1 { font-size: 1.6rem; margin: 0 0 4px; letter-spacing: -0.01em; }
  h2 { font-size: 1.05rem; margin: 0 0 2px; }
  .sub { color: var(--text-secondary); margin: 0 0 20px; }
  .note { color: var(--text-muted); font-size: 0.85rem; margin: 0 0 12px; }
  a { color: var(--link); }
  .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 16px; }
  .tile, .card { background: var(--surface-1); border: 1px solid var(--border); border-radius: 10px; }
  .tile { padding: 14px 16px; }
  .tile .label { color: var(--text-secondary); font-size: 0.85rem; }
  .tile .value { font-size: 1.75rem; font-weight: 650; font-variant-numeric: tabular-nums; line-height: 1.2; }
  .card { padding: 16px; margin-bottom: 16px; min-width: 0; }
  .two { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr)); gap: 16px; }
  .two .card { margin-bottom: 0; }
  .two { margin-bottom: 16px; }
  #map { height: 420px; }
  @media (max-width: 600px) { #map { height: 260px; } }
  .legend { display: flex; flex-wrap: wrap; gap: 6px 14px; margin-top: 10px; font-size: 0.82rem; color: var(--text-secondary); }
  .legend span { display: inline-flex; align-items: center; gap: 6px; }
  .legend i { width: 14px; height: 14px; border-radius: 3px; display: inline-block; }
  .chart-box { position: relative; }
  .table-scroll { overflow-x: auto; }
  table { border-collapse: collapse; width: 100%; font-size: 0.9rem; }
  th, td { text-align: left; padding: 7px 10px; border-bottom: 1px solid var(--border); white-space: nowrap; }
  th { color: var(--text-secondary); font-weight: 600; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.03em; }
  td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
  td.ip { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  td.wrap-cell { white-space: normal; min-width: 200px; color: var(--text-secondary); }
  details summary { cursor: pointer; font-weight: 600; }
  details[open] summary { margin-bottom: 10px; }
  .empty { padding: 40px 16px; text-align: center; color: var(--text-secondary); }
  footer { color: var(--text-muted); font-size: 0.8rem; margin-top: 8px; }
  .jvm-tooltip { background: var(--text-primary); color: var(--surface-1); font: 13px/1.4 system-ui, sans-serif; padding: 5px 9px; border-radius: 6px; box-shadow: none; }
  .jvm-zoom-btn { background: var(--surface-1); color: var(--text-primary); border: 1px solid var(--border); }
  dialog#country-modal {
    border: none; border-radius: 10px; padding: 0; max-width: min(92vw, 480px); width: 100%;
    background: var(--surface-1); color: var(--text-primary);
  }
  dialog#country-modal::backdrop { background: rgba(0, 0, 0, 0.5); }
  .modal-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 14px 16px; border-bottom: 1px solid var(--border); }
  .modal-head h3 { margin: 0; font-size: 1.05rem; }
  .modal-head button { background: none; border: none; color: var(--text-secondary); font-size: 1.3rem; line-height: 1; cursor: pointer; padding: 4px; }
  .modal-body { padding: 10px 16px 16px; max-height: 60vh; overflow-y: auto; }
</style>
</head>
<body>
<div class="wrap">
  <h1>__TITLE__</h1>
  <p class="sub" id="subtitle"></p>

  <div id="content">
    <div class="tiles">
      <div class="tile"><div class="label">Total bans</div><div class="value" id="t-total"></div></div>
      <div class="tile"><div class="label">Unique attacking IPs</div><div class="value" id="t-ips"></div></div>
      <div class="tile"><div class="label">Countries</div><div class="value" id="t-countries"></div></div>
      <div class="tile"><div class="label">Bans in the last 7 days</div><div class="value" id="t-week"></div></div>
    </div>

    <section class="card">
      <h2>Where the attacks come from</h2>
      <p class="note">Countries shaded by number of bans. Hover a country for its count.</p>
      <div id="map" role="img" aria-label="World map shaded by number of bans per country"></div>
      <div class="legend" id="map-legend"></div>
    </section>

    <div class="two">
      <section class="card">
        <h2>Top countries</h2>
        <p class="note" id="bar-note"></p>
        <div class="chart-box" id="bar-box"><canvas id="bar" aria-label="Bar chart of bans by country"></canvas></div>
      </section>
      <section class="card">
        <h2>Attacks over time</h2>
        <p class="note" id="line-note"></p>
        <div class="chart-box" style="height:320px"><canvas id="line" aria-label="Line chart of bans over time"></canvas></div>
      </section>
    </div>

    <section class="card">
      <h2>Top attacking IP addresses</h2>
      <p class="note" id="ip-note"></p>
      <div class="table-scroll">
        <table>
          <thead><tr><th class="num">#</th><th>IP address</th><th>Country</th><th class="num">Bans</th><th>Last banned</th><th>Jails</th></tr></thead>
          <tbody id="ip-rows"></tbody>
        </table>
      </div>
    </section>

    <section class="card">
      <details>
        <summary>All countries</summary>
        <div class="table-scroll">
          <table>
            <thead><tr><th class="num">#</th><th>Country</th><th class="num">Bans</th><th class="num">Unique IPs</th><th class="num">Share</th></tr></thead>
            <tbody id="country-rows"></tbody>
          </table>
        </div>
      </details>
    </section>
  </div>

  <footer>Built from fail2ban ban notifications<span id="foot-updated"></span>__ATTRIBUTION__ · <a href="https://github.com/paulfarr0w/attackmap" target="_blank" rel="noopener">Want a map like this for your own server? Get AttackMap on GitHub</a></footer>
</div>

<dialog id="country-modal">
  <div class="modal-head">
    <h3 id="modal-title"></h3>
    <button type="button" id="modal-close" aria-label="Close">&times;</button>
  </div>
  <div class="modal-body">
    <p class="note" id="modal-note"></p>
    <div class="table-scroll">
      <table>
        <thead><tr><th class="num">#</th><th>IP address</th><th class="num">Bans</th><th>Last banned</th></tr></thead>
        <tbody id="modal-rows"></tbody>
      </table>
    </div>
  </div>
</dialog>

<script type="application/json" id="report-data">__DATA__</script>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.6.1/dist/chart.umd.min.js" integrity="sha384-Emf5ZVIUiarAdhkgtqK+5/Z5qIvM6QoVrS+qAVtfyZVuD5lIHrac0me485+Antp0" crossorigin="anonymous"></script>
<script src="https://cdn.jsdelivr.net/npm/jsvectormap@1.7.0/dist/jsvectormap.min.js" integrity="sha384-5T42pwmm9Eq6IKYKkuugmsyxvzvXDazwImzsC76X6lihpgExlMJYSrAtpb5JGZsW" crossorigin="anonymous"></script>
<script src="https://cdn.jsdelivr.net/npm/jsvectormap@1.7.0/dist/maps/world.js" integrity="sha384-sGlkSOVF9H+QNNQrLV9xrMIyghEgioD3M3gPd9/tV0mh9qJX75AyCl0l7+OVJ3CD" crossorigin="anonymous"></script>
<script>
(function () {
  "use strict";
  var data = JSON.parse(document.getElementById("report-data").textContent);
  var css = getComputedStyle(document.documentElement);
  function v(name) { return css.getPropertyValue(name).trim(); }
  var fmt = new Intl.NumberFormat();
  function n(x) { return fmt.format(x); }
  function setText(id, text) { var el = document.getElementById(id); if (el) el.textContent = text; }
  function cell(tr, text, cls) { var td = document.createElement("td"); td.textContent = text; if (cls) td.className = cls; tr.appendChild(td); return td; }

  var s = data.summary;
  setText("foot-updated", " · updated " + data.generated);

  if (!s.total) {
    setText("subtitle", "No bans recorded yet.");
    document.getElementById("content").innerHTML =
      '<div class="card empty">No fail2ban ban emails have been found yet. This page will fill in after the next run that finds some.</div>';
    return;
  }

  setText("subtitle", n(s.total) + " bans recorded since " + s.first + ". Updated " + data.generated + ".");
  setText("t-total", n(s.total));
  setText("t-ips", n(s.uniqueIps));
  setText("t-countries", n(s.countries));
  setText("t-week", n(s.last7Days));

  var countries = data.countries;
  var known = countries.filter(function (c) { return c.code !== "??"; });

  // ---- Map: classed choropleth, single blue ramp, light -> dark with magnitude ----
  (function () {
    var counts = {}, names = {};
    known.forEach(function (c) { counts[c.code] = c.count; names[c.code] = c.name; });
    var max = known.length ? known[0].count : 0;

    // Nice log-spaced class breaks (1, 2, 5, 10, 20, 50 ...), at most 6 classes.
    var nice = [];
    for (var p = 1; p <= max; p *= 10) [1, 2, 5].forEach(function (m) { if (p * m <= max) nice.push(p * m); });
    if (!nice.length) nice = [1];
    var classes = Math.min(6, nice.length), breaks = [];
    for (var i = 0; i < classes; i++) {
      var b = nice[Math.round(i * (nice.length - 1) / Math.max(1, classes - 1))];
      if (breaks.indexOf(b) < 0) breaks.push(b);
    }
    var ramp = [v("--seq-1"), v("--seq-2"), v("--seq-3"), v("--seq-4"), v("--seq-5"), v("--seq-6")];
    var colors = breaks.map(function (_, i) {
      return ramp[breaks.length === 1 ? 3 : Math.round(i * (ramp.length - 1) / (breaks.length - 1))];
    });
    function classOf(count) { var k = 0; for (var i = 0; i < breaks.length; i++) if (count >= breaks[i]) k = i; return k; }

    var scale = {}, values = {};
    colors.forEach(function (c, i) { scale["c" + i] = c; });
    Object.keys(counts).forEach(function (code) { values[code] = "c" + classOf(counts[code]); });

    var legend = document.getElementById("map-legend");
    function swatch(color, label) {
      var span = document.createElement("span"), i = document.createElement("i");
      i.style.background = color; span.appendChild(i); span.appendChild(document.createTextNode(label)); legend.appendChild(span);
    }
    breaks.forEach(function (b, i) {
      var next = breaks[i + 1];
      swatch(colors[i], next === undefined ? n(b) + "+" : (next - 1 === b ? n(b) : n(b) + "–" + n(next - 1)));
    });
    swatch(v("--map-empty"), "none");

    if (typeof jsVectorMap === "undefined") { setText("map", "Map could not be loaded."); return; }
    var map = new jsVectorMap({
      selector: "#map",
      map: "world",
      zoomButtons: true,
      zoomOnScroll: false,
      regionStyle: {
        initial: { fill: v("--map-empty"), stroke: v("--surface-1"), strokeWidth: 0.6, fillOpacity: 1 },
        hover: { fillOpacity: 0.8, cursor: "pointer" }
      },
      series: { regions: [{ attribute: "fill", scale: scale, values: values }] },
      onRegionTooltipShow: function (event, tooltip, code) {
        var c = counts[code];
        tooltip.text((names[code] || tooltip.text()) + ": " + (c ? n(c) + (c === 1 ? " ban" : " bans") : "no bans"));
      },
      onRegionClick: function (event, code) { openCountryModal(code); }
    });

    // jsVectorMap bakes its SVG size in at creation time and never re-reads the container,
    // so rotating a phone (or any resize) needs an explicit nudge to redraw at the new size.
    var resizeTimer;
    function scheduleMapResize() {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () { map.updateSize(); }, 150);
    }
    window.addEventListener("resize", scheduleMapResize);
    window.addEventListener("orientationchange", scheduleMapResize);

    var modal = document.getElementById("country-modal");
    var modalTitle = document.getElementById("modal-title");
    var modalNote = document.getElementById("modal-note");
    var modalRows = document.getElementById("modal-rows");
    function openCountryModal(code) {
      if (!modal || typeof modal.showModal !== "function") return;
      var country = countries.find(function (c) { return c.code === code; });
      modalTitle.textContent = country ? country.name : (names[code] || code);
      modalRows.innerHTML = "";
      var ips = country ? country.topIps : [];
      if (!ips.length) {
        modalNote.textContent = "No bans recorded for this country.";
      } else {
        modalNote.textContent = "Top " + ips.length + " attacking IP" + (ips.length === 1 ? "" : "s") + ".";
        ips.forEach(function (r, i) {
          var tr = document.createElement("tr");
          cell(tr, String(i + 1), "num"); cell(tr, r.ip, "ip");
          cell(tr, n(r.count), "num"); cell(tr, r.last);
          modalRows.appendChild(tr);
        });
      }
      modal.showModal();
    }
    if (modal) {
      document.getElementById("modal-close").addEventListener("click", function () { modal.close(); });
      modal.addEventListener("click", function (e) { if (e.target === modal) modal.close(); });
    }
  })();

  if (typeof Chart === "undefined") return;
  Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
  Chart.defaults.color = v("--text-secondary");
  var tooltipStyle = {
    backgroundColor: v("--text-primary"), titleColor: v("--surface-1"), bodyColor: v("--surface-1"),
    displayColors: false, padding: 8, cornerRadius: 6
  };

  // ---- Top countries: horizontal bars, single series ----
  (function () {
    var top = countries.slice(0, data.topCountryCount);
    var rest = countries.length - top.length;
    setText("bar-note", rest > 0
      ? "Top " + top.length + " of " + countries.length + " countries. The full list is in “All countries” below."
      : "All " + countries.length + " countries by number of bans.");
    document.getElementById("bar-box").style.height = (top.length * 28 + 40) + "px";
    new Chart(document.getElementById("bar"), {
      type: "bar",
      data: {
        labels: top.map(function (c) { return c.name; }),
        datasets: [{ data: top.map(function (c) { return c.count; }), backgroundColor: v("--series-1"),
                     borderRadius: 4, borderSkipped: "start", maxBarThickness: 18, categoryPercentage: 0.8 }]
      },
      options: {
        indexAxis: "y", maintainAspectRatio: false, animation: false,
        plugins: { legend: { display: false }, tooltip: Object.assign({}, tooltipStyle, {
          callbacks: { label: function (ctx) { return n(ctx.parsed.x) + " bans · " + n(top[ctx.dataIndex].ips) + " IPs"; } } }) },
        scales: {
          x: { beginAtZero: true, grid: { color: v("--grid") }, border: { display: false }, ticks: { precision: 0 } },
          y: { grid: { display: false }, border: { color: v("--border") }, ticks: { color: v("--text-primary") } }
        }
      }
    });
  })();

  // ---- Over time: single line with crosshair-style index tooltip ----
  (function () {
    var t = data.timeline, weekly = t.granularity === "week";
    setText("line-note", (weekly ? "Bans per week" : "Bans per day") + ", " + t.labels[0] + " to " + t.labels[t.labels.length - 1] +
      ". The dashed end is the " + (weekly ? "current week" : "current day") + ", still in progress.");
    new Chart(document.getElementById("line"), {
      type: "line",
      data: { labels: t.labels, datasets: [{ data: t.values, borderColor: v("--series-1"), backgroundColor: v("--series-1"),
              borderWidth: 2, pointRadius: t.labels.length === 1 ? 4 : 0, pointHoverRadius: 5, pointHitRadius: 12, tension: 0,
              // The last point is the current day/week, which isn't over yet: draw its segment dashed.
              segment: { borderDash: function (ctx) { return ctx.p1DataIndex === t.labels.length - 1 ? [5, 4] : undefined; } } }] },
      options: {
        maintainAspectRatio: false, animation: false,
        interaction: { mode: "index", intersect: false },
        plugins: { legend: { display: false }, tooltip: Object.assign({}, tooltipStyle, {
          callbacks: {
            title: function (items) { return (weekly ? "Week of " : "") + items[0].label; },
            label: function (ctx) {
              var text = n(ctx.parsed.y) + (ctx.parsed.y === 1 ? " ban" : " bans");
              return ctx.dataIndex === t.labels.length - 1 ? text + " so far" : text;
            }
          } }) },
        scales: {
          x: { grid: { display: false }, border: { color: v("--border") }, ticks: { maxTicksLimit: 8, maxRotation: 0 } },
          y: { beginAtZero: true, grid: { color: v("--grid") }, border: { display: false }, ticks: { precision: 0 } }
        }
      }
    });
  })();

  // ---- Tables (built with textContent, so nothing from an email can inject markup) ----
  setText("ip-note", "The " + data.topIps.length + " addresses banned most often.");
  var ipRows = document.getElementById("ip-rows");
  data.topIps.forEach(function (r, i) {
    var tr = document.createElement("tr");
    cell(tr, String(i + 1), "num"); cell(tr, r.ip, "ip"); cell(tr, r.name);
    cell(tr, n(r.count), "num"); cell(tr, r.last); cell(tr, r.jails, "wrap-cell");
    ipRows.appendChild(tr);
  });

  var cRows = document.getElementById("country-rows");
  countries.forEach(function (c, i) {
    var tr = document.createElement("tr");
    cell(tr, String(i + 1), "num"); cell(tr, c.name); cell(tr, n(c.count), "num");
    cell(tr, n(c.ips), "num"); cell(tr, (100 * c.count / s.total).toFixed(1) + "%", "num");
    cRows.appendChild(tr);
  });
})();
</script>
</body>
</html>
""";
}
