"""Bounded PostgreSQL protocol scheduling faults over actual server messages."""
from __future__ import annotations

import hashlib
import re
import threading

from harness import PostgreSqlProxy, _cstring, require


def category(statement: bytes) -> str:
    # Classify fixed product SQL structure without retaining SQL or parameters.
    text = statement.decode("utf-8", errors="strict").strip().rstrip(";").strip()
    if re.fullmatch(r"BEGIN(?:\s+.*)?", text, re.I): return "begin"
    if re.fullmatch(r"COMMIT(?:\s+WORK)?", text, re.I): return "commit"
    if re.fullmatch(r"ROLLBACK(?:\s+WORK)?", text, re.I): return "rollback"
    for table in ("operations", "items", "outbox", "attachments"):
        if re.match(r'INSERT\s+INTO\s+"?' + table + r'"?(?:\s|\()', text, re.I): return table + "_insert"
        if re.match(r'UPDATE\s+"?' + table + r'"?\s', text, re.I): return table + "_update"
        if re.match(r'SELECT\b', text, re.I) and re.search(r'\bFROM\s+"?' + table + r'"?(?:\s|$)', text, re.I): return table + "_select"
    return "other"


class FaultProxy(PostgreSqlProxy):
    """One-shot barrier with explicit SQL category and upstream disposition.

    Inherited transport parsing/authentication is unchanged. This controller
    changes only scheduling or disconnects; statements and bound bytes stay exact.
    """
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.target = None
        self.action = None
        self.match_number = 1
        self.matches = 0
        self.triggered = threading.Event()
        self._statements = {}
        self._portals = {}
        self._next = {}
        self._completed_commit = set()
        self._post_commit_syncs = {}
        self._business_commit = set()
        self._business = set()
        self._last_command = {}

    def arm_fault(self, target: str, action: str, *, match_number: int = 1):
        require(action in ("hold", "disconnect", "discard"), "Unsupported protocol fault action")
        require(match_number >= 1, "A fault requires a positive matching statement ordinal")
        with self._lock:
            require(self.target is None, "A protocol instance accepts one armed fault")
            self.target, self.action, self.match_number = target, action, match_number
        self._record("fault_armed", category=target, action=action, matching_ordinal=match_number)

    def _frame(self, stream, connection):
        tag, payload, frame = super()._frame(stream, connection)
        if stream is connection.client:
            kind = None
            if tag == b"P":
                name, offset = _cstring(payload); statement, _ = _cstring(payload, offset)
                self._statements[(connection.number, name)] = category(statement)
            elif tag == b"B":
                portal, offset = _cstring(payload); statement, _ = _cstring(payload, offset)
                self._portals[(connection.number, portal)] = self._statements.get((connection.number, statement), "other")
            elif tag == b"E":
                portal, _ = _cstring(payload)
                kind = self._portals.get((connection.number, portal), "other")
            elif tag == b"Q":
                statement, _ = _cstring(payload); kind = category(statement)
            elif tag == b"S" and connection.number in self._completed_commit:
                ordinal = self._post_commit_syncs.get(connection.number, 0) + 1
                self._post_commit_syncs[connection.number] = ordinal
                self._record("post_commit_sync", connection, ordinal=ordinal)
                self._select(connection, "drain_after_commit", None)
            if kind is not None:
                self._next[connection.number] = kind
        else:
            if tag == b"C" and payload == b"COMMIT\0" and connection.number in self._business_commit:
                self._completed_commit.add(connection.number)
                self._post_commit_syncs[connection.number] = 0
                self._business_commit.discard(connection.number)
            if tag == b"Z" and payload == b"I":
                self._business.discard(connection.number)
        return tag, payload, frame

    def _before_command(self, connection, kind, statement_sha256):
        actual = self._next.pop(connection.number, "other")
        if actual == "operations_insert":
            self._business.add(connection.number)
        if actual == "commit" and connection.number in self._business:
            self._business_commit.add(connection.number)
        sequence = self._record("command", connection, category=actual)
        connection.pending.append((sequence, actual))
        self._last_command[connection.number] = actual
        self._select(connection, actual, sequence)

    def _select(self, connection, category_name, sequence):
        with self._lock:
            if self.selected_connection is not None or self.target != category_name:
                return
            self.matches += 1
            if self.matches != self.match_number:
                return
            self.selected_connection, self.selected_command = connection.number, sequence
            action = self.action
            self._record("fault_barrier", connection, category=category_name, action=action,
                         command_sequence=sequence, upstream_dispatched=False)
            if action == "discard":
                require(category_name == "commit", "Acknowledgment discard is limited to COMMIT")
                connection.suppress = True
            self.triggered.set()
        if action == "disconnect":
            connection.close(); raise EOFError()
        if action == "hold":
            self.held.set()
            require(self.release.wait(self.timeout), "Protocol barrier was not resolved within its finite deadline")
            if connection.closed.is_set():
                raise EOFError()
            self._record("fault_barrier_released", connection, category=category_name)

    def disconnect_selected(self):
        with self._lock:
            selected = [value for value in self._connections if value.number == self.selected_connection]
        require(len(selected) == 1, "No exact selected backend to disconnect")
        self._record("controller_disconnect", selected[0])
        selected[0].close(); self.release.set()

    def wait(self, timeout=30):
        require(self.triggered.wait(timeout), "The selected actual PostgreSQL barrier was not reached")
        return self.selected_pid()
