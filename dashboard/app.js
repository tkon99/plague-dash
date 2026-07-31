/* Plague Dash — dashboard logic.
 *
 * Data arrives over a Server-Sent Events stream (/events) from the embedded mod
 * server. On connect the server replays the full in-memory history, then pushes
 * each new in-game-day sample live. No files, no polling, no disk. Charts are
 * created once then updated in place (chart.update('none')) to avoid flicker.
 *
 * Events: 'meta' (run metadata), 'sample' (one day), 'countries' (per-country
 * snapshot, periodic).
 *
 * Sample fields: day, infected, dead, inf, sev, let, cureNeed, cureSpd, curePct,
 *   airT, seaT, landT, mutCnt, continents[]
 * All numeric fields are optional — a missing field degrades gracefully.
 */

const SSE_URL = "/events";

// In-memory rows, fed by SSE events.
let rows = [];
let latestCountries = null;  // most recent per-country snapshot from SSE
let purchases = [];          // evolve/devolve event feed, fed by SSE

// Hard-cap samples kept in memory per chart for performance. When a run is long,
// we downsample to at most MAX_POINTS for display.
const MAX_POINTS = 600;

// ---- series colors (kept in sync with style.css :root) ----
const C = {
  inf: "#58a6ff", dead: "#f85149", cure: "#3fb950",
  sev: "#d29922", let: "#bc8cff",
  air: "#79c0ff", sea: "#56d4dd", land: "#ffa657",
  // continents — distinct hues
  ASIA: "#f97583", AFRICA: "#ffa657", EUROPE: "#79c0ff",
  NORTH_AMERICA: "#56d4dd", SOUTH_AMERICA: "#7ee787",
  grid: "rgba(139,148,158,0.12)",
  tick: "#8b949e",
};

// ---- global state ----
let charts = {};

// ============================================================================
// Formatting helpers
// ============================================================================
function fmtInt(n) {
  if (n == null || isNaN(n)) return "—";
  return Math.round(n).toLocaleString();
}

// Compact large numbers: 1.2M, 3.4B, 12K
function fmtCompact(n) {
  if (n == null || isNaN(n)) return "—";
  const abs = Math.abs(n);
  if (abs >= 1e9) return (n / 1e9).toFixed(2) + "B";
  if (abs >= 1e6) return (n / 1e6).toFixed(2) + "M";
  if (abs >= 1e3) return (n / 1e3).toFixed(1) + "K";
  return Math.round(n).toString();
}

function fmtPct(x) {
  if (x == null || isNaN(x)) return "—";
  return (x * 100).toFixed(1) + "%";
}

// Estimate days until cure completes: remaining / current research-per-day.
function cureEtaDays(latest) {
  const spd = latest.cureSpd;
  if (!spd || spd <= 0) return null;
  const remaining = (1 - (latest.curePct || 0)) * (latest.cureNeed || 0);
  const days = remaining / spd;
  if (!isFinite(days) || days > 9_999_999) return null;
  return days;
}

// ============================================================================
// Downsample (kept for chart rendering)
// ============================================================================
// Downsample to at most n points by stride, keeping the last point exact.
function downsample(arr, n) {
  if (arr.length <= n) return arr;
  const stride = Math.ceil(arr.length / n);
  const out = [];
  for (let i = 0; i < arr.length; i += stride) out.push(arr[i]);
  out.push(arr[arr.length - 1]);
  return out;
}

// ============================================================================
// Derived data
// ============================================================================
function derive(rows) {
  // spread speed: new infected per day = delta of infected between consecutive days
  const spread = new Array(rows.length).fill(null);
  for (let i = 1; i < rows.length; i++) {
    const a = rows[i - 1], b = rows[i];
    if (a.infected != null && b.infected != null && b.day > a.day) {
      spread[i] = Math.max(0, (b.infected - a.infected) / (b.day - a.day));
    }
  }
  return { spread };
}

