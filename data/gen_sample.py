#!/usr/bin/env python3
"""Generate a realistic Plague Inc run as JSON Lines for testing the dashboard.

Models a typical Bacteria-on-Normal run: slow asymptomatic spread, lethality
kicks in late, cure research ramps as global infectivity rises. Day count and
the relative timing of phases mirror a real ~550-day run.
"""
import json, math, random
random.seed(7)

DAYS = 560
WORLD_POP = 7_000_000_000

rows = []
infected = 1.0          # patient zero
dead = 0.0
cure_pct = 0.0
cure_need = 25_000_000.0     # base cure requirement (the game's constant)
mut_cnt = 0.0

for d in range(1, DAYS + 1):
    # --- growth phase model ---------------------------------------------------
    # infectivity climbs as the player evolves transmission; logistic spread.
    inf_base = 8 + 10 * (1 - math.exp(-d / 90))      # grows toward ~18
    if d > 160: inf_base += 4                         # evolve more transmission
    # severity/lethality stay low early (stealth), ramp after day ~300
    sev = 1 + 7 * max(0, (d - 120)) / 200
    if d > 300: sev += 6
    let = 0.5 + max(0, (d - 300)) / 40
    if d > 360: let += 4
    if d > 430: let += 6

    # --- infection spread -----------------------------------------------------
    # daily growth factor derived from infectivity; logistic-capped by world pop
    growth = 1.0 + (inf_base / 100.0) * (1 - infected / WORLD_POP) * 0.30
    infected = min(infected * growth, WORLD_POP)

    # deaths lag infections and scale with lethality
    death_rate = (let / 100.0) * 0.05
    new_dead = infected * death_rate * 0.01
    dead += new_dead
    infected -= new_dead

    # --- cure research --------------------------------------------------------
    # cure effort ramps as % infected rises (more funding when world notices)
    pct_inf = infected / WORLD_POP
    research_ineff = max(0.05, 0.8 - (let / 100.0))   # lethality slows research
    cure_spd_raw = 8e5 * pct_inf * 40                 # raw global research
    cure_spd_eff = cure_spd_raw * (1 - research_ineff)
    cure_need = 25_000_000 * (1 + (let / 100.0))      # lethality raises requirement
    cure_pct = min(1.0, cure_pct + cure_spd_eff / cure_need)

    # mutation counter accumulates, resets each trigger
    mut_cnt += 0.02 * (1 + inf_base / 20)
    if mut_cnt > 1.0:
        mut_cnt = 0.0  # triggered

    rows.append({
        "day": d,
        "infected": round(infected),
        "dead": round(dead),
        "inf": round(inf_base, 3),
        "sev": round(sev, 3),
        "let": round(let, 3),
        "cureNeed": round(cure_need, 1),
        "cureSpd": round(cure_spd_eff, 1),
        "curePct": round(cure_pct, 4),
        "airT": round(0.30 + 0.4 * (d / DAYS), 4),
        "seaT": round(0.40 + 0.3 * (d / DAYS), 4),
        "landT": round(0.50 + 0.3 * (d / DAYS), 4),
        "mutCnt": round(mut_cnt, 3),
    })

# --- continent + country model ---------------------------------------------
# 5 continents with share of world population; infection spreads to them at
# different rates (air/sea reach islands and the origin continent first).
CONTINENTS = [
    # name,            pop share, infection-delay (days), spread factor
    ("ASIA",            0.60,     20,  1.2),
    ("AFRICA",          0.17,     60,  0.9),
    ("EUROPE",          0.10,     10,  1.1),
    ("NORTH_AMERICA",   0.08,     30,  1.0),
    ("SOUTH_AMERICA",   0.05,     80,  0.8),
]
# ~30 representative countries (id, name, continent, pop share of that continent)
COUNTRIES = [
    ("CHN","China","ASIA",0.20),("IND","India","ASIA",0.18),("IDN","Indonesia","ASIA",0.04),
    ("JPN","Japan","ASIA",0.03),("SAU","Saudi Arabia","ASIA",0.01),
    ("NGA","Nigeria","AFRICA",0.13),("ETH","Ethiopia","AFRICA",0.07),("EGY","Egypt","AFRICA",0.06),
    ("ZAF","South Africa","AFRICA",0.04),
    ("RUS","Russia","EUROPE",0.10),("DEU","Germany","EUROPE",0.06),("GBR","United Kingdom","EUROPE",0.05),
    ("FRA","France","EUROPE",0.05),("ITA","Italy","EUROPE",0.05),
    ("USA","United States","NORTH_AMERICA",0.70),("MEX","Mexico","NORTH_AMERICA",0.15),("CAN","Canada","NORTH_AMERICA",0.10),
    ("BRA","Brazil","SOUTH_AMERICA",0.55),("ARG","Argentina","SOUTH_AMERICA",0.15),("COL","Colombia","SOUTH_AMERICA",0.10),
]
CONT_POP = {c[0]: WORLD_POP * c[1] for c in CONTINENTS}

