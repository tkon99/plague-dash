/* Plague Dash — dashboard logic.
 *
 * Data arrives over a Server-Sent Events stream (/events) from the embedded mod
 * server. On connect the server replays the full in-memory history, then pushes
 * each new in-game-day sample live. No files, no polling, no disk.
 *
 * Events:
 *   'meta'      run metadata (plague, difficulty, origin, status)
 *   'sample'    one in-game day
 *   'countries' per-country snapshot (periodic)
 *   'purchase'  one evolve/devolve event
 *   'techs'     the evolvable-trait list (costs + effects)
 *   'archive'   a completed run, archived when a new game starts (session history)
 *
 * Sample fields: day, infected, dead, inf, sev, let, cureNeed, cureSpd, curePct,
 *   dna, dnaEarned, dnaSpent, airT, seaT, landT, corpseT, continents[]
 * (mutCnt is collected but not rendered — it resets after some evolves and has
 * no decision audience.)
 * All numeric fields are optional — a missing field degrades gracefully.
 *
 * Layout philosophy (no tabs): every view answers one question —
 *   The Race        → am I winning?
 *   Plague Triangle → what kind of plague am I?
 *   DNA Flow        → can I afford the next buy?
 *   Continent Health→ where does the world stand?
 *   Trait Timeline  → what did I do, when?
 *   Planner + Countries → what should I do next / where exactly?
 */

const SSE_URL = "/events";

// ---- series colors (kept in sync with style.css :root) ----
const C = {
  inf: "#58a6ff", dead: "#f85149", cure: "#3fb950",
  sev: "#d29922", let: "#bc8cff",
  air: "#79c0ff", sea: "#56d4dd", land: "#ffa657",
  grid: "rgba(139,148,158,0.12)",
  tick: "#8b949e",
};
const CAT_COLORS = {
  Symptom: "#2ea043",
  Transmission: "#58a6ff",
  Ability: "#d29922",
};

// Hard-cap samples kept in memory per chart for performance. When a run is long,
// we downsample to at most MAX_POINTS for display.
const MAX_POINTS = 600;
const WORLD_POP = 7_000_000_000;

// ---- multi-run state ----
// runs.live is fed by SSE; runs.list holds archived runs (from 'archive' events).
const runs = {
  live: { id: "live", meta: {}, samples: [], purchases: [], countries: null, techs: [] },
  list: [],                       // archived runs, oldest first
};
let selected = "live";            // which run the charts currently show

function curRun() {
  return selected === "live" ? runs.live : runs.list.find((r) => r.id === selected) || runs.live;
}

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

// Estimate days until cure completes from the recent cure-progress pace
// (linear extrapolation of remaining_effort / speed gives nonsense values,
// because research speed grows super-linearly as more countries join in).
function cureEtaDays(samples) {
  if (!samples.length) return null;
  const latest = samples[samples.length - 1];
  const pct = latest.curePct;
  if (pct == null || pct >= 1) return null;
  // pace anchor: the earliest sample within the last ~10 days that has curePct
  let a = latest;
  for (let i = samples.length - 1; i >= 0 && samples[i].day >= latest.day - 10; i--) {
    if (samples[i].curePct != null) a = samples[i];
  }
  if (a === latest) {
    // no window yet — use the previous curePct sample
    let prev = null;
    for (let i = samples.length - 2; i >= 0; i--) {
      if (samples[i].curePct != null) { prev = samples[i]; break; }
    }
    if (!prev || prev === latest) return null;
    a = prev;
  }
  const dd = latest.day - a.day;
  const dp = pct - a.curePct;
  if (dd <= 0 || dp <= 0) return null;
  const days = ((1 - pct) / dp) * dd;
  if (!isFinite(days) || days > 9999) return null;
  return days;
}

// Approximate world population from continent pops when available (fallback 7B).
function worldPop(samples) {
  if (!samples.length) return WORLD_POP;
  const cs = samples[samples.length - 1].continents;
  if (!cs || !cs.length) return WORLD_POP;
  const sum = cs.reduce((a, c) => a + (c.pop || 0), 0);
  return sum > 0 ? sum : WORLD_POP;
}