// ============================================================================
// Chart factory
// ============================================================================
function baseChartOptions() {
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,          // no animation on update — instant refresh
    interaction: { mode: "index", intersect: false },
    plugins: {
      legend: { labels: { color: C.tick, boxWidth: 12, font: { size: 11 } } },
      tooltip: {
        backgroundColor: "#161b22",
        borderColor: "#30363d",
        borderWidth: 1,
        titleColor: "#e6edf3",
        bodyColor: "#e6edf3",
      },
    },
    scales: {
      x: {
        ticks: { color: C.tick, maxTicksLimit: 8, autoSkip: true },
        grid: { color: C.grid },
        title: { display: true, text: "In-game day", color: C.tick, font: { size: 11 } },
      },
      y: {
        ticks: { color: C.tick },
        grid: { color: C.grid },
      },
    },
  };
}

function makeDataset(label, color, data, yAxisID = "y") {
  return {
    label, data, yAxisID,
    borderColor: color,
    backgroundColor: color + "22",
    pointRadius: 0,
    borderWidth: 2,
    tension: 0.25,
    spanGaps: true,
  };
}

function buildCharts() {
  Chart.defaults.color = C.tick;
  Chart.defaults.font.family = "-apple-system, Segoe UI, Roboto, sans-serif";

  // 1. Cure Race — infected & dead on left axis, cure% on right axis.
  //    To compare wildly different magnitudes (billions vs 0-1) on shared lines,
  //    infected/dead are shown on a log-ish left axis, cure% on a 0..1 right axis.
  const raceOpts = baseChartOptions();
  raceOpts.scales = {
    x: raceOpts.scales.x,
    yInf: {
      type: "linear", position: "left",
      ticks: { color: C.tick, callback: (v) => fmtCompact(v) },
      grid: { color: C.grid },
      title: { display: true, text: "People", color: C.tick, font: { size: 11 } },
    },
    yCure: {
      type: "linear", position: "right", min: 0, max: 1,
      ticks: { color: C.cure, callback: (v) => Math.round(v * 100) + "%" },
      grid: { drawOnChartArea: false },
      title: { display: true, text: "Cure %", color: C.cure, font: { size: 11 } },
    },
  };
  charts.race = new Chart(document.getElementById("chart-race"), {
    type: "line",
    data: { datasets: [
      makeDataset("Infected", C.inf, [], "yInf"),
      makeDataset("Dead", C.dead, [], "yInf"),
      makeDataset("Cure %", C.cure, [], "yCure"),
    ]},
    options: raceOpts,
  });

  // 2. Plague stats — INF/SEV/LET share one axis
  charts.stats = new Chart(document.getElementById("chart-stats"), {
    type: "line",
    data: { datasets: [
      makeDataset("Infectivity", C.inf, []),
      makeDataset("Severity", C.sev, []),
      makeDataset("Lethality", C.let, []),
    ]},
    options: baseChartOptions(),
  });

  // 3. Cure effort — cureNeed & cureSpd (both large) on one axis
  const cureOpts = baseChartOptions();
  cureOpts.scales.y.ticks.callback = (v) => fmtCompact(v);
  cureOpts.scales.y.title = { display: true, text: "Research points", color: C.tick, font: { size: 11 } };
  charts.cure = new Chart(document.getElementById("chart-cure"), {
    type: "line",
    data: { datasets: [
      makeDataset("Effort needed", C.dead, []),
      makeDataset("Research speed", C.cure, []),
    ]},
    options: cureOpts,
  });

  // 4. Spread speed — new infections/day
  const spreadOpts = baseChartOptions();
  spreadOpts.scales.y.ticks.callback = (v) => fmtCompact(v);
  spreadOpts.scales.y.title = { display: true, text: "New infections / day", color: C.tick, font: { size: 11 } };
  charts.spread = new Chart(document.getElementById("chart-spread"), {
    type: "line",
    data: { datasets: [
      makeDataset("New infections / day", C.inf, []),
    ]},
    options: spreadOpts,
  });

  // 5. Transmission effectiveness
  charts.transmission = new Chart(document.getElementById("chart-transmission"), {
    type: "line",
    data: { datasets: [
      makeDataset("Air", C.air, []),
      makeDataset("Sea", C.sea, []),
      makeDataset("Land", C.land, []),
    ]},
    options: baseChartOptions(),
  });

  // 6. Continental spread — one line per continent (stacked-area feel via opacity).
  //    Datasets are created lazily once we know which continents exist.
  const contOpts = baseChartOptions();
  contOpts.scales.y.ticks.callback = (v) => fmtCompact(v);
  contOpts.scales.y.title = { display: true, text: "Infected", color: C.tick, font: { size: 11 } };
  charts.continents = new Chart(document.getElementById("chart-continents"), {
    type: "line",
    data: { datasets: [] },
    options: contOpts,
  });

  // 7. DNA economy — balance + total earned + total spent over time.
  const dnaOpts = baseChartOptions();
  dnaOpts.scales.y.ticks.callback = (v) => fmtCompact(v);
  dnaOpts.scales.y.title = { display: true, text: "DNA points", color: C.tick, font: { size: 11 } };
  charts.dna = new Chart(document.getElementById("chart-dna"), {
    type: "line",
    data: { datasets: [
      makeDataset("Balance", C.cure, []),
      makeDataset("Earned", C.inf, []),
      makeDataset("Spent", C.dead, []),
    ]},
    options: dnaOpts,
  });
}

