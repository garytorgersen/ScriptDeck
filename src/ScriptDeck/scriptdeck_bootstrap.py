"""
ScriptDeck Python bootstrap module.

Imported by Python button scripts to access ScriptDeck-specific helpers:

    from scriptdeck_bootstrap import (
        write_rtb, write_grid,
        set_shared_input, get_shared_input, remove_shared_input,
        is_local_target, inputs,
    )

This module ships next to ScriptDeck.exe and is added to PYTHONPATH by
the executor at run time, so the import works without any setup.

Output protocol
---------------
ScriptDeck's PythonExecutor watches stdout for lines that begin with
the sentinel prefix `__SCRIPTDECK_JSON__`. Each such line is one JSON
object describing a routing event. The helpers below emit those lines;
ordinary print() output is treated as plain text destined for the
console RTB.

The protocol mirrors the PowerShell bootstrap's tagged-PSObject pattern
(see ScriptDeck.Bootstrap.ps1) -- different transport, same contract.

Environment exposure
--------------------
At run time the executor populates a few env vars the bootstrap reads:

  __SCRIPTDECK_INPUTS__       JSON-serialised dict of all shared inputs
                              (Static + Volatile) at click time.
  __SCRIPTDECK_STATIC_IDS__   JSON array of Static-only input ids, used
                              by set_shared_input to refuse Static-id
                              shadowing client-side (matches the
                              PowerShell bootstrap's behavior).

Each shared input is ALSO published as an individual env var under its
own id (computerName, etc.) for direct os.environ access.
"""

import json as _json
import os as _os
import sys as _sys

_PREFIX = "__SCRIPTDECK_JSON__"


def _emit(payload):
    """Write one tagged JSON line to stdout, flushed."""
    line = _PREFIX + _json.dumps(payload, default=str)
    # write to the underlying stdout directly so a caller that has
    # redirected sys.stdout (e.g. unit tests) still sees the line on
    # the original stream the executor is reading.
    target = getattr(_sys, "__stdout__", None) or _sys.stdout
    target.write(line + "\n")
    target.flush()


# ---- Shared inputs ----------------------------------------------------------

def _load_inputs():
    """Return the {id: value} dict published by the executor."""
    raw = _os.environ.get("__SCRIPTDECK_INPUTS__", "")
    if not raw:
        return {}
    try:
        data = _json.loads(raw)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def _load_static_ids():
    """Return the set of input ids the executor flagged as Static."""
    raw = _os.environ.get("__SCRIPTDECK_STATIC_IDS__", "")
    if not raw:
        return set()
    try:
        data = _json.loads(raw)
        return set(data) if isinstance(data, list) else set()
    except Exception:
        return set()


#: Read-only snapshot of every shared input (id -> string value) at the
#: moment of the button click. Includes both Static and Volatile inputs.
#: Same data is also exposed as individual env vars (e.g. os.environ['computerName']).
inputs = _load_inputs()


def get_shared_input(id=None):
    """
    Return the value of a single shared input by id, or the full inputs
    dict if id is None. Mirrors PowerShell's Get-SharedInput.
    """
    if id is None:
        return dict(inputs)  # defensive copy
    return inputs.get(id, "")


def set_shared_input(id, value, label=None):
    """
    Create or update a Volatile shared input. Refuses Static ids (same
    rule as PowerShell's Set-SharedInput) -- ScriptDeck owns those via
    the workspace file. Returns True on emit, False when refused.
    """
    if not id:
        raise ValueError("set_shared_input: id is required")
    if id in _load_static_ids():
        raise ValueError(
            "Cannot set shared input '" + str(id) + "': it is a Static "
            "input owned by the workspace. Edit it via the top input bar "
            "or via Tools -> Edit Shared Inputs instead.")
    _emit({
        "__ScriptDeckSetSharedInput": True,
        "id":    str(id),
        "value": "" if value is None else str(value),
        "label": None if label is None else str(label),
    })
    return True


def remove_shared_input(id):
    """
    Remove a Volatile shared input. Refuses Static ids. Returns True
    on emit; raises ValueError if the id is Static or empty.
    """
    if not id:
        raise ValueError("remove_shared_input: id is required")
    if id in _load_static_ids():
        raise ValueError(
            "Cannot remove shared input '" + str(id) + "': it is a Static "
            "input owned by the workspace.")
    _emit({
        "__ScriptDeckRemoveSharedInput": True,
        "id": str(id),
    })
    return True


# ---- Output routing ---------------------------------------------------------

def write_rtb(value):
    """
    Send a string to the console RTB only (skip the grid). Multi-line
    strings render with their embedded newlines. Mirrors PowerShell's
    Write-Rtb helper.
    """
    _emit({
        "__ScriptDeckTarget": "rtb",
        "value": "" if value is None else str(value),
    })


def write_grid(rows):
    """
    Send one or more structured records to the results grid. Accepts:

      - a single dict (one row)
      - a list of dicts (multiple rows)

    The first record's keys pin the column order; subsequent rows align
    to those columns -- missing keys become blank cells, extra keys are
    ignored. Mirrors PowerShell's Write-Grid helper.
    """
    if rows is None:
        return
    if isinstance(rows, dict):
        rows = [rows]
    if not isinstance(rows, (list, tuple)):
        raise TypeError(
            "write_grid: rows must be a dict or list of dicts, got "
            + type(rows).__name__)
    for r in rows:
        if not isinstance(r, dict):
            raise TypeError(
                "write_grid: every row must be a dict, got "
                + type(r).__name__)
        _emit({
            "__ScriptDeckTarget": "grid",
            "row": {str(k): ("" if v is None else str(v)) for k, v in r.items()},
        })


# ---- Computer-name convenience ---------------------------------------------

def is_local_target(name):
    """
    True if `name` is one of the well-known "I mean the local box"
    placeholders ('', '.', 'localhost', or the live %COMPUTERNAME%).
    Useful when a script needs to branch on local vs remote before
    dispatching a query. Mirrors PowerShell's Test-IsLocalTarget.
    """
    if not name:
        return True
    n = name.strip().lower()
    if n in (".", "localhost"):
        return True
    cn = _os.environ.get("COMPUTERNAME", "")
    if cn and n == cn.lower():
        return True
    return False