// ============================================================================
// Downsample to at most n points by stride, keeping the last point exact.
// ============================================================================
function downsample(arr, n) {
  if (arr.length <= n) return arr;
  const stride = Math.ceil(arr.length / n);
  const out = [];
  for (let i = 0; i < arr.length; i += stride) out.push(arr[i]);
  out.push(arr[arr.length - 1]);
  return out;
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

  // --- Evolve-events plugin: draws a thin dashed amber vertical line at each
  // evolve day on the time-series charts, with a small horizontal label near the
  // top naming the trait(s) evolved that day. When labels would overlap they
  // cascade downwards into stacked rows (each row reserved greedily, left to
  // right, so a dense cluster of evolves doesn't collide). Makes the causal
  // story visible ("evolved Total Organ Failure here → lethality spiked").
  // Reads events from chart.options.plugins.evolveEvents.events: [{day,label}],
  // one per day (collated upstream so same-day multi-evolves read "Name +N"). ---
  if (!Chart.registry.plugins.get("evolveEvents")) {
    Chart.register({
      id: "evolveEvents",
      afterDatasetsDraw(chart) {
        const opts = chart.options.plugins && chart.options.plugins.evolveEvents;
        if (!opts || !opts.enabled || !opts.events || !opts.events.length) return;
        const ctx = chart.ctx;
        const xScale = chart.scales.x;
        if (!xScale) return;
        const labels = chart.data.labels || [];
        const dayToIndex = new Map();
        for (let i = 0; i < labels.length; i++) dayToIndex.set(Number(labels[i]), i);
        const top = chart.chartArea.top;
        const bottom = chart.chartArea.bottom;
        const left = chart.chartArea.left;
        const right = chart.chartArea.right;

        ctx.save();
        ctx.font = "9px -apple-system, Segoe UI, Roboto, sans-serif";
        ctx.textAlign = "left";
        ctx.textBaseline = "top";

        // First pass: resolve x-pixel + measured width for each in-view event.
        const PAD = 3;          // horizontal padding inside the label box
        const ROW_H = 11;       // row height (line + gap)
        const MAX_ROWS = 6;     // don't let a cluster bury the chart
        const placed = [];      // {x, w, label, row}
        const rowRightEdge = []; // rightmost x occupied on each row so far
        const inView = opts.events
          .map((ev) => ({ ev, idx: dayToIndex.get(ev.day) }))
          .filter((o) => o.idx != null)
          .map((o) => {
            const x = xScale.getPixelForValue(o.idx);
            return { ev: o.ev, x };
          })
          .filter((o) => o.x >= left && o.x <= right);
        for (const o of inView) {
          const text = o.ev.label || "";
          const w = ctx.measureText(text).width + PAD * 2;
          // Greedily pick the lowest row whose right edge is clear of this label's
          // left edge, so labels cascade down only as far as needed to avoid overlap.
          let row = 0;
          while (row < MAX_ROWS && rowRightEdge[row] != null && rowRightEdge[row] + 2 > o.x) row++;
          if (row >= MAX_ROWS) row = MAX_ROWS - 1; // give up avoiding overlap past the cap
          // If this label would run off the right edge, right-align it to the line.
          let labelX = o.x + PAD;
          if (labelX + w - PAD > right) labelX = right - w + PAD;
          placed.push({ x: o.x, labelX, w, label: text, row });
          rowRightEdge[row] = labelX + w - PAD;
        }

        // Dashed lines first (under the labels), full chart height.
        ctx.setLineDash([3, 3]);
        ctx.lineWidth = 1;
        ctx.strokeStyle = "rgba(210,153,34,0.45)";
        for (const p of placed) {
          ctx.beginPath();
          ctx.moveTo(p.x, top);
          ctx.lineTo(p.x, bottom);
          ctx.stroke();
        }
        ctx.setLineDash([]);

        // Labels near the top, cascading down by row.
        ctx.fillStyle = "rgba(210,153,34,0.95)";
        for (const p of placed) {
          ctx.fillText(p.label, p.labelX, top + 3 + p.row * ROW_H);
        }
        ctx.restore();
      },
    });
  }

  // 1. The Race — headline. Infected & Dead (left axis, people) vs. cure %
  //    (right axis). The tug-of-war gauge above shows only dead-vs-cure; this
  //    chart also tracks infections so you see the full outbreak curve.
  const raceOpts = baseChartOptions();
  raceOpts.scales = {
    x: raceOpts.scales.x,
    yPeople: {
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
      makeDataset("Infected", C.inf, [], "yPeople"),
      makeDataset("Dead", C.dead, [], "yPeople"),
      makeDataset("Cure %", C.cure, [], "yCure"),
    ]},
    options: Object.assign(raceOpts, {
      plugins: Object.assign(raceOpts.plugins, {
        evolveEvents: { enabled: true, events: [] },
      }),
    }),
  });

  // 2. Plague Stats — infectivity, severity, lethality over time (line chart).
  //    These are the game's live recomputed totals, so the lines show the
  //    ramps as the outbreak matures (e.g. a lethality spike after evolving
  //    Total Organ Failure) — far more informative than a uniform radar.
  const statsOpts = baseChartOptions();
  statsOpts.plugins.evolveEvents = { enabled: true, events: [] };
  charts.stats = new Chart(document.getElementById("chart-stats"), {
    type: "line",
    data: { datasets: [
      makeDataset("Infectivity", C.inf, []),
      makeDataset("Severity", C.sev, []),
      makeDataset("Lethality", C.let, []),
    ]},
    options: statsOpts,
  });

  // 3. Continent Health — % of each continent's population affected over time.
  //    A 100% stacked view per continent: the infected wave, then the dead
  //    wave as it overtakes. Click a continent in the legend to isolate it.
  const contOpts = baseChartOptions();
  contOpts.scales.y.min = 0;
  contOpts.scales.y.max = 100;
  contOpts.scales.y.ticks.callback = (v) => v + "%";
  contOpts.scales.y.title = { display: true, text: "% of continent population", color: C.tick, font: { size: 11 } };
  charts.continents = new Chart(document.getElementById("chart-continents"), {
    type: "line",
    data: { datasets: [] },
    options: contOpts,
  });

  // 4. Trait Timeline — every evolve/devolve as an event on the day axis.
  //    y = category row (Ability / Transmission / Symptom), size ∝ cost,
  //    hollow = devolved. Per-point styling lives on the data objects.
  const tlOpts = baseChartOptions();
  tlOpts.scales.y.min = 0;
  tlOpts.scales.y.max = 2;
  tlOpts.scales.y.ticks.stepSize = 1;
  tlOpts.scales.y.ticks.callback = (v) =>
    ({ 0: "Ability", 1: "Transmission", 2: "Symptom" })[v] || "";
  tlOpts.scales.y.title = { display: false };
  tlOpts.scales.x.title.text = "In-game day";
  tlOpts.interaction = { mode: "nearest", intersect: true };
  charts.timeline = new Chart(document.getElementById("chart-timeline"), {
    type: "scatter",
    data: { datasets: [{
      label: "Traits",
      data: [],
      showLine: false,
      pointRadius: 5,
      pointHoverRadius: 8,
      borderWidth: 2,
      // per-point colors/sizes are set on each data object
    }]},
    options: Object.assign(tlOpts, {
      plugins: Object.assign(tlOpts.plugins, {
        legend: { display: false },
        tooltip: Object.assign(tlOpts.plugins.tooltip, {
          callbacks: {
            title: (items) => items.length ? "Day " + items[0].raw.x : "",
            label: (ctx) => {
              const p = ctx.raw;
              const spend = p.action === "devolve" ? "+" : "";
              return `${p.action === "devolve" ? "Devolve" : "Evolve"} — ${p.name}` +
                     ` · ${spend}${Math.round(p.cost)} DNA`;
            },
          },
        }),
      }),
    }),
  });
}