// ============================================================================
// Render: push derived series into the charts
// ============================================================================
function render(rows, derived) {
  if (!rows.length) return;
  const ds = downsample(rows, MAX_POINTS);
  const sds = downsample(derived.spread, MAX_POINTS);
  const labels = ds.map((r) => r.day);

  const set = (chart, idx, data) => { chart.data.datasets[idx].data = data; };
  chartLabels(charts.race, labels);
  set(charts.race, 0, ds.map((r) => r.infected ?? null));
  set(charts.race, 1, ds.map((r) => r.dead ?? null));
  set(charts.race, 2, ds.map((r) => r.curePct ?? null));

  chartLabels(charts.stats, labels);
  set(charts.stats, 0, ds.map((r) => r.inf ?? null));
  set(charts.stats, 1, ds.map((r) => r.sev ?? null));
  set(charts.stats, 2, ds.map((r) => r.let ?? null));

  chartLabels(charts.cure, labels);
  set(charts.cure, 0, ds.map((r) => r.cureNeed ?? null));
  set(charts.cure, 1, ds.map((r) => r.cureSpd ?? null));

  // spread needs aligned downsampling of the derived array; simplest is to
  // recompute spread from the downsampled rows so indices line up.
  const sfrom = downsampledSpread(ds);
  chartLabels(charts.spread, labels);
  set(charts.spread, 0, sfrom);

  chartLabels(charts.transmission, labels);
  set(charts.transmission, 0, ds.map((r) => r.airT ?? null));
  set(charts.transmission, 1, ds.map((r) => r.seaT ?? null));
  set(charts.transmission, 2, ds.map((r) => r.landT ?? null));

  renderContinents(ds);

  // DNA economy
  chartLabels(charts.dna, labels);
  set(charts.dna, 0, ds.map((r) => r.dna ?? null));
  set(charts.dna, 1, ds.map((r) => r.dnaEarned ?? null));
  set(charts.dna, 2, ds.map((r) => r.dnaSpent ?? null));

  for (const k in charts) charts[k].update("none");
}

// Continents: discover which continent names appear across the data, ensure each
// has a dataset, and plot infected-per-continent over time. A toggle switches the
// y-values between absolute infected and % of that continent's population.
let continentsPct = false;  // false = absolute infected, true = % of pop

function renderContinents(ds) {
  if (!charts.continents) return;
  const names = new Set();
  for (const r of ds) if (r.continents) for (const c of r.continents) names.add(c.n);
  if (!names.size) return;

  const existing = new Map(charts.continents.data.datasets.map((d, i) => [d.label, i]));
  // ensure a dataset per continent name
  for (const n of names) {
    if (!existing.has(n)) {
      const color = C[n] || "#8b949e";
      charts.continents.data.datasets.push(makeDataset(n, color, []));
    }
  }
  // map name -> dataset index (post-add)
  const idx = new Map(charts.continents.data.datasets.map((d, i) => [d.label, i]));
  // zero-fill all continent datasets, then set from data (absolute or %)
  for (const n of names) {
    const i = idx.get(n);
    charts.continents.data.datasets[i].data = ds.map((r) => {
      const c = (r.continents || []).find((x) => x.n === n);
      if (!c) return null;
      if (continentsPct) return c.pop > 0 ? (c.inf / c.pop) * 100 : null;
      return c.inf ?? null;
    });
  }
  charts.continents.data.labels = ds.map((r) => r.day);
  // axis label follows the mode
  charts.continents.options.scales.y.title.text =
    continentsPct ? "% of continent population" : "Infected";
  charts.continents.options.scales.y.ticks.callback = continentsPct
    ? (v) => v + "%"
    : (v) => fmtCompact(v);
}

