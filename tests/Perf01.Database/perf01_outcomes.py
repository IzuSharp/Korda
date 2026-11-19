"""Compact Perf-01 DB outcome storage; historical B23 stays unchanged.

One normalized outcome is serialized per append. A final, identical JSON receipt
is published only after the sealed journal's count, bytes and live digest match.
This helper does not own HTTP execution, clocks or the durable SQL oracle.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

MAX_OUTCOME_BYTES = 4 * 1024**2


class OutcomeJournal:
    """The two B23 append sites need only this append-compatible interface."""
    def __init__(self, path):
        self.path = Path(path)
        self.stream = self.path.open("xb")
        self.count = self.bytes = 0
        self.digest = hashlib.sha256()
        self.sealed = self.attempted = self.published = False
        self.error = None

    def poison(self, error):
        # A failed append may belong to an already-discarded asyncio task. Keep
        # its failure here so final publication cannot silently omit that row.
        if self.error is None:
            self.error = type(error).__name__ + ": " + str(error)[:512]

    def healthy(self):
        if self.error is not None:
            raise ValueError("Outcome journal is poisoned: " + self.error)

    def append(self, row):
        try:
            self.healthy()
            if self.sealed:
                raise ValueError("Outcome append after journal seal")
            if not isinstance(row, dict):
                raise ValueError("Outcome must be an object")
            wire = (json.dumps(row, separators=(",", ":"), ensure_ascii=True) + "\n").encode("utf-8")
            if len(wire) > MAX_OUTCOME_BYTES:
                raise ValueError("Outcome exceeds the existing evidence-row bound")
            written = self.stream.write(wire)
            if written != len(wire):
                raise OSError("Short outcome journal write")
            self.digest.update(wire)
            self.count += 1
            self.bytes += len(wire)
        except BaseException as error:
            self.poison(error)
            raise

    def seal(self):
        # Seal before flushing, so any late task append is a sticky failure.
        self.sealed = True
        if not self.stream.closed:
            for action in (self.stream.flush, self.stream.close):
                try:
                    action()
                except BaseException as error:
                    self.poison(error)
        self.healthy()

    def rows(self):
        """Read complete rows in original completion order, with bounded memory."""
        self.healthy()
        if not self.sealed or not self.stream.closed:
            raise ValueError("Outcome journal must be sealed before replay")
        observed = hashlib.sha256()
        count = total = 0
        try:
            with self.path.open("rb") as stream:
                while wire := stream.readline(MAX_OUTCOME_BYTES + 1):
                    if len(wire) > MAX_OUTCOME_BYTES or not wire.endswith(b"\n"):
                        raise ValueError("Oversized or unterminated outcome journal row")
                    observed.update(wire)
                    total += len(wire)
                    row = json.loads(wire)
                    if not isinstance(row, dict):
                        raise ValueError("Malformed outcome journal object")
                    count += 1
                    if count > self.count:
                        raise ValueError("Extra outcome journal row")
                    yield row
            if count != self.count or total != self.bytes or observed.hexdigest() != self.digest.hexdigest():
                raise ValueError("Outcome journal count/bytes/live digest mismatch")
            self.healthy()
        except BaseException as error:
            self.poison(error)
            raise

    def verify_unchanged(self):
        self.healthy()
        with self.path.open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        if actual != self.digest.hexdigest() or self.path.stat().st_size != self.bytes:
            raise ValueError("Outcome journal changed before receipt publication")

    def receipt(self):
        return {"path": self.path.name, "status": "completed" if self.published and self.error is None else "failed",
                "rows": self.count, "bytes": self.bytes, "sha256": self.digest.hexdigest(),
                "sealed": self.sealed, "closed": self.stream.closed, "published": self.published,
                "error": self.error,
                "scope": "Warmup and measured normalized outcomes in completion order; independent recovery has its existing separate receipt"}


def save_receipt(path, value, journal):
    """Exact common.save pretty JSON, replacing one array with journal replay.

    Small metadata is emitted with iterencode. Each outcome is formatted on its
    own; no list of decoded outcomes or whole-receipt string is constructed.
    """
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    try:
        journal.healthy()
        if journal.attempted:
            raise ValueError("Outcome receipt publication is one-shot")
        journal.attempted = True
        if not isinstance(value, dict) or value.get("operation_outcomes") is not journal:
            raise ValueError("Receipt does not refer to its exact outcome journal")
        journal.seal()
        encoder = json.JSONEncoder(indent=2, sort_keys=True)
        with temporary.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write("{")
            for index, key in enumerate(sorted(value)):
                stream.write(",\n  " if index else "\n  ")
                stream.write(json.dumps(key) + ": ")
                if key == "operation_outcomes":
                    stream.write("[")
                    rows = journal.rows()
                    written = 0
                    try:
                        for row in rows:
                            stream.write(",\n    " if written else "\n    ")
                            stream.write(json.dumps(row, indent=2, sort_keys=True).replace("\n", "\n    "))
                            written += 1
                    finally:
                        rows.close()
                    stream.write("\n  ]" if written else "]")
                else:
                    for chunk in encoder.iterencode(value[key]):
                        stream.write(chunk.replace("\n", "\n  "))
            stream.write("\n}\n")
        # This second check also covers an alteration after its row was read.
        journal.verify_unchanged()
        temporary.replace(path)
        journal.published = True
    except BaseException as error:
        journal.poison(error)
        # Preserve the exact journal and any partial .tmp for failed evidence;
        # an earlier initial running receipt must never become a passed result.
        raise