// ============================================================================
// Custom (DOM) graphics
// ============================================================================

// --- The Race: a single bar split by the dead:cure ratio. Each side's share
// is its absolute % over the sum of both — so whoever's dominating takes more
// of the bar. The end labels still show the absolute %s (the facts); the gauge
// visualizes who's winning the race so far. ---
function renderRaceGauge(run) {
  const samples = run.samples;
  if (!samples.length) return;
  const latest = samples[samples.length - 1];

  const cure = Math.max(0, Math.min(1, latest.curePct || 0));
  const wp = worldPop(samples);
  const dead = Math.max(0, Math.min(1, wp > 0 ? (latest.dead || 0) / wp : 0));

  const el = (id) => document.getElementById(id);
  // proportional shares: dead% / (dead% + cure%) and cure% / (dead% + cure%)
  const total = dead + cure;
  const deadShare = total > 0 ? (dead / total) * 100 : 0;
  const cureShare = total > 0 ? (cure / total) * 100 : 0;
  el("race-dead").style.width = deadShare + "%";
  el("race-cure").style.width = cureShare + "%";
  // labels keep the absolute %s
  el("race-dead-pct").textContent = fmtPct(dead);
  el("race-cure-pct").textContent = fmtPct(cure);

  const verdict = el("race-verdict");
  if (dead >= 0.999) {
    verdict.textContent = "world dead — plague won";
  } else if (cure >= 0.999) {
    verdict.textContent = "cure complete — plague lost";
  } else if (total === 0) {
    verdict.textContent = "waiting for data…";
  } else if (dead > cure) {
    verdict.textContent = `death ahead by ${fmtPct(dead - cure)}`;
  } else {
    verdict.textContent = `cure ahead by ${fmtPct(cure - dead)}`;
  }
}

