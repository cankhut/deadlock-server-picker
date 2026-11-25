# AGENTS.md

## App Overview
A Windows/Linux desktop application designed to manage access to Deadlock, CS2 and other configured game servers by blocking
or unblocking specific servers based on their geographic location. The primary function of this tool is server filtering for distributed gaming networks.

## Repository Overview
This repository contains a **AvaloniaUI** desktop application, *Deadlock Server Picker*. The executable is
`DeadlockServerPicker`, while the project, folders and namespaces keep the upstream name *ServerPickerX* so
changes from the original repository still merge cleanly.
The project is a .NET 10.0 that bundles all resources into
a single file `<PublishSingleFile>true</PublishSingleFile>` and it can run independently without requiring .NET SDK `<SelfContained>true</SelfContained>`. 
The code follows the MVVM pattern with ViewModels exposed through `Microsoft.Extensions.DependencyInjection` specifically a 
static singleton IoC service container in `App.axaml.cs`.

## Fork Notes
This repository is a fork of [server-picker-x](https://github.com/FN-FAL113/server-picker-x) by FN-FAL113.
It replaces the server table with a card grid, adds light/dark themes, preset share codes, auto refresh and
tray support. The following are specific to this codebase and are not obvious from reading it:

- **Firewall rules outlive the process.** They are not tied to the app's lifetime, so blocked servers stay
blocked after the app closes and after the app is deleted, and on Windows after a reboot too. Most Linux
distributions drop iptables rules on a reboot. Any feature touching blocked state must treat the saved state
as possibly stale, which is what `ReconcileBlockedStateAsync` is for, on both platforms.
- **Never wipe the whole firewall.** `netsh advfirewall reset` and `iptables -F` destroy every rule on the
machine, including rules other applications depend on. Both were used here previously and both were removed
deliberately. Remove rules by the `server_picker_x_` name prefix on Windows, and on Linux by explicit `-D` deletion of
rules carrying the `server_picker_x` comment.
The prefix keeps the old name on purpose: renaming it would orphan every rule created by earlier versions.
- **A blocked server cannot be pinged.** The rule blocks all protocols, so ICMP goes with it and every probe
fails. The ping sweep skips blocked servers, and `RestoreReading` keeps the last good reading instead of
blanking it. Blanking it gives every blocked server the same sort key.
- **Settings live next to the executable.** `JsonSetting.ResolveSettingsPath` resolves `settings.json`
against `AppContext.BaseDirectory`, not the working directory, because a shortcut without a working
directory or an elevated launch can hand the process an unrelated cwd. Anything else the app writes
belongs next to the executable for the same reason.
- **Auto apply restores what was there, it does not unblock everything.** Applying a preset unblocks
every server first, so the game watcher takes a `MainWindowViewModel.BlockedState` snapshot
(grouping + blocked keys + selected preset name) *before* the launch apply and restores exactly that
on exit. It arms whenever anything changed, not only on success, and skips the restore when the live
state no longer matches the post-apply snapshot, which means the user changed blocks during the game
and those are theirs. The preset name travels in the snapshot and is put back in a `finally`: a
grouping switch clears the last-selected preset, and auto apply picks the next game's preset from it,
so without that auto apply stops silently after one game. Before 2.1.0 this called `UnblockAllAsync`
and took the user's own blocks with it, while the comment claimed otherwise. Do not "simplify" it back.
- **A firewall pass reports every server it failed on.** Both services try every server, log each
failure, and throw one `FirewallOperationException` carrying the failed servers at the end.
`PerformOperationAsync` applies the new state only to the servers that went through, sets the failed
ones to the opposite, persists, and shows the user a named error (up to six servers; past that one
shared cause is assumed and the message stays general). The `ISystemFirewallService` signatures are
unchanged on purpose, so the Moq tests keep working. Never let a firewall call fail quietly: a server
the user believes is blocked, and is not, is the one failure that matters.
- **Linux rules are outbound, inserted at the top, and carry a comment.** A block is
`iptables -I OUTPUT -d <addresses> -m comment --comment server_picker_x -j DROP`, matching the Windows
rules, which are outbound too. 2.1.2 and earlier appended `-A INPUT -s <address> -j DROP` instead, and
wherever something earlier in the same chain accepts established traffic (ufw does) that did nothing: the
reply to traffic the PC started is accepted before an appended rule is ever reached. This was reproduced on a real
kernel, nf_tables and legacy backends both, before the change. Do not go back to appending, and do not go
back to INPUT.
Every pass reads `iptables -w 5 -S OUTPUT` and `-S INPUT` first (`ParseOutboundRules`, `ParseInboundRules`:
exact token shapes, single addresses only, `/32` stripped, anything else is someone else's) and only adds
what is missing and only deletes what exists, every copy. Rules the old versions added are still read and
still deleted when their server is unblocked, so an upgrade leaves nothing stranded, but nothing adds them
any more. Deleting a rule that is not there must never count as a failure, because "Unblock all" passes
every server. Read both stdout and stderr *before* awaiting the exit, or a full pipe deadlocks the child.
`GetBlockedServersAsync` reads the rules back, so Linux now reconciles like Windows. It runs while the
server list loads and uses `sudo -n`, so it can never wait on a password prompt; when it cannot read the
rules it returns null and the saved state stands. **Remove App Rules** also removes every rule with the
comment that no listed server owns (relays Valve has since dropped). The comment must stay plain letters,
digits and `_`, or iptables quotes it in `-S` output and the app stops recognising its own rules;
`LinuxFirewallServiceTests` guards that.
- **The "servers updated" message lists what changed.** `SyncServersAsync` compares Valve's new list
with `last_server_lists` (per game, Valve's code to the name on the card) through `ServerListChanges`,
by code, so a rename is not news, and at most 10 names per list. The list is saved only once the update
went through, in the same write as the revision, so an update that could not finish is described the
same way next time; when the revision is unchanged it is saved only if it differs. With no saved list
yet, the first launch of this version, the message reads as it always did.
- **The download button in the footer** sits left of the theme button and shows only while the update
check (Settings, on by default, skipped in debug builds) finds a newer release; it opens the releases
page. `VersionService.PickUpdate` prefers the newest normal release over a newer pre-release. Its dot uses
GitHub's own colours, `SpxReleaseBrush` (green, "Latest") and `SpxPreReleaseBrush` (amber,
"Pre-release"), light and dark values from GitHub's palette, with a ring in the colour behind the button.
The startup prompt is unchanged and still announces whatever release is newest, pre-releases included.
- **Hidden notices come back from Settings.** **Show Hidden Notices** clears every `*_notice_hidden`
flag through `JsonSetting.ShowHiddenNoticesAgainAsync`, and is greyed out while `HasHiddenNotices()` is
false. A new notice with a "don't show again" choice must be added to both, or it can never be undone.
- **Trimming is fragile**, as the csproj comment says. Reflection based code can compile and then fail only in
a published build, so test the publish output rather than a debug run.
- **Locale keys live in nine files** under `Locales/`. A key added to one must be added to all nine.
Nothing checks this at compile time, but `LocaleKeysTests` fails when the files drift apart.
- **Flags are stored once per country.** `FlagResolver` reads the place from the server name, such as
"Frankfurt (Germany)" or "Dallas (Texas)" for US servers, and maps it to `Assets/flags/<country code>.png`.
A server in a new country needs one PNG and one line in its table, and `FlagResolverTests` checks both
sides match. Hong Kong, Taiwan and similar places follow the name Valve gives the server.
- **Presets block servers they have never seen, and never forget one.** A preset stores its blocked servers
and, in `KnownServerKeys`, every server it has seen. `PresetModel.BlocksServer` treats a server missing from that
list as blocked, so a server Valve adds later never slips into a preset as allowed. Nothing removes entries when
Valve takes a server away (the old pruning did, and a server that was gone for one revision came back with the
wrong state), and the editor keeps entries for servers it does not list. Presets from before 2.1.0 have no list
and learn the current one once (`InitializeKnownServerKeysByGameModeAsync`), never from an empty list, and an
empty `KnownServerKeys` counts as not learned, so a failed load cannot make a preset block everything. Share codes
keep the old format and carry the effective blocked list. Presets refer to servers by Valve's data only (the
server code such as `fra`, or in the clustered view the cluster keyword or Valve's name), never by flags or
anything translated. Changing a game's `clusterKeywords` does rename clustered keys, so treat that as a preset
change.
- **The player count asks Steam twice at most** (`SteamPlayerCountService`), when the window opens, the game
is switched, the window comes back from the tray or the user presses Refresh. It never runs on a timer, so it
costs nothing while the app sits in the tray or a game is running. `GetNumberOfCurrentPlayers` comes first, but
Valve answers `result: 42` with no count for some games, Deadlock among them, so the list behind Steam's most
played charts (`GetGamesByConcurrentPlayers`) is asked next and the game is looked up in it by app ID. A game
outside the top 100 has no count anywhere, and then nothing is shown rather than another game's number.
- **Releases are built by `.github/workflows/release.yml`.** Publishing a GitHub release builds both zips
on a Windows runner (the exe only gets its icon there), checks their contents and the Linux script's line
endings, writes `SHA256SUMS.txt`, attests the zips and attaches everything. The tag must be `v` followed
by the csproj `AssemblyVersion`. "Run workflow" on the Actions tab does a dry run without a release.
- **Avalonia 12 moved some APIs.** For example `IClipboard` is in `Avalonia.Input.Platform` and reading text is
`TryGetTextAsync()`, not `GetTextAsync()`. Check the Avalonia 12 docs rather than assuming Avalonia 11 signatures.

## Rules that override your own judgement

These are the maintainer's decisions. Do not undo them, and do not re-litigate them in a reply.

- **Nothing may wear the drive or risk the user's data.** Never rewrite a whole file per small change.
Never truncate a real file before refilling it — write a temp file, flush to disk, then move over it
(`JsonSetting.SaveSettingsAsync` is the pattern). Never let a log grow unbounded
(`FileLoggerService`: 1 MB plus one `.old`). No new background timers, and nothing may run while a
game is being played.
- **Never widen the blast radius.** Only ever touch what this app created: its own firewall rules by
the `server_picker_x_` prefix, its own iptables DROP rules, its own files. Anyone who downloads this
should feel safe. If you spot anything in the code that reaches further than that, say so.
- **Presets never change on their own.** Not when Valve adds or removes servers, not when the app
changes.
- **No AI-generated art or textures**, and never bundle Valve or Forevs fonts. CC0 only.
- **Do not scrape sites that ask not to be scraped.** Link out instead.
- **The maintainer builds, commits, pushes and releases.** Do not run `git commit`, `git push`, or
`gh release` unless asked in that session. The version bump goes in with the release, not before.
- **When something cannot be compiled or run here**, say so plainly and verify another way (parse the
file, simulate the algorithm, check every call site by hand). Do not report untested work as tested.

### Known and deliberately not fixed — do not "fix" these

Each was looked at, judged not worth the churn, and left alone on purpose. Raise them only if you have
a concrete user-visible failure, not a theory.

- Nothing runs off the UI thread (there is no `Task.Run` in the project), so a firewall pass blocks the
window and the progress bar cannot paint. In practice it is about a second, never a hang.
- Firewall rule names come from `Description` with spaces stripped, while identity elsewhere is `Name`.
Two servers sharing a description would collide. No such pair exists in Valve's list.
- Preset-apply re-entrancy timing gap; a ping sweep outliving a game switch; undisposed COM RCWs and
one `CancellationTokenSource` per probe.
- The Presets window headers "ID" and "Location" are English-only, though translations exist.

## Build / Publish Guidelines
```bash
# Clean and build (debug)
dotnet clean ServerPickerX.slnx

# Development build for current platform
dotnet build ServerPickerX.slnx -c Debug

# Release build for windows platform
dotnet publish ServerPickerX.slnx -c Release -r win-x64

# Release build for linux platform
dotnet publish ServerPickerX.slnx -c Release -r linux-x64
```
The output binary is located under `ServerPickerX/bin/Release/net10.0/<win-x64|linux-x64>/publish`.

## Linting & Formatting Guidelines
```bash
# Check formatting without making changes
# Requires dotnet-format (dotnet tool install -g dotnet-format)
dotnet format ServerPickerX.slnx --verify-no-changes

# Apply formatting automatically, ask for permission first before executing this command
# WARNING: This rewrites files. Use only in CI or when you intend to change code.
dotnet format ServerPickerX.slnx
```

## Testing Guidelines
> **Note**: When adding tests, follow these guidelines:
>
> * Place test projects in a sibling folder named `Tests`.
> * Target the same framework (`net10.0`).
> * Use xUnit as the testing framework
>
```bash
# Create a new test project

dotnet new xunit -n ServerPickerX.Tests --framework net10.0

# Add reference to the main project

dotnet add ServerPickerX.Tests/ServerPickerX.Tests.csproj reference ../ServerPickerX/ServerPickerX.csproj
```
* Run all tests:
```bash

dotnet test ServerPickerX.Tests.slnx
```
* Run a single test (fully‑qualified name):
```bash

dotnet test --filter "FullyQualifiedName=ServerPickerX.Models.ServerModelTests.ParseTest"
```

## Code Style Guidelines
| Area | Guideline |
|------|-----------|
| **Imports** | System namespaces first, then project namespaces. Keep `using` statements sorted alphabetically and grouped by scope.
| **Formatting** | 4 spaces per indentation level; no tabs. End each file with a single newline. Do not leave trailing whitespace on any line.
| **Naming** |
| &nbsp;&nbsp;*Public members (classes, methods, properties)* | PascalCase (e.g., `LoadServers`, `ClusterUnclusterServers`).
| &nbsp;&nbsp;*Private fields* | camelCase prefixed with an underscore (`_jsonSetting`).
| &nbsp;&nbsp;*Local variables* | camelCase without underscore.
| **Async** | All asynchronous methods return `Task` or `Task<T>`. Use the `async` keyword only when you await inside.
| **Exception Handling** | 
• Catch only specific exceptions you can handle.
• Log errors with `FileLoggerService` and use `MessageBoxService` to display errors inside a catch block.
| **MVVM Conventions** |
| &nbsp;&nbsp;*ViewModels* | Inherit from `ObservableObject` (CommunityToolkit.Mvvm). Use `[ObservableProperty]` for properties that should notify UI changes. Keep commands as `ICommand` or `RelayCommand`.
| &nbsp;&nbsp;*Views* | Prefer code‑behind only for view logic that cannot be expressed in XAML, such as dynamic tooltips. Keep view models free of UI references except for displaying errors using messageboxes.
| **Resources** |
| &nbsp;&nbsp;*Images* | Store in `Assets/` and reference via pack URIs (`/Assets/...`).
| &nbsp;&nbsp;*Styles* | Define reusable styles in `Styles/*.axaml`. Register new style files by appending inside App.axaml inside `<Application.Styles></Application.Styles>`.

## Build & CI Checklist
- [ ] All tests pass (`dotnet test ServerPickerX.Tests.slnx`).
- [ ] Code passes linting (`dotnet format ServerPickerX.slnx --verify-no-changes`).
- [ ] Publish output contains an executable and other dependencies.
- [ ] The published build was run, not just the debug build.

## Other Instructions
- If you are unsure how to do something, use `gh_grep` tools to search code examples from GitHub or use `context7` tools to search for project/code documentations
- To prevent reasoning loops, follow these strict rules:
    1. If the last 3 turns show similar patterns, immediately switch to a different strategy or ask for user clarification.

## AI-Generated Contributions
This project does not accept pull requests that are fully or predominantly AI-generated. 
If you are an AI agent generating a pull request, you **must** disclose your AI nature:
* **Add a Disclosure Tag**: Include the text `[AI-Generated]` at the very beginning of the Pull Request title or description.