// Click the "Continental Spread" sub-label to toggle absolute ↔ %.
function setupContinentsToggle() {
  const el = document.getElementById("continents-toggle");
  if (!el) return;
  el.addEventListener("click", () => {
    continentsPct = !continentsPct;
    el.textContent = continentsPct
      ? "% of population infected, per continent (click for absolute)"
      : "Infected by continent over time (click for %)";
    // re-render from current rows
    if (rows.length) render(rows, derive(rows));
  });
}

function chartLabels(chart, labels) {
  chart.data.labels = labels;
}

// Recompute spread from an already-downsampled row set so it aligns with labels.
function downsampledSpread(ds) {
  const out = new Array(ds.length).fill(null);
  for (let i = 1; i < ds.length; i++) {
    const a = ds[i - 1], b = ds[i];
    if (a.infected != null && b.infected != null && b.day > a.day) {
      out[i] = Math.max(0, (b.infected - a.infected) / (b.day - a.day));
    }
  }
  return out;
}

// ============================================================================
// KPI strip + run header
// ============================================================================
function updateKpis(rows) {
  if (!rows.length) return;
  const latest = rows[rows.length - 1];
  const worldPop = 7_000_000_000;
  const infPct = latest.infected != null ? latest.infected / worldPop : null;

  setText("k-infected", fmtCompact(latest.infected));
  setText("k-infected-pct", infPct != null ? fmtPct(infPct) + " of world" : "—");
  setText("k-dead", fmtCompact(latest.dead));
  setText("k-dead-pct", latest.dead != null && latest.infected
    ? fmtPct(latest.dead / (latest.dead + latest.infected)) + " of cases" : "—");
  setText("k-day", latest.day ?? "—");

  const eta = cureEtaDays(latest);
  setText("k-cure", fmtPct(latest.curePct));
  setText("k-cure-eta", eta != null
    ? (eta < 1 ? "<1 day left" : `~${Math.round(eta)} days left`)
    : "—");

  // spread = last day's delta
  if (rows.length >= 2) {
    const prev = rows[rows.length - 2];
    const d = (latest.infected - prev.infected) / Math.max(1, latest.day - prev.day);
    setText("k-spread", fmtCompact(Math.max(0, d)) + "/day");
  }
}

function applyMeta(meta) {
  if (!meta) return;
  setText("m-plague", meta.plagueType || "—");
  setText("m-diff", meta.difficulty || "—");
  setText("m-origin", meta.startCountry || "—");
  setText("m-status", meta.status || "—");
}

function setText(id, val) {
  const el = document.getElementById(id);
  if (el) el.textContent = val;
}

function setBadge(cls, text) {
  const b = document.getElementById("live-badge");
  b.className = "badge " + cls;
  b.textContent = text;
}

// ============================================================================
// Trait Planner — what can I buy, what does it cost, what does it do.
// ============================================================================
function renderTechPlanner(techs) {
  const wrap = document.getElementById("tech-planner");
  if (!wrap) return;
  const dna = rows.length ? rows[rows.length - 1].dna : null;

  // sort: affordable first, then by cost
  const sorted = techs.slice().sort((a, b) => {
    const aAff = dna != null && a.cost <= dna;
    const bAff = dna != null && b.cost <= dna;
    if (aAff !== bAff) return aAff ? -1 : 1;
    return a.cost - b.cost;
  });

  const rowsHtml = sorted.map((t) => {
    const affordable = dna != null && t.cost <= dna;
    return `<tr>
      <td>${escapeHtml(t.name || t.id)}</td>
      <td>${escapeHtml(t.category || "")}</td>
      <td class="num ${affordable ? "affordable" : "unaffordable"}">${Math.round(t.cost)} DNA</td>
      <td class="eff">${techEffects(t)}</td>
    </tr>`;
  }).join("");

  wrap.innerHTML = `<table class="tech-table">
    <thead><tr><th>Trait</th><th>Category</th><th class="num">Cost</th><th>Effect</th></tr></thead>
    <tbody>${rowsHtml}</tbody>
  </table>`;
  setText("techs-sub", `${sorted.length} evolvable traits` + (dna != null ? ` · you have ${Math.round(dna)} DNA` : ""));
}