// --- DNA Flow: earned → spent → balance as one stacked bar, plus income rate
// and a sparkline of the economy over time. ---
let _sparkW = 0;

function renderDnaFlow(run) {
  const samples = run.samples;
  if (!samples.length) return;
  const latest = samples[samples.length - 1];

  const earned = latest.dnaEarned, spent = latest.dnaSpent, bal = latest.dna;
  const set = (id, v) => {
    const el = document.getElementById(id);
    if (el) el.textContent = v;
  };
  set("dna-earned", fmtCompact(earned));
  set("dna-spent", fmtCompact(spent));
  set("dna-balance", fmtCompact(bal));

  // income/day: dnaEarned delta over the last ~7 days
  let rate = null;
  if (earned != null && samples.length >= 2) {
    const a = samples[Math.max(0, samples.length - 8)], b = latest;
    const dd = b.day - a.day;
    if (dd > 0 && a.dnaEarned != null) rate = (b.dnaEarned - a.dnaEarned) / dd;
  }
  set("dna-rate", rate != null ? "+" + fmtCompact(rate) + "/d" : "—");

  // stacked proportion bar (spent portion + balance portion of all earned)
  const spentSeg = document.getElementById("dna-spent-seg");
  const balSeg = document.getElementById("dna-balance-seg");
  if (spentSeg && earned > 0) {
    const spentW = Math.min(100, ((spent || 0) / earned) * 100);
    spentSeg.style.width = spentW + "%";
    balSeg.style.width = Math.max(0, 100 - spentW) + "%";
  } else if (spentSeg) {
    spentSeg.style.width = "0%";
    balSeg.style.width = "0%";
  }

  drawSpark(samples);
}

// DNA economy sparkline as a real Chart.js chart (earned area + spent line).
// Built lazily on first render, then updated in place.
function drawSpark(samples) {
  if (!samples.length) return;
  const ds = downsample(samples, 300);
  const labels = ds.map((s) => s.day);
  if (!charts.spark) {
    const opts = baseChartOptions();
    opts.scales.y.ticks.callback = (v) => fmtCompact(v);
    opts.scales.y.title = { display: true, text: "DNA", color: C.tick, font: { size: 11 } };
    opts.plugins.legend.display = false;
    charts.spark = new Chart(document.getElementById("spark-dna"), {
      type: "line",
      data: { labels, datasets: [
        { label: "Earned", data: ds.map((s) => s.dnaEarned ?? null),
          borderColor: C.inf, backgroundColor: C.inf + "22", fill: true,
          pointRadius: 0, borderWidth: 2, tension: 0.25, spanGaps: true },
        { label: "Spent", data: ds.map((s) => s.dnaSpent ?? null),
          borderColor: C.dead, backgroundColor: "transparent",
          pointRadius: 0, borderWidth: 1.5, tension: 0.25, spanGaps: true },
      ]},
      options: opts,
    });
  } else {
    charts.spark.data.labels = labels;
    charts.spark.data.datasets[0].data = ds.map((s) => s.dnaEarned ?? null);
    charts.spark.data.datasets[1].data = ds.map((s) => s.dnaSpent ?? null);
    charts.spark.update("none");
  }
}

