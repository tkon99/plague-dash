#!/usr/bin/env python3
"""Analyze an exported Plague Dash run and summarize what happened + what the
dashboard should have told the player. Used to inform dashboard improvements."""
import json
import sys

def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)

def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "data/plague-dash-export.json"
    d = load(path)
    samples = d["samples"]
    purchases = d.get("purchases", [])
    countries = d.get("countries")
    meta = d.get("meta", {})
    techs = d.get("techs")

    print("=" * 62)
    print("RUN ANALYSIS")
    print("=" * 62)
    print(f"plague     : {meta.get('plagueType','?')}  difficulty: {meta.get('difficulty','?')}")
    print(f"status     : {meta.get('status','?')}   samples: {len(samples)} days")

    first, last = samples[0], samples[-1]
    print(f"final      : day {last['day']}  infected {fmt(last.get('infected'))}  dead {fmt(last.get('dead'))}"
          f"  cure {pct(last.get('curePct'))}")
    print(f"stats      : inf {last.get('inf')}  sev {last.get('sev')}  let {last.get('let')}")
    print(f"DNA        : balance {last.get('dna')}  earned {last.get('dnaEarned')}  spent {last.get('dnaSpent')}")

    # ---- milestones ----
    print("\n--- milestones ---")
    find_first(samples, "infected", 1_000_000, "1M infected")
    find_first(samples, "infected", 100_000_000, "100M infected")
    find_first(samples, "infected", 1_000_000_000, "1B infected")
    find_first(samples, "dead", 1, "first death")
    find_first(samples, "dead", 1_000_000, "1M dead")
    find_first(samples, "cureSpd", 1, "cure research began")
    find_cure_cross(samples, 0.5, "cure 50%")
    find_cure_cross(samples, 0.99, "cure 99%")
    # spread acceleration: biggest one-day jumps
    jumps = sorted(((samples[i]["infected"] - samples[i-1]["infected"], samples[i]["day"])
                    for i in range(1, len(samples))
                    if samples[i].get("infected") and samples[i-1].get("infected")),
                   reverse=True)[:3]
    print("biggest spread days : " + ", ".join(f"day {day} +{fmt(j)}" for j, day in jumps))

    # ---- continents ----
    print("\n--- continents (first infection day / final % of pop) ---")
    if any("continents" in s for s in samples):
        names = set()
        for s in samples:
            for c in s.get("continents", []):
                names.add(c["n"])
        for n in sorted(names):
            first_day = None
            final = None
            for s in samples:
                for c in s.get("continents", []):
                    if c["n"] == n:
                        if first_day is None and c.get("inf", 0) > 0:
                            first_day = s["day"]
                        final = c
            if final:
                pp = final.get("inf", 0) / final.get("pop", 1) * 100
                print(f"  {n:14s} first day {first_day or '-':>5}  final {pp:6.2f}%  ({fmt(final.get('inf'))} / {fmt(final.get('pop'))})")

    # ---- DNA / purchases ----
    print("\n--- DNA economy ---")
    if purchases:
        cat = {}
        for p in purchases:
            cat[p.get("category", "?")] = cat.get(p.get("category", "?"), 0) + 1
        print(f"purchases  : {len(purchases)} total")
        for c, n in sorted(cat.items(), key=lambda x: -x[1]):
            print(f"  {c}: {n}")
        # timeline of purchases
        print("first 8 purchases:")
        for p in purchases[:8]:
            print(f"  day {p.get('day'):>4}  {p.get('action','?'):7s} {p.get('name','?'):35s} {p.get('cost')} DNA")
        print("last 5 purchases:")
        for p in purchases[-5:]:
            print(f"  day {p.get('day'):>4}  {p.get('action','?'):7s} {p.get('name','?'):35s} {p.get('cost')} DNA")
    else:
        print("no purchase events captured")

    # DNA rate over time: per-day income (delta of dnaEarned)
    earn = [(s["day"], s.get("dnaEarned") or 0) for s in samples if s.get("dnaEarned") is not None]
    if len(earn) > 1:
        rates = [(earn[i][0], earn[i][1] - earn[i-1][1]) for i in range(1, len(earn)) if earn[i][1] > earn[i-1][1]]
        if rates:
            rates.sort(key=lambda x: -x[1])
            print("biggest DNA income days: " + ", ".join(f"day {d} +{r}" for d, r in rates[:5]))

    # ---- techs (Trait Planner snapshot) ----
    if techs:
        aff = sum(1 for t in techs if t.get("cost", 1e9) <= (last.get("dna") or 0))
        print(f"\n--- Trait Planner at end: {len(techs)} evolvable traits, {aff} affordable at day {last['day']} (DNA {last.get('dna')}) ---")
        for t in sorted(techs, key=lambda x: x.get("cost", 0))[:10]:
            print(f"  {t.get('name','?'):35s} {t.get('category','?'):12s} {t.get('cost')} DNA")

def fmt(n):
    if n is None: return "?"
    n = float(n)
    if n >= 1e9: return f"{n/1e9:.2f}B"
    if n >= 1e6: return f"{n/1e6:.2f}M"
    if n >= 1e3: return f"{n/1e3:.1f}K"
    return str(int(n))

def pct(x):
    if x is None: return "?"
    return f"{x*100:.1f}%"

def find_first(samples, key, threshold, label):
    for s in samples:
        v = s.get(key)
        if v is not None and v >= threshold:
            print(f"  {label:20s} day {s['day']}")
            return
    print(f"  {label:20s} never reached")

def find_cure_cross(samples, thresh, label):
    for s in samples:
        v = s.get("curePct")
        if v is not None and v >= thresh:
            print(f"  {label:20s} day {s['day']}")
            return
    print(f"  {label:20s} never reached")

if __name__ == "__main__":
    main()
