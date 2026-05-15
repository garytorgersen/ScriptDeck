ScriptDeck
==========

A WinForms button-driven script launcher for Windows. Define a JSON
"workspace" of tabs and buttons; ScriptDeck renders them and routes
clicks to PowerShell / cmd / process executors.

Full documentation:
  * USER_GUIDE.md   - end-user guide
  * USER_MANUAL.md  - technical reference
  https://github.com/garytorgersen/ScriptDeck

INSTALLING
----------

1. Right-click Install.cmd and choose "Run as administrator" ONLY if
   you want a machine-wide install. The default (just double-click)
   installs to your user profile and needs no admin rights:

       %LocalAppData%\Programs\ScriptDeck

2. The installer first checks for the .NET Framework 4.8 runtime.
   If it's missing (rare on Windows 10 May 2019+ / Windows 11),
   you'll be prompted to open Microsoft's download page in your
   browser. Install .NET 4.8 and re-run Install.cmd.

3. After install you'll have a desktop shortcut and a Start Menu
   entry. The first launch shows the Welcome tab; open
   File -> Open Workspace and point at
   "%LocalAppData%\Programs\ScriptDeck\Workspaces\sample.json"
   to see a working example.

WHAT'S INSIDE
-------------

  Install.cmd      Installer (writes to user profile, no admin needed)
  Uninstall.cmd    Removes the install folder + shortcuts; KEEPS
                   user data (history.db, recent workspaces).
  README.txt       This file.
  App\             ScriptDeck.exe + dependencies + sample workspace.
                   Copied to %LocalAppData%\Programs\ScriptDeck.

REQUIREMENTS
------------

  * Windows 10 (May 2019 update / 1903) or later, or Windows 11
  * .NET Framework 4.8 (built-in on supported Windows versions)
  * PowerShell 5.1 (built-in on supported Windows versions)
  * 64-bit (x64) only - no 32-bit build is published

USER DATA LOCATION
------------------

ScriptDeck stores per-user state under:

  %LocalAppData%\ScriptDeck\
    history.db    - SQLite run history
    recent.json   - recent workspaces MRU list

You can copy these between machines if you want to carry state with
you. Workspaces themselves (the .json files you edit) live wherever
you save them.

UNINSTALL
---------

Run Uninstall.cmd from this package, OR delete:

  %LocalAppData%\Programs\ScriptDeck     (the app)
  %USERPROFILE%\Desktop\ScriptDeck.lnk   (desktop shortcut)
  %APPDATA%\Microsoft\Windows\Start Menu\Programs\ScriptDeck.lnk
                                          (Start Menu shortcut)

User data under %LocalAppData%\ScriptDeck is preserved by default.
Delete that folder too if you want a fully clean removal.

SUPPORT
-------

  https://github.com/garytorgersen/ScriptDeck