// --- Continent Health: % of each continent's population affected over time.
// For every continent we plot two lines: % infected (current cases) and
// % dead. The infected line rises first, then falls as the dead line overtakes
// it — the wave moving across each continent. ---
function renderContinentBars(run) {
  const chart = charts.continents;
  if (!chart) return;
  const samples = run.samples;
  // discover continent names present anywhere in the run
  const names = new Set();
  for (const s of samples) if (s.continents) for (const c of s.continents) names.add(c.n);
  if (!names.size) return;
  const ds = downsample(samples, MAX_POINTS);
  const labels = ds.map((s) => s.day);
  // ensure two datasets (infected + dead) per continent, colored consistently
  const existing = new Map(chart.data.datasets.map((d) => [d.label, d]));
  const desired = [];
  for (const n of [...names].sort()) {
    const base = CONT_COLORS[n] || C.tick;
    const infLabel = n + " · infected";
    const deadLabel = n + " · dead";
    desired.push(existing.get(infLabel) || makeDataset(infLabel, base, []));
    desired.push(existing.get(deadLabel) || makeDataset(deadLabel, shade(base, 0.55), []));
  }
  // fill each dataset's y-values as % of that continent's population
  for (const n of [...names].sort()) {
    const pop = (ds.find((s) => s.continents && s.continents.find((c) => c.n === n)) || {})
      .continents?.find?.((c) => c.n === n)?.pop || 0;
    const inf = desired.find((d) => d.label === n + " · infected");
    const dead = desired.find((d) => d.label === n + " · dead");
    if (inf) inf.data = ds.map((s) => {
      const c = (s.continents || []).find((x) => x.n === n);
      return c && pop > 0 ? Math.min(100, ((c.inf || 0) / pop) * 100) : null;
    });
    if (dead) dead.data = ds.map((s) => {
      const c = (s.continents || []).find((x) => x.n === n);
      return c && pop > 0 ? Math.min(100, ((c.dead || 0) / pop) * 100) : null;
    });
  }
  chart.data.datasets = desired;
  chart.data.labels = labels;
}

// --- Transmission colors per continent + a brightness shade helper for the
// "dead" companion line of each continent in the Continent Health chart. ---
const CONT_COLORS = {
  ASIA: "#f97583", AFRICA: "#ffa657", EUROPE: "#79c0ff",
  NORTH_AMERICA: "#56d4dd", SOUTH_AMERICA: "#7ee787",
};
function shade(hex, factor) {
  // factor < 1 darkens, > 1 lightens (clamped). Simple linear toward black/white.
  const n = parseInt(hex.slice(1), 16);
  let r = (n >> 16) & 255, g = (n >> 8) & 255, b = n & 255;
  const f = (c) => Math.max(0, Math.min(255, Math.round(c * factor)));
  return "#" + ((1 << 24) + (f(r) << 16) + (f(g) << 8) + f(b)).toString(16).slice(1);
}

// ============================================================================
// Chart updates (Chart.js)
// ============================================================================
function renderCharts(run) {
  if (!run.samples.length) return;
  const ds = downsample(run.samples, MAX_POINTS);
  const labels = ds.map((r) => r.day);

  const set = (chart, idx, data) => { chart.data.datasets[idx].data = data; };
  // Race: infected + dead (left axis, people) vs cure % (right axis).
  // The gauge above stays dead-vs-cure; the chart shows the full outbreak curve.
  chartLabels(charts.race, labels);
  set(charts.race, 0, ds.map((r) => r.infected ?? null));
  set(charts.race, 1, ds.map((r) => r.dead ?? null));
  set(charts.race, 2, ds.map((r) => r.curePct ?? null));

  // Plague Stats: infectivity / severity / lethality over time
  chartLabels(charts.stats, labels);
  set(charts.stats, 0, ds.map((r) => r.inf ?? null));
  set(charts.stats, 1, ds.map((r) => r.sev ?? null));
  set(charts.stats, 2, ds.map((r) => r.let ?? null));

  // Evolve events: collate evolves to one labeled event per day (devolves
  // excluded — they revert rather than cause) and feed to the plugin on both
  // time-series charts. Same-day multi-evolves read "FirstName +N".
  const evolveEvents = collateEvolveEvents(run.purchases);
  if (charts.race.options.plugins.evolveEvents)
    charts.race.options.plugins.evolveEvents.events = evolveEvents;
  if (charts.stats.options.plugins.evolveEvents)
    charts.stats.options.plugins.evolveEvents.events = evolveEvents;

  renderContinentBars(run);
  renderTimeline(run);

  for (const k in charts) charts[k].update("none");
}

// Group evolve purchases by day → one {day,label} per day. Single evolve:
// the trait name. Multiple: "FirstName +N" (e.g. day 498's 6 symptoms).
function collateEvolveEvents(purchases) {
  const byDay = new Map();
  for (const p of purchases) {
    if (p.action !== "evolve") continue;
    if (!byDay.has(p.day)) byDay.set(p.day, []);
    byDay.get(p.day).push(p.name || "?");
  }
  const out = [];
  for (const [day, names] of byDay) {
    const label = names.length === 1 ? names[0] : `${names[0]} +${names.length - 1}`;
    out.push({ day, label });
  }
  out.sort((a, b) => a.day - b.day);
  return out;
}

