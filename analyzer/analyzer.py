#!/usr/bin/env python3
"""A replay analyzer for the replay-check plugin, built on circleguard.

Licensed under the GNU AGPL v3 (see LICENSE in this folder), as circleguard is.

POST /analyze with the plugin's JSON:
    {"score_id": 1, "user_id": 2, "beatmap_id": 3, "ruleset_id": 0,
     "replay": "<base64 .osr>", "beatmap": "<base64 .osu, or null>"}
and it answers:
    {"suspicion": 0.0-1.0, "reasons": ["..."], "fields": {"UR": "84.2", ...}}

What it looks at (osu! only; other rulesets get suspicion 0):
  - frame time: osu!(stable) records a frame about every 16 ms; a lower median (after DT/HT) means the game ran
    slower than real time, i.e. timewarp. Not for Relax/Autopilot: those replays are recorded far more often
    (0-5 ms on every Relax play we measured, honest ones included), so the check would flag them all.
  - unstable rate: needs the map; not for Relax/Autopilot (their replays have no key presses to time). Very low UR is what Relax hacks and replay editors leave; good players get low
    URs too, so it's a hint, never proof.
  - snaps: sudden sharp cursor jumps onto circles, typical of aim assist.
Every threshold is an environment variable, so you can tune them to your players. The default ones are
deliberately loose: an analyzer that cries wolf gets ignored.

Run:  pip install -r requirements.txt && ANALYZER_KEY=... python3 analyzer.py   (or the Dockerfile)
"""

import base64
import binascii
import hmac
import json
import logging
import math
import os
import sys
import warnings
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from circleguard import KeylessCircleguard, Mod, ReplayString
from slider import Beatmap

KEY = os.environ.get("ANALYZER_KEY", "")
HOST = os.environ.get("ANALYZER_HOST", "0.0.0.0")
PORT = int(os.environ.get("ANALYZER_PORT", "8600"))
MAX_BODY = int(os.environ.get("ANALYZER_MAX_BODY", str(16 * 1024 * 1024)))

# Thresholds (see the docstring).
FRAMETIME_TIMEWARP = float(os.environ.get("FRAMETIME_TIMEWARP", "14.0"))  # ms, median, DT/HT applied (stable: ~16)
UR_SUSPICIOUS = float(os.environ.get("UR_SUSPICIOUS", "50.0"))  # converted UR (DT/HT applied)
SNAPS_SUSPICIOUS = int(os.environ.get("SNAPS_SUSPICIOUS", "8"))

# numpy warns about empty slices on short or key-less replays; the results already say so.
warnings.filterwarnings("ignore", category=RuntimeWarning)
warnings.filterwarnings("ignore", category=UserWarning, module="circleguard")

log = logging.getLogger("analyzer")
cg = KeylessCircleguard()


def analyze(req: dict) -> dict:
    """Runs the checks on one play. Raises ValueError on a bad request."""
    try:
        replay_bytes = base64.b64decode(req["replay"], validate=True)
        beatmap_bytes = base64.b64decode(req["beatmap"], validate=True) if req.get("beatmap") else None
    except (KeyError, TypeError, binascii.Error) as e:
        raise ValueError(f"bad replay/beatmap: {e}") from e

    if req.get("ruleset_id", 0) != 0:
        return {"suspicion": 0.0, "reasons": [], "fields": {"Analyzer": "osu! only"}}

    replay = ReplayString(replay_bytes)
    cg.load(replay)
    if not replay.has_data():
        return {"suspicion": 0.0, "reasons": [], "fields": {"Analyzer": "the replay has no frames"}}

    suspicion = 0.0
    reasons: list[str] = []
    fields: dict[str, str] = {}
    assisted = Mod.RX in replay.mods or Mod.AP in replay.mods

    frametime = cg.frametime(replay, cv=True)
    fields["Frame time"] = f"{frametime:.2f} ms" + (" (Relax/Autopilot: not checked)" if assisted else "")
    if not assisted and frametime < FRAMETIME_TIMEWARP:
        suspicion = max(suspicion, 0.9)
        reasons.append(f"Median frame time {frametime:.2f} ms (normal is about 16.7): the game may have been slowed down (timewarp).")

    beatmap = None
    if beatmap_bytes:
        try:
            beatmap = Beatmap.parse(beatmap_bytes.decode("utf-8-sig"))
        except Exception as e:  # slider raises many kinds on a bad file
            fields["Map"] = f"couldn't be read ({type(e).__name__})"

    if beatmap is not None and not assisted:
        ur = cg.ur(replay, cv=True, beatmap=beatmap)
        fields["UR"] = f"{ur:.1f}" if not math.isnan(ur) else "no hits found"
        if not math.isnan(ur) and ur < UR_SUSPICIOUS:
            suspicion = max(suspicion, 0.6)
            reasons.append(f"Unstable rate {ur:.1f} (under {UR_SUSPICIOUS:g}): very steady hits; check the player's history.")

    if beatmap is not None:
        snaps = list(cg.snaps(replay, beatmap=beatmap))
        fields["Snaps"] = str(len(snaps))
        if len(snaps) >= SNAPS_SUSPICIOUS:
            suspicion = max(suspicion, 0.5)
            at = ", ".join(f"{s.time / 1000:.1f}s" for s in snaps[:5])
            reasons.append(f"{len(snaps)} cursor snaps onto circles (first at {at}).")
    else:
        fields["Snaps"] = "no map"
    if assisted:
        fields["UR"] = "Relax/Autopilot: not checked"
    elif beatmap is None:
        fields["UR"] = "no map"

    return {"suspicion": round(suspicion, 3), "reasons": reasons, "fields": fields}


class Handler(BaseHTTPRequestHandler):
    server_version = "replay-analyzer/0.1"

    def do_GET(self):
        if self.path == "/health":
            self.reply(200, {"status": "ok"})
        else:
            self.reply(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/analyze":
            return self.reply(404, {"error": "not found"})
        if KEY and not hmac.compare_digest(self.headers.get("Authorization", ""), f"Bearer {KEY}"):
            return self.reply(401, {"error": "bad key"})
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_BODY:
            return self.reply(413, {"error": "body missing or too big"})
        req = None
        try:
            req = json.loads(self.rfile.read(length))
            result = analyze(req)
        except (ValueError, json.JSONDecodeError) as e:
            return self.reply(400, {"error": str(e)})
        except Exception:
            log.exception("analyzing score %s failed", req.get("score_id") if isinstance(req, dict) else "?")
            return self.reply(500, {"error": "analysis failed"})
        log.info("score %s: suspicion %.2f %s", req.get("score_id"), result["suspicion"], result["fields"])
        self.reply(200, result)

    def reply(self, status: int, body: dict):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, fmt, *args):  # quieter than the default per-request line on stderr
        log.debug(fmt, *args)


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    if not KEY:
        log.warning("ANALYZER_KEY is empty: anyone who can reach this port can use it")
    if len(sys.argv) == 3:  # analyzer.py <replay.osr> <map.osu>: check one play from files, no server
        req = {"replay": base64.b64encode(open(sys.argv[1], "rb").read()).decode(),
               "beatmap": base64.b64encode(open(sys.argv[2], "rb").read()).decode(), "ruleset_id": 0}
        print(json.dumps(analyze(req), indent=2))
        return
    log.info("listening on %s:%d", HOST, PORT)
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