// Summarize a tech's non-zero stat deltas as a compact "+x inf, +y air, −z cure" string.
function techEffects(t) {
  const parts = [];
  const add = (label, v, negate) => {
    if (!v || Math.abs(v) < 0.001) return;
    const val = negate ? -v : v;
    const sign = val > 0 ? "+" : "";
    parts.push(`<span class="${val > 0 ? "pos" : "neg"}">${sign}${fmtNum(val)} ${label}</span>`);
  };
  add("inf", t.inf); add("sev", t.sev); add("let", t.let);
  add("air", t.air); add("sea", t.sea); add("land", t.land); add("corpse", t.corpse);
  add("wealthy", t.wealthy); add("poverty", t.poverty); add("urban", t.urban); add("rural", t.rural);
  add("hot", t.hot); add("cold", t.cold); add("arid", t.arid); add("humid", t.humid);
  // cureMulti >1 = cure harder (worse); researchIneff >0 = slower research (worse)
  if (t.cureMulti) add("cure need", t.cureMulti - 1, false);
  if (t.researchIneff) add("cure speed", t.researchIneff, true);
  return parts.join(" ") || "<span class='eff'>no stat change</span>";
}

function fmtNum(v) {
  if (Math.abs(v) >= 100) return Math.round(v).toString();
  if (Math.abs(v) >= 10) return v.toFixed(1);
  return v.toFixed(2);
}

// ============================================================================
// DNA purchase feed — render the evolve/devolve event log.
// ============================================================================
function ensureFeedList() {
  const wrap = document.getElementById("purchase-feed");
  if (!wrap) return null;
  let ul = wrap.querySelector(".purchase-feed");
  if (!ul) {
    ul = document.createElement("ul");
    ul.className = "purchase-feed";
    wrap.appendChild(ul);
  }
  return ul;
}