// Trait Timeline: purchases as scatter points on the day axis.
function renderTimeline(run) {
  const ds = charts.timeline.data.datasets[0];
  const pts = run.purchases.map((p, i) => {
    const cat = p.category || "";
    const color = CAT_COLORS[cat] || C.tick;
    const row = { Symptom: 2, Transmission: 1, Ability: 0 }[cat];
    // deterministic jitter so same-day purchases don't fully overlap
    const jitter = ((i * 7) % 3 - 1) * 0.13;
    const dev = p.action === "devolve";
    const r = 4 + Math.min((p.cost || 0) / 120, 11);
    return {
      x: p.day, y: (row == null ? 0.5 : row) + jitter,
      name: p.name, cost: p.cost, action: p.action, category: cat,
      radius: dev ? r * 0.85 : r,
      pointStyle: dev ? "circle" : "triangle",
      backgroundColor: dev ? "transparent" : color,
      borderColor: color,
      borderWidth: dev ? 2 : 1,
    };
  });
  ds.data = pts;
}

function chartLabels(chart, labels) {
  chart.data.labels = labels;
}

// ============================================================================
// KPI strip + run header
// ============================================================================
function updateKpis(run) {
  const rows = run.samples;
  if (!rows.length) return;
  const latest = rows[rows.length - 1];
  const wp = worldPop(rows);
  const infPct = latest.infected != null ? latest.infected / wp : null;

  setText("k-infected", fmtCompact(latest.infected));
  // Infected sub-line: % of world, plus the current spread rate (new infections/day).
  let infSub = infPct != null ? fmtPct(infPct) + " of world" : "—";
  if (rows.length >= 2) {
    const prev = rows[rows.length - 2];
    const d = (latest.infected - prev.infected) / Math.max(1, latest.day - prev.day);
    if (d != null) infSub += " · " + fmtCompact(Math.max(0, d)) + "/day";
  }
  setText("k-infected-pct", infSub);
  setText("k-dead", fmtCompact(latest.dead));
  setText("k-dead-pct", latest.dead != null && (latest.infected || 0) > 0
    ? fmtPct(latest.dead / (latest.dead + latest.infected)) + " of cases"
    : (latest.dead || 0) > 0 ? "100% of cases" : "—");
  setText("k-day", latest.day ?? "—");

  const eta = cureEtaDays(rows);
  setText("k-cure", fmtPct(latest.curePct));
  setText("k-cure-eta", eta != null
    ? (eta < 1 ? "<1 day left" : `~${Math.round(eta)} days left`)
    : "—");

  // DNA balance + income rate (sub of the DNA KPI)
  setText("k-dna", fmtCompact(latest.dna));
  let rate = null;
  if (latest.dnaEarned != null && rows.length >= 2) {
    const a = rows[Math.max(0, rows.length - 8)], b = latest;
    const dd = b.day - a.day;
    if (dd > 0 && a.dnaEarned != null) rate = (b.dnaEarned - a.dnaEarned) / dd;
  }
  setText("k-dna-rate", rate != null ? "+" + fmtCompact(rate) + "/day" : "—");

  // Evolved traits (count) + devolves
  let ev = 0, dev = 0;
  for (const p of run.purchases) (p.action === "devolve" ? dev++ : ev++);
  setText("k-evolved", fmtInt(ev));
  setText("k-evolved-sub", dev ? `${dev} devolved` : "traits bought");
}

