"""
ScriptDeck Python demo button.

Demonstrates the three things a Python button typically does:
  1. Plain print() lands in the console RTB.
  2. write_grid() populates the structured results grid.
  3. set_shared_input() creates / updates a Volatile shared input
     visible in the Inputs grid for subsequent button clicks.

Run from the sample workspace's "Tools" tab -> "Python Info" button.
"""

import platform
import sys
from scriptdeck_bootstrap import write_grid, set_shared_input

# 1. Plain print -> console RTB.
print("Python", sys.version.split()[0])
print("Running on:", platform.platform())

# 2. Structured records -> results grid.
write_grid([
    {"property": "version",  "value": sys.version.split()[0]},
    {"property": "implementation", "value": platform.python_implementation()},
    {"property": "executable", "value": sys.executable},
    {"property": "platform",   "value": platform.platform()},
])

# 3. Session-scoped Volatile input that other buttons can read via
#    {{lastPythonRun}} token substitution.
import datetime as _dt
set_shared_input(
    "lastPythonRun",
    _dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    label="Last Python run")