# pre-compute per-country infection curves sampled from the global logistic
def country_share(day, cname, cont, cpop_share, delay, factor):
    """infected fraction of this country on a given day."""
    # each continent's curve is the global curve time-shifted + scaled
    cpop = CONT_POP[cont] * cpop_share
    cont_global_frac = 0.0
    # find this continent's reached-fraction by mirroring global logistic with delay
    if day > delay:
        eff_day = day - delay
        # use the global infected fraction at eff_day, scaled by factor
        idx = min(eff_day, DAYS - 1)
        g = rows[idx]["infected"] / WORLD_POP if rows[idx]["infected"] else 0
        cont_global_frac = min(1.0, g * factor)
    return cpop * cont_global_frac

# attach continent rollups to each day row
country_snapshots = []  # list of (day, [country dicts]) written periodically
for i, r in enumerate(rows):
    d = r["day"]
    conts = []
    for cname, share, delay, factor in CONTINENTS:
        cpop = CONT_POP[cname]
        if d > delay:
            idx = min(d - delay, DAYS - 1)
            g = rows[idx]["infected"] / WORLD_POP if rows[idx]["infected"] else 0
            frac = min(1.0, g * factor)
        else:
            frac = 0.0
        conts.append({"n": cname, "inf": round(cpop * frac), "dead": round(cpop * frac * (r["dead"]/ max(r["infected"],1))),
                      "pop": round(cpop)})
    r["continents"] = conts

    # full country snapshot every 25 days
    if d % 25 == 0 or d == DAYS:
        cs = []
        for cid, cname, cont, cshare in COUNTRIES:
            inf = round(country_share(d, cname, cont, cshare, 0, 0) )
            cs.append({"id": cid, "name": cname, "continent": cont,
                       "inf": inf, "dead": round(inf * (r["dead"]/max(r["infected"],1))),
                       "pop": round(CONT_POP[cont] * cshare)})
        country_snapshots.append((d, cs))

import os
_HERE = os.path.dirname(os.path.abspath(__file__))

with open(os.path.join(_HERE, "plague-dash.sample.jsonl"), "w") as f:
    for r in rows:
        f.write(json.dumps(r) + "\n")

# write the country snapshots file (most recent line is the live state)
with open(os.path.join(_HERE, "countries.sample.jsonl"), "w") as f:
    for day, cs in country_snapshots:
        f.write(json.dumps({"day": day, "countries": cs}) + "\n")

with open(os.path.join(_HERE, "meta.json"), "w") as f:
    json.dump({
        "plagueType": "Bacteria",
        "difficulty": "Normal",
        "startCountry": "Saudi Arabia (sample run)",
        "runStart": "2026-07-30T12:00:00",
        "status": "in_progress",
    }, f, indent=2)

print(f"Wrote {len(rows)} days -> plague-dash.sample.jsonl")
print(f"Wrote {len(country_snapshots)} country snapshots -> countries.sample.jsonl")
print(f"Final: infected={rows[-1]['infected']:,}  dead={rows[-1]['dead']:,}  cure={rows[-1]['curePct']*100:.1f}%")
print(f"Continents in last day: {[(c['n'], c['inf']) for c in rows[-1]['continents']]}")