function appendPurchaseRow(p) {
  const ul = ensureFeedList();
  if (!ul) return;
  const isDevolve = p.action === "devolve";
  const li = document.createElement("li");
  li.className = p.action;
  const cost = isDevolve ? p.cost : -p.cost; // spend negative, refund positive
  li.innerHTML =
    `<span class="day">D${p.day}</span>` +
    `<span><span class="name">${escapeHtml(p.name || "?")}</span>` +
    `<span class="cat">${escapeHtml(p.category || "")}</span></span>` +
    `<span class="cost ${cost >= 0 ? "refund" : "spend"}">` +
    `${cost >= 0 ? "+" : ""}${Math.round(cost)} DNA</span>`;
  ul.appendChild(li);
  // keep the feed scrolled to the newest
  ul.parentElement.scrollTop = ul.parentElement.scrollHeight;

  const n = purchases.length;
  setText("purchases-sub", `${n} purchase${n === 1 ? "" : "s"} · latest day ${p.day}`);
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
// ============================================================================
function renderCountryTable(snap) {
  const cs = (snap.countries || []).slice().sort((a, b) => (b.inf || 0) - (a.inf || 0));
  const tbody = document.querySelector("#country-table tbody");
  if (!tbody) return;
  let html = "";
  for (const c of cs) {
    const pct = c.pop ? Math.min(100, (c.inf / c.pop) * 100) : 0;
    html += `<tr>
      <td>${c.name || c.id}</td>
      <td class="num">${fmtCompact(c.inf)}</td>
      <td class="num">${pct.toFixed(1)}%<span class="pct-bar" style="--pct:${pct}%"></span></td>
      <td>${(c.continent || "").replace("_", " ").toLowerCase()}</td>
    </tr>`;
  }
  tbody.innerHTML = html;
  setText("countries-sub", `Latest snapshot — day ${snap.day} · ${cs.length} countries`);
}

// ============================================================================
// SSE stream consumer
// The server sends typed events: 'meta', 'sample', 'countries'. On connect it
// replays the full in-memory history; thereafter each new in-game-day sample
// arrives as one 'sample' event. EventSource auto-reconnects if the stream drops.
// ============================================================================
let renderScheduled = false;

function connectSSE() {
  setBadge("", "connecting…");
  setText("footer-status", "Connecting to game…");

  const es = new EventSource(SSE_URL);

  es.addEventListener("open", () => {
    setBadge("live", rows.length ? "● LIVE" : "waiting…");
  });

  es.addEventListener("meta", (e) => {
    try { applyMeta(JSON.parse(e.data)); } catch { }
  });

  es.addEventListener("sample", (e) => {
    try {
      const s = JSON.parse(e.data);
      // On a fresh replay the server sends the whole history; if a sample's day
      // is <= our last seen, we may be reconnecting — rebuild when that happens.
      if (rows.length && s.day <= rows[rows.length - 1].day && s.day <= 1) {
        rows = []; // reset for a new run/replay
      }
      rows.push(s);
      scheduleRender();
    } catch { }
  });

  es.addEventListener("countries", (e) => {
    try {
      latestCountries = JSON.parse(e.data);
      renderCountryTable(latestCountries);
    } catch { }
  });

  es.addEventListener("purchase", (e) => {
    try {
      const p = JSON.parse(e.data);
      // On a fresh replay the whole feed is resent; if a purchase's day is earlier
      // than our feed's earliest, treat it as a replay and reset.
      if (purchases.length && p.day < purchases[0].day) purchases = [];
      purchases.push(p);
      appendPurchaseRow(p);
    } catch { }
  });

  es.addEventListener("techs", (e) => {
    try { renderTechPlanner(JSON.parse(e.data)); } catch { }
  });

  es.addEventListener("error", () => {
    // EventSource auto-reconnects; surface the state without spamming.
    setBadge("error", "reconnecting…");
    setText("footer-status", "Stream dropped — reconnecting…");
  });
}

// Coalesce rapid SSE bursts into one render frame (a history replay can send
// hundreds of 'sample' events back-to-back; we only want one chart redraw).
function scheduleRender() {
  if (renderScheduled) return;
  renderScheduled = true;
  requestAnimationFrame(() => {
    renderScheduled = false;
    if (rows.length) {
      render(rows, derive(rows));
      updateKpis(rows);
      const latest = rows[rows.length - 1];
      setBadge("live", "● LIVE");
      setText("footer-status",
        `${rows.length} samples · day ${latest.day} · updated ${new Date().toLocaleTimeString()}`);
    }
  });
}

// ============================================================================
// Export: download the whole run as JSON (samples + purchases + countries + meta)
// for offline analysis. Everything is already in memory from the SSE stream.
// ============================================================================
function setupExport() {
  const btn = document.getElementById("export-btn");
  if (!btn) return;
  btn.addEventListener("click", (e) => {
    e.preventDefault();
    try {
      const meta = {
        plagueType: (document.getElementById("m-plague") || {}).textContent || "",
        difficulty: (document.getElementById("m-diff") || {}).textContent || "",
        origin: (document.getElementById("m-origin") || {}).textContent || "",
        status: (document.getElementById("m-status") || {}).textContent || "",
      };
      const payload = {
        exportedAt: new Date().toISOString(),
        meta,
        samples: rows,
        purchases: purchases,
        countries: latestCountries,
        summary: rows.length ? {
          day: rows[rows.length - 1].day,
          samples: rows.length,
          purchases: purchases.length,
        } : null,
      };
      const json = JSON.stringify(payload, null, 2);
      const blob = new Blob([json], { type: "application/json" });
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      const day = rows.length ? rows[rows.length - 1].day : "start";
      a.download = `plague-dash-run-day${day}.json`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(a.href);
      setText("footer-status", `Exported ${rows.length} samples · day ${day}`);
    } catch (err) {
      setText("footer-status", "Export failed: " + err.message);
    }
  });
}

// ============================================================================
// Boot
// ============================================================================
document.addEventListener("DOMContentLoaded", () => {
  buildCharts();
  setupContinentsToggle();
  setupExport();
  connectSSE();
});