function applyMeta(run) {
  const meta = run.meta || {};
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
// Run selector (live vs session history)
// ============================================================================
function buildRunSelector() {
  const sel = document.getElementById("run-select");
  if (!sel) return;
  const live = runs.live;
  const liveLabel = "● Live run" + (live.meta.plagueType ? " — " + live.meta.plagueType : "");
  sel.innerHTML = `<option value="live">${escapeHtml(liveLabel)}</option>` +
    runs.list.map((r, i) =>
      `<option value="${r.id}">Run ${r.n} · D${r.finalDay} · ${escapeHtml(r.meta.status || "ended")} · ${escapeHtml(r.meta.plagueType || "?")}</option>`
    ).join("");
  // keep the current selection; if the selected archive was dropped (cap), go live
  if (selected !== "live" && !runs.list.some((r) => r.id === selected)) selected = "live";
  sel.value = selected;
}

function selectRun(id) {
  selected = id;
  const banner = document.getElementById("run-banner");
  banner.hidden = selected === "live";
  if (selected === "live") setBadge("live", "● LIVE");
  else setBadge("paused", "ARCHIVE");
  applyMeta(curRun());
  renderAll(true);
  updateFooterStatus(curRun());
}

function updateFooterStatus(run) {
  if (selected === "live" && run.samples.length) {
    const latest = run.samples[run.samples.length - 1];
    setText("footer-status",
      `${run.samples.length} samples · day ${latest.day} · updated ${new Date().toLocaleTimeString()}`);
  } else {
    setText("footer-status",
      `Archived run · ended day ${run.finalDay} · ${run.samples.length} samples · ${run.purchases.length} purchases`);
  }
}

// ============================================================================
// Trait Planner — what can I buy, what does it cost, what does it do.
// ============================================================================
function renderTechPlanner(run) {
  const wrap = document.getElementById("tech-planner");
  if (!wrap) return;
  const techs = run.techs || [];
  const dna = run.samples.length ? run.samples[run.samples.length - 1].dna : null;
  if (!techs.length) {
    wrap.innerHTML = '<div class="empty" style="color:var(--text-dim);font-size:12px">trait data not available yet</div>';
    return;
  }

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
// Color is by BENEFIT to the plague (green = helps you, red = hurts), not by the
// raw value sign: for most stats up = good; for the cure stats, up = bad.
function techEffects(t) {
  const parts = [];
  // goodIfPositive: true when a positive delta helps the plague (the common case).
  // false when a positive delta hurts (cure-related stats, already pre-transformed
  // so that "worse" lands as positive).
  const add = (label, v, { goodIfPositive = true } = {}) => {
    if (!v || Math.abs(v) < 0.001) return;
    const sign = v > 0 ? "+" : "";
    const good = goodIfPositive ? v > 0 : v < 0;
    parts.push(`<span class="${good ? "pos" : "neg"}">${sign}${fmtNum(v)} ${label}</span>`);
  };
  add("inf", t.inf); add("sev", t.sev); add("let", t.let);
  add("air", t.air); add("sea", t.sea); add("land", t.land); add("corpse", t.corpse);
  add("wealthy", t.wealthy); add("poverty", t.poverty); add("urban", t.urban); add("rural", t.rural);
  add("hot", t.hot); add("cold", t.cold); add("arid", t.arid); add("humid", t.humid);
  // cureMulti >1 = cure harder (worse for plague); researchIneff >0 = slower cure (better).
  if (t.cureMulti) add("cure need", t.cureMulti - 1, { goodIfPositive: false });
  if (t.researchIneff) add("cure speed", t.researchIneff, { goodIfPositive: true });
  return parts.join(" ") || "<span class='eff'>no stat change</span>";
}

function fmtNum(v) {
  if (Math.abs(v) >= 100) return Math.round(v).toString();
  if (Math.abs(v) >= 10) return v.toFixed(1);
  return v.toFixed(2);
}

// ============================================================================
function renderCountryTable(run) {
  const snap = run.countries;
  const tbody = document.querySelector("#country-table tbody");
  if (!tbody) return;
  if (!snap || !snap.countries || !snap.countries.length) {
    tbody.innerHTML = '<tr><td colspan="4" class="empty" style="color:var(--text-dim)">no country data yet</td></tr>';
    setText("countries-sub", "Latest snapshot — day —");
    return;
  }
  const cs = snap.countries.slice().sort((a, b) => (b.inf || 0) - (a.inf || 0));
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

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

// ============================================================================
// Full re-render of everything from one run's data
// ============================================================================
function renderAll(force = false) {
  const run = curRun();
  if (!run.samples.length) return;

  renderCharts(run);
  renderRaceGauge(run);
  renderDnaFlow(run);
  updateKpis(run);
  renderTechPlanner(run);
  renderCountryTable(run);
}

// ============================================================================
// SSE stream consumer
// The server sends typed events: 'meta', 'sample', 'countries', 'purchase',
// 'techs', 'archive'. On connect it replays the full in-memory history; then
// each new sample arrives as one event. EventSource auto-reconnects.
// ============================================================================
let renderScheduled = false;

function connectSSE() {
  setBadge("", "connecting…");
  setText("footer-status", "Connecting to game…");

  const es = new EventSource(SSE_URL);

  es.addEventListener("open", () => {
    setBadge(selected === "live" ? "live" : "paused", selected === "live" ? "● LIVE" : "ARCHIVE");
  });

  es.addEventListener("meta", (e) => {
    try {
      runs.live.meta = JSON.parse(e.data);
      applyMeta(runs.live);
      buildRunSelector();
    } catch { }
  });

  es.addEventListener("sample", (e) => {
    try {
      const s = JSON.parse(e.data);
      // On a fresh replay the server sends the whole history; if a sample's day
      // is <= our last seen and <= 1, a new run started — rebuild everything.
      const rows = runs.live.samples;
      if (rows.length && s.day <= rows[rows.length - 1].day && s.day <= 1) {
        rows.length = 0;
        runs.live.purchases = [];
        runs.live.countries = null;
        runs.live.techs = [];
      }
      rows.push(s);
      scheduleRender();
    } catch { }
  });

  es.addEventListener("countries", (e) => {
    try {
      runs.live.countries = JSON.parse(e.data);
      if (selected === "live") renderCountryTable(runs.live);
    } catch { }
  });

  es.addEventListener("purchase", (e) => {
    try {
      const p = JSON.parse(e.data);
      const list = runs.live.purchases;
      // On a fresh replay the whole feed is resent; if a purchase's day is earlier
      // than our feed's earliest, treat it as a replay and reset.
      if (list.length && p.day < list[0].day) list.length = 0;
      list.push(p);
      scheduleRender();
    } catch { }
  });

  es.addEventListener("techs", (e) => {
    try {
      runs.live.techs = JSON.parse(e.data);
      if (selected === "live") renderTechPlanner(runs.live);
    } catch { }
  });

  // A completed run was archived when a new game started — session history.
  es.addEventListener("archive", (e) => {
    try {
      const a = JSON.parse(e.data);
      const n = runs.list.length + 1;
      runs.list.push({
        id: "a" + n,
        n,
        meta: a.meta || {},
        finalDay: a.finalDay,
        samples: a.samples || [],
        purchases: a.purchases || [],
        countries: a.countries || null,
        techs: a.techs || null,
      });
      buildRunSelector();
    } catch { }
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
    const run = curRun();
    if (!run.samples.length) return;
    renderAll();
    if (selected === "live") setBadge("live", "● LIVE");
    else setBadge("paused", "ARCHIVE");
    updateFooterStatus(run);
  });
}

// ============================================================================
// Resize / zoom robustness. Chart.js listens for window resize, but browser
// zoom changes and container layout shifts can slip through; we explicitly
// resize every chart (and the custom sparkline) whenever the grid changes size.
// ============================================================================
function resizeAll() {
  // spark is now a Chart.js chart (charts.spark), so the loop covers it too.
  for (const k in charts) charts[k].resize();
}

function setupResize() {
  let t = null;
  const debounced = () => {
    if (t) clearTimeout(t);
    t = setTimeout(resizeAll, 120);
  };
  window.addEventListener("resize", debounced);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) resizeAll();
  });
  const grid = document.querySelector(".grid");
  if (grid && typeof ResizeObserver !== "undefined") {
    new ResizeObserver(debounced).observe(grid);
  }
}

// ============================================================================
// Export: download the currently-viewed run as JSON (samples + purchases +
// countries + meta) for offline analysis. Everything is in memory already.
// ============================================================================
function setupExport() {
  const btn = document.getElementById("export-btn");
  if (!btn) return;
  btn.addEventListener("click", (e) => {
    e.preventDefault();
    try {
      const run = curRun();
      const meta = {
        plagueType: run.meta.plagueType || "",
        difficulty: run.meta.difficulty || "",
        origin: run.meta.startCountry || "",
        status: run.meta.status || "",
      };
      const payload = {
        exportedAt: new Date().toISOString(),
        meta,
        samples: run.samples,
        purchases: run.purchases,
        countries: run.countries,
        summary: run.samples.length ? {
          day: run.samples[run.samples.length - 1].day,
          samples: run.samples.length,
          purchases: run.purchases.length,
        } : null,
      };
      const json = JSON.stringify(payload, null, 2);
      const blob = new Blob([json], { type: "application/json" });
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      const day = run.samples.length ? run.samples[run.samples.length - 1].day : "start";
      a.download = `plague-dash-run-day${day}.json`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(a.href);
      setText("footer-status", `Exported ${run.samples.length} samples · day ${day}`);
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
  setupResize();
  setupExport();
  buildRunSelector();
  document.getElementById("run-select").addEventListener("change", (e) => selectRun(e.target.value));
  document.getElementById("run-banner-live").addEventListener("click", (e) => {
    e.preventDefault();
    selectRun("live");
  });
  connectSSE();
});
